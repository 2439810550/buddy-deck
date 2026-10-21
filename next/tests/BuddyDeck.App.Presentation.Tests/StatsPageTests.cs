using BuddyDeck.App.Services;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// 用量统计的纯逻辑（t72）：时间范围、聚合、格式化、空态。
///
/// ## 为什么能直接测
///
/// <see cref="StatsPresentation"/> 刻意不依赖 WPF，也**不依赖
/// `BuddyDeck.Core`** —— 它吃的是自己声明的窄接口
/// <see cref="StatsPresentation.ILedgerRow"/>（7 个成员）。
/// 于是这个测试项目不需要为它加任何 `ProjectReference`，
/// 保持了「不引用 App / 不引用 Core」的既有性质。
///
/// 真实实现那边（`UsageLedgerRecord` 是 record，属性名与类型逐一对上）
/// 在 `StatsViewModel` 里直接传即可 —— 见那个文件的说明。
///
/// ## 时间全部注入
///
/// <see cref="StatsPresentation.Aggregate"/> 的 `now` 是参数不是
/// `DateTimeOffset.Now`。否则「今天/本月」这类范围**没法测**
/// （测试会在月初/午夜随机红）。
/// </summary>
public class StatsPageTests
{
    // ══════════════════════════════════════════════════════════
    // 夹具
    // ══════════════════════════════════════════════════════════

    /// <summary>一条账目（只填聚合用得上的字段）。</summary>
    private sealed record Row(
        long Ts,
        string Outcome = "ok",
        long? PromptTokens = null,
        long? CompletionTokens = null,
        long? TotalTokens = null,
        double? Credit = null) : StatsPresentation.ILedgerRow;

    /// <summary>某个时刻的 Unix 毫秒。</summary>
    private static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    /// <summary>一个固定的「现在」—— 用固定值不用 Now，否则测试会随机红。</summary>
    private static readonly DateTimeOffset Now =
        new(2026, 10, 15, 14, 30, 0, TimeSpan.Zero);

    /// <summary>把「本地时间」换算成时刻（测试里统一用 +08:00 的直觉）。</summary>
    private static long AtLocal(int year, int month, int day, int hour = 12)
        => Ms(new DateTimeOffset(new DateTime(year, month, day, hour, 0, 0), TimeSpan.Zero));

    // ══════════════════════════════════════════════════════════
    // ① 时间范围：起点算得对不对
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 今天从本地零点算起()
    {
        // ⚠ 关键：不是 UTC 零点。用 UTC 的话东八区用户在早上 8 点前
        //   会看到「今天」是空的 —— 而那正是最常看的时候。
        var start = StatsPresentation.StartOf(StatsPresentation.Range.Today, Now);
        var local = start.ToLocalTime();

        Assert.Equal(0, local.Hour);
        Assert.Equal(0, local.Minute);
        Assert.Equal(Now.ToLocalTime().Date, local.Date);
    }

    [Fact]
    public void 近七天是滚动窗口()
    {
        var start = StatsPresentation.StartOf(StatsPresentation.Range.Last7Days, Now);

        Assert.Equal(Now - TimeSpan.FromDays(7), start);
    }

    [Fact]
    public void 近三十天是滚动窗口()
    {
        var start = StatsPresentation.StartOf(StatsPresentation.Range.Last30Days, Now);

        Assert.Equal(Now - TimeSpan.FromDays(30), start);
    }

    [Fact]
    public void 本月从本地一号零点算起()
    {
        // ★ 唯一的例外：「本月」按自然月，因为用户拿它和账单对账 ——
        //   滚动 30 天对不上。
        var start = StatsPresentation.StartOf(StatsPresentation.Range.ThisMonth, Now);
        var local = start.ToLocalTime();

        Assert.Equal(1, local.Day);
        Assert.Equal(0, local.Hour);
        Assert.Equal(Now.ToLocalTime().Month, local.Month);
        Assert.Equal(Now.ToLocalTime().Year, local.Year);
    }

    [Fact]
    public void 四个范围都有中文名且有顺序()
    {
        Assert.Equal(4, StatsPresentation.AllRanges.Count);
        Assert.Equal("今天", StatsPresentation.Label(StatsPresentation.Range.Today));
        Assert.Equal("近 7 天", StatsPresentation.Label(StatsPresentation.Range.Last7Days));
        Assert.Equal("近 30 天", StatsPresentation.Label(StatsPresentation.Range.Last30Days));
        Assert.Equal("本月", StatsPresentation.Label(StatsPresentation.Range.ThisMonth));
    }

    // ══════════════════════════════════════════════════════════
    // ② 聚合：三个维度
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 空账本聚合出全零()
    {
        var totals = StatsPresentation.Aggregate([], StatsPresentation.Range.Today, Now);

        Assert.True(totals.IsEmpty);
        Assert.Equal(0, totals.Requests);
        Assert.Equal(0, totals.TotalTokens);
        Assert.Equal(0, totals.Credit);
    }

    [Fact]
    public void 传_null_不崩()
    {
        var totals = StatsPresentation.Aggregate(null, StatsPresentation.Range.Today, Now);

        Assert.True(totals.IsEmpty);
    }

    [Fact]
    public void 三个维度各自求和()
    {
        var rows = new List<Row>
        {
            new(AtLocal(2026, 10, 15), "ok", 100, 50, 150, 0.5),
            new(AtLocal(2026, 10, 15), "ok", 200, 60, 260, 0.25),
            new(AtLocal(2026, 10, 15), "failed", null, null, null, null),
        };

        var totals = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);

        Assert.Equal(3, totals.Requests);        // 请求数（含失败的）
        Assert.Equal(2, totals.Ok);
        Assert.Equal(1, totals.Failed);
        Assert.Equal(300, totals.PromptTokens);  // 输入
        Assert.Equal(110, totals.CompletionTokens);
        Assert.Equal(410, totals.TotalTokens);
        Assert.Equal(0.75, totals.Credit, 6);     // 积分
    }

    [Fact]
    public void 失败与取消分开计数()
    {
        var rows = new List<Row>
        {
            new(AtLocal(2026, 10, 15), "ok"),
            new(AtLocal(2026, 10, 15), "failed"),
            new(AtLocal(2026, 10, 15), "failed"),
            new(AtLocal(2026, 10, 15), "cancelled"),
        };

        var totals = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);

        Assert.Equal(1, totals.Ok);
        Assert.Equal(2, totals.Failed);
        Assert.Equal(1, totals.Cancelled);
        Assert.Equal(4, totals.Requests);
    }

    [Fact]
    public void 未知_outcome_计入总数但不计入任何一档()
    {
        // 丢掉它会让「请求数」和账本行数对不上 —— 用户看到 3 条记录
        // 却只有 2 个请求
        var rows = new List<Row>
        {
            new(AtLocal(2026, 10, 15), "ok"),
            new(AtLocal(2026, 10, 15), "something-new"),
        };

        var totals = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);

        Assert.Equal(2, totals.Requests);
        Assert.Equal(1, totals.Ok);
        Assert.Equal(0, totals.Failed);
        Assert.Equal(0, totals.Cancelled);
    }

    [Fact]
    public void 缺少_token_的被计数_不当成零()
    {
        // ★ 「上游没给 usage」与「用了 0 个 token」不是一回事。
        //   静默当 0 会让合计偏低且**看不出来**。
        var rows = new List<Row>
        {
            new(AtLocal(2026, 10, 15), "ok", 100, 50, 150),
            new(AtLocal(2026, 10, 15), "failed"),           // 没有 token 字段
        };

        var totals = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);

        Assert.Equal(1, totals.RowsMissingTokens);
        Assert.Equal(150, totals.TotalTokens);   // 不是 150 + 0 而是只算有的那条
    }

    [Fact]
    public void 没有_total_时用输入输出补()
    {
        var rows = new List<Row>
        {
            new(AtLocal(2026, 10, 15), "ok", 100, 50),   // 只有 prompt/completion
        };

        var totals = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);

        Assert.Equal(150, totals.TotalTokens);
    }

    [Fact]
    public void 有_total_时不被重复累加()
    {
        // ★ 反证：如果实现是「total = prompt + completion」无条件覆盖，
        //   或者既累加 total 又补一次，这里会翻倍。
        var rows = new List<Row>
        {
            new(AtLocal(2026, 10, 15), "ok", 100, 50, 999),
        };

        var totals = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);

        Assert.Equal(999, totals.TotalTokens);
    }

    [Fact]
    public void 成功率在没有请求时是_null_不是零()
    {
        // ★ 0% 的意思是「全都失败了」，null 的意思是「什么都没发生」。
        //   空账本上显示 0% 会让用户以为网关全挂了。
        var totals = StatsPresentation.Aggregate([], StatsPresentation.Range.Today, Now);

        Assert.Null(totals.SuccessRate);
        Assert.Equal("—", StatsPresentation.PercentText(totals.SuccessRate));
    }

    [Fact]
    public void 成功率算得对()
    {
        var rows = new List<Row>
        {
            new(AtLocal(2026, 10, 15), "ok"),
            new(AtLocal(2026, 10, 15), "ok"),
            new(AtLocal(2026, 10, 15), "ok"),
            new(AtLocal(2026, 10, 15), "failed"),
        };

        var totals = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);

        Assert.Equal(75.0, totals.SuccessRate!.Value, 6);
        Assert.Equal("75%", StatsPresentation.PercentText(totals.SuccessRate));
    }

    // ══════════════════════════════════════════════════════════
    // ③ 时间过滤的边界
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 范围外的记录被排除()
    {
        var rows = new List<Row>
        {
            new(AtLocal(2026, 10, 15), "ok", 100, 0, 100),
            new(AtLocal(2026, 9, 1), "ok", 999, 0, 999),    // 一个月前
        };

        var today = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);

        Assert.Equal(1, today.Requests);
        Assert.Equal(100, today.TotalTokens);
    }

    [Fact]
    public void 起点那一刻的记录被包含()
    {
        // ★ 边界方向：`>=` 而不是 `>`。写成 `>` 的话，恰好落在
        //   零点那一刻的请求会消失 —— 而那正是跨天时最容易被注意到的。
        var start = StatsPresentation.StartOf(StatsPresentation.Range.Today, Now);
        var rows = new List<Row> { new(Ms(start), "ok", 1, 0, 1) };

        var totals = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);

        Assert.Equal(1, totals.Requests);
    }

    [Fact]
    public void 未来时间的记录也计入()
    {
        // 时钟偏差（网关与界面不同机）不该让记录消失 ——
        // 用户会看到「明明有请求但统计里没有」
        var future = Ms(Now + TimeSpan.FromHours(3));
        var rows = new List<Row> { new(future, "ok", 10, 0, 10) };

        var totals = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);

        Assert.Equal(1, totals.Requests);
    }

    [Fact]
    public void 近七天包含三天前的记录()
    {
        var threeDaysAgo = Ms(Now - TimeSpan.FromDays(3));
        var rows = new List<Row> { new(threeDaysAgo, "ok") };

        Assert.Equal(1, StatsPresentation.Aggregate(
            rows, StatsPresentation.Range.Last7Days, Now).Requests);

        // 但「今天」不含它
        Assert.Equal(0, StatsPresentation.Aggregate(
            rows, StatsPresentation.Range.Today, Now).Requests);
    }

    // ══════════════════════════════════════════════════════════
    // ④ 格式化
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 小数字原样显示()
    {
        // 1 万以下时精确值是有意义的（比如 1200 个 token）
        Assert.Equal("0", StatsPresentation.FormatTokens(0));
        Assert.Equal("1,200", StatsPresentation.FormatTokens(1200));
        Assert.Equal("9,999", StatsPresentation.FormatTokens(9999));
    }

    [Fact]
    public void 上万用万表示()
    {
        Assert.Equal("1 万", StatsPresentation.FormatTokens(10_000));
        Assert.Equal("1.2 万", StatsPresentation.FormatTokens(12_345));
        Assert.Equal("128.5 万", StatsPresentation.FormatTokens(1_284_932));
    }

    [Fact]
    public void 上亿用亿表示()
    {
        Assert.Equal("1 亿", StatsPresentation.FormatTokens(100_000_000));
        Assert.Equal("2.5 亿", StatsPresentation.FormatTokens(250_000_000));
    }

    [Fact]
    public void 负数不崩()
    {
        // 账本里不该出现负数，但真出现了也要能显示而不是抛
        Assert.StartsWith("-", StatsPresentation.FormatTokens(-100), StringComparison.Ordinal);
        Assert.StartsWith("-", StatsPresentation.FormatCredit(-1.5), StringComparison.Ordinal);
    }

    [Fact]
    public void 积分保留有效小数()
    {
        // 上游 credit 是 0.22 / 0.03 这种，**小数位不能丢**（对账就靠它）
        Assert.Equal("0.22", StatsPresentation.FormatCredit(0.22));
        Assert.Equal("0.03", StatsPresentation.FormatCredit(0.03));
        Assert.Equal("1.5", StatsPresentation.FormatCredit(1.5));
        Assert.Equal("100", StatsPresentation.FormatCredit(100));
    }

    [Fact]
    public void 积分不显示浮点尾巴()
    {
        // 0.1 + 0.2 的经典尾巴不该出现在界面上
        Assert.Equal("0.3", StatsPresentation.FormatCredit(0.1 + 0.2));
    }

    [Fact]
    public void 非数积分显示占位符()
    {
        Assert.Equal("—", StatsPresentation.FormatCredit(double.NaN));
        Assert.Equal("—", StatsPresentation.FormatCredit(double.PositiveInfinity));
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0.0, "0%")]
    [InlineData(12.5, "12.5%")]
    [InlineData(100.0, "100%")]
    public void 百分比文案(double? percent, string expected)
    {
        Assert.Equal(expected, StatsPresentation.PercentText(percent));
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ 空态
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 从没发过请求的空态要说怎么办()
    {
        // ★ 「暂无数据」是最没用的一种空态 —— 它没说为什么、也没说怎么办。
        var hint = StatsPresentation.EmptyHint(StatsPresentation.Range.Today, hasAnyRecords: false);

        Assert.Contains("聊天测试", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("暂无数据", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void 这段范围没数据_与从没数据_文案不同()
    {
        // 两种空态对用户是完全不同的信息：
        //   一个是他该去发请求，一个是数据在更早的范围里
        var never = StatsPresentation.EmptyHint(StatsPresentation.Range.Today, hasAnyRecords: false);
        var notInRange = StatsPresentation.EmptyHint(StatsPresentation.Range.Today, hasAnyRecords: true);

        Assert.NotEqual(never, notInRange);
        Assert.Contains("换一个时间范围", notInRange, StringComparison.Ordinal);
    }

    [Fact]
    public void 范围说明里带起始时刻()
    {
        // 用户看到「今天 0 个请求」第一反应是「坏了」，
        // 所以要说清这个「今天」从几点算起
        var caption = StatsPresentation.RangeCaption(StatsPresentation.Range.Today, Now);

        Assert.Contains("今天", caption, StringComparison.Ordinal);
        Assert.Contains("00:00", caption, StringComparison.Ordinal);
    }

    [Fact]
    public void 每个范围都有范围说明()
    {
        foreach (var range in StatsPresentation.AllRanges)
        {
            var caption = StatsPresentation.RangeCaption(range, Now);

            Assert.False(string.IsNullOrWhiteSpace(caption), $"{range} 缺范围说明");
            Assert.Contains(StatsPresentation.Label(range), caption, StringComparison.Ordinal);
        }
    }

    // ══════════════════════════════════════════════════════════
    // ⑥ 不变量
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 各档之和等于总数()
    {
        // ★ 防「有记录在分档时凭空消失」—— 那是最难查的一类：
        //   用户说「我有 10 个请求，只统计到 8 个」，
        //   而没有这条断言的话我们连该有几个都不知道。
        var rows = new List<Row>
        {
            new(AtLocal(2026, 10, 15), "ok"),
            new(AtLocal(2026, 10, 15), "ok"),
            new(AtLocal(2026, 10, 15), "failed"),
            new(AtLocal(2026, 10, 15), "cancelled"),
            new(AtLocal(2026, 10, 15), "unknown-kind"),
        };

        var totals = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);

        Assert.Equal(totals.Requests, totals.Ok + totals.Failed + totals.Cancelled + 1);
    }

    [Fact]
    public void 聚合对同一批数据是确定性的()
    {
        var rows = new List<Row>
        {
            new(AtLocal(2026, 10, 15), "ok", 100, 50, 150, 0.5),
            new(AtLocal(2026, 10, 15), "failed"),
            new(AtLocal(2026, 10, 14), "ok", 200, 60, 260, 0.25),
        };

        var a = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);
        var b = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);

        Assert.Equal(a, b);
    }

    [Fact]
    public void 顺序不影响结果()
    {
        var rows = new List<Row>
        {
            new(AtLocal(2026, 10, 15), "ok", 100, 50, 150, 0.5),
            new(AtLocal(2026, 10, 15), "ok", 200, 60, 260, 0.25),
        };

        var forward = StatsPresentation.Aggregate(rows, StatsPresentation.Range.Today, Now);
        var reversed = StatsPresentation.Aggregate(rows.AsEnumerable().Reverse().ToList(),
            StatsPresentation.Range.Today, Now);

        Assert.Equal(forward.Requests, reversed.Requests);
        Assert.Equal(forward.TotalTokens, reversed.TotalTokens);
        Assert.Equal(forward.Credit, reversed.Credit, 6);
    }
}
