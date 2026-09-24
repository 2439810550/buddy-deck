using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using WorkBuddyPanel.Models;
using WorkBuddyPanel.Services;

namespace WorkBuddyPanel.ViewModels;

/// <summary>账号列表里的一行。</summary>
public sealed class AccountRowVm : ObservableObject
{
    private readonly MainViewModel _owner;
    private AccountDto _dto;

    public AccountRowVm(MainViewModel owner, AccountDto dto)
    {
        _owner = owner;
        _dto = dto;

        UseCommand = new AsyncRelayCommand(UseAsync, () => !IsActive, owner.ReportError);
        CheckinCommand = new AsyncRelayCommand(CheckinAsync, null, owner.ReportError);
        RenewCommand = new AsyncRelayCommand(RenewAsync, null, owner.ReportError);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsRefreshing, owner.ReportError);
        RenameCommand = new RelayCommand(Rename);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, null, owner.ReportError);
    }

    public AsyncRelayCommand UseCommand { get; }
    public AsyncRelayCommand CheckinCommand { get; }
    public AsyncRelayCommand RenewCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand RenameCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }

    public string Id => _dto.Id ?? "";

    /// <summary>界面上的显示名：优先标签，其次昵称，最后 uid 前 8 位。</summary>
    public string Display
    {
        get
        {
            var label = _dto.Label;
            if (!string.IsNullOrWhiteSpace(label)) return label!;
            var nickname = _dto.Nickname;
            if (!string.IsNullOrWhiteSpace(nickname)) return nickname!;
            var uid = _dto.Uid ?? "";
            return uid.Length > 8 ? uid[..8] : uid;
        }
    }

    public string UidShort
    {
        get
        {
            var uid = _dto.Uid ?? "";
            return uid.Length > 8 ? uid[..8] : uid;
        }
    }

    public bool IsActive => _dto.Active;

    public string ActiveBadge => IsActive ? "网关正在用" : "";

    public Brush StatusBrush => MainViewModel.BrushFor(
        _dto.Error is { Length: > 0 } ? "Bad" : !IsActive ? "TextSub" : "Ok");

    public string CreditsText => _dto.CreditsUnlimited
        ? "无限"
        : _dto.Credits is { } credits
            ? credits.ToString("0.##", CultureInfo.InvariantCulture)
            : "—";

    public string CheckinText
    {
        get
        {
            var checkin = _dto.Checkin;
            if (checkin is null) return "签到状态读不到";
            if (!checkin.Active) return "当前没有签到活动";
            return checkin.TodayCheckedIn
                ? $"今天已签 · 连续 {checkin.StreakDays} 天 · 每天 +{checkin.DailyCredit}"
                : $"今天还没签 · 连续 {checkin.StreakDays} 天 · 签了拿 +{checkin.DailyCredit}";
        }
    }

    public Brush CheckinBrush
    {
        get
        {
            var checkin = _dto.Checkin;
            if (checkin is null || !checkin.Active) return MainViewModel.BrushFor("TextSub");
            return MainViewModel.BrushFor(checkin.TodayCheckedIn ? "Ok" : "Warn");
        }
    }

    /// <summary>本周进度画成一串圆点，实心 = 已签。</summary>
    public string WeekText
    {
        get
        {
            var checkin = _dto.Checkin;
            if (checkin?.WeekProgress is not { Count: > 0 } week) return "";
            var dots = string.Concat(week.Select(day => day ? "●" : "○"));
            return $"{dots}  本周 {checkin.WeekCheckinDays}/7　本季累计 {checkin.TotalCredits} 积分";
        }
    }

    public string ActivityText
    {
        get
        {
            var checkin = _dto.Checkin;
            if (checkin is null || string.IsNullOrWhiteSpace(checkin.ActivityName)) return "";
            var season = checkin.Season > 0 ? $"第 {checkin.Season} 期　" : "";
            var until = string.IsNullOrWhiteSpace(checkin.EndTime) ? "" : $"　截止 {checkin.EndTime}";
            return $"{season}{checkin.ThemeName} · {checkin.ActivityName}{until}";
        }
    }

    public string ExpiresText
    {
        get
        {
            if (!DateTimeOffset.TryParse(_dto.ExpiresAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal, out var expires))
            {
                return "令牌到期时间未知";
            }

            var remaining = expires - DateTimeOffset.UtcNow;
            return remaining.TotalHours > 0
                ? $"令牌 {expires.ToLocalTime():yyyy-MM-dd HH:mm} 到期"
                : "令牌已过期（会自动刷新）";
        }
    }

    /// <summary>
    /// 登录还能自动续多久。
    ///
    /// 这是**唯一**决定「会不会掉登录」的指标 —— accessToken 过期无所谓，
    /// 能自动刷；但 refreshToken 的窗口一旦走完，就必须重新扫码。
    /// 服务端每次刷新都会重新给一个满额 60 天的窗口，所以在窗口内刷一次就又能续。
    /// </summary>
    public int? DaysLeft => _dto.RefreshDaysLeft;

    public string RenewText
    {
        get
        {
            var days = _dto.RefreshDaysLeft;
            if (days is null) return "续期窗口未知（点一次「续期」就知道）";
            if (days < 0) return "⚠ 续期窗口已过期，需要重新登录这个号";
            if (days < 20) return $"⚠ 还能自动续 {days} 天，建议马上点「续期」";
            return $"还能自动续 {days} 天";
        }
    }

    public Brush RenewBrush
    {
        get
        {
            var days = _dto.RefreshDaysLeft;
            if (days is null) return MainViewModel.BrushFor("TextSub");
            if (days < 0) return MainViewModel.BrushFor("Bad");
            if (days < 20) return MainViewModel.BrushFor("Warn");
            return MainViewModel.BrushFor("Ok");
        }
    }

    public bool NeedsRenew => _dto.RefreshDaysLeft is null or < 45;

    public string ErrorText => _dto.Error ?? "";
    public bool HasError => !string.IsNullOrEmpty(_dto.Error);

    /// <summary>轻量刷新拿不到签到状态时，用它把上一次的补回来。</summary>
    public CheckinDto? CheckinSnapshot => _dto.Checkin;

    public void Refresh(AccountDto dto)
    {
        _dto = dto;        Raise(nameof(Display));
        Raise(nameof(UidShort));
        Raise(nameof(IsActive));
        Raise(nameof(ActiveBadge));
        Raise(nameof(StatusBrush));
        Raise(nameof(CreditsText));
        Raise(nameof(CheckinText));
        Raise(nameof(CheckinBrush));
        Raise(nameof(WeekText));
        Raise(nameof(ActivityText));
        Raise(nameof(ExpiresText));
        Raise(nameof(RenewText));
        Raise(nameof(RenewBrush));
        Raise(nameof(DaysLeft));
        Raise(nameof(NeedsRenew));
        Raise(nameof(ErrorText));
        Raise(nameof(HasError));
        UseCommand.RaiseCanExecuteChanged();
    }

    private async Task UseAsync() => await _owner.UseAccountAsync(this);
    /// <summary>
    /// 手动续期 / 续期+签到：**一定真的刷新**。
    ///
    /// 以前走的是 `renewIfStale` 的 45 天阈值 —— 窗口还剩 59 天就直接跳过，
    /// 界面上看着就是“点了没反应”。用户手动点按钮的意思就是“现在给我刷一下”，
    /// 不是“你自己判断需不需要”。所以手动路径一律 force。
    ///
    /// （自动刷新（定时器和启动时）仍然走阈值，那个才是真的不该白刷。）
    /// </summary>
    private async Task CheckinAsync() => await _owner.CheckinAccountsAsync([this], force: true);

    private async Task RenewAsync() => await _owner.RenewAccountsAsync([this], force: true);

    private bool _isRefreshing;

    /// <summary>
    /// 正在刷新这一行。
    ///
    /// 单行刷新要 0.5 秒，这段时间界面上什么都没发生 —— 用户会以为没点上，
    /// 然后连着戳好几下。转个圈比“什么都不做”诚实得多。
    /// </summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!Set(ref _isRefreshing, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>给外部（VM）置位，内部不直接改，避免刷到一半又被点一下。</summary>
    public void SetRefreshing(bool value) => IsRefreshing = value;

    /// <summary>只刷这一个账号的积分。不查签到、不续期，所以很快。</summary>
    private async Task RefreshAsync() => await _owner.RefreshAccountAsync(this);

    private void Rename() => _owner.RenameAccount(this);
    private async Task RemoveAsync() => await _owner.RemoveAccountAsync(this);
}

public sealed partial class MainViewModel
{
    /// <summary>保险库里的账号。</summary>
    public ObservableCollection<AccountRowVm> Accounts { get; } = [];

    public AsyncRelayCommand RefreshAccountsCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshAccountsLightCommand { get; private set; } = null!;
    public AsyncRelayCommand ImportAccountCommand { get; private set; } = null!;
    public AsyncRelayCommand CheckinAllCommand { get; private set; } = null!;
    public AsyncRelayCommand RenewAllCommand { get; private set; } = null!;
    public AsyncRelayCommand ClearActiveAccountCommand { get; private set; } = null!;
    public AsyncRelayCommand RegisterTaskCommand { get; private set; } = null!;
    public AsyncRelayCommand RemoveTaskCommand { get; private set; } = null!;
    public RelayCommand RunTaskNowCommand { get; private set; } = null!;
    public AsyncRelayCommand ExportAccountsCommand { get; private set; } = null!;
    public AsyncRelayCommand ImportAccountsCommand { get; private set; } = null!;

    private string _taskStatus = "";
    public string TaskStatus { get => _taskStatus; private set => Set(ref _taskStatus, value); }

    private bool _taskRegistered;
    public bool TaskRegistered
    {
        get => _taskRegistered;
        private set
        {
            if (!Set(ref _taskRegistered, value)) return;
            RegisterTaskCommand?.RaiseCanExecuteChanged();
            RemoveTaskCommand?.RaiseCanExecuteChanged();
            RunTaskNowCommand?.RaiseCanExecuteChanged();
        }
    }

    private string _accountsSummary = "";
    public string AccountsSummary { get => _accountsSummary; private set => Set(ref _accountsSummary, value); }

    private string _accountsResult = "";
    public string AccountsResult
    {
        get => _accountsResult;
        private set
        {
            if (Set(ref _accountsResult, value)) Raise(nameof(HasAccountsResult));
        }
    }

    public bool HasAccountsResult => _accountsResult.Length > 0;

    // ── 首页紧凑条用 ────────────────────────────────────

    /// <summary>首页只显示“当前在用哪个号”。</summary>
    public string ActiveAccountShort
    {
        get
        {
            var active = Accounts.FirstOrDefault(row => row.IsActive);
            if (active is not null) return active.Display;
            return Accounts.Count == 0 ? "桌面 App 登录的号" : "桌面 App 登录的号";
        }
    }

    /// <summary>首页只显示“在用那个号的余额”；没存账号就退回 status 的值。</summary>
    public string StatusCreditsText
    {
        get
        {
            var active = Accounts.FirstOrDefault(row => row.IsActive);
            return active is not null ? active.CreditsText : CreditsText;
        }
    }

    public string AccountsCountText => Accounts.Count == 0 ? "暂未存号" : $"共 {Accounts.Count} 个号";

    private void RaiseHomeSummary()
    {
        Raise(nameof(ActiveAccountShort));
        Raise(nameof(StatusCreditsText));
        Raise(nameof(AccountsCountText));
    }

    // ── 自动刷新 ──────────────────────────────────────────

    /// <summary>账号页是否自动刷新积分。默认开。</summary>
    private bool _accountsAutoRefresh = true;
    public bool AccountsAutoRefresh
    {
        get => _accountsAutoRefresh;
        set
        {
            if (!Set(ref _accountsAutoRefresh, value)) return;
            _settings.AccountsAutoRefresh = value;
            _settings.Save();
        }
    }

    /// <summary>自动刷新的间隔（秒）。积分按次扣，没必要刷太勤。</summary>
    public int AccountsRefreshSeconds => 60;

    /// <summary>
    /// 绑定用的字符串。
    /// 直接绑 int 到 Run.Text 渲染不出来（实测得到空），拼成字符串就没事。
    /// </summary>
    public string AccountsRefreshText => $"停留在本页时每 {AccountsRefreshSeconds} 秒刷一次（只查积分，不查签到，比较省）";

    /// <summary>上次刷新完的时间，界面显示用。</summary>
    private string _accountsFetchedAt = "还没刷新";
    public string AccountsFetchedAt { get => _accountsFetchedAt; private set => Set(ref _accountsFetchedAt, value); }

    /// <summary>
    /// 轻量刷新：只拉积分和续期窗口，**不问签到状态**。
    ///
    /// 自动刷新会反复跑，而签到状态每天只变一次 —— 每次都问就是白花 2N 个请求。
    /// 实测完整刷新 4.0s，轻量刷新 2.0s。
    /// </summary>
    public async Task RefreshAccountsLightAsync()
    {
        if (IsBusy || !CanRunNode || Accounts.Count == 0) return;

        // 不置 IsBusy：自动刷新是背景行为，不该把界面按钮都灰掉
        try
        {
            var result = await CliRunner.RunAsync(
                NodePath, ApiDirectory, $"src/bin.js accounts --no-checkin --json{ProviderArgs()}");
            var json = CliRunner.ExtractJson(result.StdOut);
            if (json is null) return;

            var dto = JsonSerializer.Deserialize<AccountListDto>(json);
            var incoming = (dto?.Data ?? []).ToDictionary(item => item.Id ?? "", item => item);

            // 就地更新，不重建列表（重建会让正在看的那一行跳一下）
            foreach (var row in Accounts)
            {
                if (!incoming.TryGetValue(row.Id, out var fresh)) continue;
                // 轻量刷新拿不到 checkin，保留上一次的，别把“今天已签”抹成“读不到”
                fresh.Checkin ??= row.CheckinSnapshot;
                row.Refresh(fresh);
            }

            AccountsFetchedAt = DateTime.Now.ToString("HH:mm:ss");
            Raise(nameof(ActiveAccountText));
            RaiseHomeSummary();
        }
        catch (Exception ex)
        {
            AppendLog($"[刷新] 自动刷新失败：{ex.Message}");
        }
    }

    public bool HasAccounts => Accounts.Count > 0;

    public bool HasNoAccounts => Accounts.Count == 0;

    /// <summary>网关当前到底在用哪个号（保险库选中就写名字，没选就是桌面 App 那个）。</summary>
    public string ActiveAccountText
    {
        get
        {
            var active = Accounts.FirstOrDefault(row => row.IsActive);
            if (active is not null) return $"{active.Display}（{active.UidShort}）";
            return Accounts.Count == 0 ? "还没有存过账号" : "桌面 App 当前登录的账号（保险库未选定）";
        }
    }

    private void InitialiseAccounts()
    {
        RefreshAccountsCommand = new AsyncRelayCommand(RefreshAccountsAsync, null, ReportError);
        RefreshAccountsLightCommand = new AsyncRelayCommand(RefreshAccountsLightAsync, null, ReportError);
        ImportAccountCommand = new AsyncRelayCommand(ImportAccountAsync, null, ReportError);
        CheckinAllCommand = new AsyncRelayCommand(() => CheckinAccountsAsync(Accounts.ToList(), force: true), null, ReportError);
        RenewAllCommand = new AsyncRelayCommand(() => RenewAccountsAsync(Accounts.ToList(), force: true), null, ReportError);
        ClearActiveAccountCommand = new AsyncRelayCommand(ClearActiveAccountAsync, null, ReportError);
        RegisterTaskCommand = new AsyncRelayCommand(RegisterTaskAsync, () => !TaskRegistered, ReportError);
        RemoveTaskCommand = new AsyncRelayCommand(RemoveTaskAsync, () => TaskRegistered, ReportError);
        RunTaskNowCommand = new RelayCommand(RunTaskNow, () => TaskRegistered);
        ExportAccountsCommand = new AsyncRelayCommand(ExportAccountsAsync, null, ReportError);
        ImportAccountsCommand = new AsyncRelayCommand(ImportAccountsAsync, null, ReportError);
        RefreshTaskStatus();
    }

    private void RefreshTaskStatus() => _ = RefreshTaskStatusAsync();

    /// <summary>
    /// 查计划任务状态。
    ///
    /// 要走两次 schtasks（实测合计 ~53ms），以前是在构造函数里同步跑的 ——
    /// 那就是窗口出现前的纯等待。现在丢后台，先显示“查询中…”，回来再填。
    ///
    /// @returns 查完后的状态文字，调用方需要拿它拼结果就 await 一下
    /// </summary>
    private async Task<string> RefreshTaskStatusAsync()
    {
        try
        {
            var (registered, status) = await Task.Run(() =>
            {
                bool isRegistered = ScheduledTask.IsRegistered();
                return (isRegistered, isRegistered
                    ? $"已注册每日自动续期（{ScheduledTask.Describe()}），错过时间会在下次开机后补跑"
                    : "未注册。不注册也能用，但你得自己记得定期点「续期」");
            });

            TaskRegistered = registered;
            TaskStatus = status;
            return status;
        }
        catch (Exception ex)
        {
            TaskRegistered = false;
            var message = $"查询计划任务失败：{ex.Message}";
            TaskStatus = message;
            return message;
        }
    }

    /// <summary>
    /// 注册每日自动续期。
    ///
    /// 结果只写日志和底部状态栏 —— 「操作结果」那里**故意不写**。
    /// 上面那张卡片里的状态框已经把结果写上去了，再写一遍就是同一句话出现两遍。
    /// </summary>
    private async Task RegisterTaskAsync()
    {
        await Task.Run(() => ScheduledTask.Register());
        var status = await RefreshTaskStatusAsync();
        AppendLog("[续期任务] " + status);
        AccountsResult = "";        // 结果由上面卡片里的状态框展示，不在这里重复
        FlashStatus("已注册每日自动续期");
    }

    private async Task RemoveTaskAsync()
    {
        await Task.Run(() => ScheduledTask.Remove());
        var status = await RefreshTaskStatusAsync();
        AppendLog("[续期任务] " + status);
        AccountsResult = "";        // 同上：结果由卡片里的状态框展示
        FlashStatus("已删除每日自动续期");
    }

    private void RunTaskNow() => _ = RunTaskNowAsync();

    /// <summary>
    /// 立即跑一次计划任务，并**等它的真实结果**。
    ///
    /// `schtasks /Run` 只是“叫任务现在跑”，它立刻返回 —— 任务本体的输出跟它无关。
    /// 所以以前点了按钮只能写一句“大约 10 秒后看结果”，实际上永远没反馈：
    /// 用户等到最后只能自己去翻日志。
    ///
    /// 现在改成：记下日志当前长度 → 触发 → 每秒看日志有没有变长 →
    /// 拿到新增那几行就是本次结果（实测任务跑完约 2 秒）。
    /// </summary>
    private async Task RunTaskNowAsync()
    {
        var logPath = MaintenanceLogPath;

        long before;
        try
        {
            before = await Task.Run(() => File.Exists(logPath) ? new FileInfo(logPath).Length : 0L);
        }
        catch
        {
            before = 0L;
        }

        try
        {
            await Task.Run(() => ScheduledTask.RunNow());
        }
        catch (Exception ex)
        {
            ReportError(ex);
            return;
        }

        FlashStatus("已触发，正在等维护跑完…");

        // 最多等 40 秒。正常情况 2 秒左右就回来了。
        var deadline = DateTime.UtcNow.AddSeconds(40);
        string? result = null;

        while (DateTime.UtcNow < deadline && result is null)
        {
            await Task.Delay(800);
            result = await Task.Run(() => ReadNewLines(logPath, before));
        }

        if (result is null)
        {
            SetStatus("等了 40 秒没等到维护结果。去「日志」标签页看看，或者检查计划任务是不是被禁了。");
            return;
        }

        AccountsResult = result;
        AppendLog("[续期任务] 手动触发完成：" + result);
        FlashStatus("计划任务已跑完");

        // 维护会把续期窗口往后推，刷一下让界面跟上
        await RefreshAccountsAsync();
    }

    /// <summary>读文件从 from 字节开始的新内容；没新增就返回 null。</summary>
    private static string? ReadNewLines(string path, long from)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= from) return null;
            stream.Seek(from, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd().Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            // 正在写的时候可能读个半截，下一轮再看
            return null;
        }
    }

    /// <summary>
    /// 主动续期。
    ///
    /// 服务端给的是**滑动窗口** —— 每次刷新都会重新发一个满额 60 天的 refreshToken。
    /// 所以在窗口内刷一次就又能续 60 天，一次扫码可以一直用下去。
    /// 这个是手动的；`checkin` 和启动时的维护会自动带一遍。
    /// </summary>
    public async Task RenewAccountsAsync(IReadOnlyList<AccountRowVm> rows, bool force)
    {
        if (rows.Count == 0) return;

        IsBusy = true;
        BusyText = "正在续期…";
        try
        {
            var ids = string.Join(" ", rows.Select(row => $"\"{row.Id}\""));
            var forceArg = force ? " --force" : "";
            var result = await CliRunner.RunAsync(
                NodePath, ApiDirectory, $"src/bin.js accounts renew{forceArg} {ids}{ProviderArgs()}");
            AccountsResult = result.Combined;
            AppendLog("[续期] " + result.Combined.Replace("\n", " / "));
            if (result.Ok) FlashStatus("续期完成"); else SetStatus("续期失败，看「日志」标签页");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }

        await RefreshAccountsAsync();
    }

    /// <summary>
    /// 导出账号：把保险库打包成一个**加密**文件，拷到另一台电脑导入就能继续用。
    ///
    /// 为什么要加密：文件里有 refreshToken，而且服务端是滑动窗口，能一直续 ——
    /// 明文拷到微信/网盘就等于把额度公开了。
    /// </summary>
    private async Task ExportAccountsAsync()
    {
        if (Accounts.Count == 0)
        {
            MessageBox.Show("保险库里还没有账号，没东西可导出。", "WorkBuddy 控制台",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var owner = Application.Current?.MainWindow;
        var passphrase = PasswordInputDialog.Ask(
            owner,
            "导出账号",
            $"把保险库里的 {Accounts.Count} 个账号导出成一个加密文件。\n\n" +
            "设一个口令：在另一台电脑导入时要输同一个口令。",
            "导出后建议导入完就把文件删掉。",
            TransferRules.ExportWarning,
            confirm: true);
        if (passphrase is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存账号导出文件",
            FileName = TransferRules.SuggestedFileName(Provider),
            Filter = "账号导出文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            DefaultExt = ".json",
            AddExtension = true,
        };
        if (dialog.ShowDialog(owner) != true) return;

        IsBusy = true;
        BusyText = "正在导出账号…";
        try
        {
            // 口令走 stdin（写两遍，CLI 会要求确认），不经命令行参数
            var result = await CliRunner.RunAsync(
                NodePath,
                ApiDirectory,
                $"src/bin.js accounts export --out \"{dialog.FileName}\"{ProviderArgs()}",
                stdin: $"{passphrase}\n{passphrase}\n");

            AccountsResult = result.Combined;
            AppendLog("[迁移] " + result.Combined.Replace("\n", " / "));
            if (result.Ok) FlashStatus($"已导出到 {dialog.FileName}"); else SetStatus("导出失败，看「日志」标签页");

            if (result.Ok)
            {
                MessageBox.Show(
                    $"已导出到：\n{dialog.FileName}\n\n" +
                    "拷到另一台电脑，在「账号」页点「导入账号」选这个文件，输入同一个口令就行。\n\n" +
                    "导入完记得把这个文件删掉。",
                    "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    /// <summary>
    /// 导入账号：解开文件、合并进本机保险库，**并立刻验证每个号能不能刷新**。
    /// 验证这一步才是「无缝接上」的关键 —— 不能只写进去就说成了。
    /// </summary>
    private async Task ImportAccountsAsync()
    {
        var owner = Application.Current?.MainWindow;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择账号导出文件",
            Filter = "账号导出文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(owner) != true) return;

        var passphrase = PasswordInputDialog.Ask(
            owner,
            "导入账号",
            "输入导出时设的那个口令。",
            "导入后会自动验证每个账号能不能刷新。",
            "导入只新增/更新账号，不会删掉本机已有的；本机起过的名字也不会被覆盖。",
            confirm: false);
        if (passphrase is null) return;

        IsBusy = true;
        BusyText = "正在导入并验证…";
        try
        {
            var result = await CliRunner.RunAsync(
                NodePath,
                ApiDirectory,
                $"src/bin.js accounts import \"{dialog.FileName}\" --json{ProviderArgs()}",
                stdin: $"{passphrase}\n");

            var json = CliRunner.ExtractJson(result.StdOut);
            var lines = new List<string>();

            if (json is null)
            {
                lines.Add(result.Combined);
            }
            else
            {
                var dto = JsonSerializer.Deserialize<ImportListDto>(json) ?? new ImportListDto();
                foreach (var item in dto.Data ?? [])
                {
                    var name = string.IsNullOrWhiteSpace(item.Label)
                        ? (item.Uid ?? "").PadRight(0).Trim()
                        : item.Label!;
                    lines.Add($"{(item.Ok ? "✔" : "✘")}　{name}：{item.Msg}");
                }

                var usable = (dto.Data ?? []).Count(item => item.Ok);
                lines.Add("");
                lines.Add($"导入 {dto.Data?.Count ?? 0} 个，可正常使用 {usable} 个");
                if (usable < (dto.Data?.Count ?? 0))
                {
                    lines.Add("失败的那些可能是服务端风控（换机器/换 IP 会拦）或令牌已作废，重新登录一次就好。");
                }
            }

            AccountsResult = string.Join("\n", lines);
            AppendLog("[迁移] " + string.Join(" / ", lines));
            if (result.Ok) FlashStatus("导入完成"); else SetStatus("导入有失败项，看「日志」标签页");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }

        await RefreshAccountsAsync();
    }

    /// <summary>
    /// 单行刷新：只拉这一个账号的积分，不查签到、不续期。
    ///
    /// 为什么要单独开一个而不是复用全量刷新：6 个号全刷要跑 6 个请求，
    /// 而用户往往只想看某一行。实测单刷 0.55s，全刷 1.9s。
    /// </summary>
    public async Task RefreshAccountAsync(AccountRowVm row)
    {
        if (!CanRunNode || row.IsRefreshing) return;

        row.SetRefreshing(true);
        try
        {
            var result = await CliRunner.RunAsync(
                NodePath,
                ApiDirectory,
                $"src/bin.js accounts --no-checkin --id \"{row.Id}\" --json{ProviderArgs()}");
            var json = CliRunner.ExtractJson(result.StdOut);
            if (json is null)
            {
                AppendLog($"[刷新] {row.Display} 失败：{(string.IsNullOrWhiteSpace(result.Combined) ? "无输出" : result.Combined)}");
                return;
            }

            var dto = JsonSerializer.Deserialize<AccountListDto>(json);
            var fresh = dto?.Data?.FirstOrDefault();
            if (fresh is null) return;

            // 没查签到，把上一次的补回来，别把「今天已签」抹成「读不到」
            fresh.Checkin ??= row.CheckinSnapshot;
            row.Refresh(fresh);

            AccountsFetchedAt = DateTime.Now.ToString("HH:mm:ss");
            RaiseHomeSummary();
        }
        catch (Exception ex)
        {
            AppendLog($"[刷新] {row.Display} 出错：{ex.Message}");
        }
        finally
        {
            row.SetRefreshing(false);
        }
    }

    /// <summary>
    /// 一次日常维护：续期 + 签到。`--maintain` 和启动时的自动跑都走这里。
    /// checkin 内部会对每个账号先续期再签，所以一条命令就够。
    ///
    /// @returns 结果说明（写日志用）
    /// </summary>
    public async Task<string> RunMaintenanceAsync()
    {
        if (!CanRunNode)
        {
            const string skipped = "维护跳过：还没找到 node / workbuddy_api";
            LogMaintenance(skipped);
            return skipped;
        }

        var result = await CliRunner.RunAsync(
            NodePath, ApiDirectory, $"src/bin.js checkin --json{ProviderArgs()}");
        var json = CliRunner.ExtractJson(result.StdOut);
        if (json is null)
        {
            LogMaintenance($"取不到 JSON（exit={result.ExitCode}）stdout={Trim(result.StdOut)} stderr={Trim(result.StdErr)}");
            return "维护失败：命令没返回可解析的结果";
        }

        var dto = JsonSerializer.Deserialize<CheckinListDto>(json) ?? new CheckinListDto();
        var summary = string.Join("；", (dto.Data ?? []).Select(item =>
            $"{item.Label} {(item.Claimed ? "新签到" : "已签过")}{(item.Renewed ? "+已续期" : "")}"));
        var message = string.IsNullOrEmpty(summary)
            ? $"维护完成：没有账号。原文={Trim(result.StdOut)}"
            : $"维护完成：{summary}（exit={result.ExitCode}）";
        LogMaintenance(message);
        return message;
    }

    /// <summary>日志里不要把整段输出刷进去。</summary>
    private static string Trim(string value)
        => value.Length <= 400 ? value : value[..400] + "…";

    /// <summary>给维护流程写日志。计划任务是隐形的，不落盘就没法查。MainWindow 也要用。</summary>
    internal void LogMaintenance(string message)
    {
        AppendLog("[维护] " + message);
        WriteMaintenanceLog(message);
    }

    /// <summary>
    /// 只写文件（不发 UI）。静态的，因为 App 在窗口还没建好时就需要它。
    /// </summary>
    internal static void WriteMaintenanceLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MaintenanceLogPath)!);
            File.AppendAllText(
                MaintenanceLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch
        {
            // 日志写不了不能拦住维护本身
        }
    }

    /// <summary>维护日志。计划任务跑在后台，出错全靠它。</summary>
    internal static string MaintenanceLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkBuddyPanel",
        "maintain.log");

    /// <summary>启动时如果发现哪个号的续期窗口不够了，就静默跑一次维护。</summary>
    public async Task AutoMaintainIfNeededAsync()
    {
        await RefreshAccountsAsync();
        if (Accounts.Count == 0) return;

        // 只看续期窗口：窗口不够或未知就先刷一遍，别等用户想起来
        var urgent = Accounts.Where(row => row.NeedsRenew).ToList();
        if (urgent.Count == 0)
        {
            var detail = string.Join("、", Accounts.Select(row => $"{row.Display} {row.DaysLeft}天"));
            AppendLog($"[维护] 续期窗口都还够（{detail}），跳过");
            return;
        }

        AppendLog($"[维护] {urgent.Count} 个账号需要续期，自动跑一次…");
        await RunMaintenanceAsync();
        await RefreshAccountsAsync();
    }

    /// <summary>把保险库读进来，顺带拉积分和签到状态。</summary>
    public async Task RefreshAccountsAsync()
    {
        if (!CanRunNode)
        {
            AccountsSummary = "还没找到 node / workbuddy_api，去「设置」点自动探测";
            return;
        }

        IsBusy = true;
        BusyText = "正在读取账号…";
        try
        {
            var result = await CliRunner.RunAsync(
                NodePath, ApiDirectory, $"src/bin.js accounts --json{ProviderArgs()}");
            var json = CliRunner.ExtractJson(result.StdOut);
            if (json is null)
            {
                AccountsSummary = "账号列表读不出来";
                AccountsResult = string.IsNullOrWhiteSpace(result.Combined) ? "accounts 没有输出" : result.Combined;
                return;
            }

            var dto = JsonSerializer.Deserialize<AccountListDto>(json) ?? new AccountListDto();
            var rows = dto.Data ?? [];

            Accounts.Clear();
            foreach (var account in rows) Accounts.Add(new AccountRowVm(this, account));

            var active = Accounts.FirstOrDefault(row => row.IsActive);
            AccountsSummary = Accounts.Count == 0
                ? "保险库里还没有账号"
                : $"共 {Accounts.Count} 个账号" + (active is null ? "" : $"　当前使用：{active.Display}");

            AccountsFetchedAt = DateTime.Now.ToString("HH:mm:ss");
            RaiseHomeSummary();
            Raise(nameof(HasAccounts));
            Raise(nameof(HasNoAccounts));
            Raise(nameof(ActiveAccountText));
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    /// <summary>把桌面 App 现在登录的那个号存进保险库。</summary>
    private async Task ImportAccountAsync()
    {
        var suggested = $"账号 {Accounts.Count + 1}";
        var label = TextInputDialog.Ask(
            Application.Current?.MainWindow,
            "导入当前账号",
            "给这个账号起个名字（方便区分，可以留空）：\n" +
            "导入的是 WorkBuddy 桌面 App 现在登录的那个号。想加第二个号，\n" +
            "先去 App 里换号，再回来点一次这个按钮。",
            suggested);
        if (label is null) return;

        IsBusy = true;
        BusyText = "正在导入账号…";
        try
        {
            var args = $"src/bin.js accounts add{ProviderArgs()}";
            if (!string.IsNullOrWhiteSpace(label)) args += $" --label \"{label.Trim()}\"";
            var result = await CliRunner.RunAsync(NodePath, ApiDirectory, args);
            AccountsResult = result.Combined;
            AppendLog("[账号] " + result.Combined);
            if (result.Ok) FlashStatus("账号已导入"); else SetStatus("导入失败，看「日志」标签页");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }

        await RefreshAccountsAsync();
    }

    /// <summary>给选中的账号签到。已经签过的上游会直接告诉你，不会重复扣。</summary>
    /// <summary>
    /// 给选中的账号续期 + 签到。
    ///
    /// `force = true` 时一定真的发刷新请求；
    /// `force = false` 时交给 45 天阈值判断（旧的定时路径用）。
    /// </summary>
    public async Task CheckinAccountsAsync(IReadOnlyList<AccountRowVm> rows, bool force = false)
    {
        if (rows.Count == 0) return;

        IsBusy = true;
        BusyText = force ? "正在续期并签到…" : "正在签到…";
        try
        {
            var ids = string.Join(" ", rows.Select(row => $"--id \"{row.Id}\""));
            var forceArg = force ? " --force" : "";
            var result = await CliRunner.RunAsync(
                NodePath, ApiDirectory, $"src/bin.js checkin --json{forceArg} {ids}{ProviderArgs()}");
            var json = CliRunner.ExtractJson(result.StdOut);

            var lines = new List<string>();
            if (json is null)
            {
                lines.Add(result.Combined);
            }
            else
            {
                var dto = JsonSerializer.Deserialize<CheckinListDto>(json) ?? new CheckinListDto();
                foreach (var item in dto.Data ?? [])
                {
                    var mark = item.Ok ? (item.Claimed ? "✔ 新签到" : "· 已签过") : "✘ 失败";
                    lines.Add($"{mark}　{item.Label}：{item.Msg}");
                }

                var claimed = (dto.Data ?? []).Count(item => item.Claimed);
                lines.Add("");
                lines.Add($"共 {dto.Data?.Count ?? 0} 个账号，本次新签到 {claimed} 个");
            }

            AccountsResult = string.Join("\n", lines);
            AppendLog("[签到] " + string.Join(" / ", lines));
            FlashStatus("签到完成");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }

        await RefreshAccountsAsync();
    }

    /// <summary>选定网关要用哪个号。切换立刻生效，不用重启网关。</summary>
    public async Task UseAccountAsync(AccountRowVm row)
    {
        await RunAccountCommand($"src/bin.js accounts use \"{row.Id}\"{ProviderArgs()}", $"已切换到 {row.Display}");
        await RefreshAccountsAsync();
    }

    /// <summary>取消选定，退回桌面 App 当前登录的号。</summary>
    private async Task ClearActiveAccountAsync()
    {
        await RunAccountCommand($"src/bin.js accounts clear{ProviderArgs()}", "已取消选定");
        await RefreshAccountsAsync();
    }

    internal void RenameAccount(AccountRowVm row)
    {
        var label = TextInputDialog.Ask(
            Application.Current?.MainWindow,
            "重命名账号",
            $"给 {row.Display} 换个名字：",
            row.Display);
        if (label is null) return;

        _ = RunAccountCommand(
            $"src/bin.js accounts rename \"{row.Id}\" \"{label.Trim()}\"{ProviderArgs()}",
            $"已重命名为 {label.Trim()}",
            refresh: true);
    }

    public async Task RemoveAccountAsync(AccountRowVm row)
    {
        var confirm = MessageBox.Show(
            $"确定要把账号「{row.Display}」从保险库里删掉吗？\n\n" +
            "只删本程序存的副本，不动 WorkBuddy 桌面 App 的登录。\n" +
            "删了之后想再用，重新点一次「导入当前账号」就行。",
            "WorkBuddy 控制台", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        await RunAccountCommand($"src/bin.js accounts remove \"{row.Id}\"{ProviderArgs()}", $"已删除 {row.Display}");
        await RefreshAccountsAsync();
    }

    private async Task RunAccountCommand(string args, string successNote, bool refresh = false)
    {
        IsBusy = true;
        BusyText = "正在执行…";
        try
        {
            var result = await CliRunner.RunAsync(NodePath, ApiDirectory, args);
            AccountsResult = result.Combined;
            AppendLog("[账号] " + result.Combined);
            if (result.Ok) FlashStatus(successNote); else SetStatus("操作失败，看「日志」标签页");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }

        if (refresh) await RefreshAccountsAsync();
    }
}
