using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.Gateway;      // ← ICredentialSource 在这里（文档 §6 有说明）
using BuddyDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.TestSupport.DocSamples;

/// <summary>
/// docs/test-support.md 里示例代码的**编译 + 运行验证**。
///
/// 这一组测试不测夹具内部（那是 <c>BuddyDeck.TestSupport.Tests</c> 的活），
/// 它只证明一件事：文档里贴出来的每一段，**逐字抄下来是能跑的**。
///
/// 为什么值得单独建一个项目：文档里的代码不会被编译器检查。
/// 夹具改名、签名调整、参数顺序变了 —— 行为测试照样绿，文档悄悄烂掉，
/// 而下游（t1 / t4）是照着文档写的，会一路踩到底。
/// </summary>
public class DocSamplesTests
{
    // ══════════════════════════════════════════════════════════
    // docs §0「三十秒上手」示例 ①
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 文档_0_示例一_光测假上游()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.TooManyRequests());
        var client = new UpstreamClient(fake.CreateClient(), UpstreamVariant.China, NullLogger<UpstreamClient>.Instance);
        var result = await client.ChatStreamAsync(new TestVault().Add().FirstCredential, "{}", null, null, CancellationToken.None);

        Assert.Equal(UpstreamErrorKind.RateLimited, result.Kind);
        Assert.Equal("/v2/chat/completions", fake.Last.Path);
    }

    // ══════════════════════════════════════════════════════════
    // docs §2 构造/记录/帮手 —— 每一个 API 名字都在这里出现一次
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 文档_2_FakeUpstreamHandler_的三种构造()
    {
        // ① 固定响应
        var a = new FakeUpstreamHandler(_ => UpstreamResponses.SseText("""{"choices":[{"delta":{"content":"hi"}}]}"""));
        // ② 由响应对象构造
        var b = FakeUpstreamHandler.Returning(UpstreamResponses.RefreshOk("at-new"));
        // ③ 路由
        var c = FakeUpstreamHandler.Routing(
            UpstreamRoute.Chat(UpstreamResponses.SseText("""{"a":1}""")),
            UpstreamRoute.Refresh(UpstreamResponses.RefreshOk("at-new")),
            UpstreamRoute.Catalog(UpstreamResponses.Catalog("hy3", "glm-5.3")));

        using (var http = a.CreateClient())
        using (var http2 = b.CreateClient())
        using (var http3 = c.CreateClient())
        {
            Assert.NotNull(await http.PostAsync("https://x/v2/chat/completions",
                new StringContent("{}", Encoding.UTF8, "application/json")));
            Assert.NotNull(await http2.PostAsync("https://x/v2/plugin/auth/token/refresh",
                new StringContent("{}", Encoding.UTF8, "application/json")));
            Assert.NotNull(await http3.PostAsync("https://x/v2/chat/completions",
                new StringContent("{}", Encoding.UTF8, "application/json")));
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task 文档_2_记录与断言帮手全都存在()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("""{"code":0}"""));
        var client = new UpstreamClient(fake.CreateClient(), UpstreamVariant.China, NullLogger<UpstreamClient>.Instance);

        await client.ChatStreamAsync(
            new TestVault().Add(uid: "u1").FirstCredential,
            """{"model":"hy3"}""", null, null, CancellationToken.None);

        // 文档 §2.2 里列的每一样
        Assert.Equal(1, fake.Count);
        Assert.Single(fake.Requests);
        Assert.Equal(["/v2/chat/completions"], fake.Paths);
        Assert.Equal(["hy3"], fake.RequestedModels);
        Assert.Equal(["Bearer access-token-1"], fake.AuthorizationHeaders);

        var r = fake.Last;
        Assert.Equal("POST", r.Method);
        Assert.Equal("https://copilot.tencent.com/v2/chat/completions", r.Url);
        Assert.Equal("/v2/chat/completions", r.Path);
        Assert.Equal("Bearer access-token-1", r.Header("Authorization"));
        Assert.Equal("Bearer access-token-1", r.HeaderOrNull("Authorization"));
        Assert.True(r.HasHeader("Authorization"));
        Assert.Equal("hy3", r.Json()!["model"]!.GetValue<string>());
        Assert.Contains("application/json", r.ContentType);
        Assert.Equal(0, r.Sequence);

        fake.Clear();
        Assert.Equal(0, fake.Count);
        fake.Reset();
        Assert.Equal(0, fake.Count);
    }

    // ══════════════════════════════════════════════════════════
    // docs §3 响应工厂 —— 清单里每一个工厂都在这里被调用一次
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 文档_3_1_正常类工厂全部可调用()
    {
        using var a = UpstreamResponses.Text("""{"code":0}""");
        using var b = UpstreamResponses.Text("<html>…</html>", HttpStatusCode.BadGateway, "text/html");
        using var c = UpstreamResponses.Json("""{"a":1}""");
        using var d = UpstreamResponses.Envelope(0, new JsonObject { ["x"] = 1 });
        using var e = UpstreamResponses.RefreshOk("at-new", refreshExpiresIn: 3600);
        using var f = UpstreamResponses.Catalog("hy3", "glm-5.3");

        foreach (var response in (HttpResponseMessage[])[a, b, c, d, e, f])
        {
            Assert.NotNull(await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task 文档_3_2_流式类工厂全部可调用()
    {
        using var one = UpstreamResponses.SseText(
            """{"choices":[{"delta":{"content":"你"}}]}""",
            """{"choices":[{"delta":{"content":"好"}}]}""");

        using var two = UpstreamResponses.SseText(["""{"a":1}"""], crlf: true);

        using var three = UpstreamResponses.SseChunks(
            "data: {\"choices\":[{\"delta\":{\"content\":\"上\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":\"con",
            "tent\":\"下\"}}]}\n\n",
            "data: [DONE]\n\n");

        using var four = UpstreamResponses.SseFrames(["""{"a":1}""", """{"b":2}"""], partsPerFrame: 2);
        using var five = UpstreamResponses.SseBroken("""{"a":1}""");
        using var six = UpstreamResponses.SseAborted("""{"a":1}""", """{"b":""");
        using var seven = UpstreamResponses.InvalidSsePayload("""{"a":1}""", """{"broken":""");

        foreach (var response in (HttpResponseMessage[])[one, two, three, four, five, six, seven])
        {
            Assert.NotNull(await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task 文档_3_3_到_3_4_限流_凭证_服务端_脏数据全部可调用()
    {
        using var a = UpstreamResponses.TooManyRequests("账号被限流", resetAtMs: 1793731164678);
        using var b = UpstreamResponses.ModelRateLimited("hy3");
        using var c = UpstreamResponses.Unauthorized();
        using var d = UpstreamResponses.Forbidden();
        using var e = UpstreamResponses.ServerError("boom", 500);
        using var f = UpstreamResponses.BadGateway();
        using var g = UpstreamResponses.ServiceUnavailable();
        using var h = UpstreamResponses.HugeErrorBody(100_000);
        using var i = UpstreamResponses.InvalidJson();
        using var j = UpstreamResponses.EmptyBody();

        // 文档说 ModelRateLimited 的正文里必须能找到 6004
        Assert.Contains("6004", await b.Content.ReadAsStringAsync());
        // 文档说 HugeErrorBody 能造出超过 4096 字节的正文
        Assert.True((await h.Content.ReadAsByteArrayAsync()).Length > 4096);

        foreach (var response in (HttpResponseMessage[])[a, c, d, e, f, g, i, j])
        {
            Assert.NotNull(await response.Content.ReadAsStringAsync());
        }
    }

    // ══════════════════════════════════════════════════════════
    // docs §4 UpstreamScript
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 文档_4_脚本队列与按请求分支()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.ServerError("兜底，不该走到"));

        fake.Script(
            UpstreamResponses.TooManyRequests(),
            UpstreamResponses.SseText("""{"choices":[{"delta":{"content":"ok"}}]}"""));

        var client = new UpstreamClient(fake.CreateClient(), UpstreamVariant.China, NullLogger<UpstreamClient>.Instance);
        var vault = new TestVault().Add().Add();

        var first = await client.ChatStreamAsync(vault.Entries[0].ToCredential(), "{}", null, null, CancellationToken.None);
        var second = await client.ChatStreamAsync(vault.Entries[1].ToCredential(), "{}", null, null, CancellationToken.None);

        Assert.Equal(UpstreamErrorKind.RateLimited, first.Kind);
        Assert.True(second.Ok);
        second.Response!.Dispose();

        // 按请求内容分支的那种写法
        // ⚠ 注意：新开一个假上游。脚本是按**请求序号**走的，
        //    复用上面那个 fake 的话序号已经过了第 1 步，就取不到分支那一步了。
        var branched = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        branched.Script(
            request => request.HeaderOrNull("Authorization") == "Bearer access-token-1"
                ? UpstreamResponses.ModelRateLimited("hy3")
                : UpstreamResponses.SseText("""{"a":1}"""));

        var branchedClient = new UpstreamClient(
            branched.CreateClient(), UpstreamVariant.China, NullLogger<UpstreamClient>.Instance);

        var b1 = await branchedClient.ChatStreamAsync(
            vault.Entries[0].ToCredential(), "{}", null, null, CancellationToken.None);
        var b2 = await branchedClient.ChatStreamAsync(
            vault.Entries[1].ToCredential(), "{}", null, null, CancellationToken.None);

        Assert.Equal(UpstreamErrorKind.ModelRateLimited, b1.Kind);
        Assert.True(b2.Ok);
        b2.Response!.Dispose();
    }

    [Fact]
    public async Task 文档_4_1_SwappingChatLoop_完整示例逐字可用()
    {
        var vault = new TestVault().Add(uid: "user-a").Add(uid: "user-b");

        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.ServerError("兜底"));
        fake.Script(
            UpstreamResponses.TooManyRequests(),
            UpstreamResponses.SseText("""{"choices":[{"delta":{"content":"ok"}}]}"""));

        var pool = new AccountPool(TimeProvider.System, NullLogger<AccountPool>.Instance);
        pool.Replace(vault.Entries);

        var attempts = await new SwappingChatLoop(fake, pool, UpstreamVariant.China).RunAsync("hy3");

        Assert.Equal(2, attempts.Count);
        Assert.Equal(UpstreamErrorKind.RateLimited, attempts[0].Kind);
        Assert.True(attempts[1].Ok);
        Assert.NotEqual(attempts[0].MemberId, attempts[1].MemberId);
        Assert.Equal("Bearer access-token-2", fake.AuthorizationHeaders[1]);

        // 文档 §4.1 表格里列的那三个参数名
        var pool2 = new AccountPool(TimeProvider.System, NullLogger<AccountPool>.Instance);
        pool2.Replace(new TestVault().Add().Entries);
        await new SwappingChatLoop(fake, pool2, UpstreamVariant.China)
            .RunAsync(model: "hy3", maxAttempts: 5, roundsPerMember: 1);
    }

    // ══════════════════════════════════════════════════════════
    // docs §5 TestVault
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 文档_5_凭证工厂的每个方法都可用()
    {
        var vault = new TestVault()
            .Add()
            .Add(uid: "u2", enterpriseId: "e2", domain: "corp.cn", label: "企业号")
            .Add(label: "快过期的", expiresAtMs: 0)
            .Expired("过期号")
            .NoExpiry("上游没给过期时间")
            .EmptyIdentity("空身份号")
            .AddMany(5, "pool");

        Assert.True(vault.Entries.Count >= 11);
        Assert.NotNull(vault.FirstCredential);
        Assert.NotNull(vault.CredentialOf(vault.Entries[0].Id));
        Assert.NotNull(vault.ToDocument());
        Assert.Contains("\"accounts\"", vault.ToVaultJson());

        var path = vault.WriteToTempFile();
        try
        {
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }

        await Task.CompletedTask;
    }

    [Fact]
    public async Task 文档_5_1_空身份账号示例逐字可用()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("""{"code":0}"""));
        var client = new UpstreamClient(fake.CreateClient(), UpstreamVariant.China, NullLogger<UpstreamClient>.Instance);

        await client.ChatStreamAsync(new TestVault().EmptyIdentity().FirstCredential, "{}", null, null, CancellationToken.None);

        Assert.Equal("1", fake.Last.Header("X-No-User-Id"));
        Assert.False(fake.Last.HasHeader("X-User-Id"));
    }

    [Fact]
    public async Task 文档_5_3_VaultJson_的每个方法都可用()
    {
        var dir = Path.Combine(Path.GetTempPath(), "buddydeck-docsample-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var bad = VaultJson.WriteTo(Path.Combine(dir, "bad.json"), VaultJson.AllNumericTimestamps());
            Assert.Throws<System.Text.Json.JsonException>(() => Vault.Read(bad));

            VaultJson.WriteTo(Path.Combine(dir, "blank.json"), VaultJson.Blank());
            Assert.Null(Vault.Read(Path.Combine(dir, "blank.json")));

            VaultJson.WriteTo(Path.Combine(dir, "trunc.json"), VaultJson.Truncated());
            Assert.Throws<System.Text.Json.JsonException>(() => Vault.Read(Path.Combine(dir, "trunc.json")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }

        await Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════
    // docs §6 FakeCredentialSource
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 文档_6_内存账号源与那段适配器()
    {
        var source = FakeCredentialSource.EmptyIdentity();
        source.Add(new Credential { AccessToken = "at-2", Uid = "u2" });
        source.SetAccounts(new TestVault().AddMany(3).Entries);
        Assert.Equal(3, source.Load().Count);
        source.Clear();
        Assert.Empty(source.Load());

        // 文档里给的那段适配器 —— 逐字抄下来能编译
        var vault = new TestVault().AddMany(2);
        ICredentialSource adapted = new Adapter(FakeCredentialSource.From(vault));
        Assert.Equal(2, adapted.Load().Count);

        await Task.CompletedTask;
    }

    /// <summary>文档 §6 里贴的那段适配器（逐字）。</summary>
    private sealed class Adapter : ICredentialSource
    {
        private readonly FakeCredentialSource _inner;

        public Adapter(FakeCredentialSource inner) => _inner = inner;

        public IReadOnlyList<VaultEntry> Load() => _inner.Load();
    }

    // ══════════════════════════════════════════════════════════
    // docs §7.2 / §8.2 GatewayHost
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 文档_7_2_起宿主并读_healthz_字段()
    {
        await using var host = await GatewayHost.StartReadyAsync(
            fake: new FakeUpstreamHandler(_ => UpstreamResponses.SseText("""{"a":1}""")),
            vault: new TestVault().Add(),
            apiKey: "test-key");

        Assert.StartsWith("http://127.0.0.1:", host.BaseAddress);
        Assert.True(host.Port > 0);
        Assert.NotNull(host.Client);
        Assert.NotNull(host.Application);
        Assert.NotNull(host.CapturedLog);
        Assert.NotNull(host.DumpLog());

        // 分两步那种写法
        await using var host2 = await GatewayHost.StartAsync(
            fake: new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}")),
            vault: new TestVault().Add());
        var health = await host2.WaitForHealthAsync(TimeSpan.FromSeconds(15));

        Assert.True(health["ok"]!.GetValue<bool>());
        Assert.True(health["buildId"]!.GetValue<string>().Length > 0);
    }

    [Fact]
    public async Task 文档_7_3_夹具自己钉住的端口约束()
    {
        await using var host = await GatewayHost.StartReadyAsync();

        Assert.NotEqual(8787, host.Port);
        Assert.NotEqual(3080, host.Port);
    }

    [Fact]
    public async Task 文档_8_2_端到端流式聊天示例逐字可用()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.SseText(
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{"role":"assistant","content":"你好"}}]}""",
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));

        await using var host = await GatewayHost.StartReadyAsync(
            fake: fake, vault: new TestVault().Add());

        using var request = GatewayHost.BearerRequest(HttpMethod.Post, "/v1/chat/completions");
        request.Content = new StringContent(
            """{"model":"hy3","stream":true,"messages":[{"role":"user","content":"你好"}]}""",
            Encoding.UTF8, "application/json");

        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/event-stream", response.Content.Headers.ContentType!.ToString());
        var text = await response.Content.ReadAsStringAsync();
        Assert.EndsWith("data: [DONE]\n\n", text);

        Assert.Equal("/v2/chat/completions", fake.Last.Path);
    }
}
