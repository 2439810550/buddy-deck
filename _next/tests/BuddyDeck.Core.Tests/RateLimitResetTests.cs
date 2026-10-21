using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 限流文案重置时间解析的测试（t28）。
///
/// ## 期望值从哪来（重要）
///
/// **不是我自己推的**，而是用 python 独立算出来的 ——
/// 时区换算这类东西，"看起来对"和"真的对"差得很远，自己推容易把自己的
/// 错误理解当成标准答案。
///
/// ```python
/// datetime(2026,9,25,17,7,48, tzinfo=timezone(timedelta(hours=8))).timestamp()*1000
/// ```
///
/// 下面几条关键断言都挂在这个独立来源上。
/// </summary>
public class RateLimitResetTests
{
    // python 复算出来的期望值（见文件头说明）
    private const long Expected20260925_170748_Utc8 = 1790327268000;   // 17:07:48 UTC+8
    private const long Expected20260925_170748_UtcMinus5_30 = 1790375868000; // 17:07:48 -05:30

    // ══ 两种格式都认 ══════════════════════════════════════════

    [Fact]
    public void 解析_UTC加8_格式()
    {
        // 上游实际发的就是这种（ratelimit.js:10 的实测原文）
        var text = "您的使用量已超出频率限制，将在 2026-09-25 17:07:48 UTC+8 重置，您也可以切换其他模型继续使用。";

        Assert.Equal(Expected20260925_170748_Utc8, RateLimitReset.Parse(text));
    }

    [Fact]
    public void 解析_ISO_加偏移格式()
    {
        var text = "将在 2026-09-25T17:07:48+08:00 重置";

        Assert.Equal(Expected20260925_170748_Utc8, RateLimitReset.Parse(text));
    }

    [Fact]
    public void 两种格式表达同一时刻时结果相同()
    {
        // ★ 这是最有价值的一条：它验的是「两种写法真的等价」，
        //   而不是各自跟自己比。
        var utc8 = RateLimitReset.Parse("2026-09-25 17:07:48 UTC+8");
        var iso = RateLimitReset.Parse("2026-09-25T17:07:48+08:00");

        Assert.Equal(utc8, iso);
        Assert.Equal(Expected20260925_170748_Utc8, utc8);
    }

    [Fact]
    public void 解析_UTC无偏移视为零()
    {
        // "09:07:48 UTC" 与 "17:07:48 UTC+8" 是**同一时刻**
        var withOffset = RateLimitReset.Parse("2026-09-25 17:07:48 UTC+8");
        var plainUtc = RateLimitReset.Parse("2026-09-25 09:07:48 UTC");

        Assert.Equal(withOffset, plainUtc);
        Assert.Equal(Expected20260925_170748_Utc8, plainUtc);
    }

    [Fact]
    public void 解析_小时位只有一位数()
    {
        // ⚠ 上游实际发的是 `UTC+8` 而不是 `UTC+08` —— 正则必须认单位数小时。
        Assert.Equal(
            Expected20260925_170748_Utc8,
            RateLimitReset.Parse("2026-09-25 17:07:48 UTC+8"));
    }

    [Fact]
    public void 解析_负偏移带分钟_符号要整体带()
    {
        // ★ `-05:30` 是 -(5h30m)，不是 -5h + 30min。
        //   这个符号处理错了会差一小时 —— 而"差一小时"在只看一个用例时看不出来。
        Assert.Equal(
            Expected20260925_170748_UtcMinus5_30,
            RateLimitReset.Parse("2026-09-25 17:07:48-05:30"));
    }

    [Fact]
    public void 解析_ISO无冒号偏移也认()
    {
        Assert.Equal(
            Expected20260925_170748_Utc8,
            RateLimitReset.Parse("2026-09-25T17:07:48+0800"));
    }

    // ══ 容错：解析不出来要返回 null，不能抛也不能瞎猜 ═══════════

    [Fact]
    public void 解析不出来时返回_null()
    {
        // ⚠ 返回 null 的语义是「上游没说」→ 调用方退回默认冷却。
        //   不能返回 0（那等于"1970 年就恢复了"，冷却立即失效）。
        Assert.Null(RateLimitReset.Parse("您的使用量已超出频率限制"));
        Assert.Null(RateLimitReset.Parse(""));
        Assert.Null(RateLimitReset.Parse(null));
        Assert.Null(RateLimitReset.Parse("没有日期的文案"));
    }

    [Fact]
    public void 不合理的日期视为解析失败()
    {
        // 把冷却记到 3000 年的话，那个号就永远不再被用了 —— 比不解析更糟。
        Assert.Null(RateLimitReset.Parse("3000-09-25 17:07:48 UTC+8"));
        Assert.Null(RateLimitReset.Parse("2026-13-25 17:07:48 UTC+8"));   // 13 月
        Assert.Null(RateLimitReset.Parse("2026-09-25 25:07:48 UTC+8"));   // 25 点
    }

    [Fact]
    public void 解析整个_JSON_字符串也能工作()
    {
        // 实际拿到的常常是**整个 JSON**（不是抽好的 msg）——
        // 所以解析器必须能在原文里搜，不要求调用方先解析 JSON。
        var body = """
        {"code":6004,"msg":"您的使用量已超出频率限制，将在 2026-09-25 17:07:48 UTC+8 重置"}
        """;

        Assert.Equal(Expected20260925_170748_Utc8, RateLimitReset.Parse(body));
    }

    // ══ 是否算受限：不能只看状态码 ════════════════════════════

    [Fact]
    public void 限流判定_分类已是限流就算()
    {
        Assert.True(RateLimitReset.IsRateLimited(isRateLimitKind: true, status: 200, message: null));
    }

    [Fact]
    public void 限流判定_429_算()
    {
        Assert.True(RateLimitReset.IsRateLimited(false, 429, "too many"));
    }

    [Theory]
    [InlineData("""{"code":6004,"msg":"超出频率限制"}""")]
    [InlineData("""{"code": 6005}""")]
    [InlineData("""{"code" : 6006 }""")]
    public void 限流判定_业务码即使包在_400_里也算(string body)
    {
        // ★ 实测结论（ratelimit.js:76）：**服务端也会把限流包在 400 里发过来**，
        //   所以不能只看状态码。只看 429 的话，这类限流会被当成「请求有问题」
        //   直接返回给用户 —— 而它其实是该换模型的。
        Assert.True(RateLimitReset.IsRateLimited(false, 400, body));
    }

    [Fact]
    public void 限流判定_中文文案也算()
    {
        Assert.True(RateLimitReset.IsRateLimited(false, 400, "您的使用量已超出频率限制"));
        Assert.True(RateLimitReset.IsRateLimited(false, 400, "超出频率限制"));
    }

    [Fact]
    public void 限流判定_普通错误不算()
    {
        Assert.False(RateLimitReset.IsRateLimited(false, 400, """{"code":40001,"msg":"缺少 model"}"""));
        Assert.False(RateLimitReset.IsRateLimited(false, 500, "internal error"));
        Assert.False(RateLimitReset.IsRateLimited(false, 0, null));
    }

    [Fact]
    public void 限流判定_不会把随意出现的_6004_误判()
    {
        // `code` 关键字后面必须**跟着**那个码；正文里随便出现的数字不算。
        Assert.False(RateLimitReset.IsRateLimited(false, 400, """{"msg":"请求了 6004 个 token"}"""));
    }

    // ══ 从信封里抽可读消息 ════════════════════════════════════

    [Fact]
    public void 抽消息_优先_displayMsg()
    {
        // 业务错误的真实原因在 displayMsg 里，贴整个信封会把它埋掉。
        var body = """{"code":6004,"message":"通用消息","displayMsg":"将在 17:07 重置"}""";

        Assert.Equal("将在 17:07 重置", RateLimitReset.ExtractDisplayMessage(body));
    }

    [Fact]
    public void 抽消息_退回_message_再退回_msg()
    {
        Assert.Equal("m1", RateLimitReset.ExtractDisplayMessage("""{"message":"m1","msg":"m2"}"""));
        Assert.Equal("m2", RateLimitReset.ExtractDisplayMessage("""{"msg":"m2"}"""));
    }

    [Fact]
    public void 抽消息_非_JSON_正文直接用原文()
    {
        Assert.Equal("<html>502</html>", RateLimitReset.ExtractDisplayMessage("<html>502</html>"));
    }

    [Fact]
    public void 抽消息_超长时截断()
    {
        var huge = new string('x', 1000);
        var result = RateLimitReset.ExtractDisplayMessage(huge, max: 100);

        Assert.NotNull(result);
        Assert.True(result!.Length <= 101, $"实际 {result.Length}");
    }

    [Fact]
    public void 抽消息_空输入返回_null()
    {
        Assert.Null(RateLimitReset.ExtractDisplayMessage(null));
        Assert.Null(RateLimitReset.ExtractDisplayMessage("   "));
    }
}
