using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BuddyDeck.App.Converters;

namespace BuddyDeck.App.Controls;

/// <summary>
/// 配额可视化 —— 一条表示「还剩多少能打」的横条。
///
/// ## 抄的是什么（抄设计不抄代码）
///
/// 参考 <c>_ref/jlcodes99_cockpit-tools</c> 的配额做法，拿过来的是三个决定：
///
/// 1. **画余量，不画用量。** 用户关心的是「还能用多少」，不是「已经用掉多少」。
///    条从左往右填的是**余量**。参考实现的 <c>remainPercent</c> 就是这个位置。
/// 2. **四档语义色，阈值 10/30/60。** 那三条线被真实账号数据磨过。
/// 3. **状态和百分比分开判。** 冷却是一个独立维度，不是「余量很低」的一种。
///
/// ## 三态（任务要求的）怎么落在四档上
///
/// 任务说「正常 / 冷却中 / 快用尽」三态。实现是四档 + 冷却：
///
///   正常    → Normal(>60%) + Low(30–60%)   ← 都是「能用，不用管」
///   快用尽  → Warning(10–30%) + Critical(≤10%) ← 都是「要留意了」
///   冷却中  → Cooldown                     ← 「现在不能用」
///
/// 为什么中间要再切一刀：三档会把「70% 剩余」和「95% 剩余」并成一类，
/// 但前者已经在消耗的中段了。多这一档不增加颜色数（Low 和 Normal 共用绿），
/// 只是让「要不要现在续期」这个判断有依据。
///
/// ## 冷却态必须显示剩余时间
///
/// 只画一条灰紫的条等于说「这里有问题」，但不说「还要等多久」——
/// 用户唯一能做的动作（等 / 换号）取决于这个时间。所以冷却态**一定**
/// 在条尾显示倒计时文字。
///
/// 这是自绘控件（重写 OnRender），不走 Style/Template —— 因为它画的是
/// 数据几何，不是控件外观。
/// </summary>
public sealed class QuotaBar : FrameworkElement
{
    // ---- 依赖属性 ----

    /// <summary>余量比例 0–1。1 = 满血。</summary>
    public static readonly DependencyProperty RemainingRatioProperty =
        DependencyProperty.Register(
            nameof(RemainingRatio),
            typeof(double),
            typeof(QuotaBar),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>冷却截止时刻（Unix 毫秒 / ISO 字符串 / DateTimeOffset 都吃）。</summary>
    public static readonly DependencyProperty CooldownUntilProperty =
        DependencyProperty.Register(
            nameof(CooldownUntil),
            typeof(object),
            typeof(QuotaBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// 显式指定状态。null 时由 <see cref="QuotaMath.DeriveState"/> 从
    /// 余量 + 冷却推导 —— 这是默认路径，只有特殊场景才手动钉死。
    /// </summary>
    public static readonly DependencyProperty StateOverrideProperty =
        DependencyProperty.Register(
            nameof(StateOverride),
            typeof(QuotaState?),
            typeof(QuotaBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>条左边的标签（模型名 / 套餐名）。</summary>
    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(
            nameof(Caption),
            typeof(string),
            typeof(QuotaBar),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>条右边的数值说明。空的时候自动按「余量 %」出。</summary>
    public static readonly DependencyProperty ValueTextProperty =
        DependencyProperty.Register(
            nameof(ValueText),
            typeof(string),
            typeof(QuotaBar),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>「现在」。可以注入以便测试/动画对齐；默认取系统时钟。</summary>
    public static readonly DependencyProperty NowProperty =
        DependencyProperty.Register(
            nameof(Now),
            typeof(DateTimeOffset?),
            typeof(QuotaBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double RemainingRatio
    {
        get => (double)GetValue(RemainingRatioProperty);
        set => SetValue(RemainingRatioProperty, value);
    }

    public object? CooldownUntil
    {
        get => GetValue(CooldownUntilProperty);
        set => SetValue(CooldownUntilProperty, value);
    }

    public QuotaState? StateOverride
    {
        get => (QuotaState?)GetValue(StateOverrideProperty);
        set => SetValue(StateOverrideProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public DateTimeOffset? Now
    {
        get => (DateTimeOffset?)GetValue(NowProperty);
        set => SetValue(NowProperty, value);
    }

    /// <summary>
    /// 当前该显示的状态。**公开**，因为界面别处（比如提示文字）也要用它，
    /// 而且测试要能直接问控件「你现在是什么状态」。
    /// </summary>
    public QuotaState CurrentState
    {
        get
        {
            if (StateOverride is { } pinned) return pinned;
            var now = Now ?? DateTimeOffset.UtcNow;
            return QuotaMath.DeriveState(RemainingRatio, CooldownUntil, now);
        }
    }

    /// <summary>条尾那串文字。冷却时是倒计时，其余时候是余量百分比。</summary>
    public string CurrentValueText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ValueText)) return ValueText;

            var state = CurrentState;
            if (state == QuotaState.Cooldown)
            {
                var now = Now ?? DateTimeOffset.UtcNow;
                return TimeText.FormatCooldown(CooldownUntil, now);
            }

            // 无限的号（上游 total = -1）不该显示 "100%" —— 那是两回事。
            if (RemainingRatio > 1.0) return "不限";
            return QuotaMath.FormatRatio(RemainingRatio);
        }
    }

    // ---- 自绘 ----

    protected override Size MeasureOverride(Size availableSize)
    {
        // 高度固定 22：一条 6px 的条 + 上下各 8 的呼吸。
        // 不参与父容器的弹性拉伸 —— 条被拉高就变成色块了，不再像刻度。
        var width = double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width;
        return new Size(width, 22);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (dc is null) return;

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var state = CurrentState;
        var track = ResolveBrush("BarTrackBrush", Color.FromRgb(0x21, 0x29, 0x32));
        var fill = ResolveBrush(StatusAppearance.QuotaBrushKey(state), Color.FromRgb(0x4F, 0x9E, 0x6E));

        // 条高 6，垂直居中在控件里。
        const double barH = 6;
        var barTop = (h - barH) / 2;

        var trackRect = new Rect(0, barTop, w, barH);

        // 圆角 2（RadiusBar）：细长元素用大圆角会变药丸，像按钮。
        dc.DrawRoundedRectangle(track, null, trackRect, 2, 2);

        // 冷却态：不按余量画，画一条「停住了」的提示条。
        //
        // 为什么：冷却时余量是**无关信息** —— 一个 90% 余量的号可能正在冷却，
        // 画满一条绿会让用户以为能点。冷却态画一条窄的实心段，
        // 表示「这个位置被占住了，但不是因为没量」。
        if (state == QuotaState.Cooldown)
        {
            // 左侧固定 8px 的实心块 —— 像仪表上被压下去的开关。
            var blocked = new Rect(0, barTop, Math.Min(8, w), barH);
            dc.DrawRoundedRectangle(fill, null, blocked, 2, 2);
            return;
        }

        var fillW = QuotaMath.BarWidth(RemainingRatio, w);

        // 有量但太窄画不出来时，至少给 2px —— 0% 和「几乎没了」要能区分。
        if (fillW > 0 && fillW < 2) fillW = 2;

        if (fillW <= 0) return;

        var fillRect = new Rect(0, barTop, fillW, barH);
        dc.DrawRoundedRectangle(fill, null, fillRect, 2, 2);
    }

    /// <summary>
    /// 从应用资源里查 Brush。设计态（Visual Studio 设计器 / 单元测试）下
    /// <see cref="Application.Current"/> 可能是 null，所以要能回退到硬编码色 ——
    /// 回退色和 Colors.xaml 里的一致，是最后一道保障，不是第二套配色。
    /// </summary>
    private static Brush ResolveBrush(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    /// <summary>给界面/测试看的字符串形式，方便断言。</summary>
    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{Caption}: {CurrentValueText} ({CurrentState})");
}
