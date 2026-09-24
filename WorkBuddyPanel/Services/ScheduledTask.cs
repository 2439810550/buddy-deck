using System.Diagnostics;
using System.IO;
using System.Text;

namespace WorkBuddyPanel.Services;

/// <summary>
/// 注册一个每天的 Windows 计划任务，让本程序在后台自己续期 + 签到。
///
/// 为什么需要它：WorkBuddy 的登录是**滑动窗口** —— 每次刷新都会重新给一个
/// 满额 60 天的 refreshToken。只要在窗口内刷一次就还能再续 60 天。
/// 但“靠你记得手动点”等于没有保障，所以挂个计划任务，天天自动跑。
///
/// 任务内容就是 `WorkBuddyPanel.exe --maintain`：不弹窗口，跑完续期+签到就退出。
/// </summary>
public static class ScheduledTask
{
    public const string TaskName = "WorkBuddyPanel 每日自动续期";
    private const string DailyTime = "12:00";

    /// <summary>任务是否已注册。</summary>
    public static bool IsRegistered()
    {
        try
        {
            var result = Run($"/Query /TN \"{TaskName}\"");
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前注册的启动命令，写日志用。</summary>
    public static string Describe()
    {
        try
        {
            var result = Run($"/Query /TN \"{TaskName}\" /FO LIST /V");
            if (result.ExitCode != 0) return "(未注册)";
            foreach (var line in result.StdOut.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("要运行的任务", StringComparison.Ordinal)
                    || trimmed.StartsWith("Task To Run", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed;
                }
            }
            return "(已注册)";
        }
        catch
        {
            return "(查询失败)";
        }
    }

    /// <summary>
    /// 注册（或覆盖）每日任务。
    ///
    /// 用 XML 而不是 /TR 参数，是因为只有 XML 才能打开 `StartWhenAvailable` ——
    /// 也就是“错过了就开机后补跑”。电脑不会 24 小时开着，这一项不能少。
    /// </summary>
    public static string Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            throw new InvalidOperationException("拿不到本程序的可执行文件路径。");

        var xmlPath = Path.Combine(Path.GetTempPath(), $"workbuddy-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks 只认 UTF-16 的 XML
            File.WriteAllText(xmlPath, BuildXml(exe), new UnicodeEncoding(false, true));

            var result = Run($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"注册计划任务失败：{result.Combined.Trim()}\n\n" +
                    "如果提示拒绝访问，用管理员身份运行本程序再试一次。");
            }

            return $"已注册每天 {DailyTime} 的自动续期任务（错过时间会在下次开机后补跑）";
        }
        finally
        {
            try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { /* 临时文件删不掉就算了 */ }
        }
    }

    public static string Remove()
    {
        var result = Run($"/Delete /TN \"{TaskName}\" /F");
        if (result.ExitCode != 0 && IsRegistered())
            throw new InvalidOperationException($"删除计划任务失败：{result.Combined.Trim()}");
        return "已删除每日自动续期任务";
    }

    /// <summary>立刻跑一次（用来看任务配得对不对）。</summary>
    public static void RunNow()
    {
        var result = Run($"/Run /TN \"{TaskName}\"");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"启动任务失败：{result.Combined.Trim()}");
    }

    private static string BuildXml(string exe)
    {
        // 明天的同一时刻开始，之后每天一次
        var start = DateTime.Now.Date.AddDays(1)
            .AddHours(int.Parse(DailyTime.Split(':')[0]))
            .AddMinutes(int.Parse(DailyTime.Split(':')[1]))
            .ToString("yyyy-MM-dd'T'HH:mm:ss");

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>每天为保险库里的 WorkBuddy 账号续期并签到（滑动窗口续期，防止掉登录）</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{start}</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByDay>
                    <DaysInterval>1</DaysInterval>
                  </ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(exe)}</Command>
                  <Arguments>--maintain</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static (int ExitCode, string StdOut, string Combined) Run(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("启动 schtasks.exe 失败");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (process.ExitCode, stdout, $"{stdout}\n{stderr}".Trim());
    }
}
