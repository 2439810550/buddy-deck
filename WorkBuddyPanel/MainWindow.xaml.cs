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

    public MainWindow()
    {
        InitializeComponent();

        var settings = AppSettings.Load();
        _vm = new MainViewModel(settings);
        DataContext = _vm;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        Closing += OnClosing;
    }

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
            _vm.DetectPaths();
            await _vm.RunMaintenanceAsync();
            ((App)Application.Current).ExitApp();
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

    private void ScrollChatToEnd()
        => Dispatcher.BeginInvoke(() => ChatScroll?.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);

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

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        _vm.DetectPaths();
        _vm.SaveSettingsCommand.Execute(null);
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
