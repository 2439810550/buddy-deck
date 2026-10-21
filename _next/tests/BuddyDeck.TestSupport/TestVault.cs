using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;

namespace BuddyDeck.TestSupport;

/// <summary>
/// 内存保险库 / 凭证工厂。
///
/// ## 为什么需要它
///
/// 测账号池、测网关、测端到端都要「几个账号」。真账号一个都不能碰
/// （HANDOVER 第九节：不碰真实账号），所以必须能凭空造一份**形状完全合法**的。
///
/// 「形状合法」这四个字有历史包袱，见 HANDOVER 第四节坑 2：
///
/// ```
/// expiresAtMs        数字（Unix 毫秒）
/// addedAt            ISO 字符串   ← 类型和上面不一样！
/// lastSeenAt         ISO 字符串
/// lastCheckinAt      ISO 字符串
/// ```
///
/// 造数据造错了是发现不了的（那个 bug 就是「读真实文件」那条测试抓的），
/// 所以这里所有工厂都按真实格式造，**并且** <see cref="ToVaultJson"/> 会
/// 真的走一遍 <see cref="Vault.Write"/> / <see cref="Vault.Read"/> 的往返。
///
/// ## 空身份账号
///
/// <see cref="EmptyIdentity"/> 造的是**没有 uid / enterpriseId / domain** 的账号。
/// 那正是 `X-No-User-Id` / `X-No-Enterprise-Id` / `X-No-Department-Info`
/// 三个头的测试对象 —— 上游把「缺头」和「显式声明没有」当成两回事，
/// 后者才是对的（见 <c>UpstreamHeaders.ApplyIdentity</c>）。
/// </summary>
public sealed class TestVault
{
    /// <summary>一个固定的"现在"，让造出来的过期时间可预期（默认就是真现在）。</summary>
    private readonly DateTimeOffset _now;

    private readonly List<VaultEntry> _entries = [];

    private string _activeId = "";

    public TestVault(DateTimeOffset? now = null) => _now = now ?? DateTimeOffset.UtcNow;

    /// <summary>现在有哪些账号（快照）。</summary>
    public IReadOnlyList<VaultEntry> Entries => _entries.ToArray();

    /// <summary>当前选中的 id。</summary>
    public string ActiveId => _activeId;

    /// <summary>
    /// 一个默认账号 —— 有 uid、有企业、有域，token 还没过期。
    ///
    /// 用固定的可读值而不是随机值：失败信息里看到 `acct-1` 比看到一串 uuid
    /// 好定位得多。
    /// </summary>
    public static TestVault Default() => new TestVault().Add();

    /// <summary>造一个「几小时后过期」的时间戳（Unix 毫秒）。</summary>
    public long ExpiresIn(TimeSpan span) => _now.Add(span).ToUnixTimeMilliseconds();

    /// <summary>造一个「几小时前过期」的时间戳。</summary>
    public long ExpiredSince(TimeSpan span) => _now.Subtract(span).ToUnixTimeMilliseconds();

    /// <summary>
    /// 加一个账号。
    ///
    /// ⚠ `id` 的格式是 `uuid:enterpriseId`，**末尾那个冒号是格式的一部分**
    /// （个人账号就是 `uuid:`）。它和 <see cref="Credential.AccountTag"/>
    /// （`uid:enterpriseId`）不是一个东西：前者标识「保险库里的哪一条」，
    /// 后者标识「哪个登录身份」。
    /// </summary>
    public TestVault Add(
        string? id = null,
        string? uid = null,
        string? enterpriseId = null,
        string? domain = null,
        string? label = null,
        string? accessToken = null,
        string? refreshToken = null,
        long? expiresAtMs = null,
        long? refreshExpiresAtMs = null,
        DateTimeOffset? addedAt = null)
    {
        var index = _entries.Count + 1;
        var token = accessToken ?? $"access-token-{index}";

        // 默认值刻意「正常」：有身份、有域、token 还有 6 小时。
        // 要造异常账号就显式传参（见 EmptyIdentity）。
        var entry = new VaultEntry
        {
            Id = id ?? $"00000000-0000-0000-0000-{index:D12}:{enterpriseId ?? "ent-1"}",
            Uid = uid ?? $"user-{index}",
            EnterpriseId = enterpriseId ?? "ent-1",
            Domain = domain ?? "workbuddy.cn",
            AccessToken = token,
            RefreshToken = refreshToken ?? $"refresh-token-{index}",
            ExpiresAtMs = expiresAtMs ?? ExpiresIn(TimeSpan.FromHours(6)),
            RefreshExpiresAtMs = refreshExpiresAtMs ?? ExpiresIn(TimeSpan.FromDays(30)),
            Label = label ?? $"测试号 {index}",
            AddedAt = Iso(addedAt ?? _now),
            LastSeenAt = Iso(_now),
            LastCheckinAt = Iso(_now),
        };

        if (_activeId.Length == 0) _activeId = entry.Id;
        _entries.Add(entry);
        return this;
    }

    /// <summary>
    /// 加一个**空身份**账号：没有 uid、没有 enterpriseId、没有 domain。
    ///
    /// 这是 `X-No-User-Id: 1` / `X-No-Enterprise-Id: 1` /
    /// `X-No-Department-Info: 1` 三个头的唯一测试对象。
    /// 用默认参数造不出它来（默认全是有值的），所以单列一个方法。
    /// </summary>
    public TestVault EmptyIdentity(
        string? label = null,
        string? accessToken = null,
        long? expiresAtMs = null)
    {
        var index = _entries.Count + 1;

        var entry = new VaultEntry
        {
            // 个人账号的 id 就是以冒号结尾 —— 格式的一部分，别 trim
            Id = $"00000000-0000-0000-0000-{index:D12}:",
            Uid = "",
            EnterpriseId = null,
            Domain = "",
            AccessToken = accessToken ?? $"access-token-{index}",
            RefreshToken = $"refresh-token-{index}",
            ExpiresAtMs = expiresAtMs ?? ExpiresIn(TimeSpan.FromHours(6)),
            RefreshExpiresAtMs = ExpiresIn(TimeSpan.FromDays(30)),
            Label = label ?? $"空身份号 {index}",
            AddedAt = Iso(_now),
        };

        if (_activeId.Length == 0) _activeId = entry.Id;
        _entries.Add(entry);
        return this;
    }

    /// <summary>加一个 token 已经过期的账号。</summary>
    public TestVault Expired(string label = "过期号", string? uid = null, string? enterpriseId = null)
        => Add(
            uid: uid,
            enterpriseId: enterpriseId,
            label: label,
            expiresAtMs: ExpiredSince(TimeSpan.FromHours(1)));

    /// <summary>加一个「上游没给过期时间」的账号（`expiresAtMs = 0`）。</summary>
    public TestVault NoExpiry(string label = "无过期时间号")
        => Add(label: label, expiresAtMs: 0);

    /// <summary>批量加 N 个普通账号。</summary>
    public TestVault AddMany(int count, string uidPrefix = "user")
    {
        for (var i = 0; i < count; i++)
        {
            Add(uid: $"{uidPrefix}-{_entries.Count + 1}", label: $"{uidPrefix}-{_entries.Count + 1}");
        }
        return this;
    }

    /// <summary>指定当前选中的 id。</summary>
    public TestVault SetActive(string id)
    {
        _activeId = id;
        return this;
    }

    /// <summary>转成保险库文档。</summary>
    public VaultDocument ToDocument() => new()
    {
        Version = 1,
        ActiveId = _activeId,
        Accounts = [.. _entries],
    };

    /// <summary>
    /// 写到一个真实文件，返回路径。
    ///
    /// 走的是生产代码的 <see cref="Vault.Write"/>，所以格式（包括
    /// 那两个类型不一致的时间字段）和真实文件完全一致。
    /// </summary>
    public string WriteTo(string path)
    {
        Vault.Write(path, ToDocument());
        return path;
    }

    /// <summary>写到一个临时目录下的新文件，返回路径。调用方负责清理。</summary>
    public string WriteToTempFile(string? fileName = "auth.cn.json")
    {
        var directory = Path.Combine(Path.GetTempPath(), "buddydeck-testsupport-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        return WriteTo(Path.Combine(directory, fileName ?? "auth.cn.json"));
    }

    /// <summary>保险库文件的 JSON 文本（不落盘时用）。</summary>
    public string ToVaultJson() => System.Text.Json.JsonSerializer.Serialize(ToDocument(), VaultJsonOptions);

    /// <summary>写文件用的序列化选项 —— 缓存起来（CA1869：每次新建 Options 是浪费）。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions VaultJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 第一个账号的凭证（最常用）。
    ///
    /// 一次都没加账号时抛 —— 那种情况下的断言本来就要复检。
    /// </summary>
    public Credential FirstCredential => (_entries.Count > 0 ? _entries[0] : null)?.ToCredential()
        ?? throw new InvalidOperationException("TestVault 里还没有账号");

    /// <summary>按 id 取凭证。</summary>
    public Credential CredentialOf(string id)
        => (_entries.FirstOrDefault(e => e.Id == id) ?? throw new KeyNotFoundException($"没有这个账号：{id}")).ToCredential();

    /// <summary>ISO 8601 毫秒精度 —— 和真实保险库里的字符串同形。</summary>
    private static string Iso(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// 从内存列表读账号。
///
/// 网关的 <c>VaultCredentialSource</c> 每次请求都读一遍**文件**
/// （用户在面板里导入/删号之后不必重启网关）。测网关时不想要文件 IO，
/// 也不想要「刚导入的号还没落盘」这种时序问题，所以给一个内存版。
///
/// ⚠ <b>它没有声明 <c>ICredentialSource</c></b>，尽管它有那个接口的
/// <see cref="Load"/> 方法。理由：<c>ICredentialSource</c> 定义在
/// <c>BuddyDeck.Gateway</c> 里，写死接口会让 TestSupport 的编译依赖
/// 网关那条线 —— 而那正是这个任务要避免的耦合源头。
/// 用的时候一行适配：
///
/// <code>
/// ICredentialSource source = new Adapter(inner);
/// sealed class Adapter(FakeCredentialSource inner) : ICredentialSource
/// {
///     public IReadOnlyList&lt;VaultEntry&gt; Load() =&gt; inner.Load();
/// }
/// </code>
///
/// 将来 <c>ICredentialSource</c> 挪进 Core 之后，这里再 implements 一次即可。
/// </summary>
public sealed class FakeCredentialSource
{
    private readonly List<VaultEntry> _entries = [];
    private readonly object _gate = new();

    public FakeCredentialSource()
    {
    }

    public FakeCredentialSource(IEnumerable<VaultEntry> entries)
    {
        _entries.AddRange(entries);
    }

    /// <summary>从一个 <see cref="TestVault"/> 建。</summary>
    public static FakeCredentialSource From(TestVault vault) => new(vault.Entries);

    /// <summary>用一个空身份账号建（最常用的单账号场景）。</summary>
    public static FakeCredentialSource EmptyIdentity() => new(new TestVault().EmptyIdentity().Entries);

    /// <summary>当前账号（快照）。</summary>
    public IReadOnlyList<VaultEntry> Accounts
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    /// <summary>整体换一批（模拟用户改了保险库）。</summary>
    public FakeCredentialSource SetAccounts(IEnumerable<VaultEntry> entries)
    {
        lock (_gate)
        {
            _entries.Clear();
            _entries.AddRange(entries);
        }
        return this;
    }

    /// <summary>加一个（模拟用户刚导入）。</summary>
    public FakeCredentialSource Add(VaultEntry entry)
    {
        lock (_gate) _entries.Add(entry);
        return this;
    }

    /// <summary>加一条凭证（内部转成 VaultEntry）。</summary>
    public FakeCredentialSource Add(Credential credential, string? id = null, string? label = null)
    {
        var index = _entries.Count + 1;
        return Add(new VaultEntry
        {
            Id = id ?? $"00000000-0000-0000-0000-{index:D12}:{credential.EnterpriseId ?? ""}",
            Uid = credential.Uid,
            EnterpriseId = credential.EnterpriseId,
            Domain = credential.Domain,
            AccessToken = credential.AccessToken,
            RefreshToken = credential.RefreshToken,
            ExpiresAtMs = credential.ExpiresAtMs,
            Label = label ?? credential.Label,
        });
    }

    /// <summary>清空（模拟用户删光了账号）。</summary>
    public FakeCredentialSource Clear()
    {
        lock (_gate) _entries.Clear();
        return this;
    }

    /// <summary>读一次。</summary>
    public IReadOnlyList<VaultEntry> Load()
    {
        lock (_gate) return _entries.ToArray();
    }
}

/// <summary>
/// 保险库文件的字符串工具 —— 造「格式不合法」的保险库时用。
///
/// 为什么需要造不合法的：读真实文件那条历史测试（HANDOVER 坑 2）证明
/// 「造数据造不出类型错误」。所以夹具同时提供**手工拼 JSON** 的口子，
/// 让写错类型的场景可以被显式构造出来。
/// </summary>
public static class VaultJson
{
    /// <summary>
    /// 三个时间字段**全用数字**的保险库 —— 这正是当年踩过的那个坑。
    ///
    /// 真实格式里 `addedAt` 是 ISO 字符串。全数字的话
    /// <see cref="Vault.Read"/> 会抛 <c>JsonException</c>。
    /// </summary>
    public static string AllNumericTimestamps(int accounts = 1)
    {
        var array = new JsonArray();
        for (var i = 0; i < accounts; i++)
        {
            array.Add(new JsonObject
            {
                ["id"] = $"id-{i}:",
                ["uid"] = $"user-{i}",
                ["accessToken"] = $"token-{i}",
                ["refreshToken"] = $"refresh-{i}",
                ["expiresAtMs"] = 1793731164678L,
                ["addedAt"] = 1793731164678L,          // ← 故意的：应该是字符串
                ["lastSeenAt"] = 1793731164678L,       // ← 同上
                ["lastCheckinAt"] = 1793731164678L,    // ← 同上
            });
        }

        return new JsonObject
        {
            ["version"] = 1,
            ["activeId"] = "id-0:",
            ["accounts"] = array,
        }.ToJsonString();
    }

    /// <summary>一个空文件内容（模拟「文件在但没内容」）。</summary>
    public static string Blank() => "";

    /// <summary>截断的 JSON（模拟写到一半断电 —— 原子写要防的就是这个）。</summary>
    public static string Truncated() => """{"version":1,"activeId":"id-0:","accounts":[{"id":"id-0:" """;

    /// <summary>造一个保险库文件，内容由调用方给。</summary>
    public static string WriteTo(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
