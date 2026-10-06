using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Core;

/// <summary>一次上游调用的结果。</summary>
public sealed record UpstreamResult
{
    public required bool Ok { get; init; }

    /// <summary>HTTP 状态码。0 表示连接层失败（DNS、超时、TLS）。</summary>
    public required int Status { get; init; }

    /// <summary>错误分类。<see cref="UpstreamErrorKind"/> 的字符串形式，和旧版网关一致。</summary>
    public string? Kind { get; init; }

    /// <summary>错误正文（截断过的）。</summary>
    public string? Message { get; init; }

    /// <summary>成功时的响应流。**调用方负责释放。**</summary>
    public HttpResponseMessage? Response { get; init; }
}

/// <summary>
/// 上游错误分类。
///
/// 为什么分类而不是直接用 HTTP 状态码：上游把很多语义塞在 200 里
/// （业务 code），状态码本身不够用。分类完之后网关才能决定
/// 「该换号」还是「该重试」还是「该直接报错给用户」。
/// </summary>
public static class UpstreamErrorKind
{
    /// <summary>限流。可以换模型或换号重试。</summary>
    public const string RateLimited = "rate_limit";

    /// <summary>模型级限流（HTTP 429 + 业务码 6004）。这个换号有用、换模型也有用。</summary>
    public const string ModelRateLimited = "model_rate_limit";

    /// <summary>凭证失效。要刷新 token 或者让用户重新登录。</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>上游服务端错误。可以重试。</summary>
    public const string Server = "server";

    /// <summary>请求本身有问题。重试没用。</summary>
    public const string BadRequest = "bad_request";

    /// <summary>其他。</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// 上游 HTTP 客户端。
///
/// ## 职责边界
///
/// 只做「发一个请求、把结果分类」这件事。**不做**：
///   · 换号重试（那是池的活）
///   · 刷新 token（那是凭证管理的活）
///   · 记账（那是 usage 的活）
///
/// 这样它足够薄，能塞假实现进测试。
///
/// ## 为什么状态码 0 是个正经的 kind
///
/// 连接层失败（DNS 解析不了、TLS 握手失败、超时）拿不到 HTTP 状态码。
/// 旧版网关用 `status: 0` 表示，这里保留 —— 因为「连不上」和「连上了但被拒」
/// 对用户是完全不同的两件事，提示语也不一样。
/// </summary>
public sealed partial class UpstreamClient
{
    private readonly HttpClient _http;
    private readonly UpstreamVariant _variant;
    private readonly ILogger<UpstreamClient> _log;

    /// <summary>错误正文最多读这么多字节。上游出错时可能吐一大坨 HTML。</summary>
    private const int ErrorBodyLimit = 4096;

    // 日志用源生成的委托，不用 LogWarning(...) 那种带 params object[] 的重载 ——
    // 后者每次调用都要装箱参数、分配数组，在热路径上（每个请求至少一次）不划算。
    // 分析器 CA1848 会强制这一点。
    [LoggerMessage(Level = LogLevel.Warning, Message = "上游连接失败（{Kind}）")]
    private partial void LogTransportFailure(Exception ex, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "上游 {Status} → {Kind}: {Body}")]
    private partial void LogUpstreamError(int status, string kind, string body);

    public UpstreamClient(HttpClient http, UpstreamVariant variant, ILogger<UpstreamClient> log)
    {
        _http = http;
        _variant = variant;
        _log = log;
    }

    /// <summary>
    /// 发一次聊天请求（流式）。
    ///
    /// @param preparedBody 已经过 <see cref="RequestPipeline"/> 处理的 JSON 文本。
    /// @param userAgent    桌面身份 UA。null 用 CLI 形态。
    /// @param clientVersion 跟 UA 配套的版本号。
    /// </summary>
    public async Task<UpstreamResult> ChatStreamAsync(
        Credential credential,
        string preparedBody,
        string? userAgent,
        string? clientVersion,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_variant.ChatBase}/v2/chat/completions");

        request.Content = new StringContent(preparedBody, Encoding.UTF8, "application/json");
        UpstreamHeaders.ApplyChat(request, credential, _variant, userAgent, clientVersion);

        return await SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 刷新 token。
    ///
    /// ## 滑动窗口是关键
    ///
    /// 服务端每次刷新都会重新给一个**满额**的 refresh 窗口
    /// （`refreshExpiresIn`）。把这个值记下来才能判断「还能续多久」、
    /// 什么时候该主动续 —— 不记的话只能等过期，那就没法做自动续期了。
    /// </summary>
    public async Task<RefreshOutcome> RefreshTokenAsync(Credential credential, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_variant.ChatBase}/v2/plugin/auth/token/refresh");

        UpstreamHeaders.ApplyRefresh(request, credential, _variant);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var result = await SendAsync(request, ct).ConfigureAwait(false);
        if (!result.Ok || result.Response is null)
        {
            throw new UpstreamException(result.Status, result.Kind ?? UpstreamErrorKind.Unknown, result.Message ?? "");
        }

        var envelope = await ReadEnvelopeAsync(result.Response, ct).ConfigureAwait(false);
        if (envelope.Code != 0)
        {
            throw new UpstreamException(
                result.Status, UpstreamErrorKind.Unknown,
                $"刷新返回业务错误码 {envelope.Code}: {envelope.Message}");
        }

        var data = envelope.Data;
        var accessToken = data?["accessToken"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new UpstreamException(
                result.Status, UpstreamErrorKind.Unknown,
                "刷新没有返回 accessToken —— 要在 WorkBuddy App 里重新登录");
        }

        return new RefreshOutcome
        {
            AccessToken = accessToken,
            RefreshToken = data?["refreshToken"]?.GetValue<string>() ?? "",
            ExpiresInSec = IntOf(data, "expiresIn"),
            RefreshExpiresInSec = IntOf(data, "refreshExpiresIn"),
            Domain = data?["domain"]?.GetValue<string>() ?? "",
        };
    }

    /// <summary>拉模型目录（`/v3/config`）。</summary>
    public async Task<JsonNode?> FetchCatalogAsync(Credential credential, string? userAgent, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_variant.BillingBase}{_variant.CatalogPath}");

        // 注意：**不带 IDE 归属族** —— 那是 chat 和探活专属的。
        // 混进目录请求会把用量归属算乱。
        if (!string.IsNullOrEmpty(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.AccessToken}");

        var result = await SendAsync(request, ct).ConfigureAwait(false);
        if (!result.Ok || result.Response is null)
        {
            throw new UpstreamException(result.Status, result.Kind ?? UpstreamErrorKind.Unknown, result.Message ?? "");
        }

        var text = await result.Response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        result.Response.Dispose();

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new UpstreamException(result.Status, UpstreamErrorKind.Unknown, $"目录不是合法 JSON：{ex.Message}");
        }
    }

    // ── 内部 ──────────────────────────────────────────────────

    private async Task<UpstreamResult> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            // ResponseHeadersRead：不要缓冲整个响应体。
            // 聊天是流式的，缓冲等于把流式的意义抹掉。
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 客户端主动断开 —— 不是错误，原样抛让上层知道
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // 连接层失败：DNS / TLS / 超时 / 连接被重置
            LogTransportFailure(ex, ex.GetType().Name);
            return new UpstreamResult
            {
                Ok = false,
                Status = 0,
                Kind = UpstreamErrorKind.Server,
                Message = $"transport error: {ex.Message}",
            };
        }

        if (response.IsSuccessStatusCode)
        {
            return new UpstreamResult { Ok = true, Status = (int)response.StatusCode, Response = response };
        }

        // 失败：读一小段正文用于分类和报错
        var body = "";
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            body = raw.Length > ErrorBodyLimit ? raw[..ErrorBodyLimit] : raw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // 连错误正文都读不到 —— 不影响分类（状态码够了）
        }
        finally
        {
            response.Dispose();
        }

        var status = (int)response.StatusCode;
        var kind = Classify(status, body);

        LogUpstreamError(status, kind, Truncate(body, 200));

        return new UpstreamResult { Ok = false, Status = status, Kind = kind, Message = body };
    }

    /// <summary>
    /// 按状态码 + 正文分类。
    ///
    /// 为什么要看正文：上游把限流的细分语义塞在业务 code 里 ——
    /// 429 + `code: 6004` 是**模型级**限流（换个模型就好），
    /// 而普通的 429 是整个账号都在限流（要换号）。分不清就只能一律换号，
    /// 那会白白浪费账号配额。
    /// </summary>
    private static string Classify(int status, string body)
    {
        if (status == 429)
        {
            // 模型级限流：换模型能解决
            if (body.Contains("6004", StringComparison.Ordinal))
            {
                return UpstreamErrorKind.ModelRateLimited;
            }
            return UpstreamErrorKind.RateLimited;
        }

        if (status is 401 or 403) return UpstreamErrorKind.Unauthorized;
        if (status >= 500) return UpstreamErrorKind.Server;
        if (status >= 400) return UpstreamErrorKind.BadRequest;

        return UpstreamErrorKind.Unknown;
    }

    /// <summary>
    /// 读上游的统一信封 `{code, message, data}`。
    ///
    /// 上游有两种回法：直接给数据，或者包一层信封。两种都要认 ——
    /// 包不包取决于接口，不是我们能选的。
    /// </summary>
    private static async Task<Envelope> ReadEnvelopeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        response.Dispose();

        try
        {
            var node = JsonNode.Parse(text);
            if (node is not JsonObject obj) return new Envelope(0, "", null);

            var code = IntOf(obj, "code") ?? 0;
            var message = obj["message"]?.GetValue<string>() ?? obj["msg"]?.GetValue<string>() ?? "";
            var data = obj["data"] as JsonObject ?? obj;
            return new Envelope(code, message, data);
        }
        catch (JsonException)
        {
            throw new UpstreamException(200, UpstreamErrorKind.Unknown, "上游返回的不是合法 JSON");
        }
    }

    private static int? IntOf(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        return null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private sealed record Envelope(int Code, string Message, JsonObject? Data);
}

/// <summary>刷新 token 拿到的东西。</summary>
public sealed record RefreshOutcome
{
    public required string AccessToken { get; init; }

    /// <summary>上游有时会轮换 refresh token。空表示沿用旧的。</summary>
    public string RefreshToken { get; init; } = "";

    /// <summary>access token 有效期（秒）。</summary>
    public int? ExpiresInSec { get; init; }

    /// <summary>
    /// refresh token 的有效期（秒）。
    ///
    /// **滑动窗口的核心** —— 每次刷新服务端都重新给满额。记下它才能
    /// 判断「还能续多久」，进而决定什么时候主动续期。
    /// </summary>
    public int? RefreshExpiresInSec { get; init; }

    public string Domain { get; init; } = "";
}

/// <summary>上游调用失败。</summary>
public sealed class UpstreamException : Exception
{
    public UpstreamException(int status, string kind, string message) : base(message)
    {
        Status = status;
        Kind = kind;
    }

    public int Status { get; }
    public string Kind { get; }
}
