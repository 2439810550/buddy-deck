using System.Windows;
using System.Windows.Controls;
using BuddyDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BuddyDeck.App.Pages;

/// <summary>账号池页（嵌套层级导航）的代码后置。只转发动作。</summary>
public partial class VaultPage : UserControl
{
    private readonly VaultViewModel _vm;

    public VaultPage()
    {
        _vm = App.Services.GetRequiredService<VaultViewModel>();
        InitializeComponent();
        DataContext = _vm;

        Loaded += async (_, _) => await _vm.LoadAsync();
    }

    public bool DataSettled => _vm.Accounts.AccountsLoaded && _vm.Accounts.CreditsLoaded && _vm.Accounts.HealthLoaded;
    public VaultViewModel Vm => _vm;

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = _vm.LoadAsync();

    private void OnImportClick(object sender, RoutedEventArgs e) => _vm.Accounts.ToggleImportBox();

    // ══ 池表格行动作（new_api 风格）════

    private void OnPoolToggleRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PoolRowViewModel pool })
            _ = _vm.Pools.ToggleRunningAsync(pool);
    }

    private void OnPoolEditRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PoolRowViewModel pool })
        {
            _vm.Pools.Select(pool);
            _vm.Pools.EditSelectedCommand.Execute(null);
        }
    }

    private void OnPoolRemoveRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PoolRowViewModel pool })
            _ = _vm.Pools.RemoveAsync(pool);
    }

    /// <summary>点池名称 → 进入账号层。</summary>
    private void OnPoolRowOpen(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PoolRowViewModel pool })
            _vm.OpenPool(pool);
    }

    // ══ 账号点击（下沉到详情层）════

    private void OnAccountClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AccountRowViewModel row })
        {
            _vm.OpenAccount(row);
        }
    }
}
