using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 一条 API Key 的记录（**不含明文**）。
///
/// ## 为什么这里没有明文字段
///
/// 明文只在 <see cref="KeyStore.Issue"/> 的返回值里出现**一次**，
/// 之后永远不再落盘、不再可查。
/// 这是**结构上**的保证 —— 这个类型里没有能装明文的字段，
/// 想把它存起来都编译不过（不能靠"记得别存"）。
///
/// 鉴权时用**哈希比对**（见 <see cref="KeyStore.Verify"/>），
/// 所以即使有人读到落盘文件，也拿不到能用的密钥。
/// </summary>
public sealed record ApiKeyRecord
{
    /// <summary>内部 id（随机，和密钥本身无关）。用于撤销/引用。</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// 给人看的名字（「我的笔记本」「CI」）。纯展示，不参与鉴权。
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>
    /// 密钥**前缀**（含 <c>bdk-</c> 标记 + 前 8 位随机段）。
    ///
    /// 为什么留前缀：用户在面板上一眼认出"这是哪个 key"，
    /// 而前缀**不足以还原密钥**（后续还有 32 字节熵）。
    /// 这也是各家 API（OpenAI 的 <c>sk-...</c>）通行的做法。
    /// </summary>
    [JsonPropertyName("prefix")]
    public required string Prefix { get; init; }

    /// <summary>
    /// 密钥的 SHA-256（hex，小写）。**鉴权就是比这个。**
    ///
    /// 为什么用裸 SHA-256 而不是 PBKDF2/scrypt：
    /// 密钥是我们自己生成的 **192 位随机串**（不是人选的口令），
    /// 没有"低熵可爆破"的问题 —— 慢哈希在这里只增加每次请求的开销，
    /// 不增加任何安全性。（和用户口令必须用慢哈希是两回事，
    /// <see cref="BuddyDeck.Core.AccountTransfer"/> 那边用的是 scrypt。）
    /// </summary>
    [JsonPropertyName("hash")]
    public required string Hash { get; init; }

    /// <summary>签发时刻（UTC ISO 8601）。</summary>
    [JsonPropertyName("createdAt")]
    public required string CreatedAt { get; init; }

    /// <summary>撤销时刻。null = 仍然有效。**撤销是改这个字段，不是删记录**（留痕）。</summary>
    [JsonPropertyName("revokedAt")]
    public string? RevokedAt { get; init; }

    /// <summary>最后使用时刻（每次鉴权通过时更新）。诊断用。</summary>
    [JsonPropertyName("lastUsedAt")]
    public string? LastUsedAt { get; init; }

    /// <summary>这个 key 现在能不能用。</summary>
    [JsonIgnore]
    public bool Active => RevokedAt is null;
}

/// <summary>
/// 刚签发出来的密钥 —— **明文只在这里出现一次**。
///
/// ⚠ 调用方（端点）必须把 <see cref="Plaintext"/> 立刻回给用户，
/// 并且**不要把它写进日志**。这个类型刻意不重写 ToString，
/// 免得有人 <c>log(key)</c> 时把明文打进日志（那条路径我们已经踩过）。
/// </summary>
public sealed record IssuedApiKey
{
    /// <summary>明文密钥。**只此一次**，之后系统里再也拿不到它。</summary>
    public required string Plaintext { get; init; }

    /// <summary>对应的记录（不含明文）。</summary>
    public required ApiKeyRecord Record { get; init; }

    /// <summary>
    /// 故意让默认 ToString 只输出前缀 —— 防「顺手 log 一下」把明文写进日志。
    /// </summary>
    public override string ToString() => $"IssuedApiKey({Record.Prefix}…, {Record.Id})";
}

/// <summary>
/// 多 API Key 的签发 / 撤销 / 列表 / 校验。
///
/// ## 它和 <see cref="GatewayOptions.ApiKey"/> 的关系
///
/// 现有鉴权读的是**一个**配置里的 key（`--api-key`）。这个类是它的**多 key 版**：
/// 用户可以签出若干把互不相干的 key，分别给不同的人/机器用，各自能单独撤销。
///
/// **两者语义一致**（都是 Bearer + 定时安全比较），所以
/// 用这里签出来的明文去当 `GatewayOptions.ApiKey` 能直接通过 `/v1/*` 鉴权 ——
/// 本任务的注入验证就是这么做的（不改生产接线）。
///
/// ## 落盘格式
///
/// JSON（不是 JSONL）：这是一个**小而有状态的登记表**（几十条），
/// 需要整体读改写，不是追加流水。用 <see cref="AccountTransfer"/> 同款的
/// **临时文件 + 原子替换** —— 写到一半断电不能留下半个 JSON。
///
/// ⚠ 落盘文件里**只有哈希与前缀，没有明文**（测试 `落盘不含明文` 钉住）。
/// </summary>
public sealed partial class KeyStore
{
    /// <summary>
    /// 密钥前缀标记。便于用户/日志一眼认出这是 BuddyDeck 的 key。
    /// 对齐旧版风格（旧版是裸 base64url，这里加标记是为了**可识别**）。
    /// </summary>
    public const string KeyMarker = "bdk-";

    /// <summary>随机段字节数。旧版是 <c>randomBytes(24)</c>（192 位），这里保持一致。</summary>
    private const int SecretBytes = 24;

    /// <summary>展示前缀里带多少位随机段字符（8 位够区分几十把 key，又远不足以还原）。</summary>
    private const int PrefixRandomChars = 8;

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly object _gate = new();

    private readonly List<ApiKeyRecord> _records = [];

    public KeyStore(string path, TimeProvider? clock = null, ILogger? log = null)
    {
        _path = path;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        Load();
    }

    /// <summary>落盘路径。</summary>
    public string Path => _path;

    /// <summary>
    /// 签一把新 key。
    ///
    /// **明文只在返回值的 <see cref="IssuedApiKey.Plaintext"/> 里出现一次。**
    /// </summary>
    /// <param name="name">给人看的名字（可空）。</param>
    public IssuedApiKey Issue(string name = "")
    {
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        var plaintext = KeyMarker + Base64Url(secret);

        var record = new ApiKeyRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name ?? "",
            Prefix = plaintext[..(KeyMarker.Length + PrefixRandomChars)],
            Hash = HashOf(plaintext),
            CreatedAt = NowIso(),
        };

        lock (_gate)
        {
            _records.Add(record);
            Save();
        }

        return new IssuedApiKey { Plaintext = plaintext, Record = record };
    }

    /// <summary>
    /// 撤销一把 key。**幂等** —— 已撤销的再撤一次返回 false，不抛。
    ///
    /// 撤销是**标记**（写 <c>revokedAt</c>）而不是删除记录：
    /// 删掉的话「这把 key 曾经存在过」就查不到了，
    /// 而排查"是不是某个已撤的 key 在捣乱"正需要这个。
    /// </summary>
    /// <param name="id">记录 id，或密钥**明文**（两种都收，方便调用方）。</param>
    /// <returns>是否真的改变了状态。</returns>
    public bool Revoke(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        lock (_gate)
        {
            var record = FindLocked(id);
            if (record is null || !record.Active) return false;

            var index = _records.IndexOf(record);
            _records[index] = record with { RevokedAt = NowIso() };
            Save();
            return true;
        }
    }

    /// <summary>列出全部记录（含已撤销）—— **只有前缀，没有明文**。</summary>
    public IReadOnlyList<ApiKeyRecord> List()
    {
        lock (_gate) return _records.ToArray();
    }

    /// <summary>还能用的那些。</summary>
    public IReadOnlyList<ApiKeyRecord> ListActive()
        => [.. List().Where(r => r.Active)];

    /// <summary>
    /// 校验一把明文 key 是否有效。
    ///
    /// 用**定时安全比较**比哈希（不是 <c>==</c>）——
    /// 字符串比较会短路，攻击者能靠响应时间逐字节猜出哈希。
    /// （和 <c>GatewayApp.FixedTimeEquals</c> 同一个理由。）
    /// </summary>
    /// <returns>命中的记录；无效/已撤销返回 null。</returns>
    public ApiKeyRecord? Verify(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return null;

        var hash = HashOf(presented);

        lock (_gate)
        {
            foreach (var record in _records)
            {
                if (!record.Active) continue;
                if (!FixedTimeEquals(record.Hash, hash)) continue;

                // 记一次使用（诊断用）。写盘失败不影响鉴权结果。
                var index = _records.IndexOf(record);
                var touched = record with { LastUsedAt = NowIso() };
                _records[index] = touched;
                SaveQuiet();

                return touched;
            }
        }

        return null;
    }

    /// <summary>这把明文 key 有效吗。</summary>
    public bool IsValid(string? presented) => Verify(presented) is not null;

    // ── 内部 ──────────────────────────────────────────────────

    /// <summary>按 id **或**明文找记录。</summary>
    private ApiKeyRecord? FindLocked(string idOrPlaintext)
    {
        var byId = _records.FirstOrDefault(r => string.Equals(r.Id, idOrPlaintext, StringComparison.Ordinal));
        if (byId is not null) return byId;

        // 也接受明文（界面里「撤销这把」拿到的可能是刚签发的那串）
        var hash = HashOf(idOrPlaintext);
        return _records.FirstOrDefault(r => FixedTimeEquals(r.Hash, hash));
    }

    /// <summary>SHA-256 → 小写 hex。</summary>
    private static string HashOf(string plaintext)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

    /// <summary>
    /// base64url（无填充）—— 对齐旧版 <c>randomBytes(24).toString("base64url")</c>。
    ///
    /// 为什么不用标准 base64：它含 <c>+</c> <c>/</c> <c>=</c>，
    /// 放进 URL / query / 配置文件里都要额外转义，很容易在某一环被改坏。
    /// </summary>
    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string expected, string presented)
    {
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private string NowIso()
        => _clock.GetUtcNow().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private void Load()
    {
        lock (_gate)
        {
            _records.Clear();
            if (!File.Exists(_path)) return;

            try
            {
                var text = File.ReadAllText(_path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) return;

                var doc = JsonSerializer.Deserialize<KeyStoreDocument>(text, ReadOptions);
                if (doc?.Keys is not null) _records.AddRange(doc.Keys);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // ⚠ 读不出来时**不抛**，而是以空表起步 ——
                //   但必须记下来：空表意味着「所有 key 都失效」，
                //   静默变成空表会让用户以为"我签的 key 全没了"却查不到原因。
                if (_log.IsEnabled(LogLevel.Error))
                {
                    var reason = ex.GetType().Name;
                    var path = _path;
                    LogLoadFailed(reason, path);
                }
            }
        }
    }

    /// <summary>
    /// 写盘：**临时文件 + 原子替换**（同 <c>Vault.Write</c>）。
    ///
    /// 这个文件被写坏 = 所有 key 一起失效，所以不能直接覆盖写。
    /// </summary>
    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(
                new KeyStoreDocument { Version = 1, Keys = [.. _records] },
                WriteOptions);

            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var temp = _path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            File.Move(temp, _path, overwrite: true);
            HardenPermissions(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                var reason = ex.GetType().Name;
                LogSaveFailed(reason);
            }
        }
    }

    /// <summary>写盘但**不**记 warning（改 lastUsedAt 那种非关键的写）。</summary>
    private void SaveQuiet()
    {
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 使用时间记不上不影响鉴权
        }
    }

    /// <summary>Unix 上收紧到 0600 —— 里面有密钥哈希，不该给别人读。</summary>
    private static void HardenPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 某些文件系统不支持
        }
    }

    // ── 序列化 ────────────────────────────────────────────────

    private sealed record KeyStoreDocument
    {
        [JsonPropertyName("version")] public int Version { get; init; } = 1;
        [JsonPropertyName("keys")] public List<ApiKeyRecord> Keys { get; init; } = [];
    }

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

    [LoggerMessage(Level = LogLevel.Error, Message = "API key 登记表读取失败（{Reason}）：{Path} —— 已按空表起步，所有 key 都会失效")]
    private partial void LogLoadFailed(string reason, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "API key 登记表写盘失败（{Reason}）—— 本次改动未持久化")]
    private partial void LogSaveFailed(string reason);
}
