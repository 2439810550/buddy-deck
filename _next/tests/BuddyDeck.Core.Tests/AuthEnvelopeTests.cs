using System.Text;
using System.Text.Json;
using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 5.6+ 加密外壳的识别测试。
///
/// ## 这块的教训
///
/// 我第一版把格式写错了 —— 以为是「顶层有 suite + keyId 字段」。
/// 真实格式是 token 字段自己变成 `{$wbEncrypted:1, envelope:"<base64>"}`，
/// suite/keyId 藏在 base64 里面。
///
/// 单元测试当场抓到了（15 个测试挂了 1 个）。所以这里的测试**照着
/// 上游实现写**，不是照着记忆写。
/// </summary>
public class AuthEnvelopeTests
{
    /// <summary>造一个合法的加密字段（内容随意，只要格式对）。</summary>
    private static string MakeEnvelope(
        int suite = 1,
        string keyId = "0123456789abcdef",
        int nonceLen = 12,
        int tagLen = 16,
        int cipherLen = 48)
    {
        var inner = new
        {
            suite,
            keyId,
            nonce = Convert.ToBase64String(new byte[nonceLen]),
            authTag = Convert.ToBase64String(new byte[tagLen]),
            ciphertext = Convert.ToBase64String(new byte[cipherLen]),
        };
        var envelopeB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(inner)));
        return $$"""{ "$wbEncrypted": 1, "envelope": "{{envelopeB64}}" }""";
    }

    // ── 正常情况 ──────────────────────────────────────────────

    [Fact]
    public void 合法的加密字段能解析()
    {
        using var doc = JsonDocument.Parse(MakeEnvelope());
        var env = AuthEnvelope.TryParseField(doc.RootElement);

        Assert.NotNull(env);
        Assert.Equal(1, env.Suite);
        Assert.Equal("0123456789abcdef", env.KeyId);
        Assert.Equal(12, env.Nonce.Length);
        Assert.Equal(16, env.AuthTag.Length);
        Assert.Equal(48, env.Ciphertext.Length);
    }

    [Fact]
    public void 文档里有加密字段时能被识别()
    {
        var json = $$"""{ "auth": { "accessToken": {{MakeEnvelope()}}, "refreshToken": {{MakeEnvelope()}} } }""";
        Assert.True(AuthEnvelope.IsEncrypted(json));
    }

    [Fact]
    public void 明文文档不算加密()
    {
        Assert.False(AuthEnvelope.IsEncrypted("""{ "accessToken": "plain-string" }"""));
        Assert.False(AuthEnvelope.IsEncrypted("""{ "auth": { "accessToken": "plain" } }"""));
    }

    [Fact]
    public void 能找出文档里所有外壳()
    {
        var json = $$"""
        { "auth": {
            "accessToken": {{MakeEnvelope()}},
            "refreshToken": {{MakeEnvelope(keyId: "fedcba9876543210")}},
            "domain": "workbuddy.cn"
        } }
        """;

        var found = AuthEnvelope.FindEnvelopes(json);

        Assert.Equal(2, found.Count);
        Assert.True(found.ContainsKey("accessToken"));
        Assert.True(found.ContainsKey("refreshToken"));
        Assert.Equal("fedcba9876543210", found["refreshToken"].KeyId);
        // domain 是明文，不该被当成外壳
        Assert.False(found.ContainsKey("domain"));
    }

    // ── 坏数据：都要当成「不是加密」，而不是崩 ────────────────

    [Fact]
    public void 标记是_1_但没有_envelope_不算加密()
    {
        using var doc = JsonDocument.Parse("""{ "$wbEncrypted": 1 }""");
        Assert.Null(AuthEnvelope.TryParseField(doc.RootElement));
    }

    [Fact]
    public void envelope_不是合法_base64_不算加密()
    {
        using var doc = JsonDocument.Parse("""{ "$wbEncrypted": 1, "envelope": "这不是base64!!!" }""");
        Assert.Null(AuthEnvelope.TryParseField(doc.RootElement));
    }

    [Fact]
    public void base64_解出来不是_JSON_不算加密()
    {
        var garbage = Convert.ToBase64String(Encoding.UTF8.GetBytes("完全不是 json"));
        using var doc = JsonDocument.Parse($$"""{ "$wbEncrypted": 1, "envelope": "{{garbage}}" }""");
        Assert.Null(AuthEnvelope.TryParseField(doc.RootElement));
    }

    [Theory]
    [InlineData(2)]          // suite 只认 1
    [InlineData(0)]
    public void suite_不是_1_不算加密(int suite)
    {
        using var doc = JsonDocument.Parse(MakeEnvelope(suite: suite));
        Assert.Null(AuthEnvelope.TryParseField(doc.RootElement));
    }

    [Theory]
    [InlineData("0123456789ABCDEF")]     // 大写不算（上游要求小写）
    [InlineData("0123456789abcde")]      // 15 位
    [InlineData("0123456789abcdef0")]    // 17 位
    [InlineData("0123456789abcdeg")]     // 非 hex
    [InlineData("")]
    public void keyId_格式不对不算加密(string keyId)
    {
        using var doc = JsonDocument.Parse(MakeEnvelope(keyId: keyId));
        Assert.Null(AuthEnvelope.TryParseField(doc.RootElement));
    }

    [Fact]
    public void nonce_长度不对不算加密()
    {
        // nonce 必须是 12 字节（AES-GCM 规范）
        using var doc = JsonDocument.Parse(MakeEnvelope(nonceLen: 16));
        Assert.Null(AuthEnvelope.TryParseField(doc.RootElement));
    }

    [Fact]
    public void authTag_长度不对不算加密()
    {
        // authTag 必须是 16 字节
        using var doc = JsonDocument.Parse(MakeEnvelope(tagLen: 12));
        Assert.Null(AuthEnvelope.TryParseField(doc.RootElement));
    }

    [Fact]
    public void 密码文为空不算加密()
    {
        using var doc = JsonDocument.Parse(MakeEnvelope(cipherLen: 0));
        Assert.Null(AuthEnvelope.TryParseField(doc.RootElement));
    }

    [Fact]
    public void 字段值是字符串或数组不崩()
    {
        using var a = JsonDocument.Parse("""{ "$wbEncrypted": 1, "envelope": [] }""");
        Assert.Null(AuthEnvelope.TryParseField(a.RootElement));
        using var b = JsonDocument.Parse("\"就是字符串\"");
        Assert.Null(AuthEnvelope.TryParseField(b.RootElement));
        using var c = JsonDocument.Parse("null");
        Assert.Null(AuthEnvelope.TryParseField(c.RootElement));
    }

    [Fact]
    public void 非法_JSON_的文档不算加密()
    {
        Assert.False(AuthEnvelope.IsEncrypted("{ 坏掉的"));
        Assert.False(AuthEnvelope.IsEncrypted("[]"));
        Assert.False(AuthEnvelope.IsEncrypted("null"));
    }
}
