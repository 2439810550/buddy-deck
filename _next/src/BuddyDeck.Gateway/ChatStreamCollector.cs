using System.Text.Json.Nodes;

namespace BuddyDeck.Gateway;

/// <summary>
/// 把流式的上游响应重组成一条非流式的 `chat.completion`。
///
/// ## 为什么需要它
///
/// 上游**只支持流式**（`stream:false` 直接拒）。客户端要非流式时，
/// 网关只能自己把流收完再拼成整条消息。
///
/// 拼接规则对齐旧版 `collectChatCompletion`（`upstream.js`）：
///
/// · `content` / `reasoning_content` 逐片**字符串相接**
/// · `tool_calls` **按 index 合并**（同一次调用的 name 和 arguments 是
///   分好几个片来的；`arguments` 是 JSON 字符串的碎片，接错了就不是合法 JSON）
/// · 终止片的 `usage` 原样保留（含上游私有字段 —— 记账要用 credit）
///
/// ⚠ `id` / `model` / `created` 一旦上游给过就用上游的：换过模型重试时，
/// 客户端看到的模型名必须和实际用的一致。
/// </summary>
public sealed class ChatStreamCollector
{
    private readonly Dictionary<int, ToolCallBuilder> _toolCalls = [];
    private readonly System.Text.StringBuilder _content = new();
    private readonly System.Text.StringBuilder _reasoning = new();

    private string? _id;
    private string? _model;
    private long? _created;
    private string? _finishReason;

    /// <summary>上游给的 usage（原样，含私有字段）。</summary>
    public JsonNode? Usage { get; private set; }

    /// <summary>吃一片。传 <see cref="SseSanitizer.Process"/> 吐出来的东西。</summary>
    public void Feed(in SseChunk chunk)
    {
        if (chunk.IsDone) return;

        // usage 先记（清洗后的分片里，usage 已经裁成三个字段了，
        // 但 SseChunk.Usage 是裁之前的快照）
        if (chunk.Usage is not null) Usage = chunk.Usage;

        if (chunk.Raw is null) return;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(chunk.Raw);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (node is not JsonObject obj) return;

        if (obj["id"] is JsonValue idv && idv.TryGetValue<string>(out var id) && id.Length > 0) _id = id;
        if (obj["model"] is JsonValue mv && mv.TryGetValue<string>(out var model) && model.Length > 0) _model = model;
        if (obj["created"] is JsonValue cv && cv.TryGetValue<long>(out var created)) _created = created;

        if (chunk.ContentDelta.Length > 0) _content.Append(chunk.ContentDelta);
        if (chunk.ReasoningDelta.Length > 0) _reasoning.Append(chunk.ReasoningDelta);
        if (chunk.FinishReason is not null) _finishReason = chunk.FinishReason;

        CollectToolCalls(obj);
    }

    /// <summary>
    /// 按 index 合并 tool_calls 的分片。
    ///
    /// `arguments` 是**流式 JSON 碎片** —— 必须原样相接再交给客户端解析，
    /// 中途 parse 一次就会失败（比如只收到 `{"pa`）。
    /// </summary>
    private void CollectToolCalls(JsonObject obj)
    {
        if (obj["choices"] is not JsonArray choices) return;

        foreach (var choice in choices)
        {
            if (choice is not JsonObject c) continue;
            if (c["delta"] is not JsonObject delta) continue;
            if (delta["tool_calls"] is not JsonArray calls) continue;

            foreach (var call in calls)
            {
                if (call is not JsonObject tc) continue;

                var index = 0;
                if (tc["index"] is JsonValue iv && iv.TryGetValue<int>(out var idx)) index = idx;

                if (!_toolCalls.TryGetValue(index, out var builder))
                {
                    builder = new ToolCallBuilder();
                    _toolCalls[index] = builder;
                }

                if (tc["id"] is JsonValue idv && idv.TryGetValue<string>(out var id) && id.Length > 0) builder.Id = id;
                if (tc["type"] is JsonValue tv && tv.TryGetValue<string>(out var type) && type.Length > 0) builder.Type = type;

                if (tc["function"] is JsonObject fn)
                {
                    if (fn["name"] is JsonValue nv && nv.TryGetValue<string>(out var name) && name.Length > 0)
                    {
                        builder.Name.Append(name);
                    }
                    if (fn["arguments"] is JsonValue av && av.TryGetValue<string>(out var args))
                    {
                        builder.Arguments.Append(args);
                    }
                }
            }
        }
    }

    /// <summary>有没有收到任何内容（用来决定 content 是 null 还是 ""）。</summary>
    private bool HasContent => _content.Length > 0 || _reasoning.Length > 0;

    /// <summary>
    /// 合并后的 `tool_calls` —— **网关自己要执行它们**（媒体工具）。
    ///
    /// ## 为什么需要这个出口
    ///
    /// 上游把工具调用当一个"结束条件"交给客户端（`finish_reason: "tool_calls"`），
    /// 默认语义是**客户端执行完再发第二轮**。
    ///
    /// 但我们是网关：模型调 `workbuddy_generate_image` 时，
    /// 客户端（我们的 WPF 界面）**没有能力执行它** —— 执行要拿账号、调上游、落盘。
    /// 所以必须由网关执行，然后把结果拼进回复。
    ///
    /// 用户实测反馈「聊天测试生成图片和视频没法在回复框看见」，根因就在这里：
    /// 收集器**已经**把 `tool_calls` 合出来了，但没有任何人读它、执行它。
    ///
    /// 返回的是**克隆**：调用方拿去构造请求时不会改动收集器内部状态
    /// （那份状态还要用于 `Build()` 输出给客户端）。
    /// </summary>
    public IReadOnlyList<JsonObject> ToolCalls
        => _toolCalls.OrderBy(kv => kv.Key).Select(kv => kv.Value.Build()).ToList();

    /// <summary>
    /// 拼成最终的 `chat.completion`。
    ///
    /// @param fallbackModel 上游一个字节都没给模型名时用的（客户端请求的那个）。
    /// @param fallbackId    上游没给 id 时生成的。
    /// </summary>
    public JsonObject Build(string fallbackModel, string fallbackId)
    {
        if (_id is null) _id = fallbackId;

        var message = new JsonObject { ["role"] = "assistant" };

        // 有 tool_calls 但正文为空时 content 必须是 null —— 这是 OpenAI 的约定，
        // 给空字符串会让某些客户端认为「模型说了一句空话」。
        if (_content.Length == 0 && _toolCalls.Count > 0) message["content"] = null;
        else message["content"] = _content.ToString();

        if (_reasoning.Length > 0) message["reasoning_content"] = _reasoning.ToString();

        if (_toolCalls.Count > 0)
        {
            var array = new JsonArray();
            foreach (var (_, call) in _toolCalls.OrderBy(kv => kv.Key))
            {
                array.Add(call.Build());
            }
            message["tool_calls"] = array;
        }

        var choice = new JsonObject
        {
            ["index"] = 0,
            ["message"] = message,
            ["logprobs"] = null,
            ["finish_reason"] = _finishReason ?? "stop",
        };

        var completion = new JsonObject
        {
            ["id"] = _id,
            ["object"] = "chat.completion",
            ["created"] = _created ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = _model ?? fallbackModel,
            ["choices"] = new JsonArray(choice),
        };

        if (Usage is not null) completion["usage"] = Usage.DeepClone();

        return completion;
    }

    /// <summary>一个 tool_call 的累积器。</summary>
    private sealed class ToolCallBuilder
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "function";
        public System.Text.StringBuilder Name { get; } = new();
        public System.Text.StringBuilder Arguments { get; } = new();

        public JsonObject Build()
        {
            var arguments = Arguments.ToString();

            // 上游有时给空 arguments（无参调用）。空字符串不是合法 JSON，
            // 严格客户端解析会炸 —— 补成 `{}`。
            if (arguments.Length == 0) arguments = "{}";

            return new JsonObject
            {
                ["id"] = Id,
                ["type"] = Type,
                ["function"] = new JsonObject
                {
                    ["name"] = Name.ToString(),
                    ["arguments"] = arguments,
                },
            };
        }
    }
}
