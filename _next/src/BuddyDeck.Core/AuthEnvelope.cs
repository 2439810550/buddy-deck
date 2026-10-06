using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuddyDeck.Core;

/// <summary>
/// 5.6+ 的加密凭据外壳。
///
/// ## 磁盘上的真实形状
///
/// ```jsonc
/// {
///   "auth": {
///     "accessToken": {
///       "$wbEncrypted": 1,
///       "envelope": "eyJzdWl0ZSI6MSwia2V5SWQiOiI…"   // base64
///     }
///   }
/// }
/// ```
///
/// 把那个 base64 解开，是一层 JSON：
///
/// ```jsonc
/// { "suite": 1, "keyId": "0123456789abcdef",
///   "nonce": "<base64 12 字节>", "authTag": "<base64 16 字节>",
///   "ciphertext": "<base64>" }
/// ```
///
/// ## ⚠ 一个我踩过的坑
///
/// 第一版我写的是「在文档顶层或 `auth` 层找 `suite` + `keyId` 字段」——
/// **完全错了**。真正的标记是 token 字段**自己**变成
/// `{$wbEncrypted: 1, envelope: "…"}`，`suite`/`keyId` 藏在 base64 里。
///
/// 教训：涉及外部格式的东西，必须对着**实现**写，不能对着记忆写。
/// 单元测试当场抓到了这个错（15 个测试里挂了 1 个）。
///
/// ## 为什么这里的校验要和上游一样严
///
/// 上游的判断是：**声称加密但解不开的，整个文档当作「认不出」而不是「加密」**。
/// 因为没有任何密钥能打开它 —— 当成加密只会让后面白跑一次解密。
/// 这里照抄那个语义。
/// </summary>
public sealed record EncryptedEnvelope
{
    public required int Suite { get; init; }
    public required string KeyId { get; init; }
    public required byte[] Nonce { get; init; }
    public required byte[] AuthTag { get; init; }
    public required byte[] Ciphertext { get; init; }
}

public static partial class AuthEnvelope
{
    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex KeyIdPattern();

    /// <summary>
    /// 试着把一个字段值解析成加密外壳。
    ///
    /// @returns 解析成功返回外壳；不是加密形态、或声称加密但格式坏掉，都返回 null。
    /// </summary>
    public static EncryptedEnvelope? TryParseField(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;

        // 必须是 $wbEncrypted: 1
        if (!value.TryGetProperty("$wbEncrypted", out var flag)) return null;
        if (flag.ValueKind != JsonValueKind.Number || flag.GetInt32() != 1) return null;

        // 且 envelope 是字符串
        if (!value.TryGetProperty("envelope", out var envEl)) return null;
        if (envEl.ValueKind != JsonValueKind.String) return null;
        var envelopeText = envEl.GetString();
        if (string.IsNullOrEmpty(envelopeText)) return null;

        // 解开 base64，里面应该是一层 JSON
        string innerJson;
        try
        {
            innerJson = Encoding.UTF8.GetString(Convert.FromBase64String(envelopeText));
        }
        catch (FormatException)
        {
            return null;
        }

        JsonElement inner;
        try
        {
            using var doc = JsonDocument.Parse(innerJson);
            inner = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (inner.ValueKind != JsonValueKind.Object) return null;

        // suite 必须是 1
        if (!inner.TryGetProperty("suite", out var suiteEl)) return null;
        if (suiteEl.ValueKind != JsonValueKind.Number) return null;
        if (suiteEl.GetInt32() != 1) return null;

        // keyId 必须是 16 位小写 hex
        if (!inner.TryGetProperty("keyId", out var keyEl)) return null;
        if (keyEl.ValueKind != JsonValueKind.String) return null;
        var keyId = keyEl.GetString() ?? "";
        if (!KeyIdPattern().IsMatch(keyId)) return null;

        // 三段长度是固定的：nonce 12、authTag 16、ciphertext 不限但要能解
        var nonce = Base64OfLength(inner, "nonce", 12);
        var authTag = Base64OfLength(inner, "authTag", 16);
        var ciphertext = Base64Any(inner, "ciphertext");
        if (nonce is null || authTag is null || ciphertext is null) return null;
        if (ciphertext.Length == 0) return null;

        return new EncryptedEnvelope
        {
            Suite = 1,
            KeyId = keyId,
            Nonce = nonce,
            AuthTag = authTag,
            Ciphertext = ciphertext,
        };
    }

    /// <summary>
    /// 这个文档是不是加密的（至少有一个 token 字段是外壳）。
    ///
    /// 只看 `accessToken` 和 `refreshToken` 这两个 —— 它们是凭证的核心，
    /// 其他字段（domain、nickname）上游没加密。
    /// </summary>
    public static bool IsEncrypted(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var auth = root.TryGetProperty("auth", out var authEl) && authEl.ValueKind == JsonValueKind.Object
                ? authEl
                : root;

            foreach (var field in (string[])["accessToken", "refreshToken"])
            {
                if (auth.TryGetProperty(field, out var v) && TryParseField(v) is not null)
                {
                    return true;
                }
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>把文档里所有加密字段的外壳找出来（解密时要按 keyId 分组，一次解密一个 key 下的所有字段）。</summary>
    public static IReadOnlyDictionary<string, EncryptedEnvelope> FindEnvelopes(string json)
    {
        var result = new Dictionary<string, EncryptedEnvelope>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result;

            var auth = root.TryGetProperty("auth", out var authEl) && authEl.ValueKind == JsonValueKind.Object
                ? authEl
                : root;

            foreach (var prop in auth.EnumerateObject())
            {
                if (TryParseField(prop.Value) is { } env)
                {
                    result[prop.Name] = env;
                }
            }
        }
        catch (JsonException)
        {
            // 认不出就当没有 —— 调用方会走明文路径
        }
        return result;
    }

    private static byte[]? Base64OfLength(JsonElement obj, string name, int expected)
    {
        var bytes = Base64Any(obj, name);
        return bytes is { Length: var len } && len == expected ? bytes : null;
    }

    private static byte[]? Base64Any(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        if (string.IsNullOrEmpty(s)) return null;
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
