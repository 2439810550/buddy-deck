namespace BuddyDeck.App.Services;

/// <summary>
/// 左侧导航栏的**纯逻辑**：折叠阈值、选中态推导、无障碍标签。
///
/// ## 为什么单独一个文件
///
/// 与 <see cref="AccountPresentation"/> / <see cref="ChatMedia"/> 同一个理由：
/// 这些是**可脱离界面测**的判断，而 ViewModel 依赖 CommunityToolkit 的源生成器、
/// 不能被测试项目链接。放这里才测得到。
///
/// ## 这个文件存在的背景（一次被推翻的设计决定）
///
/// 我原先在 `MainWindow.xaml` 里写过：「六个页面是平级的，没有二级结构。
/// 左侧常驻导航栏是在为一个不存在的层级付租金」。**那个推理本身没错** ——
/// 六页确实平级。错的是**把它当成了"所以不要左导航"的充分理由**。
///
/// 用户实测反馈推翻了它（原话：「侧边栏没内容 无交互」）。回头看，我漏了一个维度：
///
///   平级 → 不需要**层级**          ← 我当时想到的
///   桌面工具 → 需要**常驻导航**    ← 我漏掉的
///
/// 两者不矛盾：左导航同时满足。顶栏页签在窄窗口会被挤，
/// 而且它和"应用名 / 网关状态 / 刷新"抢同一行。
///
/// 原注释没有删掉 —— 删掉等于让下一个人再犯一次同样的推理错误。
/// </summary>
public static class ShellNavigation
{
    /// <summary>
    /// 低于这个宽度就折叠成只有图标的窄栏。
    ///
    /// 为什么是 900：导航栏展开约 200px，内容区至少要 700px 才够放
    /// 账号卡那种宽内容（卡片 360 + 详情面板）。900 之下再展开导航，
    /// 内容区就只剩 600 出头 —— 那时宁可折叠导航，也别挤坏内容。
    ///
    /// 与我们已有的 `MinWidth="960"` 配合：窗口最小 960，所以折叠状态
    /// 只在用户手动拖窄到 900 以下时出现（留了 60px 的余量避免抖动）。
    /// </summary>
    public const double CollapseThreshold = 900;

    /// <summary>展开时的宽度。</summary>
    public const double ExpandedWidth = 200;

    /// <summary>折叠时的宽度（只留图标）。</summary>
    public const double CompactWidth = 52;

    /// <summary>这个窗口宽度下要不要折叠。</summary>
    public static bool ShouldCollapse(double windowWidth) => windowWidth < CollapseThreshold;

    /// <summary>按折叠状态给出导航栏宽度。</summary>
    public static double WidthFor(bool collapsed) => collapsed ? CompactWidth : ExpandedWidth;

    /// <summary>
    /// 导航项的 ToolTip / 无障碍标签。
    ///
    /// 折叠时导航栏只剩图标 —— 那时 **tooltip 是唯一能说明"这个图标是什么"的东西**，
    /// 所以折叠态必须给 tooltip。展开态图标旁边已经有文字，
    /// tooltip 留着反而会在鼠标划过时一直弹。
    ///
    /// 返回 null 表示不设 tooltip（WPF 的 ToolTip=null 就是不显示）。
    /// </summary>
    public static string? TooltipFor(string title, string description, bool collapsed)
        => collapsed ? $"{title} —— {description}" : null;

    /// <summary>
    /// 选中态的**下划线**是否显示。
    ///
    /// 展开态用左侧竖条 + 底色表达选中（竖条在图标左边）；
    /// 折叠态没有足够宽度放竖条，改用图标底色 + 外框。
    /// 所以这个函数决定「要不要画那条竖条」。
    /// </summary>
    public static bool ShowSelectionRail(bool collapsed) => !collapsed;

    /// <summary>
    /// 给当前页在导航序列里的位置。
    ///
    /// 为什么需要它：折叠态下用户只能看到图标，**没有文字说明"你在哪一页"**。
    /// 顶栏需要显示当前页名来补这个信息（展开态也显示，保持一致）。
    /// 找不到返回 null —— 不崩，也只是少一行字。
    /// </summary>
    public static string? CurrentTitle(IEnumerable<string> titles, int index)
    {
        if (index < 0) return null;
        foreach (var t in titles)
        {
            if (index-- == 0) return t;
        }
        return null;
    }
}
