using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 一个假的 HTTP 消息处理器 —— 用来在测试里冒充上游。
///
/// ## 为什么不用真的打上游
///
/// · 慢、要网络、会被限流
/// · 测不了错误路径（想让上游返回 429 得先真被限流）
/// · 会消耗真实账号的配额
///
/// ## 为什么不用 Mock 库
///
/// 这个处理器就几十行，比引一个 mock 框架更直接。而且它记录的东西
/// （请求头、请求体）正是我们要断言的。
///
/// ## 为什么提到单独的文件
///
/// 网关的测试也要用它 —— 把假上游塞在一个测试类的文件里，
/// 别的项目引用不到。这里保持 internal + 无状态工厂，谁都能用。
/// </summary>
public sealed class FakeUpstream : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public FakeUpstream(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

    /// <summary>收到过的请求（请求体已读出来存好 —— HttpContent 只能读一次）。</summary>
    public List<CapturedRequest> Requests { get; } = [];

    public sealed record CapturedRequest(
        string Method,
        string Url,
        IReadOnlyDictionary<string, string[]> Headers,
        string? Body);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var headers = request.Headers.ToDictionary(
            h => h.Key,
            h => h.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        Requests.Add(new CapturedRequest(
            request.Method.Method,
            request.RequestUri?.ToString() ?? "",
            headers,
            body));

        return _handler(request);
    }

    // ── 造响应的便捷方法 ──────────────────────────────────────

    public static HttpResponseMessage Ok(string body = """{"code":0,"data":{}}""", string contentType = "application/json")
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };

    public static HttpResponseMessage Status(HttpStatusCode code, string body = "")
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>造一个流式 SSE 响应。</summary>
    public static HttpResponseMessage Sse(params string[] dataLines)
    {
        var sb = new StringBuilder();
        foreach (var line in dataLines)
        {
            sb.Append("data: ").Append(line).Append('\n').Append('\n');
        }
        sb.Append("data: [DONE]\n\n");

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream"),
        };
    }

    /// <summary>包一层上游信封。</summary>
    public static string Envelope(int code, JsonNode? data, string message = "")
    {
        var obj = new JsonObject { ["code"] = code };
        if (message.Length > 0) obj["message"] = message;
        if (data is not null) obj["data"] = data;
        return obj.ToJsonString();
    }
}
