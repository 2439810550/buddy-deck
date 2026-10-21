using System.Windows;
using System.Windows.Controls;
using BuddyDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BuddyDeck.App.Pages;

/// <summary>
/// 账号页的代码后置。没有业务逻辑 —— 只转发动作。
/// </summary>
public partial class AccountsPage : UserControl
{
    private readonly AccountsViewModel _vm;

    /// <summary>
    /// 外壳的 ViewModel（状态轨的数据在它上面）。
    ///
    /// 与 <see cref="DashboardPage.Shell"/> 同一个理由：这一页的 DataContext 是
    /// <see cref="AccountsViewModel"/>，但状态轨的数据挂在 ShellViewModel 上。
    /// XAML 里原来用 `RelativeSource={RelativeSource AncestorType=Window}` 去走视觉树 ——
    /// **那条路在页面里不通**（ContentHost 是 ContentControl，换内容时父链断开重建），
    /// 绑定**静默失败**：一条竖条都不画，底部数字也是空的。
    /// 改成 `ElementName=PageRoot` + 这个显式属性，不依赖视觉树形状。
    /// </summary>
    public ShellViewModel Shell { get; }

    public AccountsPage()
    {
        _vm = App.Services.GetRequiredService<AccountsViewModel>();
        Shell = App.Services.GetRequiredService<ShellViewModel>();

        InitializeComponent();
        DataContext = _vm;

        Loaded += async (_, _) => await _vm.RefreshAsync();
    }

    /// <summary>
    /// 三块数据都拉完了吗 —— 给 `--probe` 等数据用。
    ///
    /// 为什么页面要暴露这个：截图工具（`MainWindow.SaveScreenshotAsync`）
    /// 需要在**数据落定之后**才截图，否则拍到的是「只有账号列表」的中间态
    /// —— 那看起来像"积分/限流没做"，实际是截图太早。
    /// </summary>
    public bool DataSettled => _vm.AccountsLoaded && _vm.CreditsLoaded && _vm.HealthLoaded;

    /// <summary>给 `--probe` 的诊断读的（看三块数据到底落到哪一步）。</summary>
    public AccountsViewModel Vm => _vm;

    /// <summary>列表里点了一行 —— 把行本身交给 ViewModel 记住。</summary>
    private void OnSelectAccount(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AccountRowViewModel row }) _vm.Select(row);
    }

    /// <summary>详情浮层的「关闭」—— 清掉选中。</summary>
    private void OnCloseDetail(object sender, RoutedEventArgs e) => _vm.Select(null);

    private void OnToggleImport(object sender, RoutedEventArgs e) => _vm.ToggleImportBox();

    /// <summary>
    /// 顶部「导入账号」按钮 —— 展开/收起「从哪里导入？」面板。
    ///
    /// ⚠ 与 `OnToggleImport` 是同一个动作：合并导入入口后，
    ///   取消按钮和主按钮都调它（一个开一个关，由 `ToggleImportBox` 自己判）。
    /// </summary>
    private void OnImportMenuClick(object sender, RoutedEventArgs e) => _vm.ToggleImportBox();

    /// <summary>
    /// 导出面板的密码框变了 —— 把口令同步给 ViewModel。
    ///
    /// ## ⚠ 为什么用 PasswordBox 而不是 TextBox + 绑定
    ///
    /// WPF 的 `PasswordBox.Password` **不是依赖属性**，绑不了。
    /// 而且这是有意的：明文口令留在可绑定的属性上会进内存 dump、
    /// 也可能被别处的绑定读走。托管到 ViewModel 已经够用
    /// （`ExportAsync` 结束会立刻清空它）。
    ///
    /// ⚠ 这条**必须和口令校验成对存在**：我第一版只写了 ViewModel 里的校验、
    ///   忘了这个框 —— 于是点导出永远报"至少要 8 个字符"（因为口令永远是空）。
    /// </summary>
    private void OnExportPassphraseChanged(object sender, RoutedEventArgs e)
    {
        // ⚠ `PasswordBox` 在 `Wpf.Ui.Controls` 与 `System.Windows.Controls` 里都有 ——
        //   两个命名空间都通过全局 using 进来了，直接用裸名会 CS0104 歧义。
        //   XAML 里写的是 `<PasswordBox>`（默认解析到 WPF 那个），这里必须全限定保持一致。
        if (sender is System.Windows.Controls.PasswordBox box) _vm.ExportPassphrase = box.Password;
    }
}
