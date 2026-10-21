using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Core;

/// <summary>
/// 落盘的设置文档。
///
/// ## 键名对齐旧版 `config.js`
///
/// 旧版的键是 `provider/host/port/apiKey/authFile/authFileAI/defaultModel`
/// （见 `config.js:19-27` 的 `DEFAULTS`）。这里保留同名键，理由和
/// `GatewayOptions` 的类注释一样：**用户的现存配置文件是按那套名字写的**，
/// 换名字等于让所有已存的配置失效。
///
/// ## 与旧版的一处有意差异
///
/// 旧版 `loadConfig` **总是把所有字段填满**返回 —— 也就是说它分不清
/// 「文件里真写了这个键」和「这是默认值」。这里把这件事显式化：
/// **没被显式改过的字段根本不写进文件**（见 <see cref="SettingsStore.Save"/>）。
/// </summary>
public sealed record SettingsDocument
{
    /// <summary>文档版本。写时带上、读时校验 —— 将来改结构要能识别旧文件。</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = SettingsStore.CurrentVersion;

    /// <summary>监听端口。</summary>
    [JsonPropertyName("port")]
    public int? Port { get; init; }

    /// <summary>监听地址。</summary>
    [JsonPropertyName("host")]
    public string? Host { get; init; }

    /// <summary>网关要求的 Bearer key。</summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; init; }

    /// <summary>变体 id（旧版叫 provider）。</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    /// <summary>保险库路径（旧版叫 authFile）。</summary>
    [JsonPropertyName("authFile")]
    public string? AuthFile { get; init; }

    /// <summary>默认模型。</summary>
    [JsonPropertyName("defaultModel")]
    public string? DefaultModel { get; init; }

    /// <summary>日志目录。</summary>
    [JsonPropertyName("logDir")]
    public string? LogDir { get; init; }
}

/// <summary>
/// 设置的持久化层（M14 的一半）。
///
/// ## 它只做两件事
///
/// ```
/// Save  把「用户显式改过的字段」原子写到文件
/// Load  从文件读回来（读不到 / 读坏都返回空文档，不抛）
/// ```
///
/// **优先级、来源、合并全部在 <see cref="OptionSources"/> 那一层**，这里不管。
/// 职责分开的好处：这一层的测试不需要构造任何优先级场景，
/// 那一层的测试不需要碰文件系统。
///
/// ## 为什么放在 Core 而不是 Gateway
///
/// 它**不依赖 ASP.NET Core** —— 只用到 `System.IO` / `System.Text.Json`。
/// 放 Core 意味着：
///   · 能被 `BuddyDeck.Core.Tests` 直接测（Gateway.Tests 的引用方向是单向的）
///   · 将来 CLI（旧版 `bin.js` 的 `config` 子命令）要用同一套读写时，不必引整个网关
///   · 界面侧若要只读展示设置，也不必拖进 ASP.NET Core
///
/// 网关侧的接线（把 `/admin/settings` 的 POST 接到这里）是**另一层的事**，
/// 不在本类型职责内。
///
/// ## 文件放哪
///
/// **与保险库同目录**。理由：
///
/// 1. **它们是一起被备份/迁移的**。用户换机器时，`auth.cn.json` 和他的设置
///    是同一批东西 —— 放两个目录会漏掉一个。
/// 2. **目录的权限语义一致**。保险库所在目录已被收紧过（只有属主可读），
///    而设置文件**含 API key**，需要同样的保护。新造一个目录就要重新论证权限。
/// 3. **不新造概念**。多一个"设置目录"就多一个要解释、要在文档里维护、
///    用户会问"这个又在哪"的东西。
///
/// 具体路径由 <see cref="PathBesideVault"/> 推导 —— 这一层**不依赖
/// `GatewayOptions`**（它在另一个程序集里）。
/// </summary>
public sealed partial class SettingsStore
{
    /// <summary>当前文档版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>设置文件名。</summary>
    public const string FileName = "settings.json";

    /// <summary>
    /// 写文件用的序列化选项。
    ///
    /// `WriteIndented` 是为了用户能直接看/手改（与 `Vault.Write` 一致）；
    /// `UnsafeRelaxedJsonEscaping` 让中文不转义成 `\uXXXX`
    /// （同样与 `Vault.Write` 一致 —— 用 cat 看要能读）；
    /// `WhenWritingNull` 与"没改过的字段不写"是同一件事的两面。
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Func<string> _path;
    private readonly ILogger<SettingsStore> _log;

    /// <summary>
    /// @param path 设置文件的路径。**传委托而不是字符串** —— 与
    ///             `VaultCredentialSource` 的 `Func&lt;string&gt;` 同一形状：
    ///             管理员在设置接口里改了保险库位置后，设置文件也该跟着走，
    ///             而不是停在启动时算出来的那个值。
    /// @param log  可空。测试传 null 会用 <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger{T}"/> 兜底
    ///             —— 这样字段本身非空，源生成的 <c>[LoggerMessage]</c> 不用每处判空。
    /// </summary>
    public SettingsStore(Func<string> path, ILogger<SettingsStore>? log = null)
    {
        _path = path;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance;
    }

    /// <summary>当前设置文件路径。</summary>
    public string Path => _path();

    /// <summary>
    /// 由保险库路径推导出设置文件路径 —— **同目录**。
    ///
    /// 这是「与保险库同目录」这条要求的唯一实现点。保险库路径为空
    /// （还没定）时退化到程序运行目录，与 `GatewayOptions.ResolveVaultPath`
    /// 的兜底保持同一套行为。
    /// </summary>
    public static string PathBesideVault(string vaultPath)
    {
        var directory = string.IsNullOrEmpty(vaultPath)
            ? AppContext.BaseDirectory
            : System.IO.Path.GetDirectoryName(vaultPath);

        if (string.IsNullOrEmpty(directory)) directory = AppContext.BaseDirectory;

        return System.IO.Path.Combine(directory, FileName);
    }

    /// <summary>
    /// 读一份设置。
    ///
    /// ## 四种"读不到"都归成空文档（不抛）
    ///
    /// ```
    /// 文件不存在          → 空文档（第一次运行，正常）
    /// 文件是空的/只有空白  → 空文档
    /// 文件是坏 JSON       → 空文档 + 记一条警告
    /// 合法 JSON 但不是对象 → 空文档
    /// ```
    ///
    /// **为什么不抛**：设置文件是**锦上添花**的东西。它坏了就让网关起不来，
    /// 是拿"少一个调优参数"换"整个服务不可用"—— 代价完全不对等。
    /// 这里退化成默认值继续跑，同时记一条警告让用户能发现。
    ///
    /// ⚠ 对比：**保险库**读不出来要报错（`VaultCredentialSource` 报空池），
    /// 因为那是"没有账号"这个业务事实。两者严重性不同，处置也就不同。
    /// </summary>
    public SettingsDocument Load()
    {
        string path;
        try
        {
            path = _path();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsDocument();
        }

        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new SettingsDocument();

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnreadable(ex, path);
            return new SettingsDocument();
        }

        if (string.IsNullOrWhiteSpace(text)) return new SettingsDocument();

        try
        {
            return JsonSerializer.Deserialize<SettingsDocument>(text, ReadOptions) ?? new SettingsDocument();
        }
        catch (JsonException ex)
        {
            LogUnreadable(ex, path);
            return new SettingsDocument();
        }
    }

    /// <summary>
    /// 原子写一份设置。
    ///
    /// ## 与 <see cref="Vault.Write"/> 保持同一套实现
    ///
    /// ```
    /// ① 先写**同目录**的 <path>.tmp
    /// ② File.Move(tmp, path, overwrite: true)   ← Windows 上原子
    /// ③ Unix 上收紧到 0600
    /// ```
    ///
    /// **为什么临时文件必须同目录**：跨卷的 `File.Move` 退化成"复制 + 删除"，
    /// 那就不是原子的了。设置文件里含 API key，写到一半断电同样是泄露面。
    ///
    /// **为什么权限要收 0600**：和保险库同理由 —— 文件里有 API key。
    /// Windows 上不起作用（NTFS 继承的 ACE 已经够严），Unix 上必须显式设。
    /// </summary>
    public void Save(SettingsDocument document)
    {
        var path = _path();
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(document, WriteOptions);

        // 临时文件放**同一个目录** —— 跨卷的 Move 不是原子的（与 Vault.Write 一致）
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // File.Move(overwrite) 在 Windows 上是原子的（MoveFileEx + REPLACE_EXISTING）
        File.Move(temp, path, overwrite: true);

        HardenPermissions(path);
    }

    /// <summary>
    /// 把各字段的值投影成「只含显式改过的字段」的可落盘文档。
    ///
    /// 这是「只落显式改过的字段」的落地点 —— 判据不是"这个值等于默认吗"
    /// （那是猜），而是**这个字段是否被显式改过**，由
    /// <see cref="SettingsMerge"/> 算出的字段集合决定。完整论证见
    /// <see cref="SettingsValues.ToDocument"/>。
    /// </summary>
    /// <param name="values">合并后的设置载荷。</param>
    /// <param name="persistable">该落盘的字段名集合（由合并层给出）。</param>
    public static SettingsDocument Project(SettingsValues values, IReadOnlySet<string> persistable)
        => values.ToDocument(persistable);

    /// <summary>
    /// 收紧文件权限。**与 <c>Vault.HardenPermissions</c> 同款处理** ——
    /// 两个文件都含敏感内容（这里是 API key），处置必须一致。
    ///
    /// 设不上不算失败：有些文件系统（FAT、网络盘）不支持 —— 不能因为设不上
    /// 权限就让保存失败，那会让用户在那种盘上用不了这个功能。
    /// </summary>
    private static void HardenPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "设置文件读不了（已退化成默认值）：{Path}")]
    private partial void LogUnreadable(Exception ex, string path);
}

/// <summary>
/// 设置字段名的**唯一真源**。
///
/// ## 为什么要有这个类
///
/// 字段名会出现在四个地方：文档的 JSON 键、合并层的来源映射、落盘投影、
/// 以及测试。任何一处写错都是**静默失配**（值看起来对，只是没生效）。
///
/// 用常量集中一次，四处引用同一份，写错就编译不过。
/// </summary>
public static class FieldNames
{
    public const string Port = "port";
    public const string Host = "host";
    public const string ApiKey = "apiKey";
    public const string Provider = "provider";
    public const string AuthFile = "authFile";
    public const string DefaultModel = "defaultModel";
    public const string LogDir = "logDir";

    /// <summary>全部可持久化的字段名。</summary>
    public static readonly IReadOnlyList<string> All =
        [Port, Host, ApiKey, Provider, AuthFile, DefaultModel, LogDir];

    /// <summary>
    /// 把一个**属性名**映射到**落盘键名**；对不上返回 null。
    ///
    /// ## 为什么靠约定而不是反射读 JsonPropertyName
    ///
    /// 本项目的设置类型（<see cref="SettingsValues"/>）不带
    /// `[JsonPropertyName]` —— 它不直接序列化，落盘由
    /// <see cref="SettingsDocument"/> 负责。所以两个类型之间的对应关系
    /// 只能由**命名约定**给出：属性名与键名只差首字母大小写
    /// （`Port` ↔ `port`、`ApiKey` ↔ `apiKey`、`AuthFile` ↔ `authFile`）。
    ///
    /// ## 为什么用白名单校验而不是直接 ToLower
    ///
    /// 直接 `ToLowerInvariant` 会为**任何**属性造出一个键名。将来有人给
    /// `SettingsValues` 加一个 `IsConfigured` 之类的派生属性，就会凭空多出
    /// 一个 `isconfigured` 键写进文件 —— 那种错误静默、且只在文件里能看出。
    ///
    /// 查一遍 <see cref="All"/> 的代价可以忽略，换来"写不认识的键"不可能发生。
    /// </summary>
    public static string? Match(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return null;

        var candidate = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        return All.Contains(candidate, StringComparer.Ordinal) ? candidate : null;
    }
}

/// <summary>
/// 三层来源合并用的设置载荷（纯数据，不依赖网关）。
///
/// ## 为什么它和 <see cref="SettingsDocument"/> 是两个类型
///
/// ```
/// SettingsDocument   落盘用的文档 —— 每个字段可空，null = "文件里没这个键"
/// SettingsValues     合并用的载荷 —— 每个字段可空，null = "这一层没给值"
/// ```
///
/// 形状几乎一样，但**语义不同、变化原因不同**：
/// 文档形状跟着"文件格式"变（加版本号、改键名），载荷形状跟着"有哪些设置项"变。
/// 合成一个类型的代价是：改文件格式会波及合并逻辑的测试。
///
/// 而且合并逻辑用**属性名**做字段标识（见 <see cref="SettingsMerge.ChangedFields"/>），
/// 所以载荷的**属性名必须与 <see cref="FieldNames"/> 的常量逐字相同**
/// （大小写敏感，因为落盘键名由 `SettingsDocument` 的
/// `[JsonPropertyName]` 决定，而这里靠属性名匹配）。
/// </summary>
public sealed record SettingsValues
{
    /// <summary>监听端口。</summary>
    public int? Port { get; init; }

    /// <summary>监听地址。</summary>
    public string? Host { get; init; }

    /// <summary>网关要求的 Bearer key。</summary>
    public string? ApiKey { get; init; }

    /// <summary>变体 id。</summary>
    public string? Provider { get; init; }

    /// <summary>保险库路径。</summary>
    public string? AuthFile { get; init; }

    /// <summary>默认模型。</summary>
    public string? DefaultModel { get; init; }

    /// <summary>日志目录。</summary>
    public string? LogDir { get; init; }

    /// <summary>
    /// 转成落盘文档 —— **只带 <paramref name="persistable"/> 里的字段**。
    ///
    /// 这一步把合并层的结论（哪些字段该存）变成实际的文件内容。
    /// </summary>
    public SettingsDocument ToDocument(IReadOnlySet<string> persistable)
        => new()
        {
            Version = SettingsStore.CurrentVersion,
            Port = persistable.Contains(FieldNames.Port) ? Port : null,
            Host = persistable.Contains(FieldNames.Host) ? Host : null,
            ApiKey = persistable.Contains(FieldNames.ApiKey) ? ApiKey : null,
            Provider = persistable.Contains(FieldNames.Provider) ? Provider : null,
            AuthFile = persistable.Contains(FieldNames.AuthFile) ? AuthFile : null,
            DefaultModel = persistable.Contains(FieldNames.DefaultModel) ? DefaultModel : null,
            LogDir = persistable.Contains(FieldNames.LogDir) ? LogDir : null,
        };

    /// <summary>从落盘文档还原成合并载荷。</summary>
    public static SettingsValues FromDocument(SettingsDocument document)
        => new()
        {
            Port = document.Port,
            Host = document.Host,
            ApiKey = document.ApiKey,
            Provider = document.Provider,
            AuthFile = document.AuthFile,
            DefaultModel = document.DefaultModel,
            LogDir = document.LogDir,
        };
}
