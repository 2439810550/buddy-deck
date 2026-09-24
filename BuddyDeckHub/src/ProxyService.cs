using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace BuddyDeckHub;

/// <summary>
/// 代理核心 —— 把请求转发给某个 workbuddy_api 实例，边转发边记账。
///
/// ## 为什么必须真流式
///
/// Claude Code / Codex 的一次长回答会挂几十秒。如果这里把响应体缓冲下来
/// （await response.Content.ReadAsStringAsync），客户端会一直收不到数据，
/// 到超时为止。必须做到：上游吐一个字，客户端立刻收到一个字。
///
/// 具体做法：HttpCompletionOption.ResponseHeadersRead + 手动逐块拷贝。
///
/// ## 为什么重试只能在「流开始前」
///
/// 一旦开始往客户端写字节，就没法再换渠道了 —— 那会变成两个回答拼接，
/// 客户端拿到的是坏数据。所以重试循环必须在第一个字节到达之前完成判断。
/// 这和 one-api 的做法一致。
/// </summary>
public sealed class ProxyService
{
    private readonly HubStore _store;
    private readonly ChannelRouter _router;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ProxyService> _log;

    /// <summary>单次请求总超时。长回答留够时间，但不至于永久挂住。</summary>
    private static readonly TimeSpan UpstreamTimeout = TimeSpan.FromMinutes(10);

    /// <summary>这些请求头不能转发 —— 要么是逐跳的，要么得由我们自己的连接决定。</summary>
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "connection", "keep-alive", "transfer-encoding", "upgrade",
        "proxy-authenticate", "proxy-authorization", "te", "trailer",
        "content-length", "accept-encoding",   // 压缩交给上游处理，我们原样透传 body
    };

    public ProxyService(HubStore store, ChannelRouter router,
                        IHttpClientFactory httpFactory, ILogger<ProxyService> log)
    {
        _store = store;
        _router = router;
        _httpFactory = httpFactory;
        _log = log;
    }

    public sealed record ProxyContext(
        Member Member,
        ApiToken Token,
        Group Group,
        string Path,
        string Query);

    /// <summary>
    /// 转一次请求。返回 false 表示「已经写回错误响应了」，调用方不用再处理。
    /// </summary>
    public async Task<bool> ForwardAsync(HttpContext http, ProxyContext ctx, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();

        // ── 1. 读请求体 ──
        var body = await ReadBodyAsync(http.Request, ct);
        string model = "";
        string? errorKind = null;
        long maxTokens = 0, promptGuess = 0;
        bool streamed = false;

        if (ctx.Path.Contains("chat/completions", StringComparison.Ordinal) || body.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(body.Length > 0 ? body : "{}"u8.ToArray());
                var info = RequestInspector.Inspect(doc.RootElement);
                model = info.Model;
                maxTokens = info.MaxTokens;
                promptGuess = info.EstimatedPromptTokens;
                streamed = info.Stream;

                // 补 include_usage，让流式请求也带回 usage，Hub 才记得准
                body = RequestInspector.EnsureIncludeUsage(body, doc.RootElement, ctx.Path);
            }
            catch (JsonException)
            {
                // 解析不了就原样转发，让上游去报错
            }
        }

        // 模型名没给的话（比如 /v1/models 之外的怪请求），不拦
        if (model != "" && !ChannelRouter.GroupAllows(ctx.Group, model))
        {
            await WriteErrorAsync(http, 403, "model_not_allowed",
                $"分组「{ctx.Group.Name}」不允许使用模型 {model}", "model_denied");
            return true;
        }

        // ── 2. 配额预检 ──
        var day = DateTime.Now.ToString("yyyy-MM-dd");
        var usedToday = await _store.TodayQuotaAsync(ctx.Member.Id, day);

        // 免费模型（倍率 0）不占配额，直接放行 —— 这是「免费组」的核心
        var isFree = QuotaCalculator.IsFree(model) || ctx.Group.Rate <= 0;

        if (!isFree && ctx.Member.DailyQuota > 0 && usedToday >= ctx.Member.DailyQuota)
        {
            await WriteQuotaErrorAsync(http, ctx, usedToday, day);
            return true;
        }

        // 限速
        if (ctx.Member.RateLimitPerMinute > 0)
        {
            var recent = await _store.RecentCallsAsync(ctx.Member.Id, 60);
            if (recent >= ctx.Member.RateLimitPerMinute)
            {
                http.Response.StatusCode = 429;
                await WriteErrorAsync(http, 429, "rate_limit_exceeded",
                    $"请求太频繁，每分钟最多 {ctx.Member.RateLimitPerMinute} 次", "local_rate_limit");
                return true;
            }
        }

        // ── 3. 依次试 ──
        //
        // 用 Acquire 一个一个拿（原子「选 + 占位」），而不是先拿一堆候选再进。
        // 先看一遍只是为了在完全没渠道时给一个清楚的错误。
        if (_router.Candidates(ctx.Group, model).Count == 0)
        {
            await WriteErrorAsync(http, 503, "no_channel_available",
                model == ""
                    ? "没有可用的渠道"
                    : $"模型 {model} 当前没有可用渠道（可能都在冷却中）", "no_channel");
            return true;
        }

        HttpResponseMessage? upstream = null;
        Channel? chosen = null;
        string? lastError = null;
        var lastStatus = 503;
        var tried = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var channel = _router.Acquire(ctx.Group, model, tried);
            if (channel is null) break;          // 没得试了
            tried.Add(channel.Id);

            try
            {
                var (resp, err) = await TryOnceAsync(http, channel, body, ct);
                if (resp is not null)
                {
                    upstream = resp;
                    chosen = channel;
                    break;
                }
                lastError = err;
                lastStatus = _lastStatus;
            }
            finally
            {
                // 成功了就保持占位（直到响应写完），失败就立刻释放让下一个人用
                if (upstream is null) _router.Release(channel);
            }

            if (ct.IsCancellationRequested) break;
        }

        if (upstream is null || chosen is null)
        {
            // 所有渠道都试过了。选一个把最后一个错误返回给用户。
            await RecordAsync(ctx, model, chosen?.Id ?? "-", 0, 0, 0,
                started, lastStatus, errorKind ?? "all_channels_failed", streamed);
            await WriteErrorAsync(http, lastStatus == 503 ? 503 : lastStatus,
                "upstream_unavailable",
                $"所有渠道都失败了。最后一个错误：{lastError ?? "未知"}", "all_channels_failed");
            return true;
        }

        // ── 5. 开始转发（从这行起不能再换渠道了）──
        try
        {
            _router.MarkSuccess(chosen);

            var sniffer = new UsageSniffer();
            int responseBytes;
            long actualPrompt = promptGuess, actualCompletion = 0;

            if (streamed)
            {
                responseBytes = await PumpStreamAsync(http, upstream, sniffer, ct);
            }
            else
            {
                var payload = await upstream.Content.ReadAsByteArrayAsync(ct);
                responseBytes = payload.Length;
                sniffer.Feed(Encoding.UTF8.GetString(payload));

                if (!http.Response.HasStarted)
                {
                    http.Response.StatusCode = (int)upstream.StatusCode;
                    foreach (var h in upstream.Headers)
                        if (!HopByHop.Contains(h.Key))
                            http.Response.Headers[h.Key] = h.Value.ToArray();

                    if (upstream.Content.Headers.TryGetValues("content-type", out var ctv))
                        http.Response.ContentType = ctv.First();

                    await http.Response.Body.WriteAsync(payload, ct);
                }
            }

            // ── 6. 结算 ──
            sniffer.FallbackEstimate((int)Math.Min(promptGuess * 3, int.MaxValue), responseBytes);
            actualPrompt = sniffer.PromptTokens > 0 ? sniffer.PromptTokens : promptGuess;
            actualCompletion = sniffer.CompletionTokens;

            var quota = isFree ? 0 : QuotaCalculator.Compute(model, actualPrompt, actualCompletion, ctx.Group.Rate);

            await RecordAsync(ctx, model, chosen.Id, actualPrompt, actualCompletion, quota,
                started, (int)upstream.StatusCode, null, streamed);

            _log.LogInformation("OK {Member} {Model} {Channel} in={In} out={Out} quota={Q} {Ms}ms",
                ctx.Member.Name, model, chosen.Id, actualPrompt, actualCompletion, quota, started.ElapsedMilliseconds);

            return true;
        }
        catch (OperationCanceledException)
        {
            // 客户端断开。已经产生的 token 照记 —— 那些确实消耗掉了。
            await RecordAsync(ctx, model, chosen.Id, promptGuess, 0, isFree ? 0 :
                QuotaCalculator.Compute(model, promptGuess, 0, ctx.Group.Rate),
                started, 499, "client_disconnected", streamed);
            return true;
        }
        finally
        {
            _router.Release(chosen);
            upstream.Dispose();
        }
    }

    private int _lastStatus = 503;

    /// <summary>
    /// 试一个渠道。成功返回 response；失败返回错误描述（并且已经记过健康状态）。
    /// </summary>
    private async Task<(HttpResponseMessage?, string?)> TryOnceAsync(
        HttpContext http, Channel channel, byte[] body, CancellationToken ct)
    {
        var httpClient = _httpFactory.CreateClient("upstream");
        var target = channel.BaseUrl.TrimEnd('/') + "/" + http.Request.Path.ToString().TrimStart('/');
        if (http.Request.QueryString.HasValue) target += http.Request.QueryString.Value;

        using var req = new HttpRequestMessage(new HttpMethod(http.Request.Method), target);

        if (body.Length > 0)
            req.Content = new ByteArrayContent(body);

        // 透传客户端头（除逐跳的），但认证换成这个渠道自己的 key
        foreach (var h in http.Request.Headers)
        {
            if (HopByHop.Contains(h.Key)) continue;
            if (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            if (!req.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray()))
                req.Content?.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
        }
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + channel.ApiKey);

        // 关键：Host 必须写回环。workbuddy_api 有 DNS-rebinding 防护，
        // Host 不是 loopback 直接 403 —— 这是它的安全边界，别绕。
        req.Headers.Host = new Uri(channel.BaseUrl).Authority;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(UpstreamTimeout);

        try
        {
            var resp = await httpClient.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if (resp.IsSuccessStatusCode) return (resp, null);

            // 失败：读错误体、分类、决定要不要换渠道
            var errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(timeoutCts.Token); }
            catch { /* 读不到就算了 */ }

            var verdict = UpstreamError.Classify((int)resp.StatusCode, errBody);
            var preview = errBody.Length > 300 ? errBody[..300] : errBody;

            _lastStatus = (int)resp.StatusCode;

            if (verdict.Retryable)
            {
                _router.MarkFailure(channel, $"{verdict.Kind} {(int)resp.StatusCode}: {preview}", verdict.CoolSeconds);
                resp.Dispose();
                return (null, $"{channel.Name} → {verdict.Kind} {(int)resp.StatusCode}");
            }

            // 不可重试（客户端请求本身有问题）：直接返回这个响应，
            // 让用户看到上游的真实错误，比 Hub 自己编一个有用得多。
            _router.MarkSuccess(channel);
            return (resp, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _router.MarkFailure(channel, "超时");
            _lastStatus = 504;
            return (null, $"{channel.Name} → 超时");
        }
        catch (HttpRequestException ex)
        {
            _router.MarkFailure(channel, $"连不上：{ex.Message}");
            _lastStatus = 502;
            return (null, $"{channel.Name} → 连不上");
        }
        catch (OperationCanceledException)
        {
            _lastStatus = 499;
            return (null, "客户端断开");
        }
    }

    /// <summary>
    /// 逐块搬运 SSE。这是「真流式」的实现 —— 不缓冲、不等待、收到即转。
    ///
    /// 同时把每一块喂给 UsageSniffer，流结束后就有准确的 token 数。
    /// 之所以能这样一边转一边扫：usage 字段是纯文本的 JSON 片段，
    /// 不需要理解整个流的结构。
    /// </summary>
    private static async Task<int> PumpStreamAsync(
        HttpContext http, HttpResponseMessage upstream, UsageSniffer sniffer, CancellationToken ct)
    {
        http.Response.StatusCode = (int)upstream.StatusCode;

        foreach (var h in upstream.Headers)
            if (!HopByHop.Contains(h.Key))
                http.Response.Headers[h.Key] = h.Value.ToArray();

        if (upstream.Content.Headers.TryGetValues("content-type", out var ctv))
            http.Response.ContentType = ctv.First();

        // 打掉缓冲，让每个 chunk 立刻出去
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Response.Headers.CacheControl = "no-cache";

        await http.Response.StartAsync(ct);

        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8192];
        var total = 0;
        var scan = new StringBuilder();

        await using var source = await upstream.Content.ReadAsStreamAsync(ct);

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read <= 0) break;

            // 先转出去 —— 这一步必须在上游刚吐出来时就做
            await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;

            // 再顺手扫 usage（用解码器处理跨块的 UTF-8 边界）
            var n = decoder.GetChars(buffer, 0, read, chars, 0);
            if (n > 0)
            {
                scan.Append(chars, 0, n);
                // 定期扫，别让 buffer 无限涨；留一点尾巴防止把 JSON 切一半
                if (scan.Length > 4096)
                {
                    sniffer.Feed(scan.ToString());
                    scan.Remove(0, scan.Length - 512);
                }
            }

            await http.Response.Body.FlushAsync(ct);
        }

        if (scan.Length > 0) sniffer.Feed(scan.ToString());

        try { await http.Response.CompleteAsync(); } catch { /* 客户端已经走了 */ }
        return total;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private async Task RecordAsync(ProxyContext ctx, string model, string channelId,
        long prompt, long completion, long quota, Stopwatch sw, int status,
        string? errorKind, bool streamed)
    {
        // 记一下这把令牌的最后使用时间。管理界面靠它回答「谁还在用」。
        // 只在内存里改，不每次都落盘 —— 高频调用下写配置文件太亏。
        ctx.Token.LastUsedAt = DateTimeOffset.Now;

        try
        {
            await _store.RecordAsync(new CallRecord
            {
                Day = DateTime.Now.ToString("yyyy-MM-dd"),
                MemberId = ctx.Member.Id,
                MemberName = ctx.Member.Name,
                TokenPrefix = ctx.Token.Prefix,
                ChannelId = channelId,
                Model = model == "" ? "-" : model,
                PromptTokens = prompt,
                CompletionTokens = completion,
                Quota = quota,
                LatencyMs = sw.ElapsedMilliseconds,
                Status = status,
                ErrorKind = errorKind,
                Streamed = streamed,
            });
        }
        catch (Exception ex)
        {
            // 记账失败不能影响已经成功的回答
            _log.LogError(ex, "记账失败");
        }
    }

    private static async Task WriteErrorAsync(HttpContext http, int status, string code,
        string message, string kind)
    {
        if (http.Response.HasStarted) return;
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";

        var payload = JsonSerializer.Serialize(new
        {
            error = new
            {
                message,
                type = kind,
                code,
            },
        }, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        await http.Response.WriteAsync(payload);
    }

    private static Task WriteQuotaErrorAsync(HttpContext http, ProxyContext ctx, long used, string day)
    {
        var reset = DateTime.Today.AddDays(1);
        return WriteErrorAsync(http, 429, "insufficient_quota",
            $"今日配额已用完（{QuotaCalculator.Human(used)} / {QuotaCalculator.Human(ctx.Member.DailyQuota)}）。" +
            $"北京时间 {reset:MM-dd HH:mm} 重置。" +
            $"提示：免费模型（deepseek-v4.1-flash、glm-5.3-flash）不消耗配额，现在就能用。",
            "quota_exceeded");
    }
}
