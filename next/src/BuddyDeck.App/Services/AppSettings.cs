using System.Text.Json;
using BuddyDeck.Core;

namespace BuddyDeck.App.Services;

/// <summary>
/// 界面自己的配置（端口 / key / 变体 / 账号库路径）。
///
/// ## 与网关 settings.json 的区别（两个文件，别混）
///
/// 网关的 <c>settings.json</c>（与保险库同目录）存的是**网关进程**的启动参数。
/// 这个文件存的是**面板**怎么拉起网关：用哪个端口、给什么 key、账号库在哪。
/// 旧版 WorkBuddyPanel 也是这么分的（`%APPDATA%\WorkBuddyPanel\settings.json`
/// 对 `~/.workbuddy-api\config.json`）。
///
/// 分开的理由：面板要**先于**网关存在才能决定网关怎么起 ——
/// 读网关的 settings 需要网关先起（鸡生蛋）。面板自己的配置必须先有。
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// 面板拉起网关用的端口。
    ///
    /// ⚠ 默认 **8791** 而不是 8787：8787 是旧版 Node 网关的默认端口，
    /// 而本机那个旧网关是 AI 助手自己的模型来源，**绝不能动**。
    /// 新版网关默认用 8791，开箱即用，不撞旧网关。
    /// 用户要连回 8787（旧网关关掉之后）就自己在设置里改。
    /// </summary>
    public int Port { get; set; } = 8791;

    /// <summary>网关要求的 Bearer key。空 = 不鉴权。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>变体 id（workbuddy / workbuddy-ai）。</summary>
    public string VariantId { get; set; } = "workbuddy";

    /// <summary>
    /// 账号库（保险库）的绝对路径。空 = 用旧版账号库（~/.workbuddy-api/accounts.cn.json）。
    ///
    /// 为什么默认是旧版账号库：用户已经有一份 20 个号的库在那儿了，
    /// 新版第一次启动就该看到它们，而不是让用户重新导入一遍。
    /// </summary>
    public string VaultPath { get; set; } = "";

    /// <summary>关窗口收进托盘（true）还是直接退出（false）。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>启动时自动拉起网关。</summary>
    public bool AutoStartGateway { get; set; } = true;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BuddyDeck");

    /// <summary>
    /// 设置数据库的位置（固定：保险库同目录）。
    ///
    /// ⚠ 设置不再存 <c>%APPDATA%\BuddyDeck\settings.json</c>，
    /// 而是存进 <c>~/.workbuddy-api/buddydeck.db</c> 的 meta 表
    /// （key=<c>app_settings</c>，value=完整 JSON）。
    /// 这样设置和账号、积分、任务同一份数据库，不依赖散文件。
    ///
    /// 为什么位置固定（不随 VaultPath 走）：VaultPath 本身就是设置的一项，
    /// 存哪取决于 VaultPath 会变成鸡生蛋。所以设置库固定放在保险库默认目录。
    /// </summary>
    private static string DatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".workbuddy-api",
        "buddydeck.db");

    private const string SettingsKey = "app_settings";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 读的时候大小写不敏感 —— 手改配置文件时首字母大小写不影响。
        PropertyNameCaseInsensitive = true,
    };

    public static AppSettings Load()
    {
        try
        {
            // ★ 从数据库 meta 表读设置（不再是 settings.json）。
            using var db = new Database(DatabasePath);
            db.Initialize();
            var json = db.GetMeta(SettingsKey);
            if (string.IsNullOrWhiteSpace(json)) return WithDefaults(new AppSettings());

            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (loaded is not null)
            {
                if (loaded.Port is < 1 or > 65535) loaded.Port = 8791;
                return WithDefaults(loaded);
            }
        }
        catch
        {
            // 配置坏了不能拦着启动 —— 退回默认值，下次保存会覆盖。
        }

        return WithDefaults(new AppSettings());
    }

    private static AppSettings WithDefaults(AppSettings s)
    {
        // 默认账号库：旧版的 accounts.cn.json（用户已有的账号）。
        if (string.IsNullOrWhiteSpace(s.VaultPath))
        {
            s.VaultPath = AccountImport.DefaultLegacyPath;
        }
        return s;
    }

    public void Save()
    {
        // ★ 存进数据库 meta 表（不再是 settings.json）。
        using var db = new Database(DatabasePath);
        db.Initialize();
        db.SetMeta(SettingsKey, JsonSerializer.Serialize(this, Options));
    }
}
