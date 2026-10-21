using BuddyDeck.App.Converters;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// <see cref="TimeText"/> 的测试。
///
/// ## 这一组测试为什么值钱
///
/// HANDOVER 坑 2：保险库里的时间字段**类型不一致** ——
/// <c>expiresAtMs</c> 是数字（Unix 毫秒），<c>addedAt</c> 是 ISO 字符串。
/// 上一版按数字读，读真实保险库直接抛 JsonException，
/// 而且**造数据永远发现不了**（造的全是数字）。
///
/// 所以这里对**两种输入**都给用例，边界覆盖 0 / 负数 / 超长 / null。
/// </summary>
public class TimeTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 真实保险库里的 <c>expiresAtMs</c> 值 —— 2026-11-03T18:39:24.678Z。
    /// （这是 HANDOVER 第 5 节里那个真实文件里的数字类型字段。）
    /// </summary>
    private const long RealExpiresAtMs = 1793731164678L;

    /// <summary>
    /// 真实保险库里的 <c>addedAt</c> 字符串值 —— 对应的 Unix 毫秒是 1790242512642。
    /// **同一个文件里另一个字段，用的是 ISO 字符串而不是数字**（HANDOVER 坑 2）。
    /// </summary>
    private const long RealAddedAtMs = 1790242512642L;

    private const string RealAddedAtIso = "2026-09-24T09:35:12.642Z";

    // ========================================================================
    //  两类输入都要吃（HANDOVER 坑 2 的核心）
    // ========================================================================

    [Fact]
    public void 数字和ISO字符串解析到同一时刻()
    {
        // 同一个时刻分别用两种形式表示 —— 解析结果必须完全一致。
        // 这正是坑 2 的修法：数字和 ISO 串各走各的分支，但要归一到同一个时刻。
        var fromNumber = TimeText.TryParse(RealAddedAtMs);
        var fromIso = TimeText.TryParse(RealAddedAtIso);

        Assert.NotNull(fromNumber);
        Assert.NotNull(fromIso);
        Assert.Equal(fromNumber!.Value, fromIso!.Value);
    }

    [Theory]
    [InlineData(1793731164678L)]              // long
    [InlineData("1793731164678")]             // 数字字符串（有些落盘实现这么写）
    [InlineData("2026-09-24T09:35:12.642Z")]  // ISO 带毫秒
    [InlineData("2026-09-24T09:35:12Z")]      // ISO 不带毫秒
    [InlineData("2026-09-24T09:35:12+00:00")] // ISO 带偏移
    public void 各种合法输入都能解析(object value)
    {
        Assert.NotNull(TimeText.TryParse(value));
    }

    [Fact]
    public void DateTimeOffset直接传进来原样返回()
    {
        var dto = new DateTimeOffset(2026, 10, 6, 18, 39, 24, TimeSpan.Zero);
        Assert.Equal(dto, TimeText.TryParse(dto));
    }

    [Fact]
    public void DateTime传进来能解析()
    {
        var dt = new DateTime(2026, 10, 6, 18, 39, 24, DateTimeKind.Utc);
        var parsed = TimeText.TryParse(dt);
        Assert.NotNull(parsed);
        Assert.Equal(dt, parsed!.Value.UtcDateTime);
    }

    [Fact]
    public void double类型的时间戳能解析()
    {
        var parsed = TimeText.TryParse((double)RealExpiresAtMs);
        Assert.NotNull(parsed);
        Assert.Equal(RealExpiresAtMs, parsed!.Value.ToUnixTimeMilliseconds());
    }

    // ========================================================================
    //  边界：0 / 负数（Core 里 <=0 的约定是「上游没给」）
    // ========================================================================

    [Fact]
    public void 零不当成1970年而是解析失败()
    {
        // 这是刻意的：Core 里 expiresAtMs<=0 表示「上游没给过期时间」。
        // 显示成 1970 会让用户以为号坏了。
        Assert.Null(TimeText.TryParse(0));
        Assert.Null(TimeText.TryParse(0L));
        Assert.Equal("—", TimeText.FormatTimestamp(0));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(-999999999999L)]
    [InlineData(long.MinValue)]
    public void 负数一律当没给(long value)
    {
        Assert.Null(TimeText.TryParse(value));
        Assert.Equal("—", TimeText.FormatTimestamp(value));
    }

    [Fact]
    public void 秒级时间戳被挡掉不当成毫秒()
    {
        // 1793731164 是「秒」——误传成毫秒会落在 1970-01-21。
        // 界面上显示 1970 年比显示「—」更糟：用户会以为程序坏了。
        Assert.Null(TimeText.TryParse(1793731164L));
    }

    [Fact]
    public void 超出DateTime范围的时间戳被挡掉()
    {
        // long.MaxValue 会让 DateTimeOffset.FromUnixTimeMilliseconds 抛异常。
        Assert.Null(TimeText.TryParse(long.MaxValue));
    }

    [Fact]
    public void 合理范围内的极值能解析()
    {
        Assert.NotNull(TimeText.TryParse(TimeText.MinPlausibleUnixMs));
        Assert.NotNull(TimeText.TryParse(TimeText.MaxPlausibleUnixMs));
    }

    // ========================================================================
    //  边界：null / 空 / 空白 / 超长
    // ========================================================================

    [Fact]
    public void null解析失败()
    {
        Assert.Null(TimeText.TryParse(null));
        Assert.Equal("—", TimeText.FormatTimestamp(null));
        Assert.Equal("—", TimeText.FormatTimestamp(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void 空白字符串解析失败(string value)
    {
        Assert.Null(TimeText.TryParse(value));
    }

    [Fact]
    public void 超长字符串被挡掉不当时间解析()
    {
        // 真实的错法：把 accessToken（常常几 KB）传成了时间字段。
        // 拿它去喂 DateTime.Parse 会白磨一遍。
        var huge = new string('9', 4096);
        Assert.Null(TimeText.TryParse(huge));
        Assert.Equal("—", TimeText.FormatTimestamp(huge));
    }

    [Fact]
    public void 刚超过长度上限的字符串被挡掉()
    {
        var justOver = "2" + new string('0', 64);
        Assert.Null(TimeText.TryParse(justOver));
    }

    [Fact]
    public void 不认识的类型解析失败不抛异常()
    {
        // 防御性：不猜类型，也不要让界面因为一个意外的类型崩掉。
        Assert.Null(TimeText.TryParse(true));
        Assert.Null(TimeText.TryParse(new object()));
        Assert.Null(TimeText.TryParse(new[] { 1, 2, 3 }));
    }

    [Fact]
    public void 乱码字符串解析失败不抛异常()
    {
        Assert.Null(TimeText.TryParse("这不是时间"));
        Assert.Null(TimeText.TryParse("2026-13-45T99:99:99Z"));
    }

    // ========================================================================
    //  格式化输出
    // ========================================================================

    [Fact]
    public void 时间戳格式化到分钟()
    {
        // 精确到分钟是刻意的 —— 秒级精度在「账号寿命」界面里没有信息量。
        var text = TimeText.FormatTimestamp(RealExpiresAtMs);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", text);
    }

    [Fact]
    public void ISO字符串同样能格式化()
    {
        // 同一个时刻的两种表示，格式化结果必须一样 ——
        // 否则界面上同一个号会因为它来自哪个字段而显示不同的时间。
        var fromIso = TimeText.FormatTimestamp(RealAddedAtIso);
        var fromNum = TimeText.FormatTimestamp(RealAddedAtMs);
        Assert.Equal(fromNum, fromIso);
    }

    [Fact]
    public void 日期格式只到日()
    {
        var text = TimeText.FormatDate(RealExpiresAtMs);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", text);
    }

    [Fact]
    public void 时钟格式精确到秒()
    {
        var text = TimeText.FormatClock(RealExpiresAtMs);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", text);
    }

    // ========================================================================
    //  相对时间
    // ========================================================================

    [Fact]
    public void 剩余时间按量级换粒度()
    {
        var inMinutes = Now.AddMinutes(30);
        var remaining = TimeText.FormatRemaining(inMinutes, Now);
        Assert.Contains("还剩", remaining);
        Assert.Contains("30 分钟", remaining);

        var inDays = Now.AddDays(12);
        Assert.Contains("12 天", TimeText.FormatRemaining(inDays, Now));

        var inHours = Now.AddHours(4);
        Assert.Contains("4 小时", TimeText.FormatRemaining(inHours, Now));
    }

    [Fact]
    public void 已过期的时间说已过期()
    {
        var past = Now.AddDays(-2);
        var text = TimeText.FormatRemaining(past, Now);
        Assert.Contains("已过期", text);
        Assert.Contains("2 天", text);
    }

    [Fact]
    public void 刚好过期不久说刚刚过期()
    {
        var justNow = Now.AddSeconds(-30);
        Assert.Equal("刚刚过期", TimeText.FormatRemaining(justNow, Now));
    }

    [Fact]
    public void 剩余时间对非法输入返回占位符()
    {
        Assert.Equal("—", TimeText.FormatRemaining(0, Now));
        Assert.Equal("—", TimeText.FormatRemaining(null, Now));
        Assert.Equal("—", TimeText.FormatRemaining("不是时间", Now));
    }

    // ========================================================================
    //  冷却措辞（和「剩余寿命」措辞分开）
    // ========================================================================

    [Fact]
    public void 冷却倒计时用分钟措辞()
    {
        var until = Now.AddMinutes(3);
        Assert.Equal("3 分钟后恢复", TimeText.FormatCooldown(until, Now));
    }

    [Fact]
    public void 冷却不足一分钟用秒措辞()
    {
        // 冷却只有几分钟，秒级在这里是有信息量的 ——
        // 看着它从 59 秒走到 58 秒是用户唯一的进度反馈。
        var until = Now.AddSeconds(45);
        var text = TimeText.FormatCooldown(until, Now);
        Assert.Contains("秒后恢复", text);
    }

    [Fact]
    public void 冷却已经结束说可以用了()
    {
        var past = Now.AddMinutes(-1);
        Assert.Equal("可以用了", TimeText.FormatCooldown(past, Now));
    }

    [Fact]
    public void 冷却超过一小时用小时措辞()
    {
        var until = Now.AddHours(2);
        var text = TimeText.FormatCooldown(until, Now);
        Assert.Contains("2 小时", text);
        Assert.Contains("后恢复", text);
    }

    [Fact]
    public void 冷却对非法输入返回占位符()
    {
        Assert.Equal("—", TimeText.FormatCooldown(0, Now));
        Assert.Equal("—", TimeText.FormatCooldown(null, Now));
    }

    // ========================================================================
    //  时间跨度格式化
    // ========================================================================

    [Theory]
    [InlineData(0, "不到 1 分钟")]
    [InlineData(30, "不到 1 分钟")]           // 30 秒
    [InlineData(300, "5 分钟")]               // 5 分钟
    [InlineData(7200, "2 小时")]              // 2 小时
    [InlineData(259200, "3 天")]              // 3 天
    [InlineData(5184000, "2 个月")]           // 60 天
    public void 时间跨度按量级换粒度(int seconds, string expected)
    {
        Assert.Equal(expected, TimeText.FormatSpan(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void 负的时间跨度按绝对值处理()
    {
        // 调用方不该传负数，但传了也不该输出「-5 分钟」这种东西。
        Assert.Equal("5 分钟", TimeText.FormatSpan(TimeSpan.FromMinutes(-5)));
    }
}
