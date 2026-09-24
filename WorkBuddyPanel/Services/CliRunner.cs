using System.Diagnostics;
using System.IO;
using System.Text;

namespace WorkBuddyPanel.Services;

public sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(StdErr) ? StdOut : $"{StdOut}\n{StdErr}";
}

/// <summary>调用 `node src/bin.js ...` 这一类一次性命令。</summary>
public static class CliRunner
{
    public static async Task<CliResult> RunAsync(
        string nodePath,
        string workingDirectory,
        string arguments,
        string? stdin = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = nodePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 口令走 stdin，不走命令行参数 —— 参数会出现在进程列表里
            RedirectStandardInput = stdin is not null,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException($"无法启动进程：{nodePath}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(stdin);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
            }
            catch
            {
                // 子进程没等 stdin 就退了，不算错
            }
        }

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        // 等异步读取把缓冲区刷完
        process.WaitForExit();

        return new CliResult(process.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程已经没了
        }
    }

    /// <summary>
    /// 从一段可能混着日志的输出里，抠出第一个完整的 JSON 对象。
    /// `status --json` 是纯 JSON，但 node 有时会先打一行 warning。
    /// </summary>
    public static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                    break;
            }
        }

        return null;
    }

    /// <summary>探测某个本地端口上是否已经有服务在监听。</summary>
    public static bool IsPortInUse(int port)
    {
        try
        {
            var listeners = System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpListeners();
            foreach (var endpoint in listeners)
            {
                if (endpoint.Port == port) return true;
            }
        }
        catch
        {
            // 拿不到就当作没占用，让 node 自己去报错
        }

        return false;
    }

    public static string DescribeApiDirectory(string dir)
        => Path.Combine(dir, "src", "bin.js");
}
