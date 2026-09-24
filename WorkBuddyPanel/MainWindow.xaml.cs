using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using WorkBuddyPanel.Services;
using WorkBuddyPanel.ViewModels;

namespace WorkBuddyPanel;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly System.Windows.Threading.DispatcherTimer _accountsTimer;

    /// <summary>
    /// 滚动到底的节流。
    ///
    /// 流式输出一秒能来几十段，每段都 BeginInvoke 一次 ScrollToEnd 是白耗 ——
    /// 人眼根本分不出 20ms 和 200ms 的区别，但 UI 线程会因此一顿一顿的。
    /// 合并成“最多每 100ms 滚一次”，观感上反而更顺。
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _scrollTimer;

    /// <summary>
    /// 只有停在「账号」页时才自动刷新 —— 积分是按次扣的，
    /// 用户没在看的时候不该白白发请求。
    /// </summary>
    private bool _onAccountsTab;

    public MainWindow()
    {
        InitializeComponent();

        var settings = AppSettings.Load();
        _vm = new MainViewModel(settings);
        DataContext = _vm;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        Closing += OnClosing;

        // 滚动节流器：只负责“攒着”，真正的滚动在 Tick 里做一次
        _scrollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _scrollTimer.Tick += (_, _) =>
        {
            _scrollTimer.Stop();
            ChatScroll?.ScrollToEnd();
        };

        // 账号页的自动刷新：定时器只负责计数，真正跑不跑看下面两个开关
        _accountsTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_vm.AccountsRefreshSeconds),
        };
        _accountsTimer.Tick += async (_, _) =>
        {
            if (!_onAccountsTab) return;
            if (!_vm.AccountsAutoRefresh) return;
            await _vm.RefreshAccountsLightAsync();
        };
        _accountsTimer.Start();

        MainTabs.SelectionChanged += (_, _) =>
        {
            _onAccountsTab = MainTabs.SelectedIndex == AccountsTabIndex;
            // 刚切过来先刷一次，不用等一个周期
            if (_onAccountsTab) _ = _vm.RefreshAccountsLightAsync();
        };
    }

    /// <summary>「账号」标签页的下标。加页时跟着调。</summary>
    private int AccountsTabIndex => 2;

    public MainViewModel ViewModel => _vm;

    /// <summary>
    /// App 启动后调这里。hidden = true 时窗口始终不显示（开机自启的场景），
    /// 只把网关拉起来，用户全程看不到界面。
    /// </summary>
    public async Task StartAsync(bool hidden)
    {
        // 维护模式：只续期+签到，完事就退，不碰网关也不连界面
        if (App.MaintainOnly)
        {
            await _vm.DetectPathsAsync();
            await _vm.RunMaintenanceAsync();
            return;
        }

        await _vm.InitializeAsync();

        if (_vm.AutoStartGateway && _vm.CanRunNode)
        {
            var ok = await _vm.TryStartGatewayAsync();
            if (!ok && !hidden) ShowPanel();
        }

        // 续期窗口不够了就静默跑一次维护 —— 这是「不用以后频繁扫码」的日常保障
        try
        {
            await _vm.AutoMaintainIfNeededAsync();
        }
        catch (Exception ex)
        {
            _vm.LogMaintenance("自动维护失败：" + ex.Message);
        }
    }

    /// <summary>从托盘把窗口叫回来。</summary>
    public void ShowPanel()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>App 退出时调用：停网关、释放资源。可重复调用。</summary>
    public void Shutdown() => _vm.Dispose();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 正在退出：放行
        if (App.IsExiting) return;

        if (_vm.MinimizeToTray)
        {
            // 关窗口 = 收进托盘，网关继续跑
            e.Cancel = true;
            Hide();
            ((App)Application.Current).Tray?.HintHiddenToTray();
            return;
        }

        // 没开托盘常驻：关窗口就是退出，但必须显式结束 Application
        // （ShutdownMode 是 OnExplicitShutdown，光关窗口进程不会退）
        e.Cancel = true;
        ((App)Application.Current).ExitApp();
    }

    /// <summary>新消息出现时挂上监听；内容一变就滚到底。</summary>
    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<ChatMessageVm>())
                item.PropertyChanged += OnMessagePropertyChanged;
        }

        ScrollChatToEnd();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessageVm.Text)
            or nameof(ChatMessageVm.Reasoning)
            or nameof(ChatMessageVm.Error))
        {
            ScrollChatToEnd();
        }
    }

    /// <summary>
    /// 请求滚动到底。
    ///
    /// **不是马上滚**，而是把滚动安排到 100ms 后的那一帧。
    /// 流式输出期间这个方法一秒会被叫几十次，但实际只滚 10 次。
    /// </summary>
    private void ScrollChatToEnd()
    {
        if (!_scrollTimer.IsEnabled) _scrollTimer.Start();
    }

    private void BrowseNode_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 node.exe",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
            _vm.NodePath = dialog.FileName;
    }

    private void BrowseApiDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 workbuddy_api 目录（里面应该有 src\\bin.js）",
        };

        if (dialog.ShowDialog(this) != true) return;

        var picked = dialog.FolderName;
        if (!File.Exists(Path.Combine(picked, "src", "bin.js")))
        {
            var found = ProcessLocator.FindApiDirectory(picked);
            if (found is null)
            {
                MessageBox.Show(
                    $"这个目录里找不到 src\\bin.js：\n{picked}\n\n请选 workbuddy_api 的根目录。",
                    "WorkBuddy 控制台",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            picked = found;
        }

        _vm.ApiDirectory = picked;
    }

    private async void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        await _vm.DetectPathsAsync();
        _vm.SaveSettingsCommand.Execute(null);
    }

    /// <summary>首页那个「去账号页」按钮。</summary>
    private void GoToAccountsTab_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = AccountsTabIndex;
    }

    /// <summary>界面里的超链接（免责声明里的署名链接）。</summary>
    private void OpenLink_Click(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _vm.LogMaintenance($"打开链接失败：{ex.Message}");
        }
        e.Handled = true;
    }

    /// <summary>输入框里回车 = 发送；Shift+回车 = 换行。</summary>
    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;

        e.Handled = true;
        if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
    }
}
