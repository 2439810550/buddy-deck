using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using WorkBuddyPanel.Services;

namespace WorkBuddyPanel.ViewModels;

/// <summary>
/// 分池页里的一行。
///
/// 一行 = 一个池 = 一套账号 + 一个网关实例（自己的端口）。
/// </summary>
public sealed class PoolRowVm : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly PoolConfig _config;
    private readonly PoolRuntime _runtime;

    public PoolRowVm(MainViewModel owner, PoolConfig config, PoolRuntime runtime)
    {
        _owner = owner;
        _config = config;
        _runtime = runtime;

        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning, owner.ReportError);
        StopCommand = new RelayCommand(() => Stop(), () => IsRunning);
        EditCommand = new RelayCommand(Edit);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, null, owner.ReportError);
        CopyUrlCommand = new RelayCommand(() => CopyUrl(UrlLocal));
        CopyLanUrlCommand = new RelayCommand(() => CopyUrl(UrlLan));
        CopyKeyCommand = new RelayCommand(() => CopyUrl(ApiKey));
        OpenFolderCommand = new RelayCommand(OpenFolder);
        ManageAccountsCommand = new RelayCommand(() => _owner.SelectPoolForAccounts(this));
    }

    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand EditCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }
    public RelayCommand CopyUrlCommand { get; }
    public RelayCommand CopyLanUrlCommand { get; }
    public RelayCommand CopyKeyCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ManageAccountsCommand { get; }

    internal PoolConfig Config => _config;

    public string DisplayName => _config.DisplayName;

    public string NoteText => string.IsNullOrWhiteSpace(_config.Note) ? "" : _config.Note!;

    public bool HasNote => !string.IsNullOrWhiteSpace(_config.Note);

    public bool IsDefault => _config.IsDefault;

    /// <summary>默认池不给删，界面上把按钮藏起来。</summary>
    public bool CanRemove => !_config.IsDefault;

    /// <summary>
    /// 网关在跑，但不是这个进程起的（另一个面板实例占着端口）。
    ///
    /// 这时「停止」按钮不能给 —— 我们手里没有那个进程的句柄，
    /// 按下去只会把本地状态改乱，真实那个网关还在跑。
    /// </summary>
    public bool IsExternal => _runtime.IsRunningExternally;

    /// <summary>能停的才能停：自己的实例可以，别人的不行。</summary>
    public bool CanStop => IsRunning && !IsExternal;

    public int Port => _config.Port;

    public string ApiKey => _config.ApiKey;

    public bool IsRunning => _runtime.IsRunning;

    public string StatusText
    {
        get
        {
            if (_runtime.IsRunningExternally) return "运行中（另一实例）";
            if (_runtime.IsRunning) return "运行中";
            return _runtime.LastError is { Length: > 0 } ? "启动失败" : "已停止";
        }
    }

    /// <summary>状态点的颜色：绿=跑着，红=失败，灰=停着。</summary>
    public string StatusColor =>
        _runtime.IsRunning ? "#22A06B"
        : _runtime.LastError is { Length: > 0 } ? "#D64545"
        : "#9AA4B2";

    public string StatusDot => _runtime.IsRunning ? "●" : (_runtime.LastError is { Length: > 0 } ? "●" : "○");

    /// <summary>最近一次失败原因（成功或没跑过时为空）。</summary>
    public string ErrorText => _runtime.LastError ?? "";

    public bool HasError => _runtime.LastError is { Length: > 0 };

    /// <summary>这个池里有几个账号。-1 = 读不出来。</summary>
    public int AccountCount => _owner.PoolManagerRef.CountAccounts(_config);

    public string AccountCountText => AccountCount switch
    {
        < 0 => "账号数未知",
        0 => "还没有账号",
        1 => "1 个账号",
        _ => $"{AccountCount} 个账号",
    };

    // ── 三种 URL ───────────────────────────────────────────────
    //
    // 用户要的是「分好之后生成不同的 url」。三种都给，是因为
    // 「谁能访问」取决于对方在哪：
    //   本机   —— 自己电脑上的客户端
    //   局域网 —— 同一个 WiFi 下的人
    //   外网   —— 装过 Tailscale 的人（见 settings 里的提示）

    public string UrlLocal => $"http://127.0.0.1:{_config.Port}/v1";

    public string UrlLan
    {
        get
        {
            var ip = MainViewModel.LocalIpv4() ?? "127.0.0.1";
            return $"http://{ip}:{_config.Port}/v1";
        }
    }

    /// <summary>Tailscale 地址（装了才有）。</summary>
    public string UrlTailscale
    {
        get
        {
            var ip = MainViewModel.TailscaleIpv4();
            return ip is null ? "" : $"http://{ip}:{_config.Port}/v1";
        }
    }

    public bool HasTailscale => UrlTailscale.Length > 0;

    /// <summary>给用户直接拿去用的接入配置（客户端一般要这三样）。</summary>
    public string SetupBlock =>
        $"Base URL:  {UrlLocal}\n" +
        $"API Key:   {ApiKey}\n" +
        (HasTailscale ? $"外网地址: {UrlTailscale}（对方也要装 Tailscale）\n" : "") +
        $"模型:      用 GET /v1/models 列出来的那些";

    internal void RaiseAll()
    {
        Raise(nameof(IsRunning));
        Raise(nameof(IsExternal));
        Raise(nameof(CanStop));
        Raise(nameof(StatusText));
        Raise(nameof(StatusColor));
        Raise(nameof(StatusDot));
        Raise(nameof(ErrorText));
        Raise(nameof(HasError));
        Raise(nameof(AccountCount));
        Raise(nameof(AccountCountText));
        Raise(nameof(UrlLocal));
        Raise(nameof(UrlLan));
        Raise(nameof(UrlTailscale));
        Raise(nameof(HasTailscale));
        Raise(nameof(SetupBlock));
        Raise(nameof(DisplayName));
        Raise(nameof(NoteText));
        Raise(nameof(HasNote));
        Raise(nameof(Port));
        Raise(nameof(ApiKey));
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }

    private async Task StartAsync()
    {
        // 默认池的启动委托给主页那条流程。
        //
        // 不能自己起一个 runner：主编那个 _gateway 才是聊天页/状态/模型
        // 真正在用的实例，同时起两个会抢同一个端口。
        if (_config.IsDefault)
        {
            await _owner.StartGatewayCommand.ExecuteAsync();
            Refresh();
            return;
        }

        var node = _owner.ResolvedNodePath;
        var api = _owner.ResolvedApiDirectory;
        if (node is null || api is null)
            throw new InvalidOperationException("还没找到运行环境或服务程序，先去「设置」里探测一下。");

        var result = await _owner.PoolManagerRef.StartAsync(
            _config, node, api, _owner.Provider, _owner.AuthFileOrNull);

        if (!result) throw new InvalidOperationException(_runtime.LastError ?? "启动失败。");

        Refresh();
        _owner.FlashStatus($"「{DisplayName}」已启动");
    }

    private void Stop()
    {
        // 别人的实例不能从这儿停 —— 我们连它的句柄都没有。
        // 界面已经用 CanStop 把按钮藏了，这里再拦一道防御。
        if (IsExternal)
        {
            _owner.FlashStatus("这个网关是另一个面板实例起的，要停请去那个窗口");
            return;
        }

        _owner.PoolManagerRef.Stop(_config);
        if (_config.IsDefault) _owner.OnDefaultPoolStopped();
        Refresh();
        _owner.FlashStatus($"「{DisplayName}」已停止");
    }

    private void Edit()
    {
        var dialog = new PoolEditDialog(_config.DisplayName, _config.Note, _config.Port)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var wasDefault = _config.IsDefault;
            _owner.PoolManagerRef.Update(_config, dialog.PoolName, dialog.PoolNote, dialog.PoolPort);

            // 默认池的端口写在两个地方：pools.json（分池页显示）和
            // settings.json（真正启动网关用的）。不同步的话分池页显示 8788、
            // 而网关还在 8787 —— 两边各说各话。实测踩过。
            if (wasDefault && dialog.PortChanged)
                _owner.SyncDefaultPoolPort(_config.Port);

            Refresh();
            _owner.RefreshPools();

            if (dialog.PortChanged)
                _owner.FlashStatus("端口改了，这个池如果正在跑需要重启才生效");
            else
                _owner.FlashStatus("已保存");
        }
        catch (Exception ex)
        {
            _owner.ReportError(ex);
        }
    }

    private async Task RemoveAsync()
    {
        var count = AccountCount;
        var extra = count > 0
            ? $"\n\n这个池里有 {count} 个账号 —— 池删掉之后，这些账号的登录信息也会一起删掉。"
            : "";

        // 默认池没有「删除」，所以这里只处理非默认池
        if (count > 0)
        {
            var answer = MessageBox.Show(
                $"删除「{DisplayName}」？{extra}\n\n" +
                "（如果你只是不想用它了，点「停止」就行，不用删。）",
                "WorkBuddy 控制台", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        _owner.PoolManagerRef.Remove(_config, deleteAccounts: count > 0);
        _owner.RefreshPools();
        _owner.FlashStatus($"「{DisplayName}」已删除");
        await Task.CompletedTask;
    }

    private void CopyUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _owner.CopyTextToClipboard(text, "已复制");
    }

    private void OpenFolder()
    {
        try
        {
            var dir = _config.VaultHome();
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _owner.ReportError(ex);
        }
    }

    /// <summary>刷新界面状态（跑/停、账号数）。</summary>
    public void Refresh()
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.Invoke(RaiseAll);
            return;
        }
        RaiseAll();
    }
}

/// <summary>
/// 分池页的逻辑。挂在 MainViewModel 上（partial class）。
/// </summary>
public sealed partial class MainViewModel
{
    private PoolManager? _pools;

    /// <summary>池管理器。界面第一次用到时才建（要 Dispatcher）。</summary>
    internal PoolManager PoolManagerRef
    {
        get
        {
            if (_pools is null)
            {
                _pools = new PoolManager(Application.Current?.Dispatcher
                    ?? System.Windows.Threading.Dispatcher.CurrentDispatcher);
                _pools.Log += AppendLog;
                _pools.Load();
            }
            return _pools;
        }
    }

    /// <summary>分池页的列表。</summary>
    public ObservableCollection<PoolRowVm> Pools { get; } = [];

    /// <summary>新建一个池。</summary>
    public RelayCommand CreatePoolCommand { get; private set; } = null!;

    /// <summary>重新读池列表。</summary>
    public RelayCommand RefreshPoolsCommand { get; private set; } = null!;

    /// <summary>账号页上「回到默认池」那个按钮。</summary>
    public RelayCommand BackToDefaultPoolCommand { get; private set; } = null!;

    /// <summary>分池页顶部的说明（一般是一句当前状态）。</summary>
    public string PoolsSummary { get; private set; } = "";

    /// <summary>当前正在「管理账号」的那个池（账号页会显示它的名字）。</summary>
    public PoolRowVm? SelectedPool { get; private set; }

    public bool HasSelectedPool => SelectedPool is not null;

    /// <summary>账号页顶部那行：现在看的是哪个池的账号。</summary>
    public string CurrentPoolLabel =>
        SelectedPool is null ? "默认池" : SelectedPool.DisplayName;

    /// <summary>非默认池时界面会显示「回到默认池」按钮。</summary>
    public bool ShowBackToDefaultPool => SelectedPool is not null && !SelectedPool.IsDefault;

    internal string? AuthFileOrNull => string.IsNullOrWhiteSpace(AuthFile) ? null : AuthFile;

    /// <summary>
    /// 跑一条 accounts 命令，作用在**当前选中的池**上。
    ///
    /// 这是整个分池功能的接缝：命令本身和以前一模一样，
    /// 只多带一个 WORKBUDDY_API_HOME —— node 那边于是读另一份账号库。
    /// 没选中池（或选的是默认池）时 home 为 null，行为和分池功能之前完全一致。
    /// </summary>
    internal Task<CliResult> RunPoolCliAsync(
        string command, string? stdin = null, CancellationToken ct = default)
    {
        var home = SelectedPool is { IsDefault: false } pool ? pool.Config.VaultHome() : null;

        // 用**解析后**的绝对路径，不是 NodePath / ApiDirectory 原始值。
        //
        // 两者在「用户填的是相对路径」时会分岔：
        //   NodePath        可能是 ".\runtime\node.exe"（发布版的默认写法）
        //   ResolvedNodePath 是 "<exe目录>\runtime\node.exe"
        //
        // CliRunner 会把工作目录切成 ApiDirectory，于是那个相对路径就成了
        //   <ApiDirectory>\runtime\node.exe
        // ——多套一层，必然找不到。实测就是这么挂的：
        // 用户点了「恢复默认设置」，之后所有账号操作全部报
        //   "An error occurred trying to start process '.\runtime\node.exe'"。
        var node = ResolvedNodePath ?? NodePath;
        var api = ResolvedApiDirectory ?? ApiDirectory;
        return CliRunner.RunAsync(node, api, command, stdin, ct, home);
    }

    /// <summary>没有任何池时显示提示（理论上不会：默认池恒在）。</summary>
    public bool HasNoPools => Pools.Count == 0;

    /// <summary>把池配置读进界面。启动时调一次。</summary>
    internal void InitialisePools()
    {
        CreatePoolCommand = new RelayCommand(CreatePool);
        RefreshPoolsCommand = new RelayCommand(RefreshPools);
        BackToDefaultPoolCommand = new RelayCommand(BackToDefaultPool);
        RefreshPools();
    }

    /// <summary>重新读池列表并绑定到界面。</summary>
    public void RefreshPools()
    {
        RunOnUiThread(() =>
        {
            var configs = PoolManagerRef.Pools.ToList();

            Pools.Clear();
            foreach (var cfg in configs)
            {
                Pools.Add(new PoolRowVm(this, cfg, PoolManagerRef.RuntimeOf(cfg)));
            }

            var running = Pools.Count(p => p.IsRunning);
            var total = 0;
            foreach (var p in Pools) if (p.AccountCount > 0) total += p.AccountCount;

            PoolsSummary = Pools.Count <= 1
                ? "现在只有一个池。要给朋友单独一套账号，就点「新建池」。"
                : $"{Pools.Count} 个池，{running} 个在运行，共 {total} 个账号";

            Raise(nameof(PoolsSummary));
            Raise(nameof(HasNoPools));
            Raise(nameof(CurrentPoolLabel));
            Raise(nameof(HasSelectedPool));
            Raise(nameof(ShowBackToDefaultPool));
        });
    }

    /// <summary>切到某个池的账号列表。</summary>
    internal void SelectPoolForAccounts(PoolRowVm pool)
    {
        SelectedPool = pool;
        Raise(nameof(CurrentPoolLabel));
        Raise(nameof(HasSelectedPool));
        Raise(nameof(ShowBackToDefaultPool));
        _ = RefreshAccountsAsync(fast: true);
        FlashStatus($"正在显示「{pool.DisplayName}」的账号");
    }

    /// <summary>回到默认池的账号列表。</summary>
    public void BackToDefaultPool()
    {
        SelectedPool = null;
        Raise(nameof(CurrentPoolLabel));
        Raise(nameof(HasSelectedPool));
        Raise(nameof(ShowBackToDefaultPool));
        _ = RefreshAccountsAsync(fast: true);
        FlashStatus("已回到默认池");
    }

    /// <summary>新建一个池。</summary>
    public void CreatePool()
    {
        var dialog = new PoolEditDialog("", null, PoolStore.FindFreePort())
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var pool = PoolManagerRef.Create(dialog.PoolName, dialog.PoolNote, dialog.PoolPort);
            RefreshPools();
            FlashStatus($"已新建「{pool.DisplayName}」（端口 {pool.Port}）—— 接着把账号加进去");
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    /// <summary>启动所有标了「自动启动」的池（程序启动时调）。</summary>
    public async Task AutoStartPoolsAsync()
    {
        var node = ResolvedNodePath;
        var api = ResolvedApiDirectory;
        if (node is null || api is null) return;

        foreach (var cfg in PoolManagerRef.Pools.Where(p => p.AutoStart).ToList())
        {
            try
            {
                // 默认池就是主页那个网关，它归 StartGatewayCoreAsync 管。
                //
                // 这里必须跳过 —— 不能靠「IsGatewayRunning」判断，因为启动时
                // 那一位是 concurrent 跑的，到这里可能还没写上；真的再起一个
                // 会撞上自己已经占着的端口（node 退出码 1）。
                if (cfg.IsDefault) continue;

                await PoolManagerRef.StartAsync(cfg, node, api, Provider, AuthFileOrNull);
                AppendLog($"[分池] 自动启动「{cfg.DisplayName}」→ 端口 {cfg.Port}");
            }
            catch (Exception ex)
            {
                AppendLog($"[分池] 「{cfg.DisplayName}」自动启动失败：{ex.Message}");
            }
        }
        RefreshPools();
    }

    /// <summary>
    /// 把主编那个网关实例登记给默认池。
    ///
    /// 为什么不让分池页自己起一个默认池实例：
    ///   1. 聊天页 / 状态 / 模型列表全挂在主编的 _gateway 上，
    ///      分池页另起一个会多占一个端口，而且那个实例没人用
    ///   2. 两个实例各读一份账号，改了一个另一个不知道
    ///
    /// 登记之后，两边看到的是同一个进程、同一个端口、同一套账号。
    ///
    /// @param externalRunning 网关在跑但不是本进程起的（端口上已有我们的网关）
    /// </summary>
    internal void AdoptGatewayIntoDefaultPool(bool externalRunning = false, string externalUrl = "")
    {
        try
        {
            // 默认池的端口跟主设置保持一致 —— 用户在「设置」里改了端口，
            // 分池页显示的应该是改后的值，而不是旧的 8787
            var def = PoolManagerRef.Default;
            if (int.TryParse(Port, out var p) && p is > 0 and <= 65535 && def.Port != p)
            {
                def.Port = p;
                PoolManagerRef.Save();
            }

            var rt = PoolManagerRef.AdoptDefault(_gateway);
            if (externalRunning) rt.MarkExternalRunning(externalUrl);
            else rt.ClearExternalRunning();

            RefreshPools();
        }
        catch (Exception ex)
        {
            AppendLog($"[分池] 登记默认池失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把默认池的端口写回主页的设置（settings.json）。
    ///
    /// 为什么需要：默认池的端口**存在两个地方** ——
    ///   pools.json    分池页上显示的那个数字
    ///   settings.json 真正启动网关时用的那个数字
    ///
    /// 在分池页改了默认池端口，如果只写 pools.json，就会出现
    /// 「分池页写着 8788、网关实际在 8787」——实测踩过。
    /// 这里保证两边一致。
    /// </summary>
    internal void SyncDefaultPoolPort(int port)
    {
        var text = port.ToString(CultureInfo.InvariantCulture);
        if (Port == text) return;
        Port = text;
        SaveSettings();
        AppendLog($"[分池] 默认池端口同步为 {port}");
    }

    /// <summary>默认池的实例起来了 —— 同步主页那份状态。</summary>
    internal void OnDefaultPoolStarted(string baseUrl)
    {
        RunOnUiThread(() =>
        {
            IsGatewayRunning = true;
            if (!string.IsNullOrWhiteSpace(baseUrl)) BaseUrl = baseUrl;
        });
    }

    internal void OnDefaultPoolStopped()
    {
        RunOnUiThread(() => IsGatewayRunning = false);
    }
    /// <summary>复制文本到剪贴板（带重试，见 ClipboardHelper）。</summary>
    internal void CopyTextToClipboard(string text, string message)
    {
        if (ClipboardHelper.TrySetText(text)) FlashStatus(message);
        else FlashStatus("复制失败，请手动选中再复制");
    }

    // ── 本机地址探测：给「局域网 URL」用 ─────────────────────────

    private static string? DkIpCache;
    private static string? TailscaleIpCache;
    private static bool IpProbed;

    /// <summary>
    /// 本机在局域网里的 IPv4。
    ///
    /// 用「连一下外网看内核选了哪张网卡」这个技巧 —— 不会真的发包，
    /// 只是让路由表告诉我们出口地址。比枚举网卡再猜哪个是主网卡可靠得多。
    /// </summary>
    public static string? LocalIpv4()
    {
        ProbeIps();
        return DkIpCache;
    }

    /// <summary>Tailscale 的 100.x 地址（没装或没连就是 null）。</summary>
    public static string? TailscaleIpv4()
    {
        ProbeIps();
        return TailscaleIpCache;
    }

    private static void ProbeIps()
    {
        if (IpProbed) return;
        IpProbed = true;
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var ip = addr.Address.ToString();

                    // Tailscale 固定用 100.64.0.0/10（CGNAT 段）
                    if (ip.StartsWith("100."))
                    {
                        var parts = ip.Split('.');
                        if (parts.Length == 4 && int.TryParse(parts[1], out var second) && second is >= 64 and <= 127)
                        {
                            TailscaleIpCache ??= ip;
                            continue;
                        }
                    }

                    // 局域网地址：只认 192.168 / 10. / 172.16-31
                    if (ip.StartsWith("192.168.") || ip.StartsWith("10."))
                        DkIpCache ??= ip;
                    else if (ip.StartsWith("172."))
                    {
                        var parts = ip.Split('.');
                        if (parts.Length == 4 && int.TryParse(parts[1], out var second2) && second2 is >= 16 and <= 31)
                            DkIpCache ??= ip;
                    }
                }
            }
        }
        catch
        {
            // 探不到就算了，局域网 URL 会退回 127.0.0.1
        }
    }
}
