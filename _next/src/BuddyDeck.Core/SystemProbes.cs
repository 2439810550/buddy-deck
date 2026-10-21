using System.Diagnostics;

namespace BuddyDeck.Core;

/// <summary>
/// 真实的进程执行器 —— 跑 `reg query` 这类系统命令。
///
/// ## 为什么封装成接口
///
/// 测试里不能真去查注册表（跑在 CI 上、或者机器上没装 App），
/// 所以 <see cref="IProcessRunner"/> 是接口，这里是生产实现，
/// 测试塞假的。
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    /// <summary>单条命令最多等这么久。查注册表正常几十毫秒，超时就是卡住了。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<string?> RunAsync(string command, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 注册表里可能有中文产品名 —— 交给系统默认编码处理
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            return process.ExitCode == 0 ? stdout : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // 命令不存在 / 没权限 / 被拦 —— 一律当成「查不到」，不要抛。
            // 调用方会掉到兜底版本。
            return null;
        }
    }
}

/// <summary>真实的文件探测。</summary>
public sealed class SystemFileProbe : IFileProbe
{
    public bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public string ReadAllText(string path) => File.ReadAllText(path);
}
