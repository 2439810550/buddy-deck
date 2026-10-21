using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BuddyDeck.App.Converters;

namespace BuddyDeck.App.Controls;

/// <summary>
/// 网关在线状态点 —— 顶栏「网关已连接」里那个小圆点。
///
/// ## 为什么不用绿/红
///
/// 「网关没启动」**不是错误**。用户关掉面板、或者刚装好还没启动，
/// 都是正常状态。给它红色会让每次打开面板都像出了事故。
/// 所以离线用中性灰（`IdleBrush`）——「这里什么都没有」，不是「这里坏了」。
///
/// 真的坏了（端口被占、进程崩了）会在**内容区**用 `bd:ErrorState` 说，
/// 而不是把顶栏那个点变红。**顶栏负责「在不在」，内容区负责「为什么不在」。**
///
/// ## 为什么是自绘
///
/// 一个直径 8px 的圆。用 `Ellipse` + `DataTrigger` 也做得到，但
/// 「在线 + 有陈旧标记」这种组合态（代码指纹和磁盘不一致）需要
/// 双色环，`Ellipse` 拼起来很啰嗦。
/// </summary>
public sealed class StatusDot : FrameworkElement
{
    /// <summary>在线？</summary>
    public static readonly DependencyProperty OnlineProperty =
        DependencyProperty.Register(
            nameof(Online),
            typeof(bool),
            typeof(StatusDot),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// 跑的和磁盘上的代码不一致（<c>/healthz</c> 的 <c>stale</c>）。
    ///
    /// 在线但陈旧时，点在绿底外套一圈琥珀 —— 「连得上，但它跑的不是最新代码」。
    /// 这件事单独用一个点说不清，但环能让用户注意到，详情在内容区讲。
    /// </summary>
    public static readonly DependencyProperty StaleProperty =
        DependencyProperty.Register(
            nameof(Stale),
            typeof(bool),
            typeof(StatusDot),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>直径。默认 8（SizeStatusDot）。</summary>
    public static readonly DependencyProperty DotSizeProperty =
        DependencyProperty.Register(
            nameof(DotSize),
            typeof(double),
            typeof(StatusDot),
            new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool Online
    {
        get => (bool)GetValue(OnlineProperty);
        set => SetValue(OnlineProperty, value);
    }

    public bool Stale
    {
        get => (bool)GetValue(StaleProperty);
        set => SetValue(StaleProperty, value);
    }

    public double DotSize
    {
        get => (double)GetValue(DotSizeProperty);
        set => SetValue(DotSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 陈旧时需要一圈环，所以实际占用比点本身大 4px。
        var extent = DotSize + (Stale ? 4 : 0);
        return new Size(extent, extent);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (dc is null) return;

        var size = DotSize;
        if (size <= 0) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        // 陈旧环先画（在点底下）。1.5px 的琥珀环。
        if (Stale)
        {
            var ring = ResolveBrush(StatusAppearance.WarnKey, Color.FromRgb(0xD2, 0xA2, 0x4C));
            dc.DrawEllipse(null, new Pen(ring, 1.5), center, (size / 2) + 2.5, (size / 2) + 2.5);
        }

        var key = StatusAppearance.GatewayDotKey(Online);
        var fill = ResolveBrush(key, Online ? Color.FromRgb(0x4F, 0x9E, 0x6E) : Color.FromRgb(0x5A, 0x66, 0x72));

        dc.DrawEllipse(fill, null, center, size / 2, size / 2);
    }

    /// <summary>设计态 / 测试下 Application.Current 可能为 null，回退到硬编码同色。</summary>
    private static Brush ResolveBrush(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}

/// <summary>
/// 左侧状态轨 —— 把每个账号的余量画成一根竖条，余光可扫。
///
/// ## 它是什么，不是什么
///
/// **是**：一个「还有多少能打」的余光指示器。列表要读文字才知道哪个号要没了；
/// 竖条扫一眼就知道。它也是这个界面的签名元素（设计计划第 1.7 节）。
///
/// **不是**：一个图表。没有坐标轴、没有网格线、没有图例。每条就是一格，
/// 颜色是状态，高度是余量（等宽），顺序和右边的列表一致。
///
/// ## 为什么条是等宽的
///
/// 如果用「面积」表示余量（条的宽度也变），那 9 个号会排成一把扇子，
/// 视觉上很难比较。等宽 + 高度变化 = 一眼看出哪个矮。
///
/// ## 它只出现在总览和账号两页
///
/// 不是全局常驻。日志页旁边的余量条是装饰 —— 它不服务「刚才发生了什么」
/// 那个任务（设计计划第 1.8 节原则 2）。
///
/// ## ⚠ 这个控件曾经「永远什么都不画」（已修，留档防复发）
///
/// 缺陷：类里**漏了 `MeasureOverride`**，于是 WPF 把期望尺寸算成 0×0，
/// `OnRender` 开头的 `if (width &lt;= 0 || height &lt;= 0) return;` 每次都退出。
///
/// **它不报错、不抛异常、日志里什么都没有** —— 只是静静地不显示。
/// 实测表现：起假网关喂 6 个账号（数据层确认真有 6 条），界面上一条竖条都没有。
///
/// **为什么难查**：「什么都不显示」看起来太像布局问题，
/// 排查的人会先怀疑容器、再怀疑宽度、再怀疑截图时序 —— 全错方向。
/// 最后是靠**对照实验**（换一个确定能工作的绑定看是否正常）才掰回来。
///
/// **所以现在有测试盯着它**：`StatusRailLayoutTests` 会把控件真的放进
/// 布局里量一次，断言测量结果 &gt; 0 —— 见 tests/BuddyDeck.App.Presentation.Tests/。
/// </summary>
public sealed class StatusRail : FrameworkElement
{
    /// <summary>
    /// 每根条的数据。界面传一个 `IReadOnlyList<StatusRailItem>` 进来。
    ///
    /// 这里刻意用一个小 record 而不是直接吃 `AccountRowViewModel` ——
    /// 那样这个控件会依赖 ViewModels 命名空间，而 ui 拥有那个目录。
    /// 控件吃一个它自己的、极简的输入类型，双方都不用迁就对方。
    /// </summary>
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(
            nameof(Items),
            typeof(IReadOnlyList<StatusRailItem>),
            typeof(StatusRail),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>条的宽度。默认跟 SizeStatusRail 的留白走。</summary>
    public static readonly DependencyProperty BarWidthProperty =
        DependencyProperty.Register(
            nameof(BarWidth),
            typeof(double),
            typeof(StatusRail),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>「现在」。可注入，方便对齐刷新。</summary>
    public static readonly DependencyProperty NowProperty =
        DependencyProperty.Register(
            nameof(Now),
            typeof(DateTimeOffset?),
            typeof(StatusRail),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<StatusRailItem>? Items
    {
        get => (IReadOnlyList<StatusRailItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public double BarWidth
    {
        get => (double)GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    public DateTimeOffset? Now
    {
        get => (DateTimeOffset?)GetValue(NowProperty);
        set => SetValue(NowProperty, value);
    }

    /// <summary>条之间的缝。测量与绘制共用（见 <see cref="ComputeLayout"/>）。</summary>
    private const double Gap = 6;

    /// <summary>单根条的高度上限。一个号不该占特别高的一格，否则 3 个号的池子
    /// 会画出三根巨条，看起来像柱状图。测量与绘制共用。</summary>
    private const double MaxSlot = 28;

    /// <summary>
    /// 自己算要多大。
    ///
    /// ## ⚠ 这个方法是**必需的**，不是优化 —— 缺了它会「永远什么都不画」
    ///
    /// `FrameworkElement` 不给 `MeasureOverride` 时，WPF 把期望尺寸算成 **0×0**。
    /// 于是 <see cref="OnRender"/> 开头的
    /// <c>if (width &lt;= 0 || height &lt;= 0) return;</c> 每次都从这里退出 ——
    /// **一条竖条都画不出来**。
    ///
    /// 而且它**编译期不报、运行期不报、异常也不抛**，只是静静地什么都不显示。
    /// 这个缺陷实测发生过（ui 起的假网关喂了 6 个账号，数据层确认真有 6 条，
    /// 但界面上一条竖条都没有）。
    ///
    /// ## 为什么在控件里 self-size，而不是让使用方给 Height
    ///
    /// 在 Style 里补一个 `Height="220"` 只能掩盖症状：**下一次谁把它换个容器
    /// （比如从 DockPanel 挪进 Grid 的 Auto 行）还会塌成 0**。
    /// 控件应该自己决定「我要多大」，而不是依赖使用方每次记得给高度。
    ///
    /// ## 尺寸怎么算
    ///
    /// 宽度：<see cref="BarWidth"/>，但不超出可用宽度。
    /// 高度：条数 × (槽高 + 缝)，其中槽高受 <see cref="MaxSlot"/> 约束。
    ///
    /// ⚠ 这里的布局算式必须和 <see cref="OnRender"/> **完全一致** ——
    /// 两边各算一套的话，测得的高度和画出来的内容会对不上
    /// （经典表现是「底部被裁掉一截」）。所以两边都走
    /// <see cref="ComputeLayout"/>。
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var n = Items?.Count ?? 0;
        if (n == 0) return new Size(0, 0);

        // 期望尺寸：按条数撑开。
        var desiredWidth = Math.Max(0, BarWidth);
        var desiredHeight = DesiredHeight(n);

        // 被可用空间约束（Infinity 表示无约束，那就用期望值）。
        var width = double.IsInfinity(availableSize.Width)
            ? desiredWidth
            : Math.Min(desiredWidth, availableSize.Width);

        var height = double.IsInfinity(availableSize.Height)
            ? desiredHeight
            : Math.Min(desiredHeight, availableSize.Height);

        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    /// <summary>
    /// n 根条自然需要多高。
    ///
    /// 提取出来给 <see cref="MeasureOverride"/> 用，也让「期望高度」有一个
    /// 可单独测试的入口。
    /// </summary>
    internal double DesiredHeight(int itemCount)
        => itemCount <= 0
            ? 0
            : (itemCount * MaxSlot) + ((itemCount - 1) * Gap);

    /// <summary>
    /// 布局算式 —— **测量与绘制共用的唯一实现**。
    ///
    /// @param itemCount 条数
    /// @param width     实际可用宽
    /// @param height    实际可用高
    /// @returns (槽高, 条宽)；槽高 &lt;= 0 表示画不下，调用方直接返回
    /// </summary>
    internal static (double Slot, double BarWidth) ComputeLayout(
        int itemCount, double width, double height, double barWidth)
    {
        if (itemCount <= 0 || width <= 0 || height <= 0) return (0, 0);

        // 缝要先扣掉：缝不够时整个轨会糊成一条渐变，
        // 那就不是「每一格一个号」了，是一条没有信息的彩带。
        var available = height - (Gap * (itemCount - 1));
        if (available <= 0) return (0, 0);

        var slot = Math.Min(available / itemCount, MaxSlot);
        var barW = Math.Min(barWidth, width);

        return (slot, barW);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (dc is null) return;

        var items = Items;
        if (items is null || items.Count == 0) return;

        var width = ActualWidth;
        var height = ActualHeight;

        var (slot, barW) = ComputeLayout(items.Count, width, height, BarWidth);
        if (slot <= 0 || barW <= 0) return;

        var y = 0.0;
        var track = ResolveBrush("BarTrackBrush", Color.FromRgb(0x21, 0x29, 0x32));

        // 条的圆角 2（RadiusBar）：细长元素大圆角会变药丸。
        const double radius = 2;

        foreach (var item in items)
        {
            // 槽（背景）。画槽是必要的 —— 没有槽就看不出「空了一半」还是
            // 「这根就是这么长」。
            dc.DrawRoundedRectangle(
                track,
                null,
                new Rect(0, y, barW, slot),
                radius,
                radius);

            var state = item.State;
            var fill = ResolveBrush(StatusAppearance.QuotaBrushKey(state), Color.FromRgb(0x4F, 0x9E, 0x6E));

            if (state == QuotaState.Cooldown)
            {
                // 冷却：底部一小段实心块 —— 「这格被占住了，但不是因为没量」。
                var h = Math.Min(slot, 8);
                dc.DrawRoundedRectangle(
                    fill,
                    null,
                    new Rect(0, y, barW, h),
                    radius,
                    radius);
            }
            else
            {
                var fillH = QuotaMath.BarWidth(item.RemainingRatio, slot);
                if (fillH > 0 && fillH < 3) fillH = 3;   // 极小值也要看得见

                // 从底部往上长 —— 仪表盘的直觉：满的在上面。
                if (fillH > 0)
                {
                    dc.DrawRoundedRectangle(
                        fill,
                        null,
                        new Rect(0, y + slot - fillH, barW, fillH),
                        radius,
                        radius);
                }
            }

            y += slot + Gap;
        }
    }

    private static Brush ResolveBrush(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}

/// <summary>
/// 状态轨的一格 —— 一个账号的余量。
///
/// 刻意做得很小：这个控件只需要知道「多少量」和「什么状态」，
/// 不需要知道账号的 id、标签、域名。那些是列表的事。
/// </summary>
/// <param name="RemainingRatio">余量 0–1。</param>
/// <param name="State">已经算好的状态。调用方用 <see cref="QuotaMath.DeriveState"/> 算。</param>
public readonly record struct StatusRailItem(double RemainingRatio, QuotaState State);
