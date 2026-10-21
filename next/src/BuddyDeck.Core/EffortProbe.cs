using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Core;

/// <summary>一次档位探测的结论。</summary>
public sealed record EffortProbeResult
{
    /// <summary>上游接受了这个档位（HTTP 成功且流真的流出事件）。</summary>
    public required bool Accepted { get; init; }

    /// <summary>HTTP 状态码。0 表示连接层失败（DNS / TLS / 超时）。</summary>
    public required int Status { get; init; }

    /// <summary>响应体真的流出了事件（见到 `data:`）。</summary>
    public bool Streamed { get; init; }

    /// <summary>
    /// 上游的业务错误码（`extError.code`）。没有时为 null。
    ///
    /// 为什么单独一个字段而不是从 message 里抠：上游把**「这个档位不支持」**
    /// 表达成一个结构化的错误码。认出它就能区分「档位不行」和「网络不行」——
    /// 前者要换档位，后者要重试。
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>可读说明。失败时一定有值。</summary>
    public string Detail { get; init; } = "";

    /// <summary>有没有真的打上游。</summary>
    public bool UpstreamCalled { get; init; }
}

/// <summary>
/// 思考档位探测（M17，对齐旧版 `upstream.js:984-1052`）。
///
/// ## 它在问什么
///
/// 「上游**认不认**这个 `reasoning_effort` 值？」
///
/// 目录里写着某个模型支持 `high`，但那**只是目录的说法**。真正能不能用，
/// 要发一个真实请求才知道 —— 有些档位是灰度发布的：目录已经列出来了，
/// 你的账号却还没开。
///
/// ## 两个关键设计（旧版注释里的原话，值得逐字保留）
///
/// ### ① 探活必须复用真实消息的身份
///
/// > A real streaming chat call carries the effort under test; it shares the
/// > normal chat headers on purpose so a result describes what a real message
/// > would experience.
/// >
/// > …the probe sibling must never present a different client than a real
/// > message would.
///
/// 探活**故意**用和真实聊天请求**一模一样**的头（同一个 UA、同一个
/// `X-IDE-Version`）。理由：上游按客户端身份分流，不同身份看到的能力集不同。
/// 用假身份探出来的结果**看起来是真实的**，但和实际请求能跑的不是一回事 ——
/// 这是「看起来对、实际错」的形态，最难查。
///
/// ⚠ 所以本类**不自己编 UA**，而是把调用方给的 `userAgent` / `clientVersion`
/// **原样透传**。探测方和聊天方必须由同一个来源提供这两个值。
///
/// ### ② 收到首帧就中止
///
/// 探活只需要知道「流是不是真的流起来了」，不需要读完。收到第一个 `data:`
/// 就 abort —— 一次探测只花一个 token 的钱，而不是把整个回复生成完。
///
/// 响应体是 `max_tokens: 1`（国内版）/ `16`（国际版，因为它要求更长的最小输出）。
///
/// ## 为什么不抛异常
///
/// 批量探测（界面上「检测这个模型支持哪些档位」）会连着发好几个请求。
/// 一个档位探测失败就抛出去的话，后面的全探不了 —— 和签到是同一个理由。
/// </summary>
public sealed partial class EffortProbe
{
    /// <summary>聊天补全路径。</summary>
    public const string ChatPath = "/v2/chat/completions";

    /// <summary>探测用的提示词（旧版就是这一句）。</summary>
    public const string ProbePrompt = "ping";

    /// <summary>国际版要求首条消息是 system，缺了会被拒。</summary>
    private const string InternationalSystemPrompt = "You are a helpful assistant.";

    /// <summary>错误正文最多读这么多字节。</summary>
    private const int ErrorBodyLimit = 4096;

    /// <summary>一次探测的超时。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly UpstreamVariant _variant;
    private readonly ILogger<EffortProbe> _log;

    public EffortProbe(HttpClient http, UpstreamVariant variant, ILogger<EffortProbe> log)
    {
        _http = http;
        _variant = variant;
        _log = log;
    }

    /// <summary>
    /// 探测一个档位。
    ///
    /// @param credential    凭证（复用现有链路）。
    /// @param model         要探的模型 id。
    /// @param effort        档位值。传 null 表示**探「不带档位」这个基线**
    ///                      —— 旧版 `if (effort !== undefined)` 就是这个语义，
    ///                      基线能跑说明模型本身可用，此时不支持档位才怪得有意义。
    /// @param userAgent     桌面身份 UA。**必须和真实聊天请求用同一个值。**
    /// @param clientVersion `X-IDE-Version`，同上必须配套。
    ///
    /// **不抛异常** —— 所有失败都编成 <see cref="EffortProbeResult"/>。
    /// 只有调用方主动取消才 rethrow。
    /// </summary>
    public async Task<EffortProbeResult> ProbeAsync(
        Credential credential,
        string model,
        string? effort,
        string? userAgent,
        string? clientVersion,
        CancellationToken ct = default)
    {
        var body = BuildBody(model, effort);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_variant.ChatBase}{ChatPath}");

            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            // ⚠ 和真实聊天请求**同一套头**（ApplyChat），身份从调用方透传。
            // 这就是旧版那条「probe sibling 不能换个客户端」的落地方式：
            // 不是两处各写一遍头，而是**同一个方法**。
            UpstreamHeaders.ApplyChat(request, credential, _variant, userAgent, clientVersion);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            LogTransportFailure(ex, model);
            return new EffortProbeResult
            {
                Accepted = false,
                Status = 0,
                Streamed = false,
                Detail = $"transport error: {ex.Message}",
                UpstreamCalled = true,
            };
        }

        using (response)
        {
            var status = (int)response.StatusCode;

            // 失败：读一段正文，认里面有没有结构化的业务错误码
            if (!response.IsSuccessStatusCode)
            {
                var text = await ReadErrorBodyAsync(response, ct).ConfigureAwait(false);
                var (errorCode, detail) = ErrorCodeOf(text);

                LogRejected(model, effort, status, errorCode ?? "");

                return new EffortProbeResult
                {
                    Accepted = false,
                    Status = status,
                    Streamed = false,
                    ErrorCode = errorCode,
                    Detail = detail,
                    UpstreamCalled = true,
                };
            }

            // 成功：读流，见到第一个 `data:` 就算真的流起来了
            var streamed = await ReadFirstEventAsync(response, ct).ConfigureAwait(false);

            return new EffortProbeResult
            {
                Accepted = streamed,
                Status = status,
                Streamed = streamed,
                // 200 但一个事件都没有：上游收下了请求却没开始生成。
                // 这**不算接受** —— 旧版同样把 streamed 当结论（`probeEffort` 返回
                // 的是 `{status, streamed}`，调用方看 streamed）。
                Detail = streamed ? "" : "上游返回 200 但流里没有任何事件",
                UpstreamCalled = true,
            };
        }
    }

    /// <summary>
    /// 把要探测的模型行转成请求体。对齐旧版 `upstream.js:990-999`。
    ///
    /// 注意 `stream: true` **必须写死** —— 上游只支持流式，
    /// 而探测的判据就是「流有没有流起来」。
    /// </summary>
    private string BuildBody(string model, string? effort)
    {
        var messages = new JsonArray();

        // 国际版要求首条消息是 system（和 RequestPipeline 同一条规则）
        if (_variant.IsInternational)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = InternationalSystemPrompt,
            });
        }

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = ProbePrompt,
        });

        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["messages"] = messages,
            // 国际版要 16（它的最小输出更长），国内版 1 就够
            ["max_tokens"] = _variant.IsInternational ? 16 : 1,
        };

        // ⚠ null 表示「探不带档位的基线」，此时**不写这个字段**
        // —— 写成 null 会让上游收到显式的 null，语义完全不同。
        if (effort is not null)
        {
            body["reasoning_effort"] = effort;
        }

        return body.ToJsonString();
    }

    /// <summary>
    /// 从错误正文里抠 `extError.code`。
    ///
    /// 对齐旧版 `errorCodeOf`（`upstream.js:1024-1034`）：只有形状确实是
    /// `{"extError":{"code":"…"}}` 才认，否则退化成「原文前 200 字」。
    ///
    /// **为什么严格**：`code` 会参与「是档位不支持还是别的错」的判断。
    /// 从一个不认识的形状里猜一个 code 出来，会让探测结论反向。
    /// </summary>
    private static (string? ErrorCode, string Detail) ErrorCodeOf(string text)
    {
        if (text.Length == 0) return (null, "");

        try
        {
            if (JsonNode.Parse(text) is JsonObject parsed
                && parsed["extError"] is JsonObject ext
                && ext["code"] is JsonValue codeValue
                && codeValue.TryGetValue<string>(out var code)
                && !string.IsNullOrEmpty(code))
            {
                return (code, code);
            }
        }
        catch (JsonException)
        {
            // 不是 JSON —— 退化成原文
        }

        return (null, text.Length <= 200 ? text : text[..200]);
    }

    /// <summary>
    /// 读流直到见到一个 `data:` 前缀。
    ///
    /// ## 为什么是「见到 data: 就算」而不是解析完整事件
    ///
    /// 探测只需要回答「这个请求真的开始产出了吗」。要求解析出合法 JSON
    /// 会把判据变成「上游的输出格式对不对」—— 那是另一件事，
    /// 而且上游发第一条 `data:` 时内容未必完整。
    ///
    /// ## 读到就停
    ///
    /// 见到即 `return true`，**不读完响应体** —— 剩下的交给 using 里的
    /// response.Dispose() 断开。这就是「只花一个 token」的实现方式。
    /// </summary>
    private static async Task<bool> ReadFirstEventAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[1024];

            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) return false;   // 流结束都没见到 data:

                // 跨块边界也可能切在 "data:" 中间，但那种情况下
                // 下一块会补齐 —— 这里只看**当前累积窗口**里有没有。
                // 用固定小块读，命中率足够且不需要维护跨块缓冲：
                // SSE 的行很短，1 KiB 足够覆盖任何单条 data: 前缀。
                if (ContainsDataPrefix(buffer.AsSpan(0, read))) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            // 读流出错 = 没流起来
            return false;
        }
    }

    /// <summary>这一块里有没有 `data:`。</summary>
    private static bool ContainsDataPrefix(ReadOnlySpan<byte> bytes)
    {
        // "data:" 的 ASCII 是 64 61 74 61 3A
        ReadOnlySpan<byte> needle = [0x64, 0x61, 0x74, 0x61, 0x3A];
        return bytes.IndexOf(needle) >= 0;
    }

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Length > ErrorBodyLimit ? text[..ErrorBodyLimit] : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return "";
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "档位探测连不上上游（模型 {Model}）")]
    private partial void LogTransportFailure(Exception ex, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "档位探测被拒：模型 {Model} 档位 {Effort} http {Status} code {Code}")]
    private partial void LogRejected(string model, string? effort, int status, string code);
}
