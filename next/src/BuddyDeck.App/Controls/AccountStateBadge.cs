using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BuddyDeck.App.Converters;
using BuddyDeck.App.Services;

// 注意：这个文件刻意不用 Wpf.Ui 的控件 —— 徽章的语义色由 StatusAppearance 决定，
// 不用 ui:Badge 的视觉强调级别枚举（理由见类注释）。
//
// ⚠ 但全局 using 里有 Wpf.Ui.Controls，所以本文件里所有
//   TextBlock / Border 之类的类型名都写全名或用 System.Windows.Controls 的别名，
//   否则会和 Wpf.Ui.Controls 里的同名类型撞成 CS0104。
using TextBlock = System.Windows.Controls.TextBlock;

namespace BuddyDeck.App.Controls;

/// <summary>
/// 账号状态徽章。
///
/// ## 它是什么形状
///
/// 一个**词 + 一层薄底**，不是实心色块。底色是状态色压到 0.16 ——
/// 实心色块会让一行里每个徽章都在喊，反而没有重点。
///
/// ## 为什么颜色必须由 <see cref="StatusAppearance"/> 决定
///
/// 徽章的颜色是这个界面里最容易被随手写死的东西（"健康就绿色嘛"）。
/// 写死的代价是：三个月后有人改配色，徽章还是老绿。所以这里一律走映射表。
///
/// ## 不做的事
///
/// · **不加图标**。状态已经是文字了，再加一个图标是重复编码。
/// · **不做悬停提示**。徽章是个名词（「可用」），不是个动作。
/// · **不用 WPF-UI 的 <c>ui:Badge</c>**。它的 Appearance 枚举
///   （Primary/Secondary/Info/Dark/Light/Danger/Success/Caution/Transparent）
///   按「视觉强调级别」分类，而 BuddyDeck 需要按**账号语义**分类 ——
///   5 个账号状态要落到 5 个不同颜色上，ui:Badge 的枚举里没有
///   「即将过期」和「异常」这两个语义位，硬塞会变成
///   Caution 和 Danger 的重复映射，丢掉 Expired/Degraded 的区分。
///   （另外 HANDOVER 坑 3 记着：是 Caution 不是 Warning。）
///
/// ## 为什么基类是 ContentControl 不是 Control
///
/// 这个控件自己在 <see cref="ApplyVisuals"/> 里构造视觉树（Border + TextBlock），
/// 需要一个能装内容的基类 —— 那就是 <see cref="ContentControl"/>。
/// 用 Control 会得到一个能设 Content 但不知道怎么渲染它的壳。
/// </summary>
public sealed class AccountStateBadge : ContentControl
{
    static AccountStateBadge()
    {
        // 这个控件不用 ControlTemplate —— 结构就是「Border 里一个 TextBlock」，
        // 为它维护一个 Generic.xaml 模板文件不划算。视觉在 ApplyVisuals() 里
        // 按状态直接构造（见该方法的注释）。
        //
        // 这里只是把默认样式的查找指向自己，免得 WPF 去找一个不存在的模板
        // 而把控件渲染成空白。
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(AccountStateBadge),
            new FrameworkPropertyMetadata(typeof(AccountStateBadge)));
    }

    /// <summary>账号状态。改它会让颜色和文字一起变。</summary>
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(
            nameof(State),
            typeof(AccountState),
            typeof(AccountStateBadge),
            new FrameworkPropertyMetadata(
                AccountState.Unknown,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnStateChanged));

    /// <summary>覆盖显示的文字。留空 = 用状态的标准说法（<see cref="AccountPresentation.StateLabel"/>）。</summary>
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(AccountStateBadge),
            new FrameworkPropertyMetadata(string.Empty, OnStateChanged));

    public AccountState State
    {
        get => (AccountState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>最终显示的文字。空的时候走状态的标准说法。</summary>
    public string DisplayText
        => string.IsNullOrWhiteSpace(Label) ? StatusAppearance.Label(State) : Label;

    public AccountStateBadge()
    {
        // 徽章不参与 Tab 导航 —— 它不是可交互的。键盘用户跳过它，
        // 焦点直接落在行/按钮上。
        IsTabStop = false;
        Focusable = false;
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AccountStateBadge badge) badge.ApplyVisuals();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        ApplyVisuals();
    }

    /// <summary>
    /// 把状态落到颜色和文字上。
    ///
    /// 直接操作 Content 而不是写 ControlTemplate：这个控件的结构就是
    /// 「一个 Border 里一个 TextBlock」，为它维护一个模板文件不划算，
    /// 而且模板里的绑定反而更难读。
    /// </summary>
    private void ApplyVisuals()
    {
        // 设计器里 Application.Current 是 null，下面的查表全会落回硬编码色。
        // 直接早退，免得设计器预览显示一个和运行时不一样的颜色。
        if (DesignerProperties.GetIsInDesignMode(this)) return;

        var app = Application.Current;
        if (app is null) return;

        var fg = app.TryFindResource(StatusAppearance.BrushKey(State)) as Brush;
        var bg = app.TryFindResource(StatusAppearance.BadgeBackgroundKey(State)) as Brush;
        var border = app.TryFindResource("HairlineBrush") as Brush;
        var fontUi = app.TryFindResource("FontUi") as FontFamily;
        var badgeFontSize = app.TryFindResource("FontSizeBadge") as double? ?? 11.5;

        // ⚠ TextBlock 必须写全名 System.Windows.Controls.TextBlock：
        // 全局 using 里有 Wpf.Ui.Controls，那里也有一个 TextBlock，
        // 不限定就是 CS0104 二义性。
        var text = new System.Windows.Controls.TextBlock
        {
            Text = DisplayText,
            FontSize = badgeFontSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        if (fontUi is not null) text.FontFamily = fontUi;
        if (fg is not null) text.Foreground = fg;

        var box = new Border
        {
            Child = text,
            // 徽章比 RadiusRow(4) 再小一档：它太小了，4px 圆角会显得圆滚滚。
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            BorderThickness = new Thickness(1),
            Background = bg,
        };

        if (border is not null) box.BorderBrush = border;

        Content = box;
        ToolTip = null;   // 徽章不需要悬停提示
    }

    public override string ToString() => DisplayText;
}
