using BuddyDeck.App.Converters;
using BuddyDeck.App.Controls;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// <see cref="StatusAppearance"/> 的测试 —— 状态到外观的映射。
///
/// ## 这组测试防的是什么
///
/// 映射表最容易出的错不是「颜色不好看」，是**把两种不同的处境
/// 映成同一个颜色**。BuddyDeck 里最要区分的是：
///
///   Expired（号废了，要你动手）  vs  Degraded（上游的问题，等一等）
///   → Stop 暗红                    → Fault 锈橙
///
///   Degraded（上游拒绝）         vs  冷却（正常调度）
///   → Fault 锈橙                   → Cool 蓝紫（**不是红**）
///
/// 所以下面的测试主要不是「等于某个字符串」，而是
/// **「这两个不能相等」** —— 那才是会真的伤到用户的回归。
/// </summary>
public class StatusAppearanceTests
{
    // ========================================================================
    //  一对一映射：每个状态都要有自己的位置
    // ========================================================================

    [Theory]
    [InlineData(AccountState.Healthy, "OkBrush")]
    [InlineData(AccountState.ExpiringSoon, "WarnBrush")]
    [InlineData(AccountState.Expired, "StopBrush")]
    [InlineData(AccountState.Degraded, "FaultBrush")]
    [InlineData(AccountState.Unknown, "IdleBrush")]
    public void 账号状态映射到文字色(AccountState state, string expected)
    {
        Assert.Equal(expected, StatusAppearance.BrushKey(state));
    }

    [Fact]
    public void 已过期和异常必须是两个不同的颜色()
    {
        // ⚠ 这是本文件最重要的一条断言。
        // 号废了（要换）和上游抽风（等一等）是两种完全不同的处置；
        // 做成同一个红，用户就分不清该动手还是该等。
        Assert.NotEqual(
            StatusAppearance.BrushKey(AccountState.Expired),
            StatusAppearance.BrushKey(AccountState.Degraded));
    }

    [Fact]
    public void 冷却不是红色()
    {
        // 冷却是池子正常的调度行为（上游按模型限流是家常便饭）。
        // 给它故障色会让用户以为天天出事故。
        var coolKeys = new[]
        {
            StatusAppearance.QuotaBrushKey(QuotaState.Cooldown),
            StatusAppearance.QuotaTextKey(QuotaState.Cooldown),
        };

        foreach (var key in coolKeys)
        {
            Assert.NotEqual("StopBrush", key);
            Assert.NotEqual("FaultBrush", key);
        }
    }

    [Fact]
    public void 即将过期复用强调色是刻意的()
    {
        // 琥珀本来就是「该看一眼」的意思。这里的复用不是偷懒。
        Assert.Equal("WarnBrush", StatusAppearance.BrushKey(AccountState.ExpiringSoon));

        // 竖条用的是另一组 key（压过不透明度，0.85），
        // 但**色相必须还是琥珀** —— 名字不同，颜色是同一个。
        Assert.Equal("WarnRailBrush", StatusAppearance.RailKey(AccountState.ExpiringSoon));
    }

    [Fact]
    public void 竖条色和文字色同色相只是名字不同()
    {
        // 竖条用压过不透明度的另一组 key（见 Colors.xaml 的 *RailBrush）。
        // 不变式：RailKey 去掉 "Rail" 之后必须等于 BrushKey ——
        // 也就是说两者色相相同，只是不透明度不同。
        // 如果哪天有人给竖条换了个色相，这条会红 —— 那正是要防的。
        var pairs = new (AccountState State, string Brush, string Rail)[]
        {
            (AccountState.Healthy, "OkBrush", "OkRailBrush"),
            (AccountState.ExpiringSoon, "WarnBrush", "WarnRailBrush"),
            (AccountState.Expired, "StopBrush", "StopRailBrush"),
            (AccountState.Degraded, "FaultBrush", "FaultRailBrush"),
            (AccountState.Unknown, "IdleBrush", "IdleRailBrush"),
        };

        foreach (var (state, brush, rail) in pairs)
        {
            Assert.Equal(brush, StatusAppearance.BrushKey(state));
            Assert.Equal(rail, StatusAppearance.RailKey(state));
            Assert.Equal(brush.Replace("Brush", "RailBrush", StringComparison.Ordinal), rail);
        }
    }

    [Fact]
    public void 每个账号状态都有竖条色且各不相同()
    {
        var states = new[]
        {
            AccountState.Healthy,
            AccountState.ExpiringSoon,
            AccountState.Expired,
            AccountState.Degraded,
        };

        var rails = states.Select(StatusAppearance.RailKey).ToList();

        Assert.All(rails, r => Assert.False(string.IsNullOrWhiteSpace(r)));
        Assert.Equal(rails.Count, rails.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 每个账号状态都有徽章底色()
    {
        foreach (var state in Enum.GetValues<AccountState>())
        {
            var key = StatusAppearance.BadgeBackgroundKey(state);
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.EndsWith("BadgeBackgroundBrush", key, StringComparison.Ordinal);
        }
    }

    // ========================================================================
    //  用量条状态
    // ========================================================================

    [Fact]
    public void 用量条状态映射到填充色()
    {
        Assert.Equal("OkBrush", StatusAppearance.QuotaBrushKey(QuotaState.Normal));
        Assert.Equal("WarnBrush", StatusAppearance.QuotaBrushKey(QuotaState.Warning));
        Assert.Equal("StopBrush", StatusAppearance.QuotaBrushKey(QuotaState.Critical));
        Assert.Equal("CoolBrush", StatusAppearance.QuotaBrushKey(QuotaState.Cooldown));
    }

    [Fact]
    public void 充裕和开始消耗用不同颜色()
    {
        // Low 用蓝紫而不是黄 —— 「开始消耗了」还不需要提醒。
        Assert.NotEqual(
            StatusAppearance.QuotaBrushKey(QuotaState.Normal),
            StatusAppearance.QuotaBrushKey(QuotaState.Low));
    }

    [Fact]
    public void 临界和快用尽用不同颜色()
    {
        Assert.NotEqual(
            StatusAppearance.QuotaBrushKey(QuotaState.Warning),
            StatusAppearance.QuotaBrushKey(QuotaState.Critical));
    }

    [Fact]
    public void 正常态的文字是次要色不抢注意力()
    {
        Assert.Equal("TextSecondaryBrush", StatusAppearance.QuotaTextKey(QuotaState.Normal));
    }

    [Fact]
    public void 预警态的文字是有色的()
    {
        Assert.Equal("WarnBrush", StatusAppearance.QuotaTextKey(QuotaState.Warning));
        Assert.Equal("StopBrush", StatusAppearance.QuotaTextKey(QuotaState.Critical));
    }

    [Fact]
    public void 每个用量条状态都有对应的两个key()
    {
        foreach (var state in Enum.GetValues<QuotaState>())
        {
            Assert.False(string.IsNullOrWhiteSpace(StatusAppearance.QuotaBrushKey(state)));
            Assert.False(string.IsNullOrWhiteSpace(StatusAppearance.QuotaTextKey(state)));
        }
    }

    // ========================================================================
    //  网关状态点
    // ========================================================================

    [Fact]
    public void 网关离线用中性灰不用红()
    {
        // 「网关没启动」不是错误 —— 用户关掉面板是正常操作。
        // 每次都亮红会让打开面板像出了事故。
        Assert.Equal("IdleBrush", StatusAppearance.GatewayDotKey(online: false));
        Assert.Equal("OkBrush", StatusAppearance.GatewayDotKey(online: true));
    }

    // ========================================================================
    //  词表：转发给 ui 的 AccountPresentation，不另写一份
    // ========================================================================

    [Fact]
    public void 徽章词表和ui的定义一致()
    {
        // 同一个状态在两个地方叫不同名字，是界面开始失控的第一个信号。
        // 这里断言的是「我确实转发了」，不是「某个字符串」——
        // 词表改词时只需要改 ui 那一个地方。
        foreach (var state in Enum.GetValues<AccountState>())
        {
            Assert.Equal(AccountPresentation.StateLabel(state), StatusAppearance.Label(state));
        }
    }

    [Fact]
    public void 每个状态都有非空的中文词()
    {
        foreach (var state in Enum.GetValues<AccountState>())
        {
            var label = StatusAppearance.Label(state);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.NotEqual("—", label);
        }
    }

    [Fact]
    public void 未知状态的说法不含糊()
    {
        // 禁用「未知错误」那类词。状态词要说人话。
        var label = StatusAppearance.Label(AccountState.Unknown);
        Assert.False(label.Contains("错误", StringComparison.Ordinal));
        Assert.False(label.Contains("失败", StringComparison.Ordinal));
    }
}
