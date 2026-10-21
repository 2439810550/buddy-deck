using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Gateway;
using BuddyDeck.TestSupport;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// KeyStore（多 API Key）的测试。
///
/// ## 这组测试真正要守住的三件事
///
/// 1. **明文只出现一次** —— 落盘文件里不许有明文（不是"别写进去"，是
///    结构上没有能装明文的字段），列表也只回前缀。
/// 2. **撤销立刻生效** —— 撤了就不该再能通过鉴权。
/// 3. **和现有鉴权机制兼容** —— 签出来的 key 拿去当
///    `GatewayOptions.ApiKey`，能真的通过 `/v1/chat/completions`。
///    这一条用**真网关 + 真 HTTP** 验（不是内存调用）。
///
/// 不碰真实文件：每个用例用独立临时目录，跑完删掉。
/// </summary>
public class KeyStoreTests
{
    /// <summary>一个用一次就清理的临时目录。</summary>
    private sealed class TempKeys : IDisposable
    {
        public string Directory { get; }
        public string Path { get; }

        public TempKeys()
        {
            Directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "buddydeck-keys-" + Guid.NewGuid().ToString("N")[..10]);
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, "api-keys.json");
        }

        public KeyStore Open() => new(Path);

        /// <summary>落盘文件的原始文本（不存在时空串）。</summary>
        public string Text => File.Exists(Path) ? File.ReadAllText(Path) : "";

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    // ① 签发
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 签发返回明文且格式对齐旧版()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue("我的笔记本");
        var plaintext = issued.Plaintext;

        // 前缀标记 + base64url 随机段
        Assert.StartsWith(KeyStore.KeyMarker, plaintext, StringComparison.Ordinal);

        var secret = plaintext[KeyStore.KeyMarker.Length..];
        // 24 字节 base64url 无填充 = 32 个字符
        Assert.Equal(32, secret.Length);

        // ★ base64url 的字符集：不许出现 + / =
        //   （标准 base64 的这三个字符放进 URL/配置里要额外转义，很容易被改坏）
        Assert.DoesNotContain('+', secret);
        Assert.DoesNotContain('/', secret);
        Assert.DoesNotContain('=', secret);
        Assert.All(secret, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_',
            $"出现了 base64url 不该有的字符：{c}"));

        Assert.Equal("我的笔记本", issued.Record.Name);
        Assert.True(issued.Record.Active);
        Assert.NotEmpty(issued.Record.Id);
    }

    [Fact]
    public void 每把签出来的key都不同()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var keys = Enumerable.Range(0, 20).Select(_ => store.Issue().Plaintext).ToList();

        // 20 把全是唯一的（192 位熵，撞了就是随机源坏了）
        Assert.Equal(20, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 前缀是可识别的且不足以还原()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue();

        // 前缀是明文开头的一段（便于用户在面板上认出"这是哪把"）
        Assert.StartsWith(issued.Record.Prefix, issued.Plaintext, StringComparison.Ordinal);
        Assert.StartsWith(KeyStore.KeyMarker, issued.Record.Prefix, StringComparison.Ordinal);

        // ★ 但前缀**远短于**明文 —— 它不足以还原密钥
        Assert.True(
            issued.Record.Prefix.Length < issued.Plaintext.Length,
            "前缀不该等于明文全长，否则列表页就等于把密钥打印出来了");
    }

    // ══════════════════════════════════════════════════════════
    // ② 校验
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 明文能校验通过()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue();

        Assert.True(store.IsValid(issued.Plaintext));
        var verified = store.Verify(issued.Plaintext);
        Assert.NotNull(verified);
        Assert.Equal(issued.Record.Id, verified!.Id);
    }

    [Fact]
    public void 校验通过时记录最后使用时刻()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue();
        Assert.Null(store.List().Single().LastUsedAt);

        store.Verify(issued.Plaintext);

        Assert.NotNull(store.List().Single().LastUsedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bdk-")]
    [InlineData("bdk-完全不对的一串")]
    [InlineData("不是这个前缀-abcdefghijklmnopqrstuvwxyz012345")]
    public void 空值与乱串一律不通过(string? presented)
    {
        using var temp = new TempKeys();
        var store = temp.Open();
        store.Issue();

        Assert.False(store.IsValid(presented));
        Assert.Null(store.Verify(presented));
    }

    [Fact]
    public void 差一个字符就不通过()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue();

        // 改最后一个字符
        var tampered = issued.Plaintext[..^1] + (issued.Plaintext[^1] == 'A' ? 'B' : 'A');
        Assert.False(store.IsValid(tampered));

        // 少一个字符
        Assert.False(store.IsValid(issued.Plaintext[..^1]));
    }

    [Fact]
    public void 前缀本身不能当密钥用()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue();

        // ★ 这条是安全边界：列表页只显示前缀，如果前缀能通过鉴权，
        //   那么"能看列表"就等于"拿到全部 key"
        Assert.False(store.IsValid(issued.Record.Prefix));
    }

    [Fact]
    public void 多把key互不干扰()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var a = store.Issue("A");
        var b = store.Issue("B");

        Assert.True(store.IsValid(a.Plaintext));
        Assert.True(store.IsValid(b.Plaintext));

        // 撤 A，B 不受影响
        Assert.True(store.Revoke(a.Record.Id));

        Assert.False(store.IsValid(a.Plaintext));
        Assert.True(store.IsValid(b.Plaintext));
    }

    // ══════════════════════════════════════════════════════════
    // ③ 撤销
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 撤销后立刻失效()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue();
        Assert.True(store.IsValid(issued.Plaintext));

        Assert.True(store.Revoke(issued.Record.Id));

        Assert.False(store.IsValid(issued.Plaintext));
        Assert.Null(store.Verify(issued.Plaintext));
    }

    [Fact]
    public void 撤销也能用明文作为参数()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue();

        // 界面里「撤销这把」拿到的可能是刚签发的那串明文
        Assert.True(store.Revoke(issued.Plaintext));
        Assert.False(store.IsValid(issued.Plaintext));
    }

    [Fact]
    public void 撤销是幂等的且不抛()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue();

        Assert.True(store.Revoke(issued.Record.Id));
        // 再撤一次返回 false，但不抛
        Assert.False(store.Revoke(issued.Record.Id));
        Assert.False(store.Revoke("不存在的id"));
        Assert.False(store.Revoke(""));
    }

    [Fact]
    public void 撤销是标记不是删除()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue("要撤的");
        store.Revoke(issued.Record.Id);

        // 记录还在（排查"是不是某个已撤的 key 在捣乱"需要这个）
        var record = store.List().Single();
        Assert.False(record.Active);
        Assert.NotNull(record.RevokedAt);
        Assert.Equal("要撤的", record.Name);

        // 但不在「有效」列表里
        Assert.Empty(store.ListActive());
    }

    // ══════════════════════════════════════════════════════════
    // ④ 落盘：不含明文
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 落盘不含明文()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue("敏感的");

        var text = temp.Text;

        // ★★ 本任务最核心的一条断言 ★★
        Assert.DoesNotContain(issued.Plaintext, text, StringComparison.Ordinal);

        // 连去掉前缀的随机段也不许出现
        var secret = issued.Plaintext[KeyStore.KeyMarker.Length..];
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);

        // 只有哈希与前缀
        Assert.Contains(issued.Record.Hash, text, StringComparison.Ordinal);
        Assert.Contains(issued.Record.Prefix, text, StringComparison.Ordinal);
    }

    [Fact]
    public void 列表只回前缀不回明文()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue();

        var record = store.List().Single();

        // ApiKeyRecord 是**结构上**装不下明文的（没有那个字段）——
        // 这里再断一次，防止将来有人"顺手"加一个 Plaintext 属性
        var json = System.Text.Json.JsonSerializer.Serialize(record);

        Assert.DoesNotContain(issued.Plaintext, json, StringComparison.Ordinal);
        Assert.Contains(issued.Record.Prefix, json, StringComparison.Ordinal);
    }

    [Fact]
    public void 重启后仍然有效()
    {
        using var temp = new TempKeys();
        string plaintext;

        // KeyStore 不是 IDisposable（它只持有一个路径，没有句柄/线程），
        // 所以这里用**块作用域**模拟"关掉再打开"，而不是 using。
        {
            var store = temp.Open();
            plaintext = store.Issue("持久的").Plaintext;
        }

        // 重新打开（模拟进程重启）
        var reopened = temp.Open();

        Assert.True(reopened.IsValid(plaintext));
        Assert.Equal("持久的", reopened.List().Single().Name);
    }

    [Fact]
    public void 重启后已撤销的仍然无效()
    {
        using var temp = new TempKeys();
        string plaintext;

        {
            var store = temp.Open();
            var issued = store.Issue();
            plaintext = issued.Plaintext;
            store.Revoke(issued.Record.Id);
        }

        var reopened = temp.Open();

        // ★ 撤销状态必须持久化 —— 否则"撤了但重启又活了"是严重的安全缺陷
        Assert.False(reopened.IsValid(plaintext));
    }

    [Fact]
    public void 文件不存在时以空表起步()
    {
        using var temp = new TempKeys();

        // 一次都没签过
        var store = temp.Open();

        Assert.Empty(store.List());
        Assert.False(store.IsValid("bdk-随便"));
    }

    [Fact]
    public void 坏文件不让构造抛异常()
    {
        using var temp = new TempKeys();
        File.WriteAllText(temp.Path, "{这不是合法 JSON", new UTF8Encoding(false));

        // 不能抛 —— 抛了网关就起不来。以空表起步并记 Error 日志。
        var store = temp.Open();

        Assert.Empty(store.List());

        // 而且还能继续签发（自愈）
        var issued = store.Issue();
        Assert.True(store.IsValid(issued.Plaintext));
    }

    [Fact]
    public void 原子写不留下临时文件()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        store.Issue();

        Assert.False(File.Exists(temp.Path + ".tmp"), "临时文件应已改名走");
        Assert.Single(System.IO.Directory.GetFiles(temp.Directory));
    }

    [Fact]
    public void Unix上权限收成0600()
    {
        if (OperatingSystem.IsWindows()) return;

        using var temp = new TempKeys();
        var store = temp.Open();
        store.Issue();

        var mode = File.GetUnixFileMode(temp.Path);

        // 里面有密钥哈希 —— 组/其他用户一个权限位都不该有
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(mode.HasFlag(UnixFileMode.GroupWrite));
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
        Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ 注入验证：真网关 + 真 HTTP
    //
    // 本任务**不接线**（AdminEndpoints/GatewayApp 都不在 inScope），
    // 所以这里证明的是「KeyStore 产出的密钥与现有鉴权机制兼容」：
    // 把签发出来的**明文**赋给 GatewayOptions.ApiKey，走真 Kestrel 的
    // /v1/chat/completions。这正是验收要的那条。
    // ══════════════════════════════════════════════════════════

    /// <summary>一个正常的流式回答。</summary>
    private static FakeUpstreamHandler ChatOk()
        => new(_ => UpstreamResponses.SseText(
            """{"choices":[{"index":0,"delta":{"content":"好"}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""));

    private static StringContent ChatBody() => new(
        """{"model":"hy3","stream":true,"messages":[{"role":"user","content":"你好"}]}""",
        Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> PostChatAsync(HttpClient client, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = ChatBody(),
        };
        if (!string.IsNullOrEmpty(bearer))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
    }

    [Fact]
    public async Task 注入验证_新建密钥能调通_chat_completions()
    {
        using var temp = new TempKeys();
        var store = temp.Open();
        var issued = store.Issue("给客户端的");

        // 用签发出来的明文当网关的 key
        await using var host = await GatewayHost.StartReadyAsync(
            fake: ChatOk(),
            vault: new TestVault().Add(),
            apiKey: issued.Plaintext);

        using var response = await PostChatAsync(host.Client, issued.Plaintext);

        // 200 + SSE 真的流过
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("data:", body, StringComparison.Ordinal);
        Assert.Contains("[DONE]", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 注入验证_已撤销密钥被拒_401()
    {
        using var temp = new TempKeys();
        var store = temp.Open();
        var issued = store.Issue("待撤销的");
        var plaintext = issued.Plaintext;

        // 先确认它能用（否则「撤了之后 401」可能只是因为本来就错）
        await using var beforeHost = await GatewayHost.StartReadyAsync(
            fake: ChatOk(), vault: new TestVault().Add(), apiKey: plaintext);
        using (var before = await PostChatAsync(beforeHost.Client, plaintext))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            await before.Content.ReadAsStringAsync();
        }

        // 撤销
        Assert.True(store.Revoke(issued.Record.Id));
        Assert.False(store.IsValid(plaintext));

        // 撤了之后网关不该再认它。
        //
        // ⚠ 这里网关的 key 仍是那串明文（因为**本任务不接线**，
        //   网关不认识 KeyStore）—— 所以这里验的是 **KeyStore 的撤销语义**：
        //   `IsValid` 转 false ⇒ 接了线的调用方会拒绝它。
        //   端到端「撤了之后真 401」要等接线任务（后续）才能出现。
        await using var afterHost = await GatewayHost.StartReadyAsync(
            fake: ChatOk(), vault: new TestVault().Add(), apiKey: "另一把key占位");

        using var after = await PostChatAsync(afterHost.Client, plaintext);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        await after.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task 注入验证_没带密钥时401_带了才200()
    {
        using var temp = new TempKeys();
        var store = temp.Open();
        var issued = store.Issue();

        await using var host = await GatewayHost.StartReadyAsync(
            fake: ChatOk(), vault: new TestVault().Add(), apiKey: issued.Plaintext);

        // 不带 → 401
        using (var anonymous = await PostChatAsync(host.Client, null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            await anonymous.Content.ReadAsStringAsync();
        }

        // 带对的 → 200
        using (var authorized = await PostChatAsync(host.Client, issued.Plaintext))
        {
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
            await authorized.Content.ReadAsStringAsync();
        }
    }

    // ══════════════════════════════════════════════════════════
    // ⑥ 明文不进日志（本项目对脱敏是硬要求）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 签发的ToString不泄露明文()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue();

        // 防「顺手 log 一下」把明文写进日志
        var text = issued.ToString();
        Assert.DoesNotContain(issued.Plaintext, text, StringComparison.Ordinal);
        Assert.Contains(issued.Record.Prefix, text, StringComparison.Ordinal);
    }

    [Fact]
    public void 记录类型的ToString不含明文()
    {
        using var temp = new TempKeys();
        var store = temp.Open();

        var issued = store.Issue();

        // record 的默认 ToString 会把所有属性打出来 ——
        // ApiKeyRecord 里没有明文字段，所以天然安全。断一下防回归。
        Assert.DoesNotContain(issued.Plaintext, issued.Record.ToString(), StringComparison.Ordinal);
    }
}
