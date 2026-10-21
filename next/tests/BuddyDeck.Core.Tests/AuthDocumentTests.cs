using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 登录文件解析的测试。
///
/// 这块的输入来自**别人写的文件**（WorkBuddy App），形状变过好几次，
/// 所以测试重点在「各种历史形状都要认」和「坏数据不能崩」。
/// </summary>
public class AuthDocumentTests
{
    // ── 正常的两种形状 ────────────────────────────────────────

    [Fact]
    public void 嵌套形态_能解析()
    {
        const string json = """
        {
          "auth": { "accessToken": "tok-nested", "refreshToken": "ref-1", "expiresAt": 1790000000 },
          "account": { "uid": "u123", "nickname": "张三", "enterpriseId": "ent-9" }
        }
        """;

        var c = AuthDocument.Parse(json);

        Assert.NotNull(c);
        Assert.Equal("tok-nested", c.AccessToken);
        Assert.Equal("ref-1", c.RefreshToken);
        Assert.Equal("u123", c.Uid);
        Assert.Equal("张三", c.Label);
        Assert.Equal("ent-9", c.EnterpriseId);
    }

    [Fact]
    public void 扁平形态_能解析()
    {
        const string json = """
        { "accessToken": "tok-flat", "uid": "u9", "nickname": "李四" }
        """;

        var c = AuthDocument.Parse(json);

        Assert.NotNull(c);
        Assert.Equal("tok-flat", c.AccessToken);
        Assert.Equal("u9", c.Uid);
        Assert.Equal("李四", c.Label);
        Assert.Null(c.EnterpriseId);
    }

    // ── 边界：不该崩 ──────────────────────────────────────────

    [Fact]
    public void 没有_accessToken_返回_null()
    {
        Assert.Null(AuthDocument.Parse("""{ "uid": "u1" }"""));
        Assert.Null(AuthDocument.Parse("""{ "auth": { "refreshToken": "only-refresh" } }"""));
        Assert.Null(AuthDocument.Parse("""{ "accessToken": "" }"""));
    }

    [Fact]
    public void 顶层是数组或_null_返回_null()
    {
        Assert.Null(AuthDocument.Parse("[]"));
        Assert.Null(AuthDocument.Parse("null"));
        Assert.Null(AuthDocument.Parse("\"字符串\""));
        Assert.Null(AuthDocument.Parse("123"));
    }

    [Fact]
    public void 非法_JSON_抛_JsonException_让调用方能区分()
    {
        // 为什么要求抛而 Null：「文件格式不对」和「没登录」是两种问题，
        // 用户看到的提示不一样，所以调用方需要能分辨。
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => AuthDocument.Parse("{ 不是 json"));
    }

    [Fact]
    public void 字段类型不对时不崩()
    {
        // accessToken 是数字 → 当成没有
        Assert.Null(AuthDocument.Parse("""{ "accessToken": 12345 }"""));
        // uid 是对象 → 当空
        var c = AuthDocument.Parse("""{ "accessToken": "t", "uid": { "x": 1 } }""");
        Assert.NotNull(c);
        Assert.Equal("", c.Uid);
    }

    // ── 过期时间的单位归一 ────────────────────────────────────

    [Fact]
    public void expiresAt_秒会被转成毫秒()
    {
        // 1790000000 秒 ≈ 2026-09-21
        var c = AuthDocument.Parse("""{ "accessToken": "t", "expiresAt": 1790000000 }""");
        Assert.NotNull(c);
        Assert.Equal(1790000000000L, c.ExpiresAtMs);
    }

    [Fact]
    public void expiresAt_毫秒原样保留()
    {
        var c = AuthDocument.Parse("""{ "accessToken": "t", "expiresAt": 1790000000000 }""");
        Assert.NotNull(c);
        Assert.Equal(1790000000000L, c.ExpiresAtMs);
    }

    // ── 凭证对象本身 ──────────────────────────────────────────

    [Fact]
    public void ToString_不泄露_token()
    {
        // 这条很重要：ToString 会出现在异常消息和日志里。
        // 一旦打印了 accessToken，那就是凭证泄露。
        var c = new Credential
        {
            AccessToken = "SUPER-SECRET-TOKEN",
            Uid = "u1",
            EnterpriseId = "e1",
            Label = "张三",
        };

        var text = c.ToString();

        Assert.DoesNotContain("SUPER-SECRET-TOKEN", text, StringComparison.Ordinal);
        Assert.Contains("u1:e1", text, StringComparison.Ordinal);
        Assert.Contains("张三", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountTag_格式和旧版网关一致()
    {
        // 限流冷却的键按这个算，换格式会让旧账本对不上。
        var withEnt = new Credential { AccessToken = "t", Uid = "u1", EnterpriseId = "e9" };
        var noEnt = new Credential { AccessToken = "t", Uid = "u2" };

        Assert.Equal("u1:e9", withEnt.AccountTag);
        Assert.Equal("u2:", noEnt.AccountTag);
    }

    [Fact]
    public void Expiry_Normalize_的阈值判断()
    {
        Assert.Equal(0, Expiry.Normalize(0));
        Assert.Equal(0, Expiry.Normalize(-5));
        Assert.Equal(1790000000000L, Expiry.Normalize(1790000000));      // 秒
        Assert.Equal(1790000000000L, Expiry.Normalize(1790000000000));   // 毫秒
    }

    // ── 变体 ──────────────────────────────────────────────────

    [Fact]
    public void 变体定义完整()
    {
        Assert.Equal(2, UpstreamVariant.All.Count);
        Assert.Same(UpstreamVariant.China, UpstreamVariant.Find("workbuddy"));
        Assert.Same(UpstreamVariant.Global, UpstreamVariant.Find("workbuddy-ai"));
        Assert.Same(UpstreamVariant.China, UpstreamVariant.Find("WORKBUDDY"));  // 不区分大小写
        Assert.Null(UpstreamVariant.Find("不存在"));
    }
}
