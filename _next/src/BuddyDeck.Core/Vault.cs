using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuddyDeck.Core;

/// <summary>
/// 保险库里的一条账号记录。
///
/// 比 <see cref="Credential"/> 多了几个「工具自己维护」的字段
/// （id / addedAt / lastSeenAt / lastCheckinAt）—— 那些不是上游给的，
/// 是我们记账用的，所以不放进凭证本体。
/// </summary>
public sealed record VaultEntry
{
    /// <summary>
    /// 稳定标识。**格式是 `uuid:enterpriseId`**（企业账号）或 `uuid:`（个人账号）。
    ///
    /// ⚠ 这个格式是历史包袱，不能改：界面、分池、轻量刷新的「就地更新」
    /// 都拿它做匹配键，换了格式会让旧保险库对不上。
    ///
    /// 注意它**不是** <see cref="Credential.AccountTag"/> —— 那个是
    /// `uid:enterpriseId`（用户 id），这个是随机 uuid。两者用途不同：
    /// 前者标识「哪个登录身份」，后者标识「保险库里的哪一条」。
    /// 同一个账号换过 token 后两条都会保留。
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("uid")]
    public string Uid { get; init; } = "";

    [JsonPropertyName("enterpriseId")]
    public string? EnterpriseId { get; init; }

    [JsonPropertyName("domain")]
    public string Domain { get; init; } = "";

    [JsonPropertyName("accessToken")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; init; } = "";

    [JsonPropertyName("expiresAtMs")]
    public long ExpiresAtMs { get; init; }

    [JsonPropertyName("refreshExpiresAtMs")]
    public long? RefreshExpiresAtMs { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    /// <summary>
    /// 入库时间。**ISO 8601 字符串**（如 `2026-09-24T09:35:12.642Z`），不是 Unix 毫秒。
    ///
    /// ⚠ 这三个字段的类型我第一版猜错了 —— 以为跟 `expiresAtMs` 一样是数字，
    /// 结果读真实保险库时直接抛 JsonException。是「读真实文件」那个测试抓到的：
    ///
    /// ```
    /// The JSON value could not be converted to Nullable&lt;Int64&gt;.
    /// Path: $.accounts[0].addedAt
    /// Cannot get the value of a token type 'String' as a number.
    /// ```
    ///
    /// 造数据永远发现不了这种错（我造的全是数字）。所以那个测试留着 ——
    /// 它比任何手写用例都值钱。
    /// </summary>
    [JsonPropertyName("addedAt")]
    public string? AddedAt { get; init; }

    [JsonPropertyName("lastSeenAt")]
    public string? LastSeenAt { get; init; }

    [JsonPropertyName("lastCheckinAt")]
    public string? LastCheckinAt { get; init; }

    /// <summary>转成纯凭证（给上游客户端用）。</summary>
    public Credential ToCredential() => new()
    {
        AccessToken = AccessToken,
        RefreshToken = RefreshToken,
        ExpiresAtMs = ExpiresAtMs,
        Uid = Uid,
        EnterpriseId = EnterpriseId,
        Domain = Domain,
        Label = Label,
    };
}

/// <summary>保险库文件。</summary>
public sealed record VaultDocument
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// 当前选中的那条记录的 id。
    ///
    /// 末尾那个冒号**是格式的一部分**（历史遗留），不是笔误。
    /// 解析时不要 trim。
    /// </summary>
    [JsonPropertyName("activeId")]
    public string ActiveId { get; init; } = "";

    [JsonPropertyName("accounts")]
    public List<VaultEntry> Accounts { get; init; } = [];
}

/// <summary>保险库读写。</summary>
public static class Vault
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 中文标签不转义 —— 用 cat 看的时候要能读
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // null 字段不写出来，保持文件和旧版一致（旧版也是省略）
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// 读一份保险库。
    ///
    /// @returns 文件不存在时返回 null（不是空文档）—— 调用方要能区分
    ///          「还没建库」和「库是空的」。
    /// </summary>
    public static VaultDocument? Read(string path)
    {
        if (!File.Exists(path)) return null;

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;

        return JsonSerializer.Deserialize<VaultDocument>(text, ReadOptions);
    }

    /// <summary>
    /// 写保险库。
    ///
    /// **先写临时文件再原子替换** —— 直接覆盖的话，写到一半断电就是
    /// 半个 JSON，用户的全部账号都没了。这是唯一一份不能丢的数据。
    /// </summary>
    public static void Write(string path, VaultDocument doc)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(doc, WriteOptions);

        // 临时文件放同一个目录 —— 跨卷的 File.Move 不是原子的
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);

        // File.Move(overwrite) 在 Windows 上是原子的（内部用 MoveFileEx + REPLACE_EXISTING）
        File.Move(temp, path, overwrite: true);

        HardenPermissions(path);
    }

    /// <summary>
    /// 收紧文件权限。
    ///
    /// 这个文件里是明文 token —— 拿到它就等于拿到账号。所以：
    ///   · Windows：不起作用（NTFS 的继承 ACE 已经够严，用户目录默认只有自己能读）
    ///   · Unix：设成 0600（只有属主能读写）
    ///
    /// 为什么在这里做而不是让调用方做：漏一次就是长期明文暴露。
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
            // 某些文件系统（FAT、网络盘）不支持 —— 不能因为设不上权限就写失败
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
