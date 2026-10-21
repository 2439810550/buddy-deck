using System.Windows;
using System.Windows.Controls;
using BuddyDeck.App.ViewModels;

namespace BuddyDeck.App.Controls;

/// <summary>号池卡片。业务动作（启停/编辑/删除/查看账号）转发给页面。</summary>
public partial class PoolCardView : UserControl
{
    public PoolCardView()
    {
        InitializeComponent();
    }

    /// <summary>卡片的 DataContext 就是 PoolRowViewModel，动作用它拿 id。</summary>
    private PoolRowViewModel? Pool => DataContext as PoolRowViewModel;

    // ⚠ 用**委托属性**而不是 event：XAML 里 `ToggleRequested="OnPoolToggle"`
    // 是给属性赋一个方法，方法签名可以是任意（这里是 Action&lt;PoolRowViewModel&gt;）。
    // 用 event 的话 XAML 事件绑定强制标准签名 (object, EventArgs)，
    // 页面里的 OnPoolToggle(PoolRowViewModel) 就对不上 → 按钮点了没反应。

    /// <summary>启停。由页面赋值。</summary>
    public Action<PoolRowViewModel>? ToggleRequested { get; set; }

    /// <summary>编辑。由页面赋值。</summary>
    public Action<PoolRowViewModel>? EditRequested { get; set; }

    /// <summary>删除。由页面赋值。</summary>
    public Action<PoolRowViewModel>? RemoveRequested { get; set; }

    /// <summary>查看账号。由页面赋值。</summary>
    public Action<PoolRowViewModel>? SelectRequested { get; set; }

    private void OnTogglePool(object sender, RoutedEventArgs e)
    {
        if (Pool is { } p) ToggleRequested?.Invoke(p);
    }

    private void OnEditPool(object sender, RoutedEventArgs e)
    {
        if (Pool is { } p) EditRequested?.Invoke(p);
    }

    private void OnRemovePool(object sender, RoutedEventArgs e)
    {
        if (Pool is { } p) RemoveRequested?.Invoke(p);
    }

    private void OnSelectPool(object sender, RoutedEventArgs e)
    {
        if (Pool is { } p) SelectRequested?.Invoke(p);
    }

    private void OnMorePool(object sender, RoutedEventArgs e)
    {
        MorePanel.Visibility = MorePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
