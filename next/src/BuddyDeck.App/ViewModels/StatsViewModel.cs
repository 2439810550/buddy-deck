using System.Collections.ObjectModel;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.ViewModels;

/// <summary>
/// 用量统计页。
///
/// ## 三个维度，一个来源
///
/// 请求数 / Token 消耗 / 积分消耗**全部来自账本明细**（`/usage/recent`）——
/// 一次拉取，按时间范围在本地聚合。
///
/// **为什么不分别取**：
///   · `/metrics` 是**全局累计**且只有请求维度（没有 token/credit 合计）
///   · 分两个来源去拼「同一个时间范围里的三个数」必然对不上
///     （两个响应之间会有新请求进来）
/// 所以**单来源、单次取、本地切范围** —— 用户切范围时也不重新请求。
///
/// ## 逻辑在哪
///
/// 聚合、格式化、空态文案全在 <see cref="StatsPresentation"/> 里 ——
/// 那是纯函数、能被 37 条测试直接打。
///
/// **这个类只做绑定 glue**：属性 + 命令 + 把数据递过去。
/// 原因不是洁癖：它用了 `[ObservableProperty]` / `[RelayCommand]`，
/// 而 **CommunityToolkit 的源生成器不会为 `<Compile Include>` 链接进
/// 测试程序集的文件运行** —— 写在这里的逻辑**测不到**。
/// 本项目已实测确认多次。
///
/// ## ⚠ 为什么用 `using BuddyDeck.App.Services` 而不是 `App.Services.…`
///
/// 在 `BuddyDeck.App.ViewModels` 命名空间内部，标识符 `App` 会被解析成
/// **命名空间 `BuddyDeck.App`**（C# 的命名空间简写），而不是那个
/// `partial class App` —— 于是 `App.Services` 报 CS0426。
/// （`Pages/*.xaml.cs` 里同样的写法能编译，是因为那些文件的命名空间层级不同。）
///
/// **更根本的一条**：ViewModel **不该去拿容器**。本项目所有既有 ViewModel
/// 都是构造函数注入，容器访问只出现在视图层（`Pages/*.xaml.cs`）与组装层
/// （`App.xaml.cs`）。ViewModel 一旦依赖容器，就**没法在测试里 new 出来** ——
/// 而「能被链接进测试项目直接测」正是这个项目最值钱的性质之一。
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;

    /// <summary>全部账本明细（取一次，切范围时不重新请求）。</summary>
    private IReadOnlyList<StatsPresentation.ILedgerRow> _rows = [];

    public StatsViewModel(IGatewayClient gateway) => _gateway = gateway;

    // ── 时间范围 ──────────────────────────────────────────────

    /// <summary>可选的四个范围（界面上的切换按钮）。</summary>
    public ObservableCollection<RangeOption> Ranges { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeCaption))]
    private StatsPresentation.Range _selectedRange = StatsPresentation.Range.Today;

    /// <summary>当前范围的说明（「今天 00:00 起（10-15 00:00）」）。</summary>
    public string RangeCaption => StatsPresentation.RangeCaption(SelectedRange, DateTimeOffset.Now);

    /// <summary>一个可点的范围选项。</summary>
    public sealed partial class RangeOption : ObservableObject
    {
        private readonly Action<StatsPresentation.Range> _select;

        public RangeOption(StatsPresentation.Range range, Action<StatsPresentation.Range> select)
        {
            Range = range;
            _select = select;
            SelectCommand = new RelayCommand(() => _select(Range));
        }

        public StatsPresentation.Range Range { get; }

        /// <summary>点它切到这个范围。</summary>
        public IRelayCommand SelectCommand { get; }

        /// <summary>界面上的按钮文字（今天 / 近 7 天 / …）。</summary>
        public string Label => StatsPresentation.Label(Range);

        /// <summary>是不是当前选中的那个 —— 选中的按钮高亮。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Appearance))]
        private bool _isActive;

        /// <summary>
        /// 按钮外观：选中的用 Primary（实心），未选中的用 Transparent。
        ///
        /// 直接给 WPF-UI 的 <c>ControlAppearance</c> 值 —— 这样 XAML 里
        /// 写 <c>Appearance="{Binding Appearance}"</c> 就行，不需要一个
        /// 「bool → 枚举」的转换器（少一个转换器就少一处能静默失效的地方）。
        /// </summary>
        public Wpf.Ui.Controls.ControlAppearance Appearance
            => IsActive ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Transparent;
    }

    // ── 三个维度（界面直接绑这几个）────────────────────────────

    /// <summary>请求数（含失败与取消）。</summary>
    [ObservableProperty]
    private string _requestsText = "0";

    /// <summary>请求数的副标题（「成功 12 · 失败 2」）。</summary>
    [ObservableProperty]
    private string _requestsDetail = "";

    /// <summary>Token 消耗（已格式化，如「128.5 万」）。</summary>
    [ObservableProperty]
    private string _tokensText = "0";

    /// <summary>Token 的副标题（「输入 1.2 万 · 输出 3,400」）。</summary>
    [ObservableProperty]
    private string _tokensDetail = "";

    /// <summary>积分消耗。</summary>
    [ObservableProperty]
    private string _creditText = "0";

    /// <summary>积分的副标题（「上游实际扣费」）。</summary>
    [ObservableProperty]
    private string _creditDetail = "";

    /// <summary>成功率（`—` 表示这段时间没有请求）。</summary>
    [ObservableProperty]
    private string _successRateText = "—";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>出错时的提示（成功时为空）。</summary>
    [ObservableProperty]
    private string _error = "";

    // ── 空态 ──────────────────────────────────────────────────

    /// <summary>当前范围里没有数据（界面显示空态而不是三个 0）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasData))]
    private bool _isEmpty = true;

    /// <summary>空态文案（说清为什么空 + 怎么办）。</summary>
    [ObservableProperty]
    private string _emptyHint = "";

    /// <summary>有数据（界面显示数字而不是空态）。</summary>
    public bool HasData => !IsEmpty;

    /// <summary>账本里**总共**有没有记录（跨所有范围）—— 两种空态文案要用它区分。</summary>
    private bool _hasAnyRecords;

    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 从网关拉一次账本明细。
    ///
    /// ⚠ 取的是**尽可能多的**明细（上限 2000）：
    /// 因为要支持「近 30 天」的范围，取少了那个范围就是残缺的 ——
    /// 而残缺的统计**比没有统计更糟**（用户会拿它下结论）。
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        Error = "";

        try
        {
            var result = await FetchLedgerAsync(ct).ConfigureAwait(true);

            if (!result.Ok)
            {
                // ⚠ 网关没起（或数据源还没接）时**不是错误** ——
                //   页面显示空态 + 一句说明，而不是红字
                //   （和 Dashboard 的处理一致：离线是常态）。
                _rows = [];
                _hasAnyRecords = false;
                ApplyRange();
                return;
            }

            _rows = result.Value ?? [];
            _hasAnyRecords = _rows.Count > 0;
            ApplyRange();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>一次取多少条账本明细。</summary>
    private const int LedgerFetchLimit = 2000;

    /// <summary>
    /// 从网关拉账本明细（`/usage/recent`）。
    ///
    /// 取的是**尽可能多的**明细（上限 2000），因为要支持「近 30 天」范围。
    /// 返回值是 <see cref="UsageRecord"/>（实现 ILedgerRow），
    /// 聚合逻辑在 <see cref="StatsPresentation"/>。
    /// </summary>
    private async Task<GatewayResult<IReadOnlyList<StatsPresentation.ILedgerRow>>> FetchLedgerAsync(
        CancellationToken ct)
    {
        var result = await _gateway.GetUsageRecentAsync(LedgerFetchLimit, ct).ConfigureAwait(true);

        if (!result.Ok || result.Value is null)
        {
            return GatewayResult<IReadOnlyList<StatsPresentation.ILedgerRow>>.Failure(
                result.Error ?? "读不到用量明细");
        }

        IReadOnlyList<StatsPresentation.ILedgerRow> rows =
            result.Value.OfType<StatsPresentation.ILedgerRow>().ToArray();
        return GatewayResult<IReadOnlyList<StatsPresentation.ILedgerRow>>.Success(rows);
    }

    /// <summary>切范围 —— **不重新请求**，用已经拿到的那批数据重算。</summary>
    [RelayCommand]
    public void SelectRange(StatsPresentation.Range range)
    {
        SelectedRange = range;
        ApplyRange();
    }

    /// <summary>用当前 <see cref="SelectedRange"/> 重算三个维度并刷新界面。</summary>
    private void ApplyRange()
    {
        var totals = StatsPresentation.Aggregate(_rows, SelectedRange, DateTimeOffset.Now);

        RequestsText = totals.Requests.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        RequestsDetail = totals.Requests == 0
            ? ""
            : $"成功 {totals.Ok} · 失败 {totals.Failed}"
              + (totals.Cancelled > 0 ? $" · 取消 {totals.Cancelled}" : "");

        TokensText = StatsPresentation.FormatTokens(totals.TotalTokens);

        var inputOutput =
            $"输入 {StatsPresentation.FormatTokens(totals.PromptTokens)}"
            + $" · 输出 {StatsPresentation.FormatTokens(totals.CompletionTokens)}";

        // ⚠ 有记录缺 token 时如实说出来 —— 否则用户会拿一个偏低的合计
        //   当准数（那是上游没给 usage，不是真的只用了这么多）
        TokensDetail = totals.RowsMissingTokens > 0
            ? $"{inputOutput} · {totals.RowsMissingTokens} 条无用量数据"
            : inputOutput;

        CreditText = StatsPresentation.FormatCredit(totals.Credit);
        CreditDetail = totals.Requests == 0 ? "" : "上游实际扣费";

        SuccessRateText = StatsPresentation.PercentText(totals.SuccessRate);

        IsEmpty = totals.IsEmpty;
        EmptyHint = StatsPresentation.EmptyHint(SelectedRange, _hasAnyRecords);

        // 选中的按钮高亮（换范围时要全部重算一遍）
        foreach (var option in Ranges)
        {
            option.IsActive = option.Range == SelectedRange;
        }
    }

    /// <summary>
    /// 初始化范围列表。
    ///
    /// ⚠ 单独一个方法而不是构造函数里做：构造函数里做的话，
    /// `Ranges` 会在对象还没构造完时就被 XAML 读到（设计器/绑定时序问题）。
    /// 由页面在 <c>Loaded</c> 时调一次。
    /// </summary>
    public void InitializeRanges()
    {
        if (Ranges.Count > 0) return;

        foreach (var range in StatsPresentation.AllRanges)
        {
            Ranges.Add(new RangeOption(range, SelectRange));
        }

        ApplyRange();
    }
}
