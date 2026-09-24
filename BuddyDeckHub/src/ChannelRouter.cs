namespace BuddyDeckHub;

/// <summary>
/// 选渠道 —— 照抄 one-api 的「筛 → 排序 → 依次重试」。
///
///   1. 按分组筛出可用渠道
///   2. 按优先级排序，同级按在途请求数（最少连接优先）
///   3. 从上往下试，遇到限流/故障就换下一个
///   4. 全失败才把错误返回给用户
///
/// 「最少连接优先」很关键：如果只按固定顺序，所有请求都会挤到第一个渠道，
/// 它一被限流就全线告警，而后面几个渠道全程闲着。
/// </summary>
public sealed class ChannelRouter
{
    private readonly HubStore _store;
    private readonly ILogger<ChannelRouter> _log;
    private readonly object _gate = new();

    public ChannelRouter(HubStore store, ILogger<ChannelRouter> log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>
    /// 给一个分组挑出候选渠道，按「该先试谁」排好序。
    /// </summary>
    public List<Channel> Candidates(Group group, string model)
    {
        var now = DateTimeOffset.Now;
        var all = _store.Config.Channels;

        IEnumerable<Channel> pool = group.Channels.Count > 0
            ? all.Where(c => group.Channels.Contains(c.Id))
            : all;

        return pool
            .Where(c => c.IsAvailable(now))
            // 渠道自己声明了模型清单的话，要包含请求的这个
            .Where(c => c.Models.Count == 0 ||
                        c.Models.Any(m => ModelMatches(m, model)))
            // 分组允许的模型也要过
            .Where(c => GroupAllows(group, model))
            .OrderBy(c => c.Priority)
            .ThenBy(c => Volatile.Read(ref c.InFlight))
            .ToList();
    }

    /// <summary>模型名匹配，支持末尾 * 通配。deepseek-* 能匹到 deepseek-v4-pro。</summary>
    public static bool ModelMatches(string pattern, string model)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith('*'))
            return model.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(pattern, model, StringComparison.OrdinalIgnoreCase);
    }

    public static bool GroupAllows(Group group, string model) =>
        group.Models.Count == 0 || group.Models.Any(p => ModelMatches(p, model));

    // ── 健康状态维护 ──

    public void MarkSuccess(Channel c)
    {
        lock (_gate)
        {
            c.ConsecutiveFailures = 0;
            c.DisabledUntil = null;
            c.LastOk = DateTimeOffset.Now;
            c.LastError = null;
        }
    }

    /// <summary>
    /// 一次失败。
    ///
    /// <paramref name="coolSeconds"/> 由上游错误类型决定：
    ///   限流 → 按上游给的窗口（默认 5 分钟），别急着再试
    ///   其它 → 指数退避，连续失败越多停越久
    ///
    /// 停用期间渠道仍在配置里，只是不接活；健康探针会继续试它。
    /// </summary>
    public void MarkFailure(Channel c, string error, int? coolSeconds = null)
    {
        lock (_gate)
        {
            c.ConsecutiveFailures++;
            c.LastError = error;

            var seconds = coolSeconds ?? Math.Min(300, 15 * (int)Math.Pow(2, Math.Min(c.ConsecutiveFailures - 1, 4)));
            c.DisabledUntil = DateTimeOffset.Now.AddSeconds(seconds);

            _log.LogWarning("渠道 {Id} 失败 {N} 次，冷却 {Sec}s：{Err}",
                c.Id, c.ConsecutiveFailures, seconds, error);
        }
    }

    public void Enter(Channel c) => Interlocked.Increment(ref c.InFlight);
    public void Exit(Channel c) => Interlocked.Decrement(ref c.InFlight);

    /// <summary>给管理界面看的健康快照。</summary>
    public List<object> Health()
    {
        var now = DateTimeOffset.Now;
        return _store.Config.Channels.Select(c => (object)new
        {
            id = c.Id,
            name = c.Name,
            enabled = c.Enabled,
            available = c.IsAvailable(now),
            baseUrl = c.BaseUrl,
            priority = c.Priority,
            groups = c.Groups,
            models = c.Models,
            inFlight = Volatile.Read(ref c.InFlight),
            consecutiveFailures = c.ConsecutiveFailures,
            disabledUntil = c.DisabledUntil?.ToString("HH:mm:ss"),
            lastOk = c.LastOk?.ToString("HH:mm:ss"),
            lastError = c.LastError,
        }).ToList();
    }
}

/// <summary>
/// 上游错误分类 —— 决定要不要重试、冷却多久。
///
/// 上游有三种「失败」，处理方式完全不同：
///   限流(6004/10081)  → 换渠道。/ 冷却 5 分钟
///   额度耗尽(402)     → 换渠道。这个号今天没额度了
///   其它 4xx/5xx      → 换渠道，指数退避
///   客户端请求本身错  → **不能换渠道**，换谁都一样，直接返给用户
/// </summary>
public static class UpstreamError
{
    public sealed record Verdict(bool Retryable, int? CoolSeconds, string Kind)
    {
        /// <summary>这个错误该不该换渠道重试。</summary>
        public bool Retryable { get; init; } = Retryable;
    }

    public static Verdict Classify(int status, string body)
    {
        var text = body.Length > 4000 ? body[..4000] : body;

        // 客户端自己写错了（请求结构、模型名不存在、认证错在自己这边）
        // —— 换渠道没用，直接返给用户，别浪费其它账号的健康
        if (status == 400 && !LooksLikeRateLimit(text)) return new(false, null, "bad_request");
        if (status == 404) return new(false, null, "not_found");
        if (status == 413) return new(false, null, "payload_too_large");

        if (status == 429 || LooksLikeRateLimit(text))
            return new(true, 300, "rate_limited");

        if (status == 402 || LooksLikeCreditExhausted(text))
            return new(true, 1800, "credit_exhausted");

        if (status == 401 || status == 403)
            return new(true, 600, "auth_expired");

        if (status >= 500) return new(true, null, "upstream_error");

        return new(false, null, $"http_{status}");
    }

    /// <summary>
    /// 上游的限流有时包在 400 里（实测过），所以不能只看状态码。
    /// 识别这些码和关键词。
    /// </summary>
    private static bool LooksLikeRateLimit(string text)
    {
        if (text.Contains("6004", StringComparison.Ordinal)) return true;
        if (text.Contains("6005", StringComparison.Ordinal)) return true;
        if (text.Contains("6006", StringComparison.Ordinal)) return true;
        if (text.Contains("10081", StringComparison.Ordinal)) return true;   // IP 层限流
        if (text.Contains("频率限制", StringComparison.Ordinal)) return true;
        if (text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool LooksLikeCreditExhausted(string text)
    {
        if (text.Contains("14018", StringComparison.Ordinal)) return true;
        if (text.Contains("额度已用完", StringComparison.Ordinal)) return true;
        if (text.Contains("insufficient credit", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
