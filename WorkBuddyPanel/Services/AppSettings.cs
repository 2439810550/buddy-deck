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

    /// <summary>账号页自动刷新积分。</summary>
    public bool AccountsAutoRefresh { get; set; } = true;

    /// <summary>
    /// 开机自启的**上次已知状态**。
    ///
    /// 真实值在注册表里，但读注册表要 ~12ms，而这是在窗口出现之前 ——
    /// 所以启动时先用这个缓存值把开关摆上，真实值等窗口显示后在后台校正。
    /// 注册表仍然是唯一真相，这只是为了不拖慢启动。
    /// </summary>
    public bool AutoStartWithWindows { get; set; }

    // ── 发布版的默认运行环境 ───────────────────────────────────
    //
    // 发布包的结构是固定的：exe 旁边一个 runtime 子目录，里面装着
    // node.exe 和一份 workbuddy_api。所以发布版根本不需要「自动探测」——
    // 默认值就写死成那两个相对路径，用户开箱即用。
    //
    // 相对路径是相对 **exe 所在目录**（不是进程的当前目录）—— 计划任务
    // 拉起 --maintain 时当前目录可能是 System32。展开在 ProcessLocator 里做。
    //
    // 调试版不设默认值：开发时那份运行时不在 exe 旁边，硬塞一个不存在的
    // 路径只会让「自动探测」和体检报告都变糊涂。

    /// <summary>发布版默认的 node.exe（相对 exe 所在目录）。</summary>
    public const string DefaultNodePathRelative = @".\runtime\node.exe";

    /// <summary>发布版默认的 workbuddy_api 目录（相对 exe 所在目录）。</summary>
    public const string DefaultApiDirectoryRelative = @".\runtime\workbuddy_api";

#if DEBUG
    private const bool BuiltAsRelease = false;
#else
    private const bool BuiltAsRelease = true;
#endif

    /// <summary>
    /// 当前这个 exe 是不是发布版（Release）编出来的。
    ///
    /// 值在编译期就定死了（publish 默认走 Release，比运行时反射可靠），
    /// 但**故意是 static readonly 而不是 const**：const 会被折进调用点，
    /// 于是调试版里 `if (IsReleaseBuild)` 的另一半就成了"检测到无法访问的代码"
    /// （CS0162），每个用到它的地方都刷一条警告。static readonly 只是普通字段，
    /// JIT 照样能把它当常量优化掉。
    /// </summary>
    public static readonly bool IsReleaseBuild = BuiltAsRelease;

    /// <summary>发布版默认的 node.exe；调试版返回 null（不设默认值）。</summary>
    public static string? DefaultNodePath => IsReleaseBuild ? DefaultNodePathRelative : null;

    /// <summary>发布版默认的 workbuddy_api 目录；调试版返回 null。</summary>
    public static string? DefaultApiDirectory => IsReleaseBuild ? DefaultApiDirectoryRelative : null;

    /// <summary>
    /// 把「用户没填」的位置补上发布版默认值。
    /// 只填空位，绝不覆盖用户自己填过的东西。
    /// </summary>
    public void ApplyRuntimeDefaults()
    {
        // 用字段而不是 DefaultNodePath 属性：两者的值一样，但走属性的话
        // 调试版里这一步会被折成"什么都不做"，可读性反而差
        if (!IsReleaseBuild) return;

        if (string.IsNullOrWhiteSpace(NodePath)) NodePath = DefaultNodePathRelative;
        if (string.IsNullOrWhiteSpace(ApiDirectory)) ApiDirectory = DefaultApiDirectoryRelative;
    }

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
            if (!File.Exists(FilePath)) return WithRuntimeDefaults(new AppSettings());

            LastLoadFoundFile = true;
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (loaded is not null)
            {
                if (string.IsNullOrWhiteSpace(loaded.ApiKey)) loaded.ApiKey = "sk-workbuddy-local";
                if (loaded.Port is < 1 or > 65535) loaded.Port = 8787;
                return WithRuntimeDefaults(loaded);
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

        return WithRuntimeDefaults(new AppSettings());
    }

    /// <summary>补上发布版的默认运行环境，再交给调用方。</summary>
    private static AppSettings WithRuntimeDefaults(AppSettings settings)
    {
        settings.ApplyRuntimeDefaults();
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
