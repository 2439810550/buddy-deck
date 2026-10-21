using System.Net;
using System.Text.Json.Nodes;
using BuddyDeck.TestSupport;
using Xunit;

namespace BuddyDeck.E2E.Tests;

/// <summary>
/// 正常聊天链路 —— 客户端 → 真 Kestrel → 网关 → 真 HttpClient → 假上游。
///
/// 这里的两条主线：
/// · **流式**：上游怎么吐，客户端就怎么收（不许被网关攒完再发）
/// · **非流式**：上游**只支持流式**，所以网关必须把整条流收完自己拼成
///   <c>chat.completion</c>（<c>ChatStreamCollector</c>）
///
/// 两者必须**同时**成立 —— 同一份上游流，两种客户端拿到的东西形状不同但内容一致。
/// </summary>
[Collection("GatewayHost")]
public class ChatE2ETests
{
    private const string UserAsk = """{"model":"hy3","messages":[{"role":"user","content":"你好"}]}""";

    private static string StreamBody(string model = "hy3", bool stream = true)
        => $$"""{"model":"{{model}}","messages":[{"role":"user","content":"你好"}],"stream":{{(stream ? "true" : "false")}}}""";

    // ── 流式 ──────────────────────────────────────────────────

    [Fact]
    public async Task 流式_用户侧是真_SSE_且以_DONE_收尾()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("你", "好", "呀"));

        using var response = await e2e.PostChatAsync(StreamBody(stream: true),
            completion: HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ★ 传输层契约：Content-Type 必须是 SSE
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
        Assert.Contains("text/event-stream", contentType, StringComparison.Ordinal);

        var body = await E2EHost.TextAsync(response);

        // ★ 结束标记：没有它，客户端会一直等下去
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);

        // 正文三片都在，且顺序正确
        Assert.Equal("你好呀", StreamScript.ConcatContent(body));
    }

    [Fact]
    public async Task 流式_首条_delta_声明_assistant_角色()
    {
        // OpenAI 客户端靠第一片认定「这是助手回复」。没有它，很多客户端
        // 会把整段内容当成未知角色丢掉。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("嗨"));

        using var response = await e2e.PostChatAsync(StreamBody(stream: true));
        var payloads = StreamScript.Payloads(await E2EHost.TextAsync(response));

        var first = Assert.IsType<JsonObject>(JsonNode.Parse(payloads[0]));

        Assert.Equal("chat.completion.chunk", first["object"]!.GetValue<string>());
        Assert.Equal("assistant", first["choices"]!.AsArray()[0]!["delta"]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task 流式_分块边界落在素材中间时依然能正确拼行()
    {
        // 网络上一次 read 拿到半行是常态。网关必须自己维护「还没成行的尾巴」，
        // 按块切的话这里就会拼出半条 JSON。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStreamSplit("跨", "块", "拼", "行"));

        using var response = await e2e.PostChatAsync(StreamBody(stream: true));
        var body = await E2EHost.TextAsync(response);

        Assert.Equal("跨块拼行", StreamScript.ConcatContent(body));
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 流式_按字节切断的流也能还原()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.ChoppedStream("逐字节切断", chunkSize: 5));

        using var response = await e2e.PostChatAsync(StreamBody(stream: true));
        var body = await E2EHost.TextAsync(response);

        Assert.Equal("逐字节切断", StreamScript.ConcatContent(body));
    }

    [Fact]
    public async Task 流式_清洗掉空的_function_call_但保留_reasoning()
    {
        await using var e2e = await E2EHost.StartAsync(_ => UpstreamResponses.SseFrames(
            [
                StreamScript.FrameWithEmptyFunctionCall("答"),
                StreamScript.StopFrame(),
            ]));

        using var response = await e2e.PostChatAsync(StreamBody(stream: true));
        var body = await E2EHost.TextAsync(response);

        // 空 function_call 会让某些客户端凭空多出一个并行调用 —— 必须删掉
        Assert.DoesNotContain("\"function_call\"", body, StringComparison.Ordinal);
        Assert.Equal("答", StreamScript.ConcatContent(body));
    }

    [Fact]
    public async Task 流式_usage_被裁成客户端认的三个字段()
    {
        // 上游的 usage 带了 credit / cached_tokens 之类的私有计数器。
        // 客户端只认三个 —— 多出来的会污染显示。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("好了"));

        using var response = await e2e.PostChatAsync(StreamBody(stream: true));
        var payloads = StreamScript.Payloads(await E2EHost.TextAsync(response));

        var stop = payloads
            .Select(p => p == "[DONE]" ? null : JsonNode.Parse(p))
            .First(n => n?["choices"]?.AsArray()[0]?["finish_reason"] is JsonValue v
                        && v.TryGetValue<string>(out var fr) && fr.Length > 0)!;

        var usage = Assert.IsType<JsonObject>(stop["usage"]);
        Assert.Equal(7, usage["prompt_tokens"]!.GetValue<int>());
        Assert.Equal(3, usage["completion_tokens"]!.GetValue<int>());
        Assert.Equal(10, usage["total_tokens"]!.GetValue<int>());
        Assert.False(usage.ContainsKey("credit"), "上游私有字段不该漏给客户端");
    }

    [Fact]
    public async Task 流式_分片里的_model_被改写成实际使用的模型()
    {
        // 换过模型重试时，客户端看到的模型名必须和最终生效的一致，不然对不上。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("嗯"));

        using var response = await e2e.PostChatAsync(StreamBody(model: "hy3", stream: true));
        var payloads = StreamScript.Payloads(await E2EHost.TextAsync(response));

        foreach (var payload in payloads.Where(p => p != "[DONE]"))
        {
            var node = JsonNode.Parse(payload)!;
            Assert.Equal("hy3", node["model"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task 流式_中文不被转义成_uXXXX()
    {
        // 默认的 JsonNode.ToJsonString() 会把中文转义。正文全是中文，
        // 转义之后客户端要解回来、抓包看着是一坨乱码。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("中文"));

        using var response = await e2e.PostChatAsync(StreamBody(stream: true));
        var body = await E2EHost.TextAsync(response);

        Assert.Contains("中文", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u4e", body, StringComparison.Ordinal);
    }

    // ── 非流式 ────────────────────────────────────────────────

    [Fact]
    public async Task 非流式_重组为_chat_completion()
    {
        // 上游只支持流式。客户端要非流式时，网关自己把流收完拼成整条。
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("拼", "起", "来"));

        using var response = await e2e.PostChatAsync(StreamBody(stream: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? "",
            StringComparison.Ordinal);

        var body = await E2EHost.JsonAsync(response);

        // ★ OpenAI 的形状
        Assert.Equal("chat.completion", body["object"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(body["id"]!.GetValue<string>()));

        var choice = body["choices"]!.AsArray()[0]!;
        Assert.Equal("assistant", choice["message"]!["role"]!.GetValue<string>());
        Assert.Equal("拼起来", choice["message"]!["content"]!.GetValue<string>());
        Assert.Equal("stop", choice["finish_reason"]!.GetValue<string>());

        // ★ usage 要保留（记账要用）
        var usage = Assert.IsType<JsonObject>(body["usage"]);
        Assert.Equal(10, usage["total_tokens"]!.GetValue<int>());
        // 且私有字段在这里是**保留**的（非流式是网关自己拼的，原值照搬）
        Assert.True(usage.ContainsKey("credit"), "非流式的 usage 应保留上游原值（含 credit）");
    }

    [Fact]
    public async Task 非流式_上游仍然收到_stream_true()
    {
        // 这是「非流式」能被实现的前提：网关转发给上游的 body 必须强制 stream=true，
        // 否则上游直接拒。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("好"));
        await using var e2e = await E2EHost.StartAsync(fake);

        await e2e.PostChatAsync(StreamBody(stream: false));

        var sent = Assert.Single(fake.Requests);
        Assert.Equal("/v2/chat/completions", sent.Path);
        Assert.True(sent.Json()!["stream"]!.GetValue<bool>(), "转发给上游的 stream 必须是 true");
    }

    [Fact]
    public async Task 非流式和流式_同一份上游流_内容一致()
    {
        // 两种路径各自走一遍同一段上游流，正文必须一模一样 ——
        // 不然「换客户端就换答案」这种 bug 会溜过去。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("一致", "性"));
        await using var e2e = await E2EHost.StartAsync(fake);

        using var streamed = await e2e.PostChatAsync(StreamBody(stream: true));
        var streamedText = StreamScript.ConcatContent(await E2EHost.TextAsync(streamed));

        using var whole = await e2e.PostChatAsync(StreamBody(stream: false));
        var wholeText = (await E2EHost.JsonAsync(whole))["choices"]!.AsArray()[0]!["message"]!["content"]!.GetValue<string>();

        Assert.Equal("一致性", streamedText);
        Assert.Equal(streamedText, wholeText);
    }

    [Fact]
    public async Task 非流式_推理内容被带上()
    {
        await using var e2e = await E2EHost.StartAsync(_ => UpstreamResponses.SseFrames(
            [
                StreamScript.RoleFrame(),
                StreamScript.ReasoningFrame("先想一下"),
                StreamScript.ContentFrame("答案是 4"),
                StreamScript.StopFrame(),
            ]));

        using var response = await e2e.PostChatAsync(StreamBody(stream: false));
        var message = (await E2EHost.JsonAsync(response))["choices"]!.AsArray()[0]!["message"]!;

        Assert.Equal("答案是 4", message["content"]!.GetValue<string>());
        Assert.Equal("先想一下", message["reasoning_content"]!.GetValue<string>());
    }

    [Fact]
    public async Task 非流式_模型名用的是实际发出的那个()
    {
        await using var e2e = await E2EHost.StartAsync(_ => StreamScript.TextStream("x"));

        using var response = await e2e.PostChatAsync(StreamBody(stream: false));
        var body = await E2EHost.JsonAsync(response);

        Assert.Equal("hy3", body["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task 非流式_未知模型落到默认模型且请求成功()
    {
        // 客户端里常常写死我们不认识的别名（gpt-4o / claude-3-5-sonnet…）。
        // 报 400 的话用户得去改客户端配置，落到默认模型至少能用。
        //
        // ⚠ 这条同时钉住一个**真 bug**（已报 gateway）：
        // 网关算出 resolvedModel 之后选号用的是它，但转发给上游的 body
        // 里仍然是客户端原始写法的 model。结果是「按 hy3 选号、却让上游跑 gpt-4o」，
        // 而且冷却记账按的是 hy3、实际被限的是 gpt-4o —— 两本账对不上。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("兜底"));
        await using var e2e = await E2EHost.StartAsync(fake);

        using var response = await e2e.PostChatAsync(StreamBody(model: "gpt-4o", stream: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hy3", fake.Last.Json()!["model"]!.GetValue<string>());
        Assert.Equal("hy3", (await E2EHost.JsonAsync(response))["model"]!.GetValue<string>());
    }

    // ── tool_calls ────────────────────────────────────────────

    private const string WeatherArguments = """{"city":"北京","unit":"celsius"}""";

    [Fact]
    public async Task tool_calls_非流式_name_正确_arguments_能解析_且_finish_reason_是_tool_calls()
    {
        await using var e2e = await E2EHost.StartAsync(
            _ => StreamScript.ToolCallStream("get_weather", WeatherArguments));

        using var response = await e2e.PostChatAsync(StreamBody(stream: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await E2EHost.JsonAsync(response);

        var choice = body["choices"]!.AsArray()[0]!;
        Assert.Equal("tool_calls", choice["finish_reason"]!.GetValue<string>());

        var calls = choice["message"]!["tool_calls"]!.AsArray();
        var call = Assert.Single(calls);

        Assert.Equal("function", call!["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(call["id"]!.GetValue<string>()), "tool_call 要有 id");
        Assert.Equal("get_weather", call["function"]!["name"]!.GetValue<string>());

        // ★ arguments 必须是**合法 JSON**（上游是分片发的，网关要原样相接）
        var arguments = call["function"]!["arguments"]!.GetValue<string>();
        var parsed = JsonNode.Parse(arguments);
        Assert.Equal("北京", parsed!["city"]!.GetValue<string>());
        Assert.Equal("celsius", parsed["unit"]!.GetValue<string>());
    }

    [Fact]
    public async Task tool_calls_非流式_正文为空时_content_是_null_而不是空串()
    {
        // OpenAI 的约定：只有工具调用、没有正文时 content 必须是 null。
        // 给空字符串会让某些客户端认为「模型说了一句空话」。
        await using var e2e = await E2EHost.StartAsync(
            _ => StreamScript.ToolCallStream("get_time", """{"tz":"Asia/Shanghai"}"""));

        using var response = await e2e.PostChatAsync(StreamBody(stream: false));
        var message = (await E2EHost.JsonAsync(response))["choices"]!.AsArray()[0]!["message"]!;

        Assert.True(message["content"] is null || message["content"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null,
            "有 tool_calls 且正文为空时 content 必须是 null");
        Assert.NotNull(message["tool_calls"]);
    }

    [Fact]
    public async Task tool_calls_非流式_空的_arguments_被补成空对象()
    {
        // 无参调用的 arguments 上游会给空串。空串不是合法 JSON，
        // 严格客户端解析会炸。
        await using var e2e = await E2EHost.StartAsync(_ => UpstreamResponses.SseFrames(
            [
                StreamScript.RoleFrame(),
                StreamScript.ToolCallStartFrame(0, "call_x", "list_files"),
                StreamScript.StopFrame("tool_calls"),
            ]));

        using var response = await e2e.PostChatAsync(StreamBody(stream: false));
        var call = (await E2EHost.JsonAsync(response))["choices"]!.AsArray()[0]!["message"]!["tool_calls"]!.AsArray()[0]!;

        var arguments = call["function"]!["arguments"]!.GetValue<string>();
        Assert.Equal("{}", arguments);
        Assert.NotNull(JsonNode.Parse(arguments));
    }

    [Fact]
    public async Task tool_calls_流式_五片参数能拼回合法_JSON()
    {
        await using var e2e = await E2EHost.StartAsync(
            _ => StreamScript.ToolCallStream("search_docs", """{"query":"网关重试","top_k":5}""", argumentParts: 5));

        using var response = await e2e.PostChatAsync(StreamBody(stream: true));
        var body = await E2EHost.TextAsync(response);

        var payloads = StreamScript.Payloads(body);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);

        // 参数碎片至少要有 2 片，否则「拼接」这件事根本没被测到
        var argumentFrames = payloads.Count(p => p.Contains("\"arguments\"", StringComparison.Ordinal));
        Assert.True(argumentFrames >= 2, $"参数应该被切成多片，实际 {argumentFrames} 片");

        Assert.Equal("search_docs", StreamScript.ConcatToolName(body));

        var arguments = StreamScript.ConcatToolArguments(body);
        var parsed = JsonNode.Parse(arguments);
        Assert.Equal("网关重试", parsed!["query"]!.GetValue<string>());
        Assert.Equal(5, parsed["top_k"]!.GetValue<int>());
    }

    [Fact]
    public async Task tool_calls_流式_id_和_type_在首片就出现()
    {
        // 客户端靠首片的 id 建累积器；id 来晚了会把两段参数拼到一个调用上。
        await using var e2e = await E2EHost.StartAsync(
            _ => StreamScript.ToolCallStream("get_weather", WeatherArguments));

        using var response = await e2e.PostChatAsync(StreamBody(stream: true));
        var payloads = StreamScript.Payloads(await E2EHost.TextAsync(response));

        var withToolCall = payloads
            .Select(p => JsonNode.Parse(p))
            .First(n => n?["choices"]?.AsArray()[0]?["delta"]?["tool_calls"] is JsonArray);

        var call = withToolCall!["choices"]!.AsArray()[0]!["delta"]!["tool_calls"]!.AsArray()[0]!;
        Assert.False(string.IsNullOrWhiteSpace(call["id"]!.GetValue<string>()));
        Assert.Equal("function", call["type"]!.GetValue<string>());
        Assert.Equal("get_weather", call["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task tool_calls_非流式_两个并行调用按_index_各归各位()
    {
        // 两个调用交错发：先起 call 0 的名字，再插 call 1 的参数，
        // 再起 call 1 的名字，最后补 call 0 的参数。
        // 网关必须**按 index** 归位 —— 顺序拼接的话两个调用的参数会串到一起。
        await using var e2e = await E2EHost.StartAsync(_ => UpstreamResponses.SseFrames(
            [
                StreamScript.RoleFrame(),
                StreamScript.ToolCallStartFrame(0, "call_a", "get_weather"),
                StreamScript.ToolCallArgsFrame(1, "{\"city\":\"上海\"}"),
                StreamScript.ToolCallStartFrame(1, "call_b", "get_time"),
                StreamScript.ToolCallArgsFrame(0, "{\"city\":\"北京\"}"),
                StreamScript.StopFrame("tool_calls"),
            ]));

        using var response = await e2e.PostChatAsync(StreamBody(stream: false));
        var calls = (await E2EHost.JsonAsync(response))["choices"]!.AsArray()[0]!["message"]!["tool_calls"]!.AsArray();

        Assert.Equal(2, calls.Count);

        // index 0 → get_weather + 北京
        Assert.Equal("get_weather", calls[0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("北京", JsonNode.Parse(calls[0]!["function"]!["arguments"]!.GetValue<string>())!["city"]!.GetValue<string>());

        // index 1 → get_time + 上海
        Assert.Equal("get_time", calls[1]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("上海", JsonNode.Parse(calls[1]!["function"]!["arguments"]!.GetValue<string>())!["city"]!.GetValue<string>());
    }
}
