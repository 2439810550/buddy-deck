using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace WorkBuddyPanel.ViewModels;

/// <summary>
/// 账号列表的筛选 / 排序 / 搜索。
///
/// ## 为什么需要
///
/// 账号一多（十几二十个），列表就变成一条长长的滚动条 ——
/// 想找「哪个号快过期了」「哪个号今天还没签」得一个个往下翻。
///
/// ## 做法：CollectionView
///
/// 用 WPF 自带的 `ICollectionView` 包在 ObservableCollection 外面。
/// 它有几个好处：
///
///   - **不改数据源**：筛选只是视图过滤，`Accounts` 本身不变，
///     增删改照常走原来的逻辑
///   - **和虚拟化兼容**：ListBox 绑到 View 上照样只渲染可见行
///   - **零成本**：不用手动维护"筛选后的列表"两份数据
///
/// ## 筛选维度的选择
///
/// 只放**真的会用来找东西**的四个：
///
///   全部       —— 默认
///   需要续期   —— refresh token 快到期（< 45 天），这是最要紧的
///   今天没签   —— 漏签会断连续天数
///   有问题     —— 有错误、或者读不到状态
///
/// 不加"按积分排序"之类的 —— 那属于"看着好玩"，
/// 但真要找问题的号，上面四个才是入口。
/// </summary>
public sealed partial class MainViewModel
{
    private ICollectionView? _accountsView;

    /// <summary>给 ListBox 绑的那个视图（带筛选）。</summary>
    public ICollectionView AccountsView => _accountsView ??= CreateAccountsView();

    private ICollectionView CreateAccountsView()
    {
        var view = CollectionViewSource.GetDefaultView(Accounts);
        view.Filter = FilterAccount;

        // 排序：使用中的号永远排第一，其余按名字。
        // 用 SortDescription 而不是自己拍 —— 增删项时视图会自动维持顺序。
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(
            nameof(AccountRowVm.IsActive), ListSortDirection.Descending));
        view.SortDescriptions.Add(new SortDescription(
            nameof(AccountRowVm.Display), ListSortDirection.Ascending));

        return view;
    }

    // ── 筛选状态 ──────────────────────────────────────────────

    private string _accountFilter = AllFilter;
    private string _accountSearch = "";

    public const string AllFilter = "all";
    public const string NeedsRenewFilter = "renew";
    public const string NotCheckedInFilter = "checkin";
    public const string ProblemFilter = "problem";

    /// <summary>当前筛选。改它就会重算列表。</summary>
    public string AccountFilter
    {
        get => _accountFilter;
        set
        {
            if (!Set(ref _accountFilter, value)) return;
            RefreshAccountView();
            Raise(nameof(IsAllFilter));
            Raise(nameof(IsNeedsRenewFilter));
            Raise(nameof(IsNotCheckedInFilter));
            Raise(nameof(IsProblemFilter));
        }
    }

    // 给 RadioButton 用的四个一次性属性（WPF 绑定 bool 比绑字符串省事）
    public bool IsAllFilter
    {
        get => _accountFilter == AllFilter;
        set { if (value) AccountFilter = AllFilter; }
    }

    public bool IsNeedsRenewFilter
    {
        get => _accountFilter == NeedsRenewFilter;
        set { if (value) AccountFilter = NeedsRenewFilter; }
    }

    public bool IsNotCheckedInFilter
    {
        get => _accountFilter == NotCheckedInFilter;
        set { if (value) AccountFilter = NotCheckedInFilter; }
    }

    public bool IsProblemFilter
    {
        get => _accountFilter == ProblemFilter;
        set { if (value) AccountFilter = ProblemFilter; }
    }

    /// <summary>搜索关键字（匹配名字、UID、标签）。</summary>
    public string AccountSearch
    {
        get => _accountSearch;
        set
        {
            if (!Set(ref _accountSearch, value ?? "")) return;
            RefreshAccountView();
        }
    }

    /// <summary>
    /// 重算筛选。数据变了（刷新、续期、签到）之后要调一次 ——
    /// 否则「今天没签」这个筛选会停留在旧结果上。
    /// </summary>
    public void RefreshAccountView()
    {
        _accountsView?.Refresh();

        // 筛选结果的统计文字（"3 / 14 个"）
        var total = Accounts.Count;
        var shown = AccountsView.Cast<object>().Count();

        AccountFilterSummary = shown == total
            ? $"共 {total} 个账号"
            : $"显示 {shown} / {total} 个";

        Raise(nameof(HasVisibleAccounts));
        Raise(nameof(AccountFilterSummary));
    }

    private string _accountFilterSummary = "";
    public string AccountFilterSummary
    {
        get => _accountFilterSummary;
        private set => Set(ref _accountFilterSummary, value);
    }

    /// <summary>筛选后有没有内容（空的时候界面要显示"没有匹配的账号"）。</summary>
    public bool HasVisibleAccounts => Accounts.Count == 0 || AccountsView.Cast<object>().Any();

    /// <summary>
    /// 一个账号行该不该显示。
    ///
    /// 两个条件都要满足：先过搜索，再过筛选。
    /// </summary>
    private bool FilterAccount(object item)
    {
        if (item is not AccountRowVm account) return false;

        // ── 搜索 ──
        if (_accountSearch.Length > 0)
        {
            var q = _accountSearch.Trim();
            if (q.Length > 0)
            {
                var haystack = $"{account.Display} {account.UidShort}";
                if (!haystack.Contains(q, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        // ── 筛选 ──
        return _accountFilter switch
        {
            AllFilter => true,

            // 需要续期：refresh token 剩余天数不足（NeedsRenew 的定义是 < 45 天）
            NeedsRenewFilter => account.NeedsRenew,

            // 今天没签：签到状态里没有"今天已签"
            NotCheckedInFilter => account.NeedsCheckinToday,

            // 有问题：读不到数据、或者有错误
            ProblemFilter => account.HasError || account.HasStatusProblem,

            _ => true,
        };
    }

    // ── 批量操作（吸顶那条）────────────────────────────────────

    /// <summary>对**当前筛选出来的**账号做操作。</summary>
    private List<AccountRowVm> VisibleAccounts() =>
        AccountsView.Cast<AccountRowVm>().ToList();

    public bool CanOperateVisible => VisibleAccounts().Count > 0;
}
