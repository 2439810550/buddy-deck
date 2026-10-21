using BuddyDeck.App.ViewModels;

namespace BuddyDeck.App.Pages;

/// <summary>设置页的代码后置。只做加载，没有业务逻辑。</summary>
public partial class SettingsPage : System.Windows.Controls.UserControl
{
    private readonly SettingsViewModel _vm;

    public SettingsPage()
    {
        _vm = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        DataContext = _vm;

        Loaded += async (_, _) => await _vm.LoadAsync();
    }
}
