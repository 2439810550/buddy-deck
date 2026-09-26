using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkBuddyPanel.Services;

public sealed record GatewayOptions(
    string NodePath,
    string ApiDirectory,
    int Port,
    string ApiKey,
    string Provider,
    string? AuthFile);

/// <summary>
/// 把 `node src/bin.js serve` 拉起来当子进程，并把它讲的话转给界面。
/// 关掉本程序时会把 node 一起收掉，不留孤儿进程占着端口。
/// </summary>
public sealed partial class GatewayRunner : IDisposable
{
    private Process? _process;
    private bool _disposed;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>形如 http://127.0.0.1:8787/v1，启动后从 node 的输出里解析。</summary>
    public string BaseUrl { get; private set; } = "";

    public int ActualPort { get; private set; }

    public event Action<string>? LogLine;
    public event Action<string>? Ready;
    public event Action<int>? Exited;

    [GeneratedRegex(@"base_url\s*:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex BaseUrlRegex();

    [GeneratedRegex(@"api_key\s*:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyRegex();

    public async Task StartAsync(GatewayOptions options, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return;

        if (!File.Exists(options.NodePath))
            throw new FileNotFoundException($"找不到运行环境程序：{options.NodePath}", options.NodePath);

        var binJs = CliRunner.DescribeApiDirectory(options.ApiDirectory);
        if (!File.Exists(binJs))
            throw new FileNotFoundException($"找不到服务程序入口文件：{binJs}", binJs);

        var args = new StringBuilder();
        args.Append("src/bin.js serve");
        args.Append(" --port ").Append(options.Port);
        args.Append(" --api-key ").Append(Quote(options.ApiKey));
        if (options.Provider == "workbuddy-ai")
            args.Append(" --provider workbuddy-ai");
        if (!string.IsNullOrWhiteSpace(options.AuthFile))
            args.Append(" --auth-file ").Append(Quote(options.AuthFile!));

        var psi = new ProcessStartInfo
        {
            FileName = options.NodePath,
            Arguments = args.ToString(),
            WorkingDirectory = options.ApiDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) => HandleLine(e.Data, ready, options);
        process.ErrorDataReceived += (_, e) => HandleLine(e.Data, ready, options);

        process.Exited += (_, _) =>
        {
            var code = TryReadExitCode(process);
            ready.TrySetException(new InvalidOperationException(
                $"网关进程在启动过程中退出了（退出码 {code}）。上方的日志里有 node 打印的原因。"));
            LogLine?.Invoke($"--- 网关进程已退出，退出码 {code} ---");
            Exited?.Invoke(code);
        };

        if (!process.Start())
            throw new InvalidOperationException("无法启动 node 进程。");

        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeout = Task.Delay(TimeSpan.FromSeconds(45), ct);
        var finished = await Task.WhenAny(ready.Task, timeout);

        if (finished == timeout && !ready.Task.IsCompleted)
        {
            Stop();
            throw new TimeoutException("启动网关超时（45 秒）。请确认网络可用，或换个端口试试。");
        }

        BaseUrl = await ready.Task;
        Ready?.Invoke(BaseUrl);
    }

    private void HandleLine(string? line, TaskCompletionSource<string> ready, GatewayOptions options)
    {
        if (line is null) return;
        LogLine?.Invoke(line);

        var baseMatch = BaseUrlRegex().Match(line);
        if (baseMatch.Success)
        {
            var url = baseMatch.Groups[1].Value.Trim();
            BaseUrl = url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) ActualPort = uri.Port;
            ready.TrySetResult(url);
        }
    }

    private static int TryReadExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static string Quote(string value)
        => value.Contains(' ') ? $"\"{value}\"" : value;

    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
            // 已经退了
        }
        finally
        {
            process.Dispose();
        }

        BaseUrl = "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
