using System.Windows.Controls;
using BuddyDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BuddyDeck.App.Pages;

/// <summary>
/// 用量统计页的代码后置。没有业务逻辑 —— 只转发动作。
///
/// 聚合与格式化全在 <see cref="App.Services.StatsPresentation"/>（纯逻辑、被 37 条测试覆盖）；
/// 这里只负责「拿 VM + 在加载时拉一次数据」。
/// </summary>
public partial class StatsPage : UserControl
{
    private readonly StatsViewModel _vm;

    /// <summary>
    /// 外壳的 ViewModel（状态轨的数据在它上面）。
    ///
    /// 与 <see cref="AccountsPage.Shell"/> **同一个理由**：这一页的 DataContext 是
    /// <see cref="StatsViewModel"/>，但状态轨的数据挂在 ShellViewModel 上。
    /// XAML 里用 `RelativeSource AncestorType=Window` 走视觉树那条路**在页面里不通**
    /// （ContentHost 是 ContentControl，换内容时父链断开重建），绑定会**静默失败**。
    /// 改成 `ElementName=PageRoot` + 这个显式属性，不依赖视觉树形状。
    /// </summary>
    public ShellViewModel Shell { get; }

    public StatsPage()
    {
        _vm = App.Services.GetRequiredService<StatsViewModel>();
        Shell = App.Services.GetRequiredService<ShellViewModel>();

        InitializeComponent();
        DataContext = _vm;

        // ⚠ 范围列表在 Loaded 时才建（不在构造函数里）——
        //   构造函数里建的话，`Ranges` 会在对象还没构造完时就被 XAML 读到，
        //   绑定时序上会拿到空集合。
        Loaded += async (_, _) =>
        {
            _vm.InitializeRanges();
            await _vm.RefreshAsync();
        };
    }
}
