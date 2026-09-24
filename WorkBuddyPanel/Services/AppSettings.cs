using System.IO;
using System.Text.Json;

namespace WorkBuddyPanel.Services;

/// <summary>
/// 界面自己的配置，存在 %APPDATA%\WorkBuddyPanel\settings.json。
/// 与 workbuddy_api 自己的 ~/.workbuddy-api/config.json 互不干扰。
/// </summary>
public sealed class AppSettings
{
    public string? NodePath { get; set; }
    public string? ApiDirectory { get; set; }
    public int Port { get; set; } = 8787;
    /// <summary>workbuddy（国内版）| workbuddy-ai（国际版）</summary>
    public string Provider { get; set; } = "workbuddy";
    public string ApiKey { get; set; } = "sk-workbuddy-local";
    public string? LastModel { get; set; }
    public string? AuthFile { get; set; }

    /// <summary>关窗口时收进托盘（true）还是直接退出（false）。默认收进托盘。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>程序启动时自动拉起网关。</summary>
    public bool AutoStartGateway { get; set; } = true;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkBuddyPanel");

    /// <summary>配置文件位置，写日志用。</summary>
    public static string Location => Path.Combine(Dir, "settings.json");

    private static string FilePath => Location;

    /// <summary>上次读取配置失败的原因（正常时为 null）。</summary>
    public static string? LastLoadError { get; private set; }

    /// <summary>上次读取的配置文件全量内容是否存在（true = 有文件）。</summary>
    public static bool LastLoadFoundFile { get; private set; }

    /// <summary>读坏时保留下来的备份路径。</summary>
    public static string? LastBackupPath { get; private set; }

    /// <summary>配置被读坏时的备份文件名。</summary>
    public const string BrokenSuffix = ".broken";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load()
    {
        LastLoadError = null;
        LastLoadFoundFile = false;
        LastBackupPath = null;

        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();

            LastLoadFoundFile = true;
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (loaded is not null)
            {
                if (string.IsNullOrWhiteSpace(loaded.ApiKey)) loaded.ApiKey = "sk-workbuddy-local";
                if (loaded.Port is < 1 or > 65535) loaded.Port = 8787;
                return loaded;
            }

            LastLoadError = "配置文件内容不是有效的设置对象";
        }
        catch (Exception ex)
        {
            // 配置坏了不能拦着程序启动，但绝不能默默丢掉用户原来的配置
            LastLoadError = ex.Message;
        }

        // 先备一份再走默认值：否则随后的 Save() 会把用户原配置直接覆写掉。
        // （手工改配置文件写错一个反斜杠就会走到这里，JSON 里 \ 必须写成 \\）
        try
        {
            var backup = FilePath + BrokenSuffix;
            File.Copy(FilePath, backup, overwrite: true);
            LastBackupPath = backup;
        }
        catch
        {
            // 备不了就算了，至少日志里能看见原因
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
