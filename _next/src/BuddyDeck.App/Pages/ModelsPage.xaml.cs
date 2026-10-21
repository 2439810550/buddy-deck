using BuddyDeck.App.ViewModels;

namespace BuddyDeck.App.Pages;

/// <summary>模型页的代码后置。只做加载，没有业务逻辑。</summary>
public partial class ModelsPage : System.Windows.Controls.UserControl
{
    private readonly ModelsViewModel _vm;

    public ModelsPage()
    {
        _vm = App.Services.GetRequiredService<ModelsViewModel>();
        InitializeComponent();
        DataContext = _vm;

        // 默认选中第一项排序，不然下拉框是空的，用户会以为坏了
        _vm.Sort ??= _vm.Sorts[0];

        Loaded += async (_, _) => await _vm.RefreshAsync();
    }
}
