using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WorkBuddyPanel.Services;
using WorkBuddyPanel.ViewModels;

namespace WorkBuddyPanel;

public partial class App : Application
{
    /// <summary>
    /// 单实例锁的名字。
    ///
    /// 名字里带 exe 路径的哈希 —— 为什么：
    ///   绝对路径不同的两个实例应当能同时跑。
    ///   实际场景：用户在 D:\BuddyDeck 装着正式版（长期开着、网关挂在它身上），
    ///   同时又想跑一下刚编的新版试试 —— 固定名字的锁会让后者直接把前者
    ///   叫出来然后自己退出，根本测不了。
    ///
    /// 同一个 exe 双开仍然会被拦（那是原本要防的：两个实例抢同一个端口）。
    /// </summary>
    private static readonly string MutexName = BuildInstanceName("SingleInstance");

    private static readonly string ShowSignalName = BuildInstanceName("ShowWindow");

    private static string BuildInstanceName(string kind)
    {
        var path = Environment.ProcessPath ?? "default";
        var hash = 0;
        foreach (var ch in path.ToLowerInvariant())
            hash = (hash * 31 + ch) & 0x7FFFFFFF;
        return string.Format(@"Local\WorkBuddyPanel.{0}.{1:X}", kind, hash);
    }

    private MainWindow? _window;
    private TrayIcon? _tray;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;

    /// <summary>正在退出。窗口的 Closing 看到这个就不再加拦截，否则会退不掉。</summary>
    public static bool IsExiting { get; private set; }

    /// <summary>本次是以 --tray 启动的（开机自启走这条路），界面不弹出来。</summary>
    public static bool StartHidden { get; private set; }

    /// <summary>
    /// 本次是以 --maintain 启动的：不弹窗、不停留，只续期+签到就退出。
    /// 每日计划任务跑的就是这个。
    /// </summary>
    public static bool MaintainOnly { get; private set; }

    /// <summary>托盘图标，窗口需要它来提示「已收进托盘」。</summary>
    public TrayIcon? Tray => _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var text = args.ExceptionObject?.ToString() ?? "未知错误";
            // 先落盘再弹窗：用户随手关掉弹窗后，还能从日志里挖出来
            MainViewModel.WriteMaintenanceLog("[未处理异常] " + text);
            MessageBox.Show(
                text,
                "WorkBuddy 控制台 — 发生了未处理的错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };

        StartHidden = e.Args.Any(arg => arg.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        MaintainOnly = e.Args.Any(arg => arg.Equals("--maintain", StringComparison.OrdinalIgnoreCase));

        // 维护模式必须在单实例检查**之前**拦下来。
        //
        // 否则面板开着的时候（托盘常驻，所以大部分时间都开着），计划任务触发的
        // --maintain 会被「已有实例在跑」挡掉：它只是把面板窗口叫出来，然后自己退，
        // **当天维护静默跳过，退出码还是 0**。
        // 这个坑真踩到了 —— 现在仍然保留了第二层保险（已有实例会补跑维护）。
        if (MaintainOnly)
        {
            // 维护模式也校验授权。
            //
            // 为什么：不校验的话，删掉授权文件后计划任务照样每天续期签到 ——
            // 那是我们自己的账号，等于给人白用工信。
            //
            // 但失败时**静默退出（退出码 1）**，不弹窗：
            // 这是计划任务拉起来的，屏幕前没人，弹窗只会挂着不走。
            var gate = LicenseGuard.Verify();
            if (!gate.Ok)
            {
                MainViewModel.WriteMaintenanceLog(
                    $"[授权] {gate.FailReason}：{gate.Message.Replace(Environment.NewLine, " ")}");
                Shutdown(1);
                return;
            }

            RunMaintenanceOnly();
            return;
        }

        // 授权校验：插在 --maintain 之后、单实例之前。
        //
        // 时机考虑：
        //   在单实例之前 —— 没授权就别抢那个互斥量，否则第二次双击的行为会很怪
        //                        （没授权的实例把已授权的窗口叫出来）
        //   在 --maintain 之后 —— 那个分支自己处理，见上面
        var license = LicenseGuard.Verify();
        if (!license.Ok)
        {
            // 弹激活窗：告诉用户机器码 + 文件放哪 + 可重试
            var activation = new ActivationWindow(license);
            if (activation.ShowDialog() != true)
            {
                Shutdown(1);
                return;
            }
            license = activation.Passed!;
        }

        MainViewModel.WriteMaintenanceLog(
            $"[授权] 通过 —— 授权给 {license.CustomerName}，到期 " +
            (license.ExpiresAt is { } exp && exp != default ? $"{exp:yyyy-MM-dd}" : "永久"));

        // 单实例：程序大部分时间躲在托盘里，用户很容易忘了它还在，
        // 再双击一次 exe 就会撞端口。这里让第二个实例把第一个的窗口叫出来，然后自己退。
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            AskRunningInstanceToShow();
            Shutdown();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        ListenForShowRequests();

        _window = new MainWindow();

        // 维护模式不停留、不需要托盘，也不建托盘图标（免得闪一下）
        if (!MaintainOnly)
        {
            _tray = new TrayIcon(
                onOpenPanel: () => _window!.ShowPanel(),
                onStartGateway: () => _window!.ViewModel.StartGatewayCommand.Execute(null),
                onStopGateway: () => _window!.ViewModel.StopGatewayCommand.Execute(null),
                onExit: ExitApp,
                isGatewayRunning: () => _window?.ViewModel.IsGatewayRunning ?? false,
                canStartGateway: () => _window?.ViewModel.CanRunNode ?? false,
                isAutoStartEnabled: AutoStart.IsEnabled,
                setAutoStart: SetAutoStart,
                isMinimizeToTray: () => _window?.ViewModel.MinimizeToTray ?? true,
                setMinimizeToTray: value =>
                {
                    if (_window is not null) _window.ViewModel.MinimizeToTray = value;
                });

            // 网关启停时同步托盘提示文字
            _window.ViewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ViewModels.MainViewModel.IsGatewayRunning))
                    _tray?.UpdateState(_window.ViewModel.IsGatewayRunning);
            };
        }

        if (!StartHidden && !MaintainOnly) _window.Show();

        // 启动流程的异常不能丢。
        //
        // 以前是 `_ = _window.StartAsync(...)` —— fire-and-forget，
        // InitializeAsync 里任何异常都被静默吞掉。
        //
        // 表现很迷惑：窗口出来了，但账号列表永远空着、日志页什么都没有 ——
        // 因为初始化在抛异常那一步就停了，而用户（和开发者）都看不到错。
        // 实测就是这么晴掉的：一个异常导致账号列表一直加载不出来。
        _ = StartAsyncSafely();
    }

    private async Task StartAsyncSafely()
    {
        try
        {
            await _window!.StartAsync(StartHidden || MaintainOnly);
        }
        catch (Exception ex)
        {
            // 记完整堆栈 —— 只记 Message 的话看不出是哪一行、哪个线程
            MainViewModel.WriteMaintenanceLog("[启动异常] " + ex);
            try { _window?.ViewModel.AppendExternalLog($"[启动] 失败：{ex.Message}\n{ex.StackTrace}"); } catch { }

            MessageBox.Show(
                $"程序启动时出错了：\n\n{ex.Message}\n\n" +
                "详情已经写进日志（「日志」标签页、或 %APPDATA%\\WorkBuddyPanel\\ui.log）。",
                "WorkBuddy 控制台 — 启动失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetAutoStart(bool enabled)
    {
        try
        {
            if (enabled) AutoStart.Enable();
            else AutoStart.Disable();
            _window?.ViewModel.NotifyAutoStartChanged(enabled);
        }
        catch (Exception ex)
        {
            _window?.ViewModel.NotifyAutoStartChanged(AutoStart.IsEnabled());
            MessageBox.Show(ex.Message, "WorkBuddy 控制台", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 只跑一次维护（续期 + 签到）然后退出。不建托盘、不弹窗口、不做单实例检查。
    /// 每日计划任务走的就是这条路。
    /// </summary>
    private async void RunMaintenanceOnly()
    {
        MainWindow? window = null;
        try
        {
            window = new MainWindow();
            await window.StartAsync(hidden: true);
        }
        catch (Exception ex)
        {
            MainViewModel.WriteMaintenanceLog($"维护异常：{ex.Message}");
        }
        finally
        {
            window?.Shutdown();
            ExitApp();
        }
    }

    /// <summary>已有实例在跑：请求它把窗口弹出来，不要自己再开一个。</summary>
    private static void AskRunningInstanceToShow()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
            signal.Set();
        }
        catch
        {
            // 对方刚好在退出，安静收场
        }
    }

    /// <summary>后台等「把窗口弹出来」的请求（别的实例双击发过来的）。</summary>
    private void ListenForShowRequests()
    {
        var signal = _showSignal;
        if (signal is null) return;

        var listener = new Thread(() =>
        {
            while (!IsExiting)
            {
                try
                {
                    if (!signal.WaitOne(500)) continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                Dispatcher.BeginInvoke(() => _window?.ShowPanel());
            }
        })
        {
            IsBackground = true,
            Name = "WorkBuddyPanel.ShowListener",
        };
        listener.Start();
    }

    /// <summary>真正的退出：收掉托盘、停掉网关、结束进程。</summary>
    public void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;

        _tray?.Dispose();
        _tray = null;

        _window?.Shutdown();
        _window = null;

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 正常路径已经在 ExitApp 里清过了，这里是兜底（比如系统注销）
        _tray?.Dispose();
        _window?.Shutdown();
        _tray = null;
        _window = null;

        try
        {
            _showSignal?.Dispose();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch
        {
            // 没持有就算了
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 先落盘：这类错误（典型是绑定写回只读属性）在界面上只闪一个弹窗，
        // 用户一点确定就没了。日志里要留一份。
        MainViewModel.WriteMaintenanceLog("[未处理异常] " + e.Exception);

        // ⚠ 这里有**两层保护**，都是被真实的闪退逼出来的：
        //
        // 场景：界面在布局时抛异常（比如虚拟化列表生成容器时空引用）。
        // 异常处理器弹 MessageBox —— 而 MessageBox 是**模态**的，
        // 它会重入消息循环 → WPF 又跑一遍布局 → 又抛同一个异常
        // → 又进这个处理器 → 又弹窗 ……
        //
        // 实测：栈里这个函数重复了 29 次，2900 层栈，最后栈溢出
        // （0xc00000fd，崩在 dwrite.dll）——**整个进程死掉**。
        //
        // `e.Handled = true` 拦不住它：递归发生在 MessageBox.Show
        // **内部**，不是在它返回之后。
        e.Handled = true;

        // 第一层：同一个异常（类型 + 消息）只弹一次窗。
        //
        // 布局类的异常会一轮一轮地重复抛，但它们其实是**同一个问题**。
        // 用户看一遍就够了，看 29 遍也没用（而且第 29 遍是崩溃）。
        var signature = $"{e.Exception.GetType().Name}:{e.Exception.Message}";
        if (signature == _lastExceptionSignature) return;
        _lastExceptionSignature = signature;

        // 第二层：熔断。
        //
        // 就算签名每次都不同（消息里带随机数/时间戳之类），也不能
        // 无限弹。超过这个数就只写日志，不再弹窗 —— 保证界面还能用，
        // 用户能从托盘退出或者重启。
        _exceptionCount += 1;
        if (_exceptionCount > MAX_POPUP_EXCEPTIONS)
        {
            MainViewModel.WriteMaintenanceLog(
                $"[未处理异常] 已经弹了 {MAX_POPUP_EXCEPTIONS} 次，后面只记日志不再弹窗（防止弹窗重入把栈打爆）");
            return;
        }

        MessageBox.Show(
            e.Exception.Message,
            "WorkBuddy 控制台 — 操作出错",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>上一次弹过窗的异常签名（类型 + 消息）。</summary>
    private string? _lastExceptionSignature;

    /// <summary>这次运行弹过几次异常窗。</summary>
    private int _exceptionCount;

    /// <summary>
    /// 最多弹几次异常窗。
    ///
    /// 为什么要有上限：弹窗会重入消息循环，而重入时界面可能又抛异常。
    /// 实测 29 次就把栈打爆了（0xc00000fd）。给个远小于那个数的上限，
    /// 宁可少弹几个窗，也不能让进程死。
    /// </summary>
    private const int MAX_POPUP_EXCEPTIONS = 3;
}
