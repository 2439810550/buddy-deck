using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// 账号导入的解析与校验（t81）。
///
/// ## 覆盖策略
///
/// <see cref="AccountImport"/> 刻意不依赖 WPF，所以这里能直接打它 ——
/// **包括「一键读旧版目录」那条路**（用临时目录造一个假的旧版账号库）。
/// 唯一测不到的是 `WpfFilePicker` 里那一行 `ShowDialog()`
/// （需要 STA + 窗口句柄），它在回报里单独标注。
///
/// ## ⚠ 不碰用户的真实凭据
///
/// 主力用例全部用**伪造的同结构 JSON**。对真实文件
/// （`~/.workbuddy-api/accounts.cn.json`）**只做「存在性 + 结构」探测**
/// （字段名、账号条数），**不读 token、不打印内容** ——
/// 那是用户的真实凭据，没有必要为了「证明端到端」去碰它。
/// </summary>
public class AccountImportTests
{
    // ══════════════════════════════════════════════════════════
    // 夹具
    // ══════════════════════════════════════════════════════════

    /// <summary>造一条旧版账号记录的 JSON（形状照真实文件）。</summary>
    private static string LegacyJson(params string[] entries)
        => $$"""{"version":1,"accounts":[{{string.Join(",", entries)}}]}""";

    /// <summary>一条正常的旧版记录。</summary>
    private static string Entry(
        string uid = "uid-00000001",
        string id = "id-00000001",
        string label = "测试号",
        string accessToken = "tok-access-1",
        string refreshToken = "tok-refresh-1",
        string enterpriseId = "ent-1",
        string domain = "workbuddy.cn",
        string expiresAtMs = "1900000000000")
        => $$"""
        {"id":"{{id}}","uid":"{{uid}}","label":"{{label}}",
         "accessToken":"{{accessToken}}","refreshToken":"{{refreshToken}}",
         "enterpriseId":"{{enterpriseId}}","domain":"{{domain}}",
         "expiresAtMs":{{expiresAtMs}}}
        """;

    /// <summary>把内容写进一个临时文件，返回路径。</summary>
    private static string TempFile(string name, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "buddydeck-acctimport-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    // ⚠ 这里曾经有两个 helper（`Gbk()` / `Garble()`），用来**构造**
    //   「GBK 双重编码乱码」以测试还原逻辑。整套还原实现已按实测删除
    //   （真实文件的 label 是正常中文，见本文档 ⑤ 节的说明），
    //   所以这两个 helper 也没有存在意义了 —— 保留一行说明以免下一个人
    //   以为「乱码处理」是个漏掉的功能。

    /// <summary>假文件选择器 —— 返回预设路径，或 null（模拟取消）。</summary>
    private sealed class FakePicker(string? path) : IFilePicker
    {
        public int Calls { get; private set; }

        /// <summary>保存对话框被调用了几次（导出路径用）。</summary>
        public int SaveCalls { get; private set; }

        /// <summary>保存对话框最后一次收到的建议文件名（验"带日期"那条）。</summary>
        public string? LastSuggestedName { get; private set; }

        public string? PickJsonFile(string title)
        {
            Calls++;
            return path;
        }

        public string? PickSaveFile(string title, string suggestedName)
        {
            SaveCalls++;
            LastSuggestedName = suggestedName;
            return path;
        }
    }

    // ══════════════════════════════════════════════════════════
    // ① 解析正常文件
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 解析一条账号()
    {
        var result = AccountImport.Parse(LegacyJson(Entry()));

        Assert.True(result.Ok, result.Message);
        Assert.Single(result.Accounts);

        var account = result.Accounts[0];
        Assert.Equal("uid-00000001", account["uid"]!.GetValue<string>());
        Assert.Equal("id-00000001", account["id"]!.GetValue<string>());
        Assert.Equal("tok-access-1", account["accessToken"]!.GetValue<string>());
        Assert.Equal("workbuddy.cn", account["domain"]!.GetValue<string>());
    }

    [Fact]
    public void 解析多条账号()
    {
        var result = AccountImport.Parse(LegacyJson(
            Entry(uid: "u1", id: "i1", label: "一号"),
            Entry(uid: "u2", id: "i2", label: "二号"),
            Entry(uid: "u3", id: "i3", label: "三号")));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(3, result.Accounts.Count);
        Assert.Equal(3, result.TotalInFile);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void 过期时间是整数背书也能读到()
    {
        // ⚠ 逐类型试的必要性：JSON 的 `1900000000000` 是 long 背书，
        //   但旧版有些记录写的是 int 范围的秒级值。只试 long 会静默读成 0，
        //   后果是导入后所有账号显示「已过期」。
        var result = AccountImport.Parse(LegacyJson(
            """{"id":"i1","uid":"u1","accessToken":"t","expiresAtMs":1700000000}"""));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1700000000L, result.Accounts[0]["expiresAtMs"]!.GetValue<long>());
    }

    [Fact]
    public void 可选字段有才带_没有就不写()
    {
        // 写一堆 null 进去，下游每处都得判空 —— 对齐 import_accounts.py:71-73
        var result = AccountImport.Parse(LegacyJson(Entry()));

        Assert.True(result.Ok, result.Message);

        foreach (var optional in (string[])["refreshExpiresAtMs", "addedAt", "lastSeenAt", "lastCheckinAt"])
        {
            Assert.False(result.Accounts[0].ContainsKey(optional), $"{optional} 不该被写进去");
        }
    }

    [Fact]
    public void 可选字段有值就带上()
    {
        var result = AccountImport.Parse(LegacyJson(
            """{"id":"i1","uid":"u1","accessToken":"t","addedAt":"2026-01-01T00:00:00.000Z"}"""));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("2026-01-01T00:00:00.000Z", result.Accounts[0]["addedAt"]!.GetValue<string>());
    }

    [Fact]
    public void 没有_id_时用_uid_兜底()
    {
        // 旧版偶尔缺 id —— 直接丢掉的话用户会少一个号，而且不知道为什么
        var result = AccountImport.Parse(LegacyJson(
            """{"uid":"u-only","accessToken":"t","label":"只有uid"}"""));

        Assert.True(result.Ok, result.Message);
        Assert.Single(result.Accounts);
        Assert.Equal("u-only", result.Accounts[0]["id"]!.GetValue<string>());
    }

    // ══════════════════════════════════════════════════════════
    // ② 生成可导入的载荷
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 载荷是新版保险库形状且能被解回来()
    {
        var result = AccountImport.Parse(LegacyJson(Entry()));
        var payload = result.ToPayload();

        var doc = JsonNode.Parse(payload)!.AsObject();
        Assert.Equal(1, doc["version"]!.GetValue<int>());

        var accounts = doc["accounts"]!.AsArray();
        Assert.Single(accounts);
        Assert.Equal("uid-00000001", accounts[0]!["uid"]!.GetValue<string>());
    }

    [Fact]
    public void 载荷里没有被跳过的账号()
    {
        var result = AccountImport.Parse(LegacyJson(
            Entry(uid: "u1", id: "i1"),
            """{"id":"i2","uid":"u2","label":"没有token"}"""));

        Assert.True(result.Ok, result.Message);

        var doc = JsonNode.Parse(result.ToPayload())!.AsObject();
        Assert.Single(doc["accounts"]!.AsArray());
    }

    // ══════════════════════════════════════════════════════════
    // ③ 失败分类：文件不存在 / 格式不对
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 文件不存在_有明确错误()
    {
        var missing = Path.Combine(Path.GetTempPath(), "不存在-" + Guid.NewGuid().ToString("N") + ".json");

        var result = AccountImport.FromFile(missing);

        Assert.False(result.Ok);
        Assert.Equal(AccountImport.Failure.Missing, result.Reason);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空路径被拒(string? path)
    {
        var result = AccountImport.FromFile(path);

        Assert.False(result.Ok);
        Assert.Equal(AccountImport.Failure.Missing, result.Reason);
    }

    [Fact]
    public void 不是_JSON_的文件被拒_且带上解析器信息()
    {
        var result = AccountImport.Parse("这不是 JSON，只是一段文字");

        Assert.False(result.Ok);
        Assert.Equal(AccountImport.Failure.NotJson, result.Reason);
        Assert.Contains("JSON", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 空文件被拒()
    {
        var result = AccountImport.Parse("");

        Assert.False(result.Ok);
        Assert.Equal(AccountImport.Failure.NotJson, result.Reason);
    }

    [Fact]
    public void 顶层不是对象被拒()
    {
        var result = AccountImport.Parse("[1,2,3]");

        Assert.False(result.Ok);
        Assert.Equal(AccountImport.Failure.WrongShape, result.Reason);
    }

    [Fact]
    public void 没有_accounts_列表被拒_且提示指向正确的文件()
    {
        // ★ 用户很可能选错了文件（旧版目录里还有 auth.cn.json）。
        //   提示要告诉他「账号库叫 accounts.cn.json」，否则他只会反复试。
        var result = AccountImport.Parse("""{"version":1,"token":"x"}""");

        Assert.False(result.Ok);
        Assert.Equal(AccountImport.Failure.WrongShape, result.Reason);
        Assert.Contains("accounts.cn.json", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void accounts_是空数组被拒()
    {
        var result = AccountImport.Parse("""{"version":1,"accounts":[]}""");

        Assert.False(result.Ok);
        Assert.Equal(AccountImport.Failure.NoAccounts, result.Reason);
    }

    [Fact]
    public void 读不了的文件给_io_错误而不是崩()
    {
        // 用一个目录冒充文件 —— File.ReadAllText 会抛 UnauthorizedAccessException
        var dir = Path.Combine(Path.GetTempPath(), "buddydeck-dir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var result = AccountImport.FromFile(dir);

            Assert.False(result.Ok);
            // 要么被判成不存在，要么是 IO 错误 —— 但绝不能抛出去
            Assert.True(result.Reason is AccountImport.Failure.IoError or AccountImport.Failure.Missing);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ══════════════════════════════════════════════════════════
    // ④ 跳过与原因（导入前要知道会少几个）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 没有凭据的账号被跳过_并说明原因()
    {
        var result = AccountImport.Parse(LegacyJson(
            Entry(uid: "u1", id: "i1"),
            """{"id":"i2","uid":"u2","label":"空号"}"""));

        Assert.True(result.Ok, result.Message);
        Assert.Single(result.Accounts);
        Assert.Single(result.Skipped);
        Assert.Equal(AccountImport.SkipReason.NoToken, result.Skipped[0].Reason);
    }

    [Fact]
    public void 只有_refreshToken_的账号保留()
    {
        // ⚠ 只看 accessToken 太严：旧版有些记录 accessToken 空但 refreshToken 在，
        //   那种导进来还能靠刷新救活，不该丢。
        var result = AccountImport.Parse(LegacyJson(
            """{"id":"i1","uid":"u1","accessToken":"","refreshToken":"r-1","label":"只有refresh"}"""));

        Assert.True(result.Ok, result.Message);
        Assert.Single(result.Accounts);
        Assert.Equal("r-1", result.Accounts[0]["refreshToken"]!.GetValue<string>());
    }

    [Fact]
    public void 没有_uid_的被跳过()
    {
        var result = AccountImport.Parse(LegacyJson(
            Entry(uid: "u1", id: "i1"),
            """{"id":"i2","accessToken":"t","label":"没uid"}"""));

        Assert.True(result.Ok, result.Message);
        Assert.Single(result.Skipped);
        Assert.Equal(AccountImport.SkipReason.NoUid, result.Skipped[0].Reason);
    }

    [Fact]
    public void 文件内重复的_id_只取第一条()
    {
        var result = AccountImport.Parse(LegacyJson(
            Entry(uid: "u1", id: "same-id", label: "第一条"),
            Entry(uid: "u2", id: "same-id", label: "第二条")));

        Assert.True(result.Ok, result.Message);
        Assert.Single(result.Accounts);
        Assert.Equal("第一条", result.Accounts[0]["label"]!.GetValue<string>());
        Assert.Equal(AccountImport.SkipReason.DuplicateInFile, result.Skipped[0].Reason);
    }

    [Fact]
    public void 数组里混进非对象_被跳过而不是崩()
    {
        var result = AccountImport.Parse(LegacyJson(Entry(), "\"我是一个字符串\"", "123"));

        Assert.True(result.Ok, result.Message);
        Assert.Single(result.Accounts);
        Assert.Equal(2, result.Skipped.Count);
        Assert.All(result.Skipped, s => Assert.Equal(AccountImport.SkipReason.NotAnObject, s.Reason));
    }

    [Fact]
    public void 全部被跳过_不算成功()
    {
        // ★ 报成功的话用户点完导入什么都不发生，而界面说「成功 0 个」
        //   —— 比说清原因更没用。
        var result = AccountImport.Parse(LegacyJson(
            """{"id":"i1","uid":"u1"}""",
            """{"id":"i2","uid":"u2"}"""));

        Assert.False(result.Ok);
        Assert.Equal(AccountImport.Failure.NoAccounts, result.Reason);
        Assert.Contains("没有一条可用", result.Message, StringComparison.Ordinal);
        Assert.Equal(2, result.TotalInFile);
    }

    [Fact]
    public void 跳过汇总按原因归类()
    {
        var result = AccountImport.Parse(LegacyJson(
            Entry(uid: "u1", id: "i1"),
            """{"id":"i2","uid":"u2"}""",
            """{"id":"i3","uid":"u3"}""",
            """{"id":"i4","accessToken":"t"}"""));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(3, result.Skipped.Count);

        // 两个没凭据、一个没 uid
        Assert.Equal(2, result.Skipped.Count(s => s.Reason == AccountImport.SkipReason.NoToken));
        Assert.Equal(1, result.Skipped.Count(s => s.Reason == AccountImport.SkipReason.NoUid));

        Assert.Contains("没有登录凭据 2 个", result.SkipSummary, StringComparison.Ordinal);
        Assert.Contains("缺少用户标识 1 个", result.SkipSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void 汇总文案带数量()
    {
        var all = AccountImport.Parse(LegacyJson(Entry()));
        Assert.Equal("将导入 1 个账号", all.Summary);

        var some = AccountImport.Parse(LegacyJson(Entry(), """{"id":"i2","uid":"u2"}"""));
        Assert.Equal("将导入 1 个账号，跳过 1 个", some.Summary);
    }

    // ══════════════════════════════════════════════════════════
    // ④b 确认框文案（契约要求：「导入前显示将导入 N 个」）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 确认文案含总数()
    {
        var result = AccountImport.Parse(LegacyJson(Entry(uid: "u1", id: "i1"), Entry(uid: "u2", id: "i2")));

        Assert.True(result.Ok, result.Message);
        Assert.Contains("将导入 2 个账号", result.ConfirmationText(), StringComparison.Ordinal);
    }

    [Fact]
    public void 确认文案含跳过汇总与明细()
    {
        // ★ 契约：「导入后显示结果（成功 N 个 / 跳过 M 个及原因）」。
        //   有跳过项时用户必须先看到原因 —— 否则导入完发现少几个，
        //   而他不知道该怪谁。
        var result = AccountImport.Parse(LegacyJson(
            Entry(uid: "u1", id: "i1", label: "好号"),
            """{"id":"i2","uid":"u2","label":"没凭据的号"}"""));

        Assert.True(result.Ok, result.Message);

        var text = result.ConfirmationText();
        Assert.Contains("将导入 1 个账号，跳过 1 个", text, StringComparison.Ordinal);
        Assert.Contains("没有登录凭据", text, StringComparison.Ordinal);
        // 明细要能对上号（用户看到的是标签）
        Assert.Contains("没凭据的号", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有跳过项时确认文案只有一行()
    {
        var result = AccountImport.Parse(LegacyJson(Entry()));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("将导入 1 个账号", result.ConfirmationText());
        Assert.DoesNotContain("\n", result.ConfirmationText(), StringComparison.Ordinal);
    }

    [Fact]
    public void 跳过很多时明细截断并给总数()
    {
        // 确认框不该被 50 条明细撑满 —— 但也不能只说「跳过 50 个」
        // 而不给任何线索。前 5 条 + 「还有 N 个」是折中。
        var entries = new List<string> { Entry(uid: "u0", id: "i0") };
        for (var i = 1; i <= 8; i++)
        {
            entries.Add($$"""{"id":"i{{i}}","uid":"u{{i}}","label":"坏号{{i}}"}""");
        }

        var result = AccountImport.Parse(LegacyJson([.. entries]));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(8, result.Skipped.Count);

        var text = result.ConfirmationText();
        Assert.Contains("还有 3 个", text, StringComparison.Ordinal);

        // 恰好列 5 条明细行（以「· 」开头）
        var detailLines = text.Split('\n').Count(line => line.StartsWith("· ", StringComparison.Ordinal));
        Assert.Equal(6, detailLines);   // 5 条明细 + 1 条「还有 N 个」
    }

    [Fact]
    public void 确认文案的明细行数与常量一致()
    {
        // 钉住「最多列几条」这个常量 —— 改它要同时改这条测试，
        // 于是改动会进 review（这正是常量该有的保护）
        Assert.Equal(5, AccountImport.MaxListedSkips);
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ label 直通（**这里曾经测的是「乱码还原」，已按实测删除**）
    //
    // ## 为什么这几条现在测的是「原样保留」而不是「还原」
    //
    // 曾经以为旧版 label 是 GBK 双重编码乱码（依据是
    // `tools/import_accounts.py:27-51` 的 `fix_label`），并按此实现了
    // 拉丁-1 → GBK 还原 + 特征检测。
    //
    // **实测推翻了那个前提**（读真实文件 `~/.workbuddy-api/accounts.cn.json`）：
    // ```
    // 主号 / 少钦号 / 简单快乐 / 杨佳葳 / 张天翼 …
    // 全部是正常中文，一个乱码都没有。
    // ```
    // 之前看到的「乱码」是 **`Get-Content` 的终端显示问题**，不是文件内容。
    //
    // 而且那种形态**本身有损、不可逆**：`€`（U+20AC）超出 Latin-1 的
    // 0x00–0xFF 范围，编回时丢成 `?` —— 所以「还原」不但没用，
    // 还会**改坏正确数据**。
    //
    // ⇒ 实现已整个删除，这几条改为验证「标签原样透传」。
    //    **经验保留：判断编码问题必须用 `read` 工具或字节层，
    //      不能用 `Get-Content` 的终端显示。**
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 真实形态的短中文标签原样保留()
    {
        // 真实文件里的样子（队长只读探测到的实际值）
        foreach (var label in (string[])["主号", "少钦号", "简单快乐", "杨佳葳", "张天翼"])
        {
            var result = AccountImport.Parse(LegacyJson(Entry(label: label)));

            Assert.True(result.Ok, result.Message);
            Assert.Equal(label, result.Accounts[0]["label"]!.GetValue<string>());
        }
    }

    [Fact]
    public void 标签不被做任何转换()
    {
        // ★ 这条是**反证**：若实现里还残留任何「还原」逻辑，
        //   `€` 正是那类转换的特征字符（MojibakeMarkers 里就有它），
        //   含 € 的标签会被改掉。原样透传则必然保持不变。
        const string tricky = "含有 € 符号的标签";

        var result = AccountImport.Parse(LegacyJson(Entry(label: tricky)));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(tricky, result.Accounts[0]["label"]!.GetValue<string>());
    }

    [Fact]
    public void 英文标签原样保留()
    {
        var result = AccountImport.Parse(LegacyJson(Entry(label: "My Account #1")));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("My Account #1", result.Accounts[0]["label"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("简单快乐")]
    [InlineData("工作号 A")]
    [InlineData("中文 English 混排")]
    [InlineData("emoji 也可以 🙂")]
    public void 各类标签往返不变(string label)
    {
        var result = AccountImport.Parse(LegacyJson(Entry(label: label)));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(label, result.Accounts[0]["label"]!.GetValue<string>());
    }

    [Fact]
    public void 没有标签时给一个能认出来的名字()
    {
        // 空标签在列表里是一行空白 —— 用户分不清「这个号叫什么」和「界面坏了」
        var result = AccountImport.Parse(LegacyJson(
            """{"id":"i1","uid":"abcdefgh12345","accessToken":"t"}"""));

        Assert.True(result.Ok, result.Message);

        var label = result.Accounts[0]["label"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.Contains("abcdefgh", label, StringComparison.Ordinal);
    }

    [Fact]
    public void 被跳过条目的显示名是它的标签()
    {
        var result = AccountImport.Parse(LegacyJson(
            """{"id":"i1","uid":"u1","label":"简单快乐"}"""));

        Assert.False(result.Ok);
        // 跳过列表里的名字要可读 —— 否则用户没法判断哪个号被跳过了
        Assert.Equal("简单快乐", result.Skipped[0].Display);
    }

    // ══════════════════════════════════════════════════════════
    // ⑥ 一键读旧版目录
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 默认路径指向旧版数据目录()
    {
        Assert.EndsWith(
            Path.Combine(".workbuddy-api", "accounts.cn.json"),
            AccountImport.DefaultLegacyPath,
            StringComparison.Ordinal);

        Assert.EndsWith(".workbuddy-api", AccountImport.LegacyDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void 旧版目录里没有文件_提示要可操作()
    {
        // ★ 这个功能的失败态几乎总是「这台机器没装过旧版」。
        //   一句「失败」帮不了他 —— 要告诉他改用「从文件导入」。
        var missing = Path.Combine(Path.GetTempPath(), "没有这个目录-" + Guid.NewGuid().ToString("N"));

        var result = AccountImport.FromLegacyDirectory(missing);

        Assert.False(result.Ok);
        Assert.Equal(AccountImport.Failure.Missing, result.Reason);
        Assert.Contains("从文件导入", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 指定路径能读到_用临时目录模拟旧版数据目录()
    {
        // 用假文件走通整条「一键导入」的路 —— 不碰用户真实凭据
        var path = TempFile("accounts.cn.json", LegacyJson(
            Entry(uid: "u1", id: "i1", label: "旧版一号"),
            Entry(uid: "u2", id: "i2", label: "旧版二号")));

        var result = AccountImport.FromLegacyDirectory(path);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.Accounts.Count);
        Assert.Equal("将导入 2 个账号", result.Summary);
    }

    // ══════════════════════════════════════════════════════════
    // ⑦ 文件选择器（抽象层）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 选中文件后能读出账号()
    {
        var path = TempFile("accounts.cn.json", LegacyJson(Entry()));
        var picker = new FakePicker(path);

        var picked = picker.PickJsonFile("选择账号文件");
        Assert.Equal(path, picked);

        var result = AccountImport.FromFile(picked);
        Assert.True(result.Ok, result.Message);
        Assert.Single(result.Accounts);
        Assert.Equal(1, picker.Calls);
    }

    [Fact]
    public void 取消选择时返回_null_不是错误()
    {
        // ★ 「用户点了取消」是正常操作，不是失败 —— 界面不该弹错误提示。
        //   这条钉住「约定是返回 null 而不是抛异常」。
        var picker = new FakePicker(null);

        Assert.Null(picker.PickJsonFile("选择账号文件"));
    }

    // ══════════════════════════════════════════════════════════
    // ⑧ 与真实旧版文件的对齐（只探测结构，不读凭据）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 真实旧版文件的形状与解析器兼容()
    {
        // ⚠ 边界：**只读结构，不打印也不断言任何 token 内容**。
        //   用户的真实凭据没有理由为了「证明兼容」而被搬进测试输出。
        //
        // 真实的那个文件由队长用 tools/import_accounts.py 验过（19 个账号、
        // 标签还能 GBK 还原）。这条只回答一个问题：
        //   「我的解析器对那个文件的真实形状，会不会判成 WrongShape？」
        var path = AccountImport.DefaultLegacyPath;
        if (!File.Exists(path)) return;   // 换台机器就没有，不该让全队红

        var result = AccountImport.FromLegacyDirectory(path);

        // 形状必须被认出来（成功或「一条可用都没有」都说明识别对了形状）
        Assert.NotEqual(AccountImport.Failure.WrongShape, result.Reason);
        Assert.NotEqual(AccountImport.Failure.NotJson, result.Reason);
        Assert.True(result.TotalInFile > 0, "真实账号库里应当有账号");

        // 只断言数量与形状，不碰内容
        Assert.True(result.Accounts.Count + result.Skipped.Count == result.TotalInFile);
    }

    [Fact]
    public void 真实旧版文件的标签是正常中文_不需要任何还原()
    {
        // ★ 这条钉住那个**被推翻的前提**：当初实现「GBK 还原」是因为以为
        //   真实 label 是乱码。实测是正常中文。
        //
        //   ⚠ 边界：只断言「不含乱码特征字符」，**不打印也不断言标签内容**
        //     （那是用户的账号名，没有必要进测试输出）。
        //     队列里 5 个真实标签的形状由队长只读探测过；这里只做
        //     「它们不该被我们改写」这一层。
        var path = AccountImport.DefaultLegacyPath;
        if (!File.Exists(path)) return;   // 换台机器就没有，不该让全队红

        var result = AccountImport.FromLegacyDirectory(path);
        if (result.Accounts.Count == 0) return;

        // 若实现里还有任何「还原」逻辑，含这些特征字符的标签会被改掉；
        // 原样透传的实现则保持输入 == 输出。
        foreach (var account in result.Accounts)
        {
            var label = account["label"]?.GetValue<string>() ?? "";

            foreach (var marker in (char[])['锛', '€', '鍗', '绠', '鏄', '鐢', '涓', '榛', '璁'])
            {
                Assert.DoesNotContain(marker, label);
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    // ⑨ 不变量
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 每一种失败都带非空中文提示()
    {
        AccountImport.Result[] failures =
        [
            AccountImport.Parse(""),
            AccountImport.Parse("不是 JSON"),
            AccountImport.Parse("[1,2]"),
            AccountImport.Parse("""{"no":"accounts"}"""),
            AccountImport.Parse("""{"accounts":[]}"""),
            AccountImport.Parse(LegacyJson("""{"id":"i1","uid":"u1"}""")),
            AccountImport.FromFile(null),
            AccountImport.FromFile(Path.Combine(Path.GetTempPath(), "没有-" + Guid.NewGuid().ToString("N"))),
        ];

        foreach (var failure in failures)
        {
            Assert.False(failure.Ok);
            Assert.NotEqual(AccountImport.Failure.None, failure.Reason);
            Assert.False(string.IsNullOrWhiteSpace(failure.Message));
            Assert.DoesNotContain("Exception", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 成功路径没有失败原因()
    {
        var result = AccountImport.Parse(LegacyJson(Entry()));

        Assert.True(result.Ok);
        Assert.Equal(AccountImport.Failure.None, result.Reason);
        Assert.Equal("", result.Message);
        Assert.NotEmpty(result.Accounts);
    }

    [Fact]
    public void 数量守恒_导入数加跳过数等于文件里总数()
    {
        // ★ 这条防的是「有账号在转换过程中凭空消失」——
        //   那是最难查的一类：用户说「我明明有 19 个号，只进来 17 个」，
        //   而没有这条断言的话，我们连该有几个都不知道。
        var result = AccountImport.Parse(LegacyJson(
            Entry(uid: "u1", id: "i1"),
            Entry(uid: "u2", id: "i2"),
            """{"id":"i3","uid":"u3"}""",
            "\"垃圾\"",
            Entry(uid: "u5", id: "i5")));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(5, result.TotalInFile);
        Assert.Equal(result.Accounts.Count + result.Skipped.Count, result.TotalInFile);
    }
}
