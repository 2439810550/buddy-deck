using System.Collections.ObjectModel;
using BuddyDeck.Core;

namespace BuddyDeck.App.ViewModels;

/// <summary>
/// 账号页：列表 + 详情。
///
/// 信息架构是「列表 → 详情」：左边选一个号，右边看它的全部信息。
/// 不弹对话框 —— 弹窗一关就看不到上下文了，而管账号时最需要上下文。
/// </summary>
public sealed partial class AccountsViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;

    /// <summary>
    /// 文件选择器。可空 —— 测试夹具不挂它时「从文件导入」退化成一条
    /// 明确提示，而不是 NullReferenceException（与 GatewayApp 那几个
    /// 可选依赖同样的处理）。
    /// </summary>
    private readonly IFilePicker? _picker;

    public AccountsViewModel(IGatewayClient gateway, IFilePicker? picker = null)
    {
        _gateway = gateway;
        _picker = picker;
    }

    /// <summary>全部账号（未过滤）。</summary>
    private readonly List<AccountRowViewModel> _all = [];

    /// <summary>列表里显示的（过滤后）。</summary>
    public ObservableCollection<AccountRowViewModel> Rows { get; } = [];

    /// <summary>选中的那一个。右侧详情面板绑它。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private AccountRowViewModel? _selected;

    public bool HasSelection => Selected is not null;

    /// <summary>选中账号变了（含 ListBox 双向绑定直接赋值），重算用量明细。</summary>
    partial void OnSelectedChanged(AccountRowViewModel? value)
    {
        RenameText = value?.DisplayName ?? "";
        RebuildSelectedUsage();
    }

    /// <summary>搜索框内容。按标签/uid/域名匹配。</summary>
    [ObservableProperty]
    private string _filter = "";

    partial void OnFilterChanged(string value) => ApplyFilter();

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>上一次操作的结果，显示在页面顶部。成功也用同一块地方，颜色不同。</summary>
    [ObservableProperty]
    private string _notice = "";

    [ObservableProperty]
    private bool _noticeIsError;

    /// <summary>导入框里的内容（桌面 App 的登录文件）。</summary>
    [ObservableProperty]
    private string _importPayload = "";

    /// <summary>重命名框里的内容。</summary>
    [ObservableProperty]
    private string _renameText = "";

    [ObservableProperty]
    private bool _showImportBox;

    /// <summary>待确认的导入载荷（空 = 没有待确认的导入）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingImport))]
    private string _pendingPayload = "";

    /// <summary>
    /// 确认框上的文案：「将导入 N 个账号（跳过 M 个，原因…）」。
    /// 多行 —— 有跳过项时逐条列出来。
    /// </summary>
    [ObservableProperty]
    private string _pendingSummary = "";

    /// <summary>有没有待确认的导入（界面据此显示/隐藏确认框）。</summary>
    public bool HasPendingImport => PendingPayload.Length > 0;

    /// <summary>列表为空且不是过滤造成的 —— 空态文案要说「怎么开始」，不是「暂无数据」。</summary>
    public bool IsEmpty => Rows.Count == 0;

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _gateway.GetAccountsAsync(ct).ConfigureAwait(true);
            _all.Clear();

            if (!result.Ok || result.Value is null)
            {
                SetNotice(result.Error ?? "读不到账号列表", isError: true);
            }
            else
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var a in result.Value)
                {
                    var row = new AccountRowViewModel(a, now);
                    // ★ 冷却/冷冻的健康信息必须套用（与 DashboardViewModel 同一条路径）——
                    //   不调的话 CooldownText/FrozenText 永远是空，
                    //   用户看到的就是「缺模型冷却情况、什么时候触发限流、什么时候解除」。
                    row.ApplyHealth();
                    _all.Add(row);
                }

                if (result.Ok) ClearNotice();
            }

            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
            // 账号列表拉完了（成败都算）—— 给 `--probe` 等数据用。
            AccountsLoaded = true;
        }

        // 积分**单独拉**，不挡上面那条路径。
        //
        // 为什么不并进上面那个 try：积分要向上游逐个账号查，可能慢；
        // 而且端点可能还没实现（t70 在做）。**账号列表不该等它** ——
        // 先让用户看到号，积分到了再补上。
        await LoadCreditsAsync(ct).ConfigureAwait(true);

        // 限流矩阵也单独拉（同上：它要枚举 32 个模型 × N 个号，可能慢）。
        await LoadModelHealthAsync(ct).ConfigureAwait(true);

        // 用量明细（每个号每笔请求的积分消耗）—— 也单独拉，不挡主列表。
        await LoadUsageAsync(ct).ConfigureAwait(true);
    }

    // ══════════════════════════════════════════════════════════
    //  模型限流矩阵（用户实测反馈：「应该告诉用户这些模型限流了、
    //  啥时候解除限流」）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 有问题的模型（限流过 / 正在冷却 / 被降级过）—— **只列有问题的**。
    ///
    /// 为什么不全列：32 个模型全摆出来，那 30 个正常的会把 2 个有问题的淹掉。
    /// 这一块的意义就是"告诉我哪个不能用"，那就只放不能用的。
    /// </summary>
    public ObservableCollection<ModelHealthRow> ProblemModels { get; } = [];

    /// <summary>有没有需要提醒的模型（XAML 用它决定整块显不显示）。</summary>
    [ObservableProperty]
    private bool _hasProblemModels;

    // ── 三块数据的完成标志（给 `--probe` 用）────────────────────
    //
    // 为什么需要它们（实测踩过）：
    //   账号页的三块数据是**分别异步拉的**，其中积分与模型矩阵是**慢接口**
    //   （积分要逐个账号向上游查，矩阵要枚举 32 个模型 × N 个号）。
    //   截图工具原来只等 600ms ⇒ 拍到中间态 ⇒ **看起来像功能没做**。
    //
    // ⇒ 把"拉完了"这件事显式暴露出来，让截图能等它 ——
    //   这不是为了让测试通过，而是让截图**忠实地反映用户看到的样子**。

    /// <summary>账号列表拉完了吗。</summary>
    public bool AccountsLoaded { get; private set; }

    /// <summary>积分拉完了吗（成败都算"拉完"）。</summary>
    public bool CreditsLoaded { get; private set; }

    /// <summary>模型健康矩阵拉完了吗（成败都算"拉完"）。</summary>
    public bool HealthLoaded { get; private set; }

    /// <summary>
    /// 池子总积分（所有账号剩余积分之和）—— 账号页顶部那个汇总数字。
    /// 空 = 还没拉到数据。
    /// </summary>
    [ObservableProperty]
    private string _poolCreditTotal = "";

    /// <summary>总积分旁边那句说明（几个号有积分/共几个号）。</summary>
    [ObservableProperty]
    private string _poolCreditDetail = "";

    // ══════════════════════════════════════════════════════════
    //  账号积分消耗明细（用户需求：每个号能看见每笔请求消耗了多少积分）
    // ══════════════════════════════════════════════════════════

    /// <summary>全部用量明细（取一次，按选中账号过滤展示）。</summary>
    private IReadOnlyList<UsageRecord> _usage = [];

    /// <summary>选中账号的积分消耗明细（详情面板里展示）。</summary>
    public ObservableCollection<UsageRowViewModel> SelectedUsage { get; } = [];

    /// <summary>选中账号的累计消耗（格式化的积分值）。</summary>
    [ObservableProperty]
    private string _selectedCreditSpent = "";

    /// <summary>选中账号的请求次数说明。</summary>
    [ObservableProperty]
    private string _selectedUsageSummary = "";

    /// <summary>有没有选中账号的消耗明细（控制详情面板那块的显隐）。</summary>
    [ObservableProperty]
    private bool _hasSelectedUsage;

    /// <summary>
    /// 拉模型健康矩阵并挑出有问题的。
    ///
    /// **失败时整块不渲染**（`HasProblemModels = false`）——
    /// 与积分同一条约定：读不到 ≠ 一切正常。
    /// 宁可不说，也不要说一句"全部健康"（那是**假绿**）。
    /// </summary>
    private async Task LoadModelHealthAsync(CancellationToken ct)
    {
        try
        {
            await LoadModelHealthCoreAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            // ⚠ 成败都置位 —— 否则失败时截图会一直等到超时。
            //   "拉完了"与"拉到了"是两件事（与团队那条「读不到 vs 本来就没有」同源）。
            HealthLoaded = true;
        }
    }

    private async Task LoadModelHealthCoreAsync(CancellationToken ct)
    {
        var result = await _gateway.GetPoolHealthAsync(ct).ConfigureAwait(true);

        ProblemModels.Clear();

        if (!result.Ok || result.Value is null)
        {
            HasProblemModels = false;
            return;
        }

        // 挑「有问题的」：
        //   · 此刻有号在冷却        → 现在就不能用
        //   · 限流过（历史）        → 曾经不能用，用户该知道
        //   · 被降级过              → "平时老换模型"的证据
        foreach (var m in result.Value.Models
            .Where(m => m.CoolingNow || m.RateLimitedAccounts > 0 || m.FallbackCount > 0)
            .OrderByDescending(m => m.CoolingNow)
            .ThenByDescending(m => m.RateLimitedAccounts)
            .ThenBy(m => m.Model, StringComparer.Ordinal))
        {
            ProblemModels.Add(new ModelHealthRow(m, DateTimeOffset.Now));
        }

        HasProblemModels = ProblemModels.Count > 0;
    }

    /// <summary>
    /// 拉积分并挂到对应的行上。
    ///
    /// **失败时什么都不做** —— 行的 `HasCredits` 保持 false，界面整块不渲染。
    /// **绝不显示成 "0 积分"**：0 和"读不到"是两件事，混起来会让用户
    /// 以为积分真的没了。这与 observability 那条「读不到 vs 本来就没有」同一道理。
    /// </summary>
    private async Task LoadCreditsAsync(CancellationToken ct)
    {
        try
        {
            await LoadCreditsCoreAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            // 成败都置位（同 LoadModelHealthAsync 的理由）。
            CreditsLoaded = true;
        }
    }

    private async Task LoadCreditsCoreAsync(CancellationToken ct)
    {
        // ★ 用批量端点一次拿全部账号的积分（单个 /admin/credits 只回一个号，
        //   就是用户看到「只有第一个号有积分」的根因）。
        var result = await _gateway.GetAllCreditsAsync(ct).ConfigureAwait(true);

        if (!result.Ok || result.Value is null)
        {
            // 端点没实现（404）/ 网关没起 —— 静默保持空态。
            // 不弹提示：用户没做错什么，而且账号页顶部已经可能有别的提示了。
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // ⚠ 匹配用**前缀**，不能用相等。
        //
        // 实测数据（这是本轮真正的根因）：
        // <code>
        // 账号列表 uid     = 3e563042-d9da-4ce6-bbb8-bf52debdbff0   ← 完整 uuid
        // 积分端 accountTag = 3e563042                               ← **前 8 位**
        // </code>
        //
        // 网关的积分端点是按 uid **截断到前 8 位** 回 accountTag 的
        // （那是它的"账号标签"格式，人读友好）。所以：
        //   · 用 `Id`（完整 uuid 带冒号）匹配 → 不相等
        //   · 用 `Uid.TrimEnd(':')` 匹配     → **也不相等**（一个是 36 位、一个是 8 位）
        //
        // ⇒ 判据：账号 uid **以** 积分 tag 开头（且长度够，避免"3"匹配一切）。
        //   实测踩过：接口调成功、`CreditsLoaded=True`，但 `Row0HasCredits=False` ——
        //   数据到了却没挂上行，界面就是空的，**而且不报错**。
        foreach (var row in _all)
        {
            var uid = row.Uid ?? "";
            AccountCredits? hit = null;

            foreach (var c in result.Value)
            {
                var tag = (c.Id ?? "").TrimEnd(':');
                // tag 至少 8 位才当前缀用（防止过短的 tag 误匹配多个号）
                if (tag.Length >= 8 && uid.StartsWith(tag, StringComparison.Ordinal))
                {
                    hit = c;
                    break;
                }
            }

            row.ApplyCredits(hit, now);
        }

        // ★ 汇总池子总积分（只算成功拉到的号）。
        var withCredits = _all.Where(r => r.HasCredits && r.CreditRemainingRaw is not null).ToList();
        var sum = withCredits.Sum(r => r.CreditRemainingRaw ?? 0);

        PoolCreditTotal = withCredits.Count > 0
            ? AccountPresentation.FormatCredits(sum)
            : "";
        PoolCreditDetail = _all.Count > 0
            ? $"{withCredits.Count}/{_all.Count} 个号有积分"
            : "";
    }

    private void ApplyFilter()
    {
        var q = Filter.Trim();

        var matched = string.IsNullOrEmpty(q)
            ? _all
            : [.. _all.Where(r =>
                r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.UidShort.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Domain.Contains(q, StringComparison.OrdinalIgnoreCase))];

        Rows.Clear();
        foreach (var r in matched) Rows.Add(r);

        if (Selected is not null && !Rows.Contains(Selected))
        {
            Selected = null;
            RenameText = "";
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    public void Select(AccountRowViewModel? row)
    {
        Selected = row;   // OnSelectedChanged 会处理 RenameText 和用量明细
        ClearNotice();
    }

    /// <summary>
    /// 拉用量明细（`/usage/recent`），供「每个号的积分消耗」用。
    ///
    /// 失败（端点没接/网关没起）静默 —— 这块只是详情面板的补充，
    /// 不挡账号列表。
    /// </summary>
    private async Task LoadUsageAsync(CancellationToken ct)
    {
        var result = await _gateway.GetUsageRecentAsync(2000, ct).ConfigureAwait(true);

        if (!result.Ok || result.Value is null)
        {
            _usage = [];
        }
        else
        {
            _usage = result.Value;
        }

        RebuildSelectedUsage();
    }

    /// <summary>
    /// 按当前选中的账号过滤用量明细，展示它的每笔积分消耗。
    ///
    /// 匹配用 uid 前缀（与积分挂载同一套逻辑）：账本里的 account 是
    /// 脱敏短标签（uid 前 8 位），账号行的 Uid 是完整 uuid。
    /// </summary>
    private void RebuildSelectedUsage()
    {
        SelectedUsage.Clear();
        SelectedCreditSpent = "";
        SelectedUsageSummary = "";
        HasSelectedUsage = false;

        if (Selected is null) return;

        var uid = Selected.Uid ?? "";
        var head = uid.Length <= 8 ? uid : uid[..8];

        var mine = _usage
            .Where(u => !string.IsNullOrEmpty(u.Account)
                        && (u.Account.StartsWith(head, StringComparison.Ordinal)
                            || head.StartsWith(u.Account, StringComparison.Ordinal)))
            .OrderByDescending(u => u.Ts)
            .Take(50)
            .ToList();

        foreach (var u in mine)
        {
            SelectedUsage.Add(new UsageRowViewModel(u));
        }

        if (mine.Count > 0)
        {
            var spent = mine.Where(u => u.Credit is not null).Sum(u => u.Credit ?? 0);
            var missing = mine.Count(u => u.Credit is null);

            SelectedCreditSpent = AccountPresentation.FormatCredits(spent);
            SelectedUsageSummary = missing > 0
                ? $"最近 {mine.Count} 次请求（{missing} 次无扣费数据）"
                : $"最近 {mine.Count} 次请求";
            HasSelectedUsage = true;
        }
    }

    [RelayCommand]
    public void ToggleImportBox()
    {
        ShowImportBox = !ShowImportBox;
        if (!ShowImportBox) ImportPayload = "";
    }

    /// <summary>把一段登录文件内容交给网关导入。</summary>
    [RelayCommand]
    public async Task ImportAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ImportPayload))
        {
            SetNotice("先把登录文件的内容贴进来。", isError: true);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _gateway.ImportAccountsAsync(ImportPayload, ct).ConfigureAwait(true);
            if (result.Ok)
            {
                SetNotice($"导入完成，新增 {result.Value} 个账号。", isError: false);
                ImportPayload = "";
                ShowImportBox = false;
            }
            else
            {
                SetNotice(result.Error ?? "导入失败", isError: true);
            }

            await RefreshAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ══════════════════════════════════════════════════════════
    // 从旧版导入（t81）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 「从文件导入」—— 弹文件选择器，选旧版导出的账号 JSON。
    ///
    /// ## 两段式：先解析 → 确认 → 再导入
    ///
    /// 用户选完文件后**先不导入**，而是显示「将导入 N 个账号，跳过 M 个」，
    /// 等他点「确认」才真发。理由：
    ///   · 选错文件是常事（旧版目录里还有 `auth.cn.json`）
    ///   · 导入是**不可逆的写操作**（会往保险库里加账号）
    ///   · 有跳过项时用户需要先看到原因，而不是事后发现少了几个
    /// </summary>
    [RelayCommand]
    public void PickImportFile()
    {
        if (_picker is null)
        {
            SetNotice("这个环境不支持文件选择（请在桌面应用里使用）。", isError: true);
            return;
        }

        var path = _picker.PickJsonFile("选择旧版导出的账号文件");

        // ⚠ null = 用户点了取消 —— **正常操作，不该报错**。
        //   报错的话用户每次取消都会被红字打扰。
        if (path is null) return;

        PrepareImport(AccountImport.FromFile(path));
    }

    /// <summary>
    /// 「导入旧版账号」—— 一键读 `~/.workbuddy-api/accounts.cn.json`。
    ///
    /// 与 <see cref="PickImportFile"/> 共用两段式确认流程，
    /// 只是文件来源不同。
    /// </summary>
    [RelayCommand]
    public void ImportFromLegacy()
    {
        PrepareImport(AccountImport.FromLegacyDirectory());
    }

    /// <summary>把解析结果摆到界面上等确认（或直接报错）。</summary>
    private void PrepareImport(AccountImport.Result result)
    {
        if (!result.Ok)
        {
            // 失败要说清是什么问题（文件不存在 / 格式不对 / 一条可用都没有），
            // 而不是一句「导入失败」
            SetNotice(result.Message, isError: true);

            PendingPayload = "";
            PendingSummary = "";
            return;
        }

        // 成功的解析结果先存着 —— 用户点「确认」时用的是它。
        // ⚠ 存**载荷**而不是重解析一次：重解析可能读到「文件已经被改了」
        //   的另一个版本，确认框上写 N 个、实际导入 M 个。
        PendingPayload = result.ToPayload();

        // ⚠ 文案由 AccountImport 生成，**不在这里拼** ——
        //   VM 用了 [ObservableProperty]/[RelayCommand]，源生成器不为
        //   链接进测试程序集的文件跑，所以这里的任何逻辑都测不到。
        //   放 Services 里那条路径是被 45+ 条测试覆盖的。
        PendingSummary = result.ConfirmationText();
        SetNotice("", isError: false);
    }

    /// <summary>确认导入（用户在确认框上点了「导入」）。</summary>
    [RelayCommand]
    public async Task ConfirmImportAsync(CancellationToken ct = default)
    {
        var payload = PendingPayload;
        CancelImport();

        if (string.IsNullOrEmpty(payload))
        {
            SetNotice("没有待导入的内容。", isError: true);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _gateway.ImportAccountsAsync(payload, ct).ConfigureAwait(true);
            SetNotice(
                result.Ok ? $"导入完成，新增 {result.Value} 个账号。" : (result.Error ?? "导入失败"),
                isError: !result.Ok);

            await RefreshAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>取消确认框。**不报错** —— 取消是正常操作。</summary>
    [RelayCommand]
    public void CancelImport()
    {
        PendingPayload = "";
        PendingSummary = "";
    }

    /// <summary>把网关当前使用的号切成选中的这个。</summary>
    [RelayCommand]
    public async Task ActivateAsync(CancellationToken ct = default)
    {
        if (Selected is null) return;

        IsBusy = true;
        try
        {
            var result = await _gateway.ActivateAccountAsync(Selected.Id, ct).ConfigureAwait(true);
            SetNotice(
                result.Ok ? $"已切到「{Selected.DisplayName}」。" : result.Error ?? "切换失败",
                isError: !result.Ok);

            await RefreshAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RenameAsync(CancellationToken ct = default)
    {
        if (Selected is null) return;
        if (string.IsNullOrWhiteSpace(RenameText))
        {
            SetNotice("标签不能是空的。", isError: true);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _gateway
                .RenameAccountAsync(Selected.Id, RenameText.Trim(), ct)
                .ConfigureAwait(true);
            SetNotice(
                result.Ok ? "标签已改。" : result.Error ?? "改名失败",
                isError: !result.Ok);

            await RefreshAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task RemoveAsync(CancellationToken ct = default)
    {
        if (Selected is null) return;
        var name = Selected.DisplayName;

        IsBusy = true;
        try
        {
            var result = await _gateway.RemoveAccountAsync(Selected.Id, ct).ConfigureAwait(true);
            SetNotice(
                result.Ok ? $"已删除「{name}」。" : result.Error ?? "删除失败",
                isError: !result.Ok);

            Selected = null;
            await RefreshAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>手动续期 token。上游不稳定时用户会一直点这个。</summary>
    [RelayCommand]
    public async Task RefreshTokenAsync(CancellationToken ct = default)
    {
        if (Selected is null) return;

        IsBusy = true;
        try
        {
            var result = await _gateway.RefreshTokenAsync(Selected.Id, ct).ConfigureAwait(true);
            SetNotice(
                result.Ok ? $"「{Selected.DisplayName}」的 token 已续期。" : result.Error ?? "续期失败",
                isError: !result.Ok);

            await RefreshAsync(ct).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 一键签到全部账号 —— 走后台任务（不阻塞 UI）。
    ///
    /// 触发后立刻返回，进度在右侧任务库看。
    /// </summary>
    [RelayCommand]
    public async Task CheckinAllAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var result = await _gateway.StartTaskAsync("checkin-all", ct).ConfigureAwait(true);

            if (result.Ok)
            {
                SetNotice("签到任务已开始，进度在右侧任务库查看。", isError: false);
            }
            else
            {
                SetNotice(result.Error ?? "签到启动失败", isError: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 手动触发「刷新全部积分」后台任务（不阻塞 UI）。
    ///
    /// 账号页的普通「刷新」走 cached 秒读；这个按钮是**强制重新拉上游**，
    /// 把最新积分写进数据库。进度在右侧任务库看。
    /// </summary>
    [RelayCommand]
    public async Task RefreshCreditsAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var result = await _gateway.StartTaskAsync("refresh-credits", ct).ConfigureAwait(true);

            if (result.Ok)
            {
                SetNotice("积分刷新任务已开始，进度在右侧任务库查看。", isError: false);
            }
            else
            {
                SetNotice(result.Error ?? "刷新启动失败", isError: true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetNotice(string text, bool isError)
    {
        Notice = text;
        NoticeIsError = isError;
    }

    private void ClearNotice()
    {
        Notice = "";
        NoticeIsError = false;
    }

    // ══════════════════════════════════════════════════════════
    // 导出（用户实测反馈「留一个导入账号和导出账号就行」）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 导出全部账号到加密文件。
    ///
    /// ## 为什么必须给口令、且**不保存它**
    ///
    /// 导出文件是加密的，口令是唯一钥匙：丢了就永久打不开。
    /// 所以：
    ///   · 界面**不缓存**口令（用完即弃，属性上不留）
    ///   · 不写进日志（`ExportAccountsAsync` 那条路径也不记）
    ///
    /// ## 口令从哪来
    ///
    /// 用 `ExportPassphrase` 这个绑定属性（界面上一个密码框）。
    /// 不用弹窗问 —— 弹窗会把"输入口令"和"导出进行中"割成两段状态，
    /// 出错时用户不知道是口令的问题还是导出的问题。
    /// </summary>
    [RelayCommand]
    public async Task ExportAsync(CancellationToken ct)
    {
        if (_picker is null)
        {
            SetNotice("这个环境不支持文件选择（请在桌面应用里使用）。", isError: true);
            return;
        }

        var passphrase = ExportPassphrase ?? "";
        if (passphrase.Length < AccountTransfer.MinPassphraseLength)
        {
            SetNotice($"导出文件是加密的 —— 口令至少要 {AccountTransfer.MinPassphraseLength} 个字符。", isError: true);
            return;
        }

        // 文件名带日期：用户会导出很多次，同名会互相覆盖。
        var suggested = $"buddydeck-accounts-{DateTimeOffset.Now:yyyy-MM-dd}.json";
        var path = _picker.PickSaveFile("导出账号到…", suggested);

        // null = 用户取消 —— **正常操作，不是错误**（与导入同一条约定）。
        if (path is null) return;

        IsBusy = true;
        ClearNotice();
        try
        {
            var result = await _gateway.ExportAccountsAsync(path, passphrase, ct).ConfigureAwait(true);

            if (result.Ok)
            {
                // ⚠ 提示里**带上路径** —— 用户下一步就是去拿那个文件，
                //   只说"导出成功"他还得自己找。
                SetNotice($"已导出 {result.Value} 个账号到 {path}", isError: false);
            }
            else
            {
                SetNotice(result.Error ?? "导出失败", isError: true);
            }
        }
        finally
        {
            IsBusy = false;

            // ⚠ **用完立刻清掉口令**，不留在属性上。
            //   属性值会进内存 dump、也可能被后续的 UI 绑定读走。
            ExportPassphrase = "";
        }
    }

    /// <summary>
    /// 导出用的口令（界面上一个密码框）。
    ///
    /// ⚠ 它**只在一次导出之内**有效 —— `ExportAsync` 结束就清空。
    ///   这不是"设置项"，不要持久化、不要进配置。
    /// </summary>
    [ObservableProperty]
    private string _exportPassphrase = "";

    /// <summary>导出面板是否展开。</summary>
    [ObservableProperty]
    private bool _showExportBox;

    /// <summary>展开/收起导出面板。</summary>
    [RelayCommand]
    public void ToggleExportBox()
    {
        ShowExportBox = !ShowExportBox;
        if (ShowExportBox) ShowImportBox = false;   // 导入/导出不同时展开
        ClearNotice();
    }
}

/// <summary>
/// 模型健康矩阵里的一行（界面用）。
///
/// 纯数据 + 构造时算好显示串 —— 与 `AccountRowViewModel` 同一策略：
/// 这些值不随时间变（下次刷新重算），不做倒计时。
/// </summary>
public sealed class ModelHealthRow
{
    public ModelHealthRow(ModelHealth m, DateTimeOffset now)
    {
        Model = m.Model;
        CoolingNow = m.CoolingNow;
        UsableCount = m.UsableCount;
        Total = m.Total;
        CoolingNames = m.Cooling.Count > 0 ? string.Join("、", m.Cooling.Take(5)) : "";
        CoolingMore = m.Cooling.Count > 5 ? $"…等 {m.Cooling.Count} 个号" : "";
        FallbackCount = m.FallbackCount;

        // ── 那句话怎么说（用户要的是"哪些模型限流了、啥时候解除"）──
        //
        // ⚠ 分三种情形，因为**用户的下一步动作不同**：
        //   · 此刻在冷却  → 他会等，所以要给**具体时刻**
        //   · 只限过、已恢复 → 不用等，但要知道"这个模型不太稳"
        //   · 只是被降级过  → 他可能根本没察觉，提一句
        if (m.CoolingNow && m.LastResetAtMs > 0)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(m.LastResetAtMs).ToLocalTime();
            var wait = at - now;

            StatusText = wait > TimeSpan.Zero
                ? $"{m.Cooling.Count} 个号限流中，约 {FormatWait(wait)} 后恢复（{at:HH:mm}）"
                : $"{m.Cooling.Count} 个号限流中";
        }
        else if (m.RateLimitedAccounts > 0)
        {
            StatusText = m.RecoveredAccounts >= m.RateLimitedAccounts
                ? $"{m.RateLimitedAccounts} 个号曾限流，已恢复"
                : $"{m.RateLimitedAccounts} 个号曾限流（{m.RecoveredAccounts} 个已恢复）";
        }
        else
        {
            StatusText = $"最近 {m.FallbackCount} 次请求被换成了别的模型";
        }

        // 可用情况单独一句 —— 它是"现在能不能用"的唯一直接答案
        AvailabilityText = $"{m.UsableCount}/{m.Total} 个号可用";
    }

    public string Model { get; }
    public bool CoolingNow { get; }
    public int UsableCount { get; }
    public int Total { get; }
    public string CoolingNames { get; }
    public string CoolingMore { get; }
    public int FallbackCount { get; }
    public string StatusText { get; }
    public string AvailabilityText { get; }

    /// <summary>此刻就不能用（醒目色）—— 与"曾限流但已恢复"分开配色。</summary>
    public bool IsBlocked => CoolingNow;

    private static string FormatWait(TimeSpan wait)
    {
        if (wait.TotalMinutes < 1) return $"{Math.Ceiling(wait.TotalSeconds):F0} 秒";
        if (wait.TotalHours < 1) return $"{Math.Ceiling(wait.TotalMinutes):F0} 分钟";
        return $"{Math.Ceiling(wait.TotalHours):F0} 小时";
    }
}

/// <summary>
/// 一个账号的一次请求消耗（详情面板里的一行）。
///
/// 纯数据 + 构造时算好显示串 —— 与 <see cref="AccountRowViewModel"/> 同一策略：
/// 不随时间变，不需要通知。
/// </summary>
public sealed class UsageRowViewModel
{
    public UsageRowViewModel(UsageRecord record)
    {
        Time = record.Ts > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(record.Ts).ToLocalTime().ToString("MM-dd HH:mm")
            : "—";
        Model = string.IsNullOrWhiteSpace(record.UsedModel) ? record.Model : record.UsedModel;
        Outcome = record.Outcome switch
        {
            "ok" => "成功",
            "failed" => "失败",
            "cancelled" => "取消",
            _ => record.Outcome,
        };
        Credit = record.Credit is { } c
            ? AccountPresentation.FormatCredits(c)
            : "—";
        Tokens = record.TotalTokens is { } t
            ? t.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
            : "—";
    }

    public string Time { get; }
    public string Model { get; }
    public string Outcome { get; }
    public string Credit { get; }
    public string Tokens { get; }

    /// <summary>失败的那行用醒目色。</summary>
    public bool IsFailure => Outcome == "失败";
}
