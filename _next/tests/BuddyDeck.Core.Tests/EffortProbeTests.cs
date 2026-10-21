using System.Net;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// M17 思考档位探测的测试。对齐旧版 `upstream.js:984-1052`。
///
/// 全部走假上游（<see cref="FakeUpstream"/>），**不打真实上游**
/// —— 探测会消耗真实配额，而且真实上游不给你「档位不支持」这种边界。
/// </summary>
public class EffortProbeTests
{
    private static readonly Credential TestCredential = new()
    {
        AccessToken = "probe-token",
        Uid = "u1",
        EnterpriseId = "e1",
        Domain = "workbuddy.cn",
    };

    /// <summary>桌面身份 —— 探测和聊天必须用同一份。</summary>
    private const string DesktopUa = "WorkBuddy/5.6.2 (Windows; x64)";
    private const string DesktopVersion = "5.6.2";

    private static (EffortProbe Probe, FakeUpstream Fake) Make(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        UpstreamVariant? variant = null)
    {
        var fake = new FakeUpstream(handler);
        var probe = new EffortProbe(
            new HttpClient(fake),
            variant ?? UpstreamVariant.China,
            NullLogger<EffortProbe>.Instance);
        return (probe, fake);
    }

    /// <summary>一个真的会流事件的 SSE 响应。</summary>
    private static HttpResponseMessage StreamOk()
        => FakeUpstream.Sse("""{"choices":[{"delta":{"content":"p"}}]}""");

    /// <summary>上游拒绝档位：HTTP 400 + extError.code。</summary>
    private static HttpResponseMessage EffortRejected(string code = "InvalidParameter.ReasoningEffort")
        => FakeUpstream.Status(HttpStatusCode.BadRequest, new JsonObject
        {
            ["code"] = 400,
            ["extError"] = new JsonObject { ["code"] = code },
        }.ToJsonString());

    /// <summary>
    /// 把一次被捕获的请求体解析成 JSON。
    ///
    /// 不用 `.Json()`：那个便利方法在 `TestSupport.FakeUpstreamHandler.CapturedRequest`
    /// 上，而 Core.Tests 用的是**自己那个** `FakeUpstream`（它不引用 TestSupport
    /// —— 后者反向依赖 Gateway，引进来会把网关拖进 Core 的测试依赖）。
    /// 两者记录的内容是同构的，只是没有那个便利方法。
    /// </summary>
    private static JsonNode Body(FakeUpstream.CapturedRequest request)
        => JsonNode.Parse(request.Body ?? "{}")!;

    // ══ ① 接受 / 拒绝 ═══════════════════════════════════════════

    [Fact]
    public async Task 档位被接受()
    {
        var (probe, _) = Make(_ => StreamOk());

        var result = await probe.ProbeAsync(
            TestCredential, "hy3", "high", DesktopUa, DesktopVersion);

        Assert.True(result.Accepted);
        Assert.True(result.Streamed);
        Assert.Equal(200, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.True(result.UpstreamCalled);
    }

    [Fact]
    public async Task 档位被拒时带回业务错误码()
    {
        var (probe, _) = Make(_ => EffortRejected());

        var result = await probe.ProbeAsync(
            TestCredential, "hy3", "xhigh", DesktopUa, DesktopVersion);

        Assert.False(result.Accepted);
        Assert.False(result.Streamed);
        Assert.Equal(400, result.Status);
        // extError.code 被认出来了 —— 这是「档位不支持」和「网络不行」的分界
        Assert.Equal("InvalidParameter.ReasoningEffort", result.ErrorCode);
    }

    [Fact]
    public async Task 错误码不是在_extError_形状里时不编造()
    {
        // 形状不对 → ErrorCode 必须为 null，退化成原文
        var (probe, _) = Make(_ => FakeUpstream.Status(
            HttpStatusCode.BadRequest,
            """{"code":400,"message":"bad effort"}"""));

        var result = await probe.ProbeAsync(
            TestCredential, "hy3", "max", DesktopUa, DesktopVersion);

        Assert.False(result.Accepted);
        Assert.Null(result.ErrorCode);
        Assert.Contains("bad effort", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 错误码不是字符串时不认()
    {
        // extError.code 是数字 —— 旧版要求 typeof === "string"
        var (probe, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.BadRequest,
            """{"extError":{"code":12345}}"""));

        var result = await probe.ProbeAsync(
            TestCredential, "hy3", "high", DesktopUa, DesktopVersion);

        Assert.Null(result.ErrorCode);
    }

    // ══ ② HTTP 200 但流是空的 ═══════════════════════════════════

    [Fact]
    public async Task 返回_200_但没有事件流时不算接受()
    {
        // 上游收下了请求却没开始生成 —— 判据是「流有没有流起来」，
        // 不是「HTTP 有没有成功」
        var (probe, _) = Make(_ => FakeUpstream.Ok("{}", "application/json"));

        var result = await probe.ProbeAsync(
            TestCredential, "hy3", "high", DesktopUa, DesktopVersion);

        Assert.False(result.Accepted);
        Assert.False(result.Streamed);
        Assert.Equal(200, result.Status);
        Assert.NotEmpty(result.Detail);
    }

    [Fact]
    public async Task 空正文也不算接受()
    {
        var (probe, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.OK, ""));

        var result = await probe.ProbeAsync(
            TestCredential, "hy3", "high", DesktopUa, DesktopVersion);

        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task 分块到达的流也能认出首帧()
    {
        // 首块不含 data:，第二块才含 —— 证明是**累进读取**而不是只看第一块
        var (probe, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(": keep-alive\n\ndata: {\"choices\":[]}\n\n",
                System.Text.Encoding.UTF8, "text/event-stream"),
        });

        var result = await probe.ProbeAsync(
            TestCredential, "hy3", "high", DesktopUa, DesktopVersion);

        Assert.True(result.Accepted);
        Assert.True(result.Streamed);
    }

    // ══ ③ ⚠ 身份一致性（队长点名要求这条测试）════════════════════

    [Fact]
    public async Task 探测请求的头部与真实聊天请求逐字相同()
    {
        // ⚠⚠ 这条是本文件最重要的一条 ⚠⚠
        //
        // 旧版 upstream.js:987-989 的原话：
        //   "Same identity rule as the chat path: the probe sibling must never
        //    present a different client than a real message would."
        //
        // 为什么必须钉住：上游按客户端身份分流，不同身份看到的能力集不同。
        // 用假身份探出来的结果**看起来是真实的**，但和实际请求能跑的不是一回事
        // —— 这是「看起来对、实际错」的形态，最难查。
        //
        // 下一个人重构时很容易把两处的头构造拆开（比如给探测换一个轻量 UA），
        // 这条测试会当场红。

        var (probe, fake) = Make(_ => StreamOk());

        await probe.ProbeAsync(TestCredential, "hy3", "high", DesktopUa, DesktopVersion);

        var probed = fake.Requests.Single();

        // 手工构造"真实聊天请求"应有的头，逐条比对
        using var chatRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{UpstreamVariant.China.ChatBase}/v2/chat/completions");
        UpstreamHeaders.ApplyChat(chatRequest, TestCredential, UpstreamVariant.China, DesktopUa, DesktopVersion);

        foreach (var header in chatRequest.Headers)
        {
            var name = header.Key;
            var expected = header.Value.ToArray();

            Assert.True(
                probed.Headers.TryGetValue(name, out var actual),
                $"探测请求缺少聊天请求有的头：{name}");

            Assert.Equal(expected, actual);
        }

        // 反向：探测不该**多**带聊天没有的头（多了也是身份不一致）
        foreach (var name in probed.Headers.Keys)
        {
            if (name is "Content-Type" or "Content-Length") continue;   // content 头，不是身份头

            Assert.True(
                chatRequest.Headers.Contains(name),
                $"探测请求带了聊天请求没有的头：{name}");
        }
    }

    [Fact]
    public async Task 探测用的_UA_就是传入的那个_不自己编()
    {
        // ⚠ 用不带空格的 UA。
        //
        // 原因：`User-Agent` 是**结构化头**，`HttpClient` 会按语法把它拆成
        // 多个值 —— 带空格的 UA（`SomeClient/9.9 (Custom)`）到处理器那侧
        // 会变成两个值 `["SomeClient/9.9", "(Custom)"]`。
        //
        // 那是 HttpClient 的既定行为，不是探测代码的问题。用一个无空格的
        // UA 才能精确断言「我们传的就是它、没被替换」。
        const string ua = "SomeClient/9.9";
        var (probe, fake) = Make(_ => StreamOk());

        await probe.ProbeAsync(TestCredential, "hy3", "high", ua, "9.9");

        var headers = fake.Requests.Single().Headers;
        Assert.Equal(ua, headers["User-Agent"].Single());
        Assert.Equal("9.9", headers["X-IDE-Version"].Single());
    }

    [Fact]
    public async Task 带空格的_UA_被_HttpClient_按语法拆成多值_不是我们改了它()
    {
        // 这条记录上面那个现象的成因，免得下一个人看到多值以为是我们拼错了头。
        // 带空格的 UA 是合法的，HttpClient 只是按 HTTP 语法拆成多个 token。
        const string ua = "WorkBuddy/5.6.2 (Windows; x64)";
        var (probe, fake) = Make(_ => StreamOk());

        await probe.ProbeAsync(TestCredential, "hy3", "high", ua, "5.6.2");

        var values = fake.Requests.Single().Headers["User-Agent"];
        Assert.True(values.Length > 1, "带空格的 UA 应当被 HttpClient 拆成多个值");

        // 关键：拼回来仍然是我们传的那个字符串（没有丢、没有改）
        Assert.Equal(ua, string.Join(" ", values));
    }

    [Fact]
    public async Task 不传_UA_时退化成_CLI_形态_而不是编一个()
    {
        // null = CLI 形态（ApplyChat 的行为）。探测**不该**自己填一个 UA
        var (probe, fake) = Make(_ => StreamOk());

        await probe.ProbeAsync(TestCredential, "hy3", "high", null, null);

        var headers = fake.Requests.Single().Headers;
        Assert.False(headers.ContainsKey("User-Agent"));
        Assert.False(headers.ContainsKey("X-IDE-Version"));
        // 但身份三元组要有（ApplyChat 负责）
        Assert.Equal("u1", headers["X-User-Id"].Single());
    }

    // ══ ④ 请求体形状 ═════════════════════════════════════════════

    [Fact]
    public async Task 请求体带档位_强制流式_与最小输出()
    {
        var (probe, fake) = Make(_ => StreamOk());

        await probe.ProbeAsync(TestCredential, "hy3", "medium", DesktopUa, DesktopVersion);

        var body = Body(fake.Requests.Single());
        Assert.Equal("hy3", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());          // 上游只支持流式
        Assert.Equal("medium", body["reasoning_effort"]!.GetValue<string>());
        Assert.Equal(1, body["max_tokens"]!.GetValue<int>());   // 国内版 = 1（只花一个 token）

        var messages = body["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("ping", messages[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task 档位传_null_时不写_reasoning_effort_字段()
    {
        // null = 探「不带档位」的基线，**不是**发一个显式 null
        var (probe, fake) = Make(_ => StreamOk());

        await probe.ProbeAsync(TestCredential, "hy3", null, DesktopUa, DesktopVersion);

        var body = Body(fake.Requests.Single());
        Assert.False(body.AsObject().ContainsKey("reasoning_effort"));
    }

    [Fact]
    public async Task 探测打到聊天端点且是_POST()
    {
        var (probe, fake) = Make(_ => StreamOk());

        await probe.ProbeAsync(TestCredential, "hy3", "high", DesktopUa, DesktopVersion);

        var req = fake.Requests.Single();
        Assert.Equal("POST", req.Method);
        Assert.Equal("https://copilot.tencent.com/v2/chat/completions", req.Url);
        Assert.Equal("Bearer probe-token", req.Headers["Authorization"].Single());
    }

    // ══ ⑤ 国际版 ═════════════════════════════════════════════════

    [Fact]
    public async Task 国际版补_system_且_max_tokens_是_16()
    {
        var (probe, fake) = Make(_ => StreamOk(), UpstreamVariant.Global);

        await probe.ProbeAsync(TestCredential, "hy3", "high", "WorkBuddyAI/1.0", "1.0");

        var body = Body(fake.Requests.Single());
        Assert.Equal(16, body["max_tokens"]!.GetValue<int>());

        var messages = body["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());   // 国际版必须首条是 system
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("ping", messages[1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task 国内版不补_system()
    {
        var (probe, fake) = Make(_ => StreamOk());

        await probe.ProbeAsync(TestCredential, "hy3", "high", DesktopUa, DesktopVersion);

        var messages = Body(fake.Requests.Single())["messages"]!.AsArray();
        Assert.Single(messages);
    }

    // ══ ⑥ 失败不抛 ═══════════════════════════════════════════════

    [Fact]
    public async Task 连接层失败时返回结果不抛()
    {
        var (probe, _) = Make(_ => throw new HttpRequestException("connection refused"));

        var result = await probe.ProbeAsync(
            TestCredential, "hy3", "high", DesktopUa, DesktopVersion);

        Assert.False(result.Accepted);
        Assert.Equal(0, result.Status);           // 0 = 连不上
        Assert.Contains("transport error", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 上游_500_时返回结果不抛()
    {
        var (probe, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.InternalServerError, "boom"));

        var result = await probe.ProbeAsync(
            TestCredential, "hy3", "high", DesktopUa, DesktopVersion);

        Assert.False(result.Accepted);
        Assert.Equal(500, result.Status);
        Assert.True(result.UpstreamCalled);
    }

    [Fact]
    public async Task 凭证失效_401_时返回结果不抛()
    {
        var (probe, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.Unauthorized, """{"code":401}"""));

        var result = await probe.ProbeAsync(
            TestCredential, "hy3", "high", DesktopUa, DesktopVersion);

        Assert.False(result.Accepted);
        Assert.Equal(401, result.Status);
    }

    [Fact]
    public async Task 批量探测中单个失败不影响后续()
    {
        // 一个档位探测失败不该让后面的档位全探不了
        var (probe, _) = Make(request =>
        {
            var body = JsonNode.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            var effort = body["reasoning_effort"]?.GetValue<string>();

            return effort == "xhigh" ? EffortRejected() : StreamOk();
        });

        var results = new List<EffortProbeResult>();
        foreach (var effort in Effort.Values)
        {
            results.Add(await probe.ProbeAsync(
                TestCredential, "hy3", effort, DesktopUa, DesktopVersion));
        }

        Assert.Equal(5, results.Count);
        Assert.True(results[0].Accepted);      // low
        Assert.True(results[1].Accepted);      // medium
        Assert.True(results[2].Accepted);      // high
        Assert.False(results[3].Accepted);     // xhigh ← 只有这个被拒
        Assert.True(results[4].Accepted);      // max —— 前面的失败没拖垮它
    }

    // ══ ⑦ 词汇表 ═════════════════════════════════════════════════

    [Fact]
    public void 档位词汇表与旧版一致()
    {
        // 旧版 upstream.js:54 的 EFFORT_VALUES
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], Effort.Values);
    }

    [Theory]
    [InlineData("low", true)]
    [InlineData("medium", true)]
    [InlineData("high", true)]
    [InlineData("xhigh", true)]
    [InlineData("max", true)]
    [InlineData("HIGH", false)]      // 大小写敏感 —— 上游只认小写
    [InlineData("ultra", false)]     // 不认识的档位
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 档位白名单判定(string? effort, bool expected)
    {
        Assert.Equal(expected, Effort.IsKnown(effort));
    }

    // ══ ⑧ 从目录行解析思考能力 ═══════════════════════════════════

    [Fact]
    public void 思考能力_从目录行解析()
    {
        var row = new JsonObject
        {
            ["supportsReasoning"] = true,
            ["onlyReasoning"] = false,
            ["reasoning"] = new JsonObject
            {
                ["supportedEfforts"] = new JsonArray("low", "medium", "high", "ultra"),
                ["defaultEffort"] = "medium",
                ["canDisableThinking"] = true,
            },
        };

        var reasoning = Effort.ResolveReasoning(row);

        Assert.True(reasoning.Supports);
        Assert.False(reasoning.OnlyReasoning);
        // "ultra" 不在白名单里，被丢掉
        Assert.Equal(["low", "medium", "high"], reasoning.SupportedEfforts);
        Assert.Equal("medium", reasoning.DefaultEffort);
        Assert.True(reasoning.CanDisableThinking);
    }

    [Fact]
    public void 思考能力_旧的_effort_字段名也认()
    {
        var row = new JsonObject
        {
            ["reasoning"] = new JsonObject { ["effort"] = "high" },
        };

        Assert.Equal("high", Effort.ResolveReasoning(row).DefaultEffort);
    }

    [Fact]
    public void 思考能力_defaultEffort_优先于_effort()
    {
        var row = new JsonObject
        {
            ["reasoning"] = new JsonObject { ["defaultEffort"] = "max", ["effort"] = "low" },
        };

        Assert.Equal("max", Effort.ResolveReasoning(row).DefaultEffort);
    }

    [Fact]
    public void 思考能力_认不出的默认档位当没给()
    {
        var row = new JsonObject
        {
            ["reasoning"] = new JsonObject { ["defaultEffort"] = "ultra" },
        };

        Assert.Equal("", Effort.ResolveReasoning(row).DefaultEffort);
    }

    [Fact]
    public void 思考能力_没有_reasoning_对象时给安全默认()
    {
        var reasoning = Effort.ResolveReasoning(new JsonObject());

        Assert.False(reasoning.Supports);
        Assert.False(reasoning.OnlyReasoning);
        Assert.Empty(reasoning.SupportedEfforts);
        Assert.Equal("", reasoning.DefaultEffort);
        Assert.True(reasoning.CanDisableThinking);   // 默认允许关（旧版默认也是 true）
    }

    [Fact]
    public void 思考能力_只思考的模型()
    {
        var row = new JsonObject
        {
            ["supportsReasoning"] = true,
            ["onlyReasoning"] = true,
        };

        var reasoning = Effort.ResolveReasoning(row);

        Assert.True(reasoning.Supports);
        Assert.True(reasoning.OnlyReasoning);
    }
}
