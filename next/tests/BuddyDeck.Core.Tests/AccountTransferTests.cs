using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 账号加密导出/导入的测试。
///
/// ## 这个文件的重点不是"往返能跑通"
///
/// 往返测试（自己 seal → 自己 open）**证明不了格式是对的** ——
/// 两边错得一样照样绿。真正的证据有两条：
///
/// 1. **与旧版 `transfer.js` 的互通**（用固定向量验证，不依赖用户文件）
/// 2. **口令错必须报错**（GCM 的 authTag 真的用上了）
///
/// 用户手里那份旧版导出的文件正是他换到新版时要拿过来的东西 ——
/// 打不开的话他会以为自己的账号没了。
/// </summary>
public class AccountTransferTests
{
    private const string Passphrase = "correct horse battery staple";

    private static VaultEntry Entry(
        string id = "acc-1",
        string uid = "uid-1",
        string? enterpriseId = "ent-1",
        string accessToken = "at-secret",
        string refreshToken = "rt-secret",
        long expiresAtMs = 1793731164678,
        string label = "测试号",
        string domain = "workbuddy.cn")
        => new()
        {
            Id = id,
            Uid = uid,
            EnterpriseId = enterpriseId,
            Domain = domain,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAtMs = expiresAtMs,
            RefreshExpiresAtMs = 1793903964678,
            Label = label,
            AddedAt = "2026-09-24T09:35:12.642Z",
            LastSeenAt = "2026-10-06T18:39:24.680Z",
            LastCheckinAt = "2026-10-06T18:39:26.706Z",
        };

    private static readonly DateTimeOffset FixedTime = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>一个用一次就清理的临时目录。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "buddydeck-transfer-" + Guid.NewGuid().ToString("N")[..10]);
            System.IO.Directory.CreateDirectory(Path);
        }

        public string File(string name = "export.json") => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    // ① 往返一致
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 往返一致_逐字段相等()
    {
        var original = Entry();
        var sealedDoc = AccountTransfer.Seal([original], Passphrase, "workbuddy", FixedTime);

        var opened = AccountTransfer.Open(sealedDoc, Passphrase);

        var back = Assert.Single(opened.Accounts);

        Assert.Equal(original.Id, back.Id);
        Assert.Equal(original.Uid, back.Uid);
        Assert.Equal(original.EnterpriseId, back.EnterpriseId);
        Assert.Equal(original.Domain, back.Domain);
        Assert.Equal(original.AccessToken, back.AccessToken);      // ★ token 必须一致
        Assert.Equal(original.RefreshToken, back.RefreshToken);
        Assert.Equal(original.ExpiresAtMs, back.ExpiresAtMs);       // ★ 过期时间
        Assert.Equal(original.RefreshExpiresAtMs, back.RefreshExpiresAtMs);
        Assert.Equal(original.Label, back.Label);                   // ★ 标签
        Assert.Equal(original.AddedAt, back.AddedAt);
        Assert.Equal(original.LastSeenAt, back.LastSeenAt);
        Assert.Equal(original.LastCheckinAt, back.LastCheckinAt);
    }

    [Fact]
    public void 往返一致_多账号()
    {
        var accounts = new[]
        {
            Entry("acc-1", "u1", "e1", label: "号一"),
            Entry("acc-2", "u2", "e2", label: "号二"),
            Entry("acc-3", "u3", null, label: "个人号", domain: ""),
        };

        var opened = AccountTransfer.Open(
            AccountTransfer.Seal(accounts, Passphrase, "workbuddy", FixedTime), Passphrase);

        Assert.Equal(3, opened.Accounts.Count);

        for (var i = 0; i < accounts.Length; i++)
        {
            Assert.Equal(accounts[i].Id, opened.Accounts[i].Id);
            Assert.Equal(accounts[i].AccessToken, opened.Accounts[i].AccessToken);
            Assert.Equal(accounts[i].Label, opened.Accounts[i].Label);
        }
    }

    [Fact]
    public void 往返一致_企业版与个人版()
    {
        // EnterpriseId = null 的个人账号
        var personal = Entry("acc-personal", enterpriseId: null, domain: "");

        var opened = AccountTransfer.Open(
            AccountTransfer.Seal([personal], Passphrase, "workbuddy", FixedTime), Passphrase);

        var back = Assert.Single(opened.Accounts);

        Assert.Null(back.EnterpriseId);
        Assert.Equal("", back.Domain);
    }

    [Fact]
    public void 往返一致_中文标签与特殊字符()
    {
        var entry = Entry(label: "志强池的号（备用）#2 —— 含 emoji 🎯");

        var opened = AccountTransfer.Open(
            AccountTransfer.Seal([entry], Passphrase, "workbuddy", FixedTime), Passphrase);

        // 中文/emoji 不能被转义坏掉
        Assert.Equal(entry.Label, opened.Accounts[0].Label);
    }

    [Fact]
    public void 空保险库能导出能导入()
    {
        var opened = AccountTransfer.Open(
            AccountTransfer.Seal([], Passphrase, "workbuddy", FixedTime), Passphrase);

        // ⚠ 空库导出是正常操作（用户想导出一个"空包"或者刚清完号）——
        // 不能抛异常。旧版的 openAccounts 遇到空 accounts 会抛
        // 「文件里没有账号」，那是**导入侧**的防呆；
        // 这里保持"能往返"，让调用方自己决定要不要提示。
        Assert.Empty(opened.Accounts);
        Assert.Equal("workbuddy", opened.Provider);
    }

    [Fact]
    public void 导入保留provider和导出时间()
    {
        var opened = AccountTransfer.Open(
            AccountTransfer.Seal([Entry()], Passphrase, "workbuddy-ai", FixedTime), Passphrase);

        Assert.Equal("workbuddy-ai", opened.Provider);
        Assert.Equal("2026-10-07T12:00:00.000Z", opened.ExportedAt);
    }

    [Fact]
    public void 两次导出密文不同_盐和iv每次都是新的()
    {
        var a = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);
        var b = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        // 盐/iv 必须是随机的 —— 否则同一个口令每次导出同样的密文，
        // 攻击者可以据此判断"两次导出的内容一样"
        Assert.NotEqual(a["kdf"]!["salt"]!.GetValue<string>(), b["kdf"]!["salt"]!.GetValue<string>());
        Assert.NotEqual(a["iv"]!.GetValue<string>(), b["iv"]!.GetValue<string>());
        Assert.NotEqual(a["payload"]!.GetValue<string>(), b["payload"]!.GetValue<string>());
    }

    // ══════════════════════════════════════════════════════════
    // ② 错误口令必须报错
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 错误口令报错而不是解出乱码()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(doc, "wrong passphrase here"));

        // 消息里**不能**出现口令本身
        Assert.DoesNotContain("wrong passphrase here", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 口令差一个字符就解不开()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(doc, Passphrase + "x"));
    }

    [Fact]
    public void 口令错与文件被改过报同一句话()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        var wrongPass = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(doc, "another wrong passphrase"));

        // 改一个密文字节 → 认证标签失配
        var tampered = JsonNode.Parse(doc.ToJsonString())!.AsObject();
        var payload = Convert.FromBase64String(tampered["payload"]!.GetValue<string>());
        payload[0] ^= 0xFF;
        tampered["payload"] = Convert.ToBase64String(payload);

        var corrupted = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(tampered, Passphrase));

        // ⚠ 两种失败必须**报同一句话**：区分了就等于告诉攻击者
        // 「口令对了但文件坏了」—— 那是可用的旁路信息
        Assert.Equal(wrongPass.Message, corrupted.Message);
    }

    [Fact]
    public void 认证标签被改也解不开()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        var tag = Convert.FromBase64String(doc["tag"]!.GetValue<string>());
        tag[0] ^= 0xFF;
        doc["tag"] = Convert.ToBase64String(tag);

        // 这条是关键：即使密文没动，标签变了也必须拒绝 ——
        // 证明 GCM 的认证**真的在用**，不是摆设
        Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(doc, Passphrase));
    }

    [Fact]
    public void 短口令在导出和导入时都拒绝()
    {
        // 导出侧
        Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Seal([Entry()], "short", "workbuddy", FixedTime));

        Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Seal([Entry()], "", "workbuddy", FixedTime));

        // 导入侧（先报"口令太短"比让用户以为文件坏了有帮助）
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(doc, "abc"));
    }

    // ══════════════════════════════════════════════════════════
    // ③ 格式与版本
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 容器有格式标识与版本()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        Assert.Equal("workbuddy-accounts", doc["format"]!.GetValue<string>());
        Assert.Equal(1, doc["version"]!.GetValue<int>());
        Assert.Equal("aes-256-gcm", doc["cipher"]!.GetValue<string>());
    }

    [Fact]
    public void KDF参数与旧版逐项一致()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);
        var kdf = doc["kdf"]!.AsObject();

        // ⚠ 任何一项和旧版不同 → 生成的密文两边互相解不开
        Assert.Equal("scrypt", kdf["name"]!.GetValue<string>());
        Assert.Equal(16384, kdf["N"]!.GetValue<int>());
        Assert.Equal(8, kdf["r"]!.GetValue<int>());
        Assert.Equal(1, kdf["p"]!.GetValue<int>());
        Assert.Equal(32, kdf["keylen"]!.GetValue<int>());

        // iv / tag 长度是 AES-GCM 的标准值
        Assert.Equal(12, Convert.FromBase64String(doc["iv"]!.GetValue<string>()).Length);
        Assert.Equal(16, Convert.FromBase64String(doc["tag"]!.GetValue<string>()).Length);
    }

    [Fact]
    public void 版本不匹配时报出版本号()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);
        doc["version"] = 99;

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(doc, Passphrase));

        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 格式标识不对时报人话()
    {
        var notOurs = new JsonObject { ["format"] = "something-else", ["version"] = 1 };

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(notOurs, Passphrase));

        Assert.Contains("workbuddy-accounts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 缺字段的文件报文件损坏()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        foreach (var key in (string[])["iv", "tag", "payload"])
        {
            var broken = JsonNode.Parse(doc.ToJsonString())!.AsObject();
            broken.Remove(key);

            var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
                AccountTransfer.Open(broken, Passphrase));

            // 结构层失败 → 明说「文件损坏」，**不该**把用户引去试口令
            Assert.Equal(AccountTransfer.CorruptedMessage, ex.Message);
        }
    }

    // ══════════════════════════════════════════════════════════
    // ③b 错误措辞的分界线（t49 修正）
    //
    // ## 为什么这两类可以分开说
    //
    // 报错信息按「有没有过口令这道关」分两类：
    //
    // ```
    // 结构层（还没派生密钥，没碰过口令）
    //   字段缺失 / base64 非法 / iv,tag 长度不符 / 不是合法 JSON
    //   → 明说「文件已损坏」    ← 分开说泄露不了任何东西
    //
    // 认证层（已经用口令派生过密钥）
    //   GCM authTag 失配
    //   → 合并「口令不对，或文件被改过」  ← 分开说等于泄露口令是否猜中
    // ```
    //
    // ⚠ 本节最后一条（`正确结构与错误口令_仍报合并措辞`）是**防走过头**的：
    //   改这个分界线时很容易顺手把认证层也分开，那会把旁路保护拆掉。
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 结构层_字段缺失_报文件损坏类()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        var broken = JsonNode.Parse(doc.ToJsonString())!.AsObject();
        broken.Remove("payload");

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(broken, Passphrase));

        Assert.Equal(AccountTransfer.CorruptedMessage, ex.Message);
        // 措辞里**不该**出现"口令"——否则又把用户引去试口令
        Assert.DoesNotContain("口令", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 结构层_base64非法_报文件损坏类()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        var broken = JsonNode.Parse(doc.ToJsonString())!.AsObject();
        // 这不是合法 base64（含非法字符 `!` 与 `?`）
        broken["payload"] = "这不是合法base64!!??";

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(broken, Passphrase));

        Assert.Equal(AccountTransfer.CorruptedMessage, ex.Message);
        Assert.DoesNotContain("口令", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 结构层_iv长度不符_报文件损坏类()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        var broken = JsonNode.Parse(doc.ToJsonString())!.AsObject();
        // 合法 base64，但解出来只有 8 字节（AES-GCM 要 12）
        broken["iv"] = Convert.ToBase64String(new byte[8]);

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(broken, Passphrase));

        Assert.Equal(AccountTransfer.CorruptedMessage, ex.Message);
        Assert.DoesNotContain("口令", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 结构层_tag长度不符_报文件损坏类()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        var broken = JsonNode.Parse(doc.ToJsonString())!.AsObject();
        // GCM 的认证标签必须是 16 字节
        broken["tag"] = Convert.ToBase64String(new byte[8]);

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(broken, Passphrase));

        Assert.Equal(AccountTransfer.CorruptedMessage, ex.Message);
        Assert.DoesNotContain("口令", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 结构层_不是合法JSON_报文件损坏类()
    {
        using var dir = new TempDir();
        var path = dir.File();

        File.WriteAllText(path, "{这不是合法 JSON", new UTF8Encoding(false));

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.ImportFromFile(path, Passphrase));

        Assert.Equal(AccountTransfer.CorruptedMessage, ex.Message);
        Assert.DoesNotContain("口令", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ★ 防走过头：**结构完全正常 + 口令错** 时仍必须报**合并措辞**。
    ///
    /// 这一条是 t49 的护栏。改那个分界线时最容易犯的错是
    /// 「既然结构层能分开，那认证层也分开吧」—— 那会把旁路保护拆掉：
    /// 能区分「口令错」与「文件被改」就意味着系统成了**口令预言机**，
    /// 攻击者可离线确认口令是否猜中。
    /// </summary>
    [Fact]
    public void 正确结构与错误口令_仍报合并措辞()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(doc, "definitely the wrong passphrase"));

        // 必须是合并措辞 —— 同时提到「口令」和「文件」，不区分是哪一种
        Assert.Equal(AccountTransfer.UnlockFailedMessage, ex.Message);
        Assert.Contains("口令", ex.Message, StringComparison.Ordinal);
        Assert.Contains("文件", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>★ 同上：密文被改过一个字节（结构正常、认证失败）也必须报合并措辞。</summary>
    [Fact]
    public void 结构正常但密文被改_报合并措辞()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        var payload = Convert.FromBase64String(doc["payload"]!.GetValue<string>());
        payload[0] ^= 0xFF;
        doc["payload"] = Convert.ToBase64String(payload);

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(doc, Passphrase));

        // 认证层 —— 即使**口令是对的**也不能告诉调用方「口令对了、文件坏了」
        Assert.Equal(AccountTransfer.UnlockFailedMessage, ex.Message);
    }

    /// <summary>
    /// ★ 两类措辞必须**确实不同** —— 否则上面几条断言可能因为
    /// 「两个常量恰好是同一句话」而全部通过（假绿）。
    /// </summary>
    [Fact]
    public void 两类措辞确实不同()
    {
        Assert.NotEqual(AccountTransfer.CorruptedMessage, AccountTransfer.UnlockFailedMessage);
    }

    // ══════════════════════════════════════════════════════════
    // ④ 文件读写：原子写 + 权限
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 导出到文件再导入回来一致()
    {
        using var dir = new TempDir();
        var path = dir.File();

        AccountTransfer.ExportToFile(path, [Entry()], Passphrase, "workbuddy", FixedTime);

        Assert.True(File.Exists(path));

        var opened = AccountTransfer.ImportFromFile(path, Passphrase);
        Assert.Equal("at-secret", Assert.Single(opened.Accounts).AccessToken);
    }

    [Fact]
    public void 导出不留下临时文件()
    {
        using var dir = new TempDir();
        var path = dir.File();

        AccountTransfer.ExportToFile(path, [Entry()], Passphrase, "workbuddy", FixedTime);

        // 原子写用的是 <path>.tmp —— 成功后必须已经改名走，
        // 留着的话下次导出会撞名，而且用户会看到两个文件
        Assert.False(File.Exists(path + ".tmp"), "临时文件没有被改名走");
        Assert.Single(System.IO.Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void 重复导出覆盖旧文件_不产生半截文件()
    {
        using var dir = new TempDir();
        var path = dir.File();

        AccountTransfer.ExportToFile(path, [Entry(label: "第一版")], Passphrase, "workbuddy", FixedTime);
        var size1 = new FileInfo(path).Length;

        AccountTransfer.ExportToFile(path, [Entry(label: "第二版"), Entry("acc-2")], Passphrase, "workbuddy", FixedTime);

        // 新文件是完整可解析的（不是两次内容混在一起）
        var opened = AccountTransfer.ImportFromFile(path, Passphrase);
        Assert.Equal(2, opened.Accounts.Count);
        Assert.NotEqual(size1, new FileInfo(path).Length);
    }

    [Fact]
    public void Unix上权限收成0600()
    {
        if (OperatingSystem.IsWindows()) return;   // Windows 交给 NTFS 继承 ACE

        using var dir = new TempDir();
        var path = dir.File();

        AccountTransfer.ExportToFile(path, [Entry()], Passphrase, "workbuddy", FixedTime);

        var mode = File.GetUnixFileMode(path);

        // 这个文件里是加密凭据：拿到它 + 口令 = 拿到账号。
        // 组/其他用户一个权限位都不该有。
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(mode.HasFlag(UnixFileMode.GroupWrite));
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
        Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
    }

    [Fact]
    public void 导入不存在的文件报错不抛原生异常()
    {
        using var dir = new TempDir();

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.ImportFromFile(dir.File("nope.json"), Passphrase));

        Assert.Contains("找不到文件", ex.Message, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ 损坏 / 截断
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 截断的文件报错不崩()
    {
        using var dir = new TempDir();
        var path = dir.File();

        AccountTransfer.ExportToFile(path, [Entry()], Passphrase, "workbuddy", FixedTime);

        // 砍掉一半字节 —— 模拟传输中断
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.ImportFromFile(path, Passphrase));
    }

    [Fact]
    public void 空文件报错()
    {
        using var dir = new TempDir();
        var path = dir.File();

        File.WriteAllText(path, "", new UTF8Encoding(false));

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.ImportFromFile(path, Passphrase));

        Assert.Contains("空文件", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 不是JSON的文件报人话()
    {
        using var dir = new TempDir();
        var path = dir.File("random.txt");

        File.WriteAllText(path, "这不是 JSON", new UTF8Encoding(false));

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.ImportFromFile(path, Passphrase));

        Assert.DoesNotContain("JsonException", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JSON是数组而不是对象时报人话()
    {
        using var dir = new TempDir();
        var path = dir.File();

        File.WriteAllText(path, "[1,2,3]", new UTF8Encoding(false));

        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.ImportFromFile(path, Passphrase));

        Assert.Contains("不是导出的账号文件", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 密文被改一个字节就解不开()
    {
        using var dir = new TempDir();
        var path = dir.File();

        AccountTransfer.ExportToFile(path, [Entry()], Passphrase, "workbuddy", FixedTime);

        var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var payload = Convert.FromBase64String(doc["payload"]!.GetValue<string>());
        payload[^1] ^= 0x01;   // 改最后一个字节
        doc["payload"] = Convert.ToBase64String(payload);

        File.WriteAllText(path, doc.ToJsonString(), new UTF8Encoding(false));

        // 传输过程中被改坏 → GCM 拦下
        Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.ImportFromFile(path, Passphrase));
    }

    // ══════════════════════════════════════════════════════════
    // ⑥ 与旧版的互通（最关键的一组）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 用**固定向量**证明与旧版 `transfer.js` 互通。
    ///
    /// 怎么造的：盐、iv、口令全部写死，直接调用旧版 Node 的
    /// `crypto.scryptSync` + `createCipheriv("aes-256-gcm")` 生成。
    /// 这里把它解出来，证明**我的 scrypt 与 Node 的 scrypt 输出一致**、
    /// 我的 GCM 解密与它的加密匹配。
    ///
    /// ⚠ 为什么必须有这条：往返测试（自己 seal → 自己 open）在
    /// 「两边都错」时照样绿。只有拿**外部生成的密文**来解，
    /// 才能真正证明格式与算法互通。
    /// </summary>
    [Fact]
    public void 能解开旧版transfer_js生成的容器()
    {
        // 由 Node 的 transfer.js 生成（口令 = "legacy-passphrase-2026"）
        // 明文：{"provider":"workbuddy","exportedAt":"2026-09-25T00:00:00.000Z",
        //        "accounts":[{"id":"legacy-1","uid":"u-legacy",...,"accessToken":"at-legacy",...}]}
        var legacy = JsonNode.Parse(LegacyVectorJson)!.AsObject();

        var opened = AccountTransfer.Open(legacy, "legacy-passphrase-2026");

        var account = Assert.Single(opened.Accounts);

        Assert.Equal("legacy-1", account.Id);
        Assert.Equal("u-legacy", account.Uid);
        Assert.Equal("at-legacy-token", account.AccessToken);   // ★ token 解出来了
        Assert.Equal("rt-legacy-refresh", account.RefreshToken);
        Assert.Equal(1793731164678, account.ExpiresAtMs);
        Assert.Equal("旧版导出的号", account.Label);
        Assert.Equal("workbuddy", opened.Provider);
    }

    /// <summary>
    /// 由旧版 `transfer.js`（Node crypto）真实生成的容器。
    ///
    /// **生成方式（可重现）**：盐 = 0x00..0x0F、iv = 0x10..0x1B、
    /// 口令 = `legacy-passphrase-2026`，用 Node 走一遍旧版的
    /// `scryptSync(..., {N:16384,r:8,p:1})` + `createCipheriv("aes-256-gcm")`。
    /// 因为盐和 iv 都是写死的，这个向量可以被任何人重新生成并核对。
    /// </summary>
    private const string LegacyVectorJson = """
        {
          "format": "workbuddy-accounts",
          "version": 1,
          "provider": "workbuddy",
          "createdAt": "2026-09-25T00:00:00.000Z",
          "cipher": "aes-256-gcm",
          "kdf": {
            "name": "scrypt",
            "N": 16384,
            "r": 8,
            "p": 1,
            "keylen": 32,
            "salt": "AAECAwQFBgcICQoLDA0ODw=="
          },
          "iv": "EBESExQVFhcYGRob",
          "tag": "4NsycUq+AxTaOcx3fQDq/w==",
          "payload": "JtUYDzEJ4FjM0iaukmwxkk5R6hvHcbo3mkcxm97w9sjRHw2aJCLLBdTB77THIIgPPP9YVJb7l3xmdYBtTDRsztYuDPb8Pds2uBC5ofyWzQe6EzUyq3EjfIxzfQq9O5S5UgorXtYHThVjU2Z511nVSVRIg3AfelGweTAaceQeEzbNYKszv6R+IIHX1nlWV720SEsta7yTj0Xv+L5EnTIrw+7CBpKveO8Eh91hBNCkQJ4ynRoahShRw/VTya+oH388dwQUQ7ljyvnfUU/hU6HkvcTaWvF0pcMLamAnZ3FFarY4VTtmIUp8U8u3AWgDKRYPOEebBlcx0h6rNLN3edPIGUythcxCikHP5UOTZdpmT8zlxVIVhV/qTBVp/I3niG2RhnVKATWW3SGrPuIU3deHF9M3tbsPiORqJv2CsBt4gd/J7gBn7OYnaKBe3Y318MOwKZcLjEtxypH2qVx7iYn9c+6/ycG8/BSa/lUkgwuFy7cjaWIiqfCsWOTnvwK0NxfBOSlzOQQXrbWBm2F15qVK7DwGNjr3aqfk5LFvLvXAPnlbX9WvpV8m6YPbW2zGFlhs/eCq72KS6IReOao="
        }
        """;

    // ══════════════════════════════════════════════════════════
    // ⑦ 口令不进日志 / 不落盘
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 导出文件里没有口令的任何痕迹()
    {
        using var dir = new TempDir();
        var path = dir.File();

        const string secret = "MySecretPassphrase123";
        AccountTransfer.ExportToFile(path, [Entry()], secret, "workbuddy", FixedTime);

        var text = File.ReadAllText(path);

        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        // 连 base64 形式也不该有
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), text, StringComparison.Ordinal);
        // 明文 token 也不能出现在文件里（必须已加密）
        Assert.DoesNotContain("at-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rt-secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 文件里只有可公开的KDF参数和盐()
    {
        using var dir = new TempDir();
        var path = dir.File();

        AccountTransfer.ExportToFile(path, [Entry()], Passphrase, "workbuddy", FixedTime);

        var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        // 容器里允许出现的字段（白名单）—— 多出任何东西都要问一句"这该公开吗"
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "format", "version", "provider", "createdAt", "cipher",
            "kdf", "iv", "tag", "payload",
        };

        foreach (var (key, _) in doc)
        {
            Assert.True(allowed.Contains(key), $"容器里出现了预期外的字段：{key}");
        }
    }

    [Fact]
    public void 异常消息里不含口令()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        const string attempt = "ThisIsMyWrongAttempt";
        var ex = Assert.Throws<AccountTransfer.TransferException>(() =>
            AccountTransfer.Open(doc, attempt));

        Assert.DoesNotContain(attempt, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, ex.Message, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════
    // ⑧ 辅助
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 建议文件名带变体和日期()
    {
        Assert.Equal(
            "workbuddy-accounts-workbuddy-2026-10-07.json",
            AccountTransfer.SuggestedFileName("workbuddy", FixedTime));
    }

    [Fact]
    public void 口令确认比较()
    {
        Assert.True(AccountTransfer.SamePassphrase("abc12345", "abc12345"));
        Assert.False(AccountTransfer.SamePassphrase("abc12345", "abc12346"));
        Assert.False(AccountTransfer.SamePassphrase("abc12345", "abc1234"));
        Assert.True(AccountTransfer.SamePassphrase(null, null));
    }

    [Fact]
    public void KDF参数识别()
    {
        var doc = AccountTransfer.Seal([Entry()], Passphrase, "workbuddy", FixedTime);

        Assert.True(AccountTransfer.IsSupportedKdf(doc));

        doc["kdf"]!["N"] = 4096;
        Assert.False(AccountTransfer.IsSupportedKdf(doc));
    }
}
