using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Core;

/// <summary>一次上游调用的结果。</summary>
public sealed record UpstreamResult
{
    public required bool Ok { get; init; }

    /// <summary>HTTP 状态码。0 表示连接层失败（DNS、超时、TLS）。</summary>
    public required int Status { get; init; }

    /// <summary>错误分类。<see cref="UpstreamErrorKind"/> 的字符串形式，和旧版网关一致。</summary>
    public string? Kind { get; init; }

    /// <summary>错误正文（截断过的）。</summary>
    public string? Message { get; init; }

    /// <summary>成功时的响应流。**调用方负责释放。**</summary>
    public HttpResponseMessage? Response { get; init; }
}

/// <summary>
/// 上游错误分类。
///
/// 为什么分类而不是直接用 HTTP 状态码：上游把很多语义塞在 200 里
/// （业务 code），状态码本身不够用。分类完之后网关才能决定
/// 「该换号」还是「该重试」还是「该直接报错给用户」。
/// </summary>
public static class UpstreamErrorKind
{
    /// <summary>限流。可以换模型或换号重试。</summary>
    public const string RateLimited = "rate_limit";

    /// <summary>模型级限流（HTTP 429 + 业务码 6004）。这个换号有用、换模型也有用。</summary>
    public const string ModelRateLimited = "model_rate_limit";

    /// <summary>凭证失效。要刷新 token 或者让用户重新登录。</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>上游服务端错误。可以重试。</summary>
    public const string Server = "server";

    /// <summary>请求本身有问题。重试没用。</summary>
    public const string BadRequest = "bad_request";

    /// <summary>
    /// 会话已死（token 在，但服务端不再认这个登录态）。
    ///
    /// ## 和 <see cref="Unauthorized"/> 的区别（**不是同义词**）
    ///
    /// ```
    /// unauthorized   401 —— 凭证本身无效（过期、伪造、被撤销）
    ///                → 处置：刷新 token / 让用户重新登录
    /// session_dead   会话在服务端不存在了（HTTP 401/403，或正文带
    ///                "Offline user session not found" / "12153"）
    ///                → 处置：**必须重新登录**，刷新 token 没用
    /// ```
    ///
    /// 分出来是因为**用户看到的提示不同**：一个说"登录过期了，点这里续期"，
    /// 另一个说"登录态已失效，请重新登录" —— 后者刷新是白费力气。
    /// </summary>
    public const string SessionDead = "session_dead";

    /// <summary>
    /// 额度耗尽（**硬够了**，不是限速）。
    ///
    /// 与 <see cref="RateLimited"/> / <see cref="ModelRateLimited"/> 的关键区别：
    /// ```
    /// rate_limit / model_rate_limit   "你太快了" → 等一会儿/换号/换模型 有用
    /// hard_credit                     "你没额度了" → 上面那些全都没用
    /// ```
    /// 拿限流的处置去应付额度耗尽，只会白转一圈再把同样的错误报给用户。
    ///
    /// 判据（对齐旧版 `upstream.js` `classifyUpstreamError`）：
    /// HTTP 402，或正文含 `HARD_CREDIT_MARKERS` 里的任一词。
    /// </summary>
    public const string HardCredit = "hard_credit";

    /// <summary>上游说"这个资源不存在"。</summary>
    public const string NotFound = "not_found";

    /// <summary>其他。</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// 上游 HTTP 客户端。
///
/// ## 职责边界
///
/// 只做「发一个请求、把结果分类」这件事。**不做**：
///   · 换号重试（那是池的活）
///   · 刷新 token（那是凭证管理的活）
///   · 记账（那是 usage 的活）
///
/// 这样它足够薄，能塞假实现进测试。
///
/// ## 为什么状态码 0 是个正经的 kind
///
/// 连接层失败（DNS 解析不了、TLS 握手失败、超时）拿不到 HTTP 状态码。
/// 旧版网关用 `status: 0` 表示，这里保留 —— 因为「连不上」和「连上了但被拒」
/// 对用户是完全不同的两件事，提示语也不一样。
/// </summary>
public sealed partial class UpstreamClient
{
    private readonly HttpClient _http;
    private readonly UpstreamVariant _variant;
    private readonly ILogger<UpstreamClient> _log;

    /// <summary>错误正文最多读这么多字节。上游出错时可能吐一大坨 HTML。</summary>
    private const int ErrorBodyLimit = 4096;

    // 日志用源生成的委托，不用 LogWarning(...) 那种带 params object[] 的重载 ——
    // 后者每次调用都要装箱参数、分配数组，在热路径上（每个请求至少一次）不划算。
    // 分析器 CA1848 会强制这一点。
    [LoggerMessage(Level = LogLevel.Warning, Message = "上游连接失败（{Kind}）")]
    private partial void LogTransportFailure(Exception ex, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "上游 {Status} → {Kind}: {Body}")]
    private partial void LogUpstreamError(int status, string kind, string body);

    /// <summary>
    /// 客户端身份（UA 里的版本号）。
    ///
    /// ## 为什么需要它
    ///
    /// 上游会校验 UA 里的版本号。实测：真实版本（5.8.3）通过，
    /// 旧版本（5.5.6）被拦。所以**必须带真实版本**，不能带 null。
    ///
    /// 传 null 表示「用 CLI 形态」—— 那条路能通但没有桌面身份，
    /// 风控收紧时最先被拦的就是它。生产环境应该总是传。
    /// </summary>
    private readonly ChatIdentity? _identity;

    public UpstreamClient(
        HttpClient http,
        UpstreamVariant variant,
        ILogger<UpstreamClient> log,
        ChatIdentity? identity = null)
    {
        _http = http;
        _variant = variant;
        _log = log;
        _identity = identity;
    }

    /// <summary>
    /// 发一次聊天请求（流式）。
    ///
    /// @param preparedBody 已经过 <see cref="RequestPipeline"/> 处理的 JSON 文本。
    /// @param userAgent    桌面身份 UA。null 用 CLI 形态。
    /// @param clientVersion 跟 UA 配套的版本号。
    /// </summary>
    public async Task<UpstreamResult> ChatStreamAsync(
        Credential credential,
        string preparedBody,
        string? userAgent,
        string? clientVersion,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_variant.ChatBase}/v2/chat/completions");

        request.Content = new StringContent(preparedBody, Encoding.UTF8, "application/json");

        // 调用方没指定身份时用探测到的真实身份 —— 这是风控能否通过的关键。
        // 显式传 null 的调用方（比如探活）仍然可以不带 UA。
        var ua = userAgent;
        var version = clientVersion;
        if (ua is null && _identity is not null)
        {
            ua = _identity.UserAgent(_variant);
            version = _identity.ClientVersion;
        }

        UpstreamHeaders.ApplyChat(request, credential, _variant, ua, version);

        return await SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 刷新 token。
    ///
    /// ## 滑动窗口是关键
    ///
    /// 服务端每次刷新都会重新给一个**满额**的 refresh 窗口
    /// （`refreshExpiresIn`）。把这个值记下来才能判断「还能续多久」、
    /// 什么时候该主动续 —— 不记的话只能等过期，那就没法做自动续期了。
    /// </summary>
    public async Task<RefreshOutcome> RefreshTokenAsync(Credential credential, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_variant.ChatBase}/v2/plugin/auth/token/refresh");

        UpstreamHeaders.ApplyRefresh(request, credential, _variant);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var result = await SendAsync(request, ct).ConfigureAwait(false);
        if (!result.Ok || result.Response is null)
        {
            throw new UpstreamException(result.Status, result.Kind ?? UpstreamErrorKind.Unknown, result.Message ?? "");
        }

        var envelope = await ReadEnvelopeAsync(result.Response, ct).ConfigureAwait(false);
        if (envelope.Code != 0)
        {
            throw new UpstreamException(
                result.Status, UpstreamErrorKind.Unknown,
                $"刷新返回业务错误码 {envelope.Code}: {envelope.Message}");
        }

        var data = envelope.Data;
        var accessToken = data?["accessToken"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new UpstreamException(
                result.Status, UpstreamErrorKind.Unknown,
                "刷新没有返回 accessToken —— 要在 WorkBuddy App 里重新登录");
        }

        return new RefreshOutcome
        {
            AccessToken = accessToken,
            RefreshToken = data?["refreshToken"]?.GetValue<string>() ?? "",
            ExpiresInSec = IntOf(data, "expiresIn"),
            RefreshExpiresInSec = IntOf(data, "refreshExpiresIn"),
            Domain = data?["domain"]?.GetValue<string>() ?? "",
        };
    }

    /// <summary>拉模型目录（`/v3/config`）。</summary>
    public async Task<JsonNode?> FetchCatalogAsync(Credential credential, string? userAgent, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_variant.BillingBase}{_variant.CatalogPath}");

        // 注意：**不带 IDE 归属族** —— 那是 chat 和探活专属的。
        // 混进目录请求会把用量归属算乱。
        if (!string.IsNullOrEmpty(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.AccessToken}");

        var result = await SendAsync(request, ct).ConfigureAwait(false);
        if (!result.Ok || result.Response is null)
        {
            throw new UpstreamException(result.Status, result.Kind ?? UpstreamErrorKind.Unknown, result.Message ?? "");
        }

        var text = await result.Response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        result.Response.Dispose();

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new UpstreamException(result.Status, UpstreamErrorKind.Unknown, $"目录不是合法 JSON：{ex.Message}");
        }
    }

    /// <summary>
    /// 查积分（额度）—— `POST /v2/billing/meter/get-user-resource`。
    ///
    /// 对齐旧版 `upstream.js:750` `fetchCredits`。**只做 HTTP 与解信封**，
    /// 具体字段的解析交给 <see cref="Credits.Parse"/>（纯函数、可穷举测试）。
    ///
    /// @param credential 凭证（复用既有链路，头走 <see cref="UpstreamHeaders.ApplyBilling"/>）。
    /// @param userAgent  UA。
    ///        ⚠ **这个接口很可能必须带 UA** —— t48 实测 `/v3/config` 缺 UA 会回
    ///        `{"code":12403,"msg":"check ua, get coding copilot version error"}`。
    ///        积分接口**没有单独实测过 UA 敏感性**（那是一次未验的推断），
    ///        所以这里**按同样的保守做法要求传**：传了不会有副作用，不传
    ///        万一它也要 UA，症状是「查不到积分」而不是一个清晰的错误。
    /// @param now  查询的"现在"（决定时间窗口）。**显式传入** —— 内部读时钟会让调用不可测。
    /// @param ct   取消令牌。
    ///
    /// @returns 解析后的积分快照。
    /// @throws UpstreamException 上游返回错误（含分类）。**解析失败不抛** ——
    ///         形状不对时 <see cref="Credits.Parse"/> 会返回空快照，
    ///         因为"余额显示为 0"比"面板炸掉"好。
    /// </summary>
    public async Task<CreditsSnapshot> FetchCreditsAsync(
        Credential credential,
        string? userAgent,
        DateTimeOffset now,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            // ⚠ 走 **BillingBase** 而不是 ChatBase —— 国内版是
            //   codebuddy.cn / copilot.tencent.com 两个域名，用错就 404。
            $"{_variant.BillingBase}{Credits.ResourcePath}");

        // 与 chat 一样带 UA，但**不带 X-IDE-* 归属族** ——
        // 那是 chat 和探活专属的，混进计费请求会把用量归属算乱。
        if (!string.IsNullOrEmpty(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        // 复用既有的计费头（Authorization / X-Product / X-Enterprise-Id）
        UpstreamHeaders.ApplyBilling(request, credential);

        var body = Credits.BuildQueryBody(now).ToJsonString();
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var result = await SendAsync(request, ct).ConfigureAwait(false);
        if (!result.Ok || result.Response is null)
        {
            throw new UpstreamException(
                result.Status,
                ClassifyCreditFailure(result.Status, result.Message ?? ""),
                result.Message ?? "");
        }

        var text = await result.Response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        result.Response.Dispose();

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new UpstreamException(
                result.Status, UpstreamErrorKind.Unknown, $"积分接口不是合法 JSON：{ex.Message}");
        }

        // ⚠ 信封里的业务 code 也要判 —— 这个接口会用 **HTTP 200 + code!=0**
        //   表达业务错误（和签到同款）。只看 HTTP 状态会把失败当成功，
        //   然后解析出一个空快照 ⇒ 界面显示「余额 0」，而实际是我们的凭证/额度有问题。
        var code = IntAt(root, "code");
        if (code is not null and not 0)
        {
            var message = StringAt(root, "msg");
            if (message.Length == 0) message = StringAt(root, "message");
            throw new UpstreamException(
                result.Status,
                ClassifyCreditFailure(result.Status, $"{message} code={code}"),
                message.Length > 0 ? message : $"积分接口返回业务错误码 {code}");
        }

        return Credits.Parse(root);
    }

    /// <summary>
    /// 积分请求的失败分类。对齐旧版 `classifyUpstreamError`（`upstream.js`）。
    ///
    /// **公开**（而不是 private）：网关侧把 <see cref="UpstreamException.Kind"/>
    /// 映射成 HTTP 状态码时要按同一张表判断，各写一份就会出现
    /// 「Core 说 session_dead、网关按 unauthorized 处理」这类不一致。
    ///
    /// ## 判定顺序是语义的一部分（照抄旧版，连注释里的理由一起）
    ///
    /// ```
    /// ① 402                     → hard_credit
    /// ② 401                     → session_dead   ← 不看正文
    /// ③ 正文含硬额度标记          → hard_credit
    /// ④ 正文含会话失效标记        → session_dead
    /// ⑤ 429                     → soft_rate
    /// ⑥ 404                     → not_found
    /// ⑦ >=500                   → server
    /// ⑧ 其余                     → client
    /// ```
    ///
    /// **① ② 必须在正文匹配之前**，旧版注释的原话：
    ///
    /// > A 401 is an auth failure whatever its body says: there is no business
    /// > meaning for it on this upstream… Classification happens before the
    /// > credit markers on purpose, so a bare 401 whose body happens to name
    /// > credits still reports `session_dead`.
    ///
    /// 也就是说：**正文里出现"credit"字样不该把 401 变成额度问题** ——
    /// 一个错误页顺口提到 credits，就会让凭证失效被误报成"没额度"，
    /// 而这两者的处置完全相反（重新登录 vs 什么都做不了）。
    /// </summary>
    public static string ClassifyCreditFailure(int status, string body)
    {
        // ⚠ status == 0 是**连接层失败**（DNS / TLS / 超时）—— 不是"请求有问题"。
        //    它在 `SendAsync` 里被编成 status 0（沿用旧版网关的约定：
        //    「连不上」和「连上了但被拒」对用户是完全不同的两件事）。
        //    漏掉这一条会让"连不上上游"被报成 bad_request ⇒ 上游决定**不重试**，
        //    而连接失败恰恰是最该重试的一类。
        if (status == 0) return UpstreamErrorKind.Server;

        if (status == 402) return UpstreamErrorKind.HardCredit;
        if (status == 401) return UpstreamErrorKind.SessionDead;

        var lower = body.ToLowerInvariant();

        // 硬额度标记：ASCII 小写 + 原文中文
        foreach (var marker in HardCreditMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal)
                || body.Contains(marker, StringComparison.Ordinal))
            {
                return UpstreamErrorKind.HardCredit;
            }
        }

        foreach (var marker in SessionDeadMarkers)
        {
            if (body.Contains(marker, StringComparison.Ordinal)) return UpstreamErrorKind.SessionDead;
        }

        if (status == 429) return UpstreamErrorKind.RateLimited;
        if (status == 404) return UpstreamErrorKind.NotFound;
        if (status >= 500) return UpstreamErrorKind.Server;
        return UpstreamErrorKind.BadRequest;   // 旧版叫 "client"
    }

    /// <summary>
    /// 额度不足的特征词。**ASCII 小写 + 原文中文两套都要**。
    ///
    /// 旧版就是这么做的（`HARD_CREDIT_MARKERS` = 英文若干 + 中文原文），
    /// 因为上游对同一个语义会按语言回不同的文案。
    /// 只写英文会漏掉中文响应，只写中文会漏掉英文响应。
    /// </summary>
    private static readonly string[] HardCreditMarkers =
    [
        "insufficient credit",
        "no credit",
        "credit exhausted",
        "额度不足",
        "余额不足",
        "积分不足",
        "额度已用完",
    ];

    /// <summary>
    /// 会话失效的特征词。
    ///
    /// `12153` 是个业务码 —— 上游把"离线会话"表达成这个码。
    /// 旧版把它和那句英文并列检查（`upstream.js:51`），这里保持一致。
    /// </summary>
    private static readonly string[] SessionDeadMarkers =
    [
        "Offline user session not found",
        "12153",
    ];

    // ── 内部 ──────────────────────────────────────────────────

    private async Task<UpstreamResult> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            // ResponseHeadersRead：不要缓冲整个响应体。
            // 聊天是流式的，缓冲等于把流式的意义抹掉。
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 客户端主动断开 —— 不是错误，原样抛让上层知道
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // 连接层失败：DNS / TLS / 超时 / 连接被重置
            LogTransportFailure(ex, ex.GetType().Name);
            return new UpstreamResult
            {
                Ok = false,
                Status = 0,
                Kind = UpstreamErrorKind.Server,
                Message = $"transport error: {ex.Message}",
            };
        }

        if (response.IsSuccessStatusCode)
        {
            return new UpstreamResult { Ok = true, Status = (int)response.StatusCode, Response = response };
        }

        // 失败：读一小段正文用于分类和报错
        var body = "";
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            body = raw.Length > ErrorBodyLimit ? raw[..ErrorBodyLimit] : raw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // 连错误正文都读不到 —— 不影响分类（状态码够了）
        }
        finally
        {
            response.Dispose();
        }

        var status = (int)response.StatusCode;
        var kind = Classify(status, body);

        LogUpstreamError(status, kind, Truncate(body, 200));

        return new UpstreamResult { Ok = false, Status = status, Kind = kind, Message = body };
    }

    /// <summary>
    /// 按状态码 + 正文分类。
    ///
    /// 为什么要看正文：上游把限流的细分语义塞在业务 code 里 ——
    /// 429 + `code: 6004` 是**模型级**限流（换个模型就好），
    /// 而普通的 429 是整个账号都在限流（要换号）。分不清就只能一律换号，
    /// 那会白白浪费账号配额。
    /// </summary>
    private static string Classify(int status, string body)
    {
        if (status == 429)
        {
            // 模型级限流：换模型能解决
            if (body.Contains("6004", StringComparison.Ordinal))
            {
                return UpstreamErrorKind.ModelRateLimited;
            }
            return UpstreamErrorKind.RateLimited;
        }

        if (status is 401 or 403) return UpstreamErrorKind.Unauthorized;
        if (status >= 500) return UpstreamErrorKind.Server;
        if (status >= 400) return UpstreamErrorKind.BadRequest;

        return UpstreamErrorKind.Unknown;
    }

    /// <summary>
    /// 读上游的统一信封 `{code, message, data}`。
    ///
    /// 上游有两种回法：直接给数据，或者包一层信封。两种都要认 ——
    /// 包不包取决于接口，不是我们能选的。
    /// </summary>
    private static async Task<Envelope> ReadEnvelopeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        response.Dispose();

        try
        {
            var node = JsonNode.Parse(text);
            if (node is not JsonObject obj) return new Envelope(0, "", null);

            var code = IntOf(obj, "code") ?? 0;
            var message = obj["message"]?.GetValue<string>() ?? obj["msg"]?.GetValue<string>() ?? "";
            var data = obj["data"] as JsonObject ?? obj;
            return new Envelope(code, message, data);
        }
        catch (JsonException)
        {
            throw new UpstreamException(200, UpstreamErrorKind.Unknown, "上游返回的不是合法 JSON");
        }
    }

    private static int? IntOf(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        return null;
    }

    /// <summary>从一个可能为 null 的根节点上读整数。读不出返回 null。</summary>
    private static int? IntAt(JsonNode? root, string name)
        => root is JsonObject obj ? IntOf(obj, name) : null;

    /// <summary>从一个可能为 null 的根节点上读字符串。读不出返回空串。</summary>
    private static string StringAt(JsonNode? root, string name)
        => root is JsonObject obj && obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private sealed record Envelope(int Code, string Message, JsonObject? Data);
}

/// <summary>刷新 token 拿到的东西。</summary>
public sealed record RefreshOutcome
{
    public required string AccessToken { get; init; }

    /// <summary>上游有时会轮换 refresh token。空表示沿用旧的。</summary>
    public string RefreshToken { get; init; } = "";

    /// <summary>access token 有效期（秒）。</summary>
    public int? ExpiresInSec { get; init; }

    /// <summary>
    /// refresh token 的有效期（秒）。
    ///
    /// **滑动窗口的核心** —— 每次刷新服务端都重新给满额。记下它才能
    /// 判断「还能续多久」，进而决定什么时候主动续期。
    /// </summary>
    public int? RefreshExpiresInSec { get; init; }

    public string Domain { get; init; } = "";
}

/// <summary>上游调用失败。</summary>
public sealed class UpstreamException : Exception
{
    public UpstreamException(int status, string kind, string message) : base(message)
    {
        Status = status;
        Kind = kind;
    }

    public int Status { get; }
    public string Kind { get; }
}
