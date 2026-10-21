using BuddyDeck.App.Controls;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// <see cref="QuotaMath"/> 的测试 —— 配额可视化的纯逻辑。
///
/// 配额条的**三态**（正常 / 冷却中 / 快用尽）落在这个类的推导上：
/// 前两个由百分比阈值决定，冷却是一个独立维度且**优先级更高**。
/// </summary>
public class QuotaMathTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    // ========================================================================
    //  阈值分档（10 / 30 / 60，抄参考项目被真实数据磨过的三条线）
    // ========================================================================

    [Theory]
    [InlineData(1.00, QuotaState.Normal)]
    [InlineData(0.95, QuotaState.Normal)]
    [InlineData(0.61, QuotaState.Normal)]
    [InlineData(0.60, QuotaState.Low)]        // 边界属于下一档
    [InlineData(0.45, QuotaState.Low)]
    [InlineData(0.31, QuotaState.Low)]
    [InlineData(0.30, QuotaState.Warning)]    // 边界
    [InlineData(0.15, QuotaState.Warning)]
    [InlineData(0.11, QuotaState.Warning)]
    [InlineData(0.10, QuotaState.Critical)]   // 边界
    [InlineData(0.05, QuotaState.Critical)]
    [InlineData(0.00, QuotaState.Critical)]
    public void 余量比例按10_30_60分档(double ratio, QuotaState expected)
    {
        Assert.Equal(expected, QuotaMath.FromRemainingRatio(ratio));
    }

    [Fact]
    public void 超出0到1的比例被钳制()
    {
        Assert.Equal(QuotaState.Normal, QuotaMath.FromRemainingRatio(5.0));
        Assert.Equal(QuotaState.Critical, QuotaMath.FromRemainingRatio(-1.0));
    }

    [Fact]
    public void NaN和无穷大不崩()
    {
        // 算不出来时退回 Normal（没有第五个状态可用）。
        // 关键是**不抛异常** —— 界面上一个 NaN 不该让整页渲染失败。
        Assert.Equal(QuotaState.Normal, QuotaMath.FromRemainingRatio(double.NaN));
        Assert.Equal(QuotaState.Normal, QuotaMath.FromRemainingRatio(double.PositiveInfinity));
    }

    // ========================================================================
    //  冷却优先级 —— 这一组是配额可视化的核心正确性
    // ========================================================================

    [Fact]
    public void 冷却状态压过百分比()
    {
        // ⚠ 这是最重要的一条：余量 95%（绿油油的正常态）但**正在冷却**，
        // 必须显示冷却。反过来会让用户点下去才发现用不了。
        var until = Now.AddMinutes(5);
        var state = QuotaMath.DeriveState(0.95, until, Now);

        Assert.Equal(QuotaState.Cooldown, state);
    }

    [Fact]
    public void 余量很低且冷却中时显示冷却()
    {
        // 两个条件都成立时，冷却仍然是更高优先级 ——
        // 因为「现在不能用」比「快没了」更紧急。
        var until = Now.AddMinutes(5);
        Assert.Equal(QuotaState.Cooldown, QuotaMath.DeriveState(0.02, until, Now));
    }

    [Fact]
    public void 冷却已过期就不算冷却()
    {
        var past = Now.AddMinutes(-1);
        Assert.Equal(QuotaState.Normal, QuotaMath.DeriveState(0.95, past, Now));
        Assert.False(QuotaMath.IsCooling(past, Now));
    }

    [Fact]
    public void 冷却时刻恰好等于现在算结束()
    {
        // 用的是 `until > now`，相等即已恢复。
        Assert.False(QuotaMath.IsCooling(Now, Now));
        Assert.Equal(QuotaState.Normal, QuotaMath.DeriveState(0.95, Now, Now));
    }

    [Fact]
    public void 没有冷却信息时按百分比推导()
    {
        Assert.Equal(QuotaState.Normal, QuotaMath.DeriveState(0.9, null, Now));
        Assert.Equal(QuotaState.Critical, QuotaMath.DeriveState(0.05, null, Now));
    }

    [Fact]
    public void 冷却时刻可以给ISO字符串()
    {
        // 统一走 TimeText，两种输入都吃。
        var iso = Now.AddMinutes(5).ToString("O");
        Assert.True(QuotaMath.IsCooling(iso, Now));
        Assert.Equal(QuotaState.Cooldown, QuotaMath.DeriveState(0.9, iso, Now));
    }

    [Fact]
    public void 冷却时刻给垃圾值不崩()
    {
        Assert.False(QuotaMath.IsCooling("不是时间", Now));
        Assert.Equal(QuotaState.Normal, QuotaMath.DeriveState(0.9, new object(), Now));
    }

    // ========================================================================
    //  余量比例计算：0 / 负数 / 超支 / 无总量
    // ========================================================================

    [Theory]
    [InlineData(0, 100, 1.0)]      // 没用过
    [InlineData(100, 100, 0.0)]    // 用完了
    [InlineData(50, 100, 0.5)]     // 一半
    [InlineData(25, 100, 0.75)]    // 还剩 3/4
    public void 正常情况算余量比例(double used, double total, double expected)
    {
        Assert.Equal(expected, QuotaMath.RemainingRatio(used, total), 3);
    }

    [Fact]
    public void 超支时余量是零不是负数()
    {
        // 上游偶尔会给 used > total。返回负数会让条的宽度算成负的。
        Assert.Equal(0.0, QuotaMath.RemainingRatio(150, 100));
        Assert.Equal(0.0, QuotaMath.RemainingRatio(1e9, 100));
    }

    [Fact]
    public void 总量为零或负数时不崩()
    {
        // 总量没给（上游有时这么干）：没用过 → 当成没消耗；
        // 已经用过了 → 当成用尽。
        Assert.Equal(1.0, QuotaMath.RemainingRatio(0, 0));
        Assert.Equal(0.0, QuotaMath.RemainingRatio(5, 0));
        Assert.Equal(1.0, QuotaMath.RemainingRatio(0, -1));
        Assert.Equal(0.0, QuotaMath.RemainingRatio(5, -1));
    }

    [Fact]
    public void 负数用量被处理()
    {
        // used 为负数（上游数据脏）：余量会超过 100%，钳到 1。
        Assert.Equal(1.0, QuotaMath.RemainingRatio(-10, 100));
    }

    [Fact]
    public void NaN输入不崩()
    {
        Assert.Equal(1.0, QuotaMath.RemainingRatio(double.NaN, 100));
        Assert.Equal(1.0, QuotaMath.RemainingRatio(10, double.NaN));
    }

    // ========================================================================
    //  条宽计算：0 / 负数 / 超长
    // ========================================================================

    [Theory]
    [InlineData(1.0, 200, 200.0)]
    [InlineData(0.5, 200, 100.0)]
    [InlineData(0.0, 200, 0.0)]
    [InlineData(1.0, 0, 0.0)]        // 轨道宽为 0
    [InlineData(1.0, -100, 0.0)]     // 负宽度
    public void 条宽按比例算且不为负(double ratio, double track, double expected)
    {
        Assert.Equal(expected, QuotaMath.BarWidth(ratio, track), 3);
    }

    [Fact]
    public void 条宽对超范围比例钳制()
    {
        Assert.Equal(200.0, QuotaMath.BarWidth(5.0, 200), 3);
        Assert.Equal(0.0, QuotaMath.BarWidth(-5.0, 200), 3);
    }

    [Fact]
    public void 条宽对NaN不崩()
    {
        Assert.Equal(0.0, QuotaMath.BarWidth(double.NaN, 200));
        Assert.Equal(0.0, QuotaMath.BarWidth(0.5, double.NaN));
    }

    [Fact]
    public void 超大轨道宽不溢出()
    {
        Assert.Equal(1e9, QuotaMath.BarWidth(1.0, 1e9), 3);
    }

    // ========================================================================
    //  比例文字
    // ========================================================================

    [Theory]
    [InlineData(1.0, "100%")]
    [InlineData(0.934, "93.4%")]
    [InlineData(0.5, "50%")]
    [InlineData(0.0, "0%")]
    public void 比例格式化(double ratio, string expected)
    {
        Assert.Equal(expected, QuotaMath.FormatRatio(ratio));
    }

    [Fact]
    public void 比例文字对非法输入给占位符()
    {
        Assert.Equal("—", QuotaMath.FormatRatio(double.NaN));
        Assert.Equal("—", QuotaMath.FormatRatio(double.PositiveInfinity));
    }

    [Fact]
    public void 比例文字钳制超范围()
    {
        Assert.Equal("100%", QuotaMath.FormatRatio(3.0));
        Assert.Equal("0%", QuotaMath.FormatRatio(-3.0));
    }
}
