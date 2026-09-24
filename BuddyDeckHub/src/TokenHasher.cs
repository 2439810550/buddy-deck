using System.Security.Cryptography;
using System.Text;

namespace BuddyDeckHub;

/// <summary>
/// 令牌的生成与校验。
///
/// 用 PBKDF2-HMAC-SHA256（BCL 自带，不引第三方）。
/// 格式：pbkdf2$迭代次数$salt(base64)$hash(base64)
///
/// 为什么不直接 SHA256：令牌是唯一凭证，等价于密码。SHA256 太快了，
/// 拿到库的人可以每秒试几十亿次。PBKDF2 迭代 21 万次，单次约 100ms。
/// 校验成本只发生在认证那一刻，不影响转发性能。
/// </summary>
public static class TokenHasher
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>发给用户的 key 长这样：bd-<43 个 base64url 字符>。</summary>
    public static string GenerateToken()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        return "bd-" + Base64Url(raw);
    }

    public static string Hash(string token)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(token.Normalize(NormalizationForm.FormKC)),
            salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>定长比较，避免时序侧信道。</summary>
    public static bool Verify(string token, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iterations) || iterations < 1) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(token.Normalize(NormalizationForm.FormKC)),
            salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>日志里用的短标识，取自明文前 11 位（含 bd- 前缀）。</summary>
    public static string PrefixOf(string token) =>
        token.Length <= 11 ? token : token[..11];

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
