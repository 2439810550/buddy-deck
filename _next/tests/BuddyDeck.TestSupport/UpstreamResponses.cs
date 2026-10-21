using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace BuddyDeck.TestSupport;

/// <summary>
/// 一个「响应模板」—— <see cref="HttpResponseMessage"/> 的延迟工厂。
///
/// 为什么要这一层而不是直接给 <see cref="HttpResponseMessage"/>：
/// 响应对象是**一次性**的（交出去就被 HttpClient Dispose 了），
/// 而夹具要在路由表里长期持有一个模板、每次请求现造一个。
/// 直接存 <see cref="HttpResponseMessage"/> 的代码第二次请求就会炸。
///
/// 隐式转换到 <see cref="HttpResponseMessage"/> 是给一次性场景的语法糖，
/// 那种场景本来也只用一次。
/// </summary>
public sealed class UpstreamResponse
{
    private readonly Func<HttpResponseMessage> _factory;

    public UpstreamResponse(Func<HttpResponseMessage> factory) => _factory = factory;

    /// <summary>现造一个新的响应实例。</summary>
    public HttpResponseMessage Build() => _factory();

    /// <summary>语法糖：可以让模板出现在需要响应实例的位置（只用一次的场景）。</summary>
    public static implicit operator HttpResponseMessage(UpstreamResponse template) => template.Build();

    public static UpstreamResponse From(Func<HttpResponseMessage> factory) => new(factory);
}

/// <summary>
/// 上游响应的工厂集合。
///
/// 这里每一个方法都对应一种**真实出现过的上游行为**，不是凭空造的
/// —— 写测试时需要造什么，看这个类的清单就够了：
///
/// ```
/// 正常      Ok / Envelope / EnvelopeJson / Text / Json
/// 流式      SseChunks / SseText / SseFrames / SseBroken / SseAborted
/// 限流      TooManyRequests / ModelRateLimited          ← 后者就是 429 + code 6004
/// 凭证      Unauthorized(401) / Forbidden(403)
/// 服务端    ServerError(500) / BadGateway(502) / ServiceUnavailable(503)
/// 脏数据    HugeErrorBody / InvalidJson / InvalidSsePayload
/// ```
///
/// ## 两条约定
///
/// 1. 工厂出来的每一个响应都是**新对象**，可以安全地反复用同一个模板。
/// 2. 流的**分块边界由调用方给定**（<see cref="SseChunks"/>）——
///    这是刻意留的：SSE 的切块必须跨块边界解析，一次网络读可能拿到半行、
///    也可能拿到三行半。不给边界的假上游测不出 <c>SseReader</c> 的缓冲逻辑。
/// </summary>
public static class UpstreamResponses
{
    private const string JsonContentType = "application/json";

    // ── 正常 ──────────────────────────────────────────────────

    /// <summary>200 + 任意文本 + 任意 Content-Type。最底层的一个。</summary>
    public static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = JsonContentType)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return response;
    }

    /// <summary>200 + JSON 文本（内容由调用方自己拼）。</summary>
    public static HttpResponseMessage Json(string json) => Text(json, HttpStatusCode.OK, JsonContentType);

    /// <summary>200 + 上游的统一信封 `{code, message, data}`。</summary>
    public static HttpResponseMessage Envelope(int code = 0, JsonNode? data = null, string message = "")
        => Text(EnvelopeBody(code, data, message));

    /// <summary>只拼信封正文（要嵌到别的地方时用）。</summary>
    public static string EnvelopeBody(int code = 0, JsonNode? data = null, string message = "")
    {
        var obj = new JsonObject { ["code"] = code };
        if (message.Length > 0) obj["message"] = message;
        if (data is not null) obj["data"] = data;
        return obj.ToJsonString();
    }

    /// <summary>
    /// 刷新 token 的成功响应（对齐 <c>UpstreamClient.RefreshTokenAsync</c> 读的字段）。
    ///
    /// 注意 `refreshExpiresIn` —— 它是滑动窗口的核心，每次刷新服务端都重新给满额。
    /// </summary>
    public static HttpResponseMessage RefreshOk(
        string accessToken,
        string refreshToken = "rt-new",
        int expiresIn = 3600,
        int refreshExpiresIn = 30 * 24 * 3600,
        string domain = "")
    {
        var data = new JsonObject
        {
            ["accessToken"] = accessToken,
            ["refreshToken"] = refreshToken,
            ["expiresIn"] = expiresIn,
            ["refreshExpiresIn"] = refreshExpiresIn,
        };
        if (domain.Length > 0) data["domain"] = domain;
        return Envelope(0, data);
    }

    /// <summary>模型目录的响应（`/v3/config`）。</summary>
    public static HttpResponseMessage Catalog(params string[] modelIds)
    {
        var models = new JsonArray();
        foreach (var id in modelIds) models.Add(new JsonObject { ["id"] = id, ["name"] = id });
        return Envelope(0, new JsonObject { ["models"] = models });
    }

    // ── 流式 ──────────────────────────────────────────────────

    /// <summary>
    /// 标准 SSE 响应：每条分片占一个块，块之间用 `\n\n` 分开，末尾补 `data: [DONE]\n\n`。
    ///
    /// @param dataLines 每条 data 载荷（**不含** `data: ` 前缀，也**不含** `[DONE]`）。
    /// @param crlf      用 `\r\n` 而不是 `\n`。HTTP 上行结束本来是 CRLF，
    ///                  有些上游就是这么发的 —— 想确认 reader 认两样时用它。
    /// </summary>
    public static HttpResponseMessage SseText(IEnumerable<string> dataLines, bool crlf = false)
    {
        var newline = crlf ? "\r\n" : "\n";
        var sb = new StringBuilder();
        foreach (var line in dataLines)
        {
            sb.Append("data: ").Append(line).Append(newline).Append(newline);
        }
        sb.Append("data: [DONE]").Append(newline).Append(newline);
        return SseRaw(sb.ToString());
    }

    /// <summary>多行重载（最常用）。</summary>
    public static HttpResponseMessage SseText(params string[] dataLines) => SseText(dataLines, crlf: false);

    /// <summary>
    /// **分块可控**的 SSE 响应 —— 夹具里最重要的一条路。
    ///
    /// 每个 <see cref="SseChunkSpec"/> 是「一次网络读会拿到的字节」，块边界
    /// **故意可以切在行中间**：真实网络上一次 read 拿到半行是常态，
    /// 而 <c>SseReader</c> 的增量扫行逻辑只有在这种情况下才被测到。
    ///
    /// <code>
    /// // 第 2 块把一行切成两半 —— reader 必须自己缓冲
    /// var response = UpstreamResponses.SseChunks(
    ///     "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"你\"}}]}\n\n",
    ///     "data: {\"choices\":[{\"delta\":{\"con",
    ///     "tent\":\"好\"}}]}\n\n",
    ///     "data: [DONE]\n\n");
    /// </code>
    /// </summary>
    public static HttpResponseMessage SseChunks(params string[] chunks)
        => SseRaw(string.Concat(chunks));

    /// <summary>
    /// 从「一个事件一条」的描述生成 SSE，自动切块。
    ///
    /// @param partsPerFrame 每个事件切成几块（≥2 时边界落在事件中间，
    ///                      用来验证「块边界和事件边界无关」）。
    /// @param withDone      末尾要不要补 `data: [DONE]`。
    /// </summary>
    public static HttpResponseMessage SseFrames(
        IEnumerable<string> dataLines,
        int partsPerFrame = 1,
        bool withDone = true,
        bool crlf = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partsPerFrame, 1);

        var newline = crlf ? "\r\n" : "\n";
        var chunks = new List<string>();

        foreach (var line in dataLines)
        {
            var frame = $"data: {line}{newline}{newline}";

            if (partsPerFrame == 1)
            {
                chunks.Add(frame);
                continue;
            }

            // 按长度均分 —— 边界会落在行中间，这正是要测的
            var size = Math.Max(1, (int)Math.Ceiling(frame.Length / (double)partsPerFrame));
            for (var offset = 0; offset < frame.Length; offset += size)
            {
                chunks.Add(frame.Substring(offset, Math.Min(size, frame.Length - offset)));
            }
        }

        if (withDone) chunks.Add($"data: [DONE]{newline}{newline}");

        return SseRaw(string.Concat(chunks));
    }

    /// <summary>
    /// **没有 `[DONE]` 就断流** 的 SSE。
    ///
    /// 上游中途挂掉时就是这样：最后一帧发完，TCP 关了。
    /// 网关必须把已经收到的处理完再收摊，不能卡在等 `[DONE]` 上。
    /// </summary>
    public static HttpResponseMessage SseBroken(params string[] dataLines)
        => SseRaw(BuildFrames(dataLines, withDone: false, crlf: false));

    /// <summary>
    /// 断在**一帧中间**的 SSE：最后一条数据只有半行 JSON。
    ///
    /// 这是最刁钻的一种 —— reader 会把它当「还没成行的尾巴」攒着，
    /// 流结束时的兜底逻辑必须原样吐出去（<c>SseReader</c> 末尾那段），
    /// 否则最后一片内容会凭空消失。
    /// </summary>
    public static HttpResponseMessage SseAborted(string completePayload, string partialTail)
        => SseRaw($"data: {completePayload}\n\ndata: {partialTail}");

    /// <summary>裸 SSE 正文（调用方自己拼好了 `data:` 前缀和空行）。</summary>
    public static HttpResponseMessage SseRaw(string rawBody)
        => Text(rawBody, HttpStatusCode.OK, "text/event-stream; charset=utf-8");

    /// <summary>
    /// SSE 里混一条**非法 JSON** 的分片。
    ///
    /// 上游偶尔会吐一条不完整的 JSON。<c>SseSanitizer</c> 的处理是
    /// **原样转发**而不是丢掉 —— 丢掉等于用户那一句话少一段。
    /// 这个工厂就是用来钉住这个行为的。
    /// </summary>
    public static HttpResponseMessage InvalidSsePayload(string validPayload, string garbagePayload, bool withDone = true)
    {
        var chunks = new List<string>
        {
            $"data: {validPayload}\n\n",
            $"data: {garbagePayload}\n\n",
        };
        if (withDone) chunks.Add("data: [DONE]\n\n");
        return SseRaw(string.Concat(chunks));
    }

    /// <summary>拼一组 SSE 帧正文。</summary>
    private static string BuildFrames(IEnumerable<string> dataLines, bool withDone, bool crlf)
    {
        var newline = crlf ? "\r\n" : "\n";
        var sb = new StringBuilder();
        foreach (var line in dataLines)
        {
            sb.Append("data: ").Append(line).Append(newline).Append(newline);
        }
        if (withDone) sb.Append("data: [DONE]").Append(newline).Append(newline);
        return sb.ToString();
    }

    // ── 限流 ──────────────────────────────────────────────────

    /// <summary>
    /// 普通 429 —— **整个账号**被限流。
    ///
    /// 网关照这个的处置是「换号」（或者等冷却），不是「换模型」。
    /// </summary>
    public static HttpResponseMessage TooManyRequests(string message = "too many requests", long? resetAtMs = null)
    {
        var obj = new JsonObject
        {
            ["code"] = 429,
            ["message"] = message,
        };
        if (resetAtMs is not null) obj["resetAt"] = resetAtMs.Value;

        var response = Text(obj.ToJsonString(), HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "5");
        return response;
    }

    /// <summary>
    /// **模型级**限流：HTTP 429 + 正文里的业务码 `6004`。
    ///
    /// 这是全项目最容易被搞错的一条语义（HANDOVER 第三节第 4 条）：
    /// `429 + 正文含 "6004"` = 只有这个**模型**被限，换模型能解决，
    /// 换号是浪费。分不清就只能一律换号 —— 白扔配额。
    ///
    /// 注意分类是 **按正文里有没有 "6004" 这个子串** 判的
    /// （<c>UpstreamClient.Classify</c>），所以这里 code 直接就是 6004。
    /// </summary>
    public static HttpResponseMessage ModelRateLimited(string model = "", string message = "model rate limited")
    {
        var obj = new JsonObject
        {
            ["code"] = 6004,
            ["message"] = message,
        };
        if (model.Length > 0) obj["model"] = model;
        return Text(obj.ToJsonString(), HttpStatusCode.TooManyRequests);
    }

    // ── 凭证 / 服务端 ─────────────────────────────────────────

    /// <summary>401 —— 凭证失效，要重新登录或刷新。</summary>
    public static HttpResponseMessage Unauthorized(string message = "bad token")
        => Text(new JsonObject { ["code"] = 401, ["message"] = message }.ToJsonString(), HttpStatusCode.Unauthorized);

    /// <summary>403 —— 有了 token 但没权限 / 身份头不对。</summary>
    public static HttpResponseMessage Forbidden(string message = "forbidden")
        => Text(new JsonObject { ["code"] = 403, ["message"] = message }.ToJsonString(), HttpStatusCode.Forbidden);

    /// <summary>5xx —— 上游自己挂了，可以重试。</summary>
    public static HttpResponseMessage ServerError(string message = "internal error", int status = 500)
        => Text(new JsonObject { ["code"] = status, ["message"] = message }.ToJsonString(), (HttpStatusCode)status);

    /// <summary>502。</summary>
    public static HttpResponseMessage BadGateway(string message = "bad gateway") => ServerError(message, 502);

    /// <summary>503。</summary>
    public static HttpResponseMessage ServiceUnavailable(string message = "service unavailable") => ServerError(message, 503);

    // ── 脏数据 ────────────────────────────────────────────────

    /// <summary>
    /// **超长错误正文**。
    ///
    /// 上游出错时经常吐一坨 HTML 错误页（网关配置错误时的表现）。
    /// <c>UpstreamClient</c> 只读前 4096 字节 —— 这个工厂就是来钉住
    /// 「不会把整个 HTML 读进内存」和「截断逻辑真的生效」的。
    /// </summary>
    public static HttpResponseMessage HugeErrorBody(int bytes = 100_000, string prefix = "上游错误页")
    {
        var sb = new StringBuilder(bytes + prefix.Length + 32);
        sb.Append(prefix);
        while (sb.Length < bytes) sb.Append('x');
        return Text(sb.ToString(), HttpStatusCode.BadGateway, "text/html; charset=utf-8");
    }

    /// <summary>非法 JSON（不是 JSON 的正文）。</summary>
    public static HttpResponseMessage InvalidJson(string body = "<html><body>502 Bad Gateway</body></html>")
        => Text(body, HttpStatusCode.OK, "application/json");

    /// <summary>空正文（上游 200 但什么都没给）。</summary>
    public static HttpResponseMessage EmptyBody(HttpStatusCode status = HttpStatusCode.OK)
        => Text("", status, "application/json");

    /// <summary>
    /// 复制一份响应（同一个模板被多次使用时）。
    ///
    /// 所有工厂出来的响应都自带全新的 <see cref="ByteArrayContent"/>，
    /// 所以复制 = 换个壳、共享同一份只读字节数组。
    /// </summary>
    internal static HttpResponseMessage Clone(HttpResponseMessage source)
    {
        if (source.Content is null)
        {
            return new HttpResponseMessage(source.StatusCode) { Version = source.Version };
        }

        var content = source.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

        var copy = new HttpResponseMessage(source.StatusCode)
        {
            Version = source.Version,
            Content = new ByteArrayContent(content),
        };

        copy.Content.Headers.Clear();
        foreach (var header in source.Content.Headers)
        {
            copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in source.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return copy;
    }
}

/// <summary>SSE 分块的描述（给需要显式表达「这块是什么」的用例）。</summary>
/// <param name="Raw">这一块原始的字节内容。</param>
public sealed record SseChunkSpec(string Raw)
{
    public static SseChunkSpec Of(string raw) => new(raw);

    /// <summary>一条完整的数据帧。</summary>
    public static SseChunkSpec Frame(string payload, bool crlf = false)
        => new($"data: {payload}{(crlf ? "\r\n" : "\n")}{(crlf ? "\r\n" : "\n")}");

    /// <summary>终止帧。</summary>
    public static SseChunkSpec Done => new("data: [DONE]\n\n");

    public static implicit operator SseChunkSpec(string raw) => new(raw);
}
