using BuddyDeck.App.Services;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// <see cref="KeyManagement"/> 的测试 —— 「密钥」页的纯逻辑（t75）。
///
/// ## 这一层为什么值得单独测
///
/// 它决定三件**用户直接看到**的事：
/// ```
/// 列表里显示的字符串    → 显示错了用户会把前缀当完整密钥复制走
/// 状态与按钮可用性      → 给已撤销的行一个"撤销"按钮 = 看起来像坏了
/// 失败文案              → 404 说成"not_found"用户没法处置
/// ```
/// 三条都是**错了不报错、只是体验变差**的那一类 —— 正是本项目反复在防的形态。
/// </summary>
public class KeyManagementTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>造一个创建时刻（相对 Now 偏移）。</summary>
    private static long Ago(TimeSpan span) => Now.Subtract(span).ToUnixTimeMilliseconds();

    // ══ ① 前缀显示：永远不是完整密钥 ═════════════════════════════

    [Fact]
    public void 前缀带省略号_让用户知道这不是完整密钥()
    {
        // ⚠ 这条的理由不是好看：**用户会把界面上看到的字符串复制去试**。
        //    不加省略号，他可能以为自己复制的就是密钥本身。
        Assert.Equal("bdk-3f9a2c81…", KeyManagement.DisplayPrefix("bdk-3f9a2c81"));
    }

    [Fact]
    public void 已带省略号的不重复加()
    {
        Assert.Equal("bdk-3f9a2c81…", KeyManagement.DisplayPrefix("bdk-3f9a2c81…"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 前缀为空时给明确占位而不是空白(string? prefix)
    {
        // 空白格子看起来像 bug；"(未知)" 是一个可理解的状态
        Assert.Equal("(未知)", KeyManagement.DisplayPrefix(prefix));
    }

    [Fact]
    public void 前缀两端空白被裁掉()
    {
        Assert.Equal("bdk-abc…", KeyManagement.DisplayPrefix("  bdk-abc  "));
    }

    // ══ ② 名称显示 ═══════════════════════════════════════════════

    [Fact]
    public void 空名称是合法的_显示为未命名()
    {
        // ⚠ 空名称**合法**（Issue(name = "") 允许），不是错误。
        //    但列表里一片空白会让人以为数据坏了。
        Assert.Equal("(未命名)", KeyManagement.DisplayName(""));
        Assert.Equal("(未命名)", KeyManagement.DisplayName(null));
        Assert.Equal("(未命名)", KeyManagement.DisplayName("   "));
    }

    [Fact]
    public void 有名称时原样显示并裁空白()
    {
        Assert.Equal("我的笔记本", KeyManagement.DisplayName("  我的笔记本  "));
    }

    // ══ ③ 状态 ═══════════════════════════════════════════════════

    [Fact]
    public void 状态文案()
    {
        Assert.Equal("可用", KeyManagement.StateLabel(KeyManagement.KeyState.Active));
        Assert.Equal("已撤销", KeyManagement.StateLabel(KeyManagement.KeyState.Revoked));
    }

    [Fact]
    public void 状态给的是资源_key_而不是色值()
    {
        // 与 StatusAppearance 的分工一致：纯逻辑只决定用哪个 key，
        // **具体色值由主题字典管** —— 否则换主题要改 C#
        var key = KeyManagement.StateResourceKey(KeyManagement.KeyState.Active);
        Assert.Contains("Brush", key, StringComparison.Ordinal);
        Assert.DoesNotContain("#", key, StringComparison.Ordinal);
    }

    [Fact]
    public void 只有可用的才允许撤销()
    {
        Assert.True(KeyManagement.CanRevoke(KeyManagement.KeyState.Active));
        // ⚠ 撤销是**终态**（不提供恢复）。给一个点了没反应的按钮，
        //    比不给按钮更糟 —— 用户会以为按钮坏了。
        Assert.False(KeyManagement.CanRevoke(KeyManagement.KeyState.Revoked));
    }

    [Fact]
    public void IsRevoked_与_CanRevoke_互补()
    {
        foreach (var state in Enum.GetValues<KeyManagement.KeyState>())
        {
            Assert.Equal(!KeyManagement.IsRevoked(state), KeyManagement.CanRevoke(state));
        }
    }

    // ══ ④ 时间显示 ═══════════════════════════════════════════════

    [Theory]
    [InlineData(0, "刚刚")]                    // 0 秒前
    [InlineData(30, "刚刚")]                   // 30 秒
    [InlineData(60, "1 分钟前")]               // 边界
    [InlineData(300, "5 分钟前")]
    [InlineData(3600, "1 小时前")]             // 边界
    [InlineData(7200, "2 小时前")]
    [InlineData(86400, "1 天前")]              // 边界
    [InlineData(86400 * 7, "7 天前")]
    [InlineData(86400 * 29, "29 天前")]        // 边界内
    public void 相对时间的分档(int secondsAgo, string expected)
    {
        var createdAt = Ago(TimeSpan.FromSeconds(secondsAgo));
        Assert.Equal(expected, KeyManagement.DisplayCreatedAt(createdAt, Now));
    }

    [Fact]
    public void 超过30天给绝对日期()
    {
        // 「421 天前」没有信息量，不如直接给日期
        var createdAt = Ago(TimeSpan.FromDays(30));
        var text = KeyManagement.DisplayCreatedAt(createdAt, Now);

        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", text);
    }

    [Fact]
    public void 未来时间给绝对日期而不是负数()
    {
        // ⚠ 时钟不同步 / 上游时间漂移时，创建时间可能在"未来"。
        //    显示「-3 小时前」比显示日期糟得多 —— 看起来像 bug。
        var future = Now.AddHours(3).ToUnixTimeMilliseconds();
        var text = KeyManagement.DisplayCreatedAt(future, Now);

        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", text);
        Assert.DoesNotContain("-", text[..4], StringComparison.Ordinal);
    }

    [Fact]
    public void 创建时间为零给未知()
    {
        Assert.Equal("(未知)", KeyManagement.DisplayCreatedAt(0, Now));
        Assert.Equal("(未知)", KeyManagement.DisplayCreatedAt(-1, Now));
    }

    [Fact]
    public void 悬停提示是完整时间含时区()
    {
        var text = KeyManagement.TooltipCreatedAt(Ago(TimeSpan.FromMinutes(5)));

        Assert.Contains("2026-10-07", text, StringComparison.Ordinal);
        Assert.Matches(@"\d{2}:\d{2}:\d{2}", text);
        // 带时区偏移 —— 跨时区排障时这是必要信息
        Assert.Matches(@"[+-]\d{2}:\d{2}$", text);
    }

    [Fact]
    public void 悬停提示在时间为零时也给话()
    {
        Assert.Equal("创建时间未知", KeyManagement.TooltipCreatedAt(0));
    }

    // ══ ⑤ 名称校验 ═══════════════════════════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空名称通过校验(string? name)
    {
        Assert.True(KeyManagement.ValidateName(name).Ok);
    }

    [Fact]
    public void 正常名称通过校验()
    {
        Assert.True(KeyManagement.ValidateName("我的笔记本").Ok);
        Assert.True(KeyManagement.ValidateName("CI - 公司").Ok);
    }

    [Fact]
    public void 名称过长被拒_且错误里带长度()
    {
        var name = new string('x', KeyManagement.MaxNameLength + 1);
        var check = KeyManagement.ValidateName(name);

        Assert.False(check.Ok);
        Assert.Contains(KeyManagement.MaxNameLength.ToString(), check.Error, StringComparison.Ordinal);
        Assert.Contains((KeyManagement.MaxNameLength + 1).ToString(), check.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 长度边界_正好上限通过()
    {
        var name = new string('x', KeyManagement.MaxNameLength);
        Assert.True(KeyManagement.ValidateName(name).Ok);
    }

    [Fact]
    public void 长度按裁掉空白之后算()
    {
        // "  " + 64 字符 + "  " → 裁完正好 64，应当通过
        var name = "  " + new string('x', KeyManagement.MaxNameLength) + "  ";
        Assert.True(KeyManagement.ValidateName(name).Ok);
    }

    [Fact]
    public void 换行与控制字符被拒()
    {
        // ⚠ 控制字符会破坏列表渲染与日志（换行尤其）
        Assert.False(KeyManagement.ValidateName("第一行\n第二行").Ok);
        Assert.False(KeyManagement.ValidateName("带\t制表符").Ok);
        Assert.False(KeyManagement.ValidateName("带\0空字符").Ok);

        Assert.Contains("控制字符", KeyManagement.ValidateName("a\nb").Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 普通中文与符号不受影响()
    {
        // 控制字符判定不能误伤正常字符
        Assert.True(KeyManagement.ValidateName("我的钥匙·备用（2）").Ok);
        Assert.True(KeyManagement.ValidateName("key@ci.example.com").Ok);
    }

    [Fact]
    public void NormalizeName_把空白归一成空串()
    {
        // 传 "" 与传 null 给 Issue 语义相同，但**显式归一**让调用点只有一种形状
        Assert.Equal("", KeyManagement.NormalizeName(null));
        Assert.Equal("", KeyManagement.NormalizeName("   "));
        Assert.Equal("名字", KeyManagement.NormalizeName("  名字  "));
    }

    // ══ ⑥ 一次性提示 ═════════════════════════════════════════════

    [Fact]
    public void 新建提示含明文与名称()
    {
        var notice = KeyManagement.NewKeyNotice("bdk-full-secret-here", "我的笔记本");

        Assert.Contains("bdk-full-secret-here", notice, StringComparison.Ordinal);
        Assert.Contains("我的笔记本", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void 新建提示必须说清只显示一次()
    {
        // ⚠ 措辞是功能的一部分。用户点掉对话框之后就再也找不回来了
        //    （结构性保证：ApiKeyRecord 里没有明文可存）。
        //    如果这句话藏在小字里，用户会在三天后才发现自己没存。
        var notice = KeyManagement.NewKeyNotice("bdk-x", "n");

        Assert.Contains("唯一一次", notice, StringComparison.Ordinal);
        Assert.Contains("复制", notice, StringComparison.Ordinal);
        Assert.Contains("无法再次查看", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void 未命名密钥的提示也用未命名文案()
    {
        var notice = KeyManagement.NewKeyNotice("bdk-x", "");
        Assert.Contains("(未命名)", notice, StringComparison.Ordinal);
    }

    // ══ ⑦ 失败文案 ═══════════════════════════════════════════════

    [Fact]
    public void 失败文案带操作名()
    {
        // 让用户知道**是哪一步**失败了 —— 列表刷不出来和创建失败处置不同
        Assert.Contains("创建失败", KeyManagement.FailureMessage("创建", null), StringComparison.Ordinal);
        Assert.Contains("撤销失败", KeyManagement.FailureMessage("撤销", null), StringComparison.Ordinal);
    }

    [Fact]
    public void _404_翻译成后端未接线()
    {
        // ⚠ 这是 t75 开工前核查出来的真实状态 —— 把它写成一句明确的话，
        //    比让用户看到 "not_found" 然后来问要好
        var text = KeyManagement.Detail("not_found");

        Assert.Contains("404", text, StringComparison.Ordinal);
        Assert.Contains("未接线", text, StringComparison.Ordinal);
    }

    [Fact]
    public void _401_翻译成鉴权失败并给处置()
    {
        var text = KeyManagement.Detail("unauthorized");

        Assert.Contains("鉴权失败", text, StringComparison.Ordinal);
        Assert.Contains("API Key", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 连接失败翻译成网关没运行()
    {
        var text = KeyManagement.Detail("connection refused");
        Assert.Contains("连不上网关", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 未识别的错误原样展示但截断()
    {
        var long400 = new string('x', 500);
        var text = KeyManagement.Detail(long400);

        Assert.True(text.Length < 300, $"应当截断，实际长度 {text.Length}");
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 空错误返回空串_不编造原因()
    {
        // 编一个"未知错误"不如什么都不说 —— 后者不会被误读
        Assert.Equal("", KeyManagement.Detail(null));
        Assert.Equal("", KeyManagement.Detail(""));
        Assert.Equal("", KeyManagement.Detail("   "));
    }

    [Fact]
    public void 失败文案里的空详情不产生多余冒号()
    {
        Assert.Equal("创建失败", KeyManagement.FailureMessage("创建", ""));
    }

    // ══ ⑧ 排序 ═══════════════════════════════════════════════════

    private sealed record Row(string Id, KeyManagement.KeyState State, long CreatedAt);

    private static IReadOnlyList<Row> SortRows(IEnumerable<Row> rows)
        => KeyManagement.Sort(rows, r => r.State, r => r.CreatedAt, r => r.Id);

    [Fact]
    public void 可用的排在已撤销的前面()
    {
        var rows = new[]
        {
            new Row("a", KeyManagement.KeyState.Revoked, Ago(TimeSpan.FromMinutes(1))),
            new Row("b", KeyManagement.KeyState.Active, Ago(TimeSpan.FromDays(9))),
        };

        // 即使已撤销的那个更新，可用的仍在前面 ——
        // 用户打开这页通常是想拿一个能用的 key
        Assert.Equal(["b", "a"], SortRows(rows).Select(r => r.Id));
    }

    [Fact]
    public void 同状态下新的在前()
    {
        var rows = new[]
        {
            new Row("old", KeyManagement.KeyState.Active, Ago(TimeSpan.FromDays(5))),
            new Row("new", KeyManagement.KeyState.Active, Ago(TimeSpan.FromHours(1))),
        };

        Assert.Equal(["new", "old"], SortRows(rows).Select(r => r.Id));
    }

    [Fact]
    public void 创建时间相同时有稳定决胜键()
    {
        // ⚠ 同一毫秒内建了两个（或上游只给到秒）时，不加决胜键的
        //    OrderBy 是**不稳定**的 —— 每次刷新顺序可能变，
        //    用户会觉得"列表在乱跳"。
        var same = Ago(TimeSpan.FromMinutes(5));
        var rows = new[]
        {
            new Row("c", KeyManagement.KeyState.Active, same),
            new Row("a", KeyManagement.KeyState.Active, same),
            new Row("b", KeyManagement.KeyState.Active, same),
        };

        // 多跑几次都该是同一个顺序
        var first = SortRows(rows).Select(r => r.Id).ToArray();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(first, SortRows(rows).Select(r => r.Id).ToArray());
        }

        Assert.Equal(["a", "b", "c"], first);   // 决胜键是 Id 序
    }

    [Fact]
    public void 排序不改动输入集合()
    {
        var rows = new List<Row>
        {
            new("a", KeyManagement.KeyState.Revoked, Ago(TimeSpan.FromMinutes(1))),
            new("b", KeyManagement.KeyState.Active, Ago(TimeSpan.FromMinutes(2))),
        };

        _ = SortRows(rows);

        Assert.Equal(["a", "b"], rows.Select(r => r.Id));   // 原顺序不变
    }

    [Fact]
    public void 空集合排序不抛()
    {
        Assert.Empty(SortRows([]));
    }

    // ══ ⑨ 摘要与空态 ═════════════════════════════════════════════

    [Fact]
    public void 摘要统计可用数()
    {
        var rows = new[]
        {
            new Row("a", KeyManagement.KeyState.Active, 1),
            new Row("b", KeyManagement.KeyState.Active, 2),
            new Row("c", KeyManagement.KeyState.Revoked, 3),
        };

        Assert.Equal("共 3 个，2 个可用", KeyManagement.Summary(rows, r => r.State));
    }

    [Fact]
    public void 全部可用时措辞不同()
    {
        var rows = new[]
        {
            new Row("a", KeyManagement.KeyState.Active, 1),
            new Row("b", KeyManagement.KeyState.Active, 2),
        };

        Assert.Equal("共 2 个，全部可用", KeyManagement.Summary(rows, r => r.State));
    }

    [Fact]
    public void 空列表摘要为空串()
    {
        // ⚠ 空列表时界面显示的是**空态**，不是"共 0 个"。
        //    两个都显示会重复，且空态更有指导性。
        Assert.Equal("", KeyManagement.Summary(Array.Empty<Row>(), r => r.State));
    }

    [Fact]
    public void 空态提示有指导性()
    {
        // 空态要说"下一步做什么"，不是一句"暂无数据"
        Assert.Contains("创建", KeyManagement.EmptyHint, StringComparison.Ordinal);
        Assert.Contains("明文 token", KeyManagement.EmptyHint, StringComparison.Ordinal);
    }

    // ══ ⑩ 撤销确认 ═══════════════════════════════════════════════

    [Fact]
    public void 撤销确认说清不可恢复与立即失效()
    {
        var text = KeyManagement.RevokeConfirm("我的笔记本");

        Assert.Contains("我的笔记本", text, StringComparison.Ordinal);
        Assert.Contains("无法恢复", text, StringComparison.Ordinal);
        Assert.Contains("立即", text, StringComparison.Ordinal);
        Assert.Contains("401", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 撤销确认提醒多处配置的后果()
    {
        // ⚠ 用户可能在 CI / 另一台机器 / 脚本里都配了它，
        //    撤销之后他自己未必记得全都改了
        var text = KeyManagement.RevokeConfirm("k");

        Assert.Contains("CI", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 未命名密钥的撤销确认也用未命名()
    {
        Assert.Contains("(未命名)", KeyManagement.RevokeConfirm(null), StringComparison.Ordinal);
    }

    // ══ ⑪ 常量契约 ═══════════════════════════════════════════════

    [Fact]
    public void 前缀标记与_KeyStore_一致()
    {
        // KeyStore 生成的格式是 `bdk-` + 随机段。这个常量若漂了，
        // 校验/展示就会与真实密钥对不上。
        Assert.Equal("bdk-", KeyManagement.KeyPrefixMarker);
    }
}
