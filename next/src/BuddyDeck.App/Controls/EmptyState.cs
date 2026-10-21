using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

// ⚠ 全局 using 里有 Wpf.Ui.Controls，它也有 TextBlock / Button。
// 本文件构造的是**平台原生**控件（用 System.Windows 的样式 System），
// 所以显式起别名，避免 CS0104 二义性。
using TextBlock = System.Windows.Controls.TextBlock;
using Button = System.Windows.Controls.Button;

namespace BuddyDeck.App.Controls;

/// <summary>
/// 空态。
///
/// ## 一句话原则：空屏是一个**还没做的动作**，不是一种需要被安慰的处境
///
/// 所以这个控件的结构被三样东西钉死（不是自由的）：
///
///   Headline   一句话说清「这里现在没有 X」
///   Detail     一句话说清「怎么才能有 X」
///   ActionText + ActionCommand   那个动作本身
///
/// **Action 是可选的**，但在 BuddyDeck 里几乎总是该给 —— 一个说不出
/// 下一步动作的空态，等于告诉用户「你自己想办法」。
///
/// ## 不做的事（这些是 AI 生成界面的识别特征，SKILL.md 第 69 行）
///
/// · **没有插画**。一个空列表配一张「搜索无结果」的卡通图，是把
///   「没有内容」这件小事放大成一件需要被化解的情绪。
/// · **没有表情符号**。空态里没有「~」、没有笑脸。
/// · **不说「暂无数据」**。这句话说了等于没说 —— 用户已经看见是空的了。
///   要说的是**为什么**空、**怎么**不空。
/// · **不居中一大段散文**。三行，每行干一件事。
///
/// ## 为什么对齐是居中的（整个界面唯一一处居中）
///
/// 界面里所有东西都是左对齐的（设计计划第 1.7 节）。这里例外：
/// 空态时页面上**只有这一块**，左对齐会让它飘在一边，
/// 居中才是它该在的位置。这是位置逻辑，不是「空态就该居中」的惯例。
///
/// ## 为什么自己构造视觉树（不用 ControlTemplate）
///
/// 这样 ui 在页面里直接写
/// <c>&lt;bd:EmptyState Headline="还没有账号" Detail="导入一份登录文件就能开始。"
/// ActionText="导入登录文件" /&gt;</c> 就能看见东西，
/// 不需要再去 Themes 里写一个模板 —— 少一个两边都要维护的契约。
/// </summary>
public class EmptyState : ContentControl
{
    /// <summary>「这里现在没有什么」。必填。</summary>
    public static readonly DependencyProperty HeadlineProperty =
        DependencyProperty.Register(
            nameof(Headline),
            typeof(string),
            typeof(EmptyState),
            new FrameworkPropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>「怎么让它有」。必填 —— 空态的价值全在这一句上。</summary>
    public static readonly DependencyProperty DetailProperty =
        DependencyProperty.Register(
            nameof(Detail),
            typeof(string),
            typeof(EmptyState),
            new FrameworkPropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>按钮文字。**动词开头，说清点下去会发生什么**（「导入登录文件」，不是「确定」）。</summary>
    public static readonly DependencyProperty ActionTextProperty =
        DependencyProperty.Register(
            nameof(ActionText),
            typeof(string),
            typeof(EmptyState),
            new FrameworkPropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>按钮命令。为空时按钮不显示 —— 不显示一个点了没反应的按钮。</summary>
    public static readonly DependencyProperty ActionCommandProperty =
        DependencyProperty.Register(
            nameof(ActionCommand),
            typeof(ICommand),
            typeof(EmptyState),
            new FrameworkPropertyMetadata(null, OnContentChanged));

    public string Headline
    {
        get => (string)GetValue(HeadlineProperty);
        set => SetValue(HeadlineProperty, value);
    }

    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public EmptyState()
    {
        // 空态是不可交互的容器，焦点应该直接落到里面的按钮上。
        Focusable = false;
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is EmptyState state) state.BuildVisualTree();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        BuildVisualTree();
    }

    /// <summary>
    /// 构造视觉树。做成 <c>private protected</c> 是给 <see cref="ErrorState"/> 复用
    /// 一个辅助方法用的 —— 两者共享排版，只是内容不同。
    /// </summary>
    private protected void BuildVisualTree()
    {
        if (DesignerProperties.GetIsInDesignMode(this)) return;

        var app = Application.Current;
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // --- 标题：一句话说清「没有 X」 ---
        if (!string.IsNullOrWhiteSpace(Headline))
        {
            var headline = new TextBlock
            {
                Text = Headline,
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };

            if (app?.TryFindResource("FontUi") is FontFamily font) headline.FontFamily = font;
            if (app?.TryFindResource("FontWeightEmphasis") is FontWeight weight) headline.FontWeight = weight;
            headline.Foreground =
                app?.TryFindResource("TextPrimaryBrush") as Brush ?? DefaultPrimary;

            stack.Children.Add(headline);
        }

        // --- 说明：一句话说清「怎么让它有」 ---
        if (!string.IsNullOrWhiteSpace(Detail))
        {
            var detail = new TextBlock
            {
                Text = Detail,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                // 说明永远不超过一个易读宽度 —— 空态说明写很宽会变成一段散文。
                MaxWidth = 420,
            };

            if (app?.TryFindResource("FontUi") is FontFamily font) detail.FontFamily = font;
            detail.Foreground = app?.TryFindResource("TextSecondaryBrush") as Brush ?? DefaultSecondary;

            stack.Children.Add(detail);
        }

        // --- 动作：那个还没做的事 ---
        if (!string.IsNullOrWhiteSpace(ActionText) && ActionCommand is not null)
        {
            var button = new Button
            {
                Content = ActionText,
                Command = ActionCommand,
                Margin = new Thickness(0, 16, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(14, 7, 14, 7),
                Cursor = Cursors.Hand,
            };

            // 优先用主题里的主按钮样式；查不到就退到平台默认外观，
            // 至少按钮是可用的，不会因为样式缺失而消失。
            if (app?.TryFindResource("PrimaryButton") is Style style) button.Style = style;

            stack.Children.Add(button);
        }

        Content = stack;
    }

    // 设计器 / 无 Application 时的兜底色。**不是第二套配色**，
    // 和 Colors.xaml 里同值 —— 只是为了在没有资源字典的环境里不显示成透明。
    private protected static Brush DefaultPrimary { get; } =
        new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));

    private protected static Brush DefaultSecondary { get; } =
        new SolidColorBrush(Color.FromRgb(0x8B, 0x98, 0xA5));

    private protected static Brush DefaultAccent { get; } =
        new SolidColorBrush(Color.FromRgb(0xD2, 0xA2, 0x4C));
}

/// <summary>
/// 错误态。
///
/// ## 一句话原则：讲清**发生了什么**和**怎么修**，用界面自己的口吻
///
/// 固定三段结构（三个属性，缺一个都是不合格的错误提示）：
///
///   What    发生了什么 —— 具体到对象和现象（「连不上 127.0.0.1:8787」）
///   Where   在哪里 / 为什么 —— 定位信息（端口、路径、状态码）
///   Fix     怎么修 —— 一句祈使句，或配一个按钮
///
/// ## 硬规则
///
/// · **不道歉**。不说「抱歉」「对不起」。界面没做错什么，
///   道歉只是让提示更长，不提供任何信息。
/// · **不含糊**。禁止「出错了」「操作失败」「未知错误」「请稍后重试」
///   「Something went wrong」。每一条错误都必须能让用户
///   **下一步做点什么**，否则它就不是一个错误提示，是一个噪音。
/// · **不用红色铺满**。只有左边一条 3px 的红 + 标题是红的，正文是常规色。
///   整块红底会让人以为程序崩了。
///
/// 具体文案范例见 docs/ui-design-plan.md 第 3.3 节。
/// </summary>
public sealed class ErrorState : ContentControl
{
    /// <summary>发生了什么。**必填**，且必须具体。</summary>
    public static readonly DependencyProperty WhatProperty =
        DependencyProperty.Register(
            nameof(What),
            typeof(string),
            typeof(ErrorState),
            new FrameworkPropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>在哪 / 为什么。定位信息（端口、路径、状态码）。</summary>
    public static readonly DependencyProperty WhereProperty =
        DependencyProperty.Register(
            nameof(Where),
            typeof(string),
            typeof(ErrorState),
            new FrameworkPropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>怎么修。一句祈使句。</summary>
    public static readonly DependencyProperty FixProperty =
        DependencyProperty.Register(
            nameof(Fix),
            typeof(string),
            typeof(ErrorState),
            new FrameworkPropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>修法的按钮文字。为空时按钮不显示。</summary>
    public static readonly DependencyProperty FixTextProperty =
        DependencyProperty.Register(
            nameof(FixText),
            typeof(string),
            typeof(ErrorState),
            new FrameworkPropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>修法的按钮命令。</summary>
    public static readonly DependencyProperty FixCommandProperty =
        DependencyProperty.Register(
            nameof(FixCommand),
            typeof(ICommand),
            typeof(ErrorState),
            new FrameworkPropertyMetadata(null, OnContentChanged));

    /// <summary>
    /// 严重程度。决定左侧竖条和标题的颜色。
    ///
    /// 只有两档：**要你动手**（Stop，暗红）和**等一等**（Fault，锈橙）。
    /// 这不是「错误等级」，是「用户该干嘛」—— 因为错误提示的唯一用途
    /// 就是让用户知道该干嘛。三档以上会变成没人分得清的等级体系。
    /// </summary>
    public static readonly DependencyProperty NeedsActionProperty =
        DependencyProperty.Register(
            nameof(NeedsAction),
            typeof(bool),
            typeof(ErrorState),
            new FrameworkPropertyMetadata(true, OnContentChanged));

    public string What
    {
        get => (string)GetValue(WhatProperty);
        set => SetValue(WhatProperty, value);
    }

    public string Where
    {
        get => (string)GetValue(WhereProperty);
        set => SetValue(WhereProperty, value);
    }

    public string Fix
    {
        get => (string)GetValue(FixProperty);
        set => SetValue(FixProperty, value);
    }

    public string FixText
    {
        get => (string)GetValue(FixTextProperty);
        set => SetValue(FixTextProperty, value);
    }

    public ICommand? FixCommand
    {
        get => (ICommand?)GetValue(FixCommandProperty);
        set => SetValue(FixCommandProperty, value);
    }

    public bool NeedsAction
    {
        get => (bool)GetValue(NeedsActionProperty);
        set => SetValue(NeedsActionProperty, value);
    }

    public ErrorState()
    {
        Focusable = false;
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ErrorState state) state.BuildVisualTree();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        BuildVisualTree();
    }

    /// <summary>
    /// 构造视觉树。
    ///
    /// 结构的核心是**左侧那条 3px 的竖条**：它是「这是一条提示，不是页面内容」
    /// 的唯一信号。不铺红底 —— 铺满红会让人以为程序崩了。
    /// </summary>
    private void BuildVisualTree()
    {
        if (DesignerProperties.GetIsInDesignMode(this)) return;

        var app = Application.Current;

        // 两档强调色：要你动手 = Stop（暗红），等一等 = Fault（锈橙）。
        // 这一对色在 Colors.xaml 里是两个不同的红，对应两种不同的处置。
        var accentKey = NeedsAction ? "StopBrush" : "FaultBrush";
        var accent = app?.TryFindResource(accentKey) as Brush ?? DefaultAccent;

        var textStack = new StackPanel();

        // --- 发生了什么（红色，因为它定义了这条提示的性质） ---
        var what = new TextBlock
        {
            Text = What,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = accent,
        };

        if (app?.TryFindResource("FontUi") is FontFamily font) what.FontFamily = font;
        if (app?.TryFindResource("FontWeightEmphasis") is FontWeight weight) what.FontWeight = weight;

        textStack.Children.Add(what);

        // --- 在哪里 / 为什么（常规色，等宽 —— 里面是端口/路径/状态码这种标识符） ---
        if (!string.IsNullOrWhiteSpace(Where))
        {
            var where = new TextBlock
            {
                Text = Where,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };

            if (app?.TryFindResource("FontNumeric") is FontFamily mono) where.FontFamily = mono;
            else if (app?.TryFindResource("FontUi") is FontFamily ui) where.FontFamily = ui;

            where.Foreground = app?.TryFindResource("TextSecondaryBrush") as Brush ?? DefaultSecondary;

            textStack.Children.Add(where);
        }

        // --- 怎么修（一句祈使句） ---
        if (!string.IsNullOrWhiteSpace(Fix))
        {
            var fix = new TextBlock
            {
                Text = Fix,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };

            if (app?.TryFindResource("FontUi") is FontFamily font2) fix.FontFamily = font2;
            fix.Foreground = app?.TryFindResource("TextPrimaryBrush") as Brush ?? DefaultPrimary;

            textStack.Children.Add(fix);
        }

        // --- 修法的按钮 ---
        if (!string.IsNullOrWhiteSpace(FixText) && FixCommand is not null)
        {
            var button = new Button
            {
                Content = FixText,
                Command = FixCommand,
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 6, 12, 6),
                Cursor = Cursors.Hand,
            };

            // 用安静按钮：错误的主动作通常是「去看一眼」或「重试」，
            // 不该和页面的主按钮抢注意力。
            if (app?.TryFindResource("QuietButton") is Style style) button.Style = style;

            textStack.Children.Add(button);
        }

        // 左侧 3px 竖条 + 内容。竖条是「这是一条提示」的唯一信号。
        var rail = new Border
        {
            Width = 3,
            Background = accent,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(rail, 0);
        Grid.SetColumn(textStack, 1);
        grid.Children.Add(rail);
        grid.Children.Add(textStack);

        var background = app?.TryFindResource("SurfaceBrush") as Brush;

        Content = new Border
        {
            Child = grid,
            // RadiusRow(4)：提示框是个「行」级别的东西，不是浮层。
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Background = background,
        };
    }

    private static Brush DefaultPrimary { get; } =
        new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));

    private static Brush DefaultSecondary { get; } =
        new SolidColorBrush(Color.FromRgb(0x8B, 0x98, 0xA5));

    private static Brush DefaultAccent { get; } =
        new SolidColorBrush(Color.FromRgb(0xC9, 0x55, 0x3F));
}
