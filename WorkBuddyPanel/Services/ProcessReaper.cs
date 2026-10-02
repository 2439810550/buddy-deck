using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;

namespace WorkBuddyPanel.Services;

/// <summary>
/// 找出「占用某个端口、而且确实是我们的网关」的那个进程，并停掉它。
///
/// ## 为什么需要它
///
/// 真实事故：
///
/// <code>
/// 8787 上跑着一个几小时前启动的网关（孤儿进程，父面板已经退出）。
/// 代码后来改了好几轮 —— 视频从「当成生图处理」改成了真正的视频生成。
/// 但那个进程不会自动重载，它还在跑旧代码。
///
/// 用户重启面板 → 面板看到「端口上有人在监听」→ 采用它
/// → 功能还是旧的。用户看到的是「视频还是生成不了」，
/// 完全不知道为什么。
/// </code>
///
/// 所以面板需要一个能力：**发现旧网关就把它换成新的**。
///
/// ## ⚠ 安全约束（这份代码存在的主要理由）
///
/// 杀掉错误的进程后果极严重。所以只在**三重匹配**之后动手：
///
/// <code>
///   ① 进程名是 node.exe
///   ② 命令行含 bin.js 且含 serve        （是我们的网关，不是别的 node 程序）
///   ③ 命令行含 --port &lt;这个端口&gt;        （是**这个端口**的网关，不是别的池的）
/// </code>
///
/// 三条全过才动手。**任何一条拿不到，一律不动手**并如实返回失败。
///
/// ## 关于「工作目录」这个判据（曾经想用，放弃了）
///
/// 本来想再加一条「工作目录必须是配的那个 ApiDirectory」，理由是
/// 多池场景下每个池一个网关实例，命令行长得几乎一样（区别在环境变量
/// `WORKBUDDY_API_HOME`，不在命令行里），光看命令行分不开。
///
/// 但**放弃了**，因为拿不到：.NET 里没有可靠 API 读别的进程的当前目录
/// （要读对方的 PEB，得写 P/Invoke 加结构体偏移，脆弱且容易出错）。
///
/// 也不能假装做了这个检查 —— 安全检查里放一个永远返回空的方法，
/// 比不检查更危险，因为它会让人以为有这层保护。
///
/// 所以：**只用命令行判据**，并在这里写清楚它的边界：
///
/// <code>
/// 极限情况：如果用户同时开了两个池，两边都配了同一个端口
/// （正常情况下 PoolStore.PickPort 会避免），那可能会停掉错的那个。
/// 但那种配置本身是错的 —— 两个网关抢同一端口，本来也起不来。
/// </code>
///
/// ## 为什么不用 taskkill / netstat 命令
///
/// 那些要解析本地化输出（中文系统的「LISTENING」和英文的不一样），
/// 而且引号/编码问题一堆。这里用 .NET 自己的 API，拿到的都是结构化数据。
/// </summary>
public static class ProcessReaper
{
    /// <summary>要动一个进程之前先确认它是谁。</summary>
    public sealed record Candidate(int Pid, string CommandLine);

    /// <summary>
    /// 找出命令行匹配「我们的网关 + 这个端口」的 node 进程。
    ///
    /// 刻意**不**用 `GetExtendedTcpTable` 那种 P/Invoke 去查「谁在监听
    /// 这个端口」：那样能拿到 PID，但还得再补一次「这个 PID 是不是我们」的
    /// 判断。既然本来就要读命令行，一次枚举做完两件事更简单，也不用碰
    /// P/Invoke（那种代码在 32/64 位和不同 Windows 版本上出问题的概率不低）。
    /// </summary>
    private static List<Candidate> FindGatewayProcesses(int port)
    {
        var results = new List<Candidate>();
        var portTokens = new[] { $"--port {port}", $"--port={port}" };

        foreach (var process in Process.GetProcessesByName("node"))
        {
            int pid = process.Id;
            process.Dispose();

            var commandLine = QueryCommandLine(pid);
            if (commandLine.Length == 0) continue;

            if (!commandLine.Contains("bin.js", StringComparison.OrdinalIgnoreCase)) continue;
            if (!commandLine.Contains("serve", StringComparison.OrdinalIgnoreCase)) continue;
            if (!portTokens.Any(t => commandLine.Contains(t, StringComparison.Ordinal))) continue;

            results.Add(new Candidate(pid, commandLine));
        }

        return results;
    }

    /// <summary>用 WMI 拿一个进程的完整命令行。拿不到返回空串。</summary>
    private static string QueryCommandLine(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                using (item)
                {
                    return item["CommandLine"]?.ToString() ?? "";
                }
            }
        }
        catch
        {
            // WMI 拿不到（权限、被禁用、进程刚退出）—— 调用方当成「认不出」
        }
        return "";
    }

    /// <summary>
    /// 这个端口上有没有人监听。
    ///
    /// 只看有没有 listener，不管是谁 —— 「是谁」由 FindGatewayProcesses 回答。
    /// </summary>
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
            // 拿不到就当作没占用，让 node 自己去报错
            return false;
        }
    }

    /// <summary>
    /// 端口被占，但占它的**不是我们的网关**。
    ///
    /// 用来区分两种失败：我们的旧网关（可以换掉）vs 别的程序（只能改端口）。
    /// </summary>
    public static bool HasForeignListener(int port)
        => HasListener(port) && FindGatewayProcesses(port).Count == 0;

    /// <summary>
    /// 停掉端口上那个旧网关，好让调用方起一个新的。
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
            // 端口有人监听，但认不出是我们的网关 —— **绝不能动**
            Say($"端口 {port} 上有程序在监听，但认不出是我们的网关，不动它。");
            return false;
        }

        if (candidates.Count > 1)
        {
            Say($"注意：端口 {port} 上有 {candidates.Count} 个匹配的网关进程："
                + string.Join(", ", candidates.Select(c => c.Pid)));
        }

        foreach (var target in candidates)
        {
            Say($"停掉旧网关：PID {target.Pid}");
            Say($"  命令行：{target.CommandLine}");

            try
            {
                using var process = Process.GetProcessById(target.Pid);
                process.Kill(entireProcessTree: true);

                for (var i = 0; i < 50; i++)
                {
                    if (process.HasExited) break;
                    await Task.Delay(100);
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

        // 端口释放确认 —— 这一步很重要。
        //
        // 不带这个确认的话，「杀了」和「能起新的」之间有个窗口：
        // 新的 node 因为端口还被占直接退出，然后流程又走回「采用旧网关」，
        // 表现得像「重启了但还是旧的」，比不重启更让人困惑。
        for (var i = 0; i < 30; i++)
        {
            if (!HasListener(port)) return true;
            await Task.Delay(100);
        }

        Say($"进程已经停掉，但端口 {port} 还没释放。");
        return false;
    }
}
