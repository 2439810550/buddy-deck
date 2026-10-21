using BuddyDeck.App.Services;
using Xunit;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// `action_button.Action` 的 scheme 白名单守卫（t52，安全）。
///
/// ## 为什么这个测试值得存在
///
/// `CheckinActionButton.Action` 是一个**来自上游网络的 URL**
/// （t50 实测真机值 `workbuddy://home?openBuddySwitcher=true`）。
/// 界面若直接把它交给 `Process.Start` / `ShellExecute`，
/// **上游改一个字符串就能触发用户机器上的任意协议处理器**。
///
/// 当前值无害，但**「当前值无害」不是安全论证** —— 那个值由上游决定，
/// 明天就能变。安全属性必须由使用侧保证。
///
/// 所以这组测试锁的不是"现在的行为对不对"，而是
/// **"将来有人接线时，白名单还在不在"**。
/// </summary>
public class UrlSchemeGuardTests
{
    // ===================== 放行：唯一允许的 scheme =====================

    [Theory]
    [InlineData("workbuddy://home?openBuddySwitcher=true")]   // t50 的真机值
    [InlineData("workbuddy://home")]
    [InlineData("workbuddy:home")]                            // 无 // 的形式也是合法 URI
    [InlineData("WORKBUDDY://HOME")]                          // scheme 大小写不敏感（RFC 3986）
    [InlineData("WorkBuddy://x")]
    public void 放行_workbuddy_scheme(string url)
    {
        Assert.True(AccountPresentation.IsSafeActionUrl(url), $"应当放行：{url}");
    }

    // ===================== 拒绝：外部 scheme =====================

    [Theory]
    // 任务书点名的用例
    [InlineData("https://evil/")]
    [InlineData("http://evil/")]
    // 真正危险的本地/系统协议 —— 这些才是攻击者想要的
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("ms-windows-store://home")]
    [InlineData("smb://attacker/share")]
    [InlineData("mailto:victim@example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("ftp://attacker/")]
    [InlineData("tel:+123456")]
    [InlineData("search-ms:query=x")]
    // 近似冒充：前缀像但不是同一个 scheme
    [InlineData("workbuddy-evil://x")]
    [InlineData("notworkbuddy://x")]
    [InlineData("workbuddyx://x")]
    public void 拒绝_非白名单_scheme(string url)
    {
        Assert.False(AccountPresentation.IsSafeActionUrl(url), $"应当拒绝：{url}");
    }

    // ===================== 拒绝：畸形输入 =====================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("workbuddy")]              // 只有 scheme 名、没有冒号 → 相对路径
    [InlineData("foo/bar")]                // 相对路径
    [InlineData("./relative")]
    [InlineData("\\\\server\\share")]      // UNC 路径
    [InlineData("C:\\Windows\\notepad.exe")]
    public void 拒绝_畸形或非绝对输入(string? url)
    {
        Assert.False(AccountPresentation.IsSafeActionUrl(url), $"应当拒绝：{url ?? "(null)"}");
    }

    // ===================== 关键性质：前缀匹配不能放过近似值 =====================

    /// <summary>
    /// ⚠ 这条是本组最重要的一条。
    ///
    /// 用 `StartsWith("workbuddy")` 实现白名单会**放过** `workbuddy-evil://`。
    /// 那种写法看起来对、跑起来也"能用"，但它把 boundary 判错了。
    /// 这里用一组构造出来的近似值把它钉死。
    /// </summary>
    [Theory]
    [InlineData("workbuddyx://evil")]        // 多一个字符
    [InlineData("workbuddy.evil://x")]       // 用点续
    [InlineData("workbuddy-evil://x")]       // 用连字符续
    [InlineData("xworkbuddy://x")]           // 前面加东西
    [InlineData(" workbuddy://x")]           // 前导空格（Trim 后应当放行，见下条测试）
    public void 拒绝_用前缀冒充的近似_scheme(string url)
    {
        // 注意最后一条：前导空格会被 Trim 掉，所以它**应当放行**。
        // 这里单独处理，避免把"该放行的"和"该拒绝的"混在一个断言里。
        if (url == " workbuddy://x")
        {
            Assert.True(AccountPresentation.IsSafeActionUrl(url),
                "前后空白应当被容忍（上游偶尔带空格），Trim 后是合法 scheme");
            return;
        }

        Assert.False(AccountPresentation.IsSafeActionUrl(url),
            $"近似值必须拒绝（这正说明不能用 StartsWith 实现白名单）：{url}");
    }

    [Fact]
    public void 容忍前后空白()
    {
        // 上游偶尔会带空白 —— 那不该被当成"恶意"，Trim 掉照常判断。
        Assert.True(AccountPresentation.IsSafeActionUrl("  workbuddy://home  "));
    }

    // ===================== 拒绝时的说明文案 =====================

    [Fact]
    public void 拒绝说明不回显完整URL()
    {
        // 为什么这条重要：URL 可能带查询参数（`?token=…`）。
        // 日志里回显完整 URL 等于把上游给的东西原样落盘。
        // 只回 scheme，够定位，不泄露。
        var desc = AccountPresentation.DescribeRejectedAction(
            "https://evil.example.com/steal?token=SECRET_TOKEN_VALUE");

        Assert.Contains("https", desc);                                  // 说明是哪个 scheme
        Assert.DoesNotContain("SECRET_TOKEN_VALUE", desc);               // 不回显查询串
        Assert.DoesNotContain("evil.example.com", desc);                 // 不回显主机
    }

    [Fact]
    public void 拒绝说明对空与实际值分别可读()
    {
        Assert.NotEmpty(AccountPresentation.DescribeRejectedAction(null));
        Assert.NotEmpty(AccountPresentation.DescribeRejectedAction(""));
        Assert.Contains("scheme", AccountPresentation.DescribeRejectedAction("file:///x"));
    }
}
