using System.Text;
using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// scrypt 实现的**正确性证明**（RFC 7914 官方测试向量）。
///
/// ## 为什么这个文件必须存在
///
/// 自写密码学是高风险的事，而**往返测试证明不了它是对的**：
/// 如果 Salsa20/8 的旋转量写错、或者 BlockMix 的重排写成顺序，
/// 「我自己 seal 的文件我自己能 open」照样会绿 —— 因为两边错得一样。
///
/// 唯一能证明「我实现的确实是 RFC 7914 定义的 scrypt」的，
/// 是拿**标准里公布的输入/输出对**跑一遍。下面就是。
///
/// 向量出处：RFC 7914 §12（Test Vectors for scrypt）。
/// </summary>
public class ScryptTests
{
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static byte[] Derive(string password, byte[] salt, int n, int r, int p, int dkLen)
        => Scrypt.DeriveKey(password, salt, n, r, p, dkLen);

    // ── RFC 7914 §12 的三条向量 ────────────────────────────────

    [Fact]
    public void 向量1_空口令空盐()
    {
        var dk = Derive("", [], n: 16, r: 1, p: 1, dkLen: 64);

        Assert.Equal(
            "77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442"
            + "fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906",
            Hex(dk));
    }

    [Fact]
    public void 向量2_password_NaCl()
    {
        var dk = Derive(
            "password", Encoding.UTF8.GetBytes("NaCl"), n: 1024, r: 8, p: 16, dkLen: 64);

        Assert.Equal(
            "fdbabe1c9d3472007856e7190d01e9fe7c6ad7cbc8237830e77376634b373162"
            + "2eaf30d92e22a3886ff109279d9830dac727afb94a83ee6d8360cbdfa2cc0640",
            Hex(dk));
    }

    [Fact]
    public void 向量3_长口令长盐()
    {
        // ★ 这条用的正是我们生产上要用的参数（N=16384, r=8, p=1）
        var dk = Derive(
            "pleaseletmein",
            Encoding.UTF8.GetBytes("SodiumChloride"),
            n: 16384, r: 8, p: 1, dkLen: 64);

        Assert.Equal(
            "7023bdcb3afd7348461c06cd81fd38ebfda8fbba904f8e3ea9b543f6545da1f2"
            + "d5432955613f0fcf62d49705242a9af9e61e85dc0d651e40dfcf017b45575887",
            Hex(dk));
    }

    /// <summary>
    /// RFC 7914 第 4 条向量（N=1048576，约 1 GB 内存）。
    ///
    /// **RFC 正文自己写明「太占内存，常见实现跑不动」** —— 跳过它
    /// 不是实现有问题，那条向量的设计意图就是「多数环境跑不了」。
    /// 前三条已覆盖 N=16 / 1024 / 16384 三个量级，包含我们生产用的 16384。
    /// </summary>
    [Fact(Skip = "RFC 7914 第 4 条向量需约 1GB 内存，RFC 本身说明多数实现跑不动；前三条已覆盖生产参数 N=16384")]
    public void 向量4_超大N()
    {
    }

    // ── 参数校验（安全边界，必须硬拒）─────────────────────────

    [Fact]
    public void N不是2的幂时拒绝()
    {
        // N 不是 2 的幂 → ROMix 的两两异或归约不成立 → 算得出东西但
        // 完全不安全。必须硬拒，不能"将就用"。
        Assert.Throws<ArgumentException>(() =>
            Derive("pw", new byte[16], n: 1000, r: 8, p: 1, dkLen: 32));

        Assert.Throws<ArgumentException>(() =>
            Derive("pw", new byte[16], n: 1, r: 8, p: 1, dkLen: 32));

        Assert.Throws<ArgumentException>(() =>
            Derive("pw", new byte[16], n: 0, r: 8, p: 1, dkLen: 32));
    }

    [Fact]
    public void r乘p超上限时拒绝()
    {
        // RFC 7914 §2：r * p < 2^30
        Assert.Throws<ArgumentException>(() =>
            Derive("pw", new byte[16], n: 16, r: 1 << 20, p: 1 << 20, dkLen: 32));
    }

    [Fact]
    public void 相同输入结果稳定()
    {
        var salt = new byte[] { 1, 2, 3, 4 };
        var a = Derive("口令", salt, 16, 1, 1, 32);
        var b = Derive("口令", salt, 16, 1, 1, 32);

        Assert.Equal(Hex(a), Hex(b));
    }

    [Fact]
    public void 不同盐结果不同()
    {
        var a = Derive("口令", [1, 2, 3, 4], 16, 1, 1, 32);
        var b = Derive("口令", [4, 3, 2, 1], 16, 1, 1, 32);

        Assert.NotEqual(Hex(a), Hex(b));
    }

    [Fact]
    public void 输出长度可配()
    {
        Assert.Equal(16, Derive("pw", new byte[8], 16, 1, 1, 16).Length);
        Assert.Equal(64, Derive("pw", new byte[8], 16, 1, 1, 64).Length);
    }

    [Fact]
    public void Unicode归一化保证同一口令解得出()
    {
        // "é" 有两种写法：预组合 U+00E9 vs 组合 U+0065 U+0301。
        // 用户在不同机器/输入法下可能输入不同的那一串 —— 归一化之后
        // 必须得到同一个密钥，否则「明明输对了却解不开」。
        // 这与旧版 `passphrase.normalize("NFKC")` 的行为一致。
        var precomposed = "caf\u00e9";
        var decomposed = "cafe\u0301";

        Assert.NotEqual(precomposed, decomposed);

        var a = Derive(precomposed, [9, 9], 16, 1, 1, 32);
        var b = Derive(decomposed, [9, 9], 16, 1, 1, 32);

        Assert.Equal(Hex(a), Hex(b));
    }
}
