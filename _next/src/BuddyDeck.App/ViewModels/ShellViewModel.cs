using System.Collections.ObjectModel;
using BuddyDeck.App.Controls;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.ViewModels;

/// <summary>
/// 界面外壳。
///
/// 管两件事：现在在哪一页，以及状态轨上那几条竖条。
/// 页面自己的数据由各自的 ViewModel 管 —— 外壳不替它们加载，
/// 不然它会退化成一个什么都塞的上帝对象。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>导航项的键。用枚举而不是字符串：改名字时编译器会提醒。</summary>
    public enum PageKey
    {
        Dashboard,

        /// <summary>账号池（原「账号」+「接入池」合并）—— 池 → 账号 → 详情层级导航。</summary>
        Vault,

        Chat,

        /// <summary>用量统计（t72）—— 请求 / Token / 积分三个维度。</summary>
        Stats,

        Logs,
        Settings,
    }

    [ObservableProperty]
    private PageKey _current = PageKey.Dashboard;

    /// <summary>导航项。顺序 = 顶栏从左到右，也是按「多久看一次」排的。</summary>
    public ObservableCollection<NavigationEntry> Navigation { get; } = [];

    /// <summary>
    /// 状态轨的竖条。
    ///
    /// 类型是 design 的 <see cref="StatusRailItem"/>，不是我自己的 ——
    /// 那个 record 就是 `bd:StatusRail` 控件的输入契约，我不再包一层，
    /// 免得同一个概念两处定义。
    /// </summary>
    public ObservableCollection<StatusRailItem> StatusRail { get; } = [];

    [ObservableProperty]
    private string _railTotal = "0";

    [ObservableProperty]
    private string _railUsable = "0";

    /// <summary>顶栏左手那句话。网关在线时说「已启动」，离线时说「未启动」。</summary>
    [ObservableProperty]
    private string _statusLine = "网关未启动";

    /// <summary>在线时的补充信息（端口 + 产物名）。离线时为空。</summary>
    [ObservableProperty]
    private string _gatewayDetail = "";

    /// <summary>离线时的原因（一句人话，来自探活返回的 Error）。在线时为空。</summary>
    [ObservableProperty]
    private string _offlineReason = "";

    /// <summary>网关在线时报告的模型数量（左侧状态区单独展示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelCount))]
    private int _modelCount;

    /// <summary>有模型数量可显示时（网关在线且报过数）。</summary>
    public bool HasModelCount => ModelCount > 0;

    [ObservableProperty]
    private bool _gatewayOnline;

    /// <summary>磁盘上的代码和正在跑的是不是同一份 —— 顶栏的状态点会加一圈环。</summary>
    [ObservableProperty]
    private bool _gatewayStale;

    /// <summary>网关控制器（左侧状态区直接启停用）。</summary>
    private readonly IGatewayController _gatewayController;

    /// <summary>网关在不在跑（子进程视角，与 GatewayOnline 的探活视角互补）。</summary>
    [ObservableProperty]
    private bool _gatewayRunning;

    /// <summary>启动/停止中 —— 按钮禁用防连点。</summary>
    [ObservableProperty]
    private bool _gatewayBusy;

    /// <summary>启动网关（左侧状态区的开关按钮）。</summary>
    [RelayCommand]
    public async Task ToggleGatewayAsync(CancellationToken ct = default)
    {
        if (GatewayBusy) return;
        GatewayBusy = true;
        try
        {
            if (GatewayRunning)
            {
                _gatewayController.Stop();

                // ★ 停止后**立即**把界面置为离线 —— 不等下一次探活（最多 5 秒）。
                //   用户点停就停，状态点/文字/按钮一起变。
                GatewayOnline = false;
                StatusLine = "网关未启动";
                GatewayDetail = "";
                OfflineReason = "已手动停止，点 ▶ 重新启动";
                ModelCount = 0;
            }
            else
            {
                await _gatewayController.RestartAsync(ct).ConfigureAwait(true);

                // 启动成功 → 立即置为在线（探活会再确认细节）。
                GatewayOnline = true;
                StatusLine = "网关已启动";
                OfflineReason = "";
            }
        }
        catch (Exception ex)
        {
            // 启动失败（端口被占等）—— 原因交给 OfflineReason，由探活更新。
            OfflineReason = ex.Message;
        }
        finally
        {
            GatewayBusy = false;
            GatewayRunning = _gatewayController.IsRunning;
        }
    }

    /// <summary>由 MainWindow 探活后同步「子进程在不在跑」到界面。</summary>
    public void SyncGatewayRunning() => GatewayRunning = _gatewayController.IsRunning;

    /// <summary>任务库 VM（后台任务进度）。由 App 启动时注入。</summary>
    public TasksViewModel? Tasks { get; private set; }

    /// <summary>任务面板是否可见（有任务时显示）。</summary>
    [ObservableProperty]
    private bool _tasksVisible;

    /// <summary>任务列表。</summary>
    public System.Collections.ObjectModel.ObservableCollection<TaskRowViewModel> TaskList
        => Tasks?.Tasks ?? [];

    /// <summary>顶部一句汇总（「2 个任务进行中」）。</summary>
    public string TasksSummary
    {
        get
        {
            if (Tasks is null || Tasks.RunningCount == 0) return "";
            return Tasks.RunningCount == 1 ? "1 个任务进行中" : $"{Tasks.RunningCount} 个任务进行中";
        }
    }

    /// <summary>挂接任务库 VM（App 启动后调用一次）。</summary>
    public void AttachTasks(TasksViewModel tasks)
    {
        Tasks = tasks;
        tasks.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TasksViewModel.RunningCount) or nameof(TasksViewModel.IsOpen))
            {
                OnPropertyChanged(nameof(TasksSummary));
                // ★ 面板显隐**只由 IsOpen 决定**（不再因 RunningCount 强制显示）。
                //   之前 `IsOpen || RunningCount > 0` 导致任务运行时用户点遮罩
                //   设 IsOpen=false 也没用 —— RunningCount>0 又把它拉回来，关不掉。
                //   现在：StartAsync 设 IsOpen=true 自动展开；用户点遮罩 IsOpen=false
                //   收起；任务完成不自动消失也不自动弹回。
                TasksVisible = tasks.IsOpen;
            }
        };
        TasksVisible = tasks.IsOpen;
    }

    /// <summary>状态轨要的重算基准时刻。用 DateTimeOffset? 是因为控件契约如此。</summary>
    public DateTimeOffset RailNow { get; private set; } = DateTimeOffset.UtcNow;

    public ShellViewModel(IGatewayController gatewayController)
    {
        _gatewayController = gatewayController;

        Navigation.Add(New(PageKey.Dashboard, "总览", "网关和账号池现在什么状态", SymbolRegular.DataUsage24));
        Navigation.Add(New(PageKey.Vault, "账号池", "池 → 账号 → 详情，一套账号一个地址", SymbolRegular.People24));
        Navigation.Add(New(PageKey.Chat, "聊天测试", "发一句话，看网关到底通不通", SymbolRegular.Chat24));

        // 统计排在聊天之后、日志之前：它和「聊天测试」是一对
        // （一个产生用量、一个看用量），而日志是排障用的、看得最少。
        Navigation.Add(New(PageKey.Stats, "用量统计", "请求、Token、积分花了多少", SymbolRegular.ChartMultiple24));

        Navigation.Add(New(PageKey.Logs, "日志", "网关记了什么（只有元数据）", SymbolRegular.DocumentText24));
        Navigation.Add(New(PageKey.Settings, "设置", "端口、密钥、变体、保险库位置", SymbolRegular.Settings24));

        SyncCurrentFlags();
    }

    private NavigationEntry New(PageKey key, string title, string description, SymbolRegular icon)
        => new(this, key, title, description, icon);

    partial void OnCurrentChanged(PageKey value) => SyncCurrentFlags();

    /// <summary>
    /// 把「现在选中哪一个」写回每个导航项。
    ///
    /// 为什么写回而不是让 XAML 自己算：DataTemplate 里没法直接比较
    /// 「这一项是不是 Current」，得让项自己知道自己选没选中。
    /// </summary>
    private void SyncCurrentFlags()
    {
        foreach (var entry in Navigation) entry.NotifyIsCurrent(Current);
    }

    /// <summary>
    /// 用账号列表刷新状态轨。
    ///
    /// 竖条高度按剩余寿命占 7 天的比例 —— 和账号页用同一个刻度，
    /// 免得同一件事在两处看起来不一样。
    /// </summary>
    public void UpdateRail(IReadOnlyList<AccountSummary> accounts, DateTimeOffset now)
    {
        RailNow = now;
        StatusRail.Clear();

        var nowMs = now.ToUnixTimeMilliseconds();
        foreach (var account in accounts)
        {
            var ratio = account.ExpiresAtMs <= 0
                ? 1.0
                : Math.Clamp(
                    (account.ExpiresAtMs - nowMs) / (double)TimeSpan.FromDays(7).TotalMilliseconds,
                    0, 1);

            StatusRail.Add(new StatusRailItem(ratio, QuotaStateFor(ratio)));
        }

        RailTotal = accounts.Count.ToString();
        RailUsable = accounts
            .Count(a => AccountPresentation.DeriveState(a.ExpiresAtMs, now)
                is AccountState.Healthy or AccountState.ExpiringSoon)
            .ToString();
    }

    /// <summary>
    /// 剩余比例 → 用量档位。
    ///
    /// 阈值直接抄 design 的 <see cref="QuotaState"/> 语义（60/30/10），
    /// 这样顶栏的竖条和账号列表里的用量条同一条线，不会一个说「还早」
    /// 另一个已经标红。
    /// </summary>
    private static QuotaState QuotaStateFor(double remainingRatio) => remainingRatio switch
    {
        > 0.60 => QuotaState.Normal,
        > 0.30 => QuotaState.Low,
        > 0.10 => QuotaState.Warning,
        _ => QuotaState.Critical,
    };
}

/// <summary>顶栏上的一个导航项。</summary>
public sealed partial class NavigationEntry : ObservableObject
{
    private readonly ShellViewModel _owner;

    public NavigationEntry(
        ShellViewModel owner,
        ShellViewModel.PageKey key,
        string title,
        string description,
        SymbolRegular icon)
    {
        _owner = owner;
        Key = key;
        Title = title;
        Description = description;
        Icon = icon;
    }

    public ShellViewModel.PageKey Key { get; }
    public string Title { get; }
    public string Description { get; }
    public SymbolRegular Icon { get; }

    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>由外壳在切页时调用 —— 项自己没法知道别处发生了什么。</summary>
    internal void NotifyIsCurrent(ShellViewModel.PageKey current)
        => IsCurrent = current == Key;
}
