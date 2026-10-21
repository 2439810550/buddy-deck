using BuddyDeck.App.Services;
using Xunit;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// 左侧导航的**纯逻辑**测试（t80）。
///
/// ## 测什么、不测什么
///
/// 这个文件**不测 WPF 窗口** —— 窗口行为（标题栏、托盘、拖动）只能在真机上验，
/// 而且我已经用 `tools/check_window_chrome.py` 读 Win32 样式位来验（比单元测试更强）。
///
/// 这里测的是**折叠判断与文案推导**这类纯函数 —— 它们有边界，而边界会静默出错：
///   · 折叠阈值写错 → 窄窗口下内容被挤坏，但没人知道"正确值是多少"
///   · tooltip 在展开态也弹 → 鼠标划过每一条都弹，很吵
///   · 折叠态没有 tooltip → **只剩图标，用户不知道那个图标是什么**
/// </summary>
public class ShellNavigationTests
{
    // ===================== 折叠阈值 =====================

    [Theory]
    [InlineData(880)]   // MinWidth —— 最窄，必须折叠
    [InlineData(899)]
    [InlineData(700)]
    public void 窄窗口要折叠(double width)
    {
        Assert.True(ShellNavigation.ShouldCollapse(width), $"{width} 应当折叠");
    }

    [Theory]
    [InlineData(900)]   // 阈值本身 —— 含（>= 阈值不折叠）
    [InlineData(960)]   // MinWidth 默认值
    [InlineData(1220)]  // 默认宽度
    [InlineData(2560)]
    public void 够宽不折叠(double width)
    {
        Assert.False(ShellNavigation.ShouldCollapse(width), $"{width} 不该折叠");
    }

    [Fact]
    public void 阈值在窗口最小宽度与默认宽度之间()
    {
        // 这条是设计约束的护栏：
        //   · 阈值低于 MinWidth(880) → 用户永远看不到展开态（那折叠就没意义）
        //   · 阈值高于默认宽度(1220) → 用户一打开就是折叠态（那更糟）
        // 两个方向都会让这个功能形同虚设，所以钉住区间。
        Assert.True(ShellNavigation.CollapseThreshold >= 880,
            "阈值低于窗口最小宽度的话，用户永远看不到折叠态");
        Assert.True(ShellNavigation.CollapseThreshold < 1220,
            "阈值高于默认宽度的话，用户一打开就是折叠态");
    }

    // ===================== 宽度 =====================

    [Fact]
    public void 折叠比展开窄()
    {
        var expanded = ShellNavigation.WidthFor(false);
        var compact = ShellNavigation.WidthFor(true);

        Assert.True(compact < expanded, "折叠态必须比展开态窄，否则折叠没意义");
        Assert.Equal(ShellNavigation.ExpandedWidth, expanded);
        Assert.Equal(ShellNavigation.CompactWidth, compact);
    }

    [Fact]
    public void 折叠宽度够放一个图标()
    {
        // 太窄的话图标会被裁掉 —— 折叠态就真的什么都没了。
        // 18px 图标 + 左右各 6px 内边距 ≈ 30；留点余量。
        Assert.True(ShellNavigation.CompactWidth >= 40,
            "折叠宽度至少要放得下一个图标 + 内边距");
    }

    // ===================== ToolTip =====================

    [Fact]
    public void 折叠态必须给tooltip()
    {
        // **折叠态下 tooltip 是唯一能说明"这个图标是什么"的东西。**
        // 没有它，用户看到六个光秃秃的图标，只能一个个点过去试。
        var tip = ShellNavigation.TooltipFor("账号", "每个号的标签、过期时间和可用性", collapsed: true);

        Assert.NotNull(tip);
        Assert.Contains("账号", tip);
        Assert.Contains("过期", tip);   // 描述也要在 —— 只说名字帮助有限
    }

    [Fact]
    public void 展开态不给tooltip()
    {
        // 展开态图标旁边已经有文字了。再挂 tooltip 的话，
        // 鼠标划过每一条都弹一个框出来 —— 那是噪音，不是帮助。
        var tip = ShellNavigation.TooltipFor("账号", "每个号的标签、过期时间和可用性", collapsed: false);

        Assert.Null(tip);
    }

    // ===================== 选中态 =====================

    [Fact]
    public void 只在展开态画选中竖条()
    {
        // 折叠态只有 52px 宽，图标就占 18 —— 再挤一条 3px 竖条会把图标挤歪。
        // 折叠态的选中改用底色表达。
        Assert.True(ShellNavigation.ShowSelectionRail(collapsed: false));
        Assert.False(ShellNavigation.ShowSelectionRail(collapsed: true));
    }

    // ===================== 当前页标题 =====================

    [Fact]
    public void 能按序号取到当前页名()
    {
        var titles = new[] { "总览", "账号", "接入池" };

        Assert.Equal("总览", ShellNavigation.CurrentTitle(titles, 0));
        Assert.Equal("接入池", ShellNavigation.CurrentTitle(titles, 2));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void 序号越界返回null而不是抛(int index)
    {
        // 顶栏那行标题是**辅助信息**，不是关键路径。
        // 越界时少一行字可以接受，"窗口起不来"不可以。
        var titles = new[] { "总览", "账号", "接入池" };

        Assert.Null(ShellNavigation.CurrentTitle(titles, index));
    }

    [Fact]
    public void 空序列也不抛()
    {
        Assert.Null(ShellNavigation.CurrentTitle([], 0));
    }
}
