using System.Text.Json;

namespace BuddyDeck.Core;

/// <summary>
/// 读 WorkBuddy 桌面 App 写在本机的登录文件。
///
/// ## 文件长什么样
///
/// 有**两种**形状，历史上变过：
///
/// ```jsonc
/// // 嵌套形态
/// { "auth": { "accessToken": "...", "refreshToken": "..." },
///   "account": { "uid": "...", "nickname": "...", "enterpriseId": "..." } }
///
/// // 扁平形态
/// { "accessToken": "...", "uid": "...", "nickname": "..." }
/// ```
///
/// 两种都得认 —— 用户装的是哪个版本的 App 不由我们决定。
///
/// ## 5.6+ 的加密
///
/// App 5.6 之后，token 字段不再是明文，而是 `{$wbEncrypted: 1, envelope}` 外壳。
/// 识别和拆解在 <see cref="AuthEnvelope"/>，解密走 <see cref="ICredentialDecryptor"/>。
/// </summary>
public static class AuthDocument
{
    /// <summary>
    /// 解析一份登录文件。
    ///
    /// @returns 解析出的凭证；文件里没有 accessToken 时返回 null。
    /// @throws JsonException 不是合法 JSON。调用方通常想区分「格式不对」
    ///         和「没登录」这两种情况。
    /// </summary>
    public static Credential? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        // 嵌套形态：有 auth 对象就认它，account 拿身份信息
        JsonElement auth;
        JsonElement identity;
        if (root.TryGetProperty("auth", out var authEl) && authEl.ValueKind == JsonValueKind.Object)
        {
            auth = authEl;
            identity = root.TryGetProperty("account", out var accEl) && accEl.ValueKind == JsonValueKind.Object
                ? accEl
                : default;
        }
        else
        {
            auth = root;
            identity = root;
        }

        var accessToken = StringOf(auth, "accessToken");
        if (string.IsNullOrEmpty(accessToken)) return null;

        var label = StringOf(identity, "nickname") ?? StringOf(identity, "domain") ?? "";

        return new Credential
        {
            AccessToken = accessToken,
            RefreshToken = StringOf(auth, "refreshToken") ?? "",
            ExpiresAtMs = Expiry.Normalize(LongOf(auth, "expiresAt") ?? 0),
            Uid = StringOf(identity, "uid") ?? "",
            EnterpriseId = StringOf(identity, "enterpriseId"),
            Label = label,
            Domain = StringOf(auth, "domain") ?? "",
        };
    }

    private static string? StringOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        var s = value.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static long? LongOf(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;
    }
}

/// <summary>
/// 把 5.6+ 的加密外壳解成明文 token。
///
/// 为什么单独一个接口：解密要调 App 自带的原生 helper（Windows 上是
/// `WorkBuddy.exe` 的某个导出， macOS/Linux 各有各的）。那部分平台相关、
/// 而且要起子进程，没法在单元测试里跑。
///
/// Core 只依赖这个接口；实现放在 Gateway/App 里（或者测试里塞个假的）。
/// </summary>
public interface ICredentialDecryptor
{
    /// <summary>
    /// 解一个加密外壳。
    ///
    /// @param envelopeJson 整个外壳的 JSON 文本。
    /// @returns 明文；解不开返回 null（不要抛 —— 「解不开」是常见情况，
    ///          比如用户没装 App、或者 helper 版本不匹配）。
    /// </summary>
    string? TryDecrypt(string envelopeJson);
}
