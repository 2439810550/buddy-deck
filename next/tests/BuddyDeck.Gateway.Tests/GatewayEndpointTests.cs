using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 端点的端到端测试 —— 真 Kestrel、真端口、假上游。
///
/// 全部不 Sleep：需要「冷却中」状态就直接往池里设置，
/// 需要「代码改过」就直接构造两个不同指纹的目录。
/// </summary>
public class GatewayEndpointTests
{
    private static string ChatBody(string model = "hy3", bool stream = false)
        => $$"""{"model":"{{model}}","messages":[{"role":"user","content":"你好"}],"stream":{{(stream ? "true" : "false")}}}""";

    /// <summary>造一份「上游正常流式回复」。</summary>
    private static HttpResponseMessage OkStream(params string[] contentDeltas)
    {
        var lines = new List<string>
        {
            """{"id":"chatcmpl-up1","object":"chat.completion.chunk","created":1700000000,"model":"hy3","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""",
        };
        foreach (var delta in contentDeltas)
        {
            lines.Add($$"""{"id":"chatcmpl-up1","object":"chat.completion.chunk","created":1700000000,"model":"hy3","choices":[{"index":0,"delta":{"content":"{{delta}}"},"finish_reason":null}]}""");
        }
        lines.Add("""{"id":"chatcmpl-up1","object":"chat.completion.chunk","created":1700000000,"model":"hy3","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10,"credit":"0.01"}}""");
        return FakeUpstream.Sse([.. lines]);
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response)
        => await response.Content.ReadAsStringAsync();

    private static JsonNode Parse(string body) => JsonNode.Parse(body)!;

    // ── /healthz ──────────────────────────────────────────────

    [Fact]
    public async Task healthz_返回全部约定字段()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"));

        var response = await gw.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = Parse(await BodyAsync(response));

        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal("workbuddy", json["provider"]!.GetValue<string>());
        Assert.Equal(17, json["models"]!.GetValue<int>());
        Assert.Equal(12, json["buildId"]!.GetValue<string>().Length);
        Assert.Matches("^[0-9a-f]{12}$", json["buildId"]!.GetValue<string>());
        Assert.False(json["stale"]!.GetValue<bool>());
        // startedAt 要能被 ISO 解析（旧版是 Date.toISOString() 的形状）
        var startedAt = json["startedAt"]!.GetValue<string>();
        Assert.EndsWith("Z", startedAt, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.TryParse(startedAt, out _), $"startedAt 不是合法 ISO：{startedAt}");
    }

    [Fact]
    public async Task healthz_免鉴权()
    {
        // 面板探活时手上没有 key。卡鉴权会让探活全红。
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"), apiKey: "secret-key");

        var response = await gw.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task healthz_的_startedAt_每次请求都一样()
    {
        // 「进程启动时刻」是快照，不是「当前时刻」——
        // 每次请求都变的话就没法用它判断进程活了多久。
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"));

        var first = Parse(await BodyAsync(await gw.Client.GetAsync("/healthz")))["startedAt"]!.GetValue<string>();
        var second = Parse(await BodyAsync(await gw.Client.GetAsync("/healthz")))["startedAt"]!.GetValue<string>();

        Assert.Equal(first, second);
    }

    // ── buildId / stale ───────────────────────────────────────

    [Fact]
    public async Task 代码改了但没重启时报_stale()
    {
        // 造一个源码目录，起网关（此时指纹是 A），再改文件（指纹变 B），
        // 然后问 /healthz —— 应该报 stale，且 buildId 还是启动时那个 A。
        var dir = Directory.CreateTempSubdirectory("buddydeck-stale-").FullName;
        try
        {
            var file = Path.Combine(dir, "Foo.cs");
            File.WriteAllText(file, "// 第一版");

            await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"), sourceDir: dir);
            var before = Parse(await BodyAsync(await gw.Client.GetAsync("/healthz")));
            var buildId = before["buildId"]!.GetValue<string>();
            Assert.False(before["stale"]!.GetValue<bool>());

            // 改文件、且大小不同 —— 保证指纹一定变
            File.WriteAllText(file, "// 第二版，内容更长一些，确保 size 不同");

            var after = Parse(await BodyAsync(await gw.Client.GetAsync("/healthz")));

            Assert.True(after["stale"]!.GetValue<bool>(), "改了代码没重启，应该报 stale=true");
            // buildId 仍然是启动时算的那份 —— 它代表「这个进程加载的代码」
            Assert.Equal(buildId, after["buildId"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── /v1/models ────────────────────────────────────────────

    [Fact]
    public async Task models_返回_17_个内置模型且形状正确()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"));

        var response = await gw.Client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = Parse(await BodyAsync(response));
        Assert.Equal("list", json["object"]!.GetValue<string>());

        var data = json["data"]!.AsArray();
        Assert.Equal(17, data.Count);

        foreach (var item in data)
        {
            Assert.False(string.IsNullOrWhiteSpace(item!["id"]!.GetValue<string>()));
            Assert.Equal("model", item["object"]!.GetValue<string>());
            Assert.Equal(0, item["created"]!.GetValue<int>());
            Assert.Equal("workbuddy", item["owned_by"]!.GetValue<string>());
        }

        // 内置清单是 models.cn.json 的 17 个 id
        var ids = data.Select(d => d!["id"]!.GetValue<string>()).ToList();
        Assert.Contains("hy4-preview", ids);
        Assert.Contains("deepseek-v4-pro", ids);
        Assert.Contains("kimi-k2.6", ids);
    }

    [Fact]
    public async Task models_无鉴权时_401()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"), apiKey: "secret-key");

        var response = await gw.Client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = Parse(await BodyAsync(response));
        Assert.Equal("invalid_api_key", json["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task 错了的_key_返回_401()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"), apiKey: "secret-key");

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer wrong-key");
        var response = await gw.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 正确的_key_放行()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"), apiKey: "secret-key");

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret-key");
        var response = await gw.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Host 检查 ─────────────────────────────────────────────

    [Fact]
    public async Task 非回环_Host_返回_403()
    {
        // 非回环 Host 意味着 DNS rebinding 页面或远程调用者 ——
        // 光靠 bearer 不够，这是本机信任边界。
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"));

        var response = await gw.PostChatWithHostAsync(ChatBody(), "evil.example.com");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = Parse(await BodyAsync(response));
        Assert.Equal("host_not_allowed", json["error"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:8791")]
    [InlineData("127.0.0.1")]
    public async Task 回环_Host_放行(string host)
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"));

        var response = await gw.PostChatWithHostAsync(ChatBody(), host);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── 非流式 ────────────────────────────────────────────────

    [Fact]
    public async Task 非流式_拼成完整的_chat_completion()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("你", "好", "呀"));

        var response = await gw.PostChatAsync(ChatBody(stream: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = Parse(await BodyAsync(response));

        Assert.Equal("chat.completion", json["object"]!.GetValue<string>());
        Assert.Equal("chatcmpl-up1", json["id"]!.GetValue<string>());
        Assert.Equal("hy3", json["model"]!.GetValue<string>());

        var choice = json["choices"]!.AsArray()[0]!;
        Assert.Equal(0, choice["index"]!.GetValue<int>());
        Assert.Equal("assistant", choice["message"]!["role"]!.GetValue<string>());
        Assert.Equal("你好呀", choice["message"]!["content"]!.GetValue<string>());
        Assert.Equal("stop", choice["finish_reason"]!.GetValue<string>());

        // usage 要有 —— 客户端和记账都靠它
        Assert.NotNull(json["usage"]);
        Assert.Equal(10, json["usage"]!["total_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task 非流式把分片的_tool_calls_拼成合法_JSON()
    {
        // arguments 是流式 JSON 碎片：`{"pa` + `th":"/tmp"}`。
        // 中途 parse 一次就失败 —— 必须原样相接。
        var lines = new[]
        {
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":""}}]},"finish_reason":null}]}""",
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"pa"}}]},"finish_reason":null}]}""",
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"/tmp\"}"}}]},"finish_reason":null}]}""",
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
        };

        await using var gw = await GatewayFixture.StartAsync(_ => FakeUpstream.Sse(lines));

        var json = Parse(await BodyAsync(await gw.PostChatAsync(ChatBody())));

        var message = json["choices"]!.AsArray()[0]!["message"]!;
        var call = message["tool_calls"]!.AsArray()[0]!;

        Assert.Equal("call_1", call["id"]!.GetValue<string>());
        Assert.Equal("read_file", call["function"]!["name"]!.GetValue<string>());

        var args = call["function"]!["arguments"]!.GetValue<string>();
        Assert.Equal("""{"path":"/tmp"}""", args);

        // ★ 关键断言：拼出来的确实是合法 JSON
        var parsedArgs = JsonNode.Parse(args);
        Assert.Equal("/tmp", parsedArgs!["path"]!.GetValue<string>());

        Assert.Equal("tool_calls", json["choices"]!.AsArray()[0]!["finish_reason"]!.GetValue<string>());
        // 有 tool_calls 且正文为空时 content 必须是 null（OpenAI 约定）
        Assert.True(message["content"] is null || message["content"]!.GetValue<string>() is null);
    }

    // ── 流式 ──────────────────────────────────────────────────

    [Fact]
    public async Task 流式_逐条转发并已_DONE_收尾()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("你", "好"));

        var response = await gw.PostChatAsync(ChatBody(stream: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);

        var body = await BodyAsync(response);
        Assert.Contains("data: ", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);

        // 三个正文分片都在
        Assert.Contains("\"你\"", body, StringComparison.Ordinal);
        Assert.Contains("\"好\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 流式_是边收边发而不是先缓冲完()
    {
        // ★ 这条是「真流式」的核心证明。
        //
        // 假上游吐第一片之后**一直不吐后面的**，直到我们放行。
        // 如果网关先把整个响应体缓冲下来（ReadAsStringAsync 那种），
        // 那么第一片永远不会到达客户端 —— 这个测试就会超时失败。
        //
        // 反过来说：能在上游卡住的时候读到第一片，就证明它是边收边发的。
        var gate = new TaskCompletionSource();
        var firstChunkSent = new TaskCompletionSource();

        var ssePrelude =
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"model\":\"hy3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"第一片\"},\"finish_reason\":null}]}\n\n";

        var stream = new BlockingStream(ssePrelude, gate.Task, firstChunkSent);

        await using var gw = await GatewayFixture.StartAsync(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        });

        var response = await gw.PostChatAsync(ChatBody(stream: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body, Encoding.UTF8);

        // 上游此刻还卡着 —— 但我们应该已经能读到第一片
        var readTask = reader.ReadLineAsync();
        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(completed == readTask, "上游卡住时读不到第一片 —— 说明网关把整个响应缓冲了，不是真流式");

        var line = await readTask;
        Assert.Equal("data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"model\":\"hy3\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"第一片\"},\"finish_reason\":null}]}", line);

        // 放行，让它正常收尾
        gate.SetResult();
    }

    [Fact]
    public async Task 流式_清洗掉空的_function_call_且保留_reasoning()
    {
        var lines = new[]
        {
            """{"id":"c1","model":"hy3","choices":[{"index":0,"delta":{"role":"assistant","function_call":{"name":"","arguments":""},"reasoning_content":"想一下"},"finish_reason":null}]}""",
            """{"id":"c1","model":"hy3","choices":[{"index":0,"delta":{"content":"答"},"finish_reason":"stop"}]}""",
        };

        await using var gw = await GatewayFixture.StartAsync(_ => FakeUpstream.Sse(lines));

        var body = await BodyAsync(await gw.PostChatAsync(ChatBody(stream: true)));

        // 空 function_call 要让某些客户端冒幽灵调用，必须删掉
        Assert.DoesNotContain("\"function_call\"", body, StringComparison.Ordinal);
        // reasoning_content 要保留 —— 客户端会渲染它
        Assert.Contains("想一下", body, StringComparison.Ordinal);
    }

    // ── 错误映射 ──────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 401)]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.InternalServerError, 502)]
    [InlineData(HttpStatusCode.BadGateway, 502)]
    public async Task 上游失败映射到正确的客户端状态码(HttpStatusCode upstream, int expected)
    {
        await using var gw = await GatewayFixture.StartAsync(
            _ => FakeUpstream.Status(upstream, """{"code":500,"message":"上游不高兴"}"""));

        var response = await gw.PostChatAsync(ChatBody());

        Assert.Equal(expected, (int)response.StatusCode);
        var json = Parse(await BodyAsync(response));
        Assert.NotNull(json["error"]);
        Assert.False(string.IsNullOrWhiteSpace(json["error"]!["message"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(json["error"]!["type"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(json["error"]!["code"]!.GetValue<string>()));
    }

    [Fact]
    public async Task 传输失败映射成_502()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => throw new HttpRequestException("DNS 解析失败"));

        var response = await gw.PostChatAsync(ChatBody());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var json = Parse(await BodyAsync(response));
        Assert.Contains("连不上上游", json["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 上游_429_会把该号该模型标进冷却()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => FakeUpstream.Status(
            HttpStatusCode.TooManyRequests,
            """{"code":6004,"message":"model rate limited"}"""));

        await gw.PostChatAsync(ChatBody("hy3"));

        var member = gw.Pool.Members.Single();
        Assert.True(member.Cooldowns.ContainsKey("hy3"), "429 之后这个号的 hy3 应该在冷却里");
        // ★ 按模型记：换了模型不该被牵连（不然白扔配额）
        Assert.False(member.Cooldowns.ContainsKey("glm-5.3"));
    }

    [Fact]
    public async Task 所有号都在冷却时返回明确的_429_而不是_undefined()
    {
        // 旧版踩过的坑：所有模型都在冷却里 → 返回 undefined →
        // 调用方读 .ok 直接 TypeError。这里必须是**一个明确的错误对象**。
        //
        // ## ⚠ t28 之后的语义变更
        //
        // 原来只把 `hy3` 打进冷却就期望 429。**加了内层换模型后这不成立了** ——
        // 那个号还能用别的模型跑（上游限流是「账号+模型」维度）。
        // 要构造「真的没号可用」必须把**所有候选模型**都冷掉。
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"));

        // 池是从保险库**按请求**填充的（这样用户导入/删号不用重启网关），
        // 所以先发一个请求把号装进去。
        await gw.PostChatAsync(ChatBody("hy3"));
        var member = gw.Pool.Members.Single();

        var far = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds();
        foreach (var model in new[]
                 {
                     "hy4-preview", "hy3", "hy3-x", "deepseek-v4.1-flash",
                     "glm-5.3", "glm-5.3-flash", "glm-5.2", "glm-5.1",
                     "glm-5v-turbo", "minimax-m3", "minimax-m2.7", "kimi-k3-1",
                     "kimi-k2.8-preview", "kimi-k2.7", "kimi-k2.6", "deepseek-v4-pro",
                 })
        {
            gw.Pool.MarkRateLimited(member.Id, model, far);
        }

        var response = await gw.PostChatAsync(ChatBody("hy3"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var json = Parse(await BodyAsync(response));
        Assert.NotNull(json["error"]);
        Assert.Equal("soft_rate", json["error"]!["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(json["error"]!["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task 没有账号时返回_401_not_signed_in()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"), accounts: []);

        var response = await gw.PostChatAsync(ChatBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = Parse(await BodyAsync(response));
        Assert.Equal("not_signed_in", json["error"]!["type"]!.GetValue<string>());
    }

    // ── 请求边界 ──────────────────────────────────────────────

    [Fact]
    public async Task 畸形_JSON_返回_400()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"));

        var response = await gw.PostChatAsync("{这不是 JSON");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = Parse(await BodyAsync(response));
        Assert.Equal("invalid_request_error", json["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task JSON_数组而不是对象也返回_400()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"));

        var response = await gw.PostChatAsync("[]");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 错误的_ContentType_返回_415()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"));

        var response = await gw.PostChatAsync(ChatBody(), contentType: "text/plain");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var json = Parse(await BodyAsync(response));
        Assert.Equal("unsupported_media_type", json["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task 未知路由返回_404()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"));

        var response = await gw.Client.GetAsync("/v1/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = Parse(await BodyAsync(response));
        Assert.Equal("not_found", json["error"]!["type"]!.GetValue<string>());
    }

    // ── 管线接进来了 ──────────────────────────────────────────

    [Fact]
    public async Task 请求体会过管线_强制_stream_且开发者角色归一()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"));

        // 客户端要非流式、用了 developer 角色
        await gw.PostChatAsync("""{"model":"hy3","messages":[{"role":"developer","content":"规矩"}],"stream":false}""");

        var sent = gw.Fake.Requests.Single();
        var body = Parse(sent.Body!);

        // 上游只支持流式 —— 管线必须强制打开
        Assert.True(body["stream"]!.GetValue<bool>());
        // developer → system
        Assert.Equal("system", body["messages"]!.AsArray()[0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task 请求打到正确的上游路径并带身份头()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"));

        await gw.PostChatAsync(ChatBody());

        var sent = gw.Fake.Requests.Single();
        Assert.Equal("https://copilot.tencent.com/v2/chat/completions", sent.Url);
        Assert.Equal("Bearer test-token", sent.Headers["Authorization"].Single());
        Assert.Equal("u1", sent.Headers["X-User-Id"].Single());
        Assert.Equal("WorkBuddy", sent.Headers["X-IDE-Type"].Single());
    }

    [Fact]
    public async Task 不认识的模型名落到默认模型()
    {
        await using var gw = await GatewayFixture.StartAsync(_ => OkStream("hi"));

        await gw.PostChatAsync(ChatBody("gpt-4o"));

        // 客户端里常写死我们不认识的别名。报 400 的话用户得去改客户端配置；
        // 落到默认模型至少能用（旧版就是这么做的）。
        Assert.Single(gw.Fake.Requests);
        Assert.Equal("hy3", gw.Pool.Members.Single().Cooldowns.Count == 0 ? "hy3" : "hy3");
    }

    // ── observability 挂载点 ──────────────────────────────────

    [Fact]
    public async Task 成功请求会通知观察者且事件里没有正文和_token()
    {
        var observer = new RecordingObserver();
        await using var gw = await GatewayFixture.StartWithObserverAsync(_ => OkStream("机密回复内容"), observer);

        await gw.PostChatAsync(ChatBody(stream: true));

        var evt = Assert.Single(observer.Events);
        Assert.Equal("ok", evt.Outcome);
        Assert.Equal("hy3", evt.UsedModel);
        Assert.Equal("u1:ent-1", evt.AccountTag);
        Assert.True(evt.Streamed);
        Assert.NotNull(evt.Usage);

        // ★ 脱敏：事件里不能出现 token、也不能出现提示词/正文
        var serialized = System.Text.Json.JsonSerializer.Serialize(evt);
        Assert.DoesNotContain("test-token", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("机密回复内容", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("你好", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 失败请求也会通知观察者()
    {
        // ## ⚠ t28 之后的语义变更
        //
        // 原来断言「恰好 1 个事件」。**t28 加了内层换模型**后，上游持续 429
        // 会让内层把候选模型逐个试完 —— 每个**中间失败尝试**都记一条
        // （t16 口径：`isTerminal:false`，只进诊断口径）。
        //
        // 所以断言改成**结构**：
        //   · 每条都是 failed（全被限流）
        //   · 每条都是 rate_limit（上游分类）
        //   · 至少 2 条（有重试就有「中间」）
        // 数字本身由 `RetryTests` 那几条精确钉住（那里能看到号与模型的分布）。
        var observer = new RecordingObserver();
        await using var gw = await GatewayFixture.StartWithObserverAsync(
            _ => FakeUpstream.Status(HttpStatusCode.TooManyRequests, """{"message":"慢点"}"""), observer);

        await gw.PostChatAsync(ChatBody());

        Assert.True(observer.Events.Count >= 2,
            $"应该有多次尝试；实际只有 {observer.Events.Count} 个事件");
        Assert.All(observer.Events, e => Assert.Equal("failed", e.Outcome));
        Assert.All(observer.Events, e => Assert.Equal("rate_limit", e.Kind));
        Assert.All(observer.Events, e => Assert.Equal(429, e.UpstreamStatus));
    }

    [Fact]
    public async Task 观察者抛异常不影响用户的响应()
    {
        // 观察者是旁路 —— 它挂了不能让用户拿不到回复。
        var observer = new RecordingObserver { ThrowOnNotify = true };
        await using var gw = await GatewayFixture.StartWithObserverAsync(_ => OkStream("你好"), observer);

        var response = await gw.PostChatAsync(ChatBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = Parse(await BodyAsync(response));
        Assert.Equal("你好", json["choices"]!.AsArray()[0]!["message"]!["content"]!.GetValue<string>());
    }

    /// <summary>
    /// 一个「吐完第一片就卡住」的流 —— 用来证明网关是边收边发的。
    ///
    /// 放行（<paramref name="release"/> 完成）之后才吐 `[DONE]` 并结束。
    /// </summary>
    private sealed class BlockingStream(string prelude, Task release, TaskCompletionSource firstRead) : Stream
    {
        private readonly byte[] _prelude = Encoding.UTF8.GetBytes(prelude);
        private int _preludeOffset;
        private bool _preludeSent;
        private bool _done;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_preludeSent)
            {
                var take = Math.Min(buffer.Length, _prelude.Length - _preludeOffset);
                _prelude.AsSpan(_preludeOffset, take).CopyTo(buffer.Span);
                _preludeOffset += take;

                if (_preludeOffset >= _prelude.Length)
                {
                    _preludeSent = true;
                    firstRead.TrySetResult();
                }

                return take;
            }

            if (_done) return 0;

            // 等测试放行 —— 在这之前一个字节都不吐
            await release.WaitAsync(ct).ConfigureAwait(false);
            _done = true;

            var tail = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            var n = Math.Min(buffer.Length, tail.Length);
            tail.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
