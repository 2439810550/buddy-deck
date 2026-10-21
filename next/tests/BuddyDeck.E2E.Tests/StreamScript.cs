using System.Text.Json.Nodes;
using BuddyDeck.TestSupport;

namespace BuddyDeck.E2E.Tests;

/// <summary>
/// 造上游 SSE 分片的小工厂。
///
/// ## 为什么要自己造而不是用 <c>UpstreamResponses</c>
///
/// <c>UpstreamResponses</c> 解决的是「造出一个 SSE 响应」；这里解决的是
/// 「造出一个**内容可断言**的 SSE 流」—— 每个用例都要明确知道
/// 「上游吐了什么」和「客户端该收到什么」，写死预期值比从上游引用回读
/// 更可靠（回读的话，网关把内容原样漏掉也照样绿）。
///
/// 分片字段严格按上游真实形态：<c>object=chat.completion.chunk</c>、
/// <c>choices[].delta</c>、终止片带 usage。**连空的 function_call 也照造** ——
/// 那是上游的真实习惯（见 <c>SseSanitizer</c> 的注释），清洗掉它是要验的行为之一。
/// </summary>
internal static class StreamScript
{
    /// <summary>固定 id / created，断言时不用管它们。</summary>
    private const string ChunkId = "chatcmpl-upstream-1";

    private const long Created = 1_700_000_000L;

    /// <summary>首片：声明 assistant 角色（OpenAI 客户端靠它认定这是助手回复）。</summary>
    public static string RoleFrame(string model = "hy3", string role = "assistant")
        => $"{{\"id\":\"{ChunkId}\",\"object\":\"chat.completion.chunk\",\"created\":{Created},\"model\":\"{model}\"," +
           $"\"choices\":[{{\"index\":0,\"delta\":{{\"role\":\"{role}\"}},\"finish_reason\":null}}]}}";

    /// <summary>正文增量片。</summary>
    public static string ContentFrame(string content, string model = "hy3")
        => $"{{\"id\":\"{ChunkId}\",\"object\":\"chat.completion.chunk\",\"created\":{Created},\"model\":\"{model}\"," +
           $"\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{content}\"}},\"finish_reason\":null}}]}}";

    /// <summary>推理增量片（<c>reasoning_content</c> —— 好几个客户端会渲染它）。</summary>
    public static string ReasoningFrame(string reasoning, string model = "hy3")
        => $"{{\"id\":\"{ChunkId}\",\"object\":\"chat.completion.chunk\",\"created\":{Created},\"model\":\"{model}\"," +
           $"\"choices\":[{{\"index\":0,\"delta\":{{\"reasoning_content\":\"{reasoning}\"}},\"finish_reason\":null}}]}}";

    /// <summary>
    /// 终止片：空 delta + finish_reason + usage。
    ///
    /// ⚠ usage 里**故意带厂商私有字段**（credit / cached）—— 网关要把它裁成
    /// 客户端认的三个字段，同时保留原值给记账用（<c>SseSanitizer.SanitizeUsage</c>）。
    /// </summary>
    public static string StopFrame(
        string finishReason = "stop",
        int prompt = 7,
        int completion = 3,
        string model = "hy3",
        bool withUsage = true)
    {
        var usage = withUsage
            ? $",\"usage\":{{\"prompt_tokens\":{prompt},\"completion_tokens\":{completion}," +
              $"\"total_tokens\":{prompt + completion},\"credit\":\"0.0123\",\"cached_tokens\":0}}"
            : "";

        return $"{{\"id\":\"{ChunkId}\",\"object\":\"chat.completion.chunk\",\"created\":{Created},\"model\":\"{model}\"," +
               $"\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"{finishReason}\"}}]{usage}}}";
    }

    /// <summary>
    /// 带一段**空的 function_call** 的正片 —— 上游的真实习惯。
    ///
    /// 某些客户端看到 <c>function_call</c> 挨着 <c>tool_calls</c> 会凭空冒出一个
    /// 并行调用，所以网关必须把它删掉。这条要能被断言（流式和非流式各一次）。
    /// </summary>
    public static string FrameWithEmptyFunctionCall(string content, string model = "hy3")
        => $"{{\"id\":\"{ChunkId}\",\"object\":\"chat.completion.chunk\",\"created\":{Created},\"model\":\"{model}\"," +
           $"\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{content}\",\"function_call\":{{\"name\":\"\",\"arguments\":\"\"}},\"reasoning_content\":\"\"}},\"finish_reason\":null}}]}}";

    // ── tool_calls ────────────────────────────────────────────

    /// <summary>
    /// tool_call 的**起始片**：id + type + function.name，arguments 是空串。
    /// </summary>
    public static string ToolCallStartFrame(
        int index, string id, string name, string model = "hy3")
        => $"{{\"id\":\"{ChunkId}\",\"object\":\"chat.completion.chunk\",\"created\":{Created},\"model\":\"{model}\"," +
           $"\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":{index},\"id\":\"{id}\",\"type\":\"function\"," +
           $"\"function\":{{\"name\":\"{name}\",\"arguments\":\"\"}}}}]}},\"finish_reason\":null}}]}}";

    /// <summary>
    /// tool_call 的**参数碎片片**：只有 index + arguments 的一截。
    ///
    /// 参数是流式 JSON 碎片 —— 中途 parse 一次就会失败（比如只收到 <c>{"pa</c>）。
    /// 网关必须原样相接。
    ///
    /// ⚠ 传进来的片段会被**自动 JSON 转义**后再嵌进分片里。
    /// 不转义的话产出的分片本身就不是合法 JSON，网关的 JsonNode.Parse 会失败、
    /// 整片被静默跳过 —— 那样测出来的是「造数据造错了」而不是网关的行为。
    /// （这条真踩过：参数全变成 <c>{}</c>，看起来像网关丢参数。）
    /// </summary>
    public static string ToolCallArgsFrame(int index, string argumentsFragment, string model = "hy3")
        => $"{{\"id\":\"{ChunkId}\",\"object\":\"chat.completion.chunk\",\"created\":{Created},\"model\":\"{model}\"," +
           $"\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":{index}," +
           $"\"function\":{{\"arguments\":\"{Escape(argumentsFragment)}\"}}}}]}},\"finish_reason\":null}}]}}";

    // ── 组装整条流 ────────────────────────────────────────────

    /// <summary>
    /// 一段「正常回答」：声明角色 → 正文分片 → 终止片 → [DONE]。
    /// </summary>
    public static HttpResponseMessage TextStream(params string[] contentDeltas)
        => UpstreamResponses.SseFrames(BuildTextFrames(contentDeltas), partsPerFrame: 1);

    /// <summary>
    /// 同上，但**分块边界落在事件中间** —— 网络上一次 read 拿到半行是常态，
    /// <c>SseReader</c> 的增量扫行逻辑只有在这种输入下才真的被跑到。
    /// </summary>
    public static HttpResponseMessage TextStreamSplit(params string[] contentDeltas)
        => UpstreamResponses.SseFrames(BuildTextFrames(contentDeltas), partsPerFrame: 3);

    private static List<string> BuildTextFrames(string[] contentDeltas)
    {
        var frames = new List<string> { RoleFrame() };
        frames.AddRange(contentDeltas.Select(d => ContentFrame(d)));
        frames.Add(StopFrame());
        return frames;
    }

    /// <summary>一段「工具调用」流：name 一片、arguments 切成三片。</summary>
    public static HttpResponseMessage ToolCallStream(
        string name, string arguments, int argumentParts = 3, string toolCallId = "call_1")
    {
        // 参数故意切成几截，模拟上游的流式发法
        var size = Math.Max(1, (int)Math.Ceiling(arguments.Length / (double)argumentParts));
        var frames = new List<string>
        {
            RoleFrame(),
            ToolCallStartFrame(0, toolCallId, name),
        };

        for (var offset = 0; offset < arguments.Length; offset += size)
        {
            var take = Math.Min(size, arguments.Length - offset);
            // ToolCallArgsFrame 内部会做 JSON 转义 —— 这里传原文即可
            frames.Add(ToolCallArgsFrame(0, arguments.Substring(offset, take)));
        }

        frames.Add(StopFrame("tool_calls", withUsage: true));
        return UpstreamResponses.SseFrames(frames, partsPerFrame: 2);
    }

    /// <summary>
    /// 一段被**字节级切断**的流 —— 用来验 SseReader 的跨块拼行。
    ///
    /// 直接把拼好的 SSE 文本切成固定长度喂进去，切点必然落在行的中间。
    /// </summary>
    public static HttpResponseMessage ChoppedStream(string content, int chunkSize = 7)
    {
        var raw = $"data: {RoleFrame()}\n\n"
                  + $"data: {ContentFrame(content)}\n\n"
                  + $"data: {StopFrame()}\n\n"
                  + "data: [DONE]\n\n";

        var pieces = new List<string>();
        for (var offset = 0; offset < raw.Length; offset += chunkSize)
        {
            pieces.Add(raw.Substring(offset, Math.Min(chunkSize, raw.Length - offset)));
        }

        return UpstreamResponses.SseRaw(string.Concat(pieces));
    }

    /// <summary>一段最简单的完整 SSE 正文（延迟吐出用的场景）。</summary>
    public static string TextStreamRaw(string content)
        => $"data: {RoleFrame()}\n\n"
           + $"data: {ContentFrame(content)}\n\n"
           + $"data: {StopFrame()}\n\n"
           + "data: [DONE]\n\n";

    /// <summary>把 JSON 片段转义成能塞进 JSON 字符串字面量的形式。</summary>
    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);

    // ── 读客户端的响应 ────────────────────────────────────────

    /// <summary>
    /// 把 SSE 响应体拆成 data 载荷序列（自动去掉 <c>data: </c> 前缀和空行）。
    ///
    /// 不用 <c>JsonNode.Parse</c> 直接切文本 —— 切错了会得到一句
    /// 「JSON 解析失败」，而正确的失败信息应该是「第 3 条载荷不是 JSON」。
    /// </summary>
    public static List<string> Payloads(string sseBody)
    {
        var payloads = new List<string>();

        foreach (var line in sseBody.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;

            var value = trimmed.Length > 5 && trimmed[5] == ' ' ? trimmed[6..] : trimmed[5..];
            if (value.Length > 0) payloads.Add(value);
        }

        return payloads;
    }

    /// <summary>把一条流的所有正文增量拼起来（断言「客户端收到的正文」）。</summary>
    public static string ConcatContent(string sseBody)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var payload in Payloads(sseBody))
        {
            if (payload == "[DONE]") continue;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(payload);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;   // 非法分片不参与拼接（那是另一条用例的事）
            }

            var delta = node?["choices"]?.AsArray().FirstOrDefault()?["delta"];
            if (delta?["content"] is JsonValue value && value.TryGetValue<string>(out var text)) sb.Append(text);
        }

        return sb.ToString();
    }

    /// <summary>把一条流里所有 tool_call 的 arguments 碎片按 index 接起来。</summary>
    public static string ConcatToolArguments(string sseBody, int index = 0)
    {
        var sb = new System.Text.StringBuilder();
        var name = new System.Text.StringBuilder();

        foreach (var payload in Payloads(sseBody))
        {
            if (payload == "[DONE]") continue;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(payload);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (node?["choices"]?.AsArray().FirstOrDefault()?["delta"]?["tool_calls"] is not JsonArray calls) continue;

            foreach (var call in calls)
            {
                if (call is not JsonObject tc) continue;
                if ((tc["index"]?.GetValue<int>() ?? 0) != index) continue;

                if (tc["function"]?["name"] is JsonValue nv && nv.TryGetValue<string>(out var n)) name.Append(n);
                if (tc["function"]?["arguments"] is JsonValue av && av.TryGetValue<string>(out var a)) sb.Append(a);
            }
        }

        return sb.ToString();
    }

    /// <summary>同上，但只取名字。</summary>
    public static string ConcatToolName(string sseBody, int index = 0)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var payload in Payloads(sseBody))
        {
            if (payload == "[DONE]") continue;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(payload);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (node?["choices"]?.AsArray().FirstOrDefault()?["delta"]?["tool_calls"] is not JsonArray calls) continue;

            foreach (var call in calls)
            {
                if (call is not JsonObject tc) continue;
                if ((tc["index"]?.GetValue<int>() ?? 0) != index) continue;

                if (tc["function"]?["name"] is JsonValue nv && nv.TryGetValue<string>(out var n)) sb.Append(n);
            }
        }

        return sb.ToString();
    }

    /// <summary>流里有没有出现某个子串（断言「清洗掉了 function_call」用）。</summary>
    public static bool ContainsRaw(string sseBody, string needle)
        => sseBody.Contains(needle, StringComparison.Ordinal);
}
