using System.Collections.ObjectModel;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.ViewModels;

/// <summary>
/// 账号池页（原「账号」+「接入池」合并）。
///
/// 层级导航：池 → 账号 → 详情。
///   · 左列：池列表（点池展开账号）
///   · 中列：选中池的账号列表（点账号展开详情）
///   · 右列：账号详情（积分、冷却、token、操作）
///
/// 这个 VM 是门面：持有 <see cref="PoolsViewModel"/> 和 <see cref="AccountsViewModel"/>
/// 两个子 VM，暴露层级导航状态。页面 XAML 直接绑这两个子 VM 的属性。
/// </summary>
public sealed partial class VaultViewModel : ObservableObject
{
    /// <summary>当前嵌套层级。</summary>
    public enum VaultLevel
    {
        /// <summary>池卡片网格。</summary>
        Pools,

        /// <summary>选中池的账号列表（表单 + 筛选 + 分页）。</summary>
        Accounts,

        /// <summary>选中账号的详情。</summary>
        Detail,
    }

    public PoolsViewModel Pools { get; }
    public AccountsViewModel Accounts { get; }

    /// <summary>当前层级。点池下沉到账号层，点账号下沉到详情层，返回上浮。</summary>
    [ObservableProperty]
    private VaultLevel _level = VaultLevel.Pools;

    /// <summary>是不是在池层（显示卡片网格）。</summary>
    public bool IsPoolsLevel => Level == VaultLevel.Pools;

    /// <summary>是不是在账号层（显示账号表单）。</summary>
    public bool IsAccountsLevel => Level == VaultLevel.Accounts;

    /// <summary>是不是在详情层（显示账号详情）。</summary>
    public bool IsDetailLevel => Level == VaultLevel.Detail;

    partial void OnLevelChanged(VaultLevel value)
    {
        OnPropertyChanged(nameof(IsPoolsLevel));
        OnPropertyChanged(nameof(IsAccountsLevel));
        OnPropertyChanged(nameof(IsDetailLevel));
    }

    /// <summary>返回上一级。</summary>
    [RelayCommand]
    public void GoBack()
    {
        Level = Level switch
        {
            VaultLevel.Detail => VaultLevel.Accounts,
            VaultLevel.Accounts => VaultLevel.Pools,
            _ => VaultLevel.Pools,
        };
    }

    /// <summary>点池 → 下沉到账号层。</summary>
    public void OpenPool(PoolRowViewModel? pool)
    {
        Pools.Select(pool);
        Level = VaultLevel.Accounts;
    }

    /// <summary>点账号 → 下沉到详情层。</summary>
    public void OpenAccount(AccountRowViewModel? account)
    {
        Accounts.Select(account);
        Level = VaultLevel.Detail;
    }

    public VaultViewModel(PoolsViewModel pools, AccountsViewModel accounts)
    {
        Pools = pools;
        Accounts = accounts;

        // 池选中变化 → 联动账号列表（点池看账号）
        pools.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PoolsViewModel.Selected))
            {
                OnPropertyChanged(nameof(SelectedPool));
                OnPropertyChanged(nameof(HasSelectedPool));
            }
        };

        // 账号选中变化 → 联动详情。
        accounts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountsViewModel.Selected))
            {
                OnPropertyChanged(nameof(SelectedAccount));
                OnPropertyChanged(nameof(HasSelectedAccount));
            }
        };
    }

    /// <summary>当前选中的池。</summary>
    public PoolRowViewModel? SelectedPool => Pools.Selected;

    /// <summary>有没有选中池（控制中间列的显隐）。</summary>
    public bool HasSelectedPool => Pools.Selected is not null;

    /// <summary>当前选中的账号（在选中池的账号列表里）。</summary>
    public AccountRowViewModel? SelectedAccount => Accounts.Selected;

    /// <summary>有没有选中账号（控制详情列的显隐）。</summary>
    public bool HasSelectedAccount => Accounts.Selected is not null;

    /// <summary>加载：拉池列表 + 默认池的账号。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await Pools.RefreshAsync(ct).ConfigureAwait(true);
        await Accounts.RefreshAsync(ct).ConfigureAwait(true);
    }
}
