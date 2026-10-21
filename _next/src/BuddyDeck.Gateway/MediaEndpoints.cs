using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 媒体端点族：生图 / 改图 / 生视频 / 图生视频，以及文件回读。
///
/// ## 端点清单（对齐旧版 `gateway.js` 的路由表）
///
/// ```
/// POST /v1/images/generations      文生图（OpenAI 形状）
/// POST /v1/images/edits            改图（同时接受 JSON 与 multipart）
/// GET  /v1/images/file/&lt;name&gt;       回读落盘的产物（不过期的网关 URL）
/// POST /v1/video/generations       生视频 / 图生视频
/// GET  /v1/video/status/&lt;taskId&gt;   查视频任务
/// ```
///
/// ## 媒体产物为什么落盘
///
/// 上游给的是腾讯云 COS 的**签名 URL，1 小时失效**。直接转发有两个问题：
///   · 客户端存下来晚点再看 —— 链接已经失效
///   · **URL 很长（带签名参数），有些客户端会截断**（旧版注释里的原话）
///
/// 所以默认**下载到本地**、返回网关自己的 URL。要省那一次下载可以传
/// `deliver: "url"` 直接转发上游 URL，但那样就会过期 —— 这是调用方的选择。
///
/// ## 鉴权
///
/// **不豁免**。和 `/v1/*` 一样走网关统一的 Bearer 校验（见 `GatewayApp.HandleAsync`）
/// —— 这个族是被网关在鉴权**之后**调用的，自己不碰鉴权逻辑。
/// </summary>
public sealed partial class MediaEndpoints : IEndpointFamily
{
    private readonly MioraClient _miora;
    private readonly ILogger<MediaEndpoints> _log;

    /// <summary>视频任务登记表 —— 只为日志和「这个 id 是我们发的」粗校验。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _videoTasks = new(StringComparer.Ordinal);

    public MediaEndpoints(MioraClient miora, ILogger<MediaEndpoints> log)
    {
        _miora = miora;
        _log = log;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[images] 生成 {Count} 张，耗时 {Elapsed}ms，模型 {Model}")]
    private partial void LogImages(int count, long elapsed, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "[video] 任务已提交 {TaskId}，模型 {Model}")]
    private partial void LogVideoSubmitted(string taskId, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[media] {Code}: {Message}")]
    private partial void LogMediaError(string code, string message);

    // ── 分派 ──────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<bool> TryHandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (path.Length > 1 && path.EndsWith('/')) path = path.TrimEnd('/');

        var method = context.Request.Method;

        if (HttpMethods.IsPost(method) && path == "/v1/images/generations")
        {
            await ImagesGenerationsAsync(context).ConfigureAwait(false);
            return true;
        }

        if (HttpMethods.IsPost(method) && path == "/v1/images/edits")
        {
            await ImagesEditsAsync(context).ConfigureAwait(false);
            return true;
        }

        if (HttpMethods.IsGet(method) && path.StartsWith("/v1/images/file/", StringComparison.Ordinal))
        {
            await ServeMediaAsync(context, path["/v1/images/file/".Length..]).ConfigureAwait(false);
            return true;
        }

        if (HttpMethods.IsPost(method) && (path == "/v1/video/generations" || path == "/v1/videos"))
        {
            await VideoGenerationsAsync(context).ConfigureAwait(false);
            return true;
        }

        if (HttpMethods.IsGet(method) && path.StartsWith("/v1/video/status/", StringComparison.Ordinal))
        {
            await VideoStatusAsync(context, path["/v1/video/status/".Length..]).ConfigureAwait(false);
            return true;
        }

        // 不是我的请求 —— **一个字节都没写**，交给下一个族
        return false;
    }

    // ── 生图 ──────────────────────────────────────────────────

    /// <summary>
    /// POST /v1/images/generations
    ///
    /// 请求体对齐 OpenAI：`{ prompt, n?, size?, response_format?, deliver? }`。
    /// </summary>
    private async Task ImagesGenerationsAsync(HttpContext context)
    {
        var body = await ReadJsonBodyAsync(context).ConfigureAwait(false);
        if (body is null) return;

        var prompt = body["prompt"]?.GetValue<string>()?.Trim() ?? "";
        if (prompt.Length == 0)
        {
            await WriteErrorAsync(context, 400, "invalid_request_error", "prompt 不能为空").ConfigureAwait(false);
            return;
        }

        // n 夹到 1–4：一次要 20 张会把上游打爆，而且用户其实等不起
        var n = Math.Clamp(ReadInt(body["n"]) ?? 1, 1, 4);

        var upstreamBody = new JsonObject { ["prompt"] = prompt };
        var size = body["size"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(size) && size != "auto") upstreamBody["size"] = size;

        var started = Environment.TickCount64;
        try
        {
            // 多张并发提交（旧版也是 Promise.all）
            var tasks = Enumerable.Range(0, n)
                .Select(_ => _miora.SubmitAsync("image", "generate", (JsonObject)upstreamBody.DeepClone(), null, context.RequestAborted));

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var files = results.SelectMany(r => r.Files).ToList();

            if (files.Count == 0)
            {
                var failed = results.FirstOrDefault(r => !string.IsNullOrEmpty(r.ErrorMessage));
                await WriteErrorAsync(context, 502, "upstream_error",
                    failed?.ErrorMessage ?? "上游没有返回图片").ConfigureAwait(false);
                return;
            }

            var data = await BuildImageDataAsync(context, files, WantsBase64(body), WantsDeliverUrl(body)).ConfigureAwait(false);

            if (_log.IsEnabled(LogLevel.Information))
            {
                LogImages(data.Count, Environment.TickCount64 - started, results[0].Model);
            }

            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["data"] = data,
            }).ConfigureAwait(false);
        }
        catch (MioraException ex)
        {
            await RespondMioraErrorAsync(context, ex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// POST /v1/images/edits
    ///
    /// OpenAI 规范里是 multipart/form-data（带 image 文件）。这里**同时接受
    /// JSON 形式** —— 国内很多客户端更愿意直接传 base64：
    /// `{ image: "&lt;base64 或 data URI 或 https URL&gt;", prompt, size? }`
    /// </summary>
    private async Task ImagesEditsAsync(HttpContext context)
    {
        var body = await ReadJsonBodyAsync(context).ConfigureAwait(false);
        if (body is null) return;

        var prompt = body["prompt"]?.GetValue<string>()?.Trim() ?? "";
        if (prompt.Length == 0)
        {
            await WriteErrorAsync(context, 400, "invalid_request_error", "prompt 不能为空").ConfigureAwait(false);
            return;
        }

        var image = body["image"]?.GetValue<string>() ?? body["image_url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(image))
        {
            await WriteErrorAsync(context, 400, "invalid_request_error",
                "要提供 image：可以是 https URL、data URI，或者裸 base64").ConfigureAwait(false);
            return;
        }

        var upstreamBody = new JsonObject
        {
            ["prompt"] = prompt,
            ["images"] = new JsonArray(MediaTools.ToMediaPayload(image)),
        };

        var size = body["size"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(size) && size != "auto") upstreamBody["size"] = size;

        try
        {
            var result = await _miora
                .SubmitAsync("image", "edit", upstreamBody, null, context.RequestAborted)
                .ConfigureAwait(false);

            if (result.Files.Count == 0)
            {
                await WriteErrorAsync(context, 502, "upstream_error",
                    result.ErrorMessage ?? "上游没有返回图片").ConfigureAwait(false);
                return;
            }

            var data = await BuildImageDataAsync(context, result.Files, WantsBase64(body), WantsDeliverUrl(body)).ConfigureAwait(false);

            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["data"] = data,
            }).ConfigureAwait(false);
        }
        catch (MioraException ex)
        {
            await RespondMioraErrorAsync(context, ex).ConfigureAwait(false);
        }
    }

    private static bool WantsBase64(JsonObject body)
        => string.Equals(body["response_format"]?.GetValue<string>(), "b64_json", StringComparison.Ordinal);

    /// <summary>
    /// 调用方是不是要求**直接转发上游 URL**。
    ///
    /// ⚠ 这是与「默认落盘」相反的选项，代价明确：上游是 COS 签名 URL，
    /// **1 小时失效**。省下一次下载，换用户可能拿到一个过期链接。
    /// 默认不开 —— 见类注释里的取舍（旧版注释原话：URL 很长，有些客户端会截断）。
    /// </summary>
    private static bool WantsDeliverUrl(JsonObject body)
        => string.Equals(body["deliver"]?.GetValue<string>(), "url", StringComparison.Ordinal);

    /// <summary>
    /// 把上游的文件列表变成 OpenAI 的 `data` 数组。
    ///
    /// **默认下载落盘**并返回网关自己的 URL；`deliver: "url"` 时直接转发上游
    /// （省一次下载，但会过期 —— 见类注释里的取舍）。
    /// </summary>
    private async Task<JsonArray> BuildImageDataAsync(
        HttpContext context,
        IReadOnlyList<MediaFile> files,
        bool wantsBase64,
        bool deliverUrl)
    {
        var data = new JsonArray();

        foreach (var file in files)
        {
            if (wantsBase64)
            {
                var media = await _miora.DownloadAsync(file.Url, context.RequestAborted).ConfigureAwait(false);
                var bytes = _miora.ReadMedia(media.FileName) ?? [];
                data.Add(new JsonObject { ["b64_json"] = Convert.ToBase64String(bytes) });
                continue;
            }

            if (deliverUrl)
            {
                data.Add(new JsonObject { ["url"] = file.Url });
                continue;
            }

            var downloaded = await _miora.DownloadAsync(file.Url, context.RequestAborted).ConfigureAwait(false);
            data.Add(new JsonObject { ["url"] = MediaUrl(context, downloaded.FileName) });
        }

        return data;
    }

    /// <summary>
    /// 把文件名变成**完整可用**的 URL。
    ///
    /// 两件事：
    ///   ① 补上 host —— 只给 `/v1/images/file/xxx.png` 的话用户粘到浏览器里
    ///      打不开（不知道去哪台机器找）
    ///   ② host 从**进来的请求**里取 —— 这样反向代理转发过来时，
    ///      返回的也是代理那侧的地址，客户端照样能用
    ///
    /// ⚠ 不带签名：这个端点和 `/v1/*` 一样要 Bearer 鉴权（不豁免）。
    ///   旧版为了浏览器直接打开而加了签名 query —— 那等于给文件开了一个
    ///   绕过鉴权的口子，只靠「名字是内容哈希」当密码。新网关不这么做。
    /// </summary>
    private static string MediaUrl(HttpContext context, string fileName)
    {
        var host = context.Request.Host.Value;
        if (string.IsNullOrEmpty(host)) host = "127.0.0.1";
        return $"{context.Request.Scheme}://{host}/v1/images/file/{fileName}";
    }

    // ── 媒体文件回读 ──────────────────────────────────────────

    /// <summary>GET /v1/images/file/&lt;name&gt; —— 把落盘的产物发回去。</summary>
    private async Task ServeMediaAsync(HttpContext context, string name)
    {
        // 名字校验在 MioraClient 里（所有调用方共享），非法名字一律 400
        if (!MioraClient.IsSafeMediaName(name))
        {
            await WriteErrorAsync(context, 400, "invalid_request_error", "文件名不合法").ConfigureAwait(false);
            return;
        }

        var bytes = _miora.ReadMedia(name);
        if (bytes is null)
        {
            await WriteErrorAsync(context, 404, "not_found", "找不到这个文件（可能已被清理）").ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = MioraClient.MimeForFileName(name);
        context.Response.ContentLength = bytes.Length;

        // 文件名是**内容哈希**，所以内容永不改变 —— 可以长期缓存
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    // ── 视频 ──────────────────────────────────────────────────

    /// <summary>
    /// POST /v1/video/generations（别名 /v1/videos）
    ///
    /// 两种模式：
    ///   · **默认「提交就走」** —— 立刻回任务 id，客户端自己轮询。
    ///     视频要几分钟到十几分钟，把 HTTP 连接挂着不现实：
    ///     客户端超时撑不住，网关重启连接就断、任务白跑。
    ///   · `wait: true` —— 一直轮询到完成再回（短任务/脚本用）。
    ///
    /// 图生视频通过 `first_frame` 字段触发（见 `BuildVideoBody`）。
    /// </summary>
    private async Task VideoGenerationsAsync(HttpContext context)
    {
        var body = await ReadJsonBodyAsync(context).ConfigureAwait(false);
        if (body is null) return;

        var (upstreamBody, isImageToVideo) = BuildVideoBody(body);

        // 两样都没有就没法生成
        var hasPrompt = upstreamBody["prompt"] is not null;
        var hasFrame = upstreamBody["input"] is JsonObject;
        if (!hasPrompt && !hasFrame)
        {
            await WriteErrorAsync(context, 400, "invalid_request_error",
                "要提供 prompt（文生视频）或 first_frame（图生视频）").ConfigureAwait(false);
            return;
        }

        var wantsWait = body["wait"]?.GetValue<bool>() ?? false;

        try
        {
            if (!wantsWait)
            {
                var submit = await _miora
                    .SubmitRawAsync("video", upstreamBody, context.RequestAborted)
                    .ConfigureAwait(false);

                _videoTasks[submit.TaskId] = submit.Model;

                if (_log.IsEnabled(LogLevel.Information))
                {
                    LogVideoSubmitted(submit.TaskId, submit.Model);
                }

                await WriteJsonAsync(context, 200, new JsonObject
                {
                    ["id"] = submit.TaskId,
                    ["object"] = "video.generation",
                    ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["status"] = submit.Status,
                    ["model"] = submit.Model,
                    // 客户端可以拿这个查状态
                    ["status_url"] = $"/v1/video/status/{submit.TaskId}",
                }).ConfigureAwait(false);
                return;
            }

            // 等待模式
            var started = Environment.TickCount64;
            var result = await _miora
                .SubmitAsync("video", null, upstreamBody, null, context.RequestAborted)
                .ConfigureAwait(false);

            if (result.Files.Count == 0)
            {
                await WriteErrorAsync(context, 502, "upstream_error",
                    result.ErrorMessage ?? "上游没有返回视频").ConfigureAwait(false);
                return;
            }

            var media = await _miora
                .DownloadAsync(result.Files[0].Url, context.RequestAborted)
                .ConfigureAwait(false);

            _ = isImageToVideo;   // label 只用于日志

            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["data"] = new JsonArray(new JsonObject { ["url"] = MediaUrl(context, media.FileName) }),
                ["model"] = result.Model,
            }).ConfigureAwait(false);
        }
        catch (MioraException ex)
        {
            await RespondMioraErrorAsync(context, ex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 从请求体组装上游的视频 body，并判断是不是图生视频。
    ///
    /// 图生视频的判据是**有没有 first_frame** —— 它对应上游的 `input.first_frame_image`。
    /// 这和旧版 `runTool` 里 `image_to_video` 走的那条路是同一套形状。
    /// </summary>
    private static (JsonObject Body, bool IsImageToVideo) BuildVideoBody(JsonObject source)
    {
        var body = new JsonObject();

        var prompt = source["prompt"]?.GetValue<string>()?.Trim();
        if (!string.IsNullOrEmpty(prompt)) body["prompt"] = prompt;

        if (ReadDouble(source["duration"]) is { } duration) body["duration"] = duration;
        if (source["resolution"]?.GetValue<string>() is { Length: > 0 } resolution) body["resolution"] = resolution;
        if (source["aspect_ratio"]?.GetValue<string>() is { Length: > 0 } ratio) body["aspect_ratio"] = ratio;

        var firstFrame = source["first_frame"]?.GetValue<string>()
            ?? source["first_frame_image"]?.GetValue<string>();
        if (string.IsNullOrEmpty(firstFrame)) return (body, false);

        var input = new JsonObject { ["first_frame_image"] = MediaTools.ToMediaPayload(firstFrame) };

        var lastFrame = source["last_frame"]?.GetValue<string>()
            ?? source["last_frame_image"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(lastFrame)) input["last_frame_image"] = MediaTools.ToMediaPayload(lastFrame);

        body["input"] = input;
        return (body, true);
    }

    /// <summary>
    /// GET /v1/video/status/&lt;taskId&gt;
    ///
    /// 完成后会把文件下载到本地、返回网关的 URL。**为什么在这里下载而不是
    /// 让客户端自己取**：上游给的是 1 小时过期的签名 URL，客户端可能过几小时
    /// 才来看。
    /// </summary>
    private async Task VideoStatusAsync(HttpContext context, string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            await WriteErrorAsync(context, 400, "invalid_request_error", "缺少任务 id").ConfigureAwait(false);
            return;
        }

        try
        {
            var entry = await _miora.QueryAsync("video", taskId, context.RequestAborted).ConfigureAwait(false);

            var status = entry?["status"]?.GetValue<string>() ?? "unknown";
            var filesNode = entry?["resultFiles"] as JsonArray ?? entry?["result_files"] as JsonArray;

            var payload = new JsonObject
            {
                ["id"] = taskId,
                ["object"] = "video.generation",
                ["status"] = status,
                ["known"] = _videoTasks.ContainsKey(taskId),
            };

            if (entry?["model"]?.GetValue<string>() is { Length: > 0 } model) payload["model"] = model;
            if (entry?["errorMessage"]?.GetValue<string>() is { Length: > 0 } error) payload["error"] = error;

            // 只有在真的有产物时才下载 —— 轮询中途不该白下一次
            if (filesNode is { Count: > 0 })
            {
                var url = filesNode[0]?["signedUrl"]?.GetValue<string>()
                    ?? filesNode[0]?["url"]?.GetValue<string>();

                if (!string.IsNullOrEmpty(url))
                {
                    var media = await _miora.DownloadAsync(url, context.RequestAborted).ConfigureAwait(false);
                    payload["url"] = MediaUrl(context, media.FileName);
                }
            }

            await WriteJsonAsync(context, 200, payload).ConfigureAwait(false);
        }
        catch (MioraException ex)
        {
            await RespondMioraErrorAsync(context, ex).ConfigureAwait(false);
        }
    }

    // ── 错误与输出 ────────────────────────────────────────────

    /// <summary>把 MioraException 转成 OpenAI 形状的错误响应。</summary>
    private async Task RespondMioraErrorAsync(HttpContext context, MioraException ex)
    {
        if (_log.IsEnabled(LogLevel.Warning))
        {
            LogMediaError(ex.Code, ex.Message);
        }

        await WriteErrorAsync(context, ex.Status, ex.OpenAiType, ex.Message).ConfigureAwait(false);
    }

    /// <summary>读 JSON 请求体。**失败时已经写过响应**，返回 null 表示调用方直接 return。</summary>
    private static async Task<JsonObject?> ReadJsonBodyAsync(HttpContext context)
    {
        // 只要 JSON —— 媒体端点的请求体里有 prompt 和 base64，
        // 用错 Content-Type 的客户端应该明确被告知，而不是收到一个解析失败
        if (!IsJsonContentType(context.Request.ContentType))
        {
            await WriteErrorAsync(context, 415, "unsupported_media_type",
                "Content-Type must be application/json").ConfigureAwait(false);
            return null;
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);

        try
        {
            if (JsonNode.Parse(raw) is JsonObject body) return body;
        }
        catch (System.Text.Json.JsonException)
        {
            // 落到下面统一报错
        }

        await WriteErrorAsync(context, 400, "invalid_request_error",
            "请求体必须是合法的 JSON 对象").ConfigureAwait(false);
        return null;
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var mime = semicolon >= 0 ? contentType[..semicolon] : contentType;
        return mime.Trim().StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (value.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<long>(out var l)) return l;
        return null;
    }

    /// <summary>不转义中文（和 GatewayApp / UsageEndpoints 同一套策略）。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task WriteJsonAsync(HttpContext context, int status, JsonNode body)
    {
        var payload = body.ToJsonString(RelaxedJson);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(payload);
        await context.Response.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string kind, string message)
        => WriteJsonAsync(context, status, new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = string.IsNullOrWhiteSpace(message) ? kind : message,
                ["type"] = kind,
                ["code"] = kind,
            },
        });
}
