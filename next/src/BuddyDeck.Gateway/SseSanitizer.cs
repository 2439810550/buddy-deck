using System.Text;
using System.Text.Json.Nodes;

namespace BuddyDeck.Gateway;

/// <summary>
/// 从一个上游 SSE 分片里抽出来的东西。
///
/// @param Raw           原始 data 载荷（未经改写的 JSON 文本）。null 表示丢弃。
/// @param IsDone        是不是终止标记 `[DONE]`。
/// @param ContentDelta  本片的正文增量。
/// @param ReasoningDelta 本片的推理增量（`reasoning_content`）。
/// @param Usage         本片携带的 usage（通常是终止片才有）。
/// @param FinishReason  本片的结束原因。
/// </summary>
public readonly record struct SseChunk(
    string? Raw,
    bool IsDone,
    string ContentDelta,
    string ReasoningDelta,
    JsonNode? Usage,
    string? FinishReason);

/// <summary>
/// 上游 SSE 分片的清洗与重组。
///
/// ## 为什么要清洗
///
/// 上游有两个习惯会让严格的 OpenAI 客户端解析失败（见旧版 `sanitizeChunk`）：
///
/// 1. **每个分片都带一个空的 `delta.function_call`**（有些分片是
///    `null`）。真 tool_calls 旁边挂一个空 function_call，某些客户端会
///    凭空多出一个并行调用。
/// 2. **终止分片的 usage 带厂商私有计数器**。客户端只认
///    `prompt_tokens` / `completion_tokens` / `total_tokens`，
///    多出来的字段不影响解析但会污染客户端显示。
///
/// `reasoning_content` 要**保留** —— 好几个客户端会渲染它。
/// </summary>
public static class SseSanitizer
{
    private static readonly string[] UsageKeep = ["prompt_tokens", "completion_tokens", "total_tokens"];

    /// <summary>
    /// 清洗一个 data 载荷。
    ///
    /// @returns 处理后的分片；`Raw` 为 null 表示这一片该丢掉。
    /// </summary>
    public static SseChunk Process(string payload)
    {
        if (payload.Length == 0) return new SseChunk(null, false, "", "", null, null);

        // `[DONE]` 不是 JSON —— 必须在这里认出来，否则会被当成畸形片丢掉，
        // 客户端就永远等不到流结束。
        if (payload.AsSpan().Trim().SequenceEqual("[DONE]")) return new SseChunk(null, true, "", "", null, null);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            // 分片不完整/不是 JSON —— 原样转发，绝不因为我们的清洗把流转坏
            return new SseChunk(payload, false, "", "", null, null);
        }

        if (node is not JsonObject chunk)
        {
            return node is null ? new SseChunk(null, false, "", "", null, null) : new SseChunk(payload, false, "", "", null, null);
        }

        var content = "";
        var reasoning = "";
        string? finishReason = null;

        if (chunk["choices"] is JsonArray choices)
        {
            foreach (var choice in choices)
            {
                if (choice is not JsonObject c) continue;
                CleanDelta(c);

                if (finishReason is null && c["finish_reason"] is JsonValue fr && fr.TryGetValue<string>(out var s) && s.Length > 0)
                {
                    finishReason = s;
                }
            }

            if (choices.Count > 0 && choices[0] is JsonObject first && first["delta"] is JsonObject delta)
            {
                if (delta["content"] is JsonValue cv && cv.TryGetValue<string>(out var cs)) content = cs;
                if (delta["reasoning_content"] is JsonValue rv && rv.TryGetValue<string>(out var rs)) reasoning = rs;
            }
        }

        var usage = SanitizeUsage(chunk);

        return new SseChunk(chunk.ToJsonString(), false, content, reasoning, usage, finishReason);
    }

    /// <summary>删掉上游那些会误导客户端的小垃圾。</summary>
    private static void CleanDelta(JsonObject choice)
    {
        if (choice["delta"] is not JsonObject delta) return;

        if (delta["function_call"] is JsonObject fc)
        {
            // 空 function_call 挨着真 tool_calls 会让某些客户端冒出幽灵调用
            var name = fc["name"]?.GetValue<string>() ?? "";
            var args = fc["arguments"]?.GetValue<string>() ?? "";
            if (name.Length == 0 && args.Length == 0) delta.Remove("function_call");
        }
        else if (delta.ContainsKey("function_call") && delta["function_call"] is null)
        {
            delta.Remove("function_call");
        }

        if (delta["reasoning_content"] is JsonValue rc && rc.TryGetValue<string>(out var rcs) && rcs.Length == 0)
        {
            delta.Remove("reasoning_content");
        }

        if (delta["refusal"] is JsonValue rf && rf.TryGetValue<string>(out var rfs) && rfs.Length == 0)
        {
            delta.Remove("refusal");
        }
    }

    /// <summary>
    /// 把 usage 裁成客户端认的三个字段。
    ///
    /// @returns 裁之前**拷一份**的原值。绝不能把原对象交出去 ——
    ///          下面的删除会把同一个对象上的 credit / 缓存字段一起删掉，
    ///          将来记账拿到的就只有 token 没有 credit（旧版踩过这个坑）。
    /// </summary>
    private static JsonNode? SanitizeUsage(JsonObject chunk)
    {
        if (chunk["usage"] is not JsonObject usage) return null;

        var snapshot = usage.DeepClone();

        var drop = usage.Select(kv => kv.Key).Where(k => !UsageKeep.Contains(k)).ToList();
        foreach (var key in drop) usage.Remove(key);

        return snapshot;
    }
}
