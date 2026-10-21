using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 一个假的 HTTP 消息处理器 —— 用来在测试里冒充上游。
///
/// ## ⚠ 为什么这个文件在 Gateway.Tests 里另存一份（t58 解的耦合）
///
/// 原先 `Gateway.Tests` 通过 `<ProjectReference Include="../BuddyDeck.Core.Tests" />`
/// 复用那边同名的 `FakeUpstream`。**那个引用是一笔债，今天咬了人**：
///
/// ```
/// Gateway.Tests
///   ├─ → BuddyDeck.Gateway      ✓
///   ├─ → BuddyDeck.TestSupport  ✓
///   └─ → BuddyDeck.Core.Tests   ✗ ← Core.Tests 一编译不过，Gateway.Tests 跟着跑不了
/// ```
/// 实测（t58 当天）：features 在写 `CreditsHttpTests.cs` 时把 `Core.Tests` 弄出
/// 20 个编译错误 → **`Gateway.Tests` 一条都跑不了**，而且红的是别人的文件，
/// 很容易让人误判「是不是我弄坏的」。
///
/// ⇒ 测试项目之间**不该有编译期依赖**：一个测试项目的编译错误不该拖住另一个。
/// 所以这份副本留在这里，引用断掉。
///
/// ## 为什么不直接用 `BuddyDeck.TestSupport.FakeUpstreamHandler`
///
/// 那才是**共享夹具的正解**（harness 的 t5 建的，能力更全）。但目前
/// `Gateway.Tests` 里 6 处调用只用到了本类型的两个静态工厂
/// （`Sse` / `Status`）与构造函数，改用 `FakeUpstreamHandler` 要动
/// `GatewayFixture` 与 `GatewayEndpointTests` 的断言写法 ——
/// **属于 t7 报告里那笔「夹具收敛」的债，该由那次收敛一并做**，
/// 不在本次「解耦」的范围内。本次只做**最小改动**：断引用 + 留本地副本。
///
/// （t7 的建议仍是：`GatewayFixture` 瘦身成 `TestSupport.GatewayHost` 之上的薄层，
/// 届时这份副本与该文件一起删。）
///
/// ## 为什么不用 Mock 库
///
/// 这个处理器就几十行，比引一个 mock 框架更直接。而且它记录的东西
/// （请求头、请求体）正是我们要断言的。
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
