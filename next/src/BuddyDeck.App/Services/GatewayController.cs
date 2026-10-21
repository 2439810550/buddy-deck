using System.Diagnostics;

namespace BuddyDeck.App.Services;

/// <summary>
/// 网关子进程的生命周期控制。
///
/// 设置页改端口/key/账号库之后，需要**重启网关**让新设置生效。
/// 这个接口把「收掉旧的 → 清孤儿 → 拉新的」封装成一个动作，
/// 让 <see cref="BuddyDeck.App.ViewModels.SettingsViewModel"/> 不直接碰
/// <see cref="GatewayRunner"/> / <see cref="GatewayReaper"/> 的细节。
/// </summary>
public interface IGatewayController
{
    /// <summary>
    /// 用**当前 AppSettings** 重启网关（收掉旧的、清掉端口上的孤儿、
    /// 拉起新的并等它就绪）。
    ///
    /// @throws 端口被占 / 网关 exe 找不到 / 保险库缺失 / 就绪超时。
    ///         调用方（设置页）要把异常翻译成给用户看的提示。
    /// </summary>
    Task RestartAsync(CancellationToken ct = default);

    /// <summary>停止网关子进程（不自动重启）。</summary>
    void Stop();

    /// <summary>当前子进程在不在跑。</summary>
    bool IsRunning { get; }

    /// <summary>
    /// 网关状态变化（启动中 / 已就绪 / 失败 / 已停止）。
    /// 参数是人能读的一句状态描述。界面顶栏/提示条用。
    /// </summary>
    event Action<string>? StateChanged;

    /// <summary>当前网关状态的一句话描述（空 = 还没试过）。</summary>
    string LastState { get; }
}

/// <summary>
/// <see cref="IGatewayController"/> 的实现。持有 AppSettings 与当前的
/// <see cref="GatewayRunner"/> 实例，App 退出时负责收掉子进程。
/// </summary>
public sealed class GatewayController : IGatewayController, IDisposable
{
    private readonly AppSettings _settings;
    private readonly Action<string> _log;
    private GatewayRunner? _gateway;
    private bool _disposed;

    public event Action<string>? StateChanged;
    public string LastState { get; private set; } = "";

    private bool _starting;

    /// <summary>
    /// 网关在不在跑（或**正在启动**）。
    ///
    /// 为什么「启动中」也算：启动是异步的（后台拉进程 + 等 healthz），
    /// 窗口可能先于启动完成渲染。如果只认子进程在不在，首帧按钮会闪成
    /// 「未启动」的 ▶，用户以为网关没起、再点一次启动。
    /// </summary>
    public bool IsRunning => _starting || (_gateway?.IsRunning ?? false);

    private void SetState(string state)
    {
        LastState = state;
        StateChanged?.Invoke(state);
    }

    public GatewayController(AppSettings settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
    }

    /// <summary>
    /// 后台拉起（fire-and-forget，不阻塞启动）。
    ///
    /// 失败只记日志不崩 —— 界面顶栏会显示「网关未启动」，
    /// 用户去设置页改端口/账号库后重试。
    /// </summary>
    public void StartBackground()
    {
        if (!_settings.AutoStartGateway) return;

        _ = Task.Run(async () =>
        {
            try
            {
                SetState("正在启动网关…");
                await RestartAsync().ConfigureAwait(false);
                SetState($"网关就绪（127.0.0.1:{_settings.Port}）");
            }
            catch (Exception ex)
            {
                _log($"网关拉起失败：{ex.Message}");
                SetState($"网关未启动：{ex.Message}");
            }
        });
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _starting = true;
        try
        {
            // ① 收掉当前在跑的（换端口时旧端口要释放，新进程才能绑）。
            _gateway?.Dispose();
            _gateway = null;

            // ② 端口占用检查：如果端口上有个**认不出的**监听者（不是我们的 .NET 网关），
            //    直接给一句明确的错误，而不是等 45 秒超时。
            //    这是「端口被占提示」—— 用户一按启动就知道为什么起不来。
            if (GatewayReaper.HasForeignListener(_settings.Port))
            {
                throw new InvalidOperationException(
                    $"端口 {_settings.Port} 被占用（可能是其它程序或旧版网关）。请在设置里换一个端口。");
            }

            // ③ 清掉端口上的孤儿 .NET 网关（上次面板被强杀留下的）。
            //    只动 BuddyDeck.Gateway.exe，绝不碰 node.exe（旧版网关）。
            await GatewayReaper.ReplaceGatewayOnPortAsync(_settings.Port, _log).ConfigureAwait(false);

            // ④ 拉起新的，等它就绪。
            var runner = new GatewayRunner();
            runner.LogLine += _log;
            runner.Exited += code => _log($"网关子进程退出，码 {code}");
            await runner
                .StartAsync(
                    _settings.Port,
                    _settings.ApiKey,
                    _settings.VariantId,
                    _settings.VaultPath,
                    ct)
                .ConfigureAwait(false);

            _gateway = runner;
            SetState($"网关就绪（127.0.0.1:{_settings.Port}）");
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>停掉网关子进程。幂等。</summary>
    public void Stop()
    {
        _starting = false;
        _gateway?.Dispose();
        _gateway = null;
        SetState("网关已停止");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gateway?.Dispose();
        _gateway = null;
    }
}
