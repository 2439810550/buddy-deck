using System.Security.Cryptography;
using System.Text;

namespace BuddyDeck.Core;

/// <summary>
/// scrypt 口令派生（RFC 7914）。
///
/// ## ⚠ 为什么自己实现而不是用现成的
///
/// 旧版 `workbuddy_api/src/transfer.js` 的导出文件用的是
/// **scrypt(N=16384, r=8, p=1) + AES-256-GCM**，而 **.NET BCL 没有 scrypt**
/// （已核实：net8.0 参考程序集里没有任何 scrypt 符号）。
///
/// 三条路：
///
/// ```
/// A. 改用 PBKDF2（Rfc2898DeriveBytes）
///    → 简单、安全、零风险，但**打不开旧版导出的文件**
/// B. 自己实现 scrypt（本文件）
///    → 与旧版互通，代价是引入了自写密码学代码
/// C. 引第三方包
///    → 破坏零依赖承诺
/// ```
///
/// ## 为什么最后选了 B（而不是"更安全的" A）
///
/// **因为"旧版导出文件能否在新版打开"不是假设，是已发生的事实**：
/// 开发机上就躺着一份旧版导出的真实文件
/// （`workbuddy-accounts-workbuddy-2026-09-25.json`，55 KB，
/// format=workbuddy-accounts / version=1 / kdf=scrypt N=16384）。
///
/// 这是**整个项目重写**的场景 —— 用户手里那份文件正是他换到新版时要
/// 拿过来的东西。选 A 的话，新版一上线，用户点「导入账号」选到那个文件，
/// 得到的是「口令不对或文件损坏」—— **而他的口令是对的**。
/// 那种失败比"少一个功能"严重得多：用户会以为自己的账号没了。
///
/// ## 自己写密码学的纪律（本文件必须遵守）
///
/// 自写密码学是**高风险**的，所以这里的做法是：
///
/// 1. **只实现 RFC 7914 的精确算法，不做任何"优化"** —— 任何自作聪明的
///    简化都可能悄悄破坏安全性，而且看不出来。
/// 2. **用 RFC 7914 的官方测试向量验证**（见 AccountTransferTests）——
///    这是唯一能证明"我实现的确实是 scrypt"的方法。
///    ⚠ 只做「自己 seal 自己 open」的往返测试是**不够的**：那种测试在
///    实现整个错掉的情况下照样会绿（两边错得一样）。
/// 3. **与旧版 Node 实现的互操作测试**（见测试里的真实文件/向量）。
///
/// 本实现依据 RFC 7914 §5-§8（Salsa20/8 核心 + BlockMix + ROMix）。
///
/// ## 为什么是 public 而不是 internal
///
/// 它是**独立可验证的密码学原语**：RFC 7914 §12 公布了官方向量，
/// 而验证「我实现的确实是 scrypt」只能靠那些向量（往返测试证明不了 ——
/// 两边错得一样照样绿）。测试要能直接调它。
///
/// 另外它对项目外也有意义：任何需要「按 RFC 7914 派生密钥」的地方
/// 都该用它，而不是各自再抄一份 scrypt。
/// </summary>
public static class Scrypt
{
    /// <summary>Salsa20/8 的轮数常量。RFC 7914 §3 规定固定为 8，不是参数。</summary>
    private const int SalsaRounds = 8;

    /// <summary>
    /// 派生密钥。
    ///
    /// @param password 口令（UTF-8 编码前会做 NFKC 归一 —— 见下方说明）
    /// @param salt     盐
    /// @param n        CPU/内存代价参数。**必须是 2 的幂且 > 1**
    /// @param r        块大小参数
    /// @param p        并行度参数
    /// @param dkLen    输出长度（字节）
    /// </summary>
    /// <exception cref="ArgumentException">参数越界（N 不是 2 的幂等）。</exception>
    public static byte[] DeriveKey(
        string password,
        byte[] salt,
        int n,
        int r,
        int p,
        int dkLen)
    {
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(r);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(p);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dkLen);

        // N 必须是 >1 的 2 的幂。不是的话 ROMix 的两两异或归约不成立，
        // 结果是**算得出东西但完全不安全** —— 必须硬拒，不能"将就用"。
        if (n <= 1 || (n & (n - 1)) != 0)
        {
            throw new ArgumentException($"scrypt 的 N 必须是大于 1 的 2 的幂，收到 {n}", nameof(n));
        }

        // RFC 7914 §2：内存用量约为 128 * r * N 字节。
        // r * p < 2^30 是标准里的约束。
        if ((long)r * p >= 1L << 30)
        {
            throw new ArgumentException($"scrypt 要求 r * p < 2^30，收到 r={r} p={p}", nameof(p));
        }

        // ⚠ NFKC 归一：和旧版 `passphrase.normalize("NFKC")` 一致。
        // 不做的话，同一串"看起来一样"的字符（组合字符 vs 预组合字符）
        // 在不同机器/输入法下编码出的字节不同 —— 用户会「明明输对了却解不开」。
        var normalized = password.Normalize(NormalizationForm.FormKC);
        var passwordBytes = Encoding.UTF8.GetBytes(normalized);

        var blockSize = 128 * r;

        // ── 第 1 步：B = PBKDF2-HMAC-SHA256(P, S, 1, p * 128 * r) ──
        // RFC 7914 §7 的 scryptPBKDF。迭代次数固定为 **1**（这是 scrypt 的
        // 定义，不是"偷懒"—— 强度来自后面的 ROMix，不是这里的迭代）。
        var b = Rfc2898DeriveBytes.Pbkdf2(
            passwordBytes,
            salt,
            iterations: 1,
            HashAlgorithmName.SHA256,
            outputLength: p * blockSize);

        // ── 第 2 步：对每个并行块做 ROMix ──
        //
        // V 的大小：N 个块，每块 128*r 字节 = 32*r 个 uint。
        //   ⇒ n * 32 * r 个 uint = n * 128 * r 字节
        // N=16384 / r=8 时是 16 MiB —— **这正是 scrypt 的设计意图**
        // （memory-hard）：这个内存量是安全属性，不是浪费。
        // ⚠ 第一版我写成 `n * 16 * r`，只有需要的一半 —— 表现为
        // ArgumentOutOfRangeException，而不是「结果不对」。
        var v = new uint[n * 32 * r];

        // X 和 Y 两块交替使用，每块 32*r 个 uint
        var xy = new uint[64 * r];

        for (var i = 0; i < p; i++)
        {
            var offset = i * blockSize;
            ROMix(b.AsSpan(offset, blockSize), v, xy, n, r);
        }

        // ── 第 3 步：DK = PBKDF2-HMAC-SHA256(P, B, 1, dkLen) ──
        return Rfc2898DeriveBytes.Pbkdf2(
            passwordBytes,
            b,
            iterations: 1,
            HashAlgorithmName.SHA256,
            outputLength: dkLen);
    }

    /// <summary>
    /// ROMix（RFC 7914 §5）—— scrypt 的内存硬化核心。
    ///
    /// 它做的事：把块反复做 BlockMix 并**全部存下来**（V 数组，大小 N），
    /// 然后按一个"由数据本身决定"的伪随机顺序去访问它们。
    /// 攻击者要么花同样多的内存，要么就要重新计算 —— 这就是 scrypt
    /// 抗 ASIC/GPU 的原理。
    /// </summary>
    private static void ROMix(Span<byte> block, uint[] v, uint[] xy, int n, int r)
    {
        var words = 32 * r;

        // X 和 Y 在这块内存里交替 —— 用下标切换而不是交换变量。
        // ⚠ 不能用 `(x, y) = (y, x)` 那种元组交换：Span<T> 是 ref struct，
        // 不能作为元组的类型参数（CS9244）。
        // ⚠ 也不能用局部函数包一层 Slice：局部函数会捕获 blockWords，
        // 而 ref struct 不能被 lambda/局部函数捕获（CS8175）。
        // 所以直接写 `xy.AsSpan(索引 * words, words)`，不抽辅助。
        var current = 0;
        var other = 1;

        // X = B（小端解释成 uint）
        FromBytes(block, xy.AsSpan(0, words));

        for (var i = 0; i < n; i++)
        {
            var x = xy.AsSpan(current * words, words);
            var y = xy.AsSpan(other * words, words);

            // V[i] = X
            x.CopyTo(v.AsSpan(i * words, words));

            // X = BlockMix(X)
            BlockMix(x, y, r);
            (current, other) = (other, current);
        }

        for (var i = 0; i < n; i++)
        {
            var x = xy.AsSpan(current * words, words);
            var y = xy.AsSpan(other * words, words);

            // j = Integerify(X) mod N
            // Integerify = 取块的**最后一个 64 字节**的第 0 个 word（RFC 7914 §5）
            var j = (int)(x[words - 16] & (uint)(n - 1));

            // X = BlockMix(X XOR V[j])
            var vj = v.AsSpan(j * words, words);
            for (var k = 0; k < words; k++) x[k] ^= vj[k];

            BlockMix(x, y, r);
            (current, other) = (other, current);
        }

        ToBytes(block, xy.AsSpan(current * words, words));
    }

    /// <summary>
    /// BlockMix（RFC 7914 §4）。
    ///
    /// 把 2r 个 64 字节的块先整体做一次 Salsa20/8，再重排 —— 重排规则是
    /// 「偶数位在前、奇数位在后」，这是 scrypt 特有的，不能想当然。
    /// </summary>
    private static void BlockMix(Span<uint> input, Span<uint> output, int r)
    {
        Span<uint> x = stackalloc uint[16];

        // X = B[2r-1]（最后一个子块）
        input.Slice((2 * r - 1) * 16, 16).CopyTo(x);

        for (var i = 0; i < 2 * r; i++)
        {
            // X = Salsa20/8(X XOR B[i])
            var block = input.Slice(i * 16, 16);
            for (var k = 0; k < 16; k++) x[k] ^= block[k];

            Salsa20_8(x);

            // ⚠ 输出位置的重排：偶数子块 → 前半，奇数子块 → 后半。
            // 写成 i（顺序）的话算法就错了，而且**自己往返仍能通过** ——
            // 只有 RFC 测试向量能抓到。
            var target = (i / 2) + (i % 2) * r;
            x.CopyTo(output.Slice(target * 16, 16));
        }
    }

    /// <summary>
    /// Salsa20/8 核心（RFC 7914 §3）。
    ///
    /// 32 位字上的 ARX（加-旋-异或）网络，8 轮（4 个双轮）。
    /// 纯运算，没有查表 —— 这正是它抗时序攻击的原因。
    /// </summary>
    private static void Salsa20_8(Span<uint> block)
    {
        Span<uint> x = stackalloc uint[16];
        block.CopyTo(x);

        for (var i = 0; i < SalsaRounds; i += 2)
        {
            // 列轮
            x[4] ^= RotateLeft(x[0] + x[12], 7);
            x[8] ^= RotateLeft(x[4] + x[0], 9);
            x[12] ^= RotateLeft(x[8] + x[4], 13);
            x[0] ^= RotateLeft(x[12] + x[8], 18);

            x[9] ^= RotateLeft(x[5] + x[1], 7);
            x[13] ^= RotateLeft(x[9] + x[5], 9);
            x[1] ^= RotateLeft(x[13] + x[9], 13);
            x[5] ^= RotateLeft(x[1] + x[13], 18);

            x[14] ^= RotateLeft(x[10] + x[6], 7);
            x[2] ^= RotateLeft(x[14] + x[10], 9);
            x[6] ^= RotateLeft(x[2] + x[14], 13);
            x[10] ^= RotateLeft(x[6] + x[2], 18);

            x[3] ^= RotateLeft(x[15] + x[11], 7);
            x[7] ^= RotateLeft(x[3] + x[15], 9);
            x[11] ^= RotateLeft(x[7] + x[3], 13);
            x[15] ^= RotateLeft(x[11] + x[7], 18);

            // 行轮
            x[1] ^= RotateLeft(x[0] + x[3], 7);
            x[2] ^= RotateLeft(x[1] + x[0], 9);
            x[3] ^= RotateLeft(x[2] + x[1], 13);
            x[0] ^= RotateLeft(x[3] + x[2], 18);

            x[6] ^= RotateLeft(x[5] + x[4], 7);
            x[7] ^= RotateLeft(x[6] + x[5], 9);
            x[4] ^= RotateLeft(x[7] + x[6], 13);
            x[5] ^= RotateLeft(x[4] + x[7], 18);

            x[11] ^= RotateLeft(x[10] + x[9], 7);
            x[8] ^= RotateLeft(x[11] + x[10], 9);
            x[9] ^= RotateLeft(x[8] + x[11], 13);
            x[10] ^= RotateLeft(x[9] + x[8], 18);

            x[12] ^= RotateLeft(x[15] + x[14], 7);
            x[13] ^= RotateLeft(x[12] + x[15], 9);
            x[14] ^= RotateLeft(x[13] + x[12], 13);
            x[15] ^= RotateLeft(x[14] + x[13], 18);
        }

        // 加上输入（Salsa 家族的 feed-forward）
        for (var i = 0; i < 16; i++) block[i] += x[i];
    }

    private static uint RotateLeft(uint value, int count) => (value << count) | (value >> (32 - count));

    /// <summary>字节 → uint（**小端** —— RFC 7914 全篇按小端解释）。</summary>
    private static void FromBytes(ReadOnlySpan<byte> source, Span<uint> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            var offset = i * 4;
            destination[i] = (uint)(source[offset]
                | (source[offset + 1] << 8)
                | (source[offset + 2] << 16)
                | (source[offset + 3] << 24));
        }
    }

    /// <summary>uint → 字节（小端）。</summary>
    private static void ToBytes(Span<byte> destination, ReadOnlySpan<uint> source)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var offset = i * 4;
            var value = source[i];
            destination[offset] = (byte)value;
            destination[offset + 1] = (byte)(value >> 8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
        }
    }
}
