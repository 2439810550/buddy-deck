using System.Globalization;

namespace BuddyDeck.App.Converters;

/// <summary>
/// 时间解析 —— 值转换器里所有时间处理都走这里。
///
/// ## 为什么要有这个类（HANDOVER 坑 2）
///
/// 保险库 JSON 里那几个时间字段**类型不一致**，而且是刻意的历史包袱：
///
///   expiresAtMs   int     1793731164678          ← 数字（Unix 毫秒）
///   addedAt       str     "2026-09-24T09:35:12.642Z"   ← ISO 字符串
///   lastSeenAt    str     "2026-10-06T18:39:24.680Z"
///
/// 上一版按数字读，读真实保险库直接抛 JsonException —— 而且**造数据永远发现不了**
/// （造的全是数字）。所以这里两种输入都必须吃，而且要有测试盯着。
///
/// ## 为什么这个类不依赖 WPF
///
/// 转换器（IValueConverter）不方便直接测。所以逻辑全部放在这个静态类里，
/// 转换器只做「调它 + 套画笔」两件事。这样边界（0 / 负数 / 超长 / null）
/// 都能用普通 xUnit 覆盖。
/// </summary>
public static class TimeText
{
    /// <summary>解析不出来时统一返回的占位符。不是空白 —— 空白让人以为没读到。</summary>
    public const string Placeholder = "—";

    /// <summary>
    /// Unix 毫秒的合理下界。比这个小的一律当成「不是毫秒」。
    ///
    /// 2001-09-09T01:46:40Z。为什么定在这：上游给的过期时间都在 2025 年以后，
    /// 而误传进来的「秒」级时间戳（如 1793731164）换算成毫秒会落在 1970 年附近。
    /// 卡在这个界线上，能把「秒当成毫秒」这种最常见的传错挡掉 —— 不然界面上
    /// 会显示 1970 年，用户以为程序坏了。
    /// </summary>
    public const long MinPlausibleUnixMs = 1_000_000_000_000L;

    /// <summary>
    /// Unix 毫秒的合理上界：9999-12-31。再大 DateTimeOffset 会抛。
    /// </summary>
    public const long MaxPlausibleUnixMs = 253_402_300_799_999L;

    /// <summary>
    /// 把「可能是 Unix 毫秒、可能是 ISO 字符串、可能根本不是」的值
    /// 归一成一个 UTC 时刻。
    ///
    /// @returns 解析成功返回时刻；失败返回 null（调用方自己决定显示什么）。
    /// </summary>
    public static DateTimeOffset? TryParse(object? value)
    {
        switch (value)
        {
            case null:
                return null;

            // 已经就是时刻了，直接用
            case DateTimeOffset dto:
                return dto;

            case DateTime dt:
                // 未标明 Kind 的 DateTime 是本地时间 —— 保险库里的都是 UTC，
                // 但真的传进来一个 Unspecified 的，按本地解释比按 UTC 猜安全。
                return dt.Kind == DateTimeKind.Unspecified
                    ? new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt))
                    : new DateTimeOffset(dt);

            // 数字：Unix 毫秒。也可能是 long / int / decimal（JSON 反序列化的差异）。
            case long ms:
                return FromUnixMs(ms);
            case int ms:
                return FromUnixMs(ms);
            case double d:
                // 带小数的时间戳：截断而不是四舍五入（毫秒以下没有意义）。
                return double.IsNaN(d) || double.IsInfinity(d) ? null : FromUnixMs((long)d);
            case decimal m:
                return FromUnixMs((long)m);

            case string s:
                return FromText(s);

            default:
                // 其它类型（bool、数组……）：不猜，直接当解析失败。
                return null;
        }
    }

    /// <summary>
    /// 数字 → 时刻。0 和负数一律当「没给」（返回 null）。
    ///
    /// 为什么 0 不算「1970-01-01」：Core 里 <c>expiresAtMs &lt;= 0</c> 的约定
    /// 就是「上游没给过期时间」。把它显示成 1970 年会让用户以为号坏了。
    /// </summary>
    public static DateTimeOffset? FromUnixMs(long? unixMs)
    {
        if (unixMs is not { } ms) return null;
        if (ms <= 0) return null;
        if (ms < MinPlausibleUnixMs || ms > MaxPlausibleUnixMs) return null;

        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    /// <summary>
    /// 字符串 → 时刻。ISO 8601 是主路径，另外容忍几种常见的本地写法。
    /// </summary>
    public static DateTimeOffset? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();

        // 超长字符串不是时间，是「传错了字段」。先挡掉，
        // 免得把一个 4KB 的 token 拿去喂 DateTime.Parse 慢慢磨。
        if (trimmed.Length > 64) return null;

        if (DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return parsed;
        }

        // 有些落盘实现会写纯数字字符串（"1793731164678"）。
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
        {
            return FromUnixMs(ms);
        }

        return null;
    }

    /// <summary>
    /// 时间戳 → 本地时间，精确到分钟。
    ///
    /// 精确到分钟是刻意的：秒级精度在一个「账号寿命」的界面里没有信息量，
    /// 反而让一列时间戳看起来吵。
    /// </summary>
    public static string FormatTimestamp(object? value)
    {
        var t = TryParse(value);
        return t is { } parsed
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : Placeholder;
    }

    /// <summary>时间戳 → 「2026-09-24」这种只到日期的形式。</summary>
    public static string FormatDate(object? value)
    {
        var t = TryParse(value);
        return t is { } parsed
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : Placeholder;
    }

    /// <summary>时间戳 → 本地时钟时刻，精确到秒。用于「最近请求」那一列。</summary>
    public static string FormatClock(object? value)
    {
        var t = TryParse(value);
        return t is { } parsed
            ? parsed.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : Placeholder;
    }

    /// <summary>
    /// 相对现在还有多久，用人的说法。
    ///
    /// 粒度跟着量级走（日子级只说到天，小时级只说到小时）——
    /// 「还剩 3 天 4 小时 12 分」这种精确到分的倒计时在一个 token 上
    /// 没有信息量，只是吵。
    /// </summary>
    public static string FormatRemaining(object? value, DateTimeOffset now)
    {
        var t = TryParse(value);
        if (t is not { } target) return Placeholder;

        var delta = target - now;

        if (delta <= TimeSpan.Zero)
        {
            var ago = -delta;
            return ago < TimeSpan.FromMinutes(1)
                ? "刚刚过期"
                : $"已过期 {FormatSpan(ago)}";
        }

        return $"还剩 {FormatSpan(delta)}";
    }

    /// <summary>
    /// 冷却还剩多久。冷却态的用量条要显示它。
    ///
    /// 和 <see cref="FormatRemaining"/> 分开，是因为冷却的措辞不一样：
    /// 冷却说的是「等多久能再用」，不是「还剩多少寿命」。
    /// </summary>
    public static string FormatCooldown(object? until, DateTimeOffset now)
    {
        var t = TryParse(until);
        if (t is not { } deadline) return Placeholder;

        var delta = deadline - now;
        if (delta <= TimeSpan.Zero) return "可以用了";

        // 冷却通常只有几分钟，所以秒级precision在这里是有信息量的
        // （看着它从 4:59 走到 4:58 是用户唯一的进度反馈）。
        if (delta < TimeSpan.FromMinutes(1))
        {
            return $"{(int)delta.TotalSeconds} 秒后恢复";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            var minutes = (int)Math.Ceiling(delta.TotalMinutes);
            return $"{minutes} 分钟后恢复";
        }

        return $"{FormatSpan(delta)}后恢复";
    }

    /// <summary>
    /// 把一个时间跨度说成人话。
    /// </summary>
    public static string FormatSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = -span;

        if (span < TimeSpan.FromMinutes(1)) return "不到 1 分钟";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} 分钟";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} 小时";
        if (span < TimeSpan.FromDays(30)) return $"{(int)span.TotalDays} 天";
        if (span < TimeSpan.FromDays(365)) return $"{(int)(span.TotalDays / 30)} 个月";

        return $"{(int)(span.TotalDays / 365)} 年";
    }
}
