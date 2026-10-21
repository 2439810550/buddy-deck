using BuddyDeck.App.ViewModels;

namespace BuddyDeck.App.Pages;

/// <summary>日志页的代码后置。只做加载，没有业务逻辑。</summary>
public partial class LogsPage : System.Windows.Controls.UserControl
{
    private readonly LogsViewModel _vm;

    public LogsPage()
    {
        _vm = App.Services.GetRequiredService<LogsViewModel>();
        InitializeComponent();
        DataContext = _vm;

        // 默认选中「全部级别」和「不限时间」，不然过滤器是空的，用户会以为坏了
        _vm.Level ??= _vm.Levels[0];
        _vm.Window ??= _vm.Windows[0];

        Loaded += async (_, _) => await _vm.RefreshAsync();
    }
}
