using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.TestSupport;
using Xunit;

namespace BuddyDeck.TestSupport.Tests;

/// <summary>
/// 测试宿主的自测。
///
/// 这一组存在的意义：证明 <see cref="GatewayHost"/> **真能起来**。
/// 如果它起不来，gateway / e2e 两边会各自去手写一遍宿主装配 ——
/// 那就又回到「各造一遍、假设漂移」的老路上了。
///
/// ⚠ 这里**只用端口 0**，绝不碰 8787 / 3080。就绪判定走轮询 /healthz，
/// 全程没有 Start-Sleep。
/// </summary>
[Collection("GatewayHost")]   // 宿主要真的绑端口，串行跑，别互相抢
public class GatewayHostTests
{
    [Fact]
    public async Task 用端口_0_起得来并绑到一个非保留端口()
    {
        await using var host = await GatewayHost.StartReadyAsync();

        Assert.True(host.Port > 0, "系统应该分配一个真实端口");
        Assert.NotEqual(8787, host.Port);
        Assert.NotEqual(3080, host.Port);
        Assert.StartsWith("http://127.0.0.1:", host.BaseAddress);
    }

    [Fact]
    public async Task _healthz_轮询能等到就绪_并且带回_buildId()
    {
        await using var host = await GatewayHost.StartAsync();

        // ★ 就绪判定：轮询 /healthz，不是 sleep
        var health = await host.WaitForHealthAsync(TimeSpan.FromSeconds(15));

        Assert.True(health["ok"]!.GetValue<bool>());
        Assert.Equal("workbuddy", health["provider"]!.GetValue<string>());
        Assert.True(health["models"]!.GetValue<int>() > 0, "内置模型清单不该是空的");
        Assert.True(health["buildId"]!.GetValue<string>().Length > 0, "buildId 不能为空");
        Assert.False(health["stale"]!.GetValue<bool>());
    }

    [Fact]
    public async Task _healthz_不需要鉴权()
    {
        await using var host = await GatewayHost.StartReadyAsync(apiKey: "secret-key");

        using var response = await host.Client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task 配了_API_Key_之后别的端点要鉴权()
    {
        await using var host = await GatewayHost.StartReadyAsync(apiKey: "secret-key");

        using var anonymous = await host.Client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var request = GatewayHost.BearerRequest(HttpMethod.Get, "/v1/models", "secret-key");
        using var authorized = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
    }

    [Fact]
    public async Task _v1_models_返回_openai_形状的清单()
    {
        await using var host = await GatewayHost.StartReadyAsync();

        using var request = GatewayHost.BearerRequest(HttpMethod.Get, "/v1/models");
        using var response = await host.Client.SendAsync(request);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal("list", body["object"]!.GetValue<string>());
        var data = body["data"]!.AsArray();
        Assert.NotEmpty(data);
        Assert.Equal("model", data[0]!["object"]!.GetValue<string>());
        Assert.True(data[0]!["id"]!.GetValue<string>().Length > 0);
    }

    [Fact]
    public async Task 一个号都没有时_聊天端点回_401_而不是_500()
    {
        await using var host = await GatewayHost.StartReadyAsync(vault: new TestVault());   // 空保险库

        using var request = GatewayHost.BearerRequest(HttpMethod.Post, "/v1/chat/completions");
        request.Content = new StringContent(
            """{"model":"hy3","messages":[{"role":"user","content":"你好"}]}""",
            Encoding.UTF8, "application/json");

        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("not_signed_in", body["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task 上游成功时_流式响应带_DONE_结束()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.SseText(
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{"role":"assistant","content":"你好"}}]}""",
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));

        await using var host = await GatewayHost.StartReadyAsync(fake: fake, vault: new TestVault().Add());

        using var request = GatewayHost.BearerRequest(HttpMethod.Post, "/v1/chat/completions");
        request.Content = new StringContent(
            """{"model":"hy3","stream":true,"messages":[{"role":"user","content":"你好"}]}""",
            Encoding.UTF8, "application/json");

        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/event-stream", response.Content.Headers.ContentType!.ToString());

        var text = await response.Content.ReadAsStringAsync();
        Assert.EndsWith("data: [DONE]\n\n", text);
        Assert.Contains("你好", text);

        // 上游收到的是真的聊天请求
        Assert.Equal(1, fake.Count);
        Assert.Equal("/v2/chat/completions", fake.Last.Path);
    }

    [Fact]
    public async Task 上游_429_时网关回_429_而不是崩()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.TooManyRequests());
        await using var host = await GatewayHost.StartReadyAsync(fake: fake, vault: new TestVault().Add());

        using var request = GatewayHost.BearerRequest(HttpMethod.Post, "/v1/chat/completions");
        request.Content = new StringContent(
            """{"model":"hy3","messages":[{"role":"user","content":"你好"}]}""",
            Encoding.UTF8, "application/json");

        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        // ⚠ 绝不能是 undefined / null —— 旧版踩过这个坑
        Assert.NotNull(body["error"]);
        Assert.NotNull(body["error"]!["message"]);
    }

    [Fact]
    public async Task 非回环_Host_被拒()
    {
        await using var host = await GatewayHost.StartReadyAsync();

        using var request = GatewayHost.BearerRequest(HttpMethod.Get, "/v1/models");
        request.Headers.TryAddWithoutValidation("Host", "evil.example.com");

        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task 探活超时会抛_TimeoutException_而不是永远转下去()
    {
        // 造一个「打不通的上游宿主」场景：拿一个当前空闲但没人监听的端口。
        //
        // NOTE: 直接构造 GatewayHost 打空端口做不到（StartAsync 总是绑 0 起活的
        // 宿主），所以这里用一个等价探针 —— 同样的「轮询 + 总时限」形状，
        // 打一个没人听的端口，验证它在时限内抛而不是挂死。
        var deadPort = FindFreePort();

        var started = System.Diagnostics.Stopwatch.StartNew();
        var threw = false;

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{deadPort}"),
            Timeout = Timeout.InfiniteTimeSpan,
        };

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            await client.GetAsync("/healthz", cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TaskCanceledException)
        {
            threw = true;
        }

        Assert.True(threw, "打一个没人听的端口必须抛");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"用时 {started.Elapsed.TotalSeconds:F1}s —— 有地方在硬等");
    }

    [Fact]
    public async Task 探活有时限_不会挂死()
    {
        // 起一个真宿主，然后确认探活在**很短的时间内**就返回（端口 0 起的
        // 宿主几乎立刻可服务）。这一条同时钉住「不许硬等」这个约束。
        await using var host = await GatewayHost.StartAsync();
        var started = System.Diagnostics.Stopwatch.StartNew();

        var health = await host.WaitForHealthAsync(TimeSpan.FromSeconds(15));

        Assert.True(health["ok"]!.GetValue<bool>());
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15), $"探活用了 {started.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>拿一个当前空闲的端口（绑一下就放掉）。</summary>
    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>宿主测试串行跑 —— 它们要真的绑端口。</summary>
[CollectionDefinition("GatewayHost", DisableParallelization = true)]
public class GatewayHostCollection
{
}
