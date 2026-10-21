using System.Collections.ObjectModel;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.ViewModels;

/// <summary>
/// 接入池页。
///
/// 一个池 = 一套账号 + 一个接入地址（端口 + key）。给不同的客户端用，
/// 或者把不同来源的号分开，免得一个号被限流拖垮全部。
/// </summary>
public sealed partial class PoolsViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;

    public PoolsViewModel(IGatewayClient gateway) => _gateway = gateway;

    public ObservableCollection<PoolRowViewModel> Pools { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private PoolRowViewModel? _selected;

    public bool HasSelection => Selected is not null;

    /// <summary>选中变了，清掉旧池的账号列表，并加载新池的账号。</summary>
    partial void OnSelectedChanged(PoolRowViewModel? value)
    {
        _poolAccountsAll.Clear();
        PoolAccountsView.Clear();
        AccountFilter = "";
        AccountPage = 1;
        AccountPageCount = 1;
        OnPropertyChanged(nameof(HasPoolAccounts));
        OnPropertyChanged(nameof(AccountPageText));

        // ★ 点池（ListBox 双向绑定直接设 Selected）→ 加载它的账号。
        if (value is not null)
        {
            _ = LoadPoolAccountsAsync(value);
        }
    }

    /// <summary>
    /// 当前**显示**的账号（筛选 + 分页后的视图）。
    /// 全量数据在 <see cref="_poolAccountsAll"/>，这里只是窗口。
    /// </summary>
    public ObservableCollection<BuddyDeck.App.ViewModels.AccountRowViewModel> PoolAccountsView { get; } = [];

    /// <summary>全量账号（筛选前的源数据）。</summary>
    private readonly List<BuddyDeck.App.ViewModels.AccountRowViewModel> _poolAccountsAll = [];

    /// <summary>账号搜索词（按标签/uid 筛选）。</summary>
    [ObservableProperty]
    private string _accountFilter = "";

    partial void OnAccountFilterChanged(string value) => ApplyAccountFilter();

    /// <summary>每页账号数。</summary>
    public const int AccountPageSize = 20;

    /// <summary>当前页码（1 起）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrevPage))]
    [NotifyPropertyChangedFor(nameof(HasNextPage))]
    private int _accountPage = 1;

    /// <summary>筛选后总页数。</summary>
    [ObservableProperty]
    private int _accountPageCount = 1;

    /// <summary>有没有上一页。</summary>
    public bool HasPrevPage => AccountPage > 1;

    /// <summary>有没有下一页。</summary>
    public bool HasNextPage => AccountPage < AccountPageCount;

    /// <summary>分页说明（「第 1 / 3 页 · 共 55 个」）。</summary>
    public string AccountPageText => AccountPageCount <= 1
        ? $"{_poolAccountsAll.Count} 个账号"
        : $"第 {AccountPage} / {AccountPageCount} 页 · 共 {_poolAccountsAll.Count} 个";

    /// <summary>当前选中池的积分总和（格式化）。</summary>
    [ObservableProperty]
    private string _poolCreditTotal = "";

    /// <summary>有积分可显示。</summary>
    public bool HasPoolCredit => PoolCreditTotal.Length > 0;

    /// <summary>选中池的账号加载中。</summary>
    [ObservableProperty]
    private bool _loadingAccounts;

    /// <summary>有没有账号可显示（加载完且非空）。</summary>
    public bool HasPoolAccounts => PoolAccountsView.Count > 0;

    /// <summary>列表空的时候显示邀请动作，不是「暂无数据」。</summary>
    public bool IsEmpty => Pools.Count == 0;

    /// <summary>运行中的池数。</summary>
    public int RunningCount => Pools.Count(p => p.Running);

    /// <summary>停止的池数。</summary>
    public int StoppedCount => Pools.Count(p => !p.Running);

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _notice = "";

    [ObservableProperty]
    private bool _noticeIsError;

    /// <summary>新建/编辑表单。</summary>
    [ObservableProperty]
    private bool _showEditor;

    [ObservableProperty]
    private string _editorTitle = "新建接入池";

    [ObservableProperty]
    private string _editorName = "";

    [ObservableProperty]
    private int _editorPort = 8788;

    [ObservableProperty]
    private string _editorApiKey = "";

    /// <summary>正在编辑的池 id；空 = 新建。</summary>
    private string? _editingId;

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await RefreshCoreAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 真正拉池列表 + 通知统计卡。**不受 IsBusy 挡** ——
    /// 启停/编辑/删除后要立即刷新 UI，但那时 IsBusy 还是 true
    /// （外层操作设的），用 RefreshAsync 会直接 return → UI 不更新
    /// （用户实测反馈「停止池子只有消息响应，卡片/按钮/统计卡不变」）。
    /// </summary>
    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var result = await _gateway.GetPoolsAsync(ct).ConfigureAwait(true);
        Pools.Clear();

        if (!result.Ok || result.Value is null)
        {
            Notice = result.Error ?? "读不到池列表";
            NoticeIsError = true;
        }
        else
        {
            foreach (var p in result.Value) Pools.Add(new PoolRowViewModel(p));
            Notice = "";
            NoticeIsError = false;
        }

        if (Selected is not null && !Pools.Contains(Selected)) Selected = null;
        OnPropertyChanged(nameof(IsEmpty));
        // ★ 统计卡的数字（运行中/停止）依赖集合内容，重建后必须通知。
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(StoppedCount));
    }

    /// <summary>选中一个池（同步，只设选中态）。账号加载由 OnSelectedChanged 触发。</summary>
    public void Select(PoolRowViewModel? row) => Selected = row;

    /// <summary>加载选中池里的账号（层级导航：点池看账号）。</summary>
    private async Task LoadPoolAccountsAsync(PoolRowViewModel row)
    {
        LoadingAccounts = true;
        try
        {
            var result = await _gateway.GetPoolAccountsAsync(row.Id).ConfigureAwait(true);
            _poolAccountsAll.Clear();
            if (result.Ok && result.Value is not null)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var a in result.Value)
                {
                    var accountRow = new BuddyDeck.App.ViewModels.AccountRowViewModel(a, now);
                    accountRow.ApplyHealth();
                    _poolAccountsAll.Add(accountRow);
                }
            }
            ApplyAccountFilter();

            // ★ 拉积分（数据库 cached 端点，秒读），挂到这些账号上。
            //   这样「池子导航到的账号」也能看到余额，而不是空卡。
            await ApplyCreditsToPoolAccountsAsync().ConfigureAwait(true);
        }
        finally
        {
            LoadingAccounts = false;
        }
    }

    /// <summary>从数据库 cached 端点拉积分，按 uid 前缀挂到当前池的账号上。</summary>
    private async Task ApplyCreditsToPoolAccountsAsync()
    {
        var credits = await _gateway.GetAllCreditsAsync().ConfigureAwait(true);
        if (!credits.Ok || credits.Value is null) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var row in _poolAccountsAll)
        {
            var uid = row.Uid ?? "";
            AccountCredits? hit = null;
            foreach (var c in credits.Value)
            {
                var tag = (c.Id ?? "").TrimEnd(':');
                if (tag.Length >= 8 && uid.StartsWith(tag, StringComparison.Ordinal))
                {
                    hit = c;
                    break;
                }
            }
            row.ApplyCredits(hit, now);
        }

        // ★ 顶部积分 = **跨池总积分**（所有池的积分和，数据库联表 pool_accounts）。
        //   不是当前选中池 —— 用户要求「账号池上那个总积分是跨池汇总」。
        var total = await _gateway.GetPoolCreditTotalAsync().ConfigureAwait(true);
        PoolCreditTotal = total.Ok && total.Value > 0
            ? BuddyDeck.App.Services.AccountPresentation.FormatCredits(total.Value)
            : "";
        OnPropertyChanged(nameof(HasPoolCredit));
    }

    /// <summary>按筛选词 + 分页重建显示窗口。</summary>
    private void ApplyAccountFilter()
    {
        var keyword = AccountFilter.Trim();
        var matched = string.IsNullOrEmpty(keyword)
            ? _poolAccountsAll
            : _poolAccountsAll.Where(a =>
                (a.DisplayName ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (a.UidShort ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();

        AccountPageCount = Math.Max(1, (int)Math.Ceiling(matched.Count / (double)AccountPageSize));
        AccountPage = Math.Clamp(AccountPage, 1, AccountPageCount);

        var start = (AccountPage - 1) * AccountPageSize;
        var window = matched.Skip(start).Take(AccountPageSize).ToList();

        PoolAccountsView.Clear();
        foreach (var a in window) PoolAccountsView.Add(a);

        OnPropertyChanged(nameof(HasPoolAccounts));
        OnPropertyChanged(nameof(AccountPageText));
        OnPropertyChanged(nameof(HasPrevPage));
        OnPropertyChanged(nameof(HasNextPage));
    }

    /// <summary>上一页。</summary>
    [RelayCommand]
    public void PrevPage()
    {
        if (AccountPage > 1)
        {
            AccountPage--;
            ApplyAccountFilter();
        }
    }

    /// <summary>下一页。</summary>
    [RelayCommand]
    public void NextPage()
    {
        if (AccountPage < AccountPageCount)
        {
            AccountPage++;
            ApplyAccountFilter();
        }
    }

    [RelayCommand]
    public void NewPool()
    {
        _editingId = null;
        EditorTitle = "新建接入池";
        EditorName = "";
        EditorPort = 8788;
        EditorApiKey = "";
        ShowEditor = true;
    }

    [RelayCommand]
    public void EditSelected()
    {
        if (Selected is null) return;
        _editingId = Selected.Id;
        EditorTitle = $"编辑「{Selected.DisplayName}」";
        EditorName = Selected.DisplayName;
        EditorPort = Selected.Port;
        EditorApiKey = Selected.ApiKey;
        ShowEditor = true;
    }

    [RelayCommand]
    public void CancelEditor() => ShowEditor = false;

    [RelayCommand]
    public async Task SaveEditorAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(EditorName))
        {
            Notice = "名字不能是空的。";
            NoticeIsError = true;
            return;
        }

        var pool = new PoolSummary
        {
            Id = _editingId ?? $"pool-{EditorPort}",
            DisplayName = EditorName.Trim(),
            Port = EditorPort,
            ApiKey = EditorApiKey.Trim(),
        };

        IsBusy = true;
        try
        {
            var result = await _gateway.UpsertPoolAsync(pool, ct).ConfigureAwait(true);
            Notice = result.Ok ? "已保存。" : result.Error ?? "保存失败";
            NoticeIsError = !result.Ok;
            if (result.Ok) ShowEditor = false;

            await RefreshCoreAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>启停一个池的接入地址。</summary>
    [RelayCommand]
    public async Task ToggleRunningAsync(PoolRowViewModel? row, CancellationToken ct = default)
    {
        if (row is null) return;

        IsBusy = true;
        try
        {
            var target = !row.Running;
            var result = await _gateway.SetPoolRunningAsync(row.Id, target, ct).ConfigureAwait(true);
            Notice = result.Ok
                ? target ? $"「{row.DisplayName}」已启动。" : $"「{row.DisplayName}」已停止。"
                : result.Error ?? "操作失败";
            NoticeIsError = !result.Ok;

            await RefreshCoreAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RemoveAsync(PoolRowViewModel? row, CancellationToken ct = default)
    {
        if (row is null) return;
        if (row.IsDefault)
        {
            Notice = "默认池不能删 —— 别的客户端默认连的就是它。";
            NoticeIsError = true;
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _gateway.RemovePoolAsync(row.Id, ct).ConfigureAwait(true);
            Notice = result.Ok ? $"已删除「{row.DisplayName}」。" : result.Error ?? "删除失败";
            NoticeIsError = !result.Ok;

            Selected = null;
            await RefreshCoreAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>池列表里的一行。</summary>
public sealed class PoolRowViewModel
{
    public PoolRowViewModel(PoolSummary pool)
    {
        Id = pool.Id;
        DisplayName = string.IsNullOrWhiteSpace(pool.DisplayName) ? pool.Id : pool.DisplayName;
        Port = pool.Port;
        ApiKey = pool.ApiKey;
        Running = pool.Running;
        AccountCount = pool.AccountCount;
        IsDefault = pool.IsDefault;
        LastError = pool.LastError;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public int Port { get; }
    public string ApiKey { get; }
    public bool Running { get; }
    public int AccountCount { get; }
    public bool IsDefault { get; }
    public string? LastError { get; }

    /// <summary>客户端要粘的那个地址。</summary>
    public string Endpoint => $"http://127.0.0.1:{Port}/v1";

    /// <summary>一句话状态 —— 和 WorkBuddyPanel 一样，要能区分「别人的实例在跑」。</summary>
    public string StatusText => Running
        ? "运行中"
        : !string.IsNullOrEmpty(LastError) ? "启动失败" : "已停止";

    /// <summary>
    /// 启停按钮上的字。
    ///
    /// 动作名全流程同名（design 计划 3.1 第 3 条）：按钮写「启动」，
    /// 成功提示就说「已启动」，不写成「操作成功」。
    /// </summary>
    public string ToggleText => Running ? "停止" : "启动";

    public string AccountCountText => AccountCount switch
    {
        < 0 => "账号数未知",
        0 => "还没有账号",
        1 => "1 个账号",
        _ => $"{AccountCount} 个账号",
    };

    /// <summary>默认池不给删。</summary>
    public bool CanRemove => !IsDefault;
}
