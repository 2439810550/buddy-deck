
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using WorkBuddyPanel.Models;

namespace WorkBuddyPanel.Services;

/// <summary>一次流式响应里的一次增量。</summary>
public readonly record struct ChatDelta(string? Content, string? Reasoning, string? FinishReason, string? Error);

/// <summary>对话历史里的一条消息（发给上游用）。</summary>
public sealed record ChatTurn(string Role, string Content);

/// <summary>
/// 跟本机网关的 /v1/chat/completions 打交道。
/// 上游只支持流式，所以这里永远发 stream: true，由界面自己把增量拼起来。
/// </summary>
public sealed class ChatStreamClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ChatStreamClient(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    }

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        string model,
        IReadOnlyList<ChatTurn> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = history
                .Select(t => new Dictionary<string, object?> { ["role"] = t.Role, ["content"] = t.Content })
                .ToList(),
        };

        var json = JsonSerializer.Serialize(body, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = new StringContent(json, new UTF8Encoding(false), "application/json"),
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(ExtractErrorMessage(raw) ?? $"网关返回 HTTP {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;

            if (payload == "[DONE]") yield break;

            var chunk = TryParseChunk(payload);
            if (chunk is null) continue;

            if (chunk.Error?.Message is { Length: > 0 } streamError)
            {
                yield return new ChatDelta(null, null, null, streamError);
                yield break;
            }

            var choice = chunk.Choices?.FirstOrDefault();
            if (choice is null) continue;

            var delta = new ChatDelta(
                choice.Delta?.Content,
                choice.Delta?.ReasoningContent,
                string.IsNullOrEmpty(choice.FinishReason) ? null : choice.FinishReason,
                null);

            if (delta.Content is not null || delta.Reasoning is not null || delta.FinishReason is not null)
                yield return delta;
        }
    }

    /// <summary>分片坏掉不该打断整轮回答，解不开就返回 null 跳过。</summary>
    private static StreamChunk? TryParseChunk(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<StreamChunk>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把网关返回的错误信封拆成人话。</summary>
    private static string? ExtractErrorMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var envelope = JsonSerializer.Deserialize<ErrorEnvelope>(raw, JsonOptions);
            if (envelope?.Error?.Message is { Length: > 0 } message) return message;
        }
        catch (JsonException)
        {
            // 不是 JSON，原样回传
        }

        return raw.Length > 400 ? raw[..400] : raw;
    }

    public void Dispose() => _http.Dispose();
}
