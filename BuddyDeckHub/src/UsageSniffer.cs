using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuddyDeckHub;

/// <summary>
/// 从响应流里把 usage 抠出来。
///
/// 为什么必须自己抠：workbuddy_api 没有「查余额」接口
/// （/v2/billing/meter/balance 之类的都是 404），上游也不告诉你扣了多少。
/// 唯一可靠的办法是读每次响应里的 usage，自己记账。
///
/// 三种协议的字段名不一样：
///   OpenAI Chat    usage.prompt_tokens / usage.completion_tokens
///   Anthropic      usage.input_tokens  / usage.output_tokens
///   Responses      usage.input_tokens  / usage.output_tokens
///
/// 流式时这些字段在靠后的分片里出现（OpenAI 是最后一个 chunk，
/// Anthropic 在 message_start 和 message_delta 各报一半）。
/// 所以要么扫到结尾，要么边转发边找。
/// </summary>
public sealed partial class UsageSniffer
{
    public long PromptTokens { get; private set; }
    public long CompletionTokens { get; private set; }
    public bool Found { get; private set; }

    public long TotalTokens => PromptTokens + CompletionTokens;

    [GeneratedRegex(@"""prompt_tokens""\s*:\s*(\d+)", RegexOptions.Compiled)]
    private static partial Regex OpenAiPrompt();

    [GeneratedRegex(@"""completion_tokens""\s*:\s*(\d+)", RegexOptions.Compiled)]
    private static partial Regex OpenAiCompletion();

    [GeneratedRegex(@"""input_tokens""\s*:\s*(\d+)", RegexOptions.Compiled)]
    private static partial Regex AnthropicInput();

    [GeneratedRegex(@"""output_tokens""\s*:\s*(\d+)", RegexOptions.Compiled)]
    private static partial Regex AnthropicOutput();

    /// <summary>
    /// 拿一段文本喂进来。可以是完整的 JSON 体，也可以是 SSE 里的一行。
    ///
    /// 用正则而不是 JSON 解析：SSE 行是碎片，很多分片根本不是合法 JSON 的完整对象；
    /// 而且我们要扫的是「某个字段出现过」，不需要理解结构。
    ///
    /// 关键细节：**非零才采纳**。Anthropic 的 message_start 里 input_tokens 恒为 0，
    /// 真值在 message_delta；OpenAI 的中间分片也会带 usage:null。
    /// 不加这个判断的话，最后出现的那个 0 会把真实值覆盖掉。
    /// </summary>
    public void Feed(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return;
        var s = text.ToString();

        // 关键：用 Matches() 而不是 Match()。
        //
        // Anthropic 的流里 "output_tokens" 会出现两次：
        //   message_start  → {"input_tokens":0,"output_tokens":0}   ← 全是 0
        //   message_delta  → {"output_tokens":2}                    ← 真值
        //
        // Match() 只拿第一个，也就是那个 0，于是永远记不到真实用量。
        // 所以必须扫全部出现位置，取「最大的非零值」。
        // ——这个 bug 是拿真实 SSE 回放测出来的（.snifftest）。

        foreach (Match m in AnthropicInput().Matches(s))
            if (long.TryParse(m.Groups[1].Value, out var inp) && inp > PromptTokens)
            {
                PromptTokens = inp;
                Found = true;
            }

        foreach (Match m in AnthropicOutput().Matches(s))
            if (long.TryParse(m.Groups[1].Value, out var outp) && outp > CompletionTokens)
            {
                CompletionTokens = outp;
                Found = true;
            }

        foreach (Match m in OpenAiPrompt().Matches(s))
            if (long.TryParse(m.Groups[1].Value, out var p2) && p2 > PromptTokens)
            {
                PromptTokens = p2;
                Found = true;
            }

        foreach (Match m in OpenAiCompletion().Matches(s))
            if (long.TryParse(m.Groups[1].Value, out var c2) && c2 > CompletionTokens)
            {
                CompletionTokens = c2;
                Found = true;
            }
    }

    /// <summary>
    /// 非流式响应：直接给完整 body。
    /// </summary>
    public static UsageSniffer From(ReadOnlySpan<char> body)
    {
        var s = new UsageSniffer();
        s.Feed(body);
        return s;
    }

    /// <summary>没拿到 usage 时，用字节数粗估，至少不至于记 0。</summary>
    public void FallbackEstimate(int promptChars, int responseBytes)
    {
        if (Found) return;
        // 中文约 1 token ≈ 1.5 字符，英文约 4 字符。取保守的 3。
        PromptTokens = Math.Max(1, promptChars / 3);
        CompletionTokens = Math.Max(1, responseBytes / 3);
    }
}

/// <summary>
/// 请求体的小工具：估算输入长度、读出 model 和 max_tokens。
/// </summary>
public static class RequestInspector
{
    public sealed record Info(string Model, long MaxTokens, int PromptChars, bool Stream)
    {
        /// <summary>粗略的输入 token 估算（字符数 / 3）。真实值以响应里的 usage 为准。</summary>
        public long EstimatedPromptTokens => Math.Max(1, PromptChars / 3);
    }

    public static Info Inspect(JsonElement body)
    {
        var model = body.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() ?? "" : "";

        long maxTokens = 0;
        foreach (var key in new[] { "max_tokens", "max_completion_tokens", "max_output_tokens" })
            if (body.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
                maxTokens = v.GetInt64();

        var stream = body.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;

        // 输入量的估算：把 messages / input / system 的体积加起来。
        // 不精确，只用于预扣估算和「请求过大」的粗筛。
        var chars = 0;
        foreach (var key in new[] { "messages", "input", "system", "instructions", "prompt" })
            if (body.TryGetProperty(key, out var v))
                chars += v.GetRawText().Length;

        if (body.TryGetProperty("tools", out var t)) chars += t.GetRawText().Length;

        return new Info(model, maxTokens, chars, stream);
    }

    /// <summary>
    /// 给 OpenAI Chat 请求补上 stream_options.include_usage，
    /// 否则有些客户端拿不到 usage，Hub 就没法准确记账。
    /// 已经设了就不动。
    /// </summary>
    public static byte[] EnsureIncludeUsage(byte[] original, JsonElement body, string path)
    {
        if (!path.Contains("chat/completions", StringComparison.Ordinal)) return original;
        if (!body.TryGetProperty("stream", out var s) || s.ValueKind != JsonValueKind.True) return original;
        if (body.TryGetProperty("stream_options", out _)) return original;

        try
        {
            using var doc = JsonDocument.Parse(original);
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(original);
            if (dict is null) return original;

            var outDict = new Dictionary<string, object?>();
            foreach (var (k, v) in dict) outDict[k] = v;
            outDict["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };

            return JsonSerializer.SerializeToUtf8Bytes(outDict);
        }
        catch
        {
            return original;   // 改不动就原样发，不影响主流程
        }
    }
}
