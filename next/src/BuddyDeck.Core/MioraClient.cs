using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Core;

/// <summary>Miora 相关的错误。<see cref="Code"/> 决定 HTTP 状态码。</summary>
public sealed class MioraException : Exception
{
    public MioraException(string code, string message) : base(message) => Code = code;

    public string Code { get; }

    /// <summary>映射成 HTTP 状态码（对齐旧版 `miora.js` 的 MioraError.status）。</summary>
    public int Status => Code switch
    {
        MioraErrorCodes.AuthMissingToken or MioraErrorCodes.AuthTempKeyFailed => 401,
        MioraErrorCodes.Timeout => 504,
        _ => 502,
    };

    /// <summary>映射成 OpenAI 的错误类型 —— 客户端按这个显示文案。</summary>
    public string OpenAiType => Code switch
    {
        MioraErrorCodes.AuthMissingToken or MioraErrorCodes.AuthTempKeyFailed => "authentication_error",
        MioraErrorCodes.Timeout => "timeout_error",
        _ => "upstream_error",
    };
}

/// <summary>错误码常量。**字符串值和旧版逐字一致** —— 日志和排查习惯靠它对上。</summary>
public static class MioraErrorCodes
{
    public const string AuthMissingToken = "AUTH_MISSING_TOKEN";
    public const string AuthTempKeyFailed = "AUTH_TEMPKEY_FAILED";
    public const string Timeout = "TIMEOUT";
    public const string Network = "NETWORK";
    public const string UpstreamError = "UPSTREAM_ERROR";
    public const string DownloadFailed = "DOWNLOAD_FAILED";
    public const string InvalidRequest = "INVALID_REQUEST";
}

/// <summary>一个生成出来的文件（上游给的是 1 小时失效的 COS 签名 URL）。</summary>
/// <param name="Url">签名 URL。</param>
/// <param name="MimeType">MIME 类型。</param>
/// <param name="FileKey">上游的文件键（有些结果带，回填给模型时有用）。</param>
/// <param name="ExpiresAtUnixMs">过期时刻（0 = 上游没给）。</param>
public sealed record MediaFile(string Url, string MimeType, string FileKey, long ExpiresAtUnixMs);

/// <summary>一次生成任务的结果。</summary>
public sealed record MediaTaskResult
{
    public required string TaskId { get; init; }
    public string Model { get; init; } = "";
    public string Provider { get; init; } = "";

    /// <summary>上游报的状态：completed / failed / cancelled / pending …</summary>
    public string Status { get; init; } = "completed";

    /// <summary>
    /// 任务跑了但没出图时的原因。
    ///
    /// ⚠ 它**与「抛异常」是两件事**：上游同步模式下可能带着 failed 回来，
    /// 那时候把它抛成异常会让调用方只能给用户一个笼统的错误；返回
    /// `Files` 为空 + 这个字段，调用方才能说清「任务跑了但没出图，原因是 X」。
    /// </summary>
    public string? ErrorMessage { get; init; }

    public IReadOnlyList<MediaFile> Files { get; init; } = [];
}

/// <summary>下载到本地的结果。</summary>
/// <param name="FileName">内容哈希 + 扩展名（见 <see cref="MioraClient.DownloadAsync"/>）。</param>
/// <param name="Bytes">字节数。</param>
/// <param name="MimeType">MIME。</param>
public sealed record DownloadedMedia(string FileName, long Bytes, string MimeType);

/// <summary>
/// Miora —— WorkBuddy 的生图 / 改图 / 生视频服务。
///
/// ## 认证链路（两层 key）
///
/// ```
/// ① 保险库里的登录 JWT（我们本来就有）
///      ↓ POST {billingBase}/v2/api-keys
/// ② 一个 15 分钟有效的临时 key（ck_t_...）
///      ↓ 后续所有 Miora 请求都用它
/// ③ https://public.miora.qq.com/api/ai/workbuddy-proxy/*
/// ```
///
/// **关键是第 ① 步**：tempKey 是用 JWT 换的，不需要桌面 App 在跑 ——
/// 所以网关这个独立进程能自己完成整条链路。
///
/// ## 生成的媒体放哪
///
/// Miora 返回的是腾讯云 COS 的**签名 URL，1 小时失效**。直接转给客户端有两个问题：
///   · 客户端存下来晚点再看，链接已经失效
///   · URL 很长（带签名参数），**有些客户端会截断**
///
/// 所以默认**下载到本地**、返回网关自己的 URL（不过期）。要省那一次下载可以显式
/// 要求直接转发上游 URL，但那样就会过期。
///
/// ## 为什么用「惰性凭证」
///
/// 和多账号池同一个理由：账号可以在运行时切（轮询 / 用户手动换号），
/// 每次要用时现取才能跟上。**不要**在构造时把 token 抓下来存着。
/// </summary>
public sealed partial class MioraClient
{
    /// <summary>Miora 的正式域名。</summary>
    public const string DefaultMioraBase = "https://public.miora.qq.com";

    /// <summary>换 tempKey 的路径（App 源码里的 `CLIENT_TEMPKEY_API_PATH`）。</summary>
    private const string TempKeyPath = "/v2/api-keys";

    /// <summary>审计名 —— 上游用它标识「这个 key 是给谁用的」。**必须和 App 一致**。</summary>
    private const string TempKeyAuditName = "workbuddy-connect-cloud-service";

    /// <summary>Miora 的 API 前缀。</summary>
    public const string MioraApiBase = "/api/ai/workbuddy-proxy";

    /// <summary>tempKey 的 TTL（上游给 900 秒）。</summary>
    private const int TempKeyTtlSeconds = 900;

    /// <summary>提前这么久就重取 —— 撞在上游刚好过期那一秒上很难查。</summary>
    private static readonly TimeSpan TempKeyRefreshMargin = TimeSpan.FromMinutes(1);

    /// <summary>单次提交超时。生图是同步阻塞的，给宽松点。</summary>
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan MiscTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 轮询超时。**图片和视频差很多** —— 视频要渲染几百帧，
    /// 用图片那个超时会让它必然失败。
    /// </summary>
    public static readonly TimeSpan ImagePollTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan VideoPollTimeout = TimeSpan.FromMinutes(15);

    /// <summary>轮询间隔（旧版图片 2.5 秒、视频 3 秒，这里统一取 3 秒）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;
    private readonly Func<Credential> _credential;
    private readonly string _billingBase;
    private readonly string _mioraBase;
    private readonly string _mediaDirectory;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    private readonly object _gate = new();
    private TempKey? _tempKey;

    /// <param name="http">用来打上游的 HttpClient。</param>
    /// <param name="credential">**惰性**取当前账号凭据（每次要用时现取）。</param>
    /// <param name="variant">变体 —— 决定换 tempKey 用哪个域名。</param>
    /// <param name="mediaDirectory">生成的媒体落盘目录。</param>
    /// <param name="clock">时钟（测试用假时钟时也走这里）。</param>
    /// <param name="log">日志。</param>
    /// <param name="mioraBase">覆盖 Miora 域名（测试用）。</param>
    public MioraClient(
        HttpClient http,
        Func<Credential> credential,
        UpstreamVariant variant,
        string mediaDirectory,
        TimeProvider clock,
        ILogger log,
        string? mioraBase = null)
    {
        _http = http;
        _credential = credential;
        _billingBase = variant.BillingBase.TrimEnd('/');
        _mioraBase = (mioraBase ?? DefaultMioraBase).TrimEnd('/');
        _mediaDirectory = mediaDirectory;
        _clock = clock;
        _log = log;
    }

    /// <summary>媒体落盘目录。端点/测试要读文件时用。</summary>
    public string MediaDirectory => _mediaDirectory;

    [LoggerMessage(Level = LogLevel.Debug, Message = "[miora] 临时凭据已获取，{Seconds} 秒后过期")]
    private partial void LogTempKeyAcquired(int seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "[miora] 临时凭据失效，重新获取")]
    private partial void LogTempKeyInvalid();

    [LoggerMessage(Level = LogLevel.Debug, Message = "[miora] {Kind} 轮询第 {Attempt} 次：{Status}")]
    private partial void LogPoll(string kind, int attempt, string status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[miora] 轮询第 {Attempt} 次失败：{Message}")]
    private partial void LogPollFailed(int attempt, string message);

    // ── 临时凭据 ──────────────────────────────────────────────

    /// <summary>
    /// 拿一个可用的 tempKey。
    ///
    /// 缓存规则：
    ///   · 同一个 JWT 且没过期 → 复用
    ///   · JWT 变了（切号了）→ 重取
    ///   · 快过期了 → 重取
    /// </summary>
    /// <param name="force">强制重取（上游说 key 无效时用）。</param>
    public async Task<string> GetTempKeyAsync(bool force = false, CancellationToken ct = default)
    {
        var credential = _credential();
        var token = credential.AccessToken;
        if (string.IsNullOrEmpty(token))
        {
            throw new MioraException(MioraErrorCodes.AuthMissingToken, "保险库里没有可用的登录凭据");
        }

        // 缓存命中判断在锁外先看一眼（快路径不加锁）
        if (!force && TryGetCachedKey(token, out var cached)) return cached;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_billingBase}{TempKeyPath}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("User-Agent", "WorkBuddy/5.6.2");

        // 企业账号要带上，个人账号没有这个字段 —— 实测两种都行
        if (!string.IsNullOrEmpty(credential.EnterpriseId))
        {
            request.Headers.TryAddWithoutValidation("X-Enterprise-Id", credential.EnterpriseId);
        }

        var body = new JsonObject
        {
            ["name"] = TempKeyAuditName,
            ["expire_in_seconds"] = TempKeyTtlSeconds,
            ["temporary"] = true,
        };

        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        // ⚠ 换 tempKey 这一步的 401/403 要映射成 AUTH_TEMPKEY_FAILED（→ 401），
        //   **不是**笼统的 UPSTREAM_ERROR（→ 502）。
        //
        //   为什么较真：这两者对用户是完全不同的两件事 ——
        //     401 = 「你的登录凭据失效了，去桌面 App 重新登录」
        //     502 = 「上游挂了，等会儿再试」
        //   混成 502 会让用户反复重试一个永远不会好的操作（实测踩到：
        //   上游回 401 时拿到的却是 502）。
        var text = await SendAsync(request, MiscTimeout, ct, authFailureIsTempKey: true).ConfigureAwait(false);
        var payload = ParseJson(text, "换临时凭据");

        var key = payload?["data"]?["key"]?.GetValue<string>();
        if (string.IsNullOrEmpty(key) || !key.StartsWith("ck_t_", StringComparison.Ordinal))
        {
            throw new MioraException(
                MioraErrorCodes.AuthTempKeyFailed,
                $"上游没返回合法的临时凭据：{Truncate(text, 300)}");
        }

        var expiresAt = ReadExpiry(payload?["data"]?["expires_at"]?.GetValue<string>());

        lock (_gate) _tempKey = new TempKey(key, expiresAt, token);

        if (_log.IsEnabled(LogLevel.Debug))
        {
            var secondsLeft = (int)Math.Max(0, (expiresAt - _clock.GetUtcNow()).TotalSeconds);
            LogTempKeyAcquired(secondsLeft);
        }

        return key;
    }

    /// <summary>缓存命中检查。锁内读，避免另一个线程正在刷新时读到半状态。</summary>
    private bool TryGetCachedKey(string token, out string key)
    {
        key = "";
        lock (_gate)
        {
            var cached = _tempKey;
            if (cached is null) return false;
            if (!string.Equals(cached.ForToken, token, StringComparison.Ordinal)) return false;
            if (cached.ExpiresAt - _clock.GetUtcNow() <= TempKeyRefreshMargin) return false;

            key = cached.Key;
            return true;
        }
    }

    /// <summary>
    /// 读上游给的 `expires_at`。
    ///
    /// 上游给的是 ISO 字符串；读不出来就按 TTL 自己算 —— **不抛异常**，
    /// 一个读不准的过期时间不该让整个生图失败。
    /// </summary>
    private DateTimeOffset ReadExpiry(string? raw)
    {
        if (!string.IsNullOrEmpty(raw) &&
            DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        return _clock.GetUtcNow().AddSeconds(TempKeyTtlSeconds);
    }

    // ── 提交 / 轮询 ───────────────────────────────────────────

    /// <summary>
    /// 提交一个生成任务，等到终态。
    ///
    /// 上游有两种模式：
    ///   · `sync`  —— 提交时就带着结果回来（生图通常是这样）
    ///   · `async` —— 只给任务 id，要轮询（视频通常是这种）
    ///
    /// 这里统一处理：sync 直接用返回结果，async 才轮询。
    /// </summary>
    /// <param name="kind">image 还是 video。**两者的轮询超时差很多**。</param>
    /// <param name="action">image 才有 edit；生成传 null。</param>
    /// <param name="body">上游要的请求体。</param>
    /// <param name="pollTimeout">覆盖默认轮询超时。</param>
    public async Task<MediaTaskResult> SubmitAsync(
        string kind,
        string? action,
        JsonObject body,
        TimeSpan? pollTimeout = null,
        CancellationToken ct = default)
    {
        var submitted = await PostWithAuthAsync(SubmitPath(kind, action), body, SubmitTimeout, ct)
            .ConfigureAwait(false);

        var data = UnwrapData(submitted);
        var taskId = data?["upstreamTaskId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(taskId))
        {
            throw new MioraException(
                MioraErrorCodes.UpstreamError,
                $"上游没返回任务 id：{Truncate(submitted.ToJsonString(), 300)}");
        }

        // sync：提交即完成，不用再问。
        //
        // 这里也看 failed —— 同步模式可能带着失败回来。那种情况**不抛异常**，
        // 走 Normalize（里面带 errorMessage），让调用方看到「任务跑了但没出图」，
        // 而不是一个笼统的异常。
        var mode = data?["mode"]?.GetValue<string>();
        var status = data?["status"]?.GetValue<string>();
        if (string.Equals(mode, "sync", StringComparison.Ordinal) ||
            string.Equals(status, "completed", StringComparison.Ordinal) ||
            string.Equals(status, "failed", StringComparison.Ordinal))
        {
            return Normalize(data, taskId);
        }

        // async：轮询到终态。视频默认 15 分钟，图片 90 秒。
        var timeout = pollTimeout ?? (kind == "video" ? VideoPollTimeout : ImagePollTimeout);
        var entry = await PollAsync(kind, taskId, timeout, ct).ConfigureAwait(false);

        return Normalize(entry, taskId) with { TaskId = taskId };
    }

    /// <summary>
    /// 只提交、不等待 —— 拿到任务 id 就返回。
    ///
    /// **为什么需要它**：视频太慢了（几分钟到十几分钟）。「提交就走 + 客户端轮询」
    /// 比把 HTTP 连接挂在那儿好得多：
    ///   · 客户端的超时设置通常撑不住十几分钟
    ///   · 网关重启一下连接就断了，任务白跑
    /// </summary>
    public async Task<MediaTaskResult> SubmitRawAsync(string kind, JsonObject body, CancellationToken ct = default)
    {
        var payload = await PostWithAuthAsync(SubmitPath(kind, null), body, SubmitTimeout, ct)
            .ConfigureAwait(false);

        var data = UnwrapData(payload);
        var taskId = data?["upstreamTaskId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(taskId))
        {
            throw new MioraException(
                MioraErrorCodes.UpstreamError,
                $"上游没返回任务 id：{Truncate(payload.ToJsonString(), 300)}");
        }

        return new MediaTaskResult
        {
            TaskId = taskId,
            Model = data?["model"]?.GetValue<string>() ?? "",
            Provider = data?["provider"]?.GetValue<string>() ?? "",
            Status = data?["status"]?.GetValue<string>() ?? "pending",
        };
    }

    /// <summary>
    /// 查一个任务的状态，**不轮询、不下载**（给客户端自己问的场景用）。
    /// </summary>
    public async Task<JsonNode?> QueryAsync(string kind, string taskId, CancellationToken ct = default)
    {
        var payload = await PostWithAuthAsync(
            $"{MioraApiBase}/{kind}/query-task",
            new JsonObject { ["upstreamTaskIds"] = new JsonArray(taskId) },
            MiscTimeout,
            ct).ConfigureAwait(false);

        return PickTaskEntry(UnwrapData(payload), taskId);
    }

    /// <summary>提交路径。video 只有一条；image 按 action 分 generate / edit。</summary>
    public static string SubmitPath(string kind, string? action)
        => kind switch
        {
            "video" => $"{MioraApiBase}/video/submit",
            _ => action == "edit"
                ? $"{MioraApiBase}/image/edit/submit"
                : $"{MioraApiBase}/image/generate/submit",
        };

    /// <summary>轮询路径。</summary>
    private static string QueryPath(string kind) => $"{MioraApiBase}/{kind}/query-task";

    /// <summary>轮询到终态（completed / failed / cancelled）。</summary>
    private async Task<JsonNode?> PollAsync(string kind, string taskId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow().Add(timeout);
        var attempt = 0;

        while (_clock.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            // 间隔等待也要能被取消 —— 用 CancelAfter 而不是 Task.Delay 死等，
            // 这样客户端断开时立刻退出。
            await DelayWhileObservable(deadline, ct).ConfigureAwait(false);

            JsonNode? payload;
            try
            {
                payload = await PostWithAuthAsync(
                    QueryPath(kind),
                    new JsonObject { ["upstreamTaskIds"] = new JsonArray(taskId) },
                    MiscTimeout,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MioraException or HttpRequestException or IOException)
            {
                // 单次查询失败不致命（网络抖一下很正常）—— 接着轮，别放弃整个任务。
                // ⚠ 但**取消要原样抛**，那是客户端走了，不是网络抖动。
                if (_log.IsEnabled(LogLevel.Debug)) LogPollFailed(attempt, ex.Message);
                continue;
            }

            var entry = PickTaskEntry(UnwrapData(payload), taskId);
            var status = entry?["status"]?.GetValue<string>();

            if (_log.IsEnabled(LogLevel.Debug)) LogPoll(kind, attempt, status ?? "未知状态");

            if (status is "completed" or "failed" or "cancelled") return entry;
        }

        throw new MioraException(
            MioraErrorCodes.Timeout,
            $"等待{(kind == "video" ? "视频" : "图片")}生成超时（{(int)timeout.TotalSeconds} 秒）");
    }

    /// <summary>
    /// 轮询间隔。
    ///
    /// 平时等于 <see cref="PollInterval"/>；快到期时缩短到剩余时间，
    /// 避免「明明还剩 1 秒却因为整段间隔睡过头」而报超时。
    /// </summary>
    private async Task DelayWhileObservable(DateTimeOffset deadline, CancellationToken ct)
    {
        var remaining = deadline - _clock.GetUtcNow();
        var wait = remaining < PollInterval ? remaining : PollInterval;
        if (wait <= TimeSpan.Zero) return;

        await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
    }

    // ── 下载与落盘 ────────────────────────────────────────────

    /// <summary>
    /// 把生成结果下载到本地，返回文件名。
    ///
    /// 文件名用**内容哈希** —— 同一张图重复下载会覆盖，不会越积越多。
    /// </summary>
    public async Task<DownloadedMedia> DownloadAsync(string signedUrl, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, signedUrl);

        using var response = await SendRawAsync(request, DownloadTimeout, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new MioraException(
                MioraErrorCodes.DownloadFailed,
                $"下载生成的媒体失败（HTTP {(int)response.StatusCode}）");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var mimeType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
        var fileName = $"{Hash(bytes)}.{ExtensionFor(mimeType)}";

        Directory.CreateDirectory(_mediaDirectory);
        var fullPath = Path.Combine(_mediaDirectory, fileName);

        // 写临时文件再原子替换 —— 列举媒体目录的读者（面板、文件端点）
        // 不会读到写了一半的文件。
        var temp = fullPath + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
        File.Move(temp, fullPath, overwrite: true);

        return new DownloadedMedia(fileName, bytes.LongLength, mimeType);
    }

    /// <summary>读回之前下载过的文件（给 HTTP 端点用）。找不到返回 null。</summary>
    public byte[]? ReadMedia(string fileName)
    {
        // 只允许「哈希.扩展名」这种名字 —— 防止用 ../ 读别的文件。
        // 这道校验放在 Core 而不是端点里，是因为**任何**调用方都该受它保护。
        if (!IsSafeMediaName(fileName)) return null;

        var fullPath = Path.Combine(_mediaDirectory, fileName);
        return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
    }

    /// <summary>文件名合法性：8–64 位十六进制 + 已知扩展名。</summary>
    public static bool IsSafeMediaName(string fileName)
        => System.Text.RegularExpressions.Regex.IsMatch(
            fileName, @"^[a-f0-9]{8,64}\.(png|jpg|jpeg|webp|mp4)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>按扩展名回 MIME（文件端点的 Content-Type）。</summary>
    public static string MimeForFileName(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            _ => "image/png",
        };

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..24];

    private static string ExtensionFor(string mimeType)
        => mimeType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg"
            : mimeType.Contains("webp", StringComparison.OrdinalIgnoreCase) ? "webp"
            : mimeType.Contains("mp4", StringComparison.OrdinalIgnoreCase) ? "mp4"
            : "png";

    // ── 内部 ──────────────────────────────────────────────────

    /// <summary>
    /// 带认证的 POST。tempKey 失效时**自动重取一次**。
    ///
    /// 为什么要自动重取：tempKey 只有 15 分钟。缓存再准也可能赶上网关休眠、
    /// 时钟跳变之类的边界。上游会明确回 `AUTH_INVALID_TEMP_KEY`，
    /// 那时重取是确定正确的动作 —— **不该把这个错误抛给用户让他重试**。
    /// </summary>
    private async Task<JsonNode> PostWithAuthAsync(string path, JsonObject body, TimeSpan timeout, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var tempKey = await GetTempKeyAsync(force: attempt > 0, ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_mioraBase}{path}");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {tempKey}");
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            var text = await SendAsync(request, timeout, ct).ConfigureAwait(false);
            var payload = ParseJson(text, "上游");

            // 上游的错误码可能藏在 code 字段里（HTTP 200 但业务失败）
            var code = payload["code"]?.ToString() ?? "0";
            if (code != "0")
            {
                if (code.Contains("AUTH_INVALID_TEMP_KEY", StringComparison.Ordinal) ||
                    code.Contains("AUTH_MISSING_TEMP_KEY", StringComparison.Ordinal))
                {
                    LogTempKeyInvalid();
                    continue;   // 下一轮 force 重取
                }

                throw new MioraException(
                    MioraErrorCodes.UpstreamError,
                    payload["msg"]?.GetValue<string>()
                        ?? payload["message"]?.GetValue<string>()
                        ?? $"上游错误码 {code}");
            }

            return payload;
        }

        throw new MioraException(MioraErrorCodes.AuthTempKeyFailed, "重新获取临时凭据后仍然失败");
    }

    /// <summary>
    /// 发一次请求，非 2xx 转成 <see cref="MioraException"/>，超时转成 TIMEOUT。
    /// </summary>
    /// <param name="authFailureIsTempKey">
    /// 401/403 该不该算「凭据失效」（→ AUTH_TEMPKEY_FAILED / 401）。
    ///
    /// 换 tempKey 那一步要传 true（是登录凭据的问题，用户得重新登录）；
    /// 别的步骤传 false —— 那里拿到 401 说明**临时凭据**有问题，
    /// 由 <see cref="PostWithAuthAsync"/> 的自动重取逻辑处理。
    /// </param>
    private async Task<string> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken ct,
        bool authFailureIsTempKey = false)
    {
        using var response = await SendRawAsync(request, timeout, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;

            // 401/403 是「凭据问题」，不是「上游故障」—— 两者对用户的
            // 处置完全不同（重新登录 vs 等会儿再试），别混成一个 502。
            var code = authFailureIsTempKey && status is 401 or 403
                ? MioraErrorCodes.AuthTempKeyFailed
                : MioraErrorCodes.UpstreamError;

            throw new MioraException(code, $"上游返回 HTTP {status}：{Truncate(text, 300)}");
        }

        return text;
    }

    /// <summary>
    /// 发送 + 超时归类。
    ///
    /// ⚠ 区分「调用方取消」和「我们自己的超时」：前者原样抛
    /// <see cref="OperationCanceledException"/>（客户端走了，不是错误），
    /// 后者转成 TIMEOUT（504）。
    /// </summary>
    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 客户端取消 —— 原样抛
        }
        catch (OperationCanceledException)
        {
            throw new MioraException(
                MioraErrorCodes.Timeout,
                $"请求超时（{(int)timeout.TotalSeconds} 秒）");
        }
        catch (HttpRequestException ex)
        {
            throw new MioraException(MioraErrorCodes.Network, $"网络错误：{ex.Message}");
        }
    }

    private static JsonObject ParseJson(string text, string what)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject
                ?? throw new MioraException(MioraErrorCodes.UpstreamError, $"{what}返回了意外的结构（不是对象）");
        }
        catch (JsonException)
        {
            throw new MioraException(MioraErrorCodes.UpstreamError, $"{what}返回的不是 JSON：{Truncate(text, 300)}");
        }
    }

    private static JsonNode? UnwrapData(JsonNode payload) => payload["data"] ?? payload;

    /// <summary>
    /// 从上游返回里挑出那个 task 条目。
    ///
    /// 上游的返回结构有两种（实测都见过）：
    ///   · `data` 直接是那个 task 对象
    ///   · `data` 是 `{ "&lt;taskId&gt;": {...} }` 的映射 —— query-task 用的是后者
    /// </summary>
    private static JsonNode? PickTaskEntry(JsonNode? data, string taskId)
    {
        if (data is JsonArray array) return array.Count > 0 ? array[0] : null;
        if (data?["tasks"] is JsonArray tasks) return tasks.Count > 0 ? tasks[0] : null;
        if (data?[taskId] is JsonNode byId) return byId;
        return data;
    }

    /// <summary>把上游的 task 对象转成我们的结果形状。</summary>
    private static MediaTaskResult Normalize(JsonNode? data, string taskId)
    {
        var files = data?["resultFiles"] as JsonArray ?? data?["result_files"] as JsonArray;        var list = new List<MediaFile>();

        if (files is not null)
        {
            foreach (var item in files)
            {
                if (item is not JsonObject file) continue;

                var url = file["signedUrl"]?.GetValue<string>() ?? file["url"]?.GetValue<string>() ?? "";
                if (url.Length == 0) continue;

                list.Add(new MediaFile(
                    url,
                    file["mimeType"]?.GetValue<string>() ?? file["mime_type"]?.GetValue<string>() ?? "image/png",
                    file["fileKey"]?.GetValue<string>() ?? file["file_key"]?.GetValue<string>() ?? "",
                    ReadLong(file["expiresAt"]) ?? ReadLong(file["expires_at"]) ?? 0));
            }
        }

        return new MediaTaskResult
        {
            TaskId = taskId,
            Model = data?["model"]?.GetValue<string>() ?? "",
            Provider = data?["provider"]?.GetValue<string>() ?? "",
            Status = data?["status"]?.GetValue<string>() ?? "completed",
            ErrorMessage = data?["errorMessage"]?.GetValue<string>() ?? data?["error_message"]?.GetValue<string>(),
            Files = list,
        };
    }

    private static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<string>(out var s) && long.TryParse(s, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>缓存的临时凭据。</summary>
    /// <param name="Key">tempKey 本体。</param>
    /// <param name="ExpiresAt">过期时刻。</param>
    /// <param name="ForToken">它是用哪个 JWT 换的 —— 换号后要重取。</param>
    private sealed record TempKey(string Key, DateTimeOffset ExpiresAt, string ForToken);
}
