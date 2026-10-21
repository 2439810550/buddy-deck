using System.Net.Http;
using System.Windows;
using BuddyDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BuddyDeck.App;

/// <summary>
/// 应用入口。
///
/// 两件事：
///  ① 组装依赖（网关客户端 + 六个页面的 ViewModel）。视图自己去容器里要 ViewModel，
///     所以视图里不会出现 `new HttpGatewayClient(...)` 这种东西。
///  ② 决定用哪个 <see cref="IGatewayClient"/>：
///     默认用真实的；设了环境变量 BUDDYDECK_OFFLINE=1 就用「未连接」实现。
///     为什么留这个开关：截图和演示要画面稳定，不该依赖外面有没有起网关。
/// </summary>
public partial class App : Application
{
    /// <summary>全应用一个容器。测试和探针要拿 ViewModel 时也走它。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>网关子进程的生命周期控制。App 退出时一起收掉。</summary>
    private static GatewayController? _gateway;

    /// <summary>面板自己的设置（端口 / key / 账号库）。</summary>
    private static AppSettings _settings = AppSettings.Load();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ★ 网关控制器**必须先于 ConfigureServices 创建** ——
        //   DI 容器要注册的 IGatewayController 必须是**真正用来启停网关的那个实例**。
        //   否则 ShellViewModel 注入的是一个「从未启动过网关」的副本，
        //   它的 IsRunning 永远是 false → 按钮一直显示「启动」，
        //   而实际网关已经在跑。
        _gateway = new GatewayController(_settings, LogGatewayLine);

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // 诊断模式不需要界面，先跑掉 —— 也不该拉起窗口。
        if (Array.IndexOf(e.Args, "--selftest") >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        // ---- 截图模式：`BuddyDeck.App.exe --probe <输出目录>` ----
        //
        // 为什么要有它：验收要求「界面能启动、网关没起时不崩、截图存到 docs/ui-*.png」。
        // 靠人去点窗口截图不可重复，没桌面会话时也跑不了。这条路径把六个页面
        // 各渲染一遍存 PNG，然后退出；正常启动（不带这个参数）完全不碰。
        var args = e.Args;

        // 诊断模式：把「网关没起时界面到底拿到了什么」写成证据文件，然后退出。
        var selfIndex = Array.IndexOf(args, "--selftest");
        if (selfIndex >= 0 && selfIndex + 1 < args.Length)
        {
            // ⚠ 必须挪到线程池上跑，不能在 UI 线程上直接调 SelfTest.Run。
            //
            // 这是一个真实踩过的死锁（sync-over-async）：
            //   · OnStartup 跑在 UI 线程上
            //   · SelfTest 内部是 `xxxAsync().GetAwaiter().GetResult()` —— 同步阻塞等待
            //   · 而六个 ViewModel 的 await 全都是 ConfigureAwait(true)（共 30 处，
            //     它们是刻意的：要在 UI 线程上更新绑定属性）
            //   · 于是：UI 线程被 GetResult() 堵住 → continuation 排不进 UI 线程 → 永久挂死
            //
            // 为什么离线时看不出来：NotConnectedGatewayClient 是**同步返回**的，
            // await 从不让出线程，continuation 根本不需要回 UI 线程。
            // 所以「离线通过」掩盖了这个 bug —— 只有真实网关在线时才暴露。
            //
            // 为什么是在这里改、而不是去改 ViewModel 的 ConfigureAwait：
            // 那 30 处 ConfigureAwait(true) 是产品代码的正当需求（UI 线程更新绑定），
            // 为测试去改产品代码是本末倒置。挪诊断代码才是对的层次。
            //
            // 挪到线程池后：UI 线程空着，continuation 随时能回来，不再死锁。
            // 退出码语义不变：SelfTest 内部仍然负责设 Environment.ExitCode，Shutdown 后照常。
            Task.Run(() => SelfTest.Run(args[selfIndex + 1])).GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        var probeIndex = Array.IndexOf(args, "--probe");
        if (probeIndex >= 0 && probeIndex + 1 < args.Length)
        {
            RunProbe(args[probeIndex + 1]);
            return;
        }

        // 正常启动：建主窗口，关掉它就退出整个应用。
        //
        // ⚠ 注意这里是 `OnMainWindowClose`，而托盘模式**故意**要拦下关闭 ——
        // 两者不冲突：`MainWindow.OnClosing` 会 `e.Cancel = true` 把窗口藏起来，
        // 于是这次"关闭"根本没发生，`OnMainWindowClose` 也就不会触发。
        // 真正退出只有一条路：托盘菜单 → `_exiting = true` → `Close()` 放行。
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // ★ **拉起自己的 .NET 网关**（与旧版 WorkBuddyPanel 同构）——
        //   不是去连 8787 上的旧 Node 网关。App 退出时在 OnExit 里一起收掉。
        //
        // 为什么在这里（建窗口之前）：窗口 OnLoaded 就要探活，
        // 网关得先起来，否则探活会拍到「未连接」。
        // （实例已在 OnStartup 开头创建，这里直接接 StateChanged + 启动。）

        // 网关状态变化 → 左侧状态区。撞端口/拉失败时用户能立刻看到原因。
        _gateway.StateChanged += state =>
        {
            Dispatcher.Invoke(async () =>
            {
                try
                {
                    var shell = Services.GetService<ShellViewModel>();
                    if (shell is not null)
                    {
                        // 启动失败的具体原因（「网关未启动：端口 X 被占用」）——
                        // 直接作为左侧「未启动原因」小字，比探活的「连接被拒绝」更具体。
                        if (state.StartsWith("网关未启动", StringComparison.Ordinal))
                        {
                            shell.OfflineReason = state["网关未启动".Length..].TrimStart('：', ':');
                        }
                    }

                    // 网关就绪/失败后，重新探活让左侧状态（已启动/未启动、端口、模型数）
                    // 和状态轨（账号数）跟着变 —— 这就是「设置改端口后 UI 同步」。
                    if (MainWindow is MainWindow win)
                    {
                        await win.ProbeGatewayAsync();
                    }
                }
                catch
                {
                    // 顶栏还没建好时，状态先丢了也不致命
                }
            });
        };

        _gateway.StartBackground();

        MainWindow = new MainWindow();
        MainWindow.Show();

        // 托盘在窗口**构造之后**建：它的菜单回调要能拿到这个窗口实例。
        ((MainWindow)MainWindow).SetupTray();
    }

    /// <summary>把网关子进程的输出转给日志页（尽力而为，不抛）。</summary>
    private static void LogGatewayLine(string line)
    {
        try
        {
            var logs = Services?.GetService<LogsViewModel>();
            logs?.AppendProcessLine(line);
        }
        catch
        {
            // 日志页可能还没建好 —— 启动早期这行只是进不了日志，不致命
        }
    }

    /// <summary>
    /// App 退出时收掉网关子进程，不留孤儿进程占端口。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _gateway?.Dispose();
        }
        catch
        {
            // 收进程失败不能拦退出
        }
        _gateway = null;
        base.OnExit(e);
    }

    /// <summary>
    /// 逐页渲染截图，然后退出。
    ///
    /// 这里要显式管进程生命周期：StartupUri="MainWindow.xaml" 会让 WPF 自动
    /// 建一个主窗口，而探针要等六张图都写完才退出 —— 所以关掉自动建窗口，
    /// 自己建、自己存、自己 Shutdown。
    /// </summary>
    private void RunProbe(string outDir)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 探针不建托盘 —— 截图进程不该在通知区留图标。
        // 静态方法，直接用类型名调（不走 Application.MainWindow 那个基类引用）。
        BuddyDeck.App.MainWindow.MarkProbeMode();

        var window = new MainWindow();
        window.Show();

        window.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                Directory.CreateDirectory(outDir);

                // ⚠ 必须先等首次数据加载完成再截图。
                //
                // Show() 之后 OnLoaded 的探活是**异步**的，而截图是紧接着发生的 ——
                // 不等就会拍到「数据还没到」的空状态：状态轨没有竖条、
                // 底部数字为空。它看起来像绑定坏了，实际是**截图比数据先到**。
                // 我一开始就照那个假象查错了方向（以为 DockPanel 宽度算错）。
                await window.WaitForInitialLoadAsync(TimeSpan.FromSeconds(10));

                foreach (var page in Enum.GetValues<ViewModels.ShellViewModel.PageKey>())
                {
                    var name = page.ToString().ToLowerInvariant();

                    // 聊天页要**真的发一条请求**才看得到媒体渲染 ——
                    // 只切页的话那一页永远是空态（见 SaveScreenshotAsync 的说明）。
                    var driveChat = page == ViewModels.ShellViewModel.PageKey.Chat;

                    await window.SaveScreenshotAsync(
                        Path.Combine(outDir, $"ui-{name}.png"), page, driveChat);
                }
            }
            catch (Exception ex)
            {
                // 探针失败要留下证据，别静默 —— 不然截图缺失查不出原因。
                try { File.WriteAllText(Path.Combine(outDir, "ui-probe-error.txt"), ex.ToString()); }
                catch (IOException) { /* 连日志都写不了，只能让退出码说话 */ }
                Environment.ExitCode = 1;
            }
            finally
            {
                Shutdown();
            }
        });
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var offline = string.Equals(
            Environment.GetEnvironmentVariable("BUDDYDECK_OFFLINE"),
            "1",
            StringComparison.Ordinal);

        // ★ 端口 / key 从**面板自己的设置**读（不是环境变量）。
        //   这是本轮的架构修正：App 自己拉起网关，所以它自己决定端口和 key，
        //   与网关子进程用同一份 AppSettings —— 不再去猜 8787 上有什么。
        var port = _settings.Port;
        var apiKey = _settings.ApiKey;

        if (offline)
        {
            services.AddSingleton<IGatewayClient>(new NotConnectedGatewayClient(port));
        }
        else
        {
            // 一个 HttpClient 用一辈子 —— 每次 new 都会耗光 socket。
            services.AddSingleton<IGatewayClient>(
                _ => new HttpGatewayClient(new HttpClient(), port, apiKey: apiKey));
        }

        services.AddSingleton(_settings);

        // 网关子进程的生命周期控制 —— 设置页保存后要重启网关用。
        // 用接口隔开：SettingsViewModel 不直接碰 GatewayRunner 的细节。
        services.AddSingleton<IGatewayController>(_gateway ?? new GatewayController(_settings, LogGatewayLine));

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<TasksViewModel>();

        // 文件选择器的 WPF 实现（t81）。
        //
        // 注册在这里而不是让 VM 自己 new：那样 VM 就绑死在 WPF 上，
        // 而「从文件导入」的逻辑（解析/校验/确认流程）本来是可以脱离
        // 界面测的 —— 测试夹具塞一个假的 IFilePicker 进去就行。
        services.AddSingleton<IFilePicker, WpfFilePicker>();

        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<PoolsViewModel>();
        services.AddSingleton<VaultViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<StatsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<LogsViewModel>();
    }
}
