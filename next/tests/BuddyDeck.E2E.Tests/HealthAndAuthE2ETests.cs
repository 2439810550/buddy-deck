using System.Net;
using System.Text.Json.Nodes;
using BuddyDeck.TestSupport;
using Xunit;

namespace BuddyDeck.E2E.Tests;

/// <summary>
/// 健康检查与鉴权 —— 网关的**信任边界**。
///
/// 分三块：
/// 1. <c>/healthz</c>：免鉴权、带代码指纹、字段稳定（面板靠它判断
///    「端口上那个网关是不是最新代码」/「进程还活着吗」）
/// 2. 鉴权：缺 key / 错 key → 401，且错误形状统一
/// 3. <c>/v1/models</c>：客户端启动第一件调的事，必须可用
/// </summary>
[Collection("GatewayHost")]
public class HealthAndAuthE2ETests
{
    private const string SecretKey = "sk-workbuddy-e2e";

    private static string ChatBody(string model = "hy3")
        => $$"""{"model":"{{model}}","messages":[{"role":"user","content":"你好"}]}""";

    // ── /healthz 的字段契约 ───────────────────────────────────

    [Fact]
    public async Task healthz_返回全部约定字段且类型正确()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"));

        var body = await E2EHost.JsonAsync(await e2e.GetAsync("/healthz"));

        Assert.True(body["ok"]!.GetValue<bool>());
        Assert.Equal("workbuddy", body["provider"]!.GetValue<string>());
        Assert.Equal(17, body["models"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(body["buildId"]!.GetValue<string>()));

        var startedAt = body["startedAt"]!.GetValue<string>();
        Assert.EndsWith("Z", startedAt, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.TryParse(startedAt, out _), $"startedAt 不是合法 ISO：{startedAt}");

        // stale 字段必须存在（哪怕值是 false）—— 面板靠它区分「代码生效没有」
        Assert.NotNull(body["stale"]);
        Assert.Equal(System.Text.Json.JsonValueKind.False, body["stale"]!.GetValueKind());
    }

    [Fact]
    public async Task healthz_buildId_是12位小写hex且同一进程内多次请求一致()
    {
        // ★ 这条是 buildId 的**全部意义**所在：它代表「这个进程启动时加载的代码」。
        // 每次请求都变的话，它就回答不了「端口上跑的是不是最新代码」。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"));

        var seen = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var body = await E2EHost.JsonAsync(await e2e.GetAsync("/healthz"));
            seen.Add(body["buildId"]!.GetValue<string>());
        }

        Assert.Single(seen.Distinct(StringComparer.Ordinal));

        var buildId = seen[0];
        Assert.Equal(12, buildId.Length);
        Assert.Matches("^[0-9a-f]{12}$", buildId);
    }

    [Fact]
    public async Task healthz_startedAt_不变_且晚于或等于请求之前()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"));

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var first = await E2EHost.JsonAsync(await e2e.GetAsync("/healthz"));
        // 中间穿插一次真实请求 —— 证明 startedAt 是「进程启动时刻」而不是「上次请求时刻」
        using (var chat = await e2e.PostChatAsync(ChatBody()))
        {
            Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        }
        var second = await E2EHost.JsonAsync(await e2e.GetAsync("/healthz"));

        var t1 = first["startedAt"]!.GetValue<string>();
        var t2 = second["startedAt"]!.GetValue<string>();

        Assert.Equal(t1, t2);
        Assert.True(DateTimeOffset.Parse(t1, System.Globalization.CultureInfo.InvariantCulture) >= before,
            "startedAt 不该早于进程启动");
    }

    [Fact]
    public async Task healthz_免鉴权_即使配了_API_Key()
    {
        // 探活时手上没有 key（也不需要）。卡鉴权会让面板探活全红。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"), apiKey: SecretKey);

        using var response = await e2e.GetAsync("/healthz");   // 不带 Authorization

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await E2EHost.JsonAsync(response);
        Assert.True(body["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task healthz_不做_Host_检查()
    {
        // ⚠ 事实快照，不是不可动摇的契约 —— 这是 **t1 的一条自主决策**的镜像，
        // 不是「本机信任边界要求 /healthz 免检」。
        //
        // t1 当时的理由：容器/健康检查探针的 Host 头不一定是回环，
        // 把探活卡死比放行一个 GET 健康检查的代价大得多。
        //
        // 也就是说：这条断言记录的是「团队选择了探活可用性优先」这个取舍，
        // 而不是从协议/安全里推出来的必然结论。**如果将来有人决定让 /healthz
        // 也受 Host 检查约束**（理由同样成立：探针的 Host 是可以配的），
        // 那么要改的是这个决策，本条应当同步翻转成「非回环 Host → 403」，
        // 而不是把它当成「实现错了」来报 bug。
        //
        // 对照组：下面 `非回环_Host_头被拒为403_host_not_allowed` 是**硬要求**
        // （DNS rebinding / 远程调用者，属于本机信任边界），那条不能动。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"));

        using var response = await e2e.GetAsync("/healthz", host: "probe.internal");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── 鉴权 ──────────────────────────────────────────────────

    [Fact]
    public async Task 鉴权_缺_key返回401()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"), apiKey: SecretKey);

        using var response = await e2e.PostChatAsync(ChatBody());   // 不带 Authorization

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await E2EHost.JsonAsync(response);
        Assert.Equal("invalid_api_key", body["error"]!["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(body["error"]!["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task 鉴权_错_key返回401()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"), apiKey: SecretKey);

        using var response = await e2e.PostChatAsync(ChatBody(), apiKey: "sk-wrong-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await E2EHost.JsonAsync(response);
        Assert.Equal("invalid_api_key", body["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task 鉴权_前缀正确但长度不同的_key也返回401()
    {
        // 定时安全比较里「长度不同直接 false」那条路径 ——
        // 以及确认比较没有退化成 StartsWith。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"), apiKey: SecretKey);

        using var shorter = await e2e.PostChatAsync(ChatBody(), apiKey: "sk-workbuddy");
        Assert.Equal(HttpStatusCode.Unauthorized, shorter.StatusCode);

        using var longer = await e2e.PostChatAsync(ChatBody(), apiKey: SecretKey + "x");
        Assert.Equal(HttpStatusCode.Unauthorized, longer.StatusCode);
    }

    [Fact]
    public async Task 鉴权_非_Bearer_前缀也返回401()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"), apiKey: SecretKey);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(ChatBody(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", SecretKey);   // 少了 "Bearer "

        using var response = await e2e.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 鉴权_正确的_key放行且_Bearer_大小写不敏感()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("放行"), apiKey: SecretKey);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(ChatBody(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"bearer {SecretKey}");

        using var response = await e2e.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task 鉴权_未配置_key时不需要鉴权()
    {
        // 和旧版语义一致：key 为空 = 不鉴权。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("开放"));

        using var response = await e2e.PostChatAsync(ChatBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task 鉴权_401_响应带_WWWAuthenticate_提示()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"), apiKey: SecretKey);

        using var response = await e2e.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("Bearer", challenge, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 鉴权_受保护端点在缺_key时一律401_包括未知路由()
    {
        // 顺序契约：/healthz → Host 检查 → 鉴权 → 路由。
        // 所以未知路由在**没带 key** 时应该是 401 而不是 404 ——
        // 不然等于把「有哪些路由」白送给未鉴权的调用者。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"), apiKey: SecretKey);

        using var missing = await e2e.GetAsync("/v1/does-not-exist");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var withKey = await e2e.GetAsync("/v1/does-not-exist", apiKey: SecretKey);
        Assert.Equal(HttpStatusCode.NotFound, withKey.StatusCode);
    }

    // ── /v1/models ────────────────────────────────────────────

    [Fact]
    public async Task models_返回_openai_list_形状且非空()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"));

        var body = await E2EHost.JsonAsync(await e2e.GetAsync("/v1/models"));

        Assert.Equal("list", body["object"]!.GetValue<string>());

        var data = body["data"]!.AsArray();
        Assert.NotEmpty(data);
        Assert.Equal(17, data.Count);

        foreach (var entry in data)
        {
            Assert.Equal("model", entry!["object"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(entry["id"]!.GetValue<string>()));
            Assert.Equal("workbuddy", entry["owned_by"]!.GetValue<string>());
        }

        // 内置清单里的关键模型必须在
        var ids = data.Select(e => e!["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("hy3", ids);
        Assert.Contains("glm-5.3", ids);
    }

    [Fact]
    public async Task models_上游挂掉时仍然返回内置清单()
    {
        // /v1/models 是客户端启动时第一件调的事。拉不到目录就打挂端点的话，
        // 用户看到的是空列表 —— 明明有 17 个内置模型可用。
        var fake = new FakeUpstreamHandler(_ => throw new HttpRequestException("上游不通"));
        await using var e2e = await E2EHost.StartAsync(fake);

        using var response = await e2e.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var data = (await E2EHost.JsonAsync(response))["data"]!.AsArray();
        Assert.NotEmpty(data);
    }

    [Fact]
    public async Task models_有机会时用上游刷回来的目录()
    {
        // 上游给了目录就用上游的（内置清单只是兜底）。
        var fake = FakeUpstreamHandler.Routing(
            UpstreamRoute.Catalog(UpstreamResponses.Catalog("hy3", "self-made-model")),
            UpstreamRoute.Chat(StreamScript.TextStream("hi")));

        await using var e2e = await E2EHost.StartAsync(fake);

        var body = await E2EHost.JsonAsync(await e2e.GetAsync("/v1/models"));
        var ids = body["data"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()).ToList();

        Assert.Contains("self-made-model", ids);
    }

    [Fact]
    public async Task models_尾部斜杠等价()
    {
        // 旧版认两种写法 —— 客户端里两种都出现过。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"));

        using var withSlash = await e2e.GetAsync("/v1/models/");

        Assert.Equal(HttpStatusCode.OK, withSlash.StatusCode);
        Assert.Equal("list", (await E2EHost.JsonAsync(withSlash))["object"]!.GetValue<string>());
    }
}
