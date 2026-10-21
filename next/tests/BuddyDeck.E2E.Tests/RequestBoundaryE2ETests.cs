using System.Net;
using System.Text.Json.Nodes;
using BuddyDeck.TestSupport;
using Xunit;

namespace BuddyDeck.E2E.Tests;

/// <summary>
/// 请求边界与错误路径 —— 网关对**坏输入**的反应。
///
/// 这一组的目标是「不崩、不说谎」：
/// · 每个拒绝都给一个明确的状态码 + 一个 OpenAI 形状的错误对象
/// · 绝不返回 500（那是「我们坏了」的意思，而这些都是「你发错了」）
/// · 拒绝之后网关依然可用
/// </summary>
[Collection("GatewayHost")]
public class RequestBoundaryE2ETests
{
    private const string ChatPath = "/v1/chat/completions";

    private static string ChatBody(string model = "hy3")
        => $$"""{"model":"{{model}}","messages":[{"role":"user","content":"你好"}]}""";

    // ── Host 头（本机信任边界）────────────────────────────────

    [Theory]
    [InlineData("evil.example.com")]
    [InlineData("evil.example.com:1234")]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.5:8787")]
    [InlineData("workbuddy.cn")]
    public async Task 非回环_Host_头被拒为403_host_not_allowed(string host)
    {
        // DNS rebinding / 远程调用者：光靠 bearer 不够，Host 头是本机信任边界。
        // 注意这条在**鉴权之前** —— 所以这里不带 key 也应该是 403 而不是 401。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("不该走到"));

        using var response = await e2e.PostChatAsync(ChatBody(), host: host);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await E2EHost.JsonAsync(response);
        Assert.Equal("host_not_allowed", body["error"]!["type"]!.GetValue<string>());
        Assert.Equal("host_not_allowed", body["error"]!["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(body["error"]!["message"]!.GetValue<string>()));

        // 而且压根没打上游
        Assert.Equal(0, e2e.Fake.Count);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    public async Task 回环_Host_头一律放行(string host)
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("放行"));

        using var response = await e2e.PostChatAsync(ChatBody(), host: host);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task 非回环_Host_头对_models_也生效()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"));

        using var response = await e2e.GetAsync("/v1/models", host: "attacker.test");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("host_not_allowed",
            (await E2EHost.JsonAsync(response))["error"]!["type"]!.GetValue<string>());
    }

    // ── 404 ───────────────────────────────────────────────────

    [Theory]
    [InlineData("/")]
    [InlineData("/v1")]
    [InlineData("/v1/nonexistent")]
    [InlineData("/health")]
    [InlineData("/v1/chat")]
    [InlineData("/v3/config")]
    public async Task 未知路由返回404且带错误对象(string path)
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"));

        using var response = await e2e.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await E2EHost.JsonAsync(response);
        Assert.Equal("not_found", body["error"]!["type"]!.GetValue<string>());
        Assert.Contains(path, body["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 方法不对也返回404而不是500()
    {
        // PUT /v1/chat/completions 没有路由 —— 回 404 而不是 405 是刻意的
        // （错误形状统一在 WriteErrorAsync 一条路径上）。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"));

        using var put = await e2e.SendAsync(HttpMethod.Put, ChatPath, ChatBody());
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

        // GET /v1/chat/completions 同理
        using var get = await e2e.GetAsync(ChatPath);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        // 而 POST /v1/models 也不该被当成合法路径
        using var postModels = await e2e.SendAsync(HttpMethod.Post, "/v1/models", "{}");
        Assert.Equal(HttpStatusCode.NotFound, postModels.StatusCode);
    }

    // ── 400（畸形 JSON）───────────────────────────────────────

    [Theory]
    [InlineData("{这不是 JSON")]
    [InlineData("{\"model\":\"hy3\",}")]
    [InlineData("not json at all")]
    [InlineData("")]
    public async Task 畸形_JSON_返回400(string body)
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("不该走到"));

        using var response = await e2e.PostChatAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await E2EHost.JsonAsync(response);
        Assert.Equal("invalid_request_error", json["error"]!["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(json["error"]!["message"]!.GetValue<string>()));

        Assert.Equal(0, e2e.Fake.Count);   // 坏请求绝不转发上游
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"一个字符串\"")]
    [InlineData("123")]
    [InlineData("null")]
    public async Task 顶层不是对象也返回400(string body)
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("不该走到"));

        using var response = await e2e.PostChatAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, e2e.Fake.Count);
    }

    // ── 415（Content-Type）───────────────────────────────────

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("text/xml")]
    public async Task 错误的_ContentType_返回415(string contentType)
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("不该走到"));

        using var response = await e2e.PostChatAsync(ChatBody(), contentType: contentType);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        var body = await E2EHost.JsonAsync(response);
        Assert.Equal("unsupported_media_type", body["error"]!["type"]!.GetValue<string>());
        Assert.Equal(0, e2e.Fake.Count);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("APPLICATION/JSON")]
    public async Task 合法的_ContentType_变体被接受(string contentType)
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("接受"));

        using var response = await e2e.PostChatAsync(ChatBody(), contentType: contentType);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── 请求归一化（管线真的跑过）────────────────────────────

    [Fact]
    public async Task 归一化_developer_角色被改成_system_且请求仍成功()
    {
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("规矩收到"));
        await using var e2e = await E2EHost.StartAsync(fake);

        using var response = await e2e.PostChatAsync(
            """{"model":"hy3","stream":false,"messages":[{"role":"developer","content":"规矩"},{"role":"user","content":"你好"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 上游收到的 body 里 developer 必须已经变成 system
        var sent = Assert.Single(fake.Requests);
        var messages = sent.Json()!["messages"]!.AsArray();
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("规矩", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());

        // 而且没有漏出 developer
        Assert.DoesNotContain("\"developer\"", sent.Body!, StringComparison.Ordinal);

        // 客户端拿到的是正常回答
        var body = await E2EHost.JsonAsync(response);
        Assert.Equal("规矩收到", body["choices"]!.AsArray()[0]!["message"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task 归一化_对象形态的_tool_choice_被扁平化且不返回400()
    {
        // OpenAI 的对象形态 {"type":"function","function":{"name":"x"}} 上游不认。
        // 网关要把它压成字符串 —— 不压的话上游会拒，表现为用户看到一个莫名其妙的错误。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好"));
        await using var e2e = await E2EHost.StartAsync(fake);

        var requestBody = """
        {
          "model": "hy3",
          "messages": [{"role":"user","content":"北京天气"}],
          "tool_choice": {"type":"function","function":{"name":"get_weather"}},
          "tools": [{"type":"function","function":{"name":"get_weather","parameters":{"type":"object"}}}]
        }
        """;

        using var response = await e2e.PostChatAsync(requestBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sent = Assert.Single(fake.Requests);
        var toolChoice = sent.Json()!["tool_choice"];
        Assert.Equal(System.Text.Json.JsonValueKind.String, toolChoice!.GetValueKind());
        Assert.Equal("get_weather", toolChoice.GetValue<string>());
    }

    [Fact]
    public async Task 归一化_tool_choice_none_时_tools_被一起删掉()
    {
        // tools + none 一起发的话上游会困惑。管线的做法是两者都删。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好"));
        await using var e2e = await E2EHost.StartAsync(fake);

        var requestBody = """
        {
          "model": "hy3",
          "messages": [{"role":"user","content":"问候语"}],
          "tool_choice": "none",
          "tools": [{"type":"function","function":{"name":"get_weather"}}]
        }
        """;

        using var response = await e2e.PostChatAsync(requestBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sent = Assert.Single(fake.Requests);
        var json = sent.Json()!;
        Assert.False(json.AsObject().ContainsKey("tool_choice"));
        Assert.False(json.AsObject().ContainsKey("tools"));
    }

    [Fact]
    public async Task 归一化_客户端的扩展字段不被丢掉()
    {
        // 各家客户端都有自己的扩展字段，上游可能就是认它们。
        // 反序列化成强类型模型会把这些丢掉 —— 所以管线用可变 DOM 改。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好"));
        await using var e2e = await E2EHost.StartAsync(fake);

        await e2e.PostChatAsync("""
        {"model":"hy3","messages":[{"role":"user","content":"嗨"}],
         "temperature":0.3,"top_p":0.9,"my_client_tag":"vscode-1.2","stop":["\n\n"]}
        """);

        var json = Assert.Single(fake.Requests).Json()!;
        Assert.Equal(0.3, json["temperature"]!.GetValue<double>(), 3);
        Assert.Equal("vscode-1.2", json["my_client_tag"]!.GetValue<string>());
        Assert.True(json["stream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 归一化_每次请求都强制_stream_true_哪怕客户端要非流式()
    {
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好"));
        await using var e2e = await E2EHost.StartAsync(fake);

        using (await e2e.PostChatAsync("""{"model":"hy3","stream":false,"messages":[{"role":"user","content":"a"}]}""")) { }
        using (await e2e.PostChatAsync("""{"model":"hy3","messages":[{"role":"user","content":"b"}]}""")) { }

        Assert.Equal(2, fake.Count);
        foreach (var sent in fake.Requests)
        {
            Assert.True(sent.Json()!["stream"]!.GetValue<bool>(),
                "上游只支持流式 —— 转发时必须强制打开");
        }
    }

    // ── 身份头（本机信任边界的另一半）─────────────────────────

    [Fact]
    public async Task 上游请求带齐身份头与归属头()
    {
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("hi"));
        await using var e2e = await E2EHost.StartAsync(fake, new TestVault().Add(uid: "user-77", enterpriseId: "ent-9", domain: "corp.example"));

        await e2e.PostChatAsync(ChatBody());

        var sent = Assert.Single(fake.Requests);
        Assert.Equal("/v2/chat/completions", sent.Path);
        Assert.Equal("Bearer access-token-1", sent.Header("Authorization"));
        Assert.Equal("user-77", sent.Header("X-User-Id"));
        Assert.Equal("ent-9", sent.Header("X-Enterprise-Id"));
        Assert.Equal("corp.example", sent.Header("X-Domain"));

        // X-IDE-* 归属族只属于 chat —— 上游按它做用量归属
        Assert.Equal("WorkBuddy", sent.Header("X-IDE-Type"));
        Assert.Equal("WorkBuddy", sent.Header("X-IDE-Name"));
        Assert.Equal("SaaS", sent.Header("X-Product"));

        // 有值时不发「我没有」的标记
        Assert.False(sent.HasHeader("X-No-User-Id"));
        Assert.False(sent.HasHeader("X-No-Enterprise-Id"));
    }

    [Fact]
    public async Task 上游请求对空身份账号发显式的_X_No_族()
    {
        // 「缺 X-User-Id」和「给 X-No-User-Id: 1」是两回事。后者才是正确的
        // 「我没有这个字段」—— 省略会被上游当成异常请求（表现是偶发 403）。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("hi"));
        await using var e2e = await E2EHost.StartAsync(fake, new TestVault().EmptyIdentity());

        await e2e.PostChatAsync(ChatBody());

        var sent = Assert.Single(fake.Requests);
        Assert.Equal("1", sent.Header("X-No-User-Id"));
        Assert.Equal("1", sent.Header("X-No-Enterprise-Id"));
        Assert.Equal("1", sent.Header("X-No-Department-Info"));
        Assert.False(sent.HasHeader("X-User-Id"));
        Assert.False(sent.HasHeader("X-Enterprise-Id"));
        Assert.False(sent.HasHeader("X-Domain"));
    }

    // ── 大请求体 ──────────────────────────────────────────────

    [Fact]
    public async Task 超过上限的请求体返回413且不把网关撑爆()
    {
        // 上限存在的理由：网关要支持图片 data URI（旧版 64MB），
        // 但没上限的话一个恶意请求就能把内存吃光。
        // 这里用一个**很小**的上限把边界压出来 —— 不真发 64MB。
        await using var small = await E2EHost.StartWithBodyLimitAsync(1024);

        var under = "{\"model\":\"hy3\",\"messages\":[{\"role\":\"user\",\"content\":\"" + new string('x', 100) + "\"}]}";
        var over = "{\"model\":\"hy3\",\"messages\":[{\"role\":\"user\",\"content\":\"" + new string('x', 4096) + "\"}]}";

        // 上限之内：正常
        using (var ok = await small.PostChatAsync(under))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        // 超上限：413 + 错误对象，不进上游
        var before = small.Fake.Count;
        using var response = await small.PostChatAsync(over);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        var body = await E2EHost.JsonAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(body["error"]!["message"]!.GetValue<string>()));
        Assert.Equal(before, small.Fake.Count);
    }

    // ── 超长错误正文 ──────────────────────────────────────────

    [Fact]
    public async Task 上游吐超长错误正文时不把整坨透给客户端()
    {
        // 上游出错时可能吐一大坨 HTML。全透给客户端既没用又难看。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.HugeErrorBody(200_000, "上游错误页"));
        await using var e2e = await E2EHost.StartAsync(fake);

        using var response = await e2e.PostChatAsync(ChatBody());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var message = (await E2EHost.JsonAsync(response))["error"]!["message"]!.GetValue<string>();
        Assert.True(message.Length < 2000, $"错误消息被截断得太少：{message.Length} 字符");
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public async Task 上游返回非_JSON_的错误正文时也能给出可读消息()
    {
        // 上游出错时常常吐 HTML（网关页面 / WAF 拦截页），不是 JSON。
        // 分类只能靠状态码，但**给用户的消息不能是空的**。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text(
            "<html><body><h1>502 Bad Gateway</h1></body></html>",
            HttpStatusCode.BadGateway,
            "text/html"));
        await using var e2e = await E2EHost.StartAsync(fake);

        using var response = await e2e.PostChatAsync(ChatBody());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var message = (await E2EHost.JsonAsync(response))["error"]!["message"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public async Task 上游拿_200_但正文完全没有_SSE_帧时不崩()
    {
        // 上游抽风回一个 200 + 一堆 HTML（代理插页、WAF 拦截页都是这个形态）。
        // 网关不能崩 —— 最坏情况就是给一个空回复（确实没有内容可取）。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.InvalidJson());
        await using var e2e = await E2EHost.StartAsync(fake);

        using var response = await e2e.PostChatAsync(ChatBody());

        Assert.True((int)response.StatusCode < 500,
            $"上游回了 200 但正文不可解析时，网关不该报 5xx（实际 {(int)response.StatusCode}）");

        // 而且网关还活着
        using var health = await e2e.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    // ── 一个坏请求不该污染后续请求 ────────────────────────────

    [Fact]
    public async Task 依次发一串坏请求之后网关依然能正常服务()
    {
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("我还活着"));
        await using var e2e = await E2EHost.StartAsync(fake);

        using (await e2e.PostChatAsync("{坏了")) { }
        using (await e2e.PostChatAsync("[]")) { }
        using (await e2e.PostChatAsync(ChatBody(), contentType: "text/plain")) { }
        using (await e2e.GetAsync("/nope")) { }
        using (await e2e.PostChatAsync(ChatBody(), host: "evil.test")) { }

        using var good = await e2e.PostChatAsync(ChatBody());
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);

        var body = await E2EHost.JsonAsync(good);
        Assert.Equal("我还活着", body["choices"]!.AsArray()[0]!["message"]!["content"]!.GetValue<string>());

        // 只有最后那个好请求打到了上游
        Assert.Equal(1, fake.Count);
    }

    [Fact]
    public async Task 宿主绑的不是保留端口()
    {
        // 8787 是用户正在跑的网关（也是 DSH 自己的模型来源），
        // 3080 是 DSH Web。测试宿主一律用系统分配的端口。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("hi"));

        Assert.True(e2e.Port > 0);
        Assert.False(e2e.IsReservedPort, $"测试宿主占了保留端口 {e2e.Port}");
    }
}
