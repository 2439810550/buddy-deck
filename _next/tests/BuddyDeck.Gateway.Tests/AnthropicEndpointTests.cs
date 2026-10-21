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
/// Anthropic Messages 翻译层的测试（t26）。
///
/// ## 分两类
///
/// **第一类：纯翻译函数**（`TranslateRequest` / `TranslateResponse` /
/// `StripClientFingerprint` / `EstimateTokens`）—— 直接用 `internal static`
/// 方法测，不需要起宿主。**绝大多数映射错误在这里就能钉死。**
///
/// **第二类：端到端**（起真 Kestrel + 假上游）—— 验的是「装配与真实路径」，
/// 按队长定的证据分级属于 ④。它才能抓到「翻译对了但没挂上」这类缺口。
/// </summary>
public class AnthropicEndpointTests
{
    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    // ══════════════════════════════════════════════════════════
    // ① 指纹行剔除 —— 「做了 90% 等于 0%」的那一处
    // ══════════════════════════════════════════════════════════

    /// <summary>Claude Code 真实会带的那一行。</summary>
    private const string FingerprintLine =
        "x-anthropic-billing-header: cc_version=2.1.250.7b8; cc_entrypoint=sdk-cli;";

    [Fact]
    public void 指纹行被剔除()
    {
        // ★★ 这是 Claude Code 能不能用的**前提**：
        //    带上这一行，上游回 HTTP 400 / code 11128
        //    「Illegal API invocation from an unapproved channel」。
        var system = $"你是助手。\n{FingerprintLine}\n请遵守规则。";

        var cleaned = AnthropicEndpoints.StripClientFingerprint(system);

        Assert.DoesNotContain("x-anthropic-", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cc_version", cleaned, StringComparison.Ordinal);
        // 其余内容一字不动 —— 它纯粹是遥测元数据，不含提示词语义
        Assert.Contains("你是助手。", cleaned, StringComparison.Ordinal);
        Assert.Contains("请遵守规则。", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void 指纹行剔除_反证_不剔除的话它会留在产物里()
    {
        // ★ 契约明确要求的「不剔除会失败」反证。
        //
        // 做法：把 system 原样翻成 chat 请求（绕过 StripClientFingerprint，
        // 直接手工构造），证明**如果那一行留下了，它确实会出现在发给上游的
        // system 消息里** —— 也就是说「剔除」这一步不是可有可无的装饰。
        var systemWithFingerprint = $"你是助手。\n{FingerprintLine}";
        var body = new JsonObject
        {
            ["model"] = "hy3",
            ["system"] = systemWithFingerprint,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        };

        var chat = AnthropicEndpoints.TranslateRequest(body, "hy3");
        var systemMessage = chat["messages"]!.AsArray()[0]!["content"]!.GetValue<string>();

        // 正路：翻出来的**不含**指纹行（说明剔除确实生效了）
        Assert.DoesNotContain("x-anthropic-", systemMessage, StringComparison.OrdinalIgnoreCase);

        // 反证：手工保留原文时，那一行**确实**在会被发给上游的位置上
        //（即「若不剔除，它就会到达上游」—— 这正是要避免的）
        Assert.Contains("x-anthropic-", systemWithFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 指纹行剔除_不同形态的变体都要认()
    {
        // 上游的正则容错：行首空白、大小写、中间那段可变的标识
        var variants = new[]
        {
            "x-anthropic-billing-header: cc_version=1",
            "  x-anthropic-billing-header: cc_version=1",        // 行首空白
            "X-Anthropic-Billing-Header: cc_version=1",           // 大小写
            "x-anthropic-header: cc_version=1",                   // 中间没那段
            "x-anthropic-abc123-header: cc_version=1",            // 中间有标识
            "\tx-anthropic-billing-header:\tcc_version=1",        // 制表符
        };

        foreach (var variant in variants)
        {
            var cleaned = AnthropicEndpoints.StripClientFingerprint($"前面\n{variant}\n后面");
            Assert.DoesNotContain("x-anthropic", cleaned, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void 不含指纹行的文本原样返回()
    {
        var system = "你是一个严谨的助手。\n不要编造事实。";

        Assert.Equal(system, AnthropicEndpoints.StripClientFingerprint(system));
    }

    [Fact]
    public void 指纹行剔除_不会误伤_普通行()
    {
        // 只删**行首**是 x-anthropic-…header: 的行。
        // 其它提到这个词的地方（比如代码示例里）不该被动。
        var system = "参考这句：日志里会写 x-anthropic-billing-header 这样的字段名。";

        var cleaned = AnthropicEndpoints.StripClientFingerprint(system);

        Assert.Equal(system, cleaned);   // 不在行首 → 不该删
    }

    [Fact]
    public void system_可以是_block_数组_且指纹同样被剔()
    {
        var body = new JsonObject
        {
            ["system"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = "第一段" },
                new JsonObject { ["type"] = "text", ["text"] = FingerprintLine },
                new JsonObject { ["type"] = "text", ["text"] = "第三段" }),
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        };

        var chat = AnthropicEndpoints.TranslateRequest(body, "hy3");
        var system = chat["messages"]!.AsArray()[0]!["content"]!.GetValue<string>();

        Assert.Contains("第一段", system, StringComparison.Ordinal);
        Assert.Contains("第三段", system, StringComparison.Ordinal);
        Assert.DoesNotContain("x-anthropic-", system, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════
    // ② 请求翻译
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void system_抽离成首条_system_消息()
    {
        var body = new JsonObject
        {
            ["system"] = "你是助手",
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "你好" }),
        };

        var chat = AnthropicEndpoints.TranslateRequest(body, "hy3");
        var messages = chat["messages"]!.AsArray();

        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("你是助手", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void 没有_system_时不塞空消息()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        };

        var chat = AnthropicEndpoints.TranslateRequest(body, "hy3");
        var messages = chat["messages"]!.AsArray();

        Assert.Single(messages);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void 字符串_content_与_block_数组两种形态都认()
    {
        var fromString = AnthropicEndpoints.TranslateRequest(new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "文本" }),
        }, "hy3");

        var fromBlocks = AnthropicEndpoints.TranslateRequest(new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "文本" }),
            }),
        }, "hy3");

        Assert.Equal(
            fromString["messages"]!.AsArray()[0]!["content"]!.GetValue<string>(),
            fromBlocks["messages"]!.AsArray()[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void tool_result_挪成_role_tool_消息()
    {
        // ⚠ Anthropic 把工具结果塞在 user 的 content block 里；
        //    OpenAI 要求独立的 role:"tool" 消息。位置错了上游会拒。
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = "toolu_1",
                        ["name"] = "read_file",
                        ["input"] = new JsonObject { ["path"] = "/tmp/a" },
                    }),
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = "toolu_1",
                        ["content"] = "文件内容",
                    }),
                }),
        };

        var chat = AnthropicEndpoints.TranslateRequest(body, "hy3");
        var messages = chat["messages"]!.AsArray();

        // assistant 带 tool_calls
        var assistant = messages[0]!.AsObject();
        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        var call = assistant["tool_calls"]!.AsArray()[0]!.AsObject();
        Assert.Equal("toolu_1", call["id"]!.GetValue<string>());
        Assert.Equal("read_file", call["function"]!["name"]!.GetValue<string>());
        // ⚠ arguments 必须是**字符串**（OpenAI 形状），不是对象
        Assert.Equal("""{"path":"/tmp/a"}""", call["function"]!["arguments"]!.GetValue<string>());

        // tool 消息紧跟其后
        var tool = messages[1]!.AsObject();
        Assert.Equal("tool", tool["role"]!.GetValue<string>());
        Assert.Equal("toolu_1", tool["tool_call_id"]!.GetValue<string>());
        Assert.Equal("文件内容", tool["content"]!.GetValue<string>());
    }

    [Fact]
    public void tools_映射成_function_形态()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = "get_weather",
                ["description"] = "查天气",
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["city"] = new JsonObject { ["type"] = "string" } },
                },
            }),
        };

        var chat = AnthropicEndpoints.TranslateRequest(body, "hy3");
        var tool = chat["tools"]!.AsArray()[0]!.AsObject();

        Assert.Equal("function", tool["type"]!.GetValue<string>());
        Assert.Equal("get_weather", tool["function"]!["name"]!.GetValue<string>());
        Assert.Equal("查天气", tool["function"]!["description"]!.GetValue<string>());
        Assert.NotNull(tool["function"]!["parameters"]);
    }

    [Fact]
    public void 没有_input_schema_的服务端工具被丢掉()
    {
        // web_search 这类服务端工具上游不认 —— 留着会让整个请求被拒。
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
            ["tools"] = new JsonArray(
                new JsonObject { ["type"] = "web_search_20250305", ["name"] = "web_search" },
                new JsonObject
                {
                    ["name"] = "real_tool",
                    ["input_schema"] = new JsonObject { ["type"] = "object" },
                }),
        };

        var chat = AnthropicEndpoints.TranslateRequest(body, "hy3");
        var tools = chat["tools"]!.AsArray();

        Assert.Single(tools);
        Assert.Equal("real_tool", tools[0]!["function"]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("none", "none")]
    [InlineData("any", "required")]      // ★ 名字不同但语义相同
    public void tool_choice_映射正确(string anthropic, string expected)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
            ["tool_choice"] = new JsonObject { ["type"] = anthropic },
        };

        var chat = AnthropicEndpoints.TranslateRequest(body, "hy3");

        Assert.Equal(expected, chat["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public void tool_choice_的_tool_形态映射成函数名()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "get_weather" },
        };

        var chat = AnthropicEndpoints.TranslateRequest(body, "hy3");

        Assert.Equal("get_weather", chat["tool_choice"]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void max_tokens_与采样参数被透传()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
            ["max_tokens"] = 4096,
            ["temperature"] = 0.7,
            ["top_p"] = 0.9,
            ["stop_sequences"] = new JsonArray("STOP"),
        };

        var chat = AnthropicEndpoints.TranslateRequest(body, "hy3");

        Assert.Equal(4096, chat["max_tokens"]!.GetValue<long>());
        Assert.Equal(0.7, chat["temperature"]!.GetValue<double>(), 3);
        Assert.Equal(0.9, chat["top_p"]!.GetValue<double>(), 3);
        Assert.Equal("STOP", chat["stop"]!.AsArray()[0]!.GetValue<string>());
    }

    [Fact]
    public void 图片_block_转成_data_uri()
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "这是什么" },
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = "image/png",
                            ["data"] = "aGVsbG8=",
                        },
                    }),
            }),
        };

        var chat = AnthropicEndpoints.TranslateRequest(body, "hy3");
        var parts = chat["messages"]!.AsArray()[0]!["content"]!.AsArray();

        var image = parts[1]!.AsObject();
        Assert.Equal("image_url", image["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,aGVsbG8=", image["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void 不认识的_block_类型被丢弃而不是报错()
    {
        // 客户端发的 block 类型常比我们认识的多 —— 报错会让整条消息发不出去。
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "保留的" },
                    new JsonObject { ["type"] = "document", ["source"] = new JsonObject() },
                    new JsonObject { ["type"] = "thinking", ["thinking"] = "内部思考" }),
            }),
        };

        var chat = AnthropicEndpoints.TranslateRequest(body, "hy3");

        Assert.Equal("保留的", chat["messages"]!.AsArray()[0]!["content"]!.GetValue<string>());
    }

    // ══════════════════════════════════════════════════════════
    // ③ 响应回翻
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 响应_text_翻成_text_block()
    {
        var completion = Parse("""
        {"id":"c1","object":"chat.completion","model":"hy3",
         "choices":[{"index":0,"message":{"role":"assistant","content":"你好"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
        """);

        var message = AnthropicEndpoints.TranslateResponse(completion, "hy3");

        Assert.Equal("message", message["type"]!.GetValue<string>());
        Assert.Equal("assistant", message["role"]!.GetValue<string>());

        var block = message["content"]!.AsArray()[0]!.AsObject();
        Assert.Equal("text", block["type"]!.GetValue<string>());
        Assert.Equal("你好", block["text"]!.GetValue<string>());

        Assert.Equal("end_turn", message["stop_reason"]!.GetValue<string>());
        Assert.Equal(10, message["usage"]!["input_tokens"]!.GetValue<long>());
        Assert.Equal(5, message["usage"]!["output_tokens"]!.GetValue<long>());
    }

    [Fact]
    public void 响应_tool_calls_翻成_tool_use_block_且_input_是对象()
    {
        // ⚠ 方向与请求相反：OpenAI 的 arguments 是**字符串**，
        //    Anthropic 的 input 是**对象**。
        var completion = Parse("""
        {"id":"c1","choices":[{"index":0,
          "message":{"role":"assistant","content":"",
            "tool_calls":[{"id":"call_1","type":"function",
              "function":{"name":"read_file","arguments":"{\"path\":\"/tmp/a\"}"}}]},
          "finish_reason":"tool_calls"}],"usage":{}}
        """);

        var message = AnthropicEndpoints.TranslateResponse(completion, "hy3");
        var block = message["content"]!.AsArray()[0]!.AsObject();

        Assert.Equal("tool_use", block["type"]!.GetValue<string>());
        Assert.Equal("call_1", block["id"]!.GetValue<string>());
        Assert.Equal("read_file", block["name"]!.GetValue<string>());
        Assert.Equal("/tmp/a", block["input"]!["path"]!.GetValue<string>());
        Assert.Equal("tool_use", message["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public void 响应_非法_arguments_包成_raw_不丢调用()
    {
        // 丢掉整个调用的话，Claude Code 会以为模型没调工具 —— 行为完全变了。
        var completion = Parse("""
        {"id":"c1","choices":[{"index":0,
          "message":{"role":"assistant","content":"",
            "tool_calls":[{"id":"call_1","type":"function",
              "function":{"name":"broken","arguments":"{不是 JSON"}}]},
          "finish_reason":"tool_calls"}],"usage":{}}
        """);

        var message = AnthropicEndpoints.TranslateResponse(completion, "hy3");
        var block = message["content"]!.AsArray()[0]!.AsObject();

        Assert.Equal("tool_use", block["type"]!.GetValue<string>());
        Assert.Equal("{不是 JSON", block["input"]!["_raw"]!.GetValue<string>());
    }

    [Fact]
    public void 响应_reasoning_翻成_thinking_block_且在_text_之前()
    {
        var completion = Parse("""
        {"id":"c1","choices":[{"index":0,
          "message":{"role":"assistant","content":"答案","reasoning_content":"先想一下"},
          "finish_reason":"stop"}],"usage":{}}
        """);

        var message = AnthropicEndpoints.TranslateResponse(completion, "hy3");
        var content = message["content"]!.AsArray();

        Assert.Equal("thinking", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("先想一下", content[0]!["thinking"]!.GetValue<string>());
        Assert.Equal("text", content[1]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("stop", "end_turn")]
    [InlineData("length", "max_tokens")]
    [InlineData("tool_calls", "tool_use")]
    [InlineData("content_filter", "refusal")]
    public void stop_reason_映射(string finishReason, string expected)
    {
        Assert.Equal(expected, AnthropicEndpoints.ToAnthropicStopReason(finishReason));
    }

    [Fact]
    public void 有_tool_calls_但_finish_reason_是_stop_时报_tool_use()
    {
        // ★ 旧版实测来的修正：有些模型发了 tool_calls 却把 finish_reason 报成 stop。
        //   而 Claude Code **靠 stop_reason 决定要不要执行工具** ——
        //   报成 end_turn 的话工具调用被静默丢弃。
        var completion = Parse("""
        {"id":"c1","choices":[{"index":0,
          "message":{"role":"assistant","content":"",
            "tool_calls":[{"id":"call_1","type":"function",
              "function":{"name":"f","arguments":"{}"}}]},
          "finish_reason":"stop"}],"usage":{}}
        """);

        var message = AnthropicEndpoints.TranslateResponse(completion, "hy3");

        Assert.Equal("tool_use", message["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public void 响应内容为空时给一个空_text_block()
    {
        // 内容数组为空会让 Claude Code 的解析出问题 —— 给个空 text block。
        var completion = Parse("""
        {"id":"c1","choices":[{"index":0,"message":{"role":"assistant","content":""},"finish_reason":"stop"}],"usage":{}}
        """);

        var message = AnthropicEndpoints.TranslateResponse(completion, "hy3");
        var content = message["content"]!.AsArray();

        Assert.Single(content);
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
    }

    // ══════════════════════════════════════════════════════════
    // ④ count_tokens 粗估
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 估_token_中日文按宽字符算()
    {
        // 宽字符（码点 > 0x2e80）逐字算 1
        var chinese = AnthropicEndpoints.EstimateTokens(JsonValue.Create("中文字符测试"));

        Assert.Equal(6, chinese);
    }

    [Fact]
    public void 估_token_英文按_3_字符一个()
    {
        // "hello world" = 11 字符 → ceil(11/3) = 4
        var english = AnthropicEndpoints.EstimateTokens(JsonValue.Create("hello world"));

        Assert.Equal(4, english);
    }

    [Fact]
    public void 估_token_永不返回零()
    {
        // 返回 0 会让 Claude Code 的上下文管理算错比例。
        Assert.True(AnthropicEndpoints.EstimateTokens(JsonValue.Create("")) >= 1);
        Assert.True(AnthropicEndpoints.EstimateTokens(null) >= 1);
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ 端到端：装配与真实路径（证据等级 ④）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 起一个挂了 Anthropic 端点族的真宿主。
    ///
    /// ⚠ 这里**自己 new AnthropicEndpoints 并手写分派**，是因为挂载点
    /// （`GatewayApp`）的改动待队长授权（t26 的 inScope 不含它）。
    /// 这样测试**不依赖挂载**，功能正确性可以先验透；
    /// 挂载落地后这些用例一行不用改（它们测的是 HTTP 行为，不是挂载方式）。
    /// </summary>
    private sealed class AnthropicHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public HttpClient Client { get; }
        public FakeUpstreamHandler Fake { get; }

        private AnthropicHost(WebApplication app, string baseAddress, FakeUpstreamHandler fake)
        {
            _app = app;
            Fake = fake;
            Client = new HttpClient(new SocketsHttpHandler())
            {
                BaseAddress = new Uri(baseAddress),
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        public Task<HttpResponseMessage> PostMessagesAsync(string json)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        public Task<HttpResponseMessage> PostCountTokensAsync(string json)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages/count_tokens")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        public static async Task<AnthropicHost> StartAsync(FakeUpstreamHandler fake)
        {
            var options = new GatewayOptions
            {
                Host = "127.0.0.1",
                Port = 0,
                ApiKey = "",
                Variant = UpstreamVariant.China.Id,
                DefaultModel = "hy3",
                VaultPath = Path.Combine(Path.GetTempPath(), "buddydeck-anthropic-" + Guid.NewGuid().ToString("N") + ".json"),
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
            services.AddSingleton<ICredentialSource>(new FakeVault());
            services.AddSingleton(new HttpClient(fake, disposeHandler: false));
            services.AddSingleton(sp => new UpstreamClient(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<UpstreamVariant>(),
                sp.GetRequiredService<ILogger<UpstreamClient>>()));
            services.AddSingleton(sp => new AnthropicEndpoints(
                sp.GetRequiredService<UpstreamClient>(),
                sp.GetRequiredService<AccountPool>(),
                sp.GetRequiredService<ICredentialSource>(),
                sp.GetRequiredService<ModelCatalog>(),
                requestLog: null,
                sp.GetRequiredService<ILogger<AnthropicEndpoints>>()));

            var app = builder.Build();

            // 手写分派：模拟挂载点落地后的行为（鉴权已由外层处理）
            app.Run(async context =>
            {
                var endpoints = context.RequestServices.GetRequiredService<AnthropicEndpoints>();
                if (!await endpoints.TryHandleAsync(context))
                {
                    context.Response.StatusCode = 404;
                }
            });

            await app.StartAsync().ConfigureAwait(false);
            return new AnthropicHost(app, app.Urls.First(), fake);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        private sealed class FakeVault : ICredentialSource
        {
            public IReadOnlyList<VaultEntry> Load() =>
            [
                new VaultEntry
                {
                    Id = "acc-1:ent-1",
                    Uid = "uid-1",
                    EnterpriseId = "ent-1",
                    Domain = "workbuddy.cn",
                    AccessToken = "token-1",
                    ExpiresAtMs = 0,
                    Label = "测试号",
                },
            ];
        }
    }

    private static HttpResponseMessage UpstreamSse(params string[] lines) => UpstreamResponses.SseText(lines);

    [Fact]
    public async Task 端到端_非流式_回_Anthropic_message_形状()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""",
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{"content":"你好"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2}}"""));

        await using var host = await AnthropicHost.StartAsync(fake);

        var body = """
        {"model":"hy3","max_tokens":100,"messages":[{"role":"user","content":"hi"}]}
        """;
        using var response = await host.PostMessagesAsync(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.Equal("message", json["type"]!.GetValue<string>());
        Assert.Equal("assistant", json["role"]!.GetValue<string>());
        Assert.Equal("你好", json["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
        Assert.Equal("end_turn", json["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task 端到端_发给上游的_system_不含指纹行()
    {
        // ★ 端到端级（证据 ④）：不只是翻译函数对，**真实发出去的那一份**也不含。
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}"""));

        await using var host = await AnthropicHost.StartAsync(fake);

        var body = $$"""
        {"model":"hy3","max_tokens":100,
         "system":"你是助手\n{{FingerprintLine}}\n守规矩",
         "messages":[{"role":"user","content":"hi"}]}
        """;
        using var response = await host.PostMessagesAsync(body);
        await response.Content.ReadAsStringAsync();

        var sent = JsonNode.Parse(fake.Requests.Single().Body!)!.AsObject();
        var systemMessage = sent["messages"]!.AsArray()[0]!["content"]!.GetValue<string>();

        Assert.DoesNotContain("x-anthropic-", systemMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("你是助手", systemMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 端到端_流式_按_Anthropic_命名事件输出()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"你"},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"好"},"finish_reason":"stop"}],"usage":{"completion_tokens":2}}"""));

        await using var host = await AnthropicHost.StartAsync(fake);

        var body = """
        {"model":"hy3","max_tokens":100,"stream":true,"messages":[{"role":"user","content":"hi"}]}
        """;
        using var response = await host.PostMessagesAsync(body);
        var text = await response.Content.ReadAsStringAsync();

        // 事件名必须是 Anthropic 那套，不是 OpenAI 的匿名 data-only
        Assert.Contains("event: message_start", text, StringComparison.Ordinal);
        Assert.Contains("event: content_block_start", text, StringComparison.Ordinal);
        Assert.Contains("event: content_block_delta", text, StringComparison.Ordinal);
        Assert.Contains("event: content_block_stop", text, StringComparison.Ordinal);
        Assert.Contains("event: message_delta", text, StringComparison.Ordinal);
        Assert.Contains("event: message_stop", text, StringComparison.Ordinal);

        // message_start 必须在最前、且只一次（Claude Code 严格按此解析）
        var startIdx = text.IndexOf("event: message_start", StringComparison.Ordinal);
        Assert.Equal(0, startIdx);
        Assert.Equal(1, CountOccurrences(text, "event: message_start"));

        // 结束顺序：message_delta 在 message_stop 之前
        Assert.True(
            text.IndexOf("event: message_delta", StringComparison.Ordinal)
            < text.IndexOf("event: message_stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 端到端_流式_delta_带_text_delta_与_index()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"content":"内容"},"finish_reason":"stop"}]}"""));

        await using var host = await AnthropicHost.StartAsync(fake);

        var body = """
        {"model":"hy3","max_tokens":100,"stream":true,"messages":[{"role":"user","content":"hi"}]}
        """;
        using var response = await host.PostMessagesAsync(body);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"type\":\"text_delta\"", text, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"内容\"", text, StringComparison.Ordinal);
        Assert.Contains("\"index\":0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 端到端_流式_tool_use_按_input_json_delta_输出()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse(
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":""}}]},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"pa"}}]},"finish_reason":null}]}""",
            """{"id":"c1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"/tmp\"}"}}]},"finish_reason":"tool_calls"}]}"""));

        await using var host = await AnthropicHost.StartAsync(fake);

        var body = """
        {"model":"hy3","max_tokens":100,"stream":true,"messages":[{"role":"user","content":"hi"}]}
        """;
        using var response = await host.PostMessagesAsync(body);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"type\":\"tool_use\"", text, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"read_file\"", text, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"input_json_delta\"", text, StringComparison.Ordinal);
        // arguments 分片要原样透传（Claude Code 自己拼）
        Assert.Contains("partial_json", text, StringComparison.Ordinal);
        // stop_reason 报 tool_use
        Assert.Contains("\"stop_reason\":\"tool_use\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 端到端_count_tokens_回_input_tokens()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse("{}"));
        await using var host = await AnthropicHost.StartAsync(fake);

        var body = """
        {"model":"hy3","messages":[{"role":"user","content":"你好世界"}]}
        """;
        using var response = await host.PostCountTokensAsync(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.True(json["input_tokens"]!.GetValue<long>() >= 4);
    }

    [Fact]
    public async Task 端到端_上游失败回_Anthropic_错误信封()
    {
        // ⚠ 形状与 /v1/* 不同：Anthropic 是 {type:"error",error:{type,message}}
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Unauthorized("bad token"));
        await using var host = await AnthropicHost.StartAsync(fake);

        var body = """
        {"model":"hy3","max_tokens":100,"messages":[{"role":"user","content":"hi"}]}
        """;
        using var response = await host.PostMessagesAsync(body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("error", json["type"]!.GetValue<string>());
        Assert.Equal("authentication_error", json["error"]!["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(json["error"]!["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task 端到端_畸形_JSON_回_400()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse("{}"));
        await using var host = await AnthropicHost.StartAsync(fake);

        using var response = await host.PostMessagesAsync("{不是 JSON");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("error", json["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task 端到端_GET_请求_回_405()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamSse("{}"));
        await using var host = await AnthropicHost.StartAsync(fake);

        using var response = await host.Client.GetAsync("/v1/messages");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task 端到端_非本族的路径返回_404_让网关继续分派()
    {
        // 「就地接管」契约：不是我的前缀必须原样返回 false，网关继续走它自己的分派。
        var fake = new FakeUpstreamHandler(_ => UpstreamSse("{}"));
        await using var host = await AnthropicHost.StartAsync(fake);

        using var response = await host.Client.PostAsync("/v1/chat/completions",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

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
}
