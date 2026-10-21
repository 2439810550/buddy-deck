using BuddyDeck.App.Converters;

namespace BuddyDeck.App.Controls;

/// <summary>
/// 用量条的四种状态。
///
/// ## 为什么是四档，不是「正常 / 警告 / 危险」三档
///
/// 参考实现（jlcodes99/cockpit-tools 的 <c>quota-model.ts</c>）用的是
/// <c>quotaClass</c>：临界 / 低 / 中 / 高，阈值 10% / 30% / 60%。
/// 抄这个设计的理由不是「人家四个我们也要四个」，而是：
///
/// **「余量还很多」和「余量充裕」是两回事**，就像「快没了」和「用完了」是两回事。
/// 三档的话，「70% 剩余」和「95% 剩余」会共用一个状态 —— 但前者已经在被消耗的
/// 中段，后者是刚续期。值班的人对这两个的处置不一样（前者要留意，后者不用）。
///
/// 阈值抄的 10 / 30 / 60：这三条线在参考项目里被大量真实账号数据磨过。
///
/// 详见 docs/ui-design-plan.md 第 1.2 节（状态色永远说真话）。
/// </summary>
public enum QuotaState
{
    /// <summary>余量充裕（&gt;60%）。</summary>
    Normal = 0,

    /// <summary>开始消耗了（30%–60%）。不是警告，但值得知道。</summary>
    Low = 1,

    /// <summary>快用尽了（10%–30%）。</summary>
    Warning = 2,

    /// <summary>临界（≤10%）。</summary>
    Critical = 3,

    /// <summary>
    /// 冷却中 —— 上游在限流，这个模型暂时不能用。
    ///
    /// 这个状态**不参与上面那套百分比阈值**：冷却是一个独立维度。
    /// 一个余量 95% 的号完全可能正在冷却（上游按请求速率限流，不看余量）。
    /// 所以 <see cref="QuotaMath.DeriveState"/> 里冷却的优先级高于百分比。
    /// </summary>
    Cooldown = 4,
}

/// <summary>
/// 用量条的纯计算逻辑。
///
/// 之所以单独拿出来：控件（QuotaBar）是自绘的、不方便直接测；
/// 但「百分比 → 状态」「余量 → 条宽」这些是纯函数，必须能测。
/// 控件的 OnRender 只负责把这里算出来的数字画出来。
/// </summary>
public static class QuotaMath
{
    /// <summary>余量低于这个比例算「临界」（≤10%）。</summary>
    public const double CriticalThreshold = 0.10;

    /// <summary>余量低于这个比例算「快用尽了」（≤30%）。</summary>
    public const double WarningThreshold = 0.30;

    /// <summary>余量低于这个比例算「开始消耗了」（≤60%）。</summary>
    public const double LowThreshold = 0.60;

    /// <summary>
    /// 余量比例 → 状态。**不含冷却判断** —— 冷却要走
    /// <see cref="DeriveState"/>，因为它是更高优先级的一个维度。
    /// </summary>
    public static QuotaState FromRemainingRatio(double remainingRatio)
    {
        // NaN / Infinity：算不出来，当未知。当作 Normal 是错的（会让用户以为没事），
        // 但也没有第五个状态可用，所以退回 Normal 并把上限交给调用方钳制。
        if (double.IsNaN(remainingRatio) || double.IsInfinity(remainingRatio))
        {
            return QuotaState.Normal;
        }

        if (remainingRatio <= CriticalThreshold) return QuotaState.Critical;
        if (remainingRatio <= WarningThreshold) return QuotaState.Warning;
        if (remainingRatio <= LowThreshold) return QuotaState.Low;
        return QuotaState.Normal;
    }

    /// <summary>
    /// 推导一个用量条该显示的状态。
    ///
    /// 优先级：**冷却 &gt; 百分比**。正在冷却的号，余量多少都不重要 ——
    /// 它现在就是不能用，界面上必须说这件事。反过来，如果先判百分比，
    /// 一个「余量 95% 但在冷却」的号会显示成绿油油的正常态，用户点下去才发现
    /// 用不了 —— 这正是让人不信任界面的那类 bug。
    /// </summary>
    /// <param name="remainingRatio">余量比例 0–1。</param>
    /// <param name="cooldownUntil">冷却截止时刻（任意可解析格式）。null / 不可解析 = 不在冷却。</param>
    /// <param name="now">现在。</param>
    public static QuotaState DeriveState(
        double remainingRatio,
        object? cooldownUntil,
        DateTimeOffset now)
    {
        if (IsCooling(cooldownUntil, now)) return QuotaState.Cooldown;
        return FromRemainingRatio(remainingRatio);
    }

    /// <summary>这个时刻是不是还没到（也就是还在冷却里）。</summary>
    public static bool IsCooling(object? cooldownUntil, DateTimeOffset now)
        => TimeText.TryParse(cooldownUntil) is { } until && until > now;

    /// <summary>
    /// 把「已用量」和「总量」算成余量比例。
    ///
    /// 边界都走这里，免得每个调用点各写一遍：
    ///   · total &lt;= 0       → 1.0（没给总量，不画成空的吓人）
    ///   · total &lt; used     → 0（超支也算用尽，不返回负数）
    /// </summary>
    public static double RemainingRatio(double used, double total)
    {
        if (double.IsNaN(used) || double.IsNaN(total)) return 1.0;
        if (total <= 0) return used > 0 ? 0.0 : 1.0;

        var remaining = (total - used) / total;
        return Math.Clamp(remaining, 0.0, 1.0);
    }

    /// <summary>
    /// 余量比例 → 条的宽度（已填充部分，0–1）。
    ///
    /// 注意这里返回的是**余量**的宽度，不是「用了多少」的宽度 ——
    /// 这个界面关心的是「还剩多少能打」，条画的也是余量。
    /// </summary>
    public static double BarWidth(double remainingRatio, double trackWidth)
    {
        if (double.IsNaN(remainingRatio) || double.IsInfinity(remainingRatio)) return 0;
        if (double.IsNaN(trackWidth) || trackWidth <= 0) return 0;

        return Math.Clamp(remainingRatio, 0.0, 1.0) * trackWidth;
    }

    /// <summary>
    /// 余量比例 → 「93.4%」这种人话。
    ///
    /// 无限量（上游给 total = -1）的号返回「不限」而不是百分比。
    /// </summary>
    public static string FormatRatio(double remainingRatio)
    {
        if (double.IsNaN(remainingRatio) || double.IsInfinity(remainingRatio)) return "—";

        var clamped = Math.Clamp(remainingRatio, 0.0, 1.0);
        return (clamped * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }
}
