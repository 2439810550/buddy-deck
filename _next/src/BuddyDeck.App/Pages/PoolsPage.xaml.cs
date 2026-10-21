using System.Windows;
using BuddyDeck.App.ViewModels;

namespace BuddyDeck.App.Pages;

/// <summary>接入池页的代码后置。只转发动作，没有业务逻辑。</summary>
public partial class PoolsPage : System.Windows.Controls.UserControl
{
    private readonly PoolsViewModel _vm;

    public PoolsPage()
    {
        _vm = App.Services.GetRequiredService<PoolsViewModel>();
        InitializeComponent();
        DataContext = _vm;

        Loaded += async (_, _) => await _vm.RefreshAsync();
    }

    private void OnTogglePool(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PoolRowViewModel row })
        {
            _ = _vm.ToggleRunningAsync(row);
        }
    }

    /// <summary>点池列表里的池：选中它，加载它的账号（层级导航）。</summary>
    private void OnSelectPool(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PoolRowViewModel row)
        {
            _vm.Select(row);
        }
    }

    private void OnEditPool(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PoolRowViewModel row })
        {
            _vm.Select(row);
            _vm.EditSelectedCommand.Execute(null);
        }
    }

    private void OnRemovePool(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PoolRowViewModel row })
        {
            _ = _vm.RemoveAsync(row);
        }
    }
}
