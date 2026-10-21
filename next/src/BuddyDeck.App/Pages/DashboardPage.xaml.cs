using System.Windows;
using System.Windows.Controls;
using BuddyDeck.App.ViewModels;

namespace BuddyDeck.App.Pages;

/// <summary>
/// 总览页的代码后置。
///
/// 里面没有业务逻辑 —— 只有三件事：从容器拿 ViewModel、
/// 刷新一次、以及两个跳转到别的页面的动作。所有判断和取值都在 ViewModel 里。
/// </summary>
public partial class DashboardPage : UserControl
{
    private readonly DashboardViewModel _vm;

    /// <summary>
    /// 外壳的 ViewModel（顶栏、状态轨的数据在它上面）。
    ///
    /// 为什么页面需要它：这一页的 DataContext 是 <see cref="DashboardViewModel"/>，
    /// 但状态轨的数据（`StatusRail` / `RailUsable` / `RailTotal`）挂在**外壳**的
    /// ShellViewModel 上。
    ///
    /// XAML 里原来用 `{Binding ..., RelativeSource={RelativeSource AncestorType=Window}}`
    /// 去走视觉树找 Window —— **那条路在这一页不通**：ContentHost 是 ContentControl，
    /// 换内容时页面的可视树父链会断开重建，RelativeSource 找不到 Window，
    /// 绑定**静默失败**（不报错、不抛异常，就是空着）。
    ///
    /// 表现极具迷惑性：状态轨一条竖条都没有，底部只剩「可用」「个号」两个
    /// 标签竖排着 —— 看起来像布局错位，实际是绑定没接上。
    /// 我一开始照那个假象去查了 DockPanel、宽度、控件测量，全查错了方向。
    ///
    /// 正确做法：把外壳 VM 显式挂成一个具名属性，XAML 用 ElementName 绑它。
    /// 不依赖视觉树形状，页面被放到哪里都成立。
    /// </summary>
    public ShellViewModel Shell { get; }

    public DashboardPage()
    {
        _vm = App.Services.GetRequiredService<DashboardViewModel>();
        Shell = App.Services.GetRequiredService<ShellViewModel>();

        InitializeComponent();
        DataContext = _vm;

        // 进页面就刷新一次。异步但不等 —— 界面先把骨架画出来。
        Loaded += async (_, _) => await _vm.RefreshAsync();
    }

    /// <summary>「管理账号」——交给主窗口切页，页面自己不做导航。</summary>
    private void OnGoToAccounts(object sender, RoutedEventArgs e)
        => Go(ShellViewModel.PageKey.Vault);

    /// <summary>「去聊天测试」——同上。</summary>
    private void OnGoToChat(object sender, RoutedEventArgs e)
        => Go(ShellViewModel.PageKey.Chat);

    /// <summary>复制网关地址。</summary>
    private void OnCopyEndpoint(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_vm.Endpoint); }
        catch (Exception) { /* 剪贴板被锁（少见）不炸页面 */ }
    }

    /// <summary>复制 API key。</summary>
    private void OnCopyApiKey(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_vm.GatewayApiKey); }
        catch (Exception) { /* 同上 */ }
    }

    private void Go(ShellViewModel.PageKey key)
        => (Window.GetWindow(this) as MainWindow)?.NavigateTo(key);
}
