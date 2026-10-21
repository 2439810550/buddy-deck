using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Core;

/// <summary>联网相关的错误。<see cref="Code"/> 决定 HTTP 状态码（和 Miora 同一套约定）。</summary>
public sealed class SearchException : Exception
{
    public SearchException(string code, string message) : base(message) => Code = code;

    public string Code { get; }

    public int Status => Code switch
    {
        SearchErrorCodes.InvalidRequest => 400,
        SearchErrorCodes.AuthMissingToken or SearchErrorCodes.Unauthorized => 401,
        SearchErrorCodes.Timeout => 504,
        _ => 502,
    };

    public string OpenAiType => Code switch
    {
        SearchErrorCodes.InvalidRequest => "invalid_request_error",
        SearchErrorCodes.AuthMissingToken or SearchErrorCodes.Unauthorized => "authentication_error",
        SearchErrorCodes.Timeout => "timeout_error",
        _ => "upstream_error",
    };
}

/// <summary>错误码常量。**值和旧版 `search.js` 逐字一致**。</summary>
public static class SearchErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string AuthMissingToken = "AUTH_MISSING_TOKEN";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Timeout = "TIMEOUT";
    public const string Network = "NETWORK";
    public const string UpstreamError = "UPSTREAM_ERROR";
}

/// <summary>一条搜索结果。</summary>
public sealed record SearchResultItem(string Title, string Url, string Snippet, string Site);

/// <summary>一次搜索的结果。</summary>
public sealed record SearchResult(
    string Query,
    string Provider,
    long ResponseTimeMs,
    IReadOnlyList<SearchResultItem> Results);

/// <summary>一次网页抓取的结果。</summary>
public sealed record WebFetchResult(
    string Url,
    string Title,
    string Content,
    string ContentType,
    long ContentLength,
    bool Truncated,
    int StatusCode);

/// <summary>
/// WorkBuddy 的联网能力：搜索（web_search）与网页抓取（web_fetch）。
///
/// ## 认证（实测）
///
/// **只需要两个头**：`Content-Type` + `Authorization: Bearer &lt;accessToken&gt;`。
/// 官方 CLI 还会发 `X-Agent-Intent` / `X-Conversation-ID` / `X-IDE-Type` /
/// `X-User-Id` / 自定义 UA —— 逐项实测**全部可省**，去掉仍 200。
/// 但 **`Bearer ` 前缀不可省**，去掉立即 401。
///
/// ## 两个上游坑（都已在代码里绕开）
///
/// 1. **`allowed_domains` 上游不生效** —— 实测限定 `docs.python.org` 仍会返回
///    百度、CSDN。所以「只看某域名」是**本模块拿到结果后自己过滤**的。
///    有过滤时会把 `max_results` 拉满再筛，否则筛完不够数。
/// 2. **排除域名靠 `-site:` 折进 query 字符串** —— 上游没有排除字段，
///    这是官方 CLI 的做法。
/// </summary>
public sealed partial class SearchClient
{
    private const string SearchPath = "/agenttool/v1/search";
    private const string FetchPath = "/agenttool/v1/webfetch";

    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>上游一次最多给多少条。</summary>
    public const int MaxResultsLimit = 20;

    /// <summary>单次抓取最多收多少字符。</summary>
    public const int MaxFetchLength = 200_000;

    private readonly HttpClient _http;
    private readonly Func<Credential> _credential;
    private readonly string _base;
    private readonly ILogger _log;

    /// <param name="http">打上游的 HttpClient。</param>
    /// <param name="credential">**惰性**取当前账号凭据 —— 续期/换号自动跟上。</param>
    /// <param name="variant">变体（决定 base URL）。</param>
    /// <param name="log">日志。</param>
    public SearchClient(HttpClient http, Func<Credential> credential, UpstreamVariant variant, ILogger log)
    {
        _http = http;
        _credential = credential;
        _base = variant.ChatBase.TrimEnd('/');
        _log = log;
    }

    /// <summary>
    /// 起一次 POST。凭据在**发送前**才取，所以续期后的 token 立刻生效。
    /// </summary>
    /// <param name="path">相对路径。</param>
    /// <param name="body">请求体。</param>
    /// <param name="timeout">超时。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<JsonObject> PostAsync(string path, JsonObject body, TimeSpan timeout, CancellationToken ct)
    {
        var accessToken = _credential().AccessToken;
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new SearchException(
                SearchErrorCodes.AuthMissingToken,
                "当前账号没有 accessToken，请先在 WorkBuddy 桌面 App 登录");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}{path}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 客户端取消 —— 原样抛，不是超时
        }
        catch (OperationCanceledException)
        {
            throw new SearchException(SearchErrorCodes.Timeout, $"联网请求超时（{(int)timeout.TotalMilliseconds}ms）");
        }
        catch (HttpRequestException ex)
        {
            throw new SearchException(SearchErrorCodes.Network, $"联网请求失败：{ex.Message}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return Unwrap((int)response.StatusCode, text);
        }
    }

    /// <summary>
    /// 校验并解包上游响应。
    ///
    /// ⚠ 搜索/抓取是**扁平**返回的（业务字段和 `code` 同级），
    /// **不是**套在 `data` 里 —— 和计费那些端点不一样，别照抄 readEnvelope。
    /// </summary>
    private static JsonObject Unwrap(int status, string text)
    {
        if (status is 401 or 403)
        {
            throw new SearchException(
                SearchErrorCodes.Unauthorized,
                $"WorkBuddy 拒绝了这次联网请求（HTTP {status}）：凭据无效或已过期，请在桌面 App 重新登录");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new SearchException(
                SearchErrorCodes.UpstreamError,
                $"联网接口返回非 JSON（HTTP {status}）：{Truncate(text, 200)}");
        }

        if (parsed is not JsonObject document)
        {
            throw new SearchException(SearchErrorCodes.UpstreamError, $"联网接口返回了意外的结构（HTTP {status}）");
        }

        if (document["code"] is JsonValue codeValue && codeValue.TryGetValue<long>(out var code) && code != 0)
        {
            var msg = document["msg"]?.GetValue<string>() ?? "未知";
            throw new SearchException(SearchErrorCodes.UpstreamError, $"联网接口报错：{msg}（code {code}）");
        }

        if (status >= 400)
        {
            throw new SearchException(
                SearchErrorCodes.UpstreamError,
                $"联网接口返回 HTTP {status}：{Truncate(text, 200)}");
        }

        return document;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "[search] \"{Query}\" → {Count} 条，{Elapsed}ms")]
    private partial void LogSearch(string query, int count, long elapsed);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[webfetch] {Url} → {Length} 字符，{Elapsed}ms")]
    private partial void LogFetch(string url, long length, long elapsed);

    /// <summary>
    /// 联网搜索。
    /// </summary>
    /// <param name="query">查询词，至少 2 个字符。</param>
    /// <param name="maxResults">1–20，默认 5。</param>
    /// <param name="freshness">`d1`–`d30` / `m1`–`m12` / `y1`–`y5`。</param>
    /// <param name="includeDomains">只要这些域名（**本地过滤**）。</param>
    /// <param name="excludeDomains">排除这些域名（折成 query 的 `-site:`）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<SearchResult> SearchAsync(
        string? query,
        int? maxResults = null,
        string? freshness = null,
        IReadOnlyList<string>? includeDomains = null,
        IReadOnlyList<string>? excludeDomains = null,
        CancellationToken ct = default)
    {
        var trimmed = (query ?? "").Trim();
        if (trimmed.Length < 2)
        {
            throw new SearchException(SearchErrorCodes.InvalidRequest, "query 不能为空且至少 2 个字符");
        }

        if (!string.IsNullOrEmpty(freshness) && !ValidFreshness(freshness))
        {
            throw new SearchException(
                SearchErrorCodes.InvalidRequest,
                $"freshness 格式非法（{freshness}）：应为 d1–d30 / m1–m12 / y1–y5，数字可省略（如 d 等于 d1）");
        }

        var wanted = maxResults ?? 5;
        if (wanted < 1 || wanted > MaxResultsLimit)
        {
            throw new SearchException(
                SearchErrorCodes.InvalidRequest,
                $"maxResults 必须是 1–{MaxResultsLimit} 的整数");
        }

        var include = NormalizeDomains(includeDomains);
        // 有本地过滤就多取一些，避免筛完不够数（上游最多给 MaxResultsLimit）。
        var fetchCount = include.Count > 0 ? MaxResultsLimit : wanted;

        var body = new JsonObject
        {
            ["query"] = BuildQuery(trimmed, excludeDomains),
            ["type"] = "text2text",
            ["max_results"] = fetchCount,
        };
        if (!string.IsNullOrEmpty(freshness)) body["freshness"] = freshness;

        var started = Environment.TickCount64;
        var document = await PostAsync(SearchPath, body, SearchTimeout, ct).ConfigureAwait(false);

        var results = document["results"] as JsonArray ?? [];
        var mapped = new List<SearchResultItem>();

        foreach (var item in results)
        {
            if (item is not JsonObject entry) continue;

            var url = entry["url"]?.GetValue<string>() ?? "";

            if (include.Count > 0)
            {
                var host = HostOf(url);
                if (host.Length == 0 || !include.Any(domain => DomainMatches(host, domain))) continue;
            }

            mapped.Add(new SearchResultItem(
                entry["title"]?.GetValue<string>() ?? "",
                url,
                entry["snippet"]?.GetValue<string>() ?? "",
                entry["site"]?.GetValue<string>() ?? ""));

            if (mapped.Count >= wanted) break;
        }

        var result = new SearchResult(
            trimmed,
            document["provider"]?.GetValue<string>() ?? "",
            ReadLong(document["response_time_ms"]) ?? 0,
            mapped);

        if (_log.IsEnabled(LogLevel.Debug))
        {
            LogSearch(trimmed, mapped.Count, Environment.TickCount64 - started);
        }

        return result;
    }

    /// <summary>抓取一个网页，拿回正文（默认 markdown）。</summary>
    /// <param name="url">必填，http/https。</param>
    /// <param name="prompt">让上游按这个意图提取（不清洗就没有重点）。</param>
    /// <param name="format">正文格式，默认 markdown。</param>
    /// <param name="timeoutSec">上游抓取自己的超时（秒）。</param>
    /// <param name="maxLength">正文上限。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<WebFetchResult> FetchPageAsync(
        string? url,
        string? prompt = null,
        string? format = null,
        int? timeoutSec = null,
        int? maxLength = null,
        CancellationToken ct = default)
    {
        var trimmed = (url ?? "").Trim();
        if (trimmed.Length == 0)
        {
            throw new SearchException(SearchErrorCodes.InvalidRequest, "url 不能为空");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            throw new SearchException(SearchErrorCodes.InvalidRequest, $"url 不是合法地址：{trimmed}");
        }

        // ⚠ 只放 http/https —— 别的一律拒。**不能让客户端拿这个端点
        //   去打 file:// 之类的本地资源**（旧版 verify.mjs 专门测了这条）。
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new SearchException(
                SearchErrorCodes.InvalidRequest,
                $"url 只支持 http/https，收到 {parsed.Scheme}");
        }

        var limit = maxLength is > 0 ? Math.Min(maxLength.Value, MaxFetchLength) : MaxFetchLength;

        var body = new JsonObject
        {
            ["url"] = trimmed,
            ["format"] = string.IsNullOrEmpty(format) ? "markdown" : format,
            ["prompt"] = string.IsNullOrWhiteSpace(prompt) ? "Summarize this page." : prompt.Trim(),
            ["timeout"] = timeoutSec is > 0 ? timeoutSec.Value : 20,
            ["max_length"] = limit,
        };

        var started = Environment.TickCount64;
        var document = await PostAsync(FetchPath, body, FetchTimeout, ct).ConfigureAwait(false);

        var result = new WebFetchResult(
            document["url"]?.GetValue<string>() ?? trimmed,
            document["title"]?.GetValue<string>() ?? "",
            document["content"]?.GetValue<string>() ?? "",
            document["content_type"]?.GetValue<string>() ?? "",
            ReadLong(document["content_length"]) ?? 0,
            document["truncated"]?.GetValue<bool>() ?? false,
            (int)(ReadLong(document["status_code"]) ?? 0));

        if (_log.IsEnabled(LogLevel.Debug))
        {
            LogFetch(result.Url, result.ContentLength, Environment.TickCount64 - started);
        }

        return result;
    }

    // ── 纯函数（可独立测试） ──────────────────────────────────

    /// <summary>只接受字符串数组，逐项 trim + 小写，丢掉空值。</summary>
    public static List<string> NormalizeDomains(IReadOnlyList<string>? value)
    {
        var list = new List<string>();
        if (value is null) return list;

        foreach (var item in value)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            list.Add(item.Trim().ToLowerInvariant());
        }

        return list;
    }

    /// <summary>
    /// 把排除域名折进 query —— 上游没有排除字段，这是官方 CLI 的 `-site:` 做法。
    /// </summary>
    public static string BuildQuery(string query, IReadOnlyList<string>? excludeDomains)
    {
        var list = NormalizeDomains(excludeDomains);
        if (list.Count == 0) return query;

        return $"{query} ({string.Join(" ", list.Select(domain => $"-site:{domain}"))})";
    }

    /// <summary>取 URL 的 host，失败给空串。</summary>
    public static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Host.ToLowerInvariant() : "";

    /// <summary>
    /// 域名相等或为其子域。
    ///
    /// ⚠ **不能用 `EndsWith(domain)`** —— 那样 `a.com` 会误配 `nota.com`。
    /// </summary>
    public static bool DomainMatches(string host, string domain)
        => string.Equals(host, domain, StringComparison.Ordinal)
           || host.EndsWith("." + domain, StringComparison.Ordinal);

    /// <summary>校验 `freshness`：`d1`–`d30` / `m1`–`m12` / `y1`–`y5`，数字可省。</summary>
    public static bool ValidFreshness(string value)
        => Regex.IsMatch(value, @"^(?:d(?:[1-9]|[12]\d|30)?|m(?:[1-9]|1[0-2])?|y[1-5]?)$");

    /// <summary>
    /// 把搜索结果格式化成给模型看的 markdown。
    ///
    /// 客户端（尤其 CLI/harness）拿到的是一段可直接塞进上下文的文本，
    /// 而不是要它自己拼 JSON。
    /// </summary>
    public static string FormatSearchResults(SearchResult result)
    {
        if (result.Results.Count == 0)
        {
            return $"No results found for query: \"{result.Query}\"";
        }

        var sb = new StringBuilder();
        sb.Append("# Search Results for \"").Append(result.Query).Append("\"\n\n");

        for (var index = 0; index < result.Results.Count; index++)
        {
            var item = result.Results[index];

            sb.Append("## ").Append(index + 1).Append(". [")
              .Append(item.Title.Length > 0 ? item.Title : item.Url).Append("](").Append(item.Url).Append(")\n\n");
            sb.Append(item.Snippet.Length > 0 ? item.Snippet : "(no snippet)").Append("\n\n");
            sb.Append("**URL:** ").Append(item.Url);
            if (item.Site.Length > 0) sb.Append("\n\n**Site:** ").Append(item.Site);
            sb.Append("\n\n---\n\n");
        }

        var count = result.Results.Count;

        // ⚠ 显式给 CultureInfo.InvariantCulture：这条文本会进入模型上下文，
        //   不该跟着机器的区域设置变（CA1305）。虽然这里只有整数，
        //   但保持「输出给外部消费的文本一律 invariant」这条规矩一致。
        sb.Append(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"*Found {count} result{(count == 1 ? "" : "s")} • Provider: {result.Provider}*"));

        return sb.ToString();
    }

    /// <summary>抓取结果格式化（非 JSON 交付时的正文）。</summary>
    public static string FormatFetchResult(WebFetchResult result)
    {
        var header = result.Title.Length == 0 ? result.Url : $"{result.Title}\n{result.Url}";
        var tail = result.Truncated ? "\n\n[... 内容被上游截断]" : "";
        return $"{header}\n\n{result.Content}{tail}";
    }

    private static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d)) return (long)d;
        if (value.TryGetValue<string>(out var s) &&
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
