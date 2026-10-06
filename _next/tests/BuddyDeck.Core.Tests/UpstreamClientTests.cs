using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

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
/// 这个处理器就 30 行，比引一个 mock 框架更直接。而且它记录的东西
/// （请求头、请求体）正是我们要断言的。
/// </summary>
internal sealed class FakeUpstream : HttpMessageHandler
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

/// <summary>
/// 上游客户端的测试。全部走假上游，不打真实服务。
/// </summary>
public class UpstreamClientTests
{
    private static readonly Credential TestCredential = new()
    {
        AccessToken = "test-token",
        RefreshToken = "test-refresh",
        Uid = "u1",
        EnterpriseId = "e1",
        Domain = "workbuddy.cn",
        Label = "测试号",
    };

    private static (UpstreamClient Client, FakeUpstream Fake) Make(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var fake = new FakeUpstream(handler);
        var http = new HttpClient(fake);
        var client = new UpstreamClient(http, UpstreamVariant.China, NullLogger<UpstreamClient>.Instance);
        return (client, fake);
    }

    // ── 聊天请求 ──────────────────────────────────────────────

    [Fact]
    public async Task 聊天成功时返回响应流()
    {
        var (client, _) = Make(_ => FakeUpstream.Sse("""{"choices":[{"delta":{"content":"hi"}}]}"""));

        var result = await client.ChatStreamAsync(TestCredential, "{}", null, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(200, result.Status);
        Assert.NotNull(result.Response);
        result.Response.Dispose();
    }

    [Fact]
    public async Task 聊天请求打到正确的路径()
    {
        var (client, fake) = Make(_ => FakeUpstream.Ok());

        await client.ChatStreamAsync(TestCredential, "{}", null, null, CancellationToken.None);

        var req = fake.Requests.Single();
        Assert.Equal("POST", req.Method);
        Assert.Equal("https://copilot.tencent.com/v2/chat/completions", req.Url);
    }

    [Fact]
    public async Task 聊天请求带上身份头和_IDE_族()
    {
        var (client, fake) = Make(_ => FakeUpstream.Ok());

        await client.ChatStreamAsync(TestCredential, "{}", "WorkBuddy/5.6.2", "5.6.2", CancellationToken.None);

        var h = fake.Requests.Single().Headers;
        Assert.Equal("Bearer test-token", h["Authorization"].Single());
        Assert.Equal("u1", h["X-User-Id"].Single());
        Assert.Equal("e1", h["X-Enterprise-Id"].Single());
        Assert.Equal("WorkBuddy", h["X-IDE-Type"].Single());
        Assert.Equal("5.6.2", h["X-IDE-Version"].Single());
    }

    // ── 错误分类 ──────────────────────────────────────────────

    [Fact]
    public async Task 普通_429_分类成限流()
    {
        var (client, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.TooManyRequests, """{"message":"too many"}"""));

        var result = await client.ChatStreamAsync(TestCredential, "{}", null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(429, result.Status);
        Assert.Equal(UpstreamErrorKind.RateLimited, result.Kind);
    }

    [Fact]
    public async Task _429_带_6004_分类成模型级限流()
    {
        // 这个区分很重要：模型级限流换模型就能解决，不用换号。
        // 分不清就只能一律换号，白白浪费账号配额。
        var (client, _) = Make(_ => FakeUpstream.Status(
            HttpStatusCode.TooManyRequests,
            """{"code":6004,"message":"model rate limited"}"""));

        var result = await client.ChatStreamAsync(TestCredential, "{}", null, null, CancellationToken.None);

        Assert.Equal(UpstreamErrorKind.ModelRateLimited, result.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task _401和403_分类成凭证失效(HttpStatusCode code)
    {
        var (client, _) = Make(_ => FakeUpstream.Status(code, """{"message":"bad token"}"""));

        var result = await client.ChatStreamAsync(TestCredential, "{}", null, null, CancellationToken.None);

        Assert.Equal(UpstreamErrorKind.Unauthorized, result.Kind);
    }

    [Fact]
    public async Task _500_分类成服务端错误()
    {
        var (client, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.InternalServerError, "boom"));

        var result = await client.ChatStreamAsync(TestCredential, "{}", null, null, CancellationToken.None);

        Assert.Equal(UpstreamErrorKind.Server, result.Kind);
    }

    [Fact]
    public async Task 连接失败时状态码是_0()
    {
        // 「连不上」和「连上了但被拒」对用户是两件事 —— 提示语不同。
        var (client, _) = Make(_ => throw new HttpRequestException("DNS 解析失败"));

        var result = await client.ChatStreamAsync(TestCredential, "{}", null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, result.Status);
        Assert.Contains("transport error", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 错误正文会被截断()
    {
        // 上游出错时可能吐一大坨 HTML，不能原样带进日志和响应。
        var huge = new string('x', 100_000);
        var (client, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.BadGateway, huge));

        var result = await client.ChatStreamAsync(TestCredential, "{}", null, null, CancellationToken.None);

        Assert.True(result.Message!.Length <= 4100, $"实际 {result.Message.Length}");
    }

    // ── 刷新 token ────────────────────────────────────────────

    [Fact]
    public async Task 刷新成功时返回新_token_和滑动窗口()
    {
        var data = new JsonObject
        {
            ["accessToken"] = "new-access",
            ["refreshToken"] = "new-refresh",
            ["expiresIn"] = 7200,
            ["refreshExpiresIn"] = 2592000,
            ["domain"] = "workbuddy.cn",
        };
        var (client, _) = Make(_ => FakeUpstream.Ok(FakeUpstream.Envelope(0, data)));

        var outcome = await client.RefreshTokenAsync(TestCredential, CancellationToken.None);

        Assert.Equal("new-access", outcome.AccessToken);
        Assert.Equal("new-refresh", outcome.RefreshToken);
        Assert.Equal(7200, outcome.ExpiresInSec);
        // 滑动窗口：每次刷新服务端都给满额，记下来才能判断该什么时候续
        Assert.Equal(2592000, outcome.RefreshExpiresInSec);
    }

    [Fact]
    public async Task 刷新请求打到正确的路径且不带_IDE_族()
    {
        var (client, fake) = Make(_ => FakeUpstream.Ok(FakeUpstream.Envelope(0, new JsonObject { ["accessToken"] = "x" })));

        await client.RefreshTokenAsync(TestCredential, CancellationToken.None);

        var req = fake.Requests.Single();
        Assert.Equal("https://copilot.tencent.com/v2/plugin/auth/token/refresh", req.Url);
        Assert.Equal("test-refresh", req.Headers["X-Refresh-Token"].Single());
        Assert.Equal("workbuddy", req.Headers["X-Auth-Refresh-Source"].Single());
        // 归属族混进刷新请求会把用量账算乱
        Assert.False(req.Headers.ContainsKey("X-IDE-Type"));
    }

    [Fact]
    public async Task 刷新没返回_token_时抛异常()
    {
        var (client, _) = Make(_ => FakeUpstream.Ok(FakeUpstream.Envelope(0, new JsonObject())));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.RefreshTokenAsync(TestCredential, CancellationToken.None));

        Assert.Contains("重新登录", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 刷新返回业务错误码时抛异常()
    {
        var (client, _) = Make(_ => FakeUpstream.Ok(FakeUpstream.Envelope(40001, null, "refresh token 过期")));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.RefreshTokenAsync(TestCredential, CancellationToken.None));

        Assert.Contains("40001", ex.Message, StringComparison.Ordinal);
    }

    // ── 模型目录 ──────────────────────────────────────────────

    [Fact]
    public async Task 拉目录打到_billing_域名()
    {
        var (client, fake) = Make(_ => FakeUpstream.Ok("""{"models":[]}"""));

        await client.FetchCatalogAsync(TestCredential, null, CancellationToken.None);

        Assert.Equal("https://www.codebuddy.cn/v3/config", fake.Requests.Single().Url);
    }

    [Fact]
    public async Task 拉目录不带_IDE_族()
    {
        var (client, fake) = Make(_ => FakeUpstream.Ok("""{"models":[]}"""));

        await client.FetchCatalogAsync(TestCredential, "WorkBuddy/5.6.2", CancellationToken.None);

        var h = fake.Requests.Single().Headers;
        Assert.False(h.ContainsKey("X-IDE-Type"), "目录请求不该带归属族");
        Assert.Equal("WorkBuddy/5.6.2", h["User-Agent"].Single());
    }

    [Fact]
    public async Task 目录不是合法_JSON_时抛异常()
    {
        var (client, _) = Make(_ => FakeUpstream.Ok("<html>不是 json</html>"));

        await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCatalogAsync(TestCredential, null, CancellationToken.None));
    }

    // ── 取消 ──────────────────────────────────────────────────

    [Fact]
    public async Task 客户端取消时原样抛出_不当成上游错误()
    {
        // 用户按了「停止生成」不是错误，上层要能区分。
        using var cts = new CancellationTokenSource();
        var (client, _) = Make(_ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return FakeUpstream.Ok();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ChatStreamAsync(TestCredential, "{}", null, null, cts.Token));
    }
}
