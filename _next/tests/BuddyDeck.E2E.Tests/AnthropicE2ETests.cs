using System.Net;
using System.Text.Json.Nodes;
using BuddyDeck.TestSupport;
using Xunit;

namespace BuddyDeck.E2E.Tests;

/// <summary>
/// Anthropic Messages 协议层的**端到端覆盖（第 ④ 层）**（t36）。
///
/// ## 为什么单独一组、而且必须走真 HTTP
///
/// gateway 写的 `AnthropicEndpointTests.cs` 是**类型级**的 ——
/// 直接 `new AnthropicEndpoints(...)` 调静态函数，验的是**第 ③ 层（实现存在）**。
///
/// **本项目第 ② 层（装配）已经出过三次事故**：
/// ```
/// /admin/*            写了路由但没挂          → 全部 404
/// UsageEndpoints      类写了但没注册          → 全部 404
/// AnthropicEndpoints  接口建了但没接线        → 全部 404
/// ```
/// ⇒ **类型级测试永远验不出第 ② 层**，因为它测的正是「类型本身对不对」，
///   而事故出在「类型有没有被用上」。
///
/// 所以本组一律：**真宿主（端口 0）+ 真 HttpClient + 假上游**。
///
/// ## ⚠ 中文断言的两条规矩（observability 实测）
///
/// 1. **流式路径**经 `SseSanitizer` 重新序列化会把中文转义成 `\uXXXX` ⇒
///    断言中文内容要用**非流式 + 解析 JSON 字段**，不要 `Contains("中文")`。
/// 2. 见 <see cref="Anthropic_流式路径的中文转义_已知缺陷标记"/> ——
///    那条是**故意**记录已知缺陷的，它红了说明转义被修好了，该来更新它。
/// </summary>
[Collection("GatewayHost")]
public class AnthropicE2ETests
{
    private const string MessagesPath = "/v1/messages";
    private const string CountTokensPath = "/v1/messages/count_tokens";

    /// <summary>一段最小可用的 Anthropic Messages 请求体。</summary>
    private static string MessagesBody(
        string model = "hy3",
        string content = "你好",
        bool stream = false,
        string? system = null,
        string? extra = null)
    {
        var systemPart = system is null ? "" : $""","system":{JsonValue.Create(system)!.ToJsonString()}""";
        var extraPart = extra is null ? "" : "," + extra;

        return $$"""
        {"model":"{{model}}","max_tokens":100,"stream":{{(stream ? "true" : "false")}},
         "messages":[{"role":"user","content":{{JsonValue.Create(content)!.ToJsonString()}}}]{{systemPart}}{{extraPart}}}
        """;
    }

    /// <summary>发一个 Anthropic 请求。</summary>
    private static Task<HttpResponseMessage> PostMessagesAsync(
        E2EHost e2e,
        string body,
        string? apiKey = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
        => e2e.SendAsync(HttpMethod.Post, MessagesPath, body, apiKey, "application/json", null, completion);

    /// <summary>上游收到的那次请求的 body（解析后）。</summary>
    private static JsonObject UpstreamBody(FakeUpstreamHandler fake)
        => fake.Last.Json()?.AsObject() ?? throw new InvalidOperationException("上游没收到请求");

    // ══ ① 端点可达性（装配层）════════════════════════════════

    [Fact]
    public async Task Anthropic_POST_messages_可达_不是404()
    {
        // ★ 这条是**装配层**的核心断言。
        //   若 `AnthropicEndpoints` 没被注册进 `Program.cs`，这里会拿到 404 ——
        //   而那正是本项目出过三次的事故形态。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("好的"));

        using var response = await PostMessagesAsync(e2e, MessagesBody());

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Anthropic_POST_count_tokens_可达_且没被_v1_messages_前缀吃掉()
    {
        // ⚠ 这条验的是**路由顺序**：`/v1/messages` 是 `/v1/messages/count_tokens` 的前缀。
        //    若注册顺序写反（笼统的在前），count_tokens 会被当成 messages 处理。
        //    gateway 说它在 `Program.cs` 注释里写明了「更具体的先」—— 这里验它真的生效。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("好的"));

        using var response = await e2e.SendAsync(
            HttpMethod.Post, CountTokensPath,
            """{"model":"hy3","messages":[{"role":"user","content":"你好世界"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await E2EHost.JsonAsync(response);

        // count_tokens 的形状是 `{"input_tokens": N}`，**不是** chat.completion
        Assert.NotNull(body["input_tokens"]);
        Assert.True(body["input_tokens"]!.GetValue<int>() > 0, "input_tokens 应该是个正数");
        // 若被 messages 吃掉，会返回 Anthropic message 文档（有 type/content）
        Assert.Null(body["content"]);
    }

    [Fact]
    public async Task Anthropic_没账号时回_Anthropic_形状的错误信封_不是_OpenAI_形状()
    {
        // ★ 这条区别很重要：Claude Code 只认 Anthropic 的错误形状。
        //   回 OpenAI 的 `{"error":{"message":...}}` 它解不出来。
        //
        // 用空保险库制造「没账号」。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("好的"), new TestVault());

        using var response = await PostMessagesAsync(e2e, MessagesBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await E2EHost.JsonAsync(response);

        // Anthropic 形状：顶层 `type: "error"`，错误细节在 `error` 里
        Assert.Equal("error", body["type"]!.GetValue<string>());
        var error = Assert.IsType<JsonObject>(body["error"]);
        Assert.Equal("authentication_error", error["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(error["message"]!.GetValue<string>()));
    }

    // ══ ② 翻译正确性 ═════════════════════════════════════════

    [Fact]
    public async Task Anthropic_system_被抽离到上游的_system_消息()
    {
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好的"));
        await using var e2e = await E2EHost.StartAsync(fake);

        using var response = await PostMessagesAsync(e2e,
            MessagesBody(system: "你是兔子助手"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sent = UpstreamBody(fake);
        var messages = sent["messages"]!.AsArray();

        // Anthropic 的顶层 system 要变成 OpenAI 的 `messages[0] = {role:"system"}`
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("你是兔子助手", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anthropic_tool_result_变成_role_tool_并带上_tool_call_id()
    {
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好的"));
        await using var e2e = await E2EHost.StartAsync(fake);

        // Anthropic 把工具结果塞在 **user 消息的 content block** 里
        var body = """
        {"model":"hy3","max_tokens":100,"messages":[
          {"role":"user","content":"北京天气"},
          {"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{"city":"北京"}}]},
          {"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"晴 25 度"}]}
        ]}
        """;

        using var response = await PostMessagesAsync(e2e, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var messages = UpstreamBody(fake)["messages"]!.AsArray();

        // assistant 侧：tool_use → tool_calls
        var assistant = messages.First(m => m!["role"]!.GetValue<string>() == "assistant")!;
        var call = assistant["tool_calls"]!.AsArray()[0]!;
        Assert.Equal("toolu_1", call["id"]!.GetValue<string>());
        Assert.Equal("get_weather", call["function"]!["name"]!.GetValue<string>());

        // user 侧：tool_result → role:"tool" + tool_call_id
        var tool = messages.First(m => m!["role"]!.GetValue<string>() == "tool")!;
        Assert.Equal("toolu_1", tool["tool_call_id"]!.GetValue<string>());
        Assert.Equal("晴 25 度", tool["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("any", "required")]
    public async Task Anthropic_tool_choice_对象形态映射正确(string anthropicType, string expected)
    {
        // ⚠ **本用例原名**：`Anthropic_tool_choice_字符串形态映射正确` —— 名字是错的。
        //
        // 我第一版按**字符串形态**（`"tool_choice":"auto"`）发请求，三条全 NullReference ——
        // 因为实现里 `TranslateToolChoice` 的第一行就是：
        //     `if (choice is not JsonObject obj) return null;`
        // ⇒ **字符串形态被丢弃，不转发给上游**。
        //
        // 我去核了旧版（`workbuddy_api/src/anthropic.js:126`）：
        //     `if (!isObject(choice)) return undefined;`
        // —— **旧版也是对象形态才认**。所以这不是新版引入的偏差，两者一致。
        //
        // ⇒ 于是我把用例改成验**对象形态**（那是实际契约），
        //   并另立两条把其它形态的事实也显式记录下来
        //   （<see cref="Anthropic_tool_choice_字符串形态不被转发_记录当前契约"/>
        //     与 <see cref="Anthropic_tool_choice_none_会连_tools_一起删掉"/>）。
        //
        // ⚠ 关于「官方 Anthropic API 是否接受字符串形态」：我**没有查证**（web 检索不可用），
        //   所以不声称这是缺陷。这里只记录**本实现的实际契约**。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好的"));
        await using var e2e = await E2EHost.StartAsync(fake);

        // Anthropic 的对象形态：{"type":"auto"|"any"|"none"|"tool"}
        var extra = "\"tool_choice\":{\"type\":\"" + anthropicType + "\"},"
                  + "\"tools\":[{\"name\":\"get_weather\",\"description\":\"查天气\","
                  + "\"input_schema\":{\"type\":\"object\"}}]";

        using var response = await PostMessagesAsync(e2e, MessagesBody(extra: extra));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var choice = UpstreamBody(fake)["tool_choice"];
        Assert.NotNull(choice);
        Assert.Equal(expected, choice!.GetValue<string>());
    }

    [Fact]
    public async Task Anthropic_tool_choice_none_会连_tools_一起删掉()
    {
        // ## 为什么 "none" 单独一条（而不是并进上面那个 Theory）
        //
        // 它**不是**「映射成 "none" 后照常转发」—— 实测上游收到的体里
        // **`tool_choice` 和 `tools` 都没有了**。
        //
        // 我一开始把这个 case 也放进 Theory（期望 `tool_choice == "none"`），
        // **结果是错的**。去核了实现才明白这是**有意设计**：
        //
        //   `UpstreamProtocol.cs:318-321`
        //     /// `tool_choice: "none"` 时要把 tools 也删掉。
        //     private static void SuppressTools(JsonObject body) { body.Remove("tools"); ... }
        //   `UpstreamProtocol.cs:248-249`（类注释）：
        //     ///   · 字符串 "none"        → 删掉 tool_choice **和** tools
        //     ///   · 对象 {"type":"none"} → 同上
        //
        // 理由（注释原话）：**「留着 tools + none 上游会困惑」**。
        //
        // ⇒ 所以正确处理是「**两个都删**」，而不是「转发 tool_choice: none」。
        //   这条用例把这个容易被误当成 bug 的行为钉住。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好的"));
        await using var e2e = await E2EHost.StartAsync(fake);

        var extra = "\"tool_choice\":{\"type\":\"none\"},"
                  + "\"tools\":[{\"name\":\"get_weather\",\"description\":\"查天气\","
                  + "\"input_schema\":{\"type\":\"object\"}}]";

        using var response = await PostMessagesAsync(e2e, MessagesBody(extra: extra));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sent = UpstreamBody(fake);
        Assert.Null(sent["tool_choice"]);   // 删掉了
        Assert.Null(sent["tools"]);         // ★ 连 tools 一起删 —— 这是重点
    }

    [Fact]
    public async Task Anthropic_tool_choice_字符串形态不被转发_记录当前契约()
    {
        // ## 这条记录的是**当前实现的实际契约**，不是"应该怎样"
        //
        // 实测：`{"tool_choice":"auto"}`（Anthropic 官方文档里的字符串写法）
        // **会被翻译层静默丢弃** —— 上游收到的请求体里根本没有 `tool_choice` 字段。
        //
        // 证据链：
        //   · 新版 `AnthropicEndpoints.TranslateToolChoice` 首行 `if (choice is not JsonObject) return null;`
        //   · 旧版 `workbuddy_api/src/anthropic.js:126` 同样 `if (!isObject(choice)) return undefined;`
        //   · 两者一致 ⇒ 不是移植时引入的偏差
        //
        // ⚠ **我不断言这是缺陷** —— 「官方 API 是否接受字符串形态」我没查证
        //   （web 检索不可用），所以只把事实钉住。
        //
        // ⚠ 若哪天有人加了字符串支持，**这条会红** —— 那说明契约变了，
        //   请连同上面那条对象形态用例一起复核。（同「已知缺陷标记」的手法。）
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好的"));
        await using var e2e = await E2EHost.StartAsync(fake);

        var extra = "\"tool_choice\":\"auto\","
                  + "\"tools\":[{\"name\":\"get_weather\",\"description\":\"查天气\","
                  + "\"input_schema\":{\"type\":\"object\"}}]";

        using var response = await PostMessagesAsync(e2e, MessagesBody(extra: extra));

        // 请求本身**不失败**（这是"静默"的部分：客户端不会看到任何错误）
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 但 tool_choice 没有到达上游
        var sent = UpstreamBody(fake);
        Assert.Null(sent["tool_choice"]);
        // 而 tools 是**照常转发**的（不是整个 tools 都丢了）
        Assert.NotNull(sent["tools"]);
    }

    [Fact]
    public async Task Anthropic_tool_choice_对象形态被压成字符串()
    {
        // Anthropic 的对象形态 `{"type":"tool","name":"x"}` 上游不认 —— 要压成函数名字符串。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好的"));
        await using var e2e = await E2EHost.StartAsync(fake);

        var extra = """
        "tool_choice":{"type":"tool","name":"get_weather"},
        "tools":[{"name":"get_weather","description":"查天气","input_schema":{"type":"object"}}]
        """;

        using var response = await PostMessagesAsync(e2e, MessagesBody(extra: extra));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var choice = UpstreamBody(fake)["tool_choice"];
        Assert.Equal(System.Text.Json.JsonValueKind.String, choice!.GetValueKind());
        Assert.Equal("get_weather", choice.GetValue<string>());
    }

    [Fact]
    public async Task Anthropic_响应形状是_message_文档_不是_chat_completion()
    {
        // 上游回 OpenAI 流，客户端要拿到 Anthropic 的 `type:"message"` 文档。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("你好"));

        using var response = await PostMessagesAsync(e2e, MessagesBody(stream: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await E2EHost.JsonAsync(response);

        Assert.Equal("message", body["type"]!.GetValue<string>());
        Assert.Equal("assistant", body["role"]!.GetValue<string>());
        Assert.Equal("你好", body["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
        Assert.NotNull(body["stop_reason"]);
        Assert.NotNull(body["usage"]!["input_tokens"]);
    }

    [Fact]
    public async Task Anthropic_stop_reason_映射_stop到end_turn()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("好的"));

        using var response = await PostMessagesAsync(e2e, MessagesBody(stream: false));
        var body = await E2EHost.JsonAsync(response);

        Assert.Equal("end_turn", body["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anthropic_模型报stop但实际有tool_calls时修正为tool_use()
    {
        // ★★ 这条是翻译层里**最容易静默出错**的地方。
        //
        // 上游有些模型发了 tool_calls，却把 `finish_reason` 报成 `stop`。
        // 而 **Claude Code 靠 `stop_reason` 决定要不要执行工具** ——
        // 报成 `end_turn` 它会以为「模型只是说了句话」，
        // **把工具调用静默丢掉**（不报错、不提示，用户只觉得工具没生效）。
        //
        // 所以正确的实现是「**以实际内容为准**」：
        // 有 tool_calls 就报 `tool_use`，不管 finish_reason 说什么。
        var upstream = UpstreamResponses.SseFrames(
            [
                StreamScript.RoleFrame(),
                StreamScript.ToolCallStartFrame(0, "toolu_9", "get_weather"),
                StreamScript.ToolCallArgsFrame(0, """{"city":"北京"}"""),
                // ⚠ 关键：finish_reason 是 stop，但上面确实发了 tool_calls
                StreamScript.StopFrame("stop"),
            ]);

        await using var e2e = await E2EHost.StartAsync(_ => upstream);

        using var response = await PostMessagesAsync(e2e, MessagesBody(stream: false));
        var body = await E2EHost.JsonAsync(response);

        // 修正生效：报 tool_use 而不是 end_turn
        Assert.Equal("tool_use", body["stop_reason"]!.GetValue<string>());

        // 而且工具调用本身在（否则 Claude Code 也没有东西可执行）
        var toolUse = body["content"]!.AsArray()
            .First(c => c!["type"]!.GetValue<string>() == "tool_use");
        Assert.Equal("get_weather", toolUse!["name"]!.GetValue<string>());
        Assert.Equal("北京", JsonNode.Parse(toolUse["input"]!.ToJsonString())!["city"]!.GetValue<string>());
    }

    // ══ ③ 客户端指纹行剔除 —— 双向断言 ═══════════════════════

    [Fact]
    public async Task Anthropic_指纹行被剔除_上游收到的体不含它()
    {
        // 这是 Claude Code **能不能用**的前提：
        // 带上这一行，上游回 400 / code 11128「Illegal API invocation from an unapproved channel」。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好的"));
        await using var e2e = await E2EHost.StartAsync(fake);

        const string fingerprint =
            "x-anthropic-billing-header: cc_version=2.1.250.7b8; cc_entrypoint=sdk-cli;";

        using var response = await PostMessagesAsync(e2e,
            MessagesBody(system: $"你是助手\n{fingerprint}\n继续"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sent = UpstreamBody(fake);
        var raw = sent.ToJsonString();

        // ★ 指纹行必须**不在**上游收到的任何地方
        Assert.DoesNotContain("x-anthropic-billing-header", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("cc_entrypoint", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("cc_version", raw, StringComparison.Ordinal);

        // 但**同一段 system 里的其它内容必须留着**（别删多了）
        var system = sent["messages"]!.AsArray()
            .First(m => m!["role"]!.GetValue<string>() == "system")!["content"]!.GetValue<string>();
        Assert.Contains("你是助手", system, StringComparison.Ordinal);
        Assert.Contains("继续", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anthropic_没有指纹行时_system_原样保留()
    {
        // ★ **反向断言** —— 这条和上一条同样重要。
        //
        // 只验「删掉了」会让「**删多了**」的 bug 溜过去：
        // 一个把所有 system 都清空的实现，能通过上一条，但会让 Claude Code 彻底失能。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好的"));
        await using var e2e = await E2EHost.StartAsync(fake);

        const string system = "你是兔子助手，回答要简洁。";

        using var response = await PostMessagesAsync(e2e, MessagesBody(system: system));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sent = UpstreamBody(fake);
        var actual = sent["messages"]!.AsArray()
            .First(m => m!["role"]!.GetValue<string>() == "system")!["content"]!.GetValue<string>();

        // 逐字符相等 —— 不能多一个字、少一个字
        Assert.Equal(system, actual);
    }

    [Fact]
    public async Task Anthropic_指纹行只在行首匹配_中间提及不误删()
    {
        // 指纹行的正则是「行首可空白 + x-anthropic-…header:」。
        // 如果 system 正文里**顺带提到**了这个字符串（比如在讲它自己），
        // **那是用户内容，不该删** —— 删多了同样是 bug。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好的"));
        await using var e2e = await E2EHost.StartAsync(fake);

        // 注意：这个是在**句子中间**提到，不是独立成行
        const string system = "帮我分析这行日志：x-anthropic-billing-header: cc_version=1.0";

        using var response = await PostMessagesAsync(e2e, MessagesBody(system: system));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var actual = UpstreamBody(fake)["messages"]!.AsArray()
            .First(m => m!["role"]!.GetValue<string>() == "system")!["content"]!.GetValue<string>();

        Assert.Equal(system, actual);
    }

    // ══ ④ 流式命名事件序列 ═══════════════════════════════════

    [Fact]
    public async Task Anthropic_流式事件名与顺序符合规范()
    {
        // ★ Anthropic 流式是**命名事件**（`event: xxx` + `data: {...}`），
        //   和 OpenAI 的匿名 data-only 完全不同。
        //   Claude Code 按 `event:` 名做状态机 —— 名字错或顺序错，它就卡住。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("你好", "世界"));

        using var response = await PostMessagesAsync(e2e, MessagesBody(stream: true),
            completion: HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/event-stream",
            response.Content.Headers.ContentType?.ToString() ?? "", StringComparison.Ordinal);

        var raw = await E2EHost.TextAsync(response);
        var events = AnthropicSse.EventNames(raw);

        // ① 必须有序：message_start 第一
        Assert.Equal("message_start", events[0]);

        // ② content_block_delta 必须在 message_stop 之前
        Assert.Contains("content_block_delta", events);
        Assert.True(events.IndexOf("content_block_delta") < events.IndexOf("message_stop"),
            "content_block_delta 必须在 message_stop 之前");

        // ③ message_stop 必须是最后一个
        Assert.Equal("message_stop", events[^1]);

        // ④ 完整序列都出现（content_block_start/stop 至少各一次）
        Assert.Contains("content_block_start", events);
        Assert.Contains("content_block_stop", events);
        Assert.Contains("message_delta", events);
    }

    [Fact]
    public async Task Anthropic_流式_message_start_带_message_文档骨架()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("你好"));

        using var response = await PostMessagesAsync(e2e, MessagesBody(stream: true),
            completion: HttpCompletionOption.ResponseHeadersRead);

        var payloads = AnthropicSse.Payloads(await E2EHost.TextAsync(response));
        var first = JsonNode.Parse(payloads[0])!.AsObject();

        Assert.Equal("message_start", first["type"]!.GetValue<string>());
        var message = first["message"]!.AsObject();
        Assert.Equal("message", message["type"]!.GetValue<string>());
        Assert.Equal("assistant", message["role"]!.GetValue<string>());
        Assert.NotNull(message["usage"]);
    }

    [Fact]
    public async Task Anthropic_流式_message_delta_带_stop_reason()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("你好"));

        using var response = await PostMessagesAsync(e2e, MessagesBody(stream: true),
            completion: HttpCompletionOption.ResponseHeadersRead);

        var payloads = AnthropicSse.Payloads(await E2EHost.TextAsync(response));
        var delta = payloads
            .Select(p => JsonNode.Parse(p))
            .First(n => n?["type"]?.GetValue<string>() == "message_delta")!;

        Assert.Equal("end_turn", delta["delta"]!["stop_reason"]!.GetValue<string>());
    }

    // ══ 中文不转义（我主动补的第五条 —— 标记已按设计触发并被更新）════

    [Fact]
    public async Task Anthropic_流式路径的中文不被转义()
    {
        // ## 这条的历史：它**曾经是一条"已知缺陷标记"，而标记按设计触发了**
        //
        // 我最初按队长的批准写成「记录当前缺陷」的形式 —— 断言
        // 「流里能搜到 `\u4e2d` 转义」（因为 observability 说
        // `SseSanitizer` 重新序列化会把中文变 `\uXXXX`）。
        //
        // **它第一次跑就红了**，报的是我自己写的那句提示：
        //   「中文转义已被修复（流式响应里出现了中文原文）—— 这是好消息：
        //     请把本用例改成『必须含中文原文』，让转义再出现时会红。」
        //
        // 我起了个探针把流式原文打出来核对，事实是：
        //   ```
        //   event: content_block_delta
        //   data: {"type":"content_block_delta","index":0,
        //          "delta":{"type":"text_delta","text":"中文测试"}}   ← 中文原文，没有转义
        //   ```
        // ⇒ **Anthropic 这条路径没有那个问题。**
        //
        // 所以按它自己的指示改成本条：**断言必须含中文原文**，
        // 让**转义再出现时会红**。
        //
        // ## 为什么这个"反向断言"是对的
        //
        // 通常断言是「修好了要绿」。这条相反：**它钉住的是一个已经正确的性质** ——
        // 一旦有人让 `SseSanitizer`（或别的什么）重新开始转义中文，它会立刻红。
        // 对「已知会退化但不能明文禁止的行为」，这是对的方向。
        //
        // ⚠ 注意它**不是在验"功能对不对"** —— `\u4e2d\u6587` 和 `中文` 在 JSON 里语义等价。
        //   它验的是**可读性**：Claude Code 的日志/抓包里看到一坨转义，
        //   排查的人会先怀疑编码，而其实内容是对的。那是一种真实的时间损失。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("中文测试"));

        using var response = await PostMessagesAsync(e2e, MessagesBody(stream: true),
            completion: HttpCompletionOption.ResponseHeadersRead);

        var raw = await E2EHost.TextAsync(response);

        // ★ 必须出现中文原文
        Assert.Contains("中文测试", raw, StringComparison.Ordinal);

        // ★ 且**不得**出现转义形式（\u4e2d = 中）—— 这条防退化
        Assert.DoesNotContain("\\u4e2d", raw, StringComparison.Ordinal);

        // 对照：非流式路径同样保持中文原文（走 JSON 序列化，解析后拿到的就是中文）
        using var nonStream = await PostMessagesAsync(e2e, MessagesBody(stream: false));
        var body = await E2EHost.JsonAsync(nonStream);
        var text = body["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal("中文测试", text);
    }

    // ══ 假上游 / 端口约束 ═════════════════════════════════════

    [Fact]
    public async Task Anthropic_全程走假上游_且宿主不占保留端口()
    {
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好的"));
        await using var e2e = await E2EHost.StartAsync(fake);

        using var response = await PostMessagesAsync(e2e, MessagesBody());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 请求确实打到了假上游（不是真实上游）
        Assert.Equal(1, fake.Count);
        // ⚠ 别断言 URL 里有 "127.0.0.1" —— 那是错的：
        //    假上游替换的是 **HttpMessageHandler**，不是 base 地址，
        //    所以 URL 仍是变体里配的真实域名（copilot.tencent.com）。
        //    "走的是假上游"的证据是 **fake.Count > 0**（请求被假处理器截住了），
        //    而不是 URL 长什么样。我第一版就断言错了 URL。
        Assert.Contains("/v2/chat/completions", fake.Last.Url, StringComparison.Ordinal);

        // 宿主端口是系统分配的，不占 8787 / 3080
        Assert.True(e2e.Port > 0);
        Assert.False(e2e.IsReservedPort, $"测试宿主占了保留端口 {e2e.Port}");
    }
}

/// <summary>
/// 解析 Anthropic 命名事件流的小工具（`event: xxx` + `data: {...}`）。
///
/// 为什么不复用 OpenAI 那套：OpenAI 的 SSE 是**匿名**的（只有 `data:`），
/// Anthropic 是**命名**的（每段前面有 `event: xxx`）。
/// 用同一套解析会把两种协议混起来，验不出「事件名对不对」。
/// </summary>
internal static class AnthropicSse
{
    /// <summary>按出现顺序取出所有 `event:` 的值。</summary>
    public static List<string> EventNames(string raw)
    {
        var names = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith("event:", StringComparison.Ordinal)) continue;
            var value = trimmed.Length > 6 && trimmed[6] == ' ' ? trimmed[7..] : trimmed[6..];
            if (value.Length > 0) names.Add(value.Trim());
        }
        return names;
    }

    /// <summary>按出现顺序取出所有 `data:` 的载荷。</summary>
    public static List<string> Payloads(string raw)
    {
        var payloads = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
            var value = trimmed.Length > 5 && trimmed[5] == ' ' ? trimmed[6..] : trimmed[5..];
            if (value.Length > 0) payloads.Add(value);
        }
        return payloads;
    }
}
