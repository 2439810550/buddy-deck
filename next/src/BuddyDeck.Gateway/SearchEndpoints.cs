using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 联网端点族：搜索与网页抓取。
///
/// ## 端点清单（对齐旧版 `gateway.js`）
///
/// ```
/// POST /v1/search     联网搜索
/// POST /v1/webfetch   抓取网页正文
/// ```
///
/// ## 默认回 markdown 正文，不是 JSON
///
/// 客户端（CLI / harness）拿到就是能**直接塞进上下文**的文本；要结构化数据时
/// 显式传 `format: "json"`。这个取舍来自旧版 —— 它的调用方主要是
/// Claude Code / pi 这类需要「一段可读文本」的东西。
///
/// Content-Type 用 `text/plain` 而**不是** `text/markdown`：有些客户端不认后者，
/// 而这里的内容本来就是给人/模型读的，不是给浏览器渲染的。
///
/// ## 鉴权
///
/// **不豁免**，和其它 `/v1/*` 一样走网关统一校验。
/// </summary>
public sealed partial class SearchEndpoints : IEndpointFamily
{
    private readonly SearchClient _search;
    private readonly ILogger<SearchEndpoints> _log;

    public SearchEndpoints(SearchClient search, ILogger<SearchEndpoints> log)
    {
        _search = search;
        _log = log;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "[search] {Code}: {Message}")]
    private partial void LogSearchError(string code, string message);

    /// <inheritdoc />
    public async Task<bool> TryHandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (path.Length > 1 && path.EndsWith('/')) path = path.TrimEnd('/');

        if (!HttpMethods.IsPost(context.Request.Method)) return false;

        if (path == "/v1/search")
        {
            await SearchAsync(context).ConfigureAwait(false);
            return true;
        }

        if (path == "/v1/webfetch")
        {
            await WebFetchAsync(context).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// POST /v1/search
    ///
    /// 请求体（字段名同时接受 camelCase 与 snake_case，和旧版一样宽松）：
    /// ```
    /// { query, max_results|maxResults, freshness,
    ///   include_domains|includeDomains, exclude_domains|excludeDomains,
    ///   format: "json" | "markdown" }
    /// ```
    /// </summary>
    private async Task SearchAsync(HttpContext context)
    {
        var body = await ReadJsonBodyAsync(context).ConfigureAwait(false);
        if (body is null) return;

        try
        {
            var result = await _search.SearchAsync(
                body["query"]?.GetValue<string>() ?? body["q"]?.GetValue<string>(),
                ReadInt(body["max_results"] ?? body["maxResults"]),
                body["freshness"]?.GetValue<string>(),
                ReadStringList(body["include_domains"] ?? body["includeDomains"] ?? body["sites"]),
                ReadStringList(body["exclude_domains"] ?? body["excludeDomains"] ?? body["exclude"]),
                context.RequestAborted).ConfigureAwait(false);

            if (WantsJson(body))
            {
                await WriteJsonAsync(context, 200, ToJson(result)).ConfigureAwait(false);
                return;
            }

            await WriteTextAsync(context, SearchClient.FormatSearchResults(result)).ConfigureAwait(false);
        }
        catch (SearchException ex)
        {
            await RespondSearchErrorAsync(context, ex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// POST /v1/webfetch
    ///
    /// 请求体：`{ url, prompt?, content_format?, timeout?, max_length?, format? }`。
    /// 默认回 markdown 正文；`format: "json"` 回完整文档（含 title / truncated）。
    /// </summary>
    private async Task WebFetchAsync(HttpContext context)
    {
        var body = await ReadJsonBodyAsync(context).ConfigureAwait(false);
        if (body is null) return;

        try
        {
            var result = await _search.FetchPageAsync(
                body["url"]?.GetValue<string>(),
                body["prompt"]?.GetValue<string>(),
                body["content_format"]?.GetValue<string>(),
                ReadInt(body["timeout"]),
                ReadInt(body["max_length"] ?? body["maxLength"]),
                context.RequestAborted).ConfigureAwait(false);

            if (WantsJson(body))
            {
                await WriteJsonAsync(context, 200, new JsonObject
                {
                    ["object"] = "web_fetch_result",
                    ["url"] = result.Url,
                    ["title"] = result.Title,
                    ["content"] = result.Content,
                    ["contentType"] = result.ContentType,
                    ["contentLength"] = result.ContentLength,
                    ["truncated"] = result.Truncated,
                    ["statusCode"] = result.StatusCode,
                }).ConfigureAwait(false);
                return;
            }

            await WriteTextAsync(context, SearchClient.FormatFetchResult(result)).ConfigureAwait(false);
        }
        catch (SearchException ex)
        {
            await RespondSearchErrorAsync(context, ex).ConfigureAwait(false);
        }
    }

    // ── 形状转换 ──────────────────────────────────────────────

    private static JsonObject ToJson(SearchResult result)
    {
        var results = new JsonArray();
        foreach (var item in result.Results)
        {
            var entry = new JsonObject
            {
                ["title"] = item.Title,
                ["url"] = item.Url,
                ["snippet"] = item.Snippet,
            };
            if (item.Site.Length > 0) entry["site"] = item.Site;
            results.Add(entry);
        }

        return new JsonObject
        {
            ["object"] = "search_result",
            ["query"] = result.Query,
            ["provider"] = result.Provider,
            ["totalResults"] = result.Results.Count,
            ["responseTimeMs"] = result.ResponseTimeMs,
            ["results"] = results,
        };
    }

    private static bool WantsJson(JsonObject body)
        => string.Equals(body["format"]?.GetValue<string>(), "json", StringComparison.Ordinal);

    /// <summary>容忍 null / 单值字符串 / 数组三种输入 —— 客户端写法很杂。</summary>
    private static List<string>? ReadStringList(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonArray array:
                var list = new List<string>();
                foreach (var item in array)
                {
                    if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                    {
                        list.Add(text);
                    }
                }
                return list;

            case JsonValue single when single.TryGetValue<string>(out var one):
                return string.IsNullOrWhiteSpace(one) ? null : [one];

            default:
                return null;
        }
    }

    // ── 错误与输出 ────────────────────────────────────────────

    private async Task RespondSearchErrorAsync(HttpContext context, SearchException ex)
    {
        if (_log.IsEnabled(LogLevel.Warning))
        {
            LogSearchError(ex.Code, ex.Message);
        }

        await WriteErrorAsync(context, ex.Status, ex.OpenAiType, ex.Message).ConfigureAwait(false);
    }

    private static async Task<JsonObject?> ReadJsonBodyAsync(HttpContext context)
    {
        if (!IsJsonContentType(context.Request.ContentType))
        {
            await WriteErrorAsync(context, 415, "unsupported_media_type",
                "Content-Type must be application/json").ConfigureAwait(false);
            return null;
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);

        try
        {
            if (JsonNode.Parse(raw) is JsonObject body) return body;
        }
        catch (System.Text.Json.JsonException)
        {
            // 落到下面统一报错
        }

        await WriteErrorAsync(context, 400, "invalid_request_error",
            "请求体必须是合法的 JSON 对象").ConfigureAwait(false);
        return null;
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var mime = semicolon >= 0 ? contentType[..semicolon] : contentType;
        return mime.Trim().StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (value.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    private static async Task WriteTextAsync(HttpContext context, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

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
