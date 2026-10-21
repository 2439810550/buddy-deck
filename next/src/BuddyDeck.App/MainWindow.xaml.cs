using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BuddyDeck.App.Pages;
using BuddyDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BuddyDeck.App;

/// <summary>
/// 主窗口：左侧常驻导航 + 内容区。
///
/// 窗口本身不做数据获取 —— 它在加载时让总览页探一次网关、拉一次账号，
/// 把结果写回外壳（导航栏底部的状态）。所有 HTTP 都在 ViewModel 里。
///
/// ## 标题栏与托盘（t80）
///
/// · **标题栏**：交给 Windows 原生（**不设** `ExtendsContentIntoTitleBar`）。
///   用户实测点名要的就是「Windows 原生的边框最小化最大化关闭」——
///   自绘要自己处理拖动、双击最大化、多显示器 DPI、最大化避让任务栏，
///   这些原生行为免费且不会错。原先为了 Mica 那一片连通观感去掉了它，
///   代价是**窗口没法关**。观感的损失远小于功能的损失。
///
/// · **托盘**：见 <see cref="TrayIcon"/>。关闭窗口默认收进托盘（老版行为），
///   托盘菜单里可切回「关闭即退出」。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly DashboardViewModel _dashboard;

    /// <summary>导航项 → 页面实例。第一次点某页才创建，之后复用（保住页面上的输入）。</summary>
    private readonly Dictionary<ShellViewModel.PageKey, System.Windows.Controls.UserControl> _pages = [];

    /// <summary>托盘图标。窗口关闭时由 <see cref="OnClosing"/> 决定收进去还是退出。</summary>
    private TrayIcon? _tray;

    /// <summary>
    /// 「真的退出」意图。
    ///
    /// 为什么需要这个标志：托盘菜单点「退出」也要走 `Close()`，
    /// 而 `OnClosing` 默认会把关闭变成"收进托盘" —— 那会导致**点了退出也关不掉**。
    /// 所以退出路径先把这个标志置 true，`OnClosing` 据此放行。
    ///
    /// 这是"关闭/退出"这类双语义最经典的坑：一个 `Close()` 两个意思，
    /// 必须有个显式状态区分，否则总有一边坏掉。
    /// </summary>
    private bool _exiting;

    /// <summary>探针模式下不建托盘 —— 截图进程不该在通知区留图标。</summary>
    private static bool IsProbeMode { get; set; }

    /// <summary>
    /// 实时监控定时器：周期探活，检测网关掉线/异常。
    ///
    /// 为什么需要它：网关子进程可能因为上游抽风、端口被抢、崩溃而掉线，
    /// 而用户不会一直盯着看。定时探活让左侧状态区**实时**反映真相，
    /// 掉线时在状态区给出明确提示（而不是等用户去设置页才发现）。
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _healthTimer;

    public MainWindow()
    {
        // 视图不 new 服务 —— 从容器要。换实现（真实网关 / 未连接实现）不用改界面。
        _shell = App.Services.GetRequiredService<ShellViewModel>();
        _dashboard = App.Services.GetRequiredService<DashboardViewModel>();

        InitializeComponent();
        DataContext = _shell;

        // ★ 任务库：挂接 + 开始轮询（后台任务进度实时更新）。
        var tasks = App.Services.GetRequiredService<TasksViewModel>();
        _shell.AttachTasks(tasks);
        tasks.StartPolling();

        // ⚠ **必须在代码里显式关掉** `ExtendsContentIntoTitleBar`。
        //
        // 只在 XAML 里不写它**不够** —— `Wpf.Ui.Controls.FluentWindow`
        // 的**默认值是 `true`**，于是：
        //   · Win32 样式位仍带着 WS_CAPTION / WS_MINIMIZEBOX / WS_MAXIMIZEBOX
        //     （所以读样式位会误判成"按钮都在"）
        //   · 但非客户区高度被压成 **0 px** —— 标题栏和那三个按钮**根本画不出来**
        // 实测证据（t80 收尾）：
        //   窗口 1234x820  客户区 1234x820  ⇒ 非客户区高度 = 0
        //   窗口位置曾被最小化坐标 (-25600,-25600) 掩盖，让人误以为"按钮不存在"
        //
        // ⇒ 设 false 之后 Windows 会自己画标题栏，最小化/最大化/关闭全部回来。
        //   代价：Mica 背景不可用（WPF-UI 要求它为 true 才能设 backdrop），
        //   背景退回 design 的 InkBrush —— 本就是不透明深色，观感损失很小。
        //   用户的判断是对的：一个关不掉的窗口，谈不上"能用"。

        // ⚠ **光设属性不够 —— 实测证明它会被覆盖**。
        //
        // 设了 `ExtendsContentIntoTitleBar = false` 之后重新编译启动，
        // 非客户区高度**仍然是 0**（用 tools/check_window_chrome.py 实测）：
        //     窗口 1220x780  客户区 1220x780  ⇒ 非客户区 0px
        //     但样式位里 WS_CAPTION / MINIMIZEBOX / MAXIMIZEBOX 全在
        // ⇒ `FluentWindow` 在 OnSourceInitialized（句柄创建之后）自己重设了窗口样式，
        //   把 WPF 的属性值盖掉了。**读样式位看不出来**（它本来就带着那几位），
        //   只有量非客户区高度才暴露。
        //
        // ⇒ 所以必须在**句柄创建之后**再强制加一次 Win32 样式。
        SourceInitialized += OnSourceInitializedForChrome;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>
    /// 句柄创建后强制把原生标题栏加回来。
    ///
    /// 为什么在这个时点：`SourceInitialized` 是 WPF 拿到 HWND 之后、
    /// `FluentWindow` 改完样式之后 —— 我们在这里补最后一道。
    /// </summary>
    private void OnSourceInitializedForChrome(object? sender, EventArgs e)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        const int GWL_STYLE = -16;
        const int WS_CAPTION = 0x00C00000;
        const int WS_SYSMENU = 0x00080000;   // 系统菜单（Alt+Space + 关闭）
        const int WS_MINIMIZEBOX = 0x00020000;
        const int WS_MAXIMIZEBOX = 0x00010000;
        const int WS_THICKFRAME = 0x00040000; // 可缩放边框

        var style = NativeMethods.GetWindowLongPtr(handle, GWL_STYLE).ToInt64();
        var wanted = style | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_THICKFRAME;

        if (wanted != style)
        {
            NativeMethods.SetWindowLongPtr(handle, GWL_STYLE, new IntPtr(wanted));

            // ⚠ 改完样式必须走一次 SetWindowPos(SWP_FRAMECHANGED)，
            //   否则非客户区**不会重算**，高度还是 0 —— 改了等于没改。
            NativeMethods.SetWindowPos(
                handle, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOMOVE
                | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_shell.Navigation.Count > 0) NavigateTo(_shell.Navigation[0]);

        // ★ 先同步一次「子进程在不在跑」（同步读，不等网络）——
        //   网关在 App.OnStartup 就后台拉起了，但按钮的 GatewayRunning
        //   默认是 false，首帧会错显示成「未启动」的 ▶。
        //   这里在探活之前先纠正，探活完成后再由 ProbeGatewayAsync 更新一次。
        _shell.SyncGatewayRunning();

        await ProbeGatewayAsync();
        _initialLoadDone.TrySetResult(true);

        // ★ 实时监控：每 5 秒探一次活，掉线/异常在左侧状态区提醒。
        //   探针模式不启动（截图进程跑完就退，不需要轮询）。
        if (!IsProbeMode)
        {
            _healthTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            _healthTimer.Tick += async (_, _) => await HealthTickAsync();
            _healthTimer.Start();
        }
    }

    /// <summary>
    /// 周期探活的一次心跳。
    ///
    /// 掉线检测：从「在线」变「离线」时给一条明确的提醒（不是每次都吼），
    /// 避免状态区一直被错误刷屏。恢复在线时清掉提醒。
    /// </summary>
    private async Task HealthTickAsync()
    {
        var health = await _dashboard.ProbeAsync().ConfigureAwait(true);

        _shell.GatewayOnline = health.Online;
        _shell.GatewayStale = health.Stale;
        _shell.ModelCount = health.Online ? health.Models : 0;
        _shell.StatusLine = health.Online ? "网关已启动" : "网关未启动";
        _shell.GatewayDetail = health.Online
            ? $"127.0.0.1:{health.Port} · {health.Provider}"
            : "";
        _shell.OfflineReason = health.Online ? "" : (health.Error ?? "");
        _shell.SyncGatewayRunning();


        // 在线时也顺带刷新一次账号状态轨（账号可能被导入/删除）。
        if (health.Online)
        {
            var accounts = await _dashboard.LoadAccountsForRailAsync().ConfigureAwait(true);
            _shell.UpdateRail(accounts, DateTimeOffset.UtcNow);
        }

        UpdateTrayStatus();
    }

    // ========================================================================
    // 托盘
    // ========================================================================

    /// <summary>
    /// 建托盘图标。**探针模式不建。**
    ///
    /// 为什么探针不建：`--probe` 是个跑完就退的截图进程，
    /// 在通知区留一个图标（哪怕几十毫秒）会让"托盘图标没被清理"看起来像 bug。
    /// </summary>
    public void SetupTray()
    {
        if (_tray is not null) return;
        if (IsProbeMode) return;

        _tray = new TrayIcon(
            onOpen: () => Dispatcher.Invoke(RestoreFromTray),
            onExit: () => Dispatcher.Invoke(ExitApplication));

        UpdateTrayStatus();
    }

    /// <summary>把窗口从托盘里拿回来。</summary>
    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();

        if (_tray is not null) _tray.OpenEnabled = false;
    }

    /// <summary>真的退出（托盘菜单点的）。</summary>
    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    /// <summary>
    /// 更新托盘悬停提示。
    ///
    /// **长度由 <see cref="TrayIcon.SetStatus"/> 负责截断**（WinForms 硬上限 63 字符），
    /// 这里只管把要说的拼出来。
    /// </summary>
    public void UpdateTrayStatus()
    {
        if (_tray is null) return;

        var status = _shell.GatewayOnline
            ? $"网关 {_shell.StatusLine} · 可用账号 {_shell.RailUsable}/{_shell.RailTotal}"
            : "BuddyDeck · 网关未启动";

        _tray.SetStatus(status);
    }

    /// <summary>
    /// 窗口关闭时：收进托盘还是真退出。
    ///
    /// **默认收进托盘** —— 那是老版的行为，也是用户点名要的
    /// （「点关闭收进系统托盘工作」）。
    ///
    /// ⚠ 两个必须处理的边界：
    ///   · `_exiting`（托盘菜单点的退出）→ 放行，否则**点了退出也关不掉**
    ///   · **没有托盘时**（探针模式 / 托盘创建失败）→ 也要放行，
    ///     否则窗口关不掉，进程卡在后台 —— 那比不实现托盘更糟
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;

        if (_tray is null || !_tray.MinimizeToTray)
        {
            // 没有"收进托盘"这条退路 → 就让它正常退出。
            // 绝不能拦下这次关闭却又不提供别的出口。
            _tray?.Dispose();
            _tray = null;
            return;
        }

        e.Cancel = true;
        Hide();
        if (_tray is not null) _tray.OpenEnabled = true;
    }

    /// <summary>
    /// 「首次探活 + 拉账号」完成。
    ///
    /// 为什么需要它：`--probe` 在 `window.Show()` 之后立刻就截图，
    /// 而 <see cref="OnLoaded"/> 里的探活是异步的 —— 两者**竞争**。
    /// 结果是截图拍到的是**数据还没到**的空状态，看起来像绑定坏了：
    /// 状态轨一条竖条都没有、底部数字是空的，只剩「可用」「个号」两个标签
    /// 孤零零竖排着，很像"布局错位"。
    ///
    /// 这正是我一开始误判的方向 —— 我以为是 DockPanel/宽度算错，
    /// 实际是**截图比数据先到**。探针等这个 Task 就不会再拍到空的。
    /// </summary>
    private readonly TaskCompletionSource<bool> _initialLoadDone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>探针用：等首次数据加载完成（最多等 <paramref name="timeout"/>）。</summary>
    public Task WaitForInitialLoadAsync(TimeSpan timeout)
        => Task.WhenAny(_initialLoadDone.Task, Task.Delay(timeout));

    /// <summary>按导航项跳页。</summary>
    public void NavigateTo(NavigationEntry? entry)
    {
        if (entry is null) return;

        _shell.Current = entry.Key;

        if (!_pages.TryGetValue(entry.Key, out var page))
        {
            page = CreatePage(entry.Key);
            _pages[entry.Key] = page;
        }

        ContentHost.Content = page;
    }

    /// <summary>按 key 跳页 —— 页面之间互相跳转用这个（页面不该知道导航项怎么排）。</summary>
    public void NavigateTo(ShellViewModel.PageKey key)
        => NavigateTo(_shell.Navigation.FirstOrDefault(n => n.Key == key));

    private System.Windows.Controls.UserControl CreatePage(ShellViewModel.PageKey key) => key switch
    {
        ShellViewModel.PageKey.Dashboard => new DashboardPage(),
        ShellViewModel.PageKey.Vault => new VaultPage(),
        ShellViewModel.PageKey.Chat => new ChatPage(),
        ShellViewModel.PageKey.Stats => new StatsPage(),
        ShellViewModel.PageKey.Logs => new LogsPage(),
        _ => new SettingsPage(),
    };

    private void OnNavItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: NavigationEntry entry }) NavigateTo(entry);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ProbeGatewayAsync();

    /// <summary>顶栏「任务库」入口：**只打开，不切换**。</summary>
    private void OnToggleTasks(object sender, RoutedEventArgs e)
    {
        // ⚠ 不是 Toggle！任务运行中面板会自动展开（IsOpen=true），
        // 用户点「任务库」想查看，如果用 Toggle 会把面板翻转成收起——
        // 然后任务完成后面板就「自己没了」。这里只负责打开，
        // 收起交给遮罩（点任务库外面）。
        if (_shell.Tasks is { } tasks && !tasks.IsOpen)
        {
            tasks.IsOpen = true;
        }
    }

    /// <summary>点任务库外的遮罩：收起任务库。</summary>
    private void OnDismissTasksOverlay(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 点任务库外面就收起 —— 任务在跑也能关（用户想专注看内容区）。
        if (_shell.Tasks is { } tasks && tasks.IsOpen)
        {
            tasks.IsOpen = false;
        }
    }

    /// <summary>点任务行：展开/收起该任务的事件流详情。</summary>
    private async void OnTaskRowClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskRowViewModel row } && _shell.Tasks is { } tasks)
        {
            await tasks.ToggleDetailAsync(row).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 探一次网关并拉一次账号列表，把结果写到顶栏和状态轨。
    /// 公开 —— 网关子进程就绪/失败后（App 的 StateChanged 回调）要重新探。
    /// </summary>
    public async Task ProbeGatewayAsync()
    {
        var health = await _dashboard.ProbeAsync().ConfigureAwait(true);

        _shell.GatewayOnline = health.Online;
        _shell.GatewayStale = health.Stale;
        _shell.ModelCount = health.Online ? health.Models : 0;
        _shell.StatusLine = health.Online ? "网关已启动" : "网关未启动";
        _shell.GatewayDetail = health.Online
            ? $"127.0.0.1:{health.Port} · {health.Provider}"
            : "";
        _shell.OfflineReason = health.Online ? "" : (health.Error ?? "");
        _shell.SyncGatewayRunning();


        var accounts = await _dashboard.LoadAccountsForRailAsync().ConfigureAwait(true);
        _shell.UpdateRail(accounts, DateTimeOffset.UtcNow);

        // 托盘悬停提示跟着状态走 —— 用户把鼠标停在托盘图标上就该知道网关在不在。
        UpdateTrayStatus();
    }

    /// <summary>
    /// 由 <c>App.RunProbe</c> 调用：告诉窗口"这是探针进程"。
    ///
    /// 探针不建托盘（截图进程不该在通知区留东西），但**其它行为一致** ——
    /// 截图要拍到真实布局。
    /// </summary>
    public static void MarkProbeMode() => IsProbeMode = true;

    /// <summary>
    /// 等某一页的**异步数据**落定。
    ///
    /// ## 为什么需要它（实测踩过）
    ///
    /// 用户反馈「账号页没有积分余额、没有模型限流面板」——
    /// 而实际上那两块都是「慢接口」：积分要逐个账号向上游查，
    /// 模型矩阵要枚举 32 个模型 × N 个号。
    ///
    /// 截图原来只等 600ms ⇒ 拍到的是「只有账号列表」的中间态 ⇒
    /// **看起来像功能没做，实际是截图太早**。
    ///
    /// ⇒ 等法：轮询「该页是否还有未完成的加载」，直到全部落定或超时。
    ///   **超时也不能失败** —— 网关慢/离线时截图仍该出图（拍到空态），
    ///   那也是一张有效的证据（证明空态文案正确）。
    /// </summary>
    private async Task WaitForPageDataAsync(ShellViewModel.PageKey page)
    {
        // 上限 20 秒：够慢接口跑完，又不至于让 CI 卡死。
        // 为什么给这么长：20 个账号的积分是**逐个**查的，真机实测要十几秒。
        const int maxWaitMs = 20000;
        const int stepMs = 250;

        // ⚠ 诊断：把等待情况写到**输出目录**。
        //
        // 为什么写文件而不是 Debug.WriteLine：探针是**独立进程且无控制台**，
        // Debug 输出根本没人收得到 —— 这正是"加了日志却查不出问题"的经典原因。
        var diag = Path.Combine(
            Environment.GetEnvironmentVariable("BUDDYDECK_PROBE_DIR") ?? ".",
            $"probe-wait-{page}.txt");

        for (var waited = 0; waited < maxWaitMs; waited += stepMs)
        {
            var found = _pages.TryGetValue(page, out var p);
            var isVault = p is VaultPage;
            var settled = isVault && ((VaultPage)p!).DataSettled;

            var done = page switch
            {
                ShellViewModel.PageKey.Vault => settled,
                _ => true,
            };

            if (done)
            {
                var detail = p is VaultPage apx
                    ? $" AccountsLoaded={apx.Vm.Accounts.AccountsLoaded} CreditsLoaded={apx.Vm.Accounts.CreditsLoaded} " +
                      $"HealthLoaded={apx.Vm.Accounts.HealthLoaded} Rows={apx.Vm.Accounts.Rows.Count} " +
                      $"Problems={apx.Vm.Accounts.ProblemModels.Count} " +
                      $"WithCredits={apx.Vm.Accounts.Rows.Count(r => r.HasCredits)} " +
                      $"PoolTotal=\"{apx.Vm.Accounts.PoolCreditTotal}\""
                    : " (非账号池页)";
                TryWriteDiag(diag, $"page={page} 等了 {waited}ms 数据落定。{detail}");
                return;
            }

            await Task.Delay(stepMs).ConfigureAwait(true);
        }

        TryWriteDiag(diag, $"page={page} **超时** {maxWaitMs}ms —— 截图会拍到未加载完的状态");
    }

    /// <summary>诊断写文件（失败不影响主流程）。</summary>
    private static void TryWriteDiag(string path, string text)
    {
        try { File.WriteAllText(path, text, new System.Text.UTF8Encoding(false)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 给 <c>--probe</c> 用的：把窗口渲染成 PNG。
    ///
    /// 为什么内置而不是外面截图：无桌面会话下也能出图，而且截出来的就是
    /// 这个窗口的客户区，不带任务栏和别的窗口。正常启动完全不碰这条路径。
    /// </summary>
    /// <param name="path">PNG 落到哪。</param>
    /// <param name="page">先切到哪一页。</param>
    /// <param name="driveChat">
    /// 截图前是否**真的发一条聊天请求**。
    ///
    /// 为什么需要它（t62）：聊天页的媒体渲染只有在「收到带媒体的回复」之后才出现。
    /// 只切页的话那一页永远是空态 —— 截图拍不到要验收的东西。
    /// 这个开关让探针能把它跑起来，且**只在探针路径生效**，正常启动不碰。
    /// </param>
    public async Task<bool> SaveScreenshotAsync(
        string path,
        ShellViewModel.PageKey page,
        bool driveChat = false)
    {
        NavigateTo(page);
        await Task.Delay(600).ConfigureAwait(true);   // 等布局和主题画刷落定

        if (driveChat)
        {
            await DriveChatAsync().ConfigureAwait(true);
        }

        // ⚠ 等到**异步拉取的数据真的落定**再截。
        //
        // 实测踩过（用户反馈「账号页没有积分、没有模型限流面板」）：
        // 那一页的三块数据是**分别异步拉的** ——
        //   ① 账号列表（快，本地保险库）
        //   ② 积分（**慢**：要逐个账号向上游查）
        //   ③ 模型健康矩阵（**慢**：32 个模型 × N 个号）
        //
        // 而原来这里只等 600ms ⇒ 截图拍到的是「只有第 ① 块」的中间态，
        // 看起来像「功能没做」，实际是**截图太早**。
        //
        // ⇒ 探针必须等数据落定。这不是"让测试通过"，而是
        //   **让截图忠实地反映"用户看到的样子"** —— 否则截图作为证据是假的。
        await WaitForPageDataAsync(page).ConfigureAwait(true);

        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        var width = (int)Math.Ceiling(ActualWidth);
        var height = (int)Math.Ceiling(ActualHeight);
        if (width <= 0 || height <= 0) return false;

        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(this);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = File.Create(path);
        encoder.Save(fs);
        return true;
    }

    /// <summary>
    /// 探针专用：在聊天页发一条请求并等媒体加载完。
    ///
    /// 为什么不复用页面自己的按钮：那个按钮的 IsEnabled 绑在 `CanSend` 上，
    /// 需要一个已选模型 + 非空输入。这里直接驱动 ViewModel，少一层偶合。
    /// </summary>
    private async Task DriveChatAsync()
    {
        var chat = App.Services.GetRequiredService<ChatViewModel>();

        // 模型列表可能还没拉回来 —— 等一小会儿，拿了再走
        for (var i = 0; i < 40 && chat.Models.Count == 0; i++)
            await Task.Delay(100).ConfigureAwait(true);

        if (chat.SelectedModel is null && chat.Models.Count > 0)
            chat.SelectedModel = chat.Models[0];

        chat.Input = "画一张图";
        await chat.SendOrStopCommand.ExecuteAsync(null).ConfigureAwait(true);

        // 等媒体下载+解码（块从 IsLoading 变 Ready）。
        // 有超时兜底 —— 不能让探针永远卡在这儿。
        for (var i = 0; i < 100; i++)
        {
            var pending = chat.Blocks
                .OfType<Services.MediaBlock>()
                .Any(b => b.IsLoading);

            if (!pending && chat.Blocks.Count > 0) break;
            await Task.Delay(100).ConfigureAwait(true);
        }

        // 再给一帧让绑定刷上去
        await Task.Delay(300).ConfigureAwait(true);
    }
}

/// <summary>
/// 窗口原生样式的 P/Invoke。
///
/// 为什么不用 `WindowStyle` / `ResizeMode` 这些 WPF 属性：
/// WPF-UI 的 `FluentWindow` 在 `OnSourceInitialized` 里直接改 Win32 样式，
/// 属性层会被它盖掉 —— 实测过：设了 `ExtendsContentIntoTitleBar = false`
/// 仍然量到非客户区 0px。**只有在句柄创建之后直接改样式位才生效。**
/// </summary>
internal static class NativeMethods
{
    internal const int SWP_NOSIZE = 0x0001;
    internal const int SWP_NOMOVE = 0x0002;
    internal const int SWP_NOZORDER = 0x0004;
    internal const int SWP_FRAMECHANGED = 0x0020;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}