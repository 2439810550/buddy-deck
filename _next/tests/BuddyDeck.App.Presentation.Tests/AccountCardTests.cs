using BuddyDeck.App.Services;
using Xunit;

// CreditExpiryState 是 AccountPresentation 的**嵌套类型** —— 起个别名少写点。
using CreditExpiryState = BuddyDeck.App.Services.AccountPresentation.CreditExpiryState;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// 账号卡上那块积分信息的**纯逻辑**测试（t69）。
///
/// ## 对标来源
///
/// `workbuddy-switch` 的 `account-card.tsx`（51,943 B）—— 我把它的信息结构
/// 抄了过来（余额大字 / 积分包按到期升序 / 临期高亮 / 更新时间），
/// **但形态没抄**：它用卡片 + 五按钮并排，我们用行 + 1px 分隔线 + chip 组。
///
/// ## 这组测试守的是什么
///
/// 积分展示里有一堆**边界**是会静默出错的：
///   · 到期时间**解析不出来**时怎么办（不能猜成"最早到期"，那会制造假紧迫感）
///   · 已经过期的包算不算"临期"（算了告警就永远亮着）
///   · 读不到积分 ≠ 积分是 0（混起来会让用户以为积分没了）
///
/// 这些在界面上看起来都"正常"，只有断言能钉住。
/// </summary>
public class AccountCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    // ===================== 到期状态推导 =====================

    [Fact]
    public void 解析不出到期时间_是_Unknown_而不是_Expired()
    {
        // ⚠ 这条是关键：null 表示"上游没给 / 解析不出来"，
        // 不能当成"已过期"（那会让用户以为积分没了），
        // 也不能当成"最早到期"（那会制造假的紧迫感排序）。
        Assert.Equal(CreditExpiryState.Unknown, AccountPresentation.DeriveCreditExpiry(null, Now));
    }

    [Fact]
    public void 已经过期_是_Expired()
    {
        var past = Now.AddDays(-1);
        Assert.Equal(CreditExpiryState.Expired,
            AccountPresentation.DeriveCreditExpiry(Ms(past), Now));
    }

    [Fact]
    public void 恰好到期的那一刻_算过期()
    {
        // 边界：end == now。归到已过期（它确实不能用了）。
        Assert.Equal(CreditExpiryState.Expired,
            AccountPresentation.DeriveCreditExpiry(Ms(Now), Now));
    }

    [Theory]
    [InlineData(0.5)]   // 半天
    [InlineData(1)]     // 1 天
    [InlineData(3)]     // 正好 3 天（阈值边界，含）
    public void 阈值内_是_ExpiringSoon(double days)
    {
        Assert.Equal(CreditExpiryState.ExpiringSoon,
            AccountPresentation.DeriveCreditExpiry(Ms(Now.AddDays(days)), Now));
    }

    [Fact]
    public void 超过阈值_是_Fine()
    {
        Assert.Equal(CreditExpiryState.Fine,
            AccountPresentation.DeriveCreditExpiry(Ms(Now.AddDays(3.001)), Now));
        Assert.Equal(CreditExpiryState.Fine,
            AccountPresentation.DeriveCreditExpiry(Ms(Now.AddDays(30)), Now));
    }

    [Fact]
    public void 阈值是三天()
    {
        // 阈值本身是产品决策 —— 钉住它，免得有人顺手改成 7 天
        // （7 天的话告警会长期亮着，长期亮着的告警等于没有告警）
        Assert.Equal(3, AccountPresentation.CreditExpiringSoonDays);
    }

    // ===================== 状态词表 =====================

    [Theory]
    [InlineData(CreditExpiryState.Expired, "已过期")]
    [InlineData(CreditExpiryState.ExpiringSoon, "3 天内到期")]
    [InlineData(CreditExpiryState.Fine, "有效")]
    [InlineData(CreditExpiryState.Unknown, "无到期时间")]
    public void 状态词表(CreditExpiryState state, string expected)
    {
        Assert.Equal(expected, AccountPresentation.CreditStateLabel(state));
    }

    [Fact]
    public void Unknown_的词不说成异常()
    {
        // 「无到期时间」是事实陈述，不是故障 —— 不该出现"异常/错误"
        var label = AccountPresentation.CreditStateLabel(CreditExpiryState.Unknown);

        Assert.DoesNotContain("异常", label);
        Assert.DoesNotContain("错误", label);
        Assert.DoesNotContain("失败", label);
    }

    // ===================== 余额格式化 =====================

    [Theory]
    // ⚠ 这三条的期望值原先写错了（captain 按 ui 自己的版式设计核对后修正）：
    //   ui 在 t69 开工报告里的版式写的是「1,829.38」与「729.38」——
    //   说明它的意图是**千位以内照原样显示**，不是缩写成 "1.8k"。
    //   而 `< 10000` 走取整、"k" 只用在 ≥10000，与版式一致。
    //   （原期望 "1.8k"/"9.2k" 与注释「上万用 k」自相矛盾：1829 与 9169 都不到一万。）
    [InlineData(1829.38, "1829")]   // 千位：取整，不缩写
    [InlineData(9169.3, "9169")]    // 同上
    [InlineData(12345.6, "12.3k")]  // ← 真正的"上万用 k"
    [InlineData(999.5, "1000")]     // <10000 取整（且四舍五入：999.5 → 1000）
    [InlineData(386.4, "386")]
    [InlineData(50.25, "50.3")]     // <100 保留一位（四舍五入，不是银行家舍入）
    [InlineData(0, "0.0")]
    public void 余额格式化_小数位跟量级走(double value, string expected)
    {
        Assert.Equal(expected, AccountPresentation.FormatCredits(value));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 余额异常值不显示成数字(double bad)
    {
        Assert.Equal("—", AccountPresentation.FormatCredits(bad));
    }

    [Fact]
    public void 负余额显示为_0()
    {
        // 上游理论上不该给负数，但真给了也不能显示成 "-50"
        // （那看起来像欠费，会引起不必要的恐慌）
        Assert.Equal("0.0", AccountPresentation.FormatCredits(-50));
    }

    // ===================== 到期时间的人话 =====================

    [Fact]
    public void 无到期时间_说清楚而不是空白()
    {
        Assert.Equal("无到期时间", AccountPresentation.FormatCreditExpiry(null, Now));
    }

    [Fact]
    public void 已过期_说已过期()
    {
        Assert.Equal("已过期", AccountPresentation.FormatCreditExpiry(Ms(Now.AddHours(-1)), Now));
    }

    [Theory]
    [InlineData(0.5, "还剩 30 分钟")]
    [InlineData(5, "还剩 5 小时")]
    // ⚠ 原先写的是 [InlineData(2, "还剩 2 天")] —— 参数传的是 AddHours(2)（2 小时），
    //   却期望"还剩 2 天"，自相矛盾。按参数的真实语义（小时）修正为：
    [InlineData(2, "还剩 2 小时")]
    // 真正跨过 24 小时才说"天"：
    [InlineData(48, "还剩 2 天")]
    public void 临界时说还剩多久_比日期更有行动感(double hours, string expected)
    {
        Assert.Equal(expected, AccountPresentation.FormatCreditExpiry(Ms(Now.AddHours(hours)), Now));
    }

    [Fact]
    public void 还早就给日期_精确到分钟是噪音()
    {
        var far = Now.AddDays(20);
        var text = AccountPresentation.FormatCreditExpiry(Ms(far), Now);

        Assert.Contains("到期", text);
        Assert.DoesNotContain("还剩", text);   // 还早的不该有倒计时的紧迫感
    }

    // ===================== 总览页的风险总结 =====================

    [Fact]
    public void 没有风险_说明确没有()
    {
        var text = AccountPresentation.SummarizeCreditRisk(0, 0);

        Assert.Contains("没有账号", text);
        Assert.DoesNotContain("异常", text);
    }

    [Fact]
    public void 有风险_给出账号数与包数()
    {
        var text = AccountPresentation.SummarizeCreditRisk(2, 5);

        Assert.Contains("2 个账号", text);
        Assert.Contains("5 个包", text);
    }

    [Fact]
    public void 有账号但包数未知时_也可读()
    {
        // 包数拿不到（比如部分账号查失败）不该让整句话变成乱码
        var text = AccountPresentation.SummarizeCreditRisk(3, 0);

        Assert.Contains("3 个账号", text);
        Assert.NotEmpty(text);
    }

    // ===================== 卡上的 chip =====================

    [Fact]
    public void chip_临期时优先说积分()
    {
        // 账号好好的但积分临期 —— 这是最该被看到的情况，
        // 而它恰恰是「只看账号状态」会漏掉的。
        var chip = AccountPresentation.FormatAccountChip(
            AccountState.Healthy, CreditExpiryState.ExpiringSoon);

        Assert.Equal("积分临期", chip);
    }

    [Fact]
    public void chip_积分过期时也说积分()
    {
        var chip = AccountPresentation.FormatAccountChip(
            AccountState.Healthy, CreditExpiryState.Expired);

        Assert.Equal("积分已过期", chip);
    }

    [Fact]
    public void chip_积分没事时回退到账号状态()
    {
        // 积分正常 → chip 说账号怎么了（两个维度里"有事的那个"优先）
        var chip = AccountPresentation.FormatAccountChip(
            AccountState.Expired, CreditExpiryState.Fine);

        Assert.Equal(AccountPresentation.StateLabel(AccountState.Expired), chip);
    }

    [Fact]
    public void chip_积分状态未知时不盖掉账号状态()
    {
        // Unknown 表示"解析不出来"，不是"有问题" —— 别拿它盖住真实账号状态
        var chip = AccountPresentation.FormatAccountChip(
            AccountState.Degraded, CreditExpiryState.Unknown);

        Assert.Equal(AccountPresentation.StateLabel(AccountState.Degraded), chip);
    }
}
