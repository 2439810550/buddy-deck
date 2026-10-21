using System.Diagnostics;
using System.Net.NetworkInformation;

namespace BuddyDeck.App.Services;

/// <summary>
/// 找出「占用某个端口、而且确实是我们的 .NET 网关」的进程，并停掉它。
///
/// ## 为什么需要它（与旧版 ProcessReaper 同一条理由）
///
/// 真实事故（旧版）：8787 上跑着一个几小时前启动的孤儿网关（父面板已退出），
/// 代码改了好几轮，那个进程还在跑旧代码。用户重启面板 → 端口被占 → 面板
/// 采用旧网关 → 功能还是旧的。
///
/// 新版网关是 .NET 自包含 exe，匹配条件比旧版 node 简单：
///
/// <code>
///   ① 进程名是 BuddyDeck.Gateway.exe
///   ② 命令行含 --port &lt;这个端口&gt;
/// </code>
///
/// 两条全过才动手。任何一条拿不到，一律不动手并返回失败。
/// 绝不碰 node.exe（那是旧版网关，用户可能还在用旧面板）。
/// </summary>
public static class GatewayReaper
{
    private sealed record Candidate(int Pid, string CommandLine);

    /// <summary>这个端口上有没有人监听。</summary>
    private static bool HasListener(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>端口被占，但占它的**不是我们的 .NET 网关**（可能是旧 node 网关或别的程序）。</summary>
    public static bool HasForeignListener(int port)
        => HasListener(port) && FindGatewayProcesses(port).Count == 0;

    /// <summary>
    /// 找出命令行匹配「我们的 .NET 网关 + 这个端口」的进程。
    /// </summary>
    private static List<Candidate> FindGatewayProcesses(int port)
    {
        var results = new List<Candidate>();
        var portTokens = new[] { $"--port {port}", $"--port={port}" };

        foreach (var process in Process.GetProcessesByName("BuddyDeck.Gateway"))
        {
            int pid = process.Id;
            process.Dispose();

            var commandLine = QueryCommandLine(pid);
            if (commandLine.Length == 0) continue;
            if (!portTokens.Any(t => commandLine.Contains(t, StringComparison.Ordinal))) continue;

            results.Add(new Candidate(pid, commandLine));
        }

        return results;
    }

    private static string QueryCommandLine(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (System.Management.ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    return item["CommandLine"]?.ToString() ?? "";
                }
            }
        }
        catch
        {
            // WMI 拿不到 —— 调用方当成「认不出」
        }
        return "";
    }

    /// <summary>
    /// 停掉端口上那个旧的 .NET 网关（孤儿），好让本 App 起一个新的。
    ///
    /// @returns true = 停掉了（或本来就没有）；false = 认不出 / 停不掉。
    ///          返回 false 时**没有副作用**。
    /// </summary>
    public static async Task<bool> ReplaceGatewayOnPortAsync(int port, Action<string>? log = null)
    {
        void Say(string message) => log?.Invoke(message);

        if (!HasListener(port)) return true;   // 没人在听，直接放行

        var candidates = FindGatewayProcesses(port);

        if (candidates.Count == 0)
        {
            // 端口有人监听，但认不出是我们的 .NET 网关（可能是旧 node 网关
            // 或别的程序）—— **绝不能动**。让启动流程去报端口冲突。
            Say($"端口 {port} 上有程序在监听，但认不出是我们的网关（可能是旧版或别的程序），不动它。");
            return false;
        }

        foreach (var target in candidates)
        {
            Say($"停掉孤儿网关：PID {target.Pid}");

            try
            {
                using var process = Process.GetProcessById(target.Pid);
                process.Kill(entireProcessTree: true);

                for (var i = 0; i < 50; i++)
                {
                    if (process.HasExited) break;
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            catch (ArgumentException)
            {
                // 已经退出了，正好
            }
            catch (Exception ex)
            {
                Say($"停 PID {target.Pid} 失败：{ex.Message}");
                return false;
            }
        }

        // 端口释放确认
        for (var i = 0; i < 30; i++)
        {
            if (!HasListener(port)) return true;
            await Task.Delay(100).ConfigureAwait(false);
        }

        Say($"进程已经停掉，但端口 {port} 还没释放。");
        return false;
    }
}
