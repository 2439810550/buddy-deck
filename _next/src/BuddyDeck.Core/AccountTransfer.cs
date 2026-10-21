using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BuddyDeck.Core;

/// <summary>
/// 账号加密导出 / 导入 —— 对齐旧版 `workbuddy_api/src/transfer.js`。
///
/// ## 为什么必须加密
///
/// 导出的东西里有 `refreshToken`，**等效于账号密码** —— 而且上游给的是
/// 滑动窗口，只要不断续，它可以一直用下去。明文拷贝（微信 / U 盘 / 网盘）
/// 等于把这些号的额度公开了。
///
/// ## ⚠ 与旧版的互通性：**完全互通**（这是刻意做到的，不是巧合）
///
/// 容器格式、字段名、KDF 参数、AEAD 算法**逐项对齐 `transfer.js`**：
///
/// ```
/// format    "workbuddy-accounts"     ← 导入时先认这个
/// version   1
/// cipher    "aes-256-gcm"
/// kdf       { name:"scrypt", N:16384, r:8, p:1, keylen:32, salt:<base64> }
/// iv        <base64, 12 字节>
/// tag       <base64, 16 字节>
/// payload   <base64，AES-256-GCM 密文>
/// 明文       { provider, exportedAt, accounts:[...] } 的 UTF-8 JSON
/// ```
///
/// **为什么这不是可选项**：这是**整个项目重写**的场景，用户手里那份
/// 旧版导出的文件正是他换到新版时要拿过来的东西。任务书给的三条路里
/// 「A. 改用 PBKDF2」更简单，但它会让**旧版导出文件在新版里打不开** ——
/// 用户点「导入账号」选到那个文件，得到「口令不对或文件损坏」，
/// **而他的口令是对的**。那种失败比"少一个功能"严重得多：
/// 用户会以为自己的账号没了。
///
/// 所以这里选了「B. 自己实现 scrypt」并**用 RFC 7914 官方向量证明它是对的**
/// （见 ScryptTests）。.NET BCL 没有 scrypt（已核实 net8.0 参考程序集里
/// 无任何 scrypt 符号），这是唯一既不破坏零依赖、又能互通的走法。
///
/// ## 口令的处理（硬要求）
///
/// · **不落盘**：口令只作为参数传进来，用完即弃，本类不保存它
/// · **不进日志**：本类不打任何含口令的日志（出错消息里也只有分类，没有口令）
/// · **不参与序列化**：容器里只有 salt/iv/tag/payload，没有口令的任何派生痕迹
///
/// ## 错误措辞的分界线：有没有过「口令」这道关（t49）
///
/// 报错信息**按触发时机分两类**，这条界线是刻意的：
///
/// ```
/// 结构层（还没派生密钥，没碰过口令）
///   字段缺失 / base64 非法 / iv,tag 长度不符 / 文件不是合法 JSON
///   → 明说「文件已损坏或不是导出的账号文件」      （CorruptedMessage）
///
/// 认证层（已经用口令派生过密钥）
///   GCM authTag 失配
///   → 合并成「口令不对，或者文件被改坏了」        （UnlockFailedMessage）
/// ```
///
/// **为什么认证层必须合并**：能分开就意味着系统会告诉调用方
/// 「口令是对的，只是文件被改了」—— 攻击者据此可离线确认口令是否猜中，
/// 把 GCM 的认证退化成口令预言机。
///
/// **为什么结构层不该合并**：那时候**还没有碰过口令**，分开说
/// 泄露不了任何东西；而合并会把「文件坏了」的用户引去**反复重试口令**
/// —— 真相是文件坏了，重试一万次都没用。
///
/// ⚠ 这条界线的常见误用是**把认证层的理由推广到结构层**（"反正都是
/// 解不开，合并算了"）。本类最初就是这么写的，是 t49 修掉的。
/// </summary>
public static class AccountTransfer
{
    /// <summary>格式标识。导入时先认它 —— 拿错文件时报一句人话，而不是一堆莫名其妙的错。</summary>
    public const string Format = "workbuddy-accounts";

    /// <summary>容器版本。旧版是 1，这里保持一致。</summary>
    public const int Version = 1;

    /// <summary>AES-256-GCM 的标准 nonce 长度（字节）。</summary>
    private const int IvBytes = 12;

    /// <summary>GCM 认证标签长度（字节）。</summary>
    private const int TagBytes = 16;

    /// <summary>salt 长度（字节）。</summary>
    private const int SaltBytes = 16;

    /// <summary>派生密钥长度（AES-256 → 32 字节）。</summary>
    private const int KeyBytes = 32;

    /// <summary>
    /// scrypt 参数。**必须和旧版逐项一致**，否则生成的密文两边都解不开。
    /// N=16384 约 16 MB 内存、几十毫秒。
    /// </summary>
    private const int ScryptN = 16_384;
    private const int ScryptR = 8;
    private const int ScryptP = 1;

    /// <summary>
    /// **结构层**失败的统一措辞 —— 字段缺失 / base64 非法 / iv,tag 长度不符。
    ///
    /// ## 为什么这三处可以和「口令不对」分开说（t49 修正）
    ///
    /// 它们全部发生在 <see cref="DeriveKey"/> **之前** ——
    /// 此时还**没有碰过口令**，所以「分开说」**泄露不了任何口令信息**
    /// （没有信息可供泄露）。
    ///
    /// 而合并的代价是真实的：文件被截断（`payload` 字段没了）或 base64
    /// 被编辑器改坏时，用户看到「口令不对，或者文件损坏」会去
    /// **反复重试口令** —— 而重试一万次都没用，真相是文件坏了。
    /// **把他引向错误的方向，比多说一句「文件损坏」糟得多。**
    ///
    /// ⚠ 对比 <see cref="UnlockFailedMessage"/>：那一处（GCM 认证失配）
    /// **必须继续合并** —— 它发生在派生密钥之后，区分它等于告诉攻击者
    /// 「口令对了但文件被改过」，那是可用的旁路信息。
    ///
    /// 分界线就是「有没有过口令这道关」：
    /// ```
    /// 结构预检里（未接触口令）  → 可以明说「文件已损坏」   ← 本条
    /// 过了预检之后（已用口令）  → 必须合并                ← UnlockFailedMessage
    /// ```
    ///
    /// ## 为什么是 public 而不是 internal
    ///
    /// 它是**给调用方判断用的契约** —— 界面要据此决定提示用户
    /// 「换个文件」还是「再想想口令」，不能靠字符串匹配去猜。
    /// 测试也需要它来钉住这两类措辞不被随手改回去。
    /// </summary>
    public const string CorruptedMessage = "文件已损坏或不是导出的账号文件。";

    /// <summary>
    /// **认证层**失败的统一措辞 —— GCM authTag 失配。
    ///
    /// ⚠ **这一处必须继续合并「口令错」与「文件坏」，不许拆**：
    /// 它发生在 <see cref="DeriveKey"/> 之后，能把两者分开就意味着
    /// 系统能告诉调用方「口令是对的，只是文件被改了」——
    /// 攻击者据此可以**离线确认口令是否猜中**，把 GCM 的认证
    /// 退化成口令预言机。旧版也是合并成一句的。
    ///
    /// 这条理由**只适用于这里**（过了结构预检之后的失败）；
    /// 不要把它推广到 <see cref="CorruptedMessage"/> 那三处 ——
    /// 那里还没碰口令，合并没有安全收益（t49 修的正是这个把适用范围
    /// 写宽了的错误）。
    ///
    /// 同样是 public：调用方/测试要能钉住「这一处**不许**被拆成两类」——
    /// 那正是本类最容易发生的回归。
    /// </summary>
    public const string UnlockFailedMessage = "解不开：口令不对，或者文件在传输中被改坏了。";

    /// <summary>
    /// 最短口令长度。太短的口令挡不住离线爆破 ——
    /// **直接拒绝比事后提醒有用**（旧版同此阈值）。
    /// </summary>
    public const int MinPassphraseLength = 8;

    /// <summary>导出容器里除了密文之外的那些字段。</summary>
    private sealed record Envelope
    {
        [JsonPropertyName("format")] public string? FormatId { get; init; }
        [JsonPropertyName("version")] public int VersionNumber { get; init; }
        [JsonPropertyName("provider")] public string? Provider { get; init; }
        [JsonPropertyName("createdAt")] public string? CreatedAt { get; init; }
        [JsonPropertyName("cipher")] public string? Cipher { get; init; }
        [JsonPropertyName("kdf")] public KdfParameters? Kdf { get; init; }
        [JsonPropertyName("iv")] public string? Iv { get; init; }
        [JsonPropertyName("tag")] public string? Tag { get; init; }
        [JsonPropertyName("payload")] public string? Payload { get; init; }
    }

    /// <summary>KDF 参数块。</summary>
    private sealed record KdfParameters
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("N")] public int N { get; init; }
        [JsonPropertyName("r")] public int R { get; init; }
        [JsonPropertyName("p")] public int P { get; init; }
        [JsonPropertyName("keylen")] public int KeyLen { get; init; }
        [JsonPropertyName("salt")] public string? Salt { get; init; }
    }

    /// <summary>解密后还原出来的内容。</summary>
    public sealed record OpenedAccounts
    {
        /// <summary>来源变体（workbuddy / workbuddy-ai）。</summary>
        public required string Provider { get; init; }

        /// <summary>导出时刻（ISO 8601）。旧文件可能是空串。</summary>
        public required string ExportedAt { get; init; }

        /// <summary>还原出来的账号。</summary>
        public required IReadOnlyList<VaultEntry> Accounts { get; init; }
    }

    /// <summary>导出失败/导入失败时抛这个 —— 消息是给用户看的人话。</summary>
    public sealed class TransferException(string message) : Exception(message);

    // ── 序列化选项 ────────────────────────────────────────────

    /// <summary>
    /// 写容器：缩进（人可能要看/手工传）、中文不转义、null 省略。
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>读容器：宽容（尾逗号/注释），大小写不敏感。</summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // ── 导出 ──────────────────────────────────────────────────

    /// <summary>
    /// 把账号打包成加密容器。
    ///
    /// @param accounts   要导出的账号（含 token）
    /// @param passphrase 口令（**不会被保存，也不会进日志**）
    /// @param provider   变体标识
    /// @param at         导出时刻（测试用固定值）
    /// @returns 可以直接 JSON 序列化后写文件的文档
    /// </summary>
    public static JsonObject Seal(
        IEnumerable<VaultEntry> accounts,
        string passphrase,
        string provider,
        DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        AssertPassphrase(passphrase);

        var list = accounts.ToList();
        var exportedAt = (at ?? DateTimeOffset.UtcNow).ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

        // 明文载荷。**字段名对齐旧版**（provider / exportedAt / accounts）。
        var payload = new JsonObject
        {
            ["provider"] = provider,
            ["exportedAt"] = exportedAt,
            ["accounts"] = new JsonArray([.. list.Select(AccountToJson)]),
        };

        var plaintext = Encoding.UTF8.GetBytes(payload.ToJsonString(
            new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var iv = RandomNumberGenerator.GetBytes(IvBytes);
        var key = DeriveKey(passphrase, salt);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(iv, plaintext, ciphertext, tag);
        }

        CryptographicOperations.ZeroMemory(key);

        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["provider"] = provider,
            ["createdAt"] = exportedAt,
            ["cipher"] = "aes-256-gcm",
            ["kdf"] = new JsonObject
            {
                ["name"] = "scrypt",
                ["N"] = ScryptN,
                ["r"] = ScryptR,
                ["p"] = ScryptP,
                ["keylen"] = KeyBytes,
                ["salt"] = Convert.ToBase64String(salt),
            },
            ["iv"] = Convert.ToBase64String(iv),
            ["tag"] = Convert.ToBase64String(tag),
            ["payload"] = Convert.ToBase64String(ciphertext),
        };
    }

    /// <summary>
    /// 导出到文件。
    ///
    /// **原子写**：先写临时文件再原子替换（同 <see cref="Vault.Write"/>）——
    /// 这个文件里是用户的全部账号凭据，写到一半断电不能留下半截文件。
    /// 并且按保险库同款策略收紧权限（Unix 0600）。
    /// </summary>
    public static void ExportToFile(
        string path,
        IEnumerable<VaultEntry> accounts,
        string passphrase,
        string provider,
        DateTimeOffset? at = null)
    {
        var document = Seal(accounts, passphrase, provider, at);
        var json = document.ToJsonString(WriteOptions);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // 临时文件放同一个目录 —— 跨卷的 File.Move 不是原子的
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // File.Move(overwrite) 在 Windows 上是原子的（MoveFileEx + REPLACE_EXISTING）
        File.Move(temp, path, overwrite: true);

        HardenPermissions(path);
    }

    // ── 导入 ──────────────────────────────────────────────────

    /// <summary>
    /// 解开一个导出容器。
    ///
    /// ## 三类失败，一句人话
    ///
    /// ```
    /// 口令错 / 文件被改过 / 文件被截断  → GCM 的认证标签全部拦下
    ///                                    统一报「口令不对或文件损坏」，
    ///                                    不泄露是哪一步出的问题
    /// 格式标识不对                      → 报「这不是导出的账号文件」
    /// 版本不认识                        → 报出版本号
    /// ```
    ///
    /// **绝不静默解出乱码**：GCM 的 authTag 天然能验完整性，
    /// 这里把它用上 —— 口令错时 `Decrypt` 直接抛 `AuthenticationTagMismatchException`。
    /// </summary>
    public static OpenedAccounts Open(JsonObject document, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(document);
        AssertPassphrase(passphrase);

        // ① 格式标识
        var format = document["format"]?.GetValue<string>();
        if (!string.Equals(format, Format, StringComparison.Ordinal))
        {
            throw new TransferException(
                $"这不是导出的账号文件（format 期望 {Format}，实际 {format ?? "没有这个字段"}）。");
        }

        // ② 版本
        var version = document["version"]?.GetValue<int>() ?? 0;
        if (version != Version)
        {
            throw new TransferException(
                $"导出文件版本是 {version}，这个版本的程序只认 {Version}。");
        }

        // ③ 逐个取参数 —— 缺任何一个都报"文件损坏"，不猜默认值
        //
        // ⚠ 这三处 + 下面的长度检查都是**结构层**失败：此刻还没碰过口令，
        //   所以用明确的「文件损坏」而不是合并措辞（t49 修正）。
        //   详见 CorruptedMessage 的注释。
        var salt = DecodeBase64(document, "kdf", "salt");
        var iv = DecodeBase64(document, "iv");
        var tag = DecodeBase64(document, "tag");
        var payload = DecodeBase64(document, "payload");

        if (iv.Length != IvBytes || tag.Length != TagBytes)
        {
            throw new TransferException(CorruptedMessage);
        }

        byte[] plaintext;
        try
        {
            var key = DeriveKey(passphrase, salt);

            plaintext = new byte[payload.Length];
            using (var aes = new AesGcm(key, TagBytes))
            {
                aes.Decrypt(iv, payload, tag, plaintext);
            }

            CryptographicOperations.ZeroMemory(key);
        }
        catch (Exception ex) when (ex is AuthenticationTagMismatchException or CryptographicException or ArgumentException)
        {
            // ⚠ 这里**不区分**「口令错」和「文件损坏」——
            // 区分了等于告诉攻击者「口令对了但文件坏了」，
            // 那是可用的旁路信息。旧版也是合并成一句。
            //
            // ## ⚠ 这条理由的**适用范围**（t49 修正，别再写宽）
            //
            // 它**只适用于走到这一步的失败** —— 也就是「已经用口令派生过密钥」
            // 之后的失败。因为只有在这之后，把两类分开才会**携带口令信息**。
            //
            // **不要把它推广到结构预检里**（字段缺失 / base64 非法 /
            // iv,tag 长度不符）：那里还没碰口令，分开说泄露不了任何东西，
            // 反而能避免把「文件坏了」的用户引去反复重试口令。
            // 见 CorruptedMessage 的注释。
            throw new TransferException(UnlockFailedMessage);
        }

        // ④ 明文必须是合法 JSON
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(Encoding.UTF8.GetString(plaintext));
        }
        catch (JsonException)
        {
            throw new TransferException("解开后的内容不是合法 JSON，文件可能损坏了。");
        }

        if (parsed is not JsonObject payloadObject)
        {
            throw new TransferException("解开后的内容不是合法 JSON，文件可能损坏了。");
        }

        // ⑤ 还原账号
        var accounts = new List<VaultEntry>();
        if (payloadObject["accounts"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is not JsonObject entry) continue;

                var restored = AccountFromJson(entry);
                if (restored is not null) accounts.Add(restored);
            }
        }

        return new OpenedAccounts
        {
            Provider = payloadObject["provider"]?.GetValue<string>() ?? "",
            ExportedAt = payloadObject["exportedAt"]?.GetValue<string>() ?? "",
            Accounts = accounts,
        };
    }

    /// <summary>从文件读取并解开。**只读，不修改文件。**</summary>
    public static OpenedAccounts ImportFromFile(string path, string passphrase)
    {
        if (!File.Exists(path)) throw new TransferException($"找不到文件：{path}");

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TransferException($"读不了文件：{ex.GetType().Name}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new TransferException("这是一个空文件。");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // 文件根本不是合法 JSON（截断、被编辑器改坏、或者选错了文件）。
            //
            // ⚠ 这里和 GCM 那一处**不是一回事**（t49 修正）：此刻连文档都没解析出来，
            //   更没碰过口令 —— 所以报「文件损坏」是对的。
            //   （原注释写「两条路都报同一句话，对用户来说是一回事」——
            //    那句是错的：把「文件坏了」说成「可能口令不对」，
            //    会把用户引去反复重试口令。）
            throw new TransferException(CorruptedMessage);
        }

        if (node is not JsonObject document)
        {
            throw new TransferException("这不是导出的账号文件。");
        }

        return Open(document, passphrase);
    }

    // ── 内部 ──────────────────────────────────────────────────

    /// <summary>
    /// 口令强度检查。
    ///
    /// 导出和导入**都查** —— 虽然导入时短口令根本解不开，
    /// 但先报「口令太短」比让用户以为是文件坏了更有帮助。
    /// </summary>
    private static void AssertPassphrase(string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Trim().Length < MinPassphraseLength)
        {
            throw new TransferException(
                $"口令至少 {MinPassphraseLength} 个字符。这个文件里是账号凭据，口令太短等于没加密。");
        }
    }

    /// <summary>
    /// 派生密钥 —— 参数与旧版逐项一致（scrypt N=16384, r=8, p=1, keylen=32）。
    /// 口径不一致的话，生成的密文两边互相解不开。
    /// </summary>
    private static byte[] DeriveKey(string passphrase, byte[] salt)
        => Scrypt.DeriveKey(passphrase, salt, ScryptN, ScryptR, ScryptP, KeyBytes);

    /// <summary>
    /// 从一个可能嵌套的字段里取 base64 并解码；缺字段/坏 base64 都算**文件损坏**。
    ///
    /// ⚠ 这里报的是 <see cref="CorruptedMessage"/>（结构层）而不是合并措辞 ——
    /// 本方法只在 <see cref="Open"/> 的**结构预检**里被调用，还没有派生密钥，
    /// 所以「文件坏」与「口令错」在这里是**可以分辨、也该分辨**的（t49 修正）。
    /// </summary>
    private static byte[] DecodeBase64(JsonObject document, string key, string? nested = null)
    {
        var source = nested is null ? document : document[key] as JsonObject;
        var value = (nested is null ? document[key] : source?[nested])?.GetValue<string>();

        if (string.IsNullOrEmpty(value))
        {
            throw new TransferException(CorruptedMessage);
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw new TransferException(CorruptedMessage);
        }
    }

    /// <summary>
    /// 一条账号 → JSON。
    ///
    /// ⚠ 字段名**逐字对齐旧版保险库/导出文件的形状**（camelCase），
    /// 因为导入方可能是旧版 —— 名字错了它就认不出字段。
    /// </summary>
    private static JsonObject AccountToJson(VaultEntry entry) => new()
    {
        ["id"] = entry.Id,
        ["uid"] = entry.Uid,
        ["enterpriseId"] = entry.EnterpriseId,
        ["domain"] = entry.Domain,
        ["accessToken"] = entry.AccessToken,
        ["refreshToken"] = entry.RefreshToken,
        ["expiresAtMs"] = entry.ExpiresAtMs,
        ["refreshExpiresAtMs"] = entry.RefreshExpiresAtMs,
        ["label"] = entry.Label,
        ["addedAt"] = entry.AddedAt,
        ["lastSeenAt"] = entry.LastSeenAt,
        ["lastCheckinAt"] = entry.LastCheckinAt,
    };

    /// <summary>
    /// JSON → 一条账号。缺 id 或 accessToken 的条目跳过（返回 null）——
    /// 那两条是 <see cref="VaultEntry"/> 的必需字段，缺了造不出合法对象。
    /// </summary>
    private static VaultEntry? AccountFromJson(JsonObject json)
    {
        var id = json["id"]?.GetValue<string>();
        var accessToken = json["accessToken"]?.GetValue<string>();

        if (string.IsNullOrEmpty(id) || accessToken is null) return null;

        return new VaultEntry
        {
            Id = id,
            Uid = json["uid"]?.GetValue<string>() ?? "",
            EnterpriseId = json["enterpriseId"]?.GetValue<string>(),
            Domain = json["domain"]?.GetValue<string>() ?? "",
            AccessToken = accessToken,
            RefreshToken = json["refreshToken"]?.GetValue<string>() ?? "",
            ExpiresAtMs = ReadLong(json, "expiresAtMs"),
            RefreshExpiresAtMs = json["refreshExpiresAtMs"] is null ? null : ReadLong(json, "refreshExpiresAtMs"),
            Label = json["label"]?.GetValue<string>() ?? "",
            AddedAt = json["addedAt"]?.GetValue<string>(),
            LastSeenAt = json["lastSeenAt"]?.GetValue<string>(),
            LastCheckinAt = json["lastCheckinAt"]?.GetValue<string>(),
        };
    }

    /// <summary>
    /// 读一个 long 字段。
    ///
    /// ⚠ 不能只试 <c>GetValue&lt;long&gt;</c>：`TryGetValue`/`GetValue` 要求
    /// **底层类型精确匹配** —— JSON 里的 `1793731164678` 可能被反序列化成
    /// int（在范围内时）或 long，取决于解析路径。只认 long 会
    /// **在部分文件上抛异常**，那是极难复现的 bug。
    /// （同类坑在 UsageLedger 里踩过一次：`{"prompt_tokens": 2541}` 是 int。）
    /// </summary>
    private static long ReadLong(JsonObject json, string key)
    {
        if (json[key] is not JsonNode node) return 0;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var asLong)) return asLong;
            if (value.TryGetValue<int>(out var asInt)) return asInt;
            if (value.TryGetValue<double>(out var asDouble)) return (long)asDouble;
            if (value.TryGetValue<string>(out var text) &&
                long.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    /// <summary>
    /// 收紧文件权限 —— 与 <see cref="Vault.HardenPermissions"/> **同款策略**。
    ///
    /// 这个文件里是加密后的凭据，拿到它 + 口令就等于拿到账号。
    /// Windows 上不起作用（NTFS 继承 ACE 已够严），Unix 上设 0600。
    /// </summary>
    private static void HardenPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 某些文件系统（FAT、网络盘）不支持 —— 不能因为设不上权限就导出失败
        }
    }

    /// <summary>建议的文件名（带日期，便于区分）—— 与旧版同形。</summary>
    public static string SuggestedFileName(string provider, DateTimeOffset? at = null)
    {
        var stamp = (at ?? DateTimeOffset.UtcNow).ToUniversalTime()
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return $"workbuddy-accounts-{provider}-{stamp}.json";
    }

    /// <summary>
    /// 两个口令是否一致（界面里「确认口令」用）。
    ///
    /// 用定时安全比较：这里其实不敏感（两个都是用户刚输入的），
    /// 但保持和旧版一致，也避免以后有人把它用到敏感场合时忘了换。
    /// </summary>
    public static bool SamePassphrase(string? a, string? b)
    {
        var left = Encoding.UTF8.GetBytes(a ?? "");
        var right = Encoding.UTF8.GetBytes(b ?? "");
        if (left.Length != right.Length) return false;
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    /// <summary>容器里声明的 KDF 参数是否是我们支持的（诊断用）。</summary>
    public static bool IsSupportedKdf(JsonObject document)
    {
        if (document["kdf"] is not JsonObject kdf) return false;

        return string.Equals(kdf["name"]?.GetValue<string>(), "scrypt", StringComparison.Ordinal)
            && (kdf["N"]?.GetValue<int>() ?? 0) == ScryptN
            && (kdf["r"]?.GetValue<int>() ?? 0) == ScryptR
            && (kdf["p"]?.GetValue<int>() ?? 0) == ScryptP;
    }
}
