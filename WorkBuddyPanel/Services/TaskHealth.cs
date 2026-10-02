using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkBuddyPanel.Services;
/// <summary>
/// 计划任务的健康检查。
///
/// ## 为什么需要它
///
/// 计划任务最大的问题是**它坏了不告诉你**。实测踩到的场景：
///
///     任务注册时指向 D:\dev\publish.next\WorkBuddyPanel.exe
///     后来那个目录被清空了（换版本、换安装位置）
///     任务照样"已注册"、"下次运行时间"也正常
///     但每次触发都是静默失败 —— 你的账号一直在掉登录，没人知道
///
/// 界面当时只显示「已注册每日自动续期」，看着一切正常。
///
/// ## 查什么
///
///   1. 目标 exe **还在不在**（最常见的问题 —— 换了目录/版本就失效）
///   2. 任务的上次运行结果（0 = 成功，非 0 = 失败码）
///   3. 上次运行有没有留下维护日志（跑了但没记录也是异常）
///
/// ## 不求完美
///
/// 任何一项查不到就降级显示，不能因为"读不到 Last Result"就报错 ——
/// schtasks 的输出格式随系统语言变，匹配不上很正常。
/// </summary>
public static class TaskHealth
{
    public enum State
    {
        /// <summary>没注册。这不是错误，只是没开。</summary>
        NotRegistered,

        /// <summary>注册了、目标存在、上次跑成功了。</summary>
        Healthy,

        /// <summary>注册了，但目标 exe 不在了 —— 任务永远不会生效。</summary>
        TargetMissing,

        /// <summary>注册了、目标在，但上次运行失败了。</summary>
        LastRunFailed,

        /// <summary>注册了，但拿不到运行信息（可能从没跑过）。</summary>
        Unknown,
    }

    public sealed record Result(
        State State,
        string Summary,
        string? TargetPath,
        string? LastRunResult,
        string? LastRunTime,
        string? NextRunTime)
    {
        /// <summary>要不要在界面上标红。</summary>
        public bool NeedsAttention => State is State.TargetMissing or State.LastRunFailed;

        /// <summary>是不是真的在正常工作。</summary>
        public bool IsWorking => State == State.Healthy;
    }

    public static Result Check()
    {
        if (!ScheduledTask.IsRegistered())
        {
            return new Result(State.NotRegistered,
                "还没开启自动续期。开启后每天自动续签，登录状态不会过期。",
                null, null, null, null);
        }

        var info = QueryVerbose();

        // ── 1. 目标 exe 还在吗（最要紧的一项）──
        var target = info.Target;
        if (!string.IsNullOrWhiteSpace(target))
        {
            // schtasks 的 "Task To Run" 形如：
            //   D:\path\app.exe --maintain
            //   或带引号："D:\my path\app.exe" --maintain
            var exe = ExtractExe(target);

            if (exe is not null && !File.Exists(exe))
            {
                return new Result(State.TargetMissing,
                    $"⚠ 自动续期已失效：任务指向的程序不存在了。\n" +
                    $"　 它找的是：{exe}\n" +
                    $"　 重新点一次「开启自动续期」就会指向当前程序。",
                    target, info.LastResult, info.LastRunTime, info.NextRunTime);
            }
        }

        // ── 2. 上次运行结果 ──
        //    0x0 = 成功；0x41301 = 正在运行；0x41303 = 从没跑过（不算失败）
        var last = info.LastResult;
        if (!string.IsNullOrWhiteSpace(last))
        {
            var code = ParseResultCode(last);

            if (code is not null and not 0
                and not 0x41301     // 正在运行
                and not 0x41303)    // 还没跑过
            {
                return new Result(State.LastRunFailed,
                    $"⚠ 自动续期上次运行失败了（代码 {code:X}）。\n" +
                    $"　 {DescribeResultCode(code.Value)}\n" +
                    $"　 上次运行：{info.LastRunTime ?? "未知"}\n" +
                    $"　 点「立即跑一次」看看具体报什么错（在「日志」标签页）。",
                    target, last, info.LastRunTime, info.NextRunTime);
            }
        }

        // ── 3. 一切正常 ──
        var parts = new List<string> { "已开启每日自动续期" };
        if (info.NextRunTime is { Length: > 0 })
            parts.Add($"下次 {info.NextRunTime}");
        if (info.LastRunTime is { Length: > 0 })
            parts.Add($"上次 {info.LastRunTime}");

        return new Result(State.Healthy,
            string.Join("　·　", parts),
            target, last, info.LastRunTime, info.NextRunTime);
    }

    private sealed record VerboseInfo(
        string? Target, string? LastResult, string? LastRunTime, string? NextRunTime);

    /// <summary>
    /// 解析 `schtasks /query /v` 的输出。
    ///
    /// ⚠ 输出是**本地化**的（中文系统给中文标签），所以按多种可能的标签匹配。
    /// 匹配不到就返回 null，不报错。
    ///
    /// ⚠ 中文系统的实际标签（实测抓的，跟英文的不一样）：
    ///
    ///     要运行的任务:     D:\...\app.exe --maintain
    ///     上次结果:         -2147024894          ← 注意不是“上次运行结果”
    ///     上次运行时间:      2026/9/26 12:54:26
    ///     下次运行时间:      2026/9/27 12:00:00
    /// </summary>
    private static VerboseInfo QueryVerbose()
    {
        string stdout;
        try
        {
            stdout = ScheduledTask.QueryVerboseText();
        }
        catch
        {
            return new VerboseInfo(null, null, null, null);
        }

        return new VerboseInfo(
            FindValue(stdout, "要运行的任务", "Task To Run"),
            // 注意：中文是「上次结果」，不是「上次运行结果」
            FindValue(stdout, "上次结果", "Last Result"),
            FindValue(stdout, "上次运行时间", "Last Run Time"),
            FindValue(stdout, "下次运行时间", "Next Run Time"));
    }

    /// <summary>
    /// 从一行里取值。标签和值之间用「:」分隔（中英文都是冒号）。
    /// </summary>
    private static string? FindValue(string text, params string[] labels)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            foreach (var label in labels)
            {
                if (!trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;

                var idx = trimmed.IndexOfAny([':', '：']);
                if (idx < 0) continue;

                var value = trimmed[(idx + 1)..].Trim();
                return value.Length > 0 ? value : null;
            }
        }
        return null;
    }

    /// <summary>
    /// 从 "D:\a\app.exe --maintain" 里抠出 exe 路径。
    /// 路径可能带引号（有空格时 schtasks 会加）。
    /// </summary>
    private static string? ExtractExe(string taskToRun)
    {
        var s = taskToRun.Trim();
        if (s.Length == 0) return null;

        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }

        // 没引号：取到第一个 ".exe" 为止
        var exeIdx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIdx >= 0 ? s[..(exeIdx + 4)] : s;
    }

    /// <summary>
    /// 把 "0x0" / "0" / "1" 这类文本转成数字。
    /// </summary>
    private static uint? ParseResultCode(string text)
    {
        var s = text.Trim();

        // 有的系统会写 "0x0"，也有的写 "0"
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex)
                ? hex : null;
        }

        // 注意：不带 0x 的也可能真有 0x41301 这种大数，但那在 schtasks 里通常带 0x
        return uint.TryParse(s, out var dec) ? dec : null;
    }

    /// <summary>
    /// 把常见的失败码翻译成人话。
    ///
    /// 用 uint 比 —— 0x80070002 这类 HRESULT 超出 int 正数范围。
    /// </summary>
    private static string DescribeResultCode(uint code) => code switch
    {
        0x1 => "任务执行时出错（多半是程序本身报错，看日志）",
        0x2 => "找不到文件（任务的程序路径不对）",
        0x41300 => "任务就绪但没运行",
        0x41304 => "任务实例不存在",
        0x80070002 => "系统找不到指定的文件",
        0x80070005 => "拒绝访问（需要管理员权限）",
        0x800710E0 => "操作员或管理员拒绝了请求",
        _ => "详情看「日志」标签页",
    };

    /// <summary>
    /// 读维护日志的最后一条，看看上次维护是什么时候、成不成功。
    /// 界面用来回答「它到底跑没跑」。
    /// </summary>
    public static (DateTime? When, bool Ok, string Text)? LastMaintenance()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WorkBuddyPanel", "maintain.log");

            if (!File.Exists(path)) return null;

            var lines = File.ReadAllLines(path);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;

                // 格式：[2026-09-25 11:22:27] 维护完成：... （exit=0）
                var m = Regex.Match(line, @"^\[([\d\-: ]+)\]\s*(.*)$");
                if (!m.Success) continue;

                if (!DateTime.TryParse(m.Groups[1].Value, out var when)) continue;

                var body = m.Groups[2].Value;
                var ok = !body.Contains("exit=", StringComparison.Ordinal)
                         || body.Contains("exit=0", StringComparison.Ordinal);

                return (when, ok, line);
            }
        }
        catch
        {
            // 读日志失败不影响别的
        }
        return null;
    }
}
