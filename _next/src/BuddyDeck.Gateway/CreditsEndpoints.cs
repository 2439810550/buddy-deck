using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 积分端点 —— 把账号的积分包暴露给界面（账号页的积分卡）。
///
/// ## 端点
///
/// ```
/// GET /admin/credits          当前账号的积分包（用第一个可用账号查）
/// GET /admin/credits?account=<id>   指定账号
/// ```
///
/// 放 `/admin/*` 而不是 `/v1/*`：它暴露的是**账号维度**的经营数据
/// （剩多少额度），和 `/admin/accounts`、`/metrics` 同级敏感，
/// 而 `/v1/*` 是给第三方 OpenAI 客户端用的 —— 那边不该看到这些。
///
/// ⚠ **路径前缀是 `/admin/credits`，但本类不实现 `/admin/*` 的其余部分** ——
/// `AdminEndpoints` 管着 `/admin/accounts`、`/admin/logs` 等。
/// 两个族的前缀判断互不重叠（本类只认 `/admin/credits` 这一条精确路径），
/// 所以谁先被问到都不影响（见 <see cref="IEndpointFamily"/> 的顺序说明）。
///
/// ## 为什么不塞进 AdminEndpoints.cs
///
/// 那个文件在多个任务之间被反复占用（它带着**可变设置状态**，
/// 而且一直有人在改）。本类独立成文件 = **零文件冲突**，
/// 接线只需要在 <see cref="EndpointFamilies.AddAll"/> 加一行。
///
/// ## ⚠ 本任务不接线
///
/// 契约的 inScope 只有本文件与它的测试 —— `AddAll` 在
/// `IEndpointFamily.cs` 里（别人的文件）。所以**本类当前不会被网关调用**，
/// 直到有人在 `AddAll` 里加一行：
/// ```csharp
/// Register<CreditsEndpoints>(services);
/// ```
/// 接线时必须确认构造参数能从 DI 解析（见类尾的「接线说明」）。
/// </summary>
public sealed partial class CreditsEndpoints : IEndpointFamily
{
    /// <summary>精确路径 —— 只认这一条，不与 `/admin/*` 的其他端点抢。</summary>
    public const string Path = "/admin/credits";

    /// <summary>批量路径 —— 一次返回**所有账号**的积分（逐个查，单个失败不影响别的）。</summary>
    public const string AllPath = "/admin/credits/all";

    /// <summary>缓存路径 —— 从数据库秒读积分快照（不查上游，毫秒级返回）。</summary>
    public const string CachedPath = "/admin/credits/cached";

    /// <summary>积分流水路径 —— 读某个号的消耗/发放历史（credit_history 表）。</summary>
    public const string HistoryPath = "/admin/credits/history";

    /// <summary>跨池总积分 —— 所有池的 credits.total_remaining 加总（联表 pool_accounts）。</summary>
    public const string PoolTotalPath = "/admin/credits/pool-total";

    private readonly UpstreamClient _upstream;
    private readonly ICredentialSource _credentials;
    private readonly UpstreamVariant _variant;
    private readonly TimeProvider _clock;
    private readonly ILogger<CreditsEndpoints> _log;
    private readonly Database? _database;

    public CreditsEndpoints(
        UpstreamClient upstream,
        ICredentialSource credentials,
        UpstreamVariant variant,
        ILogger<CreditsEndpoints> log,
        TimeProvider? clock = null,
        Database? database = null)
    {
        _upstream = upstream;
        _credentials = credentials;
        _variant = variant;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _database = database;
    }

    public async Task<bool> TryHandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";

        // 尾部斜杠归一（`/admin/credits/` 与 `/admin/credits` 等价），
        // 与 GatewayApp.NormalizePath 同一套规则。
        if (path.Length > 1 && path.EndsWith('/')) path = path.TrimEnd('/');

        if (!HttpMethods.IsGet(context.Request.Method))
        {
            if (!string.Equals(path, Path, StringComparison.Ordinal) &&
                !string.Equals(path, AllPath, StringComparison.Ordinal) &&
                !string.Equals(path, CachedPath, StringComparison.Ordinal) &&
                !string.Equals(path, HistoryPath, StringComparison.Ordinal) &&
                !string.Equals(path, PoolTotalPath, StringComparison.Ordinal))
            {
                return false;
            }
            context.Response.Headers.Allow = "GET";
            await WriteJsonAsync(context, 405, Error("method_not_allowed", "积分查询只支持 GET")).ConfigureAwait(false);
            return true;
        }

        // ⚠ 只比**精确路径**。用 StartsWith 的话会把 `/admin/credits/xxx`
        //   也吃掉 —— 那可能是别人将来要加的路径。
        if (string.Equals(path, Path, StringComparison.Ordinal))
        {
            await HandleAsync(context).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(path, AllPath, StringComparison.Ordinal))
        {
            await HandleAllAsync(context).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(path, CachedPath, StringComparison.Ordinal))
        {
            await HandleCachedAsync(context).ConfigureAwait(false);
            return true;
        }

        // GET /admin/credits/history?account=<account_id>&limit=50 —— 积分流水
        if (string.Equals(path, HistoryPath, StringComparison.Ordinal))
        {
            await HandleHistoryAsync(context).ConfigureAwait(false);
            return true;
        }

        // GET /admin/credits/pool-total —— 跨池总积分
        if (string.Equals(path, PoolTotalPath, StringComparison.Ordinal))
        {
            await HandlePoolTotalAsync(context).ConfigureAwait(false);
            return true;
        }

        // 到这里才碰 Response —— 返回 false 的路径上一个字节都没写
        // （这是 IEndpointFamily 的硬约束）。
        return false;
    }

    /// <summary>
    /// 积分流水 —— 每一笔消耗/发放都进了 credit_history 表，
    /// 这里按账号读出来（倒序，最新的在前）。
    ///
    /// 供账号详情页的「积分明细」用：看到某个号的钱是**什么时候、
    /// 因为什么**一点点少掉的。
    /// </summary>
    private async Task HandleHistoryAsync(HttpContext context)
    {
        if (_database is null)
        {
            await WriteJsonAsync(context, 503, Error("no_database", "积分历史还没入库（需要启用 SQLite）")).ConfigureAwait(false);
            return;
        }

        var account = context.Request.Query.TryGetValue("account", out var raw)
            ? raw.ToString()
            : "";
        if (string.IsNullOrWhiteSpace(account))
        {
            await WriteJsonAsync(context, 400, Error("missing_account", "缺 account 参数")).ConfigureAwait(false);
            return;
        }

        var limit = 100;
        if (context.Request.Query.TryGetValue("limit", out var rawLimit) &&
            int.TryParse(rawLimit.ToString(), out var parsed) && parsed > 0)
        {
            limit = Math.Min(parsed, 1000);
        }

        var rows = _database.ReadCreditHistory(account, limit);
        var items = new JsonArray();
        foreach (var row in rows)
        {
            items.Add(new JsonObject
            {
                ["id"] = row.Id,
                ["accountId"] = row.AccountId,
                ["accountTag"] = row.AccountTag,
                ["label"] = row.Label,
                ["ts"] = row.Ts,
                ["totalDosage"] = row.TotalDosage,
                ["totalCount"] = row.TotalCount,
                ["delta"] = row.Delta,
            });
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = true,
            ["account"] = account,
            ["history"] = items,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 跨池总积分：所有池的 credits.total_remaining 加总。
    /// 联表 pool_accounts —— 只算「确实属于某个池」的账号。
    /// </summary>
    private async Task HandlePoolTotalAsync(HttpContext context)
    {
        if (_database is null)
        {
            await WriteJsonAsync(context, 503, Error("no_database", "跨池汇总还没入库（需要启用 SQLite）")).ConfigureAwait(false);
            return;
        }

        var total = _database.SumAllPoolCredits();
        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = true,
            ["total"] = total,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量查**所有账号**的积分。
    ///
    /// ## 为什么需要（用户实测反馈「只有第一个号显示了积分」）
    ///
    /// 单个 `/admin/credits` 只回一个账号（activeId 或第一个），
    /// 而账号页要**每个号都显示积分**。所以这里逐个查、逐个拼：
    ///   · 单个号查失败（凭证失效/上游抽风）→ 那条带 `ok:false` + 原因，**不挡别的号**
    ///   · 全部查完才回一个数组
    ///
    /// 这就是「账号卡的积分」和「池子总积分」的数据源。
    /// </summary>
    private async Task HandleAllAsync(HttpContext context)
    {
        var entries = _credentials.Load();
        if (entries.Count == 0)
        {
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = true,
                ["accounts"] = new JsonArray(),
            }).ConfigureAwait(false);
            return;
        }

        var userAgent = CatalogRefresh.UserAgentFor(_variant);
        var accounts = new JsonArray();

        foreach (var entry in entries)
        {
            accounts.Add(await FetchOneAsync(entry, userAgent, context.RequestAborted).ConfigureAwait(false));
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = true,
            ["accounts"] = accounts,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// `GET /admin/credits/cached` —— 从数据库秒读积分快照，**不查上游**。
    ///
    /// ## 为什么要有这个（用户需求的架构升级）
    ///
    /// 批量查 20 个账号的积分要逐个打上游，几十秒（界面默认 3 秒超时直接失败）。
    /// 数据库里已经存了最近一次成功查询的快照，界面优先走这个：
    ///   · 毫秒级返回
    ///   · 快照可能比实时旧一点，但账号页只是展示，不是精确对账
    ///
    /// 返回形状与 `/admin/credits/all` 一致（`{ok, accounts:[…]}`），
    /// 界面无需区分两个端点。
    /// </summary>
    private async Task HandleCachedAsync(HttpContext context)
    {
        if (_database is null)
        {
            // 没接数据库（老构造点）→ 退化成空结果，界面走空态。
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = true,
                ["accounts"] = new JsonArray(),
            }).ConfigureAwait(false);
            return;
        }

        var rows = _database.ReadAllCredits();
        var accounts = new JsonArray();

        foreach (var row in rows)
        {
            JsonArray packages;
            try
            {
                packages = JsonNode.Parse(row.PackagesJson) as JsonArray ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                packages = [];
            }

            accounts.Add(new JsonObject
            {
                ["ok"] = true,
                ["accountTag"] = row.AccountTag,
                ["accountId"] = row.AccountId,
                ["label"] = row.Label,
                ["totalCount"] = row.TotalCount,
                ["totalDosage"] = row.TotalDosage,
                // 余额用 total_remaining（remain 累加），不是 total_dosage。
                ["totalRemaining"] = row.TotalRemaining,
                ["packages"] = packages,
                ["cached"] = true,
            });
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = true,
            ["accounts"] = accounts,
            ["cached"] = true,
        }).ConfigureAwait(false);
    }

    /// <summary>查一个账号的积分，失败也返回结构化结果（不抛、不挡别人）。</summary>
    private async Task<JsonNode> FetchOneAsync(VaultEntry entry, string userAgent, CancellationToken ct)
    {
        var credential = entry.ToCredential();

        try
        {
            var snapshot = await _upstream
                .FetchCreditsAsync(credential, userAgent, _clock.GetUtcNow(), ct)
                .ConfigureAwait(false);

            // ★ 查询成功 → 写入数据库（积分快照，界面秒读）。
            _database?.UpsertCredits(
                entry.Id,
                AccountTagOf(entry),
                entry.Label,
                snapshot.TotalDosage,
                snapshot.TotalRemaining,
                snapshot.TotalCount,
                SerializePackages(snapshot),
                _clock.GetUtcNow().ToUnixTimeMilliseconds());

            return BuildOk(entry, snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UpstreamException ex)
        {
            return BuildFailure(entry, ex.Kind, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or IOException)
        {
            return BuildFailure(entry, UpstreamErrorKind.Server, ex.Message);
        }
    }

    /// <summary>单个账号查失败的响应项（批量数组里的一条）。</summary>
    private static JsonObject BuildFailure(VaultEntry entry, string kind, string detail)
    {
        _ = detail;
        return new JsonObject
        {
            ["ok"] = false,
            ["accountTag"] = AccountTagOf(entry),
            ["accountId"] = entry.Id,
            ["label"] = entry.Label,
            ["kind"] = kind,
            ["totalCount"] = 0,
            ["totalDosage"] = 0.0,
            ["packages"] = new JsonArray(),
            ["message"] = DescribeKind(kind),
        };
    }

    /// <summary>把积分包列表序列化成 JSON（数据库 credits.packages 列）。</summary>
    internal static string SerializePackages(CreditsSnapshot snapshot)
    {
        var arr = new JsonArray();
        foreach (var p in snapshot.Packages)
        {
            arr.Add(new JsonObject
            {
                ["packageName"] = p.PackageName,
                ["packageCode"] = p.PackageCode,
                ["productName"] = p.ProductName,
                ["capacityUnit"] = p.CapacityUnit,
                ["size"] = p.Size,
                ["remain"] = p.EffectiveRemain,
                ["used"] = p.Used,
                ["cycleSize"] = p.CycleSize,
                ["cycleRemain"] = p.CycleRemain,
                ["cycleUsed"] = p.CycleUsed,
                ["cycleStartTime"] = p.CycleStartTime,
                ["cycleEndTime"] = p.CycleEndTime,
                ["status"] = p.Status,
                ["grantSource"] = p.GrantSource,
            });
        }
        return arr.ToJsonString();
    }

    private async Task HandleAsync(HttpContext context)
    {
        var requestedId = context.Request.Query["account"].ToString();

        // ① 选账号
        var entries = _credentials.Load();
        if (entries.Count == 0)
        {
            await WriteJsonAsync(context, 401, Error("not_signed_in",
                "保险库里没有账号 —— 先在面板里导入一个 WorkBuddy 账号")).ConfigureAwait(false);
            return;
        }

        var entry = PickEntry(entries, requestedId);
        if (entry is null)
        {
            // 点名了一个不存在的账号 —— 明说，让界面能区分
            // 「没有账号」和「你点错了那个账号」
            await WriteJsonAsync(context, 404, Error("account_not_found",
                $"保险库里没有这个账号：{requestedId}")).ConfigureAwait(false);
            return;
        }

        // ② 查上游
        //
        // ⚠ **任何失败都不抛**，一律翻译成结构化结果。
        //   理由（验收第 4 条）：积分拉不到**不能影响其他端点**。
        //   账号页除了积分还显示标签/到期时间/状态，那些都不该因为
        //   积分接口抽风而整页崩掉。
        var credential = entry.ToCredential();
        var userAgent = CatalogRefresh.UserAgentFor(_variant);

        try
        {
            var snapshot = await _upstream
                .FetchCreditsAsync(credential, userAgent, _clock.GetUtcNow(), context.RequestAborted)
                .ConfigureAwait(false);

            await WriteJsonAsync(context, 200, BuildOk(entry, snapshot)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端走了 —— 不是错误，什么都不用回
            // （响应已经关掉了，往里写会抛）
        }
        catch (UpstreamException ex)
        {
            // 上游明确报错（t65 的 FetchCreditsAsync 在这类情况下抛这个）
            await WriteUpstreamFailureAsync(context, entry, ex.Kind, 0, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or IOException)
        {
            // 连接层失败 / 正文不是 JSON —— 同样不能让它冒出去变成 500
            await WriteUpstreamFailureAsync(context, entry, UpstreamErrorKind.Server, 0, ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 挑要查的账号。
    ///
    /// 没点名时用**保险库的 activeId**（用户当前选中的那个），
    /// 而不是"列表第一个" —— 界面上账号卡是按当前选中账号显示的，
    /// 两者不一致会让用户看到别的号的积分。
    /// </summary>
    private static VaultEntry? PickEntry(IReadOnlyList<VaultEntry> entries, string requestedId)
    {
        if (!string.IsNullOrEmpty(requestedId))
        {
            // id 里可能有冒号（`uuid:enterpriseId`）—— 用 Ordinal 精确比
            return entries.FirstOrDefault(e => string.Equals(e.Id, requestedId, StringComparison.Ordinal));
        }

        // 没有 activeId 信息时（ICredentialSource 只给列表）退回第一个。
        //
        // ⚠ 这里**没有**去读 VaultDocument.ActiveId —— 那需要访问保险库文件，
        //   而 ICredentialSource 的契约只有 Load()。要让"默认查当前选中账号"
        //   真正做到，需要把 activeId 一起暴露出来（那是 Core 侧的改动，不在本任务范围）。
        //   现在退化成"列表第一个"，并且返回值里带上 id，界面可以自己判断。
        return entries[0];
    }

    // ── 输出形状 ──────────────────────────────────────────────

    /// <summary>
    /// 成功响应。
    ///
    /// ⚠ `accountTag` 的分母必须与 `/metrics` 一致 —— 验收第 3 条要求
    /// 「同一账号在两处的标识可对上」。`/metrics` 用的是
    /// `GatewayApp.AccountTagOf`：**uid 前 8 位 + ':' + enterpriseId**。
    /// 这里照抄那个公式（不是 <c>Credential.AccountTag</c> ——
    /// 那个用**完整 uid**，两处对不上）。
    /// </summary>
    private static JsonObject BuildOk(VaultEntry entry, CreditsSnapshot snapshot)
    {
        var packages = new JsonArray();
        foreach (var package in snapshot.Packages)
        {
            packages.Add(new JsonObject
            {
                ["packageName"] = package.PackageName,
                ["packageCode"] = package.PackageCode,
                ["productName"] = package.ProductName,
                ["capacityUnit"] = package.CapacityUnit,
                ["size"] = package.Size,
                ["remain"] = package.EffectiveRemain,
                ["used"] = package.Used,
                ["cycleSize"] = package.CycleSize,
                ["cycleRemain"] = package.CycleRemain,
                ["cycleUsed"] = package.CycleUsed,
                // 到期时间：上游给的是字符串（可能带时区），原样透传 ——
                // 界面按自己的规则解析，网关不做时区猜测
                ["cycleStartTime"] = package.CycleStartTime,
                ["cycleEndTime"] = package.CycleEndTime,
                ["status"] = package.Status,
                ["grantSource"] = package.GrantSource,
            });
        }

        return new JsonObject
        {
            ["ok"] = true,
            // 账号维度：与 /metrics 同一套标签
            ["accountTag"] = AccountTagOf(entry),
            ["accountId"] = entry.Id,
            ["label"] = entry.Label,
            ["totalCount"] = snapshot.TotalCount,

            // ⚠⚠ 两个字段语义**不同**，界面余额必须用 totalRemaining：
            //   · totalDosage     上游的 TotalDosage = 「累计发过的总量」（含已用掉/已过期的）
            //   · totalRemaining  各包 remain 累加 = 「现在还能用的量」← 界面要的就是它
            // 拿 totalDosage 当余额会显示成「还剩一大堆」（已用的不算），
            // 这正是用户反馈「积分全都对不上」的根因。
            ["totalDosage"] = snapshot.TotalDosage,
            ["totalRemaining"] = snapshot.TotalRemaining,

            ["packages"] = packages,
        };
    }

    /// <summary>
    /// 与 `GatewayApp.AccountTagOf` **逐字一致**的标签公式。
    ///
    /// ⚠ 这是第二份实现 —— 理想情况应该抽成公共静态方法。
    ///   但那份在 `GatewayApp.cs`（别人的文件、本任务不在 inScope），
    ///   所以这里复制公式并**用测试钉住两处一致**（见 CreditsEndpointTests
    ///   的「账号标签与metrics一致」用例直接跑 /metrics 对照）。
    /// </summary>
    internal static string AccountTagOf(VaultEntry entry)
    {
        var uid = entry.Uid;
        var head = uid.Length <= 8 ? uid : uid[..8];
        return $"{head}:{entry.EnterpriseId ?? ""}";
    }

    /// <summary>
    /// 上游失败的响应。
    ///
    /// **仍然返回 200 + `ok:false`**，而不是 5xx —— 这是一个有意的取舍：
    ///
    /// ```
    /// 网关的积分查询失败 ≠ 界面出错
    /// ```
    /// 界面拿这个响应只是"这一块暂时显示不了"，其他字段照常渲染。
    /// 回 5xx 的话，客户端的通用错误处理会把它当成**整个请求失败**。
    ///
    /// 但**必须带上分类**（`kind`），否则界面没法区分
    /// 「账号过期了要重新登录」和「上游抽风稍后再试」——
    /// 那正是 t65 把 `ClassifyCreditFailure` 做成 public 的原因。
    /// </summary>
    private async Task WriteUpstreamFailureAsync(
        HttpContext context, VaultEntry entry, string kind, int upstreamStatus, string? detail)
    {
        if (_log.IsEnabled(LogLevel.Warning))
        {
            // ⚠ 只记元数据：账号标签 + 分类 + 上游状态。
            //   上游正文可能回显请求内容，**不进日志**。
            var accountTag = AccountTagOf(entry);
            var kindText = kind;
            LogCreditsFailed(kindText, upstreamStatus, accountTag);
        }

        _ = detail;   // 刻意不用：正文不进响应也不进日志

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = false,
            ["accountTag"] = AccountTagOf(entry),
            ["accountId"] = entry.Id,
            ["label"] = entry.Label,
            // 分类用同一张表（Core 的 UpstreamErrorKind 常量）
            ["kind"] = kind,
            ["totalCount"] = 0,
            ["totalDosage"] = 0.0,
            ["packages"] = new JsonArray(),
            // 对话式说明，界面可直接显示
            ["message"] = DescribeKind(kind),
        }).ConfigureAwait(false);
    }

    /// <summary>把分类翻成一句给人看的话（界面直接用，不必自己维护一张表）。</summary>
    private static string DescribeKind(string kind) => kind switch
    {
        UpstreamErrorKind.Unauthorized => "账号凭证失效，需要重新登录",
        "session_dead" => "会话已失效，需要重新登录",
        "hard_credit" => "额度不足",
        "soft_rate" => "请求过于频繁，稍后再试",
        "not_found" => "上游没有这个接口（可能版本不匹配）",
        UpstreamErrorKind.Server => "上游暂时不可用，稍后再试",
        _ => "积分查询失败",
    };

    // ── 输出 ──────────────────────────────────────────────────

    private static JsonObject Error(string kind, string message) => new()
    {
        ["ok"] = false,
        ["error"] = new JsonObject
        {
            ["message"] = message,
            ["type"] = kind,
            ["code"] = kind,
        },
    };

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "积分查询失败（{Kind}，上游 {Status}，号 {AccountTag}）")]
    private partial void LogCreditsFailed(string kind, int status, string accountTag);
}
