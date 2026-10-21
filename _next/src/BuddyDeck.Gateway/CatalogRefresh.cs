using System.Text.Json;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 模型目录的刷新 —— 从上游 `/v3/config` 拉真实花名册。
///
/// ## 这个类型解决的两个真缺陷（t53）
///
/// ### ① 目录刷新**必然失败**：没带 UA
///
/// 旧代码 `GatewayApp.cs:405` 调 `FetchCatalogAsync(credential, null, ct)` ——
/// **一个 UA 都不带**。而实测（t48）：不带 UA 访问 `/v3/config` 返回
/// **HTTP 400 / code 12403**。所以刷新从来没成功过。
///
/// ### ② 失败**静默**：日志压在 Debug 级
///
/// 旧代码 `GatewayApp.cs:413` 用 `LogLevel.Debug` 吞掉失败。
/// Debug 在生产日志里根本不出现 —— 于是表现为：
/// ```
/// /v1/models 返回 16 个内置模型（看着正常）
/// 界面上毫无异常
/// 用户永远看不到真机的真实数量
/// ```
/// 「降级但界面无异常」这类情形，**日志是唯一线索** —— 压在 Debug 等于没有。
///
/// ## ⚠ UA 是「形态」而不是「任意一个 UA」（这条最容易搞错）
///
/// 上游 `/v3/config` **按 UA 分流花名册**（旧版 `upstream.js:639-645` 的注释明确陈述）：
///
/// > the gateway splits `/v3/config` by client identity, and the split is
/// > **load-bearing**. A CLI-shaped UA yields the CLI's roster — the chat models
/// > this tool serves — while an App-shaped UA yields the App's internal roster.
///
/// 所以随便给个 UA（比如 `WorkBuddy/5.6.2`）**可能拿到另一份名册**，
/// 甚至可能因为不是已知形态而 400。
///
/// ## 为什么用 <see cref="UpstreamVariant.All"/> 分变体给（而不是一个常量）
///
/// 旧版的处理（`upstream.js:642-645`）：
/// ```
/// 国内版  → 保持它聊天时用的 **CLI 形态**（拿到的名册正好是它自己能用的）
/// 国际版  → 没有 CLI 身份，保持 **App 形态**
/// ```
/// 一个常量套两个变体是错的 —— 国际版会拿到国内版的名册。
/// </summary>
public sealed partial class CatalogRefresh
{
    private readonly UpstreamClient _upstream;
    private readonly ModelCatalog _catalog;
    private readonly UpstreamVariant _variant;
    private readonly ILogger<CatalogRefresh> _log;

    public CatalogRefresh(
        UpstreamClient upstream,
        ModelCatalog catalog,
        UpstreamVariant variant,
        ILogger<CatalogRefresh> log)
    {
        _upstream = upstream;
        _catalog = catalog;
        // ⚠ 变体单独注入，不读 `_upstream` 的私有字段 ——
        //   `UpstreamClient._variant` 是 private，且 `Core/` 不在 t53 的 inScope 里
        //   （不该为了拿一个字段去改别人的公开面）。DI 里本来就有 UpstreamVariant 单例。
        _variant = variant;
        _log = log;
    }

    /// <summary>
    /// 国内版对目录用的 UA —— 值取自旧版 `upstream.js:26` 的 `CLIENT_UA`。
    ///
    /// 注意它不是 `WorkBuddy/…` 形态：旧版对**目录/刷新/计费**这类
    /// 非聊天请求统一用 **CLI 形态**，而聊天用 App 形态。
    /// 两者拿到的花名册不同（见类型注释）。
    /// </summary>
    public const string CliFormUserAgent = "CLI/2.63.2 CodeBuddy/2.63.2";

    /// <summary>
    /// 国际版的 App 形态 UA。
    ///
    /// 旧版注释：国际版**没有 CLI 身份**，所以它保持 App 形态
    /// （`upstream.js:644-645`）。这里用一个最小可辨识的 App 形态值 ——
    /// ⚠ 我**没有**国际版的实测数据（开发机只装了国内版），
    /// 所以这个值标为「按旧版设计推断，**未实测**」。
    /// </summary>
    public const string AppFormUserAgent = "WorkBuddy/5.6.2";

    /// <summary>
    /// 按变体挑目录 UA。
    ///
    /// @returns 该变体对 `/v3/config` 应该带的 UA 形态。
    /// </summary>
    public static string UserAgentFor(UpstreamVariant variant)
        => variant.IsInternational ? AppFormUserAgent : CliFormUserAgent;

    /// <summary>
    /// 从一个已刷到的目录 JSON 里抽出模型列表。
    ///
    /// 上游的目录形状有两层：可能直接是 `{models:[...]}`，
    /// 也可能包在信封的 `data` 里。两种都认（详见 <see cref="FindModelsArray"/>）。
    /// </summary>
    public static List<CatalogModel> ExtractModels(JsonNode? node)
    {
        var result = new List<CatalogModel>();
        var array = FindModelsArray(node);
        if (array is null) return result;

        foreach (var item in array)
        {
            if (item is not JsonObject obj) continue;

            var id = obj["id"]?.GetValue<string>() ?? obj["model"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) continue;

            var name = obj["name"]?.GetValue<string>() ?? id;
            result.Add(new CatalogModel(id, name));
        }

        return result;
    }

    /// <summary>
    /// 在目录 JSON 里找模型数组。认四种形状（上游改过结构，两种都留）。
    /// </summary>
    private static JsonArray? FindModelsArray(JsonNode? node)
    {
        if (node is JsonArray direct) return direct;
        if (node is not JsonObject obj) return null;

        if (obj["models"] is JsonArray models) return models;
        if (obj["data"] is JsonObject data && data["models"] is JsonArray nested) return nested;
        if (obj["data"] is JsonArray dataArray) return dataArray;
        if (obj["list"] is JsonArray list) return list;

        return null;
    }

    /// <summary>
    /// 试着刷一次目录。**失败不抛** —— 这个端点的可用性比目录的实时性重要。
    ///
    /// ## 与旧版的区别：失败不再静默
    ///
    /// 旧代码在 Debug 级吞掉失败。这里提到 **Warning**，并且把
    /// 「缺 UA / 端点 / 状态码 / 回落到了内置目录」都写进日志 ——
    /// 光说「目录刷新失败」**不足以定位**（这次藏了很久没人发现，
    /// 就是因为日志什么都没说）。
    ///
    /// @param credential 用来鉴权的凭证（`/v3/config` 要 Bearer）。
    /// @returns true = 刷新成功且替换了目录；false = 回落到了内置目录。
    /// </summary>
    public async Task<bool> TryRefreshAsync(Credential credential, CancellationToken ct)
    {
        var variant = _variant;
        var userAgent = UserAgentFor(variant);
        var endpoint = $"{variant.BillingBase}{variant.CatalogPath}";

        try
        {
            var node = await _upstream.FetchCatalogAsync(credential, userAgent, ct).ConfigureAwait(false);
            var models = ExtractModels(node);

            if (models.Count == 0)
            {
                // ⚠ 「拿到了但一个都没有」也是一种失败 —— 不能当成成功。
                //   替换成空目录会让客户端看到 0 个模型，比用内置的还糟。
                if (_log.IsEnabled(LogLevel.Warning))
                {
                    LogCatalogEmpty(endpoint, userAgent);
                }
                return false;
            }

            _catalog.Replace(models);
            return true;
        }
        catch (Exception ex) when (ex is UpstreamException or JsonException or HttpRequestException
                                       or IOException or OperationCanceledException)
        {
            // ⚠ CA1873：参数要在局部变量里算好再传（日志关掉时不白算）。
            if (_log.IsEnabled(LogLevel.Warning))
            {
                // 上游失败时把**原始响应体**带出来 —— 只说「失败」定位不了。
                // （t53 验收明确要求：若上游仍返回 400，须给出原始响应体。）
                var status = ex is UpstreamException ue ? ue.Status : 0;
                var kind = ex is UpstreamException ue2 ? ue2.Kind : ex.GetType().Name;
                var detail = Summarize(ex.Message);

                LogCatalogRefreshFailed(endpoint, userAgent, status, kind, detail);
            }

            return false;
        }
    }

    /// <summary>把上游正文压成一行可读的（含原始片段）。绝不返回空串。</summary>
    internal static string Summarize(string? body, int max = 300)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(上游没有给出正文)";

        var text = body.Trim().ReplaceLineEndings(" ");
        return text.Length <= max ? text : text[..max] + "…";
    }

    // ── 日志 ──────────────────────────────────────────────────

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "模型目录刷新失败 —— **已回落到内置目录**。" +
                  "端点={Endpoint} UA=[{UserAgent}] 上游状态={Status} 分类={Kind} 正文={Detail}")]
    private partial void LogCatalogRefreshFailed(string endpoint, string userAgent, int status, string kind, string detail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "模型目录刷新拿到**空列表** —— 已回落到内置目录（端点={Endpoint} UA=[{UserAgent}]）")]
    private partial void LogCatalogEmpty(string endpoint, string userAgent);
}
