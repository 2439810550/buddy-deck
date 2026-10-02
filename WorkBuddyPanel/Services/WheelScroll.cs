using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WorkBuddyPanel.Services;

/// <summary>
/// 让鼠标悬在列表项上也能用滚轮滚动。
///
/// ## 问题
///
/// WPF 的虚拟化列表（ListBox / ListView）有个经典毛病：
///
///     鼠标在列表项**内部**滚轮 → 不动
///     鼠标挪到列表的空白处滚轮 → 正常滚动
///
/// 原因是虚拟化开启后（`VirtualizingPanel.ScrollUnit="Pixel"`），
/// 滚轮事件由 `VirtualizingStackPanel` 处理，而它只在鼠标直接落在
/// 自己身上时才响应 —— 落在子元素（列表项的那些 Border/Grid/Button）上时，
/// 事件虽然在冒泡，但不一定被那个面板接住。
///
/// 尤其当列表项里嵌了 `Menu` / `ContextMenu` / `ComboBox` 这类
/// 自己会吞滚轮事件的控件时，问题更明显。
///
/// ## 解法
///
/// 在 ListBox 上挂 `PreviewMouseWheel`，**抢先把事件转成滚动**：
///
///     - 算一个「3 行」的滚动量（比系统默认的 3 行手感接近）
///     - 直接调 `ScrollViewer.LineUp/LineDown`
///     - 把事件标记为已处理，避免下面的控件再吃一遍
///
/// ## 为什么用附加属性
///
/// 这样在 XAML 里写一行就行：
///
///     <ListBox svc:WheelScroll.EnableOnChildren="True" ... />
///
/// 不用每个列表都写一遍事件处理器，也不会污染 ViewModel。
/// </summary>
public static class WheelScroll
{
    /// <summary>
    /// 设成 True 就在这个控件上启用「滚轮穿透」。
    /// </summary>
    public static readonly DependencyProperty EnableOnChildrenProperty =
        DependencyProperty.RegisterAttached(
            "EnableOnChildren",
            typeof(bool),
            typeof(WheelScroll),
            new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnableOnChildren(DependencyObject element, bool value)
        => element.SetValue(EnableOnChildrenProperty, value);

    public static bool GetEnableOnChildren(DependencyObject element)
        => (bool)element.GetValue(EnableOnChildrenProperty);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control control) return;

        if (e.NewValue is true)
            control.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            control.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject d) return;

        // 找到内层的 ScrollViewer（ListBox 的模板里有一个）
        var scrollViewer = FindScrollViewer(d);
        if (scrollViewer is null) return;

        // 已经滚到顶/底就别吞事件了 —— 让外层容器能接手（比如整个标签页的滚动）
        var scrollingUp = e.Delta > 0;
        if (scrollingUp && scrollViewer.VerticalOffset <= 0) return;
        if (!scrollingUp && scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight) return;

        // 转成滚动量。
        //
        // 一次滚轮 = 120 delta，系统默认滚 3 行。
        // ScrollUnit=Pixel 时 LineDown() 滚的是「一行的高度」，
        // 所以这里手动按像素滚，手感更接近浏览器。
        var amount = e.Delta > 0 ? -60.0 : 60.0;
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + amount);
        e.Handled = true;
    }

    /// <summary>在视觉树里找第一个 ScrollViewer。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }
}
