using BuddyDeck.App.Services;
using Xunit;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// 换号重试体检指标的纯逻辑测试（t22）。
///
/// 为什么这些要单独立测：它们全是**会静默出错**的地方 ——
/// 除零、NaN、把健康态说成异常、把缺字段说成"零次尝试"。
/// 这些错编译期都不报，界面上看起来还挺正常，只有断言能钉住。
/// </summary>
public class AttemptMetricsTests
{
    // ===================== FormatAttemptRatio =====================

    [Fact]
    public void 零请求零尝试_返回空串而不是零比零()
    {
        // 为什么不是 "0 次 / 上游 0 次"：那既没信息量，
        // 又给下一个接手的人留了一个"可以拿来除一下"的诱因。
        Assert.Equal("", AccountPresentation.FormatAttemptRatio(0, 0));
    }

    [Fact]
    public void 零请求零尝试_描述走空态而不是报数()
    {
        Assert.Equal("还没有请求。", AccountPresentation.DescribeAttemptOverhead(0, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 137)]
    [InlineData(1, 1)]
    [InlineData(0, 5)]      // 病态：有尝试没请求
    [InlineData(100, 50)]   // 病态：尝试比请求还少（不该出现）
    [InlineData(long.MaxValue, long.MaxValue)]
    public void 比值串永不出现NaN或无穷(long total, long attempt)
    {
        var s = AccountPresentation.FormatAttemptRatio(total, attempt);

        Assert.DoesNotContain("NaN", s);
        Assert.DoesNotContain("∞", s);
        Assert.DoesNotContain("Infinity", s);
        Assert.DoesNotContain("-", s);   // 负数也不该冒出来
    }

    [Fact]
    public void 正常比值_格式可读()
    {
        Assert.Equal("100 次 / 上游 137 次", AccountPresentation.FormatAttemptRatio(100, 137));
    }

    // ===================== HasRetryOverhead =====================

    [Fact]
    public void 从未重试_不算有换号开销()
    {
        // 比值 = 1.0 是**健康态**，不能显示成异常。
        Assert.False(AccountPresentation.HasRetryOverhead(100, 100));
    }

    [Fact]
    public void 有重试才算有开销()
    {
        Assert.True(AccountPresentation.HasRetryOverhead(100, 137));
        Assert.True(AccountPresentation.HasRetryOverhead(100, 101));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 50)]   // 病态输入不得报成"有开销"
    public void 边界与病态输入不算有开销(long total, long attempt)
    {
        Assert.False(AccountPresentation.HasRetryOverhead(total, attempt));
    }

    // ===================== DescribeAttemptOverhead =====================

    [Fact]
    public void 从未重试的描述_是健康陈述不是报警()
    {
        var s = AccountPresentation.DescribeAttemptOverhead(100, 100);

        Assert.Contains("没有发生过换号重试", s);
        // 健康态不许出现任何警报词。
        Assert.DoesNotContain("异常", s);
        Assert.DoesNotContain("错误", s);
        Assert.DoesNotContain("故障", s);
    }

    [Theory]
    [InlineData(100, 101)]
    [InlineData(100, 137)]
    [InlineData(1, 3)]
    [InlineData(1000, 2000)]
    public void 有重试时的描述_不说成故障(long total, long attempt)
    {
        // 这是本组最重要的断言。
        //
        // 换号重试是**系统在正常工作** —— 一个号被限流，池子换一个顶上，
        // 用户照样拿到了答案。把它描述成"异常/故障/错误"会让用户以为坏了。
        // 但它也不能被藏起来：它是池子承压的早期信号。
        // 所以语气必须是「客观陈述 + 轻微提示」。
        var s = AccountPresentation.DescribeAttemptOverhead(total, attempt);

        Assert.DoesNotContain("异常", s);
        Assert.DoesNotContain("故障", s);
        Assert.DoesNotContain("错误", s);
        Assert.DoesNotContain("失败", s);
        Assert.NotEmpty(s);
    }

    [Fact]
    public void 单次重试_说法具体()
    {
        var s = AccountPresentation.DescribeAttemptOverhead(100, 101);
        Assert.Contains("1 次", s);
    }

    [Fact]
    public void 多次重试_给出平均倍数()
    {
        var s = AccountPresentation.DescribeAttemptOverhead(100, 137);

        // "平均每个请求打 1.4 次"比"多 37 次"更容易感知严重程度
        Assert.Contains("37", s);
        Assert.Contains("1.4", s);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(100, 50)]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    public void 病态输入不崩不除零(long total, long attempt)
    {
        var s = AccountPresentation.DescribeAttemptOverhead(total, attempt);

        Assert.NotEmpty(s);
        Assert.DoesNotContain("NaN", s);
        Assert.DoesNotContain("∞", s);
        Assert.DoesNotContain("Infinity", s);
    }

    // ===================== 耗时标签 =====================

    [Fact]
    public void 耗时标签_说明了是等待时间()
    {
        // t16 之后 AverageLatencyMs 含全部换号重试，是用户真实等待。
        // 原来写「平均耗时」会被读成"上游响应时间"。
        var label = AccountPresentation.AverageLatencyLabel;

        Assert.NotEqual("平均耗时", label);
        Assert.Contains("等待", label);
    }
}
