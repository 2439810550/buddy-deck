using Microsoft.Win32;

namespace WorkBuddyPanel.Services;

/// <summary>
/// 开机自启：往 HKCU 的 Run 键写一条 `"<exe>" --tray`。
/// 用 --tray 启动时程序直接进托盘、不弹窗口，用户开完机就有 API，全程看不到界面。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WorkBuddyPanel";
    private const string TrayArgument = "--tray";

    /// <summary>当前是否已登记开机自启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static void Enable()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            throw new InvalidOperationException("拿不到本程序的可执行文件路径，无法设置开机自启。");

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                            ?? throw new InvalidOperationException("打不开注册表的 Run 键。");
            key.SetValue(ValueName, $"\"{exe}\" {TrayArgument}");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"设置开机自启失败：{ex.Message}", ex);
        }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 关不掉就算了，下次还能关
        }
    }

    /// <summary>当前登记的完整命令行，写日志用。</summary>
    public static string Describe()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string ?? "(未设置)";
        }
        catch
        {
            return "(读取失败)";
        }
    }
}
