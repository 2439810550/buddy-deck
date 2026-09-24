using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WorkBuddyPanel.Services;
using WorkBuddyPanel.ViewModels;

namespace WorkBuddyPanel;

public partial class App : Application
{
    private const string MutexName = @"Local\WorkBuddyPanel.SingleInstance";
    private const string ShowSignalName = @"Local\WorkBuddyPanel.ShowWindow";

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
            RunMaintenanceOnly();
            return;
        }

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

        _ = _window.StartAsync(StartHidden || MaintainOnly);
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
        // 用户一点确定就没了。和上面那个 handler 一样，日志里要留一份。
        MainViewModel.WriteMaintenanceLog("[未处理异常] " + e.Exception);

        MessageBox.Show(
            e.Exception.Message,
            "WorkBuddy 控制台 — 操作出错",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
