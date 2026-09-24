using System.IO;

namespace WorkBuddyPanel.Services;

/// <summary>
/// CC Switch 集成（界面侧）。
///
/// ## 为什么需要
///
/// CC Switch 是「供应商管理器」，它把每个供应商的配置存在自己库里，
/// **切换时整体覆盖**对应 agent 的配置文件。所以如果我们只改 agent 的配置文件、
/// 不告诉 CC Switch：
///
///   1. 用户在 CC Switch 里一切换，我们写的就被覆盖掉
///   2. CC Switch 的列表里看不见 WorkBuddy，用户以为没配
///
/// ## 谁负责什么
///
/// 实际读写 SQLite 在 `workbuddy_api/src/ccswitch.js` 里 ——
/// 桌面端只是壳，不重复实现协议/存储逻辑（和网关其它部分的分工一致）。
/// 这里只负责：找到库、判断装没装、给界面提供几句人话。
/// </summary>
public static class CcSwitch
{
    /// <summary>库文件位置。跟 workbuddy_api 里的规则保持一致（CCSWITCH_DB 可覆盖）。</summary>
    public static string DbPath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("CCSWITCH_DB");
            if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath.Trim();
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cc-switch",
                "cc-switch.db");
        }
    }

    public static bool IsAvailable => File.Exists(DbPath);

    /// <summary>CC Switch 的可执行文件（「打开 CC Switch」按钮用）。</summary>
    public static string? FindExecutable()
    {
        foreach (var guess in new[]
                 {
                     @"D:\CCSwitch\cc-switch.exe",
                     @"C:\Program Files\CC Switch\cc-switch.exe",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Programs", "CC Switch", "cc-switch.exe"),
                 })
        {
            if (File.Exists(guess)) return guess;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (key is not null)
            {
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var item = key.OpenSubKey(sub);
                    if (item?.GetValue("DisplayName") as string != "CC Switch") continue;
                    var location = item.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(location)) continue;
                    var exe = Path.Combine(location.TrimEnd('\\'), "cc-switch.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        catch
        {
            // 读不到就算了，按钮会提示没找到
        }

        return null;
    }
}
