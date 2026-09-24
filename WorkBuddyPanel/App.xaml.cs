using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WorkBuddyPanel.Services;

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

        // 有关关闭窗口收进托盘的需求，所以退出必须显式触发，不能跟着最后一个窗口走
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            MessageBox.Show(
                args.ExceptionObject?.ToString() ?? "未知错误",
                "WorkBuddy 控制台 — 发生了未处理的错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };

        StartHidden = e.Args.Any(arg => arg.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        MaintainOnly = e.Args.Any(arg => arg.Equals("--maintain", StringComparison.OrdinalIgnoreCase));

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
        MessageBox.Show(
            e.Exception.Message,
            "WorkBuddy 控制台 — 操作出错",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
