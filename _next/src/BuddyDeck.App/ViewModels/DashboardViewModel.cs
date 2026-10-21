using System.Collections.ObjectModel;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.ViewModels;

/// <summary>
/// 总览页。
///
/// 顺序是刻意的：先回答「网关在不在」，再回答「有几个号能用」，
/// 最后才是「最近怎么样」。用户打开面板的第一个问题永远是第一个。
///
/// ---
///
/// ⚠ **安全提醒（给将来做「签到」功能的人，t52）**
///
/// 上游的签到响应里有一个 `action_button.action` 字段，值形如
/// `workbuddy://home?openBuddySwitcher=true`。**那是一个来自网络的、
/// 上游可控的 URL** —— 上游被入侵或被中间人改写时，它可以变成
/// `file:` / `ms-settings:` / `smb:` 等任何东西。
///
/// **要把它交给 `Process.Start` / `ShellExecute` 之前，必须先过
/// <see cref="AccountPresentation.IsSafeActionUrl"/>**（它只放行 `workbuddy://`）：
///
/// ```csharp
/// if (!AccountPresentation.IsSafeActionUrl(button.Action)) return;   // 不打开
/// Process.Start(new ProcessStartInfo(button.Action) { UseShellExecute = true });
/// ```
///
/// **为什么这条提醒放在这里**：本页最可能成为签到入口（它已经在展示账号状态）。
/// 写在最可能被改的地方，比写在一份没人翻的文档里有效。
///
/// **当前状态（2026-10-07）**：界面**尚未消费** `action_button` ——
/// 全 App 层搜 `ActionButton` / `Process.Start` / `ShellExecute` 均无命中。
/// 守卫已就位但没有调用方，这是**有意为之**：让接线那天有一个
/// 「拿起来就用」的安全路径，而不是让人在实现功能的同时顺手决定安全边界。
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private readonly AppSettings _settings;

    public DashboardViewModel(IGatewayClient gateway, AppSettings settings)
    {
        _gateway = gateway;
        _settings = settings;
    }

    // ---- 网关那一块 ----

    /// <summary>面板配置的网关 API key（总览页「可复制」那一行）。</summary>
    public string GatewayApiKey => _settings.ApiKey;

    /// <summary>网关在不在。界面上那个状态点用它上色。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionHeadline))]
    [NotifyPropertyChangedFor(nameof(ConnectionDetail))]
    private bool _online;

    /// <summary>
    /// 网关端口。
    ///
    /// ⚠ 必须带上 <see cref="Endpoint"/> 的通知：那个属性是从这个值算出来的
    /// （`http://127.0.0.1:{Port}`）。少了这一句，端口在刷新后变化时
    /// **地址那一行不会更新**，界面会一直显示构造时的默认端口 ——
    /// 而它看起来完全合理（8787 正是默认值），所以没人会怀疑它错了。
    /// 这是靠「对临时端口（8799 系）截图，图里却写 8787」发现的。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Endpoint))]
    private int _port = 8791;

    [ObservableProperty]
    private string _provider = "—";

    [ObservableProperty]
    private int _modelCount;

    /// <summary>代码指纹。空的时候界面显示「—」而不是空白，空白让人以为没读到。</summary>
    [ObservableProperty]
    private string _buildId = "—";

    /// <summary>磁盘上的代码和正在跑的是不是同一份。</summary>
    [ObservableProperty]
    private bool _stale;

    [ObservableProperty]
    private string _startedAt = "—";

    /// <summary>离线时的原因。在线时为空。</summary>
    [ObservableProperty]
    private string _offlineReason = "";

    /// <summary>网关的地址，带 <c>/v1</c> —— 抄给客户端直接能用。</summary>
    public string Endpoint => $"http://127.0.0.1:{Port}/v1";

    /// <summary>「网关已连接」/「网关未启动」—— 大字。</summary>
    public string ConnectionHeadline => Online ? "网关已连接" : "网关未启动";

    /// <summary>
    /// 大字下面那句。刻意分开写：在线时说在哪、跑的是什么；
    /// 离线时说怎么办。两种状态下这句话的职责不一样。
    /// </summary>
    public string ConnectionDetail => Online
        ? $"127.0.0.1:{Port} · {Provider} · {ModelCount} 个模型"
        : OfflineReason;

    // ---- 账号那一块 ----

    /// <summary>保险库里的号，按「越该管的排越前」排。</summary>
    public ObservableCollection<AccountRowViewModel> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountsHeadline))]
    [NotifyPropertyChangedFor(nameof(UsableCount))]
    private int _accountCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountsHeadline))]
    [NotifyPropertyChangedFor(nameof(UsableCount))]
    private int _healthyCount;

    public int UsableCount => HealthyCount;

    /// <summary>账号那块的一句话总结。</summary>
    public string AccountsHeadline => AccountPresentation.Summarize(AccountCount, HealthyCount);

    /// <summary>账号池子总积分（格式化，读数据库 cached）。空 = 没读到。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPoolCredit))]
    private string _poolCreditTotal = "";

    /// <summary>有积分可显示。</summary>
    public bool HasPoolCredit => PoolCreditTotal.Length > 0;

    /// <summary>积分覆盖的号数（「20/20 个号有积分」）。空 = 没读到。</summary>
    [ObservableProperty]
    private string _poolCreditDetail = "";

    // ---- 指标那一块 ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetrics))]
    private bool _metricsAvailable;

    public bool HasMetrics => MetricsAvailable;

    [ObservableProperty]
    private string _totalRequests = "0";

    [ObservableProperty]
    private string _httpSuccessRate = "—";

    [ObservableProperty]
    private string _completionRate = "—";

    [ObservableProperty]
    private string _averageLatency = "—";

    /// <summary>
    /// 「请求 N 次 / 上游尝试 M 次」—— 换号机制的体检指标。
    ///
    /// 空串表示没有数据（新装的网关），界面据此走空态。
    /// </summary>
    [ObservableProperty]
    private string _attemptRatio = "";

    /// <summary>
    /// 这个比值该怎么说（一句人话，不报警）。
    /// 发生过换号重试时才有提示语气 —— 见 <see cref="AccountPresentation.DescribeAttemptOverhead"/>。
    /// </summary>
    [ObservableProperty]
    private string _attemptNote = "";

    /// <summary>发生过换号重试（界面据此给这个数字加提示色）。</summary>
    [ObservableProperty]
    private bool _hasRetryOverhead;

    /// <summary>有没有拿到过尝试次数的数据 —— 旧版网关没有这个键，界面要能区分。</summary>
    [ObservableProperty]
    private bool _hasAttemptData;

    /// <summary>「平均耗时」那个数字的标签。语义变了，标签跟着变（见常量处的说明）。</summary>
    public string LatencyLabel => AccountPresentation.AverageLatencyLabel;

    /// <summary>最近请求（脱敏元数据）。</summary>
    public ObservableCollection<RecentRequestRow> RecentRequests { get; } = [];

    /// <summary>指标端点没实现/网关没起时的那句话。</summary>
    [ObservableProperty]
    private string _metricsNotice = "还没有请求记录。发一次聊天测试就能看到。";

    /// <summary>
    /// 「最近请求」区块头部那一行指标。
    ///
    /// 为什么是「一行字段」而不是四个独立格子：design 计划 1.8 节原则 6
    /// 明确不要「大数字英雄区」—— 那是 SKILL.md 点名的默认版面。
    /// 四个数字仍然一眼可见，只是不进英雄位。
    /// </summary>
    public ObservableCollection<MetricField> Metrics { get; } = [];

    /// <summary>按当前四个值重建指标行。</summary>
    private void RebuildMetrics()
    {
        Metrics.Clear();
        Metrics.Add(new MetricField("总请求", TotalRequests));
        Metrics.Add(new MetricField("HTTP 成功", HttpSuccessRate));
        Metrics.Add(new MetricField("完成率", CompletionRate));
        // 标签不是「平均耗时」—— 这个数含换号重试，是用户真实等待时长（t16 改了语义）
        Metrics.Add(new MetricField(LatencyLabel, AverageLatency));
    }

    // ---- 刷新 ----

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private string _lastRefreshedAt = "";

    /// <summary>
    /// 只探一眼网关并把结果写到自己的属性上。
    ///
    /// 和 <see cref="RefreshAsync"/> 分开：外壳启动时只要一个「在不在」，
    /// 顺手拉账号和指标是浪费（网关没起时那是两次必然失败的网络等待）。
    /// </summary>
    public async Task<GatewayHealth> ProbeAsync(CancellationToken ct = default)
    {
        var health = await _gateway.GetHealthAsync(ct).ConfigureAwait(true);

        Online = health.Online;
        Port = health.Port;
        OfflineReason = health.Error ?? "";
        if (health.Online)
        {
            Provider = string.IsNullOrWhiteSpace(health.Provider) ? "—" : health.Provider;
            ModelCount = health.Models;
            BuildId = string.IsNullOrWhiteSpace(health.BuildId) ? "—" : health.BuildId;
            Stale = health.Stale;
            StartedAt = health.StartedAt is { } t
                ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "—";
        }
        else
        {
            Provider = "—";
            ModelCount = 0;
            BuildId = "—";
            Stale = false;
            StartedAt = "—";
        }

        return health;
    }

    /// <summary>
    /// 拉一遍全部数据。
    ///
    /// 这里没有 try/catch —— 因为 <see cref="IGatewayClient"/> 的契约是**永不抛**。
    /// 网关没起时每个调用都返回带原因的失败结果，界面照样渲染。
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;

        try
        {
            await ProbeAsync(ct).ConfigureAwait(true);
            await LoadAccountsAsync(ct).ConfigureAwait(true);
            await LoadMetricsAsync(ct).ConfigureAwait(true);

            LastRefreshedAt = DateTimeOffset.Now.ToString("HH:mm:ss");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task LoadAccountsAsync(CancellationToken ct)
    {
        var accounts = await LoadAccountsForRailAsync(ct).ConfigureAwait(true);
        _latestAccounts = accounts;

        // 账号池子总积分（读数据库 cached 端点，秒读）。
        await LoadPoolCreditAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 读**全部 20 个号**的积分并汇总成池子总积分，填到账号卡片上。
    ///
    /// 数据源是数据库 cached 端点（<c>/admin/credits/cached</c>，秒读），
    /// 不是实时查上游 —— 上游的值由后台任务「刷新积分」写入数据库，
    /// 这里读的是最近一次刷新的快照。
    ///
    /// 为什么是「池子总积分」而不是「当前绑定账号」：
    /// 账号池是**并发**的（一个请求可能落到 20 个号里的任何一个），
    /// 不存在「当前绑定账号」这个概念 —— 那个是旧版单号架构的遗留。
    /// </summary>
    private async Task LoadPoolCreditAsync(CancellationToken ct)
    {
        var result = await _gateway.GetAllCreditsAsync(ct).ConfigureAwait(true);
        if (!result.Ok || result.Value is null || result.Value.Count == 0)
        {
            PoolCreditTotal = "";
            PoolCreditDetail = "";
            return;
        }

        // 只加「成功拉到积分」的号（带 error 的跳过，不算进去）。
        var ok = result.Value.Where(c => string.IsNullOrEmpty(c.Error)).ToList();
        var sum = ok.Sum(c => c.TotalRemaining);

        PoolCreditTotal = ok.Count > 0
            ? AccountPresentation.FormatCredits(sum)
            : "";
        PoolCreditDetail = ok.Count > 0
            ? $"{ok.Count}/{_latestAccounts.Count} 个号有积分"
            : "";
    }

    /// <summary>最近一次读到的账号原始数据 —— 外壳刷新状态轨时要用。</summary>
    private IReadOnlyList<AccountSummary> _latestAccounts = [];

    /// <summary>
    /// 读账号列表并把它们做成列表行，同时把原始数据交回去给外壳画状态轨。
    ///
    /// 为什么不重复请求：外壳的顶栏状态轨和总览页的账号摘要看的是同一份数据，
    /// 分两次拉会在网关抖动时出现两处不一致。
    /// </summary>
    public async Task<IReadOnlyList<AccountSummary>> LoadAccountsForRailAsync(CancellationToken ct = default)
    {
        var result = await _gateway.GetAccountsAsync(ct).ConfigureAwait(true);

        if (!result.Ok || result.Value is null)
        {
            Accounts.Clear();
            AccountCount = 0;
            HealthyCount = 0;
            _latestAccounts = [];
            return _latestAccounts;
        }

        var now = DateTimeOffset.UtcNow;
        var rows = result.Value
            .Select(a =>
            {
                var row = new AccountRowViewModel(a, now);
                // 冷却/冷冻的健康信息在构造之后套用 —— 见 ApplyHealth 的注释
                // （构造参数已经很长，再加两个没人读得懂）。
                row.ApplyHealth();
                return row;
            })
            .OrderBy(r => (int)r.State)
            .ToList();

        Accounts.Clear();
        foreach (var row in rows) Accounts.Add(row);

        AccountCount = rows.Count;
        HealthyCount = rows.Count(r => r.State is AccountState.Healthy or AccountState.ExpiringSoon);

        _latestAccounts = result.Value;
        return _latestAccounts;
    }

    private async Task LoadMetricsAsync(CancellationToken ct)
    {
        var result = await _gateway.GetMetricsAsync(ct).ConfigureAwait(true);

        RecentRequests.Clear();

        if (!result.Ok || result.Value is null || result.Value.IsEmpty)
        {
            MetricsAvailable = false;
            MetricsNotice = result.Error ?? "还没有请求记录。发一次聊天测试就能看到。";
            TotalRequests = "0";
            HttpSuccessRate = "—";
            CompletionRate = "—";
            AverageLatency = "—";
            // 这几个也必须清掉 —— 否则网关掉线后会留着上一次刷新读到的数字，
            // 界面显示的是"旧的但看起来正常"的体检指标，比空着更坏。
            AttemptRatio = "";
            AttemptNote = "";
            HasRetryOverhead = false;
            HasAttemptData = false;
            RebuildMetrics();
            return;
        }

        var m = result.Value;
        MetricsAvailable = true;
        MetricsNotice = "";
        TotalRequests = m.TotalRequests.ToString();
        HttpSuccessRate = AccountPresentation.FormatPercent(m.HttpSuccessRate);
        CompletionRate = AccountPresentation.FormatPercent(m.CompletionRate);
        AverageLatency = AccountPresentation.FormatLatency(m.AverageLatencyMs);

        // 换号体检指标。
        //
        // 旧版网关（没有 attemptCount 键）反序列化后 AttemptCount 是 0，
        // 而 TotalRequests 可能 > 0 —— 这时**不能**当作"零次尝试"来显示，
        // 那是把"读不到"误报成"上游一次没打"，比不显示更坏。
        // 所以判据是「attemptCount 是否真的有意义」，而不是「它是不是 0」：
        // 只要有请求就必然有尝试（attemptCount >= totalRequests），
        // 所以 totalRequests > 0 而 attemptCount == 0 只可能是**字段缺失**。
        var looksLikeMissingField = m.TotalRequests > 0 && m.AttemptCount == 0;

        HasAttemptData = !looksLikeMissingField && (m.TotalRequests > 0 || m.AttemptCount > 0);
        AttemptRatio = HasAttemptData
            ? AccountPresentation.FormatAttemptRatio(m.TotalRequests, m.AttemptCount)
            : "";
        HasRetryOverhead = HasAttemptData
            && AccountPresentation.HasRetryOverhead(m.TotalRequests, m.AttemptCount);
        AttemptNote = HasAttemptData
            ? AccountPresentation.DescribeAttemptOverhead(m.TotalRequests, m.AttemptCount)
            : "";

        RebuildMetrics();

        foreach (var r in m.Recent) RecentRequests.Add(new RecentRequestRow(r));
    }
}

/// <summary>指标行里的一个字段：微标签 + 等宽数字。</summary>
/// <param name="Label">上面那行微标签。</param>
/// <param name="Value">下面那个值。</param>
public sealed record MetricField(string Label, string Value);

/// <summary>总览页/账号页共用的一行账号。</summary>
public sealed partial class AccountRowViewModel : ObservableObject
{
    private readonly AccountSummary _account;

    public AccountRowViewModel(AccountSummary account, DateTimeOffset now, string? gatewayError = null)
    {
        _account = account;
        DisplayName = AccountPresentation.DisplayName(account);
        UidShort = AccountPresentation.Shorten(account.Uid, 12);
        Tenant = AccountPresentation.TenantLabel(account);
        Domain = string.IsNullOrWhiteSpace(account.Domain) ? "—" : account.Domain;
        Active = account.Active;
        State = AccountPresentation.DeriveState(account.ExpiresAtMs, now, gatewayError);
        StateLabel = AccountPresentation.StateLabel(State);
        Remaining = AccountPresentation.FormatRemaining(account.ExpiresAtMs, now);
        ExpiresAt = AccountPresentation.FormatTimestamp(account.ExpiresAtMs);
        RefreshExpiresAt = AccountPresentation.FormatTimestamp(account.RefreshExpiresAtMs ?? 0);

        // 进度条：从"入库到过期"没法算（没有 issue 时间），所以用「还剩多久」的
        // 量级来定宽度 —— 24 小时以内算快没了。
        RemainingRatio = account.ExpiresAtMs <= 0
            ? 1.0
            : Math.Clamp(
                (account.ExpiresAtMs - now.ToUnixTimeMilliseconds()) / (double)TimeSpan.FromDays(7).TotalMilliseconds,
                0, 1);
    }

    /// <summary>保险库里的稳定 id —— 切号/改名/删除都拿它。</summary>
    public string Id => _account.Id;

    /// <summary>
    /// 账号 uid（**不含 enterpriseId 后缀**）—— 用来与积分数据对上号。
    ///
    /// ## ⚠ 为什么积分匹配不能直接用 `Id`
    ///
    /// 实测踩过（用户反馈「账号看不到还有多少积分」）：
    /// <code>
    /// 账号列表回：Id          = 3e563042-d9da-4ce6-bbb8-bf52debdbff0:
    /// 积分端点回：accountTag  = 3e563042:          ← 短 uid，不是完整 uuid
    /// </code>
    /// 直接 `byId.GetValueOrDefault(row.Id)` **永远取不到** ⇒ 界面显示空态，
    /// 而且不报错（字典返回 null，整块静默不渲染）。
    ///
    /// ⇒ 用 uid 做匹配键：它是稳定的、两边都有。
    /// </summary>
    public string Uid => _account.Uid;

    public string DisplayName { get; }
    public string UidShort { get; }
    public string Tenant { get; }
    public string Domain { get; }
    public bool Active { get; }
    public AccountState State { get; }
    public string StateLabel { get; }
    public string Remaining { get; }
    public string ExpiresAt { get; }
    public string RefreshExpiresAt { get; }

    /// <summary>0–1，剩余时间的可视化比例。1 = 还很充裕。</summary>
    public double RemainingRatio { get; }

    // ========================================================================
    // 积分（t69，对标 workbuddy-switch 的账号卡）
    //
    // ## 为什么是可变的（而上面那些是只读的）
    //
    // 账号信息随 GetAccountsAsync 一次拿到，所以构造时就定死；
    // **积分是另一个调用**（GetCreditsAsync），而且可能：
    //   · 慢（要向上游逐个账号查）
    //   · 部分失败（某个号查不到，其它号正常）
    //   · 端点还没实现（t70 在做）
    //
    // 所以积分是**后填的**：先显示账号信息，积分到了再补上。
    // 这也让「账号信息先可见」—— 不让积分拖慢整个列表。
    // ========================================================================

    /// <summary>拿到积分数据了（没拿到时整块积分区不渲染 —— 不显示 "0 积分"）。</summary>
    [ObservableProperty]
    private bool _hasCredits;

    /// <summary>剩余积分大字（已格式化）。</summary>
    [ObservableProperty]
    private string _creditTotal = "—";

    /// <summary>
    /// 剩余积分的**原始数值**（未格式化）—— 汇总池子总积分用。
    /// 查失败/未查时为 null。
    /// </summary>
    public double? CreditRemainingRaw { get; private set; }

    /// <summary>「12 个积分包」。</summary>
    [ObservableProperty]
    private string _creditPackageCountText = "";

    /// <summary>「20:00 更新」—— 让用户知道这个数有多新。</summary>
    [ObservableProperty]
    private string _creditUpdatedText = "";

    /// <summary>
    /// 最近到期的积分包（**最多两条**，已按到期升序）。
    ///
    /// 为什么只显示两条：switch 的卡也只显示两个槽位 ——
    /// 再多会把卡片撑高，而用户真正要的是"最急的是哪个"。
    /// 剩下的用一行「还有 N 个」带过。
    /// </summary>
    public ObservableCollection<CreditPackageRow> TopCreditPackages { get; } = [];

    /// <summary>「还有 10 个积分包」。没有更多时为空（界面不渲染）。</summary>
    [ObservableProperty]
    private string _moreCreditText = "";

    /// <summary>
    /// 这个号有积分快要到期 —— 卡上那个琥珀 chip 用它。
    ///
    /// 注意它**不参与** <see cref="State"/>：账号状态（token 到期/降权）
    /// 和积分状态是两个维度，混起来会让"账号好好的但积分临期"这种情况
    /// 被当成账号故障。
    /// </summary>
    [ObservableProperty]
    private bool _creditAtRisk;

    /// <summary>
    /// 把积分数据填到这个行上（由 AccountsViewModel 在拉到数据后调用）。
    ///
    /// **失败时的处置**：<paramref name="credits"/> 为 null 或带 Error 时
    /// 保持 <see cref="HasCredits"/> = false —— 整块不显示。
    /// **绝不显示成 "0 积分"**：0 和"读不到"是两件事，
    /// 混起来会让用户以为积分真的没了。
    /// </summary>
    public void ApplyCredits(AccountCredits? credits, DateTimeOffset now)
    {
        if (credits is null || !string.IsNullOrEmpty(credits.Error))
        {
            HasCredits = false;
            CreditAtRisk = false;
            CreditRemainingRaw = null;
            TopCreditPackages.Clear();
            MoreCreditText = "";
            return;
        }

        HasCredits = true;

        // 余额优先用网关给的 totalRemaining；旧网关没这个字段（=0）时
        // 兜底用各包 remain 累加（网关已把 remain 语义换成有效剩余，见 CreditsEndpoints）。
        var remaining = credits.TotalRemaining > 0
            ? credits.TotalRemaining
            : credits.Packages.Sum(p => p.Remain);
        CreditTotal = AccountPresentation.FormatCredits(remaining);
        CreditRemainingRaw = remaining;

        var count = credits.Packages.Count;
        CreditPackageCountText = count switch
        {
            0 => "没有积分包",
            1 => "1 个积分包",
            _ => $"{count} 个积分包",
        };

        // ⚠ 网关**不回** `fetchedAt`（它只回积分数据本身）——
        //   所以"更新时间"用**本机时刻**：这次拉取就是这个时间发生的。
        //   显示"上次查到的时刻"在这里没有意义（我们每次都现拉）。
        CreditUpdatedText = $"{now.ToLocalTime():HH:mm} 更新";

        // 按到期升序（最快到期的在最前）—— 与 Core 的 ByExpiryAscending 同一取向：
        // 积分是先到期先用的，所以"下一次会过期多少"只有把最急的排前面才看得出来。
        var sorted = credits.Packages
            .Select(p => new CreditPackageRow(p, now))
            .OrderBy(r => r.SortKey)
            .ToList();

        TopCreditPackages.Clear();
        foreach (var row in sorted.Take(2)) TopCreditPackages.Add(row);

        MoreCreditText = sorted.Count > 2 ? $"还有 {sorted.Count - 2} 个积分包" : "";

        // 「有风险」= 至少一个包在阈值内到期（不含已过期 —— 那已经不是"即将"了）
        CreditAtRisk = sorted.Any(r => r.Expiry == AccountPresentation.CreditExpiryState.ExpiringSoon);
    }

    // ══════════════════════════════════════════════════════════
    //  模型冷却（用户实测反馈「每一个账号的模型冷却情况也没有」）
    // ══════════════════════════════════════════════════════════
    //
    // ## 为什么这三个属性在构造函数里就定死
    //
    // 它们**不随时间变化**（下次刷新账号列表时重算）—— 与 `Remaining` 同一个策略。
    // 如果做成每秒刷新的"倒计时"，就得起一个定时器，而那个定时器
    // 在窗口隐藏时还在跑，是纯粹的功耗与复杂度。
    //
    // ⚠ 代价：页面开着不动时，"还剩 3 分钟"会一直显示 3 分钟。
    //   这个取舍是有意的 —— 用户点一下刷新就看到真实值，
    //   而"实时跳动"的收益远小于它的成本。

    /// <summary>归纳后的冷却状态（数量 / 最快恢复 / 严重度）。</summary>
    public AccountPresentation.CooldownSummary Cooldowns { get; private set; }

    /// <summary>冷却的显示文本。没有冷却时是空串 ⇒ XAML 里整块不渲染。</summary>
    public string CooldownText { get; private set; } = "";

    /// <summary>有没有冷却 —— XAML 用它决定那一行显不显示。</summary>
    public bool HasCooldowns => Cooldowns.Count > 0;

    /// <summary>冷却明细（每个冷却模型一行：模型名 + 还剩多久）。</summary>
    public IReadOnlyList<AccountCooldownDetail> CooldownDetails { get; private set; } = [];

    /// <summary>冷却较久（&gt;5 分钟）时为 true —— 界面用醒目色，诱导用户换号。</summary>
    public bool CooldownsAreLong => Cooldowns.Level == AccountPresentation.CooldownLevel.Long;

    /// <summary>被冷冻（连续失败到阈值）。</summary>
    public bool Frozen => _account.Frozen;

    /// <summary>冷冻的解释文本（含失败次数）。**只在真冷冻时非空**。</summary>
    public string FrozenText { get; private set; } = "";

    /// <summary>
    /// 从账号摘要里读出冷却与冷冻。**由 `DashboardViewModel` 在构造行之后调用** ——
    /// 不在构造函数里做，是因为 `AccountSummary` 的冷却字段是 init-only，
    /// 构造参数已经很长（再加两个就没人读得懂了）。
    /// </summary>
    internal void ApplyHealth()
    {
        Cooldowns = AccountPresentation.SummarizeCooldowns(_account.Cooldowns);
        CooldownText = AccountPresentation.FormatCooldowns(Cooldowns);
        CooldownDetails = _account.Cooldowns
            .Select(c => new AccountCooldownDetail(c.Model, c.RemainSeconds))
            .ToList();
        FrozenText = _account.Frozen ? AccountPresentation.FormatFrozen(_account.FailureCount) : "";
    }
}

/// <summary>
/// 积分包在界面上的一行。
///
/// 纯数据 + 构造时算好显示串（不随状态变，所以不需要通知）。
/// </summary>
public sealed class CreditPackageRow
{
    public CreditPackageRow(CreditPackageInfo package, DateTimeOffset now)
    {
        Name = string.IsNullOrWhiteSpace(package.PackageName) ? "未命名积分包" : package.PackageName;
        Amount = $"{AccountPresentation.FormatCredits(package.Remain)} 积分";

        var expireAt = ParseTime(package.CycleEndTime);
        Expiry = AccountPresentation.DeriveCreditExpiry(expireAt, now);
        ExpiryText = AccountPresentation.FormatCreditExpiry(expireAt, now);

        // 排序键：解析不出时间的排**最后**（不是最前）——
        // 与 Core 的 ByExpiryAscending 一致。当成"最早到期"会制造假紧迫感。
        SortKey = expireAt ?? long.MaxValue;
    }

    public string Name { get; }
    public string Amount { get; }
    public string ExpiryText { get; }

    /// <summary>到期状态 —— 界面据此决定用不用醒目样式。</summary>
    public AccountPresentation.CreditExpiryState Expiry { get; }

    /// <summary>临期（3 天内）—— 界面上用琥珀标出来。</summary>
    public bool IsExpiring => Expiry == AccountPresentation.CreditExpiryState.ExpiringSoon;

    public long SortKey { get; }

    /// <summary>
    /// 解析 `cycleEndTime`。与 Core 那边的口径一致：解析不出就返回 null。
    ///
    /// 容忍两种格式（上游给过 ISO 也空格分隔的日期时间）。
    /// </summary>
    private static long? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
        {
            return parsed.ToUnixTimeMilliseconds();
        }

        return null;
    }
}

/// <summary>最近请求的一行。</summary>
public sealed class RecentRequestRow
{
    public RecentRequestRow(RecentRequest request)
    {
        Time = request.At == default
            ? "—"
            : request.At.ToLocalTime().ToString("HH:mm:ss");
        Model = string.IsNullOrWhiteSpace(request.Model) ? "—" : request.Model;
        Outcome = AccountPresentation.DescribeStatus(request.Status);
        Duration = AccountPresentation.FormatLatency(request.DurationMs);
        AccountTag = string.IsNullOrWhiteSpace(request.AccountTag) ? "—" : request.AccountTag;
        Mode = request.Streamed ? "流式" : "一次性";
    }

    public string Time { get; }
    public string Model { get; }
    public string Outcome { get; }
    public string Duration { get; }
    public string AccountTag { get; }
    public string Mode { get; }
}

/// <summary>账号的一条模型冷却明细（详情列里显示）。</summary>
public sealed record AccountCooldownDetail(string Model, long RemainSeconds)
{
    /// <summary>「还有 12 分钟」这种给人看的文案。</summary>
    public string RemainText => RemainSeconds switch
    {
        <= 0 => "已恢复",
        < 60 => $"{RemainSeconds} 秒",
        < 3600 => $"{RemainSeconds / 60} 分钟",
        _ => $"{RemainSeconds / 3600} 小时 {RemainSeconds % 3600 / 60} 分钟",
    };

    /// <summary>冷却较久（>5 分钟）用琥珀色提醒。</summary>
    public bool IsLong => RemainSeconds > 300;
}
