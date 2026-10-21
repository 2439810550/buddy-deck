using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// OpenAI Responses 翻译层的测试（t33 / M2 —— Codex 接入前提）。
///
/// ## 分两类（与 AnthropicEndpointTests 同构）
///
/// **第一类：纯翻译函数**（`TranslateRequest` / `TranslateResponse` /
/// `OutputItems` / `OutputText`）—— 直接用 `internal static` 方法测。
/// **绝大多数映射错误在这里就能钉死，不需要起宿主。**
///
/// **第二类：端到端**（起真 Kestrel + 假上游）—— 验的是「装配与真实路径」。
/// 它才能抓到「翻译对了但没挂上」这类缺口。
///
/// ⚠ 但**这两层都不足以证明第 ③ 层**（`Program.cs` 里那一行注册）。
/// 本文件的端到端用例是**自己 new + 手写分派**的，所以即使注册行被删掉，
/// 它们照样全绿 —— 这正是本项目的三次装配事故的形状。
/// 第 ③ 层的证据是**真网关 curl**，写在交付回报里，不在这个文件里。
/// </summary>
public class ResponsesEndpointTests
{
    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>回显用的空 options（多数纯翻译用例不关心 tools）。</summary>
    private static (JsonNode Tools, JsonNode ToolChoice) NoEcho() => (new JsonArray(), JsonValue.Create("auto")!);

    // ══════════════════════════════════════════════════════════
    // ① 请求翻译：instructions / input / 角色归一
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void instructions_抽离成首条_system_消息()
    {
        var body = Parse("""
        {"model":"hy3","instructions":"你是 Codex。","input":"你好"}
        """);

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");
        var messages = chat["messages"]!.AsArray();

        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("你是 Codex。", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void 空的_instructions_不产生_system_消息()
    {
        // 空白 instructions 加进去会让上游看到一条空洞的 system —— 语义上没变但脏
        var body = Parse("""{"model":"hy3","instructions":"   ","input":"你好"}""");

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");
        var messages = chat["messages"]!.AsArray();

        Assert.Single(messages);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void input_是字符串_直接当_user_消息()
    {
        var body = Parse("""{"model":"hy3","input":"直接一句话"}""");

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");

        Assert.Equal("user", chat["messages"]!.AsArray()[0]!["role"]!.GetValue<string>());
        Assert.Equal("直接一句话", chat["messages"]!.AsArray()[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void input_数组里的_message_项被翻译()
    {
        var body = Parse("""
        {"model":"hy3","input":[
          {"type":"message","role":"user","content":[{"type":"input_text","text":"看看这个"}]}
        ]}
        """);

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");
        var msg = chat["messages"]!.AsArray()[0]!;

        Assert.Equal("user", msg["role"]!.GetValue<string>());
        // 单个文本 part 拍平成字符串
        Assert.Equal("看看这个", msg["content"]!.GetValue<string>());
    }

    [Fact]
    public void developer_角色归一到_system()
    {
        // ★ Responses 用 developer 表示「系统级指令」，Chat 只认 system。
        //   不归一的话上游会拒（不认识的 role）。
        var body = Parse("""
        {"model":"hy3","input":[
          {"type":"message","role":"developer","content":[{"type":"input_text","text":"守规矩"}]}
        ]}
        """);

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");

        Assert.Equal("system", chat["messages"]!.AsArray()[0]!["role"]!.GetValue<string>());
        Assert.Equal("守规矩", chat["messages"]!.AsArray()[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void input_image_带_url_翻成_image_url()
    {
        var body = Parse("""
        {"model":"hy3","input":[
          {"type":"message","role":"user","content":[
            {"type":"input_text","text":"这是什么"},
            {"type":"input_image","image_url":"https://example.test/a.png"}
          ]}
        ]}
        """);

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");
        var parts = chat["messages"]!.AsArray()[0]!["content"]!.AsArray();

        Assert.Equal("image_url", parts[1]!["type"]!.GetValue<string>());
        Assert.Equal("https://example.test/a.png",
            parts[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void input_image_只有_file_id_时占位而不是丢掉()
    {
        // ⚠ 上游拿不到我们上传的文件。**占个位比整条消息丢掉强** ——
        //   丢掉会让用户彻底看不到那张图存在过（对齐旧版 responses.js:41-44）。
        var body = Parse("""
        {"model":"hy3","input":[
          {"type":"message","role":"user","content":[
            {"type":"input_image","file_id":"file-abc"}
          ]}
        ]}
        """);

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");

        // ⚠ 只剩一个 text part 时会被**拍平成字符串**（上游对字符串形态支持最好），
        //   所以这里不能无条件 `AsArray()` —— 两种形态都要认。
        var content = chat["messages"]!.AsArray()[0]!["content"]!;
        var placeholder = content is JsonValue plain
            ? plain.GetValue<string>()
            : content.AsArray()[0]!["text"]!.GetValue<string>();

        Assert.Contains("file-abc", placeholder, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════
    // ② 请求翻译：工具调用（最容易翻错的一处）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 并行_function_call_并进同一条_assistant_消息()
    {
        // ★★ 这是 Responses → Chat 最容易翻错的一处。
        //    Responses 把每个并行调用发成**独立 item**，Chat 要求它们在
        //    **同一条** assistant 的 tool_calls 数组里。
        //    翻成两条 assistant 的话，上游会拒（或只认最后一个调用）。
        var body = Parse("""
        {"model":"hy3","input":[
          {"type":"function_call","call_id":"call_1","name":"read_file","arguments":"{\"p\":\"a\"}"},
          {"type":"function_call","call_id":"call_2","name":"read_file","arguments":"{\"p\":\"b\"}"}
        ]}
        """);

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");
        var messages = chat["messages"]!.AsArray();

        Assert.Single(messages);   // ← 必须是一条，不是两条
        Assert.Equal("assistant", messages[0]!["role"]!.GetValue<string>());

        var calls = messages[0]!["tool_calls"]!.AsArray();
        Assert.Equal(2, calls.Count);
        Assert.Equal("call_1", calls[0]!["id"]!.GetValue<string>());
        Assert.Equal("call_2", calls[1]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void function_call_output_翻成_role_tool_消息()
    {
        var body = Parse("""
        {"model":"hy3","input":[
          {"type":"function_call","call_id":"call_1","name":"read_file","arguments":"{}"},
          {"type":"function_call_output","call_id":"call_1","output":"文件内容"}
        ]}
        """);

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");
        var messages = chat["messages"]!.AsArray();

        Assert.Equal(2, messages.Count);
        Assert.Equal("tool", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", messages[1]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("文件内容", messages[1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void function_call_output_的_output_是数组时拼成文本()
    {
        var body = Parse("""
        {"model":"hy3","input":[
          {"type":"function_call","call_id":"c1","name":"f","arguments":"{}"},
          {"type":"function_call_output","call_id":"c1","output":[
            {"type":"output_text","text":"第一行"},
            {"type":"output_text","text":"第二行"}
          ]}
        ]}
        """);

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");
        var content = chat["messages"]!.AsArray()[1]!["content"]!.GetValue<string>();

        Assert.Contains("第一行", content, StringComparison.Ordinal);
        Assert.Contains("第二行", content, StringComparison.Ordinal);
    }

    [Fact]
    public void function_call_output_的_output_是对象时序列化()
    {
        // output 可以是任意 JSON —— 直接 JSON.stringify（对齐旧版 responses.js:61）
        Assert.Equal("""{"a":1}""", ResponsesEndpoints.OutputText(Parse("""{"a":1}""")));
    }

    [Fact]
    public void reasoning_项被丢弃而不是报错()
    {
        // Responses 的 reasoning 项上游不认。**丢掉而不是报错** ——
        // 报错会让整条消息发不出去（客户端发的 item 类型常比我们认识的多）。
        var body = Parse("""
        {"model":"hy3","input":[
          {"type":"reasoning","summary":[]},
          {"type":"message","role":"user","content":[{"type":"input_text","text":"在"}]}
        ]}
        """);

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");
        var messages = chat["messages"]!.AsArray();

        Assert.Equal(1, messages.Count);
        Assert.Equal("在", messages[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void 扁平工具翻成嵌套_function_形状()
    {
        // ★ Responses 的 name 在**顶层**，Chat 的嵌在 function 里。
        var body = Parse("""
        {"model":"hy3","input":"hi","tools":[
          {"type":"function","name":"shell","description":"跑命令",
           "parameters":{"type":"object","properties":{"cmd":{"type":"string"}}}}
        ]}
        """);

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");
        var tool = chat["tools"]!.AsArray()[0]!;

        Assert.Equal("function", tool["type"]!.GetValue<string>());
        Assert.Equal("shell", tool["function"]!["name"]!.GetValue<string>());
        Assert.Equal("跑命令", tool["function"]!["description"]!.GetValue<string>());
        Assert.Equal("object", tool["function"]!["parameters"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void 非_function_工具被丢弃()
    {
        // web_search 之类服务端工具上游不认 —— 留着会让整个请求被拒
        var body = Parse("""
        {"model":"hy3","input":"hi","tools":[
          {"type":"web_search"},
          {"type":"function","name":"shell","parameters":{"type":"object"}}
        ]}
        """);

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");

        Assert.Single(chat["tools"]!.AsArray());
        Assert.Equal("shell", chat["tools"]!.AsArray()[0]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void 没有_parameters_的工具补一个空_schema()
    {
        // 上游对缺失的 parameters 会拒，而工具的语义本来就允许无参
        var body = Parse("""
        {"model":"hy3","input":"hi","tools":[{"type":"function","name":"now"}]}
        """);

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");
        var parameters = chat["tools"]!.AsArray()[0]!["function"]!["parameters"]!;

        Assert.Equal("object", parameters["type"]!.GetValue<string>());
    }

    [Fact]
    public void tool_choice_字符串直传()
    {
        var body = Parse("""{"model":"hy3","input":"hi","tool_choice":"required"}""");

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");

        Assert.Equal("required", chat["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public void tool_choice_指名函数的形状被改写()
    {
        // Responses: {type:"function",name:"x"} → Chat: {type:"function",function:{name:"x"}}
        var body = Parse("""
        {"model":"hy3","input":"hi","tool_choice":{"type":"function","name":"shell"}}
        """);

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");

        Assert.Equal("function", chat["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Equal("shell", chat["tool_choice"]!["function"]!["name"]!.GetValue<string>());
    }

    // ══════════════════════════════════════════════════════════
    // ③ 请求翻译：采样参数（静默丢弃的坑）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void max_output_tokens_变成_max_tokens_且整数背书也能读到()
    {
        // ⚠ 这是本项目踩过两次的坑：JSON 数值按**最窄类型**背书，
        //   `{"max_output_tokens":4096}` 里 4096 是 int，
        //   只试 `TryGetValue<long>` 会返回 false **且不抛异常** ——
        //   于是参数被静默丢弃，客户端设了却不生效。
        var body = Parse("""{"model":"hy3","input":"hi","max_output_tokens":4096}""");

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");

        Assert.Equal(4096, Int(chat["max_tokens"]));
    }

    [Fact]
    public void 采参数为浮点与整数两种背书都能读到()
    {
        var floats = ResponsesEndpoints.TranslateRequest(
            Parse("""{"model":"hy3","input":"hi","temperature":0.7,"top_p":0.9}"""), "hy3");
        Assert.Equal(0.7, floats["temperature"]!.GetValue<double>(), 6);
        Assert.Equal(0.9, floats["top_p"]!.GetValue<double>(), 6);

        // 整数书写的 1 是同一种参数的合法取值，不能被当成「没给」
        var ints = ResponsesEndpoints.TranslateRequest(
            Parse("""{"model":"hy3","input":"hi","temperature":1}"""), "hy3");
        Assert.Equal(1.0, ints["temperature"]!.GetValue<double>(), 6);
    }

    [Fact]
    public void stream_标志按原样翻()
    {
        Assert.False(ResponsesEndpoints.TranslateRequest(Parse("""{"model":"hy3","input":"hi"}"""), "hy3")["stream"]!.GetValue<bool>());
        Assert.True(ResponsesEndpoints.TranslateRequest(Parse("""{"model":"hy3","input":"hi","stream":true}"""), "hy3")["stream"]!.GetValue<bool>());
    }

    [Fact]
    public void 模型名被改写为解析后的那个()
    {
        // t11 的教训：客户端写死别名时，请求体里的 model 必须跟着换成实际用的
        var body = Parse("""{"model":"gpt-5-codex","input":"hi"}""");

        var chat = ResponsesEndpoints.TranslateRequest(body, "hy3");

        Assert.Equal("hy3", chat["model"]!.GetValue<string>());
    }

    // ══════════════════════════════════════════════════════════
    // ④ 响应翻译：output item 的形状
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 非流式_回_response_文档形状()
    {
        var completion = Parse("""
        {"id":"c1","object":"chat.completion","model":"hy3",
         "choices":[{"index":0,"message":{"role":"assistant","content":"你好"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":10,"completion_tokens":2}}
        """);

        var response = ResponsesEndpoints.TranslateResponse(completion, "hy3", NoEcho());

        Assert.Equal("response", response["object"]!.GetValue<string>());
        Assert.Equal("completed", response["status"]!.GetValue<string>());
        Assert.Equal("hy3", response["model"]!.GetValue<string>());
        Assert.StartsWith("resp_", response["id"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(response["error"]);
        Assert.Null(response["incomplete_details"]);

        var item = response["output"]!.AsArray()[0]!;
        Assert.Equal("message", item["type"]!.GetValue<string>());
        Assert.Equal("completed", item["status"]!.GetValue<string>());
        Assert.Equal("assistant", item["role"]!.GetValue<string>());
        Assert.Equal("output_text", item["content"]!.AsArray()[0]!["type"]!.GetValue<string>());
        Assert.Equal("你好", item["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void 非流式_usage_三个字段与_details_齐全()
    {
        var completion = Parse("""
        {"choices":[{"message":{"content":"x"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":10,"completion_tokens":2}}
        """);

        var response = ResponsesEndpoints.TranslateResponse(completion, "hy3", NoEcho());
        var usage = response["usage"]!;

        Assert.Equal(10, Int(usage["input_tokens"]));
        Assert.Equal(2, Int(usage["output_tokens"]));
        Assert.Equal(12, Int(usage["total_tokens"]));
        // 这两个 details 是 Responses 的必有字段 —— 缺了 Codex 的用量面板会崩
        Assert.Equal(0, Int(usage["input_tokens_details"]!["cached_tokens"]));
        Assert.Equal(0, Int(usage["output_tokens_details"]!["reasoning_tokens"]));
    }

    [Fact]
    public void 非流式_整数背书的_token_不会被读成零()
    {
        // 同 ③ 的坑：`{"prompt_tokens": 2541}` 的 2541 是 int 背书。
        // 读不出来时**不报错**，就是全 0 —— 这是 UsageLedger 那个坑的同一个形状。
        var completion = Parse("""
        {"choices":[{"message":{"content":"x"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2541,"completion_tokens":137}}
        """);

        var response = ResponsesEndpoints.TranslateResponse(completion, "hy3", NoEcho());

        Assert.Equal(2541, Int(response["usage"]!["input_tokens"]));
        Assert.Equal(137, Int(response["usage"]!["output_tokens"]));
    }

    [Fact]
    public void finish_reason_length_变成_incomplete_而不是_completed()
    {
        // ★ 报成 completed 的话 Codex 会以为回答说完了 —— 静默截断。
        var completion = Parse("""
        {"choices":[{"message":{"content":"半句"},"finish_reason":"length"}]}
        """);

        var response = ResponsesEndpoints.TranslateResponse(completion, "hy3", NoEcho());

        Assert.Equal("incomplete", response["status"]!.GetValue<string>());
        Assert.Equal("max_output_tokens", response["incomplete_details"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public void reasoning_content_变成独立的_reasoning_项且排在最前()
    {
        var completion = Parse("""
        {"choices":[{"message":{"content":"答案","reasoning_content":"想一下"},"finish_reason":"stop"}]}
        """);

        var response = ResponsesEndpoints.TranslateResponse(completion, "hy3", NoEcho());
        var output = response["output"]!.AsArray();

        Assert.Equal(2, output.Count);
        Assert.Equal("reasoning", output[0]!["type"]!.GetValue<string>());
        Assert.Equal("summary_text", output[0]!["summary"]!.AsArray()[0]!["type"]!.GetValue<string>());
        Assert.Equal("想一下", output[0]!["summary"]!.AsArray()[0]!["text"]!.GetValue<string>());
        Assert.Equal("message", output[1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void tool_calls_变成_function_call_项()
    {
        var completion = Parse("""
        {"choices":[{"message":{"role":"assistant","content":null,
          "tool_calls":[{"id":"call_1","type":"function",
            "function":{"name":"shell","arguments":"{\"cmd\":\"ls\"}"}}]},
          "finish_reason":"tool_calls"}]}
        """);

        var response = ResponsesEndpoints.TranslateResponse(completion, "hy3", NoEcho());
        var item = response["output"]!.AsArray()[0]!;

        Assert.Equal("function_call", item["type"]!.GetValue<string>());
        Assert.Equal("call_1", item["call_id"]!.GetValue<string>());
        Assert.Equal("shell", item["name"]!.GetValue<string>());
        Assert.Equal("""{"cmd":"ls"}""", item["arguments"]!.GetValue<string>());
        Assert.Equal("completed", item["status"]!.GetValue<string>());
    }

    [Fact]
    public void 空回复补一个空_message_项而不是空数组()
    {
        // ★ `output: []` 是坏结构 —— Codex 会以为响应坏了
        var completion = Parse("""{"choices":[{"message":{"content":""},"finish_reason":"stop"}]}""");

        var response = ResponsesEndpoints.TranslateResponse(completion, "hy3", NoEcho());
        var output = response["output"]!.AsArray();

        Assert.Equal(1, output.Count);
        Assert.Equal("message", output[0]!["type"]!.GetValue<string>());
        Assert.Equal("", output[0]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void tools_与_tool_choice_回显原始请求值()
    {
        // ⚠ 回显的是**原始请求值**（Responses 形状），不是翻译后的 Chat 形状 ——
        //   Codex 看到的是它自己的协议，给它 Chat 形状会解析不了。
        var request = Parse("""
        {"model":"hy3","input":"hi","tool_choice":"required",
         "tools":[{"type":"function","name":"shell","parameters":{"type":"object"}}]}
        """);
        var echo = ResponsesEndpoints.EchoOptions(request);

        var response = ResponsesEndpoints.TranslateResponse(
            Parse("""{"choices":[{"message":{"content":"x"},"finish_reason":"stop"}]}"""), "hy3", echo);

        // tools 保持扁平（name 在顶层），不是嵌进 function
        Assert.Equal("shell", response["tools"]!.AsArray()[0]!["name"]!.GetValue<string>());
        Assert.Equal("required", response["tool_choice"]!.GetValue<string>());
        Assert.True(response["parallel_tool_calls"]!.GetValue<bool>());
    }

    [Fact]
    public void tool_choice_缺省回显_auto()
    {
        var echo = ResponsesEndpoints.EchoOptions(Parse("""{"model":"hy3","input":"hi"}"""));

        Assert.Equal("auto", echo.ToolChoice.GetValue<string>());
        Assert.Empty(echo.Tools.AsArray());
    }

    [Fact]
    public void 上游一个_choice_都没有时不崩()
    {
        var response = ResponsesEndpoints.TranslateResponse(Parse("""{}"""), "hy3", NoEcho());

        Assert.Equal("completed", response["status"]!.GetValue<string>());
        Assert.Single(response["output"]!.AsArray());
    }

    [Fact]
    public void 生成的_id_前缀可区分类型()
    {
        Assert.StartsWith("resp_", ResponsesEndpoints.NewId("resp"), StringComparison.Ordinal);
        Assert.StartsWith("msg_", ResponsesEndpoints.NewId("msg"), StringComparison.Ordinal);
        Assert.StartsWith("fc_", ResponsesEndpoints.NewId("fc"), StringComparison.Ordinal);
        Assert.StartsWith("call_", ResponsesEndpoints.NewId("call"), StringComparison.Ordinal);

        // 不重复（同一个毫秒内也要不同）
        Assert.NotEqual(ResponsesEndpoints.NewId("msg"), ResponsesEndpoints.NewId("msg"));
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ 端到端：装配与真实路径
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 端到端_非流式_回_response_形状()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"你好"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2}}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync(
            """{"model":"hy3","instructions":"你是助手","input":"hi"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.Equal("response", json["object"]!.GetValue<string>());
        Assert.Equal("completed", json["status"]!.GetValue<string>());
        Assert.Equal("你好",
            json["output"]!.AsArray()[0]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task 端到端_非流式_中文没被转义()
    {
        // ⚠ observability 报的坑：**流式**响应经 SseSanitizer 重新序列化会把
        //   中文变 \uXXXX。所以断言中文内容要用**非流式 + 解析 JSON 字段**，
        //   这里正是这么做的（上一条第 853 行那条断言）。
        //   本条额外直白地钉住「原文里就是中文字符」。
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"中文内容"},"finish_reason":"stop"}]}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi"}""");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Contains("中文内容", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u4e2d", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 端到端_发给上游的是_Chat_形状()
    {
        // ★ 端到端级：不只翻译函数对，**真实发出去的那一份**也是 Chat 形状。
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync(
            """{"model":"hy3","instructions":"系统提示","input":"用户输入","max_output_tokens":256}""");
        await response.Content.ReadAsStringAsync();

        var sent = Parse(fake.Requests.Single().Body!);

        // 发出去的是 chat 形状：messages 数组 + max_tokens（不是 max_output_tokens）
        Assert.Equal(2, sent["messages"]!.AsArray().Count);
        Assert.Equal("system", sent["messages"]!.AsArray()[0]!["role"]!.GetValue<string>());
        Assert.Equal("系统提示", sent["messages"]!.AsArray()[0]!["content"]!.GetValue<string>());
        Assert.Equal(256, Int(sent["max_tokens"]));
        Assert.Null(sent["max_output_tokens"]);
        Assert.Null(sent["instructions"]);
        Assert.Null(sent["input"]);
    }

    [Fact]
    public async Task 端到端_上游收到的是流式请求()
    {
        // 上游只支持流式 —— RequestPipeline 会把 stream 强制改成 true
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi"}""");
        await response.Content.ReadAsStringAsync();

        Assert.True(Parse(fake.Requests.Single().Body!)["stream"]!.GetValue<bool>());
    }

    // ══════════════════════════════════════════════════════════
    // ⑥ 端到端：流式事件语义（与 chat/completions 的根本差别）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 端到端_流式_按_Responses_命名事件输出()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"你"},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"好"},"finish_reason":"stop"}],"usage":{"completion_tokens":2}}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi","stream":true}""");
        var text = await response.Content.ReadAsStringAsync();

        // ★ 事件名必须是 Responses 那一套 —— **不是** chat/completions 的
        //   匿名 `data: {"choices":[{"delta":…}]}`。
        Assert.Contains("event: response.created", text, StringComparison.Ordinal);
        Assert.Contains("event: response.in_progress", text, StringComparison.Ordinal);
        Assert.Contains("event: response.output_item.added", text, StringComparison.Ordinal);
        Assert.Contains("event: response.content_part.added", text, StringComparison.Ordinal);
        Assert.Contains("event: response.output_text.delta", text, StringComparison.Ordinal);
        Assert.Contains("event: response.output_text.done", text, StringComparison.Ordinal);
        Assert.Contains("event: response.content_part.done", text, StringComparison.Ordinal);
        Assert.Contains("event: response.output_item.done", text, StringComparison.Ordinal);
        Assert.Contains("event: response.completed", text, StringComparison.Ordinal);

        // ⚠ 反证：这一族**绝不能**出现 chat/completions 的 delta 形状。
        //   有的话说明翻译层被绕过了。
        Assert.DoesNotContain("\"choices\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("delta\":{\"content", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 端到端_流式_created_必须第一个且只发一次()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"x"},"finish_reason":"stop"}]}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi","stream":true}""");
        var text = await response.Content.ReadAsStringAsync();

        // Codex 靠 response.created 拿 response.id，缺了整条流解析不了
        Assert.Equal(0, text.IndexOf("event: response.created", StringComparison.Ordinal));
        Assert.Equal(1, CountOccurrences(text, "event: response.created"));
        Assert.Equal(1, CountOccurrences(text, "event: response.in_progress"));
        Assert.Equal(1, CountOccurrences(text, "event: response.completed"));
    }

    [Fact]
    public async Task 端到端_流式_每个事件都带递增的_sequence_number()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"a"},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"b"},"finish_reason":"stop"}]}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi","stream":true}""");
        var text = await response.Content.ReadAsStringAsync();

        var numbers = ExtractSequenceNumbers(text);

        Assert.NotEmpty(numbers);
        // 从 0 开始、单调递增、不跳号 —— Codex 用它检测丢帧
        Assert.Equal(0, numbers[0]);
        for (var i = 1; i < numbers.Count; i++)
        {
            Assert.Equal(numbers[i - 1] + 1, numbers[i]);
        }
    }

    [Fact]
    public async Task 端到端_流式_output_index_从零连续且成对开关()
    {
        // ★★ 这是 Responses 事件语义的**核心约束**：
        //    index 必须从 0 连续，且**上一项 done 之后才能开下一项**。
        //    错位的话 Codex 解析就乱了。
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"文本"},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"shell","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi","stream":true}""");
        var text = await response.Content.ReadAsStringAsync();

        // added / done 必须成对，且 added 在 done 之前
        Assert.Equal(2, CountOccurrences(text, "event: response.output_item.added"));
        Assert.Equal(2, CountOccurrences(text, "event: response.output_item.done"));

        // 每个 done 之后（下一个 added 之前）index 必须已经关掉 ——
        // 用「added(i) 出现在 done(i-1) 之后」来钉住顺序
        var addedIndexes = ExtractIntFields(text, "response.output_item.added", "output_index");
        var doneIndexes = ExtractIntFields(text, "response.output_item.done", "output_index");

        Assert.Equal([0, 1], addedIndexes);
        Assert.Equal([0, 1], doneIndexes);

        // 顺序：added(0) < done(0) < added(1) < done(1)
        var iAdded0 = text.IndexOf("event: response.output_item.added", StringComparison.Ordinal);
        var iDone0 = IndexOfNth(text, "event: response.output_item.done", 1);
        var iAdded1 = IndexOfNth(text, "event: response.output_item.added", 2);
        var iDone1 = IndexOfNth(text, "event: response.output_item.done", 2);

        Assert.True(iAdded0 < iDone0, "added(0) 必须在 done(0) 之前");
        Assert.True(iDone0 < iAdded1, "done(0) 必须在 added(1) 之前（上一项关掉才能开下一项）");
        Assert.True(iAdded1 < iDone1, "added(1) 必须在 done(1) 之前");
    }

    [Fact]
    public async Task 端到端_流式_文本_delta_带_item_id_与_content_index()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"内容"},"finish_reason":"stop"}]}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi","stream":true}""");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"delta\":\"内容\"", text, StringComparison.Ordinal);
        Assert.Contains("\"content_index\":0", text, StringComparison.Ordinal);
        Assert.Contains("\"item_id\":\"msg_", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 端到端_流式_文本分片累加到_done_的完整文本()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"第一"},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"第二"},"finish_reason":"stop"}]}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi","stream":true}""");
        var text = await response.Content.ReadAsStringAsync();

        // output_text.done 带**完整**文本（不是最后一片）
        var done = ExtractTextAfter(text, "response.output_text.done");
        Assert.Contains("\"text\":\"第一第二\"", done, StringComparison.Ordinal);

        // completed 里的 output item 也是完整文本
        var completed = ExtractAfterEvent(text, "response.completed");
        Assert.Contains("第一第二", completed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 端到端_流式_function_call_用_arguments_delta()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"shell","arguments":""}}]},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"cmd\""}}]},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":":\"ls\"}"}}]},"finish_reason":"tool_calls"}]}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi","stream":true}""");
        var text = await response.Content.ReadAsStringAsync();

        // ⚠ 事件名是 function_call_arguments.delta，**不是** content_block_delta
        //   （那是 Anthropic 的）。这是两个翻译层最直观的差别之一。
        Assert.Contains("event: response.function_call_arguments.delta", text, StringComparison.Ordinal);
        Assert.Contains("event: response.function_call_arguments.done", text, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"shell\"", text, StringComparison.Ordinal);

        // done 里的 arguments 是**拼好的完整 JSON**（分片要原样相接）
        var done = ExtractAfterEvent(text, "response.function_call_arguments.done");
        Assert.Contains("""{\"cmd\":\"ls\"}""", done, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 端到端_流式_tool_call_带_call_id()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_abc","type":"function","function":{"name":"f","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi","stream":true}""");
        var text = await response.Content.ReadAsStringAsync();

        // call_id 是 Codex 回传 function_call_output 时用的键，必须有
        Assert.Contains("\"call_id\":\"call_abc\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 端到端_流式_finish_reason_length_时_completed_变成_incomplete()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"半句"},"finish_reason":"length"}]}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi","stream":true}""");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("event: response.incomplete", text, StringComparison.Ordinal);
        Assert.DoesNotContain("event: response.completed", text, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"max_output_tokens\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 端到端_流式_空回复也要有一个_message_项()
    {
        // 上游一个 delta 都没有 —— 必须补一个空 message 项，
        // `output: []` 会让 Codex 认为响应结构坏掉。
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":"stop"}]}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi","stream":true}""");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(1, CountOccurrences(text, "event: response.output_item.added"));
        Assert.Contains("\"type\":\"message\"", text, StringComparison.Ordinal);
        Assert.Contains("event: response.completed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 端到端_流式_completed_带完整_output_与_usage()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"你好"},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":3}}"""));

        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi","stream":true}""");
        var text = await response.Content.ReadAsStringAsync();

        var completed = ExtractAfterEvent(text, "response.completed");

        Assert.Contains("\"status\":\"completed\"", completed, StringComparison.Ordinal);
        Assert.Contains("\"output_tokens\":3", completed, StringComparison.Ordinal);
        Assert.Contains("你好", completed, StringComparison.Ordinal);

        // in_progress 阶段 usage 必须是 null（在跑的时候报用量是错的）
        var firstFrame = text[..text.IndexOf("\n\n", StringComparison.Ordinal)];
        Assert.Contains("\"usage\":null", firstFrame, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════
    // ⑦ 端到端：错误信封（形状与 chat/completions 相近但不全同）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 端到端_上游认证失败回_Responses_错误信封()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Unauthorized("bad token"));
        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var json = Parse(await response.Content.ReadAsStringAsync());
        var error = json["error"]!.AsObject();

        Assert.Equal("authentication_error", error["type"]!.GetValue<string>());
        Assert.Equal("authentication_error", error["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(error["message"]!.GetValue<string>()));

        // ★ `param` 是 Responses 信封比 chat/completions 多的那个字段
        Assert.True(error.ContainsKey("param"));
        Assert.Null(error["param"]);

        // 顶层**没有** error 之外的壳（不是 Anthropic 的 {type:"error",...}）
        Assert.Null(json["type"]);
    }

    [Fact]
    public async Task 端到端_401_时_error_type_是_authentication_error()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Unauthorized());
        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi"}""");
        var json = Parse(await response.Content.ReadAsStringAsync());

        // ★ 类型名走 **OpenAI** 的命名，**不是** Anthropic 的
        //   （Anthropic 那边同一个失败叫 authentication_error，恰好同名；
        //    但限流那条不一样：Responses 是 rate_limit_exceeded，
        //    Anthropic 是 rate_limit_error —— 见下一条用例）
        Assert.Equal("authentication_error", json["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task 端到端_畸形_JSON_回_400()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse("{}"));
        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.PostResponsesAsync("{不是 JSON");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request_error", json["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task 端到端_GET_请求回_405()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse("{}"));
        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.Client.GetAsync("/v1/responses");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        var json = Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json["error"]!.AsObject().ContainsKey("param"));
    }

    [Fact]
    public async Task 端到端_不是本族路径返回_404_让网关继续分派()
    {
        // 「就地接管」契约：不是我的前缀必须**一个字节都不写**地返回 false。
        var fake = new FakeUpstreamHandler(_ => UpstreamSse("{}"));
        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.Client.PostAsync("/v1/chat/completions",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 端到端_前缀更长的近似路径不被吃掉()
    {
        // ⚠ 本族用的是**精确匹配**（string.Equals）而不是 StartsWith ——
        //   所以 `/v1/responses/sub` 不该被它处理。
        //   （对比 Anthropic 那边：`/v1/messages/count_tokens` 是它有意管的子路径）
        var fake = new FakeUpstreamHandler(_ => UpstreamSse("{}"));
        await using var host = await ResponsesHost.StartAsync(fake);

        using var response = await host.Client.PostAsync("/v1/responses/not-a-real-subpath",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ══════════════════════════════════════════════════════════
    // ⑧ 端到端：重试与没号
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 端到端_上游_429_后换号重试成功()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.TooManyRequests())
            .Script(UpstreamResponses.TooManyRequests(), UpstreamSse(
                """{"id":"c1","choices":[{"index":0,"delta":{"content":"第二次成功"},"finish_reason":"stop"}]}"""));

        await using var host = await ResponsesHost.StartAsync(fake, accounts: 2);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("第二次成功",
            json["output"]!.AsArray()[0]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task 端到端_所有号都限流时回_429_且类型是_rate_limit_exceeded()
    {
        // ★ 类型名与 Anthropic 那族**刻意不同**：
        //   Responses 用 OpenAI 命名 rate_limit_exceeded，
        //   Anthropic 用 rate_limit_error。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.TooManyRequests());
        await using var host = await ResponsesHost.StartAsync(fake, accounts: 1);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi"}""");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var json = Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("rate_limit_exceeded", json["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task 端到端_保险库为空回_401()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse("{}"));
        await using var host = await ResponsesHost.StartAsync(fake, accounts: 0);

        using var response = await host.PostResponsesAsync("""{"model":"hy3","input":"hi"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("authentication_error", json["error"]!["type"]!.GetValue<string>());
    }

    // ══════════════════════════════════════════════════════════
    // ⑨ 装配：端点族契约
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 本族实现了_IEndpointFamily()
    {
        // 第 ② 层（被装配）的前提是第 ① 层：类型必须实现这个接口。
        // ⚠ 但**实现了接口不等于被注册** —— 本项目出过「类写了但没注册」
        //   的完整死代码事故（UsageEndpoints）。第 ③ 层的证据是真网关 curl。
        Assert.True(typeof(IEndpointFamily).IsAssignableFrom(typeof(ResponsesEndpoints)));
    }

    [Fact]
    public void 路径常量与_Codex_期望的一致()
    {
        // Codex 的 wire_api="responses" 打的就是这个路径，写错一个字就全废
        Assert.Equal("/v1/responses", ResponsesEndpoints.Path);
    }

    // ══════════════════════════════════════════════════════════
    // 辅助
    // ══════════════════════════════════════════════════════════

    private static HttpResponseMessage UpstreamSse(params string[] lines) => UpstreamResponses.SseText(lines);

    /// <summary>
    /// 从一个 JSON 节点里读整数，**逐类型试**。
    ///
    /// ⚠ 为什么不能在测试里直接 `node.GetValue&lt;long&gt;()`：
    /// <see cref="JsonValue"/> 按**最窄类型**背书 JSON 数值 —— `{"n":0}` 里的 0
    /// 是 <c>int</c>，`GetValue&lt;long&gt;()` 会**抛 InvalidOperationException**
    /// （"A value of type 'System.Int32' cannot be converted to 'System.Int64'"）。
    ///
    /// 这正是被测代码要防的那个坑的**测试侧镜像**：生产侧
    /// <c>TryGetValue&lt;long&gt;</c> 会静默返回 false（参数被丢弃），
    /// 测试侧 <c>GetValue&lt;long&gt;</c> 会直接抛。
    /// 两边都不能只认 long —— 所以断言必须用这个逐类型试的读法。
    /// </summary>
    private static long Int(JsonNode? node) => node switch
    {
        null => throw new InvalidOperationException("节点不存在，无法读整数"),
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<double>(out var d) => (long)d,
        JsonValue v when v.TryGetValue<decimal>(out var m) => (long)m,
        _ => throw new InvalidOperationException($"节点不是数值：{node.ToJsonString()}"),
    };

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>第 n 次出现（1 基）的位置；没有则 -1。</summary>
    private static int IndexOfNth(string haystack, string needle, int n)
    {
        var index = -1;
        for (var i = 0; i < n; i++)
        {
            index = haystack.IndexOf(needle, index + 1, StringComparison.Ordinal);
            if (index < 0) return -1;
        }
        return index;
    }

    /// <summary>从一个命名事件块里抽出所有 `sequence_number` 的值。</summary>
    private static List<int> ExtractSequenceNumbers(string sse)
    {
        var result = new List<int>();
        foreach (var line in sse.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var payload = line["data: ".Length..];
            if (payload.StartsWith('{') is false) continue;

            try
            {
                if (JsonNode.Parse(payload) is JsonObject obj
                    && obj["sequence_number"] is JsonValue v
                    && v.TryGetValue<int>(out var n))
                {
                    result.Add(n);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 不是 JSON 的行（比如别的事件）跳过
            }
        }
        return result;
    }

    /// <summary>抽出某个命名事件里所有事件的某个整数字段（按出现顺序）。</summary>
    private static List<int> ExtractIntFields(string sse, string eventName, string field)
    {
        var result = new List<int>();
        var marker = $"event: {eventName}\n";
        var index = 0;

        while ((index = sse.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            var dataStart = sse.IndexOf("data: ", index, StringComparison.Ordinal);
            if (dataStart < 0) break;
            dataStart += "data: ".Length;
            var dataEnd = sse.IndexOf('\n', dataStart);
            var payload = dataEnd < 0 ? sse[dataStart..] : sse[dataStart..dataEnd];

            try
            {
                if (JsonNode.Parse(payload) is JsonObject obj
                    && obj[field] is JsonValue v
                    && v.TryGetValue<int>(out var n))
                {
                    result.Add(n);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 忽略
            }

            index = dataStart;
        }

        return result;
    }

    /// <summary>取某个命名事件那一帧的 data 行。</summary>
    private static string ExtractAfterEvent(string sse, string eventName)
    {
        var marker = $"event: {eventName}\n";
        var index = sse.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return "";

        var dataStart = sse.IndexOf("data: ", index, StringComparison.Ordinal);
        if (dataStart < 0) return "";
        dataStart += "data: ".Length;
        var dataEnd = sse.IndexOf("\n\n", dataStart, StringComparison.Ordinal);

        return dataEnd < 0 ? sse[dataStart..] : sse[dataStart..dataEnd];
    }

    private static string ExtractTextAfter(string sse, string eventName) => ExtractAfterEvent(sse, eventName);

    /// <summary>
    /// 起一个挂了 Responses 端点族的真宿主。
    ///
    /// ⚠ 这里**自己 new ResponsesEndpoints 并手写分派**，与 AnthropicEndpointTests
    /// 同样的理由：这样测试**不依赖 `Program.cs` 的注册行**，功能正确性可以先验透。
    /// 代价是——**本文件全绿并不能证明端点被挂上了**。第 ③ 层靠真网关 curl。
    /// </summary>
    private sealed class ResponsesHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public HttpClient Client { get; }
        public FakeUpstreamHandler Fake { get; }

        private ResponsesHost(WebApplication app, string baseAddress, FakeUpstreamHandler fake)
        {
            _app = app;
            Fake = fake;
            Client = new HttpClient(new SocketsHttpHandler())
            {
                BaseAddress = new Uri(baseAddress),
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        public Task<HttpResponseMessage> PostResponsesAsync(string json)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        public static async Task<ResponsesHost> StartAsync(FakeUpstreamHandler fake, int accounts = 1)
        {
            var options = new GatewayOptions
            {
                Host = "127.0.0.1",
                Port = 0,
                ApiKey = "",
                Variant = UpstreamVariant.China.Id,
                DefaultModel = "hy3",
                VaultPath = Path.Combine(Path.GetTempPath(), "buddydeck-responses-" + Guid.NewGuid().ToString("N") + ".json"),
            };

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            var services = builder.Services;
            services.AddSingleton(options);
            services.AddSingleton(UpstreamVariant.China);
            services.AddSingleton(new ModelCatalog(UpstreamVariant.China.Id, "hy3"));
            services.AddSingleton<AccountPool>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<ICredentialSource>(new FakeVault(accounts));
            services.AddSingleton(new HttpClient(fake, disposeHandler: false));
            services.AddSingleton(sp => new UpstreamClient(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<UpstreamVariant>(),
                sp.GetRequiredService<ILogger<UpstreamClient>>()));
            services.AddSingleton(sp => new ResponsesEndpoints(
                sp.GetRequiredService<UpstreamClient>(),
                sp.GetRequiredService<AccountPool>(),
                sp.GetRequiredService<ICredentialSource>(),
                sp.GetRequiredService<ModelCatalog>(),
                sp.GetRequiredService<ILogger<ResponsesEndpoints>>()));

            var app = builder.Build();

            // 手写分派：模拟挂载点落地后的行为（鉴权已由外层处理）
            app.Run(async context =>
            {
                var endpoints = context.RequestServices.GetRequiredService<ResponsesEndpoints>();
                if (!await endpoints.TryHandleAsync(context))
                {
                    context.Response.StatusCode = 404;
                }
            });

            await app.StartAsync().ConfigureAwait(false);
            return new ResponsesHost(app, app.Urls.First(), fake);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        private sealed class FakeVault(int accounts) : ICredentialSource
        {
            public IReadOnlyList<VaultEntry> Load()
            {
                var list = new List<VaultEntry>();
                for (var i = 0; i < accounts; i++)
                {
                    list.Add(new VaultEntry
                    {
                        Id = $"acc-{i}:ent-{i}",
                        Uid = $"uid-{i}",
                        EnterpriseId = $"ent-{i}",
                        Domain = "workbuddy.cn",
                        AccessToken = $"token-{i}",
                        ExpiresAtMs = 0,
                        Label = $"测试号{i}",
                    });
                }
                return list;
            }
        }
    }
}
