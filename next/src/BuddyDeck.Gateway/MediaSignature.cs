using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace BuddyDeck.Gateway;

/// <summary>
/// 媒体文件的 **HMAC 签名** —— 让 `GET /v1/images/file/&lt;name&gt;?s=&lt;签名&gt;`
/// 可以**不带 Authorization 头**访问。
///
/// ## 为什么需要它（旧版 `gateway.js:546-551` 的原话）
///
/// > 用户在浏览器里直接打开图片时**不会带那个头** —— 不特殊处理的话
/// > 他看到的是 401，而不是图。
/// > 签名是用 API key 作为 HMAC 密钥算出来的，所以
/// > **「能拿出有效签名」等价于「知道 key」**。安全性不降低，但可用性天壤之别。
///
/// 换言之：签名不是「绕开鉴权」，而是**另一条等价的鉴权路径** ——
/// 它的凭据是可推导的（知道 key 才算得出），不是公开的。
///
/// ## ⚠ 三个必须一起成立的性质（缺一个就不安全）
///
/// ① **长度固定**：签名截到 16 个十六进制字符（64 bit）。够抗暴力猜测，
///    又短到能塞进 URL。
/// ② **常量时间比较**：用 <see cref="CryptographicOperations.FixedTimeEquals"/>，
///    绝不用 `==` —— 字符串比较短路，攻击者能靠响应时间逐字节猜出签名。
/// ③ **签名只覆盖文件名，不覆盖其它参数**：所以 `?s=x&amp;foo=y` 里的
///    `foo` 不影响签名 —— **不能**把签名当成「整条 URL 的防篡改」来用。
///    （旧版也是这个语义，见 `gateway.js:555` 只把 `name` 喂给 `signName`。）
///
/// ## ⚠ 它和 `GatewayApp.Authorized` 的分工
///
/// 这个类**只算签名、只比签名**，不做任何请求分派 —— 它是纯函数集合。
/// 「什么时候该用签名放行」的决策在 `GatewayApp.Authorized` 里
/// （对应旧版 `gateway.js:552-566` 的位置：**在路由之前**的统一鉴权段，
/// **不在**文件端点内部）。
///
/// 这么分是因为旧版就是那么分的：`serveGeneratedFile`（`gateway.js:1241`）
/// **一行签名逻辑都没有**，它只管读盘和发送。
///
/// ## apiKey 为空时的语义
///
/// 返回空签名，且 <see cref="SignatureMatches"/> 恒为 false。
/// 「网关没开鉴权」时所有请求本来就放行（见 `GatewayApp.Authorized` 第一行），
/// 所以签名这条路根本不会被问到。
/// </summary>
public static class MediaSignature
{
    /// <summary>签名在 URL 里的查询参数名（与旧版一致：`?s=`）。</summary>
    public const string QueryName = "s";

    /// <summary>签名覆盖的路径前缀（与旧版一致）。</summary>
    public const string PathPrefix = "/v1/images/file/";

    /// <summary>保留的十六进制字符数（64 bit）。</summary>
    private const int SignatureLength = 16;

    /// <summary>
    /// 算一个文件名的签名。
    ///
    /// 与旧版 `gateway.js:333-336` 逐位对齐：
    /// ```js
    /// function signName(name, apiKey) {
    ///   if (apiKey === "") return "";
    ///   return createHmac("sha256", apiKey).update(name).digest("hex").slice(0, 16);
    /// }
    /// ```
    ///
    /// @returns 小写十六进制、16 字符；**apiKey 为空时返回空串**（调用方不要再比）。
    /// </summary>
    public static string SignName(string name, string? apiKey)
    {
        // 空 key = 网关没开鉴权 —— 返回空串，与旧版一致。
        // 调用方（SignatureMatches）把空串当「签名这条路不可用」。
        if (string.IsNullOrEmpty(apiKey)) return "";

        // ⚠ UTF-8 而不是默认编码：文件名虽只含 [a-f0-9].ext（纯 ASCII），
        //   但 API key 可能含中文/emoji。旧版的 Buffer.from(x,"utf8") 同样是 UTF-8，
        //   两边必须一致，否则对同一个 key 算出不同签名。
        var keyBytes = Encoding.UTF8.GetBytes(apiKey);
        var nameBytes = Encoding.UTF8.GetBytes(name);

        var hash = HMACSHA256.HashData(keyBytes, nameBytes);
        return Convert.ToHexString(hash).ToLowerInvariant()[..SignatureLength];
    }

    /// <summary>
    /// 常量时间比较签名。
    ///
    /// 与旧版 `gateway.js:339-345` 对齐：
    /// ```js
    /// function signatureMatches(expected, presented) {
    ///   if (typeof presented !== "string" || expected === "") return false;
    ///   ...
    ///   if (a.length !== b.length) return false;
    ///   return timingSafeEqual(a, b);
    /// }
    /// ```
    ///
    /// ⚠ **expected 为空串时恒 false**（网关没开鉴权 → 签名这条路不成立）。
    /// ⚠ **长度不同直接 false** —— 长度本身不是秘密（签名长度是固定的 16），
    ///    所以提前返回不泄露信息，反而避免 FixedTimeEquals 对不同长度抛异常。
    /// </summary>
    public static bool SignatureMatches(string? expected, string? presented)
    {
        if (string.IsNullOrEmpty(expected)) return false;
        if (string.IsNullOrEmpty(presented)) return false;

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(presented);
        if (a.Length != b.Length) return false;

        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// 这个请求是不是「用有效签名访问媒体文件」。
    ///
    /// 对应旧版 `gateway.js:552-557`：
    /// ```js
    /// const isSignedImageRequest = req.method === "GET"
    ///   && url.startsWith("/v1/images/file/")
    ///   && signatureMatches(signName(...), searchParams.get("s"));
    /// ```
    ///
    /// ## ⚠ 三个条件的顺序是有意的（最便宜的排前面）
    ///
    /// 方法 → 前缀 → 算签名。前面两个是纯字符串判断，失败就不必算 HMAC。
    ///
    /// ## ⚠ 提取文件名的方式与路由保持一致
    ///
    /// 这里**自己**从 path 里切，而不是接受调用方传进来的 name ——
    /// 因为调用方（`GatewayApp.Authorized`）在**路由之前**跑，
    /// 那时候还没有「解析出来的文件名」这个概念。
    /// 两处切法的唯一真源是 <see cref="PathPrefix"/>。
    /// </summary>
    public static bool IsSignedMediaRequest(HttpContext context, string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return false;
        if (!HttpMethods.IsGet(context.Request.Method)) return false;

        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith(PathPrefix, StringComparison.Ordinal)) return false;

        var name = path[PathPrefix.Length..];

        // ⚠ 尾部斜杠要剥掉，理由与 GatewayApp.NormalizePath 一致：
        //   旧版认 `/v1/models` 和 `/v1/models/` 两种写法。
        //   不剥的话 `/v1/images/file/abc.png/` 会拿 "abc.png/" 去算签名 ——
        //   而路由那边的 NormalizePath 会把它当 "abc.png"，两边对不上。
        if (name.Length > 1 && name.EndsWith('/')) name = name.TrimEnd('/');

        var presented = context.Request.Query[QueryName].ToString();
        var expected = SignName(name, apiKey);

        return SignatureMatches(expected, presented);
    }

    /// <summary>
    /// 给一个文件名拼出**带签名**的完整 URL。
    ///
    /// 与 `MediaEndpoints.MediaUrl` 的分工：
    ///   · 那个负责「**补 host**」（从进来的请求取，这样反向代理转发过来时
    ///     返回的也是代理那侧的地址）
    ///   · 这个负责「**补签名 query**」
    ///
    /// 拆成两块而不是合成一个，是因为 host 的取法属于**端点**（它要看请求），
    /// 而签名的算法属于**纯函数**（它只看 name + key）—— 后者要能被单测直接调。
    ///
    /// @param url       已经带 host 的 URL（`MediaUrl` 的产物）
    /// @param apiKey    API key；为空时**原样返回**（网关没开鉴权，不需要签名）
    /// @returns 带 `?s=&lt;签名&gt;` 的 URL。URL 里已经带了查询串时用 `&amp;` 续接。
    /// </summary>
    public static string AppendSignature(string url, string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return url;

        // 取文件名部分算签名 —— 与 IsSignedMediaRequest 用同一个前缀常量，
        // 保证「签发」和「校验」不可能漂移。
        var index = url.IndexOf(PathPrefix, StringComparison.Ordinal);
        if (index < 0) return url;

        var name = url[(index + PathPrefix.Length)..];

        // URL 后面可能已经带了 query（虽然当前调用方不会这样，但别假设）
        var queryStart = name.IndexOf('?', StringComparison.Ordinal);
        if (queryStart >= 0) name = name[..queryStart];

        if (name.Length == 0) return url;

        var signature = SignName(name, apiKey);
        if (signature.Length == 0) return url;

        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}{QueryName}={signature}";
    }
}
