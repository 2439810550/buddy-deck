using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// M14 来源与优先级层的测试。
///
/// ## 这一层为什么值得单独测
///
/// 「只落显式改过的字段」这条需求，**全部实现就是几个布尔判断**，
/// 但它错了之后**几乎不可能被发现**：
///
/// ```
/// 命令行临时值被写进文件 → 下次启动读到上次的临时值
///   · 没有报错
///   · 没有日志
///   · 只是"端口怎么变成 8791 了"，而且用户已经忘了昨天传过什么
/// ```
///
/// 所以这里把每个来源的 `ShouldPersist` 单独钉死。
/// </summary>
public class OptionSourcesTests
{
    // ══ ① ShouldPersist：这就是"只落显式改过的字段"的全部实现 ══

    [Fact]
    public void 只有用户显式改过的值才落盘()
    {
        Assert.True(OptionSources.UserEdit(1).ShouldPersist);
        Assert.True(OptionSources.FromFile(1).ShouldPersist);
    }

    [Fact]
    public void 命令行与环境变量来源的值绝不落盘()
    {
        // ⚠⚠ 这是整个 M14 最关键的两条 ⚠⚠
        //
        // 反例（真会发生的）：
        //   第 1 天  dotnet run -- --port 8791   → 若落盘，文件里记下 8791
        //   第 2 天  dotnet run                   → 没传端口却读到 8791
        // 用户完全无法解释为什么。
        Assert.False(OptionSources.FromCommandLine(8791).ShouldPersist);
        Assert.False(OptionSources.FromEnvironment("8791").ShouldPersist);
    }

    [Fact]
    public void 默认值不落盘()
    {
        Assert.False(OptionSources.Default(8787).ShouldPersist);
    }

    // ══ ② 优先级链 ═══════════════════════════════════════════════

    [Fact]
    public void 命令行胜过环境变量()
    {
        var winner = OptionSources.Winner(
            OptionSources.FromCommandLine("cli"),
            OptionSources.FromEnvironment("env"));

        Assert.Equal("cli", winner!.Value);
        Assert.Equal(OptionOrigin.CommandLine, winner.Origin);
    }

    [Fact]
    public void 环境变量胜过文件()
    {
        var winner = OptionSources.Winner(
            OptionSources.FromEnvironment("env"),
            OptionSources.FromFile("file"));

        Assert.Equal("env", winner!.Value);
    }

    [Fact]
    public void 文件胜过默认值()
    {
        var winner = OptionSources.Winner(
            OptionSources.FromFile("file"),
            OptionSources.Default("default"));

        Assert.Equal("file", winner!.Value);
    }

    [Fact]
    public void 完整优先级链_命令行大于环境变量大于文件大于默认()
    {
        // 一次性把整条链钉死，顺序写在断言里
        var all = new[]
        {
            OptionSources.Default("d"),
            OptionSources.FromFile("f"),
            OptionSources.FromEnvironment("e"),
            OptionSources.FromCommandLine("c"),
        };

        Assert.Equal("c", OptionSources.Winner(all)!.Value);
        Assert.Equal("e", OptionSources.Winner(all[0], all[1], all[2])!.Value);
        Assert.Equal("f", OptionSources.Winner(all[0], all[1])!.Value);
        Assert.Equal("d", OptionSources.Winner(all[0])!.Value);
    }

    [Fact]
    public void 缺省的来源被跳过()
    {
        var winner = OptionSources.Winner(null, null, OptionSources.FromFile("file"), null);

        Assert.Equal("file", winner!.Value);
    }

    [Fact]
    public void 全为_null_时返回_null()
    {
        Assert.Null(OptionSources.Winner<int>(null, null));
        Assert.Null(OptionSources.Winner<int>());
    }

    [Fact]
    public void 同优先级取第一个()
    {
        // 调用方按「更具体 → 更宽泛」传，第一个赢
        var winner = OptionSources.Winner(
            OptionSources.FromFile("first"),
            OptionSources.FromFile("second"));

        Assert.Equal("first", winner!.Value);
    }

    // ══ ③ 枚举数值本身就是优先级 ═════════════════════════════════

    [Fact]
    public void 枚举顺序即优先级顺序()
    {
        // 这条钉的是"想插一层新来源时只要给个合适的数值，合并逻辑零改动"
        Assert.True(OptionOrigin.Default < OptionOrigin.File);
        Assert.True(OptionOrigin.File < OptionOrigin.Environment);
        Assert.True(OptionOrigin.Environment < OptionOrigin.CommandLine);
    }

    // ══ ④ 解析辅助 ═══════════════════════════════════════════════

    [Fact]
    public void 空串视为没给()
    {
        // --api-key "" 的语义是"我没提供"，不是"清空"
        Assert.Null(OptionSources.StringOrNull("", OptionOrigin.CommandLine));
        Assert.Null(OptionSources.StringOrNull(null, OptionOrigin.CommandLine));
        Assert.Equal("x", OptionSources.StringOrNull("x", OptionOrigin.CommandLine)!.Value);
    }

    [Fact]
    public void 整数解析失败视为没给_不抛()
    {
        Assert.Null(OptionSources.IntOrNull("abc", OptionOrigin.CommandLine));
        Assert.Null(OptionSources.IntOrNull("", OptionOrigin.CommandLine));
        Assert.Equal(3, OptionSources.IntOrNull("3", OptionOrigin.CommandLine)!.Value);
    }

    [Fact]
    public void 端口非法时抛异常_不静默回退()
    {
        // ⚠ 与 GatewayOptions.ParsePort 同一约定：
        // 悄悄回退到 8787 可能撞上用户正在跑的网关，比启动失败更糟。
        Assert.Throws<ArgumentException>(() => OptionSources.PortOrNull("0", OptionOrigin.CommandLine));
        Assert.Throws<ArgumentException>(() => OptionSources.PortOrNull("70000", OptionOrigin.CommandLine));
        Assert.Throws<ArgumentException>(() => OptionSources.PortOrNull("-1", OptionOrigin.CommandLine));
        Assert.Throws<ArgumentException>(() => OptionSources.PortOrNull("八七八七", OptionOrigin.CommandLine));

        Assert.Equal(8791, OptionSources.PortOrNull("8791", OptionOrigin.CommandLine)!.Value);
        Assert.Null(OptionSources.PortOrNull("", OptionOrigin.CommandLine));
    }

    [Fact]
    public void 端口边界值合法()
    {
        Assert.Equal(1, OptionSources.PortOrNull("1", OptionOrigin.CommandLine)!.Value);
        Assert.Equal(65535, OptionSources.PortOrNull("65535", OptionOrigin.CommandLine)!.Value);
        // 0 是特例：绑定 0 = 让系统挑端口，测试里常用 ——
        // 但本项目的既有约定是不接受 0（见 GatewayOptions.ParsePort），保持一致
        Assert.Throws<ArgumentException>(() => OptionSources.PortOrNull("0", OptionOrigin.CommandLine));
    }

    // ══ ⑤ Sourced 的形状 ═════════════════════════════════════════

    [Fact]
    public void 来源会显示在_ToString_里()
    {
        Assert.Contains("CommandLine", OptionSources.FromCommandLine(1).ToString(), StringComparison.Ordinal);
        Assert.Contains("File", OptionSources.FromFile(1).ToString(), StringComparison.Ordinal);
    }
}
