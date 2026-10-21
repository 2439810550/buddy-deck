using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace BuddyDeck.App.Services;

/// <summary>
/// 拉起新版 .NET 网关（<c>BuddyDeck.Gateway.exe</c>）当子进程，并把它讲的话转给界面。
///
/// ## 为什么要有这个（架构对齐旧版）
///
/// 旧版 WorkBuddyPanel 的架构是「面板自己拉起 node 网关，再连它」——
/// 一个进程，用户双击 exe 就全有了。新版 App 早期只做了「客户端」，
/// 于是它只能去连 8787 上那个旧 Node 网关 —— **多此一举**。
///
/// 这个类型补上旧版 <c>GatewayRunner</c> 的角色：App 启动时拉起
/// <c>BuddyDeck.Gateway.exe</c>（.NET 网关，无需 node），App 退出时收掉它，
/// 不留孤儿进程占端口。
///
/// ## 与旧版 GatewayRunner 的差异
///
/// 旧版拉的是 `node src/bin.js serve`，要去找 node.exe 和 api 目录。
/// 新版网关是**自包含的 .NET 可执行文件**，没有外部运行时依赖，
/// 所以定位简单得多：找同目录或已知位置的 `BuddyDeck.Gateway.exe`。
///
/// ## 就绪判断
///
/// 旧版靠解析 node 输出的 `base_url:` 那行。新版网关启动时会打印
/// `BuddyDeck.Gateway 启动：{Host}:{Port} ...`，同时 `/healthz` 可访问。
/// 这里用「输出里出现启动行」或「healthz 通了」任一即认为就绪 ——
/// 双保险，比只认输出行更稳（输出编码/重定向出问题时不至于卡死）。
/// </summary>
public sealed class GatewayRunner : IDisposable
{
    private Process? _process;
    private bool _disposed;
    private readonly HttpClient _healthProbe;

    public GatewayRunner()
    {
        _healthProbe = new HttpClient(new SocketsHttpHandler())
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
    }

    /// <summary>子进程是不是还在跑。</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>实际在跑的进程。</summary>
    public int ActualPort { get; private set; }

    /// <summary>网关进程输出的一行（给界面日志页用）。</summary>
    public event Action<string>? LogLine;

    /// <summary>网关就绪（healthz 通了）。参数是 baseUrl，如 http://127.0.0.1:8787。</summary>
    public event Action<string>? Ready;

    /// <summary>子进程退出。</summary>
    public event Action<int>? Exited;

    /// <summary>
    /// 定位网关 exe。
    ///
    /// 优先级：
    ///   ① 环境变量 <c>BUDDYDECK_GATEWAY_EXE</c>（显式指定）
    ///   ② App 同目录的 <c>BuddyDeck.Gateway.exe</c>（发布版：两个 exe 放一起）
    ///   ③ 开发布局：从 App 的 bin 目录往上找 src/BuddyDeck.Gateway/bin 下的产物
    ///
    /// 返回 null = 找不到（调用方要给出明确错误，不能瞎猜一个路径）。
    /// </summary>
    public static string? LocateGatewayExe()
    {
        // ① 显式指定（测试/特殊部署用）
        var env = Environment.GetEnvironmentVariable("BUDDYDECK_GATEWAY_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        // ② App 同目录
        var sameDir = Path.Combine(AppContext.BaseDirectory, "BuddyDeck.Gateway.exe");
        if (File.Exists(sameDir)) return sameDir;

        // ③ 开发布局：App 的 bin/Debug/net8.0-windows → 往上找到 Gateway 的 bin/Debug/net8.0
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && dir is not null; depth++)
        {
            // Gateway 和 App 都挂在同一个解决方案根下：
            // <root>/src/BuddyDeck.Gateway/bin/Debug/net8.0/BuddyDeck.Gateway.exe
            var candidate = Path.Combine(dir.FullName, "src", "BuddyDeck.Gateway", "bin");
            if (Directory.Exists(candidate))
            {
                foreach (var build in Directory.EnumerateFiles(
                    candidate, "BuddyDeck.Gateway.exe", SearchOption.AllDirectories))
                {
                    return build;
                }
            }
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// 拉起网关子进程，等它就绪。
    ///
    /// @param port      监听端口。
    /// @param apiKey    要求的 Bearer key（空 = 不鉴权）。
    /// @param variantId 变体 id（workbuddy / workbuddy-ai）。
    /// @param vaultPath 保险库路径（**绝对路径**）。这是账号的来源 ——
    ///                  新版网关默认读 exe 目录下的 auth.cn.json，但用户的账号
    ///                  在别处（旧版是 ~/.workbuddy-api/accounts.cn.json），
    ///                  所以必须显式传。
    /// @param ct        取消令牌（取消 = 杀掉刚拉起的进程）。
    /// </summary>
    public async Task StartAsync(
        int port,
        string apiKey,
        string variantId,
        string vaultPath,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return;

        var exe = LocateGatewayExe();
        if (exe is null)
        {
            throw new FileNotFoundException(
                "找不到网关程序 BuddyDeck.Gateway.exe。请确认它与本程序放在同一目录，或用环境变量 BUDDYDECK_GATEWAY_EXE 指定。");
        }

        if (!File.Exists(vaultPath))
        {
            throw new FileNotFoundException($"保险库文件不存在：{vaultPath}", vaultPath);
        }

        // 端口给 0 会让 Kestrel 挑空闲端口 —— 但我们要求「指定的那个」，
        // 所以直接传。若被占，网关自己会报错退出，这里靠就绪超时捕获。
        var args = new StringBuilder();
        args.Append("--port ").Append(port);
        args.Append(" --host 127.0.0.1");
        args.Append(" --vault ").Append(Quote(vaultPath));
        // ★ 总是传 --api-key（**空也传**）——
        //   空串 = 「明确不鉴权」。不传的话网关会把空 key 当「没给」，
        //   回退去读 settings.json 里上次遗留的 key → 鉴权意外开启 → 401。
        args.Append(" --api-key ").Append(Quote(apiKey ?? ""));
        if (!string.IsNullOrWhiteSpace(variantId))
        {
            args.Append(" --variant ").Append(Quote(variantId));
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args.ToString(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var readyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            LogLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            LogLine?.Invoke(e.Data);
        };
        process.Exited += (_, _) =>
        {
            var code = TryReadExitCode(process);
            readyTcs.TrySetException(new InvalidOperationException(
                $"网关进程在启动过程中退出了（退出码 {code}）。日志页里有原因。"));
            LogLine?.Invoke($"--- 网关进程已退出，退出码 {code} ---");
            Exited?.Invoke(code);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动网关进程。");
        }

        _process = process;
        ActualPort = port;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 双保险就绪：healthz 通了，或 45 秒超时。
        var healthTask = WaitHealthyAsync(port, readyTcs, ct);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(45), ct);

        var finished = await Task.WhenAny(healthTask, timeoutTask).ConfigureAwait(false);

        if (finished == timeoutTask && !readyTcs.Task.IsCompleted)
        {
            Stop();
            throw new TimeoutException("启动网关超时（45 秒）。请到「日志」页看网关说了什么。");
        }

        if (readyTcs.Task.IsFaulted)
        {
            Stop();
            throw new InvalidOperationException(
                readyTcs.Task.Exception?.InnerException?.Message ?? "网关启动失败。");
        }

        var baseUrl = $"http://127.0.0.1:{port}";
        Ready?.Invoke(baseUrl);
    }

    /// <summary>
    /// 轮询 healthz 直到通。**健康检查不受鉴权限制**（网关特意把 /healthz
    /// 排在鉴权之前），所以不用带 key。
    /// </summary>
    private async Task WaitHealthyAsync(
        int port,
        TaskCompletionSource<string> readyTcs,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_process is { HasExited: true }) return;   // 退出了，交给 Exited 事件报错

            try
            {
                using var resp = await _healthProbe
                    .GetAsync($"http://127.0.0.1:{port}/healthz", ct)
                    .ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    readyTcs.TrySetResult($"http://127.0.0.1:{port}");
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // 还没起来，继续等
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 杀掉子进程（含其子进程树）。
    ///
    /// ⚠ **不等待退出**（不发 <c>WaitForExit</c>）：
    /// Kill 是同步信号，进程真正退出由系统异步收尾。
    /// 等 5 秒会让 UI 线程卡死（关闭按钮要「立刻」响应，不能等进程收尸）。
    /// 进程对象随后 Dispose，剩余清理交给系统的进程回收。
    /// </summary>
    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 已经退了
        }
        finally
        {
            process.Dispose();
        }
    }

    private static int TryReadExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    /// <summary>
    /// 命令行参数引号处理。空串也要用引号包成 `""` ——
    /// 否则 `--api-key ` 会被解析成「无值参数」，空 key 的语义就丢了。
    /// </summary>
    private static string Quote(string value)
        => value.Length == 0 || value.Contains(' ') ? $"\"{value}\"" : value;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _healthProbe.Dispose();
    }
}
