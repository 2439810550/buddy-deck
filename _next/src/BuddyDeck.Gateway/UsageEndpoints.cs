using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 用量账本的只读查询端点。
///
/// ## 为什么是「就地接管」而不是在 GatewayApp 里加路由
///
/// 和 <see cref="AdminEndpoints"/> 同一套做法：暴露一个
/// <see cref="TryHandleAsync"/>，由网关在**鉴权之后**调一次；不是
/// <c>/usage/*</c> 的请求原样返回 false，网关继续走它自己的分派。
///
/// 这样做的理由不是「省事」，是**边界**：账本这条线（成本聚合）
/// 和网关主流程（转发）没有耦合，注册进来一行就够，
/// 不需要动 <c>GatewayApp.HandleAsync</c> 那个已经很长的分派链
/// —— 那个文件同时被好几条并行线在改。
///
/// ## 和 RequestLog 的分工（再说一次，因为这里最容易混）
///
/// ```
/// GET /admin/logs   → RequestLog   「刚才那几次请求怎么样了」（环形缓冲，内存）
/// GET /usage/...    → UsageLedger  「我总共烧了多少额度」  （JSONL 落盘，算总量）
/// ```
///
/// **两者不能互相替代。** 具体地：
///   · `/admin/logs` 的数据**会随进程重启清零**，而且存满就挤掉最老的
///     —— 用它算「本月总花费」得到的是「最近 500 次的花费」
///   · `/usage/summary` 的数据来自账本文件，**跨重启累积**，
///     而且带 `credit`（上游实际扣费）—— 那是 RequestLog 根本没有的字段
///
/// 所以这里**不去复用** RequestLog 的任何东西，也不把账本塞进它的缓冲。
/// 详见 src/BuddyDeck.Gateway/RequestLog.cs 顶部的对照说明。
///
/// ## 全只读
///
/// 这个类**不写账本**（写入由请求路径上的记账钩子做）。
/// 暴露一个能清空/改账本的 HTTP 端点等于给了一个删账的口子，
/// 而账本是用户的历史数据 —— 没有明确的用户需求就不加。
/// </summary>
public sealed partial class UsageEndpoints
{
    private readonly UsageLedger _ledger;
    private readonly Database? _database;
    private readonly ILogger<UsageEndpoints> _log;

    public UsageEndpoints(UsageLedger ledger, ILogger<UsageEndpoints> log, Database? database = null)
    {
        _ledger = ledger;
        _database = database;
        _log = log;
    }

    /// <summary>
    /// 这个请求是不是 `/usage/*`。是的话就地处理并返回 true。
    ///
    /// 由网关在**鉴权之后**调用 —— 账本里有账号标签和用量，
    /// 和 <c>/admin/*</c> 同级敏感，绝不能漏在鉴权之前。
    /// </summary>
    public async Task<bool> TryHandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (!path.StartsWith("/usage/", StringComparison.Ordinal) &&
            !string.Equals(path, "/usage", StringComparison.Ordinal))
        {
            return false;
        }

        // 尾部斜杠归一，和 GatewayApp.NormalizePath 同一套规则
        if (path.Length > 1 && path.EndsWith('/')) path = path.TrimEnd('/');

        // 账本是只读资源 —— 除了 GET 一律 405（不是 404：
        // 路由存在但方法不对，说清楚比让人以为路径写错了好）
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.Headers.Allow = "GET";
            await WriteErrorAsync(context, 405, "method_not_allowed", "用量账本只支持 GET").ConfigureAwait(false);
            return true;
        }

        switch (path)
        {
            case "/usage/summary":
                await SummaryAsync(context).ConfigureAwait(false);
                return true;

            case "/usage/by-model":
                await ByModelAsync(context).ConfigureAwait(false);
                return true;

            case "/usage/by-account":
                await ByAccountAsync(context).ConfigureAwait(false);
                return true;

            case "/usage/recent":
                await RecentAsync(context).ConfigureAwait(false);
                return true;

            case "/usage":
                await AllAsync(context).ConfigureAwait(false);
                return true;

            default:
                await WriteErrorAsync(context, 404, "not_found", $"没有这条用量路由：{path}").ConfigureAwait(false);
                return true;
        }
    }

    // ── 三个聚合视图 ──────────────────────────────────────────

    /// <summary>
    /// `GET /usage/summary` —— 总计。
    ///
    /// 最常被调的那个（界面上「本月花了 X 积分」）。
    /// 它的响应**刻意很小**：只有 totals + 时间范围 + 坏行计数，
    /// 不含 `recent` 明细（那个单独走 `/usage/recent`）。
    /// 界面可以每秒刷它而不心疼。
    /// </summary>
    private async Task SummaryAsync(HttpContext context)
    {
        var (aggregate, read) = UsageAggregation.ReadAndAggregate(_ledger);

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["totals"] = Row(aggregate.Totals),
            ["entryCount"] = aggregate.EntryCount,
            ["firstTs"] = aggregate.FirstTs,
            ["lastTs"] = aggregate.LastTs,
            // 坏行信息**一定要回** —— 「总共花了 X」在账本有一半行读不出来时
            // 是个骗人的数字，用户得知道
            ["skippedLines"] = read.SkippedLines,
            ["skippedLineNumbers"] = Numbers(read.SkippedLineNumbers),
        }).ConfigureAwait(false);
    }

    /// <summary>`GET /usage/by-model` —— 按模型聚合（credit 从多到少）。</summary>
    private Task ByModelAsync(HttpContext context)
        => RowsAsync(context, "byModel", static a => a.ByModel);

    /// <summary>`GET /usage/by-account` —— 按账号聚合。</summary>
    private Task ByAccountAsync(HttpContext context)
        => RowsAsync(context, "byAccount", static a => a.ByAccount);

    private async Task RowsAsync(
        HttpContext context,
        string key,
        Func<UsageAggregate, IReadOnlyList<UsageAggregateRow>> select)
    {
        var (aggregate, read) = UsageAggregation.ReadAndAggregate(_ledger);

        var rows = new JsonArray();
        foreach (var row in select(aggregate)) rows.Add(Row(row));

        await WriteJsonAsync(context, 200, new JsonObject
        {
            [key] = rows,
            ["totals"] = Row(aggregate.Totals),
            ["entryCount"] = aggregate.EntryCount,
            ["skippedLines"] = read.SkippedLines,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// <summary>
    /// `GET /usage/recent?limit=N` —— 账本里最近的 N 条**明细**。
    ///
    /// ★ 优先从数据库读（毫秒级）；数据库为空/未接时回退 JSONL 全量解析。
    ///
    /// ⚠ 注意这不是「最近请求列表」（那是 `/admin/logs` 给的
    /// RequestLog 缓冲）。这里是**账本的原始条目** —— 带 credit、
    /// 带缓存字段，用来核对某一笔账到底扣了多少。
    ///
    /// @param limit 默认 50，上限 1000（防止一次把整个账本拉进内存）。
    /// </summary>
    private async Task RecentAsync(HttpContext context)
    {
        var limit = ReadLimit(context);

        if (_database is not null)
        {
            var fromDb = _database.ReadRecentUsage(limit);
            if (fromDb.Count > 0)
            {
                var dbItems = new JsonArray();
                foreach (var entry in fromDb) dbItems.Add(Entry(entry));

                await WriteJsonAsync(context, 200, new JsonObject
                {
                    ["items"] = dbItems,
                    ["total"] = fromDb.Count,
                    ["returned"] = fromDb.Count,
                    ["skippedLines"] = 0,
                    ["from"] = "database",
                }).ConfigureAwait(false);
                return;
            }
        }

        var read = _ledger.Read();
        var entries = read.Entries;

        // 从尾部取 —— 账本是追加写的，最后的就是最新的
        var start = Math.Max(0, entries.Count - limit);

        var items = new JsonArray();
        for (var i = start; i < entries.Count; i++) items.Add(Entry(entries[i]));

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["items"] = items,
            ["total"] = entries.Count,
            ["returned"] = entries.Count - start,
            ["skippedLines"] = read.SkippedLines,
            ["from"] = "jsonl",
        }).ConfigureAwait(false);
    }

    /// <summary>`GET /usage` —— 一次拿全（聚合 + 明细）。</summary>
    private async Task AllAsync(HttpContext context)
    {
        var limit = ReadLimit(context);

        var (aggregate, read) = UsageAggregation.ReadAndAggregate(_ledger);
        var entries = read.Entries;
        var start = Math.Max(0, entries.Count - limit);

        var items = new JsonArray();
        for (var i = start; i < entries.Count; i++) items.Add(Entry(entries[i]));

        var byModel = new JsonArray();
        foreach (var row in aggregate.ByModel) byModel.Add(Row(row));

        var byAccount = new JsonArray();
        foreach (var row in aggregate.ByAccount) byAccount.Add(Row(row));

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["totals"] = Row(aggregate.Totals),
            ["byModel"] = byModel,
            ["byAccount"] = byAccount,
            ["recent"] = items,
            ["entryCount"] = aggregate.EntryCount,
            ["firstTs"] = aggregate.FirstTs,
            ["lastTs"] = aggregate.LastTs,
            ["skippedLines"] = read.SkippedLines,
            ["path"] = _ledger.Path,
        }).ConfigureAwait(false);
    }

    // ── 形状 ──────────────────────────────────────────────────

    /// <summary>
    /// 一行聚合结果。
    ///
    /// 键名和 Core 的 <see cref="UsageAggregateRow"/> 属性名一致
    /// （camelCase），这样界面直接用同一个 DTO 反序列化就行,
    /// 不用维护两套映射 —— 映射就是漂移的开始。
    /// </summary>
    private static JsonObject Row(UsageAggregateRow row) => new()
    {
        ["key"] = row.Key,
        ["calls"] = row.Calls,
        ["failures"] = row.Failures,
        ["credit"] = row.Credit,
        ["promptTokens"] = row.PromptTokens,
        ["completionTokens"] = row.CompletionTokens,
        ["cacheHit"] = row.CacheHit,
        ["cacheMiss"] = row.CacheMiss,

        // 派生指标：null 表示「没有可用数据」，不是 0。
        // 序列化成 JSON null 而不是省略 —— 界面要靠它区分
        // 「一积分都没花」和「根本没有 token 数据」。
        ["tokens"] = row.Tokens,
        ["creditPerKToken"] = row.CreditPerKToken,
        ["tokensPerCredit"] = row.TokensPerCredit,
        ["cacheHitRate"] = row.CacheHitRate,
    };

    /// <summary>一条账目明细。</summary>
    private static JsonObject Entry(UsageLedgerRecord entry) => new()
    {
        ["iso"] = entry.Iso,
        ["ts"] = entry.Ts,
        ["model"] = entry.Model,
        ["usedModel"] = entry.UsedModel,

        // ⚠ 账本里的 account 存的是**脱敏短标签**（uid 前 8 位或池里的
        // Label）—— 这是记账时的约定，不是这个类的保证。
        // 万一哪天有调用方把凭据当账号传进来，账本里就会有 token，
        // 而这里会把它**原样回给任何能访问 /usage 的人**。
        // 所以在**出口**再兜一道：看起来像凭据的整段替换掉。
        // 写侧约定 + 读侧兜底，两道 —— 账本是长期文件，
        // 一次写错会一直留在里面。
        ["account"] = SafeLabel(entry.Account),

        ["kind"] = entry.Kind,
        ["outcome"] = entry.Outcome,
        ["elapsedMs"] = entry.ElapsedMs,
        ["attempts"] = entry.Attempts,
        ["promptTokens"] = entry.PromptTokens,
        ["completionTokens"] = entry.CompletionTokens,
        ["totalTokens"] = entry.TotalTokens,
        ["credit"] = entry.Credit,
        ["cacheHit"] = entry.CacheHit,
        ["cacheMiss"] = entry.CacheMiss,
        ["cacheRead"] = entry.CacheRead,
        ["cacheWrite"] = entry.CacheWrite,
        ["reasoning"] = entry.Reasoning,
    };

    /// <summary>
    /// 出口兜底：看起来像凭据的标签不出网。
    ///
    /// 判据和 <c>RequestLog.Sanitize</c> 同源 —— 长且无空格的连续串、
    /// 或三段式 JWT。命中就整段替换，不做局部遮蔽：
    /// 局部遮蔽很容易漏掉一段，整体丢掉最安全。
    ///
    /// ⚠ 这**不是**主防线。主防线是记账侧只用短标签
    /// （见 <c>GatewayApp.AccountTagOf</c> 只取 uid 前 8 位）。
    /// 这一道是为了「万一写侧将来放松了」时账本不会跟着泄露。
    /// </summary>
    private static string? SafeLabel(string? label)
    {
        if (string.IsNullOrEmpty(label)) return label;

        // JWT 形状：三段用点分隔
        if (label.Count(c => c == '.') == 2 && label.Split('.').All(seg => seg.Length >= 8))
        {
            return "[已脱敏]";
        }

        // 长且无空格的连续串 = token/密钥的典型形状。
        // 32 这个阈值：真实账号标签（uid 前 8 位 + 企业 id）远短于它，
        // 而 sk-xxx / Bearer xxx 这类都远超它。
        var longest = 0;
        var current = 0;
        foreach (var ch in label)
        {
            if (char.IsWhiteSpace(ch)) { current = 0; continue; }
            current++;
            if (current > longest) longest = current;
        }

        return longest >= 32 ? "[已脱敏]" : label;
    }

    private static JsonArray Numbers(IReadOnlyList<int> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    /// <summary>读 `?limit=`，夹到 [1, 1000]，非法值用默认 50。</summary>
    private static int ReadLimit(HttpContext context)
    {
        const int fallback = 50;
        const int max = 1000;

        if (!context.Request.Query.TryGetValue("limit", out var raw)) return fallback;

        // 非法值**不报错**，用默认值 —— 一个写错的 query 参数
        // 不该让用户看不到账本
        if (!int.TryParse(raw.ToString(), out var value) || value < 1) return fallback;

        return Math.Min(value, max);
    }

    // ── 输出 ──────────────────────────────────────────────────

    /// <summary>不转义中文的序列化选项（和 GatewayApp 用的是同一套策略）。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task WriteJsonAsync(HttpContext context, int status, JsonNode body)
    {
        var payload = body.ToJsonString(RelaxedJson);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(payload);
        await context.Response.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string kind, string message)
        => WriteJsonAsync(context, status, new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = string.IsNullOrWhiteSpace(message) ? kind : message,
                ["type"] = kind,
                ["code"] = kind,
            },
        });
}
