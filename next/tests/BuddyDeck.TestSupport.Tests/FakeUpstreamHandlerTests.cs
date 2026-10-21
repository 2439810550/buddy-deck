using System.Net;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.TestSupport.Tests;

/// <summary>
/// 假上游 + 响应工厂 + 脚本队列的测试。
///
/// 这些测试同时干两件事：
///   ① 证明夹具本身可用（别人敢用）
///   ② 把「上游该有什么行为」的假设**钉在这里** —— gateway / e2e 两边
///      引用的就是同一份假设，不会再各造一遍
/// </summary>
public class FakeUpstreamHandlerTests
{
    private static UpstreamClient ClientFor(FakeUpstreamHandler fake)
        => new(fake.CreateClient(), UpstreamVariant.China, NullLogger<UpstreamClient>.Instance);

    // ── 请求记录 ──────────────────────────────────────────────

    [Fact]
    public async Task 记录方法_URL_请求头和请求体全文()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("""{"code":0}"""));
        var client = ClientFor(fake);

        var credential = new TestVault().Add().FirstCredential;
        await client.ChatStreamAsync(credential, """{"model":"hy3"}""", "WorkBuddy/5.6.2", "5.6.2", CancellationToken.None);

        var request = fake.Last;
        Assert.Equal("POST", request.Method);
        Assert.Equal("https://copilot.tencent.com/v2/chat/completions", request.Url);
        Assert.Equal("/v2/chat/completions", request.Path);
        Assert.Equal("Bearer access-token-1", request.Header("Authorization"));
        Assert.Equal("WorkBuddy", request.Header("X-IDE-Type"));
        Assert.Equal("""{"model":"hy3"}""", request.Body);
        Assert.Contains("application/json", request.ContentType);
        Assert.Equal(0, request.Sequence);
    }

    [Fact]
    public async Task 请求体可以被解析成_JSON_做断言()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("""{"code":0}"""));
        var client = ClientFor(fake);

        await client.ChatStreamAsync(new TestVault().Add().FirstCredential,
            """{"model":"hy3","stream":false,"messages":[{"role":"user","content":"你好"}]}""",
            null, null, CancellationToken.None);

        var body = fake.Last.Json();
        Assert.NotNull(body);
        Assert.Equal("hy3", body!["model"]!.GetValue<string>());
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task 请求体被单独解析时不会污染记录里的原文()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("""{"code":0}"""));
        var client = ClientFor(fake);

        await client.ChatStreamAsync(new TestVault().Add().FirstCredential, """{"model":"hy3"}""", null, null, CancellationToken.None);

        var first = fake.Last.Json();
        first!["model"] = "改过了";
        Assert.Equal("hy3", fake.Last.Json()!["model"]!.GetValue<string>());
    }

    [Fact]
    public void 没收到请求时读_Last_要抛而不是回_null()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        Assert.Throws<InvalidOperationException>(() => fake.Last);
        Assert.Equal(0, fake.Count);
    }

    // ── 路由 ──────────────────────────────────────────────────

    [Fact]
    public async Task 按方法加路径路由_未命中回_404()
    {
        var fake = FakeUpstreamHandler.Routing(
            UpstreamRoute.Chat(UpstreamResponses.SseText("""{"choices":[{"delta":{"content":"hi"}}]}""")),
            UpstreamRoute.Refresh(UpstreamResponses.RefreshOk("at-new")));

        using var http = fake.CreateClient();

        using var chat = await http.PostAsync(
            "https://copilot.tencent.com/v2/chat/completions",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        Assert.Contains("hi", await chat.Content.ReadAsStringAsync());

        using var missing = await http.PostAsync(
            "https://copilot.tencent.com/v2/does-not-exist",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task 同一个响应模板可以被多次使用()
    {
        // 这是最容易踩的坑：直接存一个 HttpResponseMessage 实例的话，
        // 第一次请求之后它就被 Dispose 了。
        var template = UpstreamResponses.RefreshOk("at-new");
        var fake = FakeUpstreamHandler.Returning(template);
        var client = ClientFor(fake);

        for (var i = 0; i < 3; i++)
        {
            var outcome = await client.RefreshTokenAsync(
                new TestVault().Add().FirstCredential, CancellationToken.None);
            Assert.Equal("at-new", outcome.AccessToken);
        }

        Assert.Equal(3, fake.Count);
    }

    // ── 响应工厂 ──────────────────────────────────────────────

    [Fact]
    public async Task _429_分类成账号级限流()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.TooManyRequests());
        var result = await ClientFor(fake).ChatStreamAsync(
            new TestVault().Add().FirstCredential, "{}", null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(429, result.Status);
        Assert.Equal(UpstreamErrorKind.RateLimited, result.Kind);
    }

    [Fact]
    public async Task _429_带_6004_分类成模型级限流()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.ModelRateLimited("hy3"));
        var result = await ClientFor(fake).ChatStreamAsync(
            new TestVault().Add().FirstCredential, "{}", null, null, CancellationToken.None);

        Assert.Equal(UpstreamErrorKind.ModelRateLimited, result.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task _401_和_403_分类成凭证失效(HttpStatusCode code)
    {
        var response = code == HttpStatusCode.Unauthorized
            ? UpstreamResponses.Unauthorized()
            : UpstreamResponses.Forbidden();

        var fake = new FakeUpstreamHandler(_ => response);
        var result = await ClientFor(fake).ChatStreamAsync(
            new TestVault().Add().FirstCredential, "{}", null, null, CancellationToken.None);

        Assert.Equal(UpstreamErrorKind.Unauthorized, result.Kind);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task _5xx_分类成服务端错误(int status)
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.ServerError("boom", status));
        var result = await ClientFor(fake).ChatStreamAsync(
            new TestVault().Add().FirstCredential, "{}", null, null, CancellationToken.None);

        Assert.Equal(UpstreamErrorKind.Server, result.Kind);
        Assert.Equal(status, result.Status);
    }

    [Fact]
    public async Task 超长错误正文被截断到_4096_字节以内()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.HugeErrorBody(100_000));
        var result = await ClientFor(fake).ChatStreamAsync(
            new TestVault().Add().FirstCredential, "{}", null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.NotNull(result.Message);
        Assert.True(result.Message!.Length <= 4096, $"正文长度 {result.Message.Length} 超过上限");
    }

    [Fact]
    public async Task 非法_JSON_正文不会让分类崩掉()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.InvalidJson());
        var result = await ClientFor(fake).ChatStreamAsync(
            new TestVault().Add().FirstCredential, "{}", null, null, CancellationToken.None);

        // 200 + 不是 JSON —— 这一层（分类）不该炸，炸是后面读正文的层的事
        Assert.True(result.Ok);
        Assert.Equal(200, result.Status);
        result.Response!.Dispose();
    }

    // ── SSE ───────────────────────────────────────────────────

    [Fact]
    public async Task SSE_工厂产出合法的_event_stream()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.SseText(
            """{"choices":[{"delta":{"content":"你"}}]}""",
            """{"choices":[{"delta":{"content":"好"}}]}"""));

        var result = await ClientFor(fake).ChatStreamAsync(
            new TestVault().Add().FirstCredential, "{}", null, null, CancellationToken.None);

        Assert.True(result.Ok);
        using var response = result.Response!;
        Assert.Contains("text/event-stream", response.Content.Headers.ContentType!.ToString());

        var text = await response.Content.ReadAsStringAsync();
        Assert.EndsWith("data: [DONE]\n\n", text);
        Assert.Contains("\"content\":\"你\"", text);
    }

    [Fact]
    public async Task SSE_的分块边界可以切在行中间()
    {
        // 真实网络上一次 read 拿到半行是常态 —— 夹具必须能造出这种形状
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.SseChunks(
            "data: {\"choices\":[{\"delta\":{\"content\":\"上\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":\"con",
            "tent\":\"下\"}}]}\n\n",
            "data: [DONE]\n\n"));

        var result = await ClientFor(fake).ChatStreamAsync(
            new TestVault().Add().FirstCredential, "{}", null, null, CancellationToken.None);

        using var response = result.Response!;
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(3, text.Split("data: ").Length - 1);
    }

    [Fact]
    public async Task SSE_带_DONE_和不带_DONE_的区别是明确的()
    {
        using var withDone = UpstreamResponses.SseText("""{"a":1}""");
        using var withoutDone = UpstreamResponses.SseBroken("""{"a":1}""");

        var doneText = await withDone.Content.ReadAsStringAsync();
        var brokenText = await withoutDone.Content.ReadAsStringAsync();

        Assert.Contains("[DONE]", doneText);
        Assert.DoesNotContain("[DONE]", brokenText);
    }

    [Fact]
    public async Task SSE_断在半行时_残缺的尾巴也留在正文里()
    {
        using var response = UpstreamResponses.SseAborted("""{"a":1}""", """{"b":""");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("data: {\"a\":1}", text);
        Assert.EndsWith("""data: {"b":""", text);
    }

    [Fact]
    public async Task SSE_支持_CRLF_行结束()
    {
        using var response = UpstreamResponses.SseText(["""{"a":1}"""], crlf: true);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("data: {\"a\":1}\r\n\r\n", text);
        Assert.Contains("data: [DONE]\r\n\r\n", text);
    }

    [Fact]
    public async Task 按每个事件分的块数可控()
    {
        using var response = UpstreamResponses.SseFrames(["""{"a":1}""", """{"b":2}"""], partsPerFrame: 2);
        var text = await response.Content.ReadAsStringAsync();

        // 两个事件 + 一个 DONE，每个切成 2 块 —— 块边界一定落在事件中间
        Assert.Contains("""{"a":1}""", text.Replace("\n", ""));
        Assert.Contains("""{"b":2}""", text.Replace("\n", ""));
        Assert.Contains("[DONE]", text);
    }

    [Fact]
    public async Task 目录和刷新两个信封工具体造对了()
    {
        using var catalog = UpstreamResponses.Catalog("hy3", "glm-5.3");
        var catalogJson = System.Text.Json.Nodes.JsonNode.Parse(await catalog.Content.ReadAsStringAsync())!;
        Assert.Equal(0, catalogJson["code"]!.GetValue<int>());
        Assert.Equal(2, catalogJson["data"]!["models"]!.AsArray().Count);

        using var refresh = UpstreamResponses.RefreshOk("at-new", refreshExpiresIn: 999);
        var refreshJson = System.Text.Json.Nodes.JsonNode.Parse(await refresh.Content.ReadAsStringAsync())!;
        Assert.Equal("at-new", refreshJson["data"]!["accessToken"]!.GetValue<string>());
        Assert.Equal(999, refreshJson["data"]!["refreshExpiresIn"]!.GetValue<int>());
    }
}
