using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 网关的 HTTP 表面。
///
/// ## 为什么是独立类而不是全塞在 Program.cs
///
/// 因为要能被测试直接 `new` 出来跑 —— 起一个真的 Kestrel、真的端口、
/// 真的 HttpClient 打进去，比 mock WebApplication 靠谱得多。
/// Program.cs 只负责装配 DI，逻辑全在这里。
///
/// ## 端点
///
/// ```
/// GET  /healthz              免鉴权，带代码指纹
/// GET  /v1/models            模型清单
/// POST /v1/chat/completions  聊天（流式 + 非流式）
/// ```
///
/// ## 错误形状
///
/// 一律 `{"error":{"message","type","code"}}`，和旧版 `writeOpenAIError` 同形。
/// **绝不回 undefined/null** —— 旧版踩过：三个模型同时被限流时返回 undefined，
/// 调用方 `if (!result.ok)` 直接抛 TypeError，而且是在响应已经发了一半的
/// 流式路径上，用户看到的是莫名其妙的 "stream failed mid-flight"。
/// </summary>
public sealed partial class GatewayApp
{
    private readonly GatewayOptions _options;
    private readonly UpstreamVariant _variant;
    private readonly ModelCatalog _catalog;
    private readonly BuildInfo _build;
    private readonly UpstreamClient _upstream;
    private readonly AccountPool _pool;
    private readonly ICredentialSource _credentials;
    private readonly ChatObservers _observers;
    private readonly ILogger<GatewayApp> _log;

    /// <summary>
    /// 多池路由（可空）。null = 单池（测试夹具 / 老构造点），
    /// 此时请求始终走默认的 <see cref="_pool"/>。
    /// </summary>
    private readonly PoolRouter? _poolRouter;

    /// <summary>当前请求的账号池（多池路由下按 key 解析，否则默认池）。</summary>
    private AccountPool Pool => PoolContext.Pool?.Pool ?? _pool;

    /// <summary>当前请求的凭证源（同上）。</summary>
    private ICredentialSource Credentials => PoolContext.Pool?.Credentials ?? _credentials;

    /// <summary>
    /// 会话粘性（P1）。可空 —— 测试里可以不给，这时退化成纯池轮询。
    ///
    /// 为什么是「可空」而不是「必给 + Null 实现」：粘性只是一个优化，
    /// 它挂掉不该让网关起不来。可空 + 每处判一下，语义最直白。
    /// </summary>
    private readonly ConversationAffinity? _affinity;

    /// <summary>请求指标（P2）。可空，同上。</summary>
    private readonly IMetricsSource? _metrics;

    /// <summary>脱敏日志（P2）。可空，同上。</summary>
    private readonly RequestLog? _requestLog;

    /// <summary>
    /// 端点族（协议翻译层 / 媒体 / 搜索等），**按注册顺序**依次询问。
    ///
    /// 空列表是合法状态 —— 网关照样能跑，只是没有这些扩展端点。
    /// </summary>
    private readonly IReadOnlyList<IEndpointFamily> _families;

    /// <summary>
    /// 目录刷新器（t53）。带正确的 UA 形态 + 失败以 Warning 记。
    /// 可空 —— 测试夹具不挂它时，刷新退化成「不刷新」（仍有内置目录兜底）。
    /// </summary>
    private readonly CatalogRefresh? _catalogRefresh;

    /// <summary>
    /// 管理面端点（`/admin/*`）。可空 —— 测试夹具不挂它时行为退化成「没有管理面」，
    /// 与 t2 那三个依赖同样都是可选的。
    /// </summary>
    private readonly AdminEndpoints? _admin;

    /// <summary>
    /// 媒体工具注入器（生图 / 改图 / 生视频 / 图生视频）。
    ///
    /// 可空 —— 和上面几个依赖一样，测试夹具不挂它时行为退化成
    /// 「不注入媒体工具」，聊天路径一切照旧。
    /// </summary>
    private readonly MediaToolInjector? _mediaTools;

    /// <summary>工具**执行器** —— 模型请求调工具时由网关自己执行（界面执行不了）。</summary>
    private readonly MediaToolExecutor? _mediaToolExecutor;

    /// <summary>本机回环的主机名集合 —— 非回环 Host 一律 403。</summary>
    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "127.0.0.1", "localhost", "[::1]", "::1",
    };

    public GatewayApp(
        GatewayOptions options,
        UpstreamVariant variant,
        ModelCatalog catalog,
        BuildInfo build,
        UpstreamClient upstream,
        AccountPool pool,
        ICredentialSource credentials,
        ChatObservers observers,
        ILogger<GatewayApp> log,
        ConversationAffinity? affinity = null,
        IMetricsSource? metrics = null,
        RequestLog? requestLog = null,
        AdminEndpoints? admin = null,
        IEnumerable<IEndpointFamily>? families = null,
        MediaToolInjector? mediaTools = null,
           MediaToolExecutor? mediaToolExecutor = null,
        CatalogRefresh? catalogRefresh = null,
        PoolRouter? poolRouter = null)
    {
        _options = options;
        _variant = variant;
        _catalog = catalog;
        _build = build;
        _upstream = upstream;
        _pool = pool;
        _credentials = credentials;
        _observers = observers;
        _log = log;
        _poolRouter = poolRouter;
        _mediaTools = mediaTools;
        _mediaToolExecutor = mediaToolExecutor;
        _affinity = affinity;
        _metrics = metrics;
        _requestLog = requestLog;
        _admin = admin;

        // 端点族按**注册顺序**依次询问，第一个说「我能处理」的赢
        // （顺序由 EndpointFamilies 的注册处决定，见那里的注释）。
        _families = families as IReadOnlyList<IEndpointFamily> ?? families?.ToArray() ?? [];
        _catalogRefresh = catalogRefresh;

        // ★ **启动时就把账号池填上**。
        //
        // 原来 `Replace` 只在**请求路径**里调（下面 :555 那句）——
        // 于是：
        //   · 启动后、首个请求前，池是空的**
        //   ⇒ `/admin/pool-health` 返回 accountCount = 0（**且不报错**）
        //   ⇒ 界面「模型限流」面板静默不显示（因为它挑不出有问题的模型）
        //
        // ⚠ 请求路径里那次 Replace **必须保留** —— 用户导入/删号后不必重启网关。
        //
        // 这里只做"初始填充"，重复调用是幂等的（Replace 会重建成员表）。
        try
        {
            var initial = Credentials.Load();
            if (initial.Count > 0) Pool.Replace(initial);
        }
        catch (Exception ex)
        {
            // 启动阶段读不到账号不该让网关起不来 —— 池为空只是降级：首个请求会再补一次。
            if (_log.IsEnabled(LogLevel.Warning)) LogPoolInitFailed(ex, ex.Message);
        }
    }

    /// <summary>把端点挂到一个 ASP.NET Core 应用上。</summary>
    public void Map(WebApplication app)
    {
        // 全部端点走**同一个**终端中间件。
        //
        // 为什么不用 MapGet + app.Run 混搭：那样要有序地把路由和中间件
        // 串起来（UseRouting 在前），而 /healthz 还必须在鉴权之前 ——
        // 顺序一旦错了，症状是 /healthz 返回 404 或 401，非常难查。
        // 一个入口里显式分派，顺序就是代码顺序，没有歧义。
        app.Run(HandleAsync);
    }

    // ── /healthz ──────────────────────────────────────────────

    private Task HealthzAsync(HttpContext context)
    {
        // buildId 是**这个进程启动时加载的代码**的指纹；stale 是
        // 「磁盘现在的指纹」和它一不一样。两者不同 = 代码改了但没重启。
        return WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = true,
            ["provider"] = _variant.Id,
            ["models"] = _catalog.Count,
            ["buildId"] = _build.Loaded,
            ["startedAt"] = _build.StartedAtIso,
            ["stale"] = _build.IsStale,
        });
    }

    // ── 其余端点：先过边界，再过鉴权 ──────────────────────────

    private async Task HandleAsync(HttpContext context)
    {
        var path = NormalizePath(context.Request.Path.Value);

        // ⚠ 请求 id 在**所有分支之前**就分配，并且立刻回写响应头。
        //
        // 为什么这么早、而且放在 /healthz 前面：
        //   · 鉴权失败（401）也要能追溯 —— 用户报「我 key 没错啊」的时候，
        //     靠这个 id 才能在日志里找到那一次
        //   · /healthz 看起来「不需要」它，但面板的探活日志和我们这边的
        //     诊断要能对上，正是靠这个 id。少了它，用户说「面板显示离线」
        //     时我们连是哪一次探活都找不到
        //   · 它只是生成一个字符串 + 设一个头，没有副作用，放最前面最直白
        var requestId = RequestIds.Resolve(context);
        context.Items[RequestIdKey] = requestId;
        context.Response.Headers["X-Request-Id"] = requestId;

        // ★ 多池路由：按 Authorization key 解析当前请求属于哪个池。
        //   无 key / 没配路由 → 默认池（单池用户完全无感）。
        if (_poolRouter is not null)
        {
            var apiKey = BearerOf(context.Request.Headers.Authorization.ToString());
            PoolContext.Set(_poolRouter.Resolve(apiKey));
        }
        else
        {
            PoolContext.Set(null);
        }

        // /healthz 排在最前面：面板探活时手上没有 key（也不需要），
        // 鉴权挡在前面会让探活全红。它也不做 Host 检查 ——
        // 容器/健康检查探针的 Host 头不一定是回环。
        if (context.Request.Method == HttpMethods.Get && path == "/healthz")
        {
            await HealthzAsync(context).ConfigureAwait(false);
            return;
        }

        // Host 检查在鉴权**之前**：非回环 Host 意味着 DNS rebinding 页面
        // 或者远程调用者 —— 光靠 bearer 不够，这是本机信任边界。
        if (!IsLoopbackHost(context.Request.Host.Value))
        {
            await WriteErrorAsync(context, 403, "host_not_allowed", "Host header must name the loopback interface")
                .ConfigureAwait(false);
            return;
        }

        if (!Authorized(context))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"buddydeck\"";
            await WriteErrorAsync(context, 401, "invalid_api_key", "missing or invalid API key").ConfigureAwait(false);
            return;
        }

        if (context.Request.Method == HttpMethods.Get && path == "/v1/models")
        {
            await ModelsAsync(context).ConfigureAwait(false);
            return;
        }

        // 指标：界面读的就是这个（见 BuddyDeck.App 的 IGatewayClient.GetMetricsAsync）。
        // 放在鉴权之后 —— 它暴露了账号标签和用量形状，不该对外。
        if (context.Request.Method == HttpMethods.Get && path == "/metrics")
        {
            await MetricsAsync(context).ConfigureAwait(false);
            return;
        }

        if (context.Request.Method == HttpMethods.Post && path == "/v1/chat/completions")
        {
            await ChatCompletionsAsync(context).ConfigureAwait(false);
            return;
        }

        // ── /admin/*：界面的管理面 ────────────────────────────────
        //
        // ⚠ 位置很关键：它在**鉴权之后**（上面 :161 那段），
        // 在 404 兜底**之前**。
        //   · 放在鉴权之前 → 账号标签、配置会对无 key 的调用者可见
        //   · 放在 404 之后 → 永远走不到（兜底是终局）
        //
        // 这也是为什么「带合法 key 打 /admin/accounts 得到 404」曾经是
        // 这个缺口的症状：鉴权过了、但路由不存在。
        if (_admin is not null && await _admin.TryHandleAsync(context).ConfigureAwait(false))
        {
            return;
        }

        // ── 端点族（协议翻译层 / 媒体 / 搜索…）────────────────────
        //
        // 位置与 /admin/* 同理：**鉴权之后**（否则翻译层不需要凭据就能用）、
        // 404 兜底**之前**（兜底是终局，放在它后面永远走不到）。
        //
        // 按注册顺序依次询问，第一个说「我能处理」的赢 —— 所以
        // **前缀重叠的族必须把更具体的排在前面**，顺序在注册处显式写明。
        foreach (var family in _families)
        {
            if (await family.TryHandleAsync(context).ConfigureAwait(false))
            {
                return;
            }
        }

        await WriteErrorAsync(context, 404, "not_found", $"no route for {context.Request.Method} {path}")
            .ConfigureAwait(false);
    }

    /// <summary>请求 id 在 HttpContext.Items 里的键。</summary>
    private const string RequestIdKey = "buddydeck.request-id";

    /// <summary>取当前请求的 id（一定非空 —— <see cref="HandleAsync"/> 保证分配过）。</summary>
    private static string RequestIdOf(HttpContext context)
        => context.Items.TryGetValue(RequestIdKey, out var value) && value is string s ? s : "";

    // ── /metrics ──────────────────────────────────────────────

    /// <summary>
    /// 指标快照。
    ///
    /// 没接指标（<c>_metrics is null</c>）时回一个**空快照**而不是 404 ——
    /// 界面那边把「拿不到指标」当正常状态处理，给它一个结构完整的空对象
    /// 比给它一个错误更省事（见 App 的 `GatewayMetrics.IsEmpty`）。
    /// </summary>
    private Task MetricsAsync(HttpContext context)
    {
        var snapshot = _metrics?.Snapshot() ?? new MetricsSnapshot();
        return WriteJsonAsync(context, 200, snapshot.ToJson());
    }

    /// <summary>去掉尾部斜杠，让 `/v1/models/` 和 `/v1/models` 等价（旧版认两种）。</summary>
    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        return path.Length > 1 && path.EndsWith('/') ? path.TrimEnd('/') : path;
    }

    /// <summary>Host 头是不是回环。带端口要剥掉，IPv6 要认方括号。</summary>
    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;

        var name = host.Trim();
        if (name.StartsWith('['))
        {
            var end = name.IndexOf(']');
            name = end == -1 ? name : name[..(end + 1)];
        }
        else
        {
            var colon = name.LastIndexOf(':');
            if (colon > 0 && !name[..colon].Contains(':') && int.TryParse(name[(colon + 1)..], out _))
            {
                name = name[..colon];
            }
        }

        return LoopbackHosts.Contains(name);
    }

    // ── 鉴权 ──────────────────────────────────────────────────

    /// <summary>
    /// 校验 Bearer key。配置里 key 为空 = 不鉴权（和旧版语义一致）。
    ///
    /// 用 <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals"/>
    /// 而不是 `==` —— 字符串比较会短路，攻击者能靠响应时间逐字节猜出 key。
    ///
    /// ## 媒体文件的 HMAC 签名免鉴权（M9）
    ///
    /// `GET /v1/images/file/&lt;name&gt;?s=&lt;签名&gt;` 只要签名正确就放行，
    /// **不带 Authorization 头也行**。为什么需要（旧版 `gateway.js:546-551` 原话）：
    ///
    /// > 用户在浏览器里直接打开图片时**不会带那个头** —— 不特殊处理的话
    /// > 他看到的是 401，而不是图。签名是用 API key 作为 HMAC 密钥算出来的，
    /// > 所以**「能拿出有效签名」等价于「知道 key」**。安全性不降低，但可用性天壤之别。
    ///
    /// ## ⚠ 为什么放在这里，而不是放进 MediaEndpoints
    ///
    /// 因为**旧版就是这么分的**：签名校验在 `gateway.js:559-566` 的**鉴权段**
    /// （**路由之前**），而文件端点 `serveGeneratedFile`（`:1241`）
    /// **一行签名逻辑都没有** —— 它只管读盘和发送。
    ///
    /// 放进端点的话会变成两个 handler 争同一个路径（靠注册顺序决胜），
    /// 那是把「鉴权」和「路由」两件事混在一起了。
    ///
    /// 详见 <see cref="MediaSignature"/>。
    /// </summary>
    private bool Authorized(HttpContext context)
    {
        // ⚠ 这一行**必须最先**：网关没配 key = 不鉴权（和旧版语义一致）。
        //   签名逻辑不能改变这个语义 —— 那时即使签名是空的也照样放行。
        if (string.IsNullOrEmpty(_options.ApiKey)) return true;

        // 媒体文件签名：命中即放行，不再看 Bearer。
        //
        // ⚠ 顺序上放在 Bearer 之前，因为「浏览器直接打开图片」正是没有 Bearer 的场景；
        //   放在后面的话要先走完一套必然失败的 Bearer 检查，白做一次比较
        //   （正确性不受影响，但这是这条路径存在的全部意义）。
        if (MediaSignature.IsSignedMediaRequest(context, _options.ApiKey)) return true;


        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header)) return false;

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var presented = header[prefix.Length..].Trim();

        // ★ 多池路由：key 匹配**任意池**的 key 都算合法（new_api 的渠道 key）。
        if (_poolRouter is not null)
        {
            foreach (var pool in _poolRouter.All)
            {
                if (!string.IsNullOrEmpty(pool.ApiKey) && FixedTimeEquals(pool.ApiKey, presented))
                {
                    return true;
                }
            }
            // 一个都没匹配上 → 再比默认池的 key
            return FixedTimeEquals(_options.ApiKey, presented);
        }

        return FixedTimeEquals(_options.ApiKey, presented);
    }

    /// <summary>定时安全比较。长度不同直接 false（长度本身不是秘密）。</summary>
    private static bool FixedTimeEquals(string expected, string presented)
    {
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>从 Authorization 头里摘出 Bearer key。空头/非 Bearer → null。</summary>
    private static string? BearerOf(string header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return header[prefix.Length..].Trim();
    }

    // ── /v1/models ────────────────────────────────────────────

    private async Task ModelsAsync(HttpContext context)
    {
        // 有机会就从上游刷一次目录。刷不到（没账号、网络不好）就用内置清单 ——
        // `/v1/models` 是客户端启动时第一件调的事，不能因为上游抽风就没有模型可选。
        await TryRefreshCatalogAsync(context.RequestAborted).ConfigureAwait(false);

        var data = new JsonArray();
        foreach (var model in _catalog.Models)
        {
            data.Add(new JsonObject
            {
                ["id"] = model.Id,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = _variant.Id,
            });
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["object"] = "list",
            ["data"] = data,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 从上游刷新模型目录。
    ///
    /// ## t53：这个方法的**实现已搬到 <see cref="CatalogRefresh"/>**
    ///
    /// 原来这里有两处真缺陷：
    ///   ① `FetchCatalogAsync(entry.ToCredential(), **null**, ct)` —— 不带 UA，
    ///      而实测（t48）不带 UA 访问 `/v3/config` 返回 **400 / code 12403**
    ///      ⇒ **刷新从来没成功过**。
    ///   ② 失败日志压在 **Debug** 级 ⇒ 生产日志里根本看不到
    ///      ⇒ 表现为「`/v1/models` 返回 16 个内置模型、界面毫无异常」，
    ///      用户永远看不到真机的真实数量。
    ///
    /// 现在委托给 <see cref="CatalogRefresh.TryRefreshAsync"/> ——
    /// 那里带正确的 **CLI 形态** UA，且失败以 **Warning** 记（含端点/UA/状态码/原始正文）。
    /// 这个方法的职责退化成「怎么用保险库里的凭证去调它」。
    /// </summary>
    private async Task TryRefreshCatalogAsync(CancellationToken ct)
    {
        // 没接刷新器（测试夹具的常见状态）→ 直接不刷，用内置目录兜底。
        // 这不是「静默失败」：**根本没有尝试过**，与「试了但失败」是两回事
        //（后者由 CatalogRefresh 以 Warning 记录）。
        if (_catalogRefresh is null) return;

        var entries = Credentials.Load();
        if (entries.Count == 0) return;

        await _catalogRefresh.TryRefreshAsync(entries[0].ToCredential(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 从上游目录 JSON 里把模型 id 抽出来。
    ///
    /// 上游的目录形状有两层：可能直接是 `{models:[...]}`，
    /// 也可能包在信封的 `data` 里。两种都认。
    ///
    /// 注：对照真实响应结构接入时可能需要再调整 —— 现在按这两层兜底。
    /// </summary>
    private static List<CatalogModel> ExtractModels(JsonNode? node)
    {
        var result = new List<CatalogModel>();
        var array = FindModelsArray(node);
        if (array is null) return result;

        foreach (var item in array)
        {
            if (item is not JsonObject obj) continue;
            var id = obj["id"]?.GetValue<string>() ?? obj["model"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) continue;

            var name = obj["name"]?.GetValue<string>() ?? id;
            result.Add(new CatalogModel(id, name));
        }

        return result;
    }

    private static JsonArray? FindModelsArray(JsonNode? node)
    {
        if (node is JsonArray direct) return direct;
        if (node is not JsonObject obj) return null;

        if (obj["models"] is JsonArray models) return models;
        if (obj["data"] is JsonObject data && data["models"] is JsonArray nested) return nested;
        if (obj["data"] is JsonArray dataArray) return dataArray;
        if (obj["list"] is JsonArray list) return list;

        return null;
    }

    // ── /v1/chat/completions ──────────────────────────────────

    private async Task ChatCompletionsAsync(HttpContext context)
    {
        // 1) Content-Type 必须是 JSON —— 否则 415
        if (!IsJsonContentType(context.Request.ContentType))
        {
            await WriteErrorAsync(context, 415, "unsupported_media_type", "Content-Type must be application/json")
                .ConfigureAwait(false);
            return;
        }

        // 2) 读请求体（带大小上限）
        string rawBody;
        try
        {
            rawBody = await ReadBodyAsync(context).ConfigureAwait(false);
        }
        catch (BodyTooLargeException)
        {
            await WriteErrorAsync(context, 413, "invalid_request_error", "request body too large").ConfigureAwait(false);
            return;
        }

        // 3) 必须是合法 JSON 对象 —— 否则 400
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(rawBody);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(context, 400, "invalid_request_error", "request body is not valid JSON")
                .ConfigureAwait(false);
            return;
        }

        if (parsed is not JsonObject request)
        {
            await WriteErrorAsync(context, 400, "invalid_request_error", "request body must be a JSON object")
                .ConfigureAwait(false);
            return;
        }

        // 客户端要的是流式还是整条？**在读请求的那一刻就定下来** ——
        // 管线会把 body 里的 stream 强制改成 true（上游只支持流式），
        // 之后就没法从 body 里读出来了。
        var wantsStream = request["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var s) && s;
        var requestedModel = request["model"]?.GetValue<string>();

        // 4) 选号
        var entries = Credentials.Load();
        if (entries.Count == 0)
        {
            // 也记一笔 —— 「没账号」是用户最常遇到的失败之一，
            // 指标里看不见它就等于把最常见的失败统计漏掉了。
            //
            // ⚠ SentToUpstream = false：保险库是空的，请求压根没出网。
            //   它进主口径（用户视角：这次请求失败了），但不进 AttemptCount
            //   （上游一次都没被打过）。
            Observe(new RequestRecord
            {
                At = DateTimeOffset.UtcNow,
                RequestId = RequestIdOf(context),
                Model = requestedModel ?? "",
                UsedModel = _catalog.Resolve(requestedModel),
                Status = 401,
                DurationMs = 0,
                ErrorKind = "not_signed_in",
                Outcome = "failed",
                Streamed = wantsStream,
                SentToUpstream = false,
            });

            await WriteErrorAsync(context, 401, "not_signed_in",
                "保险库里没有账号 —— 先在面板里导入一个 WorkBuddy 账号").ConfigureAwait(false);
            return;
        }

        // 池子里没有这个号（刚导入）就补进去 —— 每次请求对一遍，
        // 用户导入/删号之后不必重启网关。
        Pool.Replace(entries);

        var resolvedModel = _catalog.Resolve(requestedModel);

        // 5) 过管线：强制 stream、角色归一、tool_choice 归一、**模型改写**
        //
        // ⚠ 第三个参数是 t11 修的那个 bug 的落点：
        // 客户端常常写死一个我们不认识的别名（gpt-4o / claude-…），
        // `ModelCatalog.Resolve` 会把它落到默认模型。**必须把这个决定
        // 一起写进请求体**，否则会出现两个模型名：
        //   · 选号 / 冷却记账用的是 resolvedModel（hy3）
        //   · 上游实际收到的是别名（gpt-4o）
        // 后果：上游拿不认识的模型名很可能直接拒；更糟的是 429 时
        // 把该号的 hy3 拉黑，而真被限的是 gpt-4o —— 用户真正想用的
        // hy3 白进冷却最长 5 分钟（HANDOVER 坑 4 的配额浪费）。
        var prepared = RequestPipeline.Prepare(_variant, rawBody, resolvedModel);

        // 5.4) 注入媒体工具（生图 / 改图 / 生视频 / 图生视频）
        //
        // ## 为什么在**请求体**里注入，而不是在端点层拦关键词
        //
        // 关键词方案（旧版第一版）有两个死穴：「生成一个视频」命中「生成」
        // 会被当生图处理（用户要视频拿到一张图）；而且一接带 tools 的客户端
        // （Claude Code / pi / DSH）就整个失效 —— 模型只会回「我没办法直接画图哦」。
        //
        // 正确做法是照 WorkBuddy 自己的做法：把工具**注册给模型**，
        // 让它读 description 自己判断该不该调。判断权在模型，不在正则。
        //
        // ⚠ 必须在 `RequestPipeline.Prepare` **之后**注入：
        //   管线里 `tool_choice: "none"` 那条分支会把 tools 整个删掉
        //   （用户明确说「别调工具」时不该再塞回去）。
        //
        // ⚠ 这里只**注入定义**；执行在 MediaToolExecutor（只有网关拿着
        //   Miora 凭证，客户端的模型不认识这些工具）。
        //
        // ⚠ `suppressTools` 从**原始**请求体判断 —— 管线已经把
        //   `tool_choice: "none"` 的 tools 删干净了，到这儿看不出来。
        prepared = _mediaTools?.InjectInto(prepared, ClientSuppressedTools(request)) ?? prepared;

        // 5.5) 会话粘性（P1）
        //
        // 多轮对话必须落在同一个号上，否则上游那边上下文会跳号
        // （用户看到「聊到一半失忆」）。派生键、查表、选号都在
        // ConversationAffinity 里做完，这里只负责把原始请求体递进去。
        //
        // ⚠ 用**原始** rawBody 而不是 prepared：
        // prepared 强制 stream:true 之后已经不是客户端发来的样子了，
        // 而会话键要的恰恰是「客户端认为的这段对话」。
        var conversationKey = ConversationKey.Derive(rawBody);

        await RunWithRetryAsync(context, prepared, resolvedModel, requestedModel, wantsStream, conversationKey)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 客户端是不是**明确说不要工具**（`tool_choice: "none"`）。
    ///
    /// ## 为什么必须从原始请求体判断
    ///
    /// `RequestPipeline.NormalizeToolChoice` 遇到 `"none"` 会把
    /// `tool_choice` **和** `tools` 一起删掉（留着 tools + none 上游会困惑）。
    /// 所以管线跑完之后，`prepared` 里已经没有任何痕迹能表明
    /// 「用户说过别调工具」。
    ///
    /// 不看这个信号的话，媒体工具注入会把四个工具**硬塞回**一个
    /// 明确说了不要工具的请求里 —— 而且完全静默。
    /// </summary>
    private static bool ClientSuppressedTools(JsonObject request)
    {
        var choice = request["tool_choice"];

        // 形态一：字符串 "none"
        if (choice is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return string.Equals(text.Trim(), "none", StringComparison.OrdinalIgnoreCase);
        }

        // 形态二：对象 {"type":"none"}
        if (choice is JsonObject obj &&
            obj["type"] is JsonValue typeValue &&
            typeValue.TryGetValue<string>(out var type))
        {
            return string.Equals(type.Trim(), "none", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// 限流降级链 —— **两层**：先在账号内换模型，耗尽后才换号。
    ///
    /// ## 为什么是两层（这是 t28 的核心改造）
    ///
    /// 上游的限流**按「账号 + 模型」算** —— 它自己的报错文案就写着
    /// 「您也可以**切换其他模型**继续使用」（`ratelimit.js:10` 的实测原文）。
    ///
    /// 所以遇到 429 时，**换模型比换号便宜得多**：
    ///   · 换模型：同一个 credential、同一个 token，**不用重走凭证链路**
    ///   · 换号：要换凭证、要让会话粘性让路、那个号可能整体也在冷却
    ///
    /// 旧版的分层（`gateway.js:445` 的 `sendWithFallback`）就是：
    /// 内层换模型，**当前账号所有模型都限流了才**换号。
    ///
    /// ⚠ t8/t19 的任务书里我写的都是「换号重试」—— **方向对，层级错了**。
    /// 只换号的话，一个号上「只有 A 模型被限」时，会白白把整号换掉，
    /// 而那个号的其他模型明明还能用（HANDOVER 坑 4 说的配额浪费）。
    ///
    /// ## 冷却中的组合「连请求都不发」
    ///
    /// 内层开新的一轮之前先问 `CanPick(memberId, model)`：
    /// 该「账号+模型」在冷却里就**直接跳过**，不发请求。
    ///
    /// 为什么这很重要：不发的话，那次请求会**出网、被上游 429、再记一次失败** ——
    /// 既浪费一次尝试计数（`AttemptCount` 被污染），又白白占掉一个在途名额。
    /// 实测表现是「0ms 秒回失败」，很难查。
    ///
    /// ## 响应头阶段失败 vs 已经开始输出
    ///
    /// 旧版注释里的关键约束：`chatStream` 是在**响应头阶段**就判定失败的
    /// （还没开始吐内容），所以重试不会产生半截输出。
    ///
    /// 本循环只在「拿到 `UpstreamResult` 且 `!Ok`」时重试 —— 那个时刻
    /// **一个字节都还没写给客户端**。一旦进入 `StreamAsync` 开始输出，
    /// 后面出错就只能原样传（见那里的注释）——**绝不能重试**，
    /// 否则用户会看到重复内容。
    /// </summary>
    private async Task RunWithRetryAsync(
        HttpContext context,
        string prepared,
        string resolvedModel,
        string? requestedModel,
        bool wantsStream,
        string? conversationKey)
    {
        // 试过哪些号（必须真的换，否则 Pick 会挑回同一个）。
        var tried = new List<string>();
        var startedAt = DateTimeOffset.UtcNow;

        // 最后一次失败的结果 —— 尝试耗尽时用它决定客户端拿到的状态码。
        UpstreamResult? lastResult = null;

        // 累计的上游尝试次数（跨两层）—— 用于终局记录的 Attempts。
        var attemptsMade = 0;

        // 外层：换**号**。上限沿用 MaxUpstreamAttempts（默认 3）。
        var maxAccounts = Math.Clamp(_options.MaxUpstreamAttempts, 1, 10);

        for (var round = 1; round <= maxAccounts; round++)
        {
            // 选号。
            //
            // ⚠⚠ **不能按「请求的那个模型」选号** —— 这是 t28 修掉的一个真 bug：
            //
            //   `Pick(model)` 会把「该号的这个模型正在冷却」的成员**整个跳过**。
            //   于是「hy3 被限流」时 `Pick("hy3")` 直接返回 null →
            //   外层立刻走 `WriteNoAccountAsync` 回 429 ——
            //   **内层的「换模型」根本轮不到执行**。
            //
            //   而这恰恰是 t28 的核心价值所在（上游文案自己说「切换其他模型继续使用」）。
            //   实测：单号池 + hy3 被冷 → 修之前直接 429，但它其实还能用 hy4-preview 跑。
            //
            // 正确做法：**选一个「至少还有一个可用模型」的号**，
            // 然后由内层去挑那个可用模型。所以这里传空模型去问池子
            // 「有没有哪个号还能用」，再在内层按模型逐个筛。
            var member = _affinity is not null
                ? _affinity.Select(conversationKey, resolvedModel, tried)
                : PickAccountWithAnyUsableModel(resolvedModel, tried);

            if (member is null)
            {
                // 号用完了。三种情况：
                //   ① 一开始就没号（tried 空）—— 保险库空/全冷却
                //   ② 试光了所有号，最后一次是**限流** → 429（配额问题）
                //   ③ 试光了所有号，最后一次是**服务端错误** → 502（上游挂了）
                //
                // ⚠ ②③ 必须分开：把 5xx 也回成 429 会让客户端把
                // 「上游服务端故障」误判成「我被限流了」，重试策略跑偏。
                await WriteNoAccountAsync(context, resolvedModel, requestedModel, wantsStream, tried, lastResult,
                        Math.Max(1, attemptsMade))
                    .ConfigureAwait(false);
                return;
            }

            // ★ 登记在途（并发保护：达上限的号不再被挑中）。
            //   选号本身可能没登记（Pick / 亲和路径），这里补一次 ——
            //   用 using 保证「成功 / 失败 / 取消」任何一条路径都归还。
            //   这就是「请求并发走池子、不压同一个号」的关键。
            using var lease = Pool.Lease(member, resolvedModel);

            if (lease is null)
            {
                // 达在途上限且策略 Reject：这个号暂时不能再用。
                // 记进 tried，下一轮选别的号。
                tried.Add(member.Id);
                continue;
            }

            // ── 内层：同一个号内换**模型** ────────────────────────
            //
            // 候选 = 当前模型 + 池里其它可用模型。当前模型排在最前 ——
            // 没被限时第一枪就是它，行为与「不降级」时完全一致。
            var candidates = BuildModelCandidates(member, resolvedModel);

            foreach (var model in candidates)
            {
                // ⚠ 冷却中的「账号+模型」**直接跳过、连请求都不发**。
                //   发了的话会出网→被 429→再记一次失败，污染指标又白占在途名额。
                if (!Pool.CanPick(member.Id, model))
                {
                    if (_log.IsEnabled(LogLevel.Debug)) LogSkippingCooling(member.Id, model);
                    continue;
                }

                // 换模型要重新过管线（body 里的 model 必须跟着换 —— t11 的教训）
                var body = model == resolvedModel
                    ? prepared
                    : RequestPipeline.Prepare(_variant, BuildBodyWithModel(prepared, model), model);

                var credential = member.Entry.ToCredential();
                attemptsMade++;

                UpstreamResult result;
                try
                {
                    result = await _upstream
                        .ChatStreamAsync(credential, body, null, null, context.RequestAborted)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    // 用户按了「停止生成」—— 不是错误，立刻中止循环、不重试。
                    //
                    // 但**粘性要留住**：这个号没坏，只是用户不想要这段回答了。
                    // 掐掉映射的话，用户下一句会跳到别的号上，上下文白丢。
                    Notify(requestedModel ?? "", model, member, "cancelled", null, 0, startedAt, null,
                        wantsStream, context, 499, attemptsMade);
                    return;
                }

                if (result.Ok && result.Response is not null)
                {
                    using var response = result.Response;

                    if (wantsStream)
                    {
                        await StreamAsync(context, response, member, model, requestedModel, startedAt, attemptsMade)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await CollectAsync(context, response, member, model, requestedModel, startedAt, attemptsMade)
                            .ConfigureAwait(false);
                    }

                    return;
                }

                // ── 失败：先判要不要重试 ────────────────────────
                var kind = result.Kind ?? UpstreamErrorKind.Unknown;

                if (!IsRetryable(kind))
                {
                    // 不可重试（凭证失效 / 请求本身有问题）：**这一枪就是结论**。
                    // 刻意不调 RecordAttempt（同 t16 的修正：没有下一枪就没有「中间」）。
                    await HandleUpstreamFailureAsync(context, result, member, model, requestedModel,
                        startedAt, wantsStream, conversationKey, attemptsMade).ConfigureAwait(false);
                    return;
                }

                // 可重试 —— 这才是真正的「中间尝试」：它后面还有一枪。
                // 记 isTerminal:false（只进诊断口径，不进成功率）。
                RecordAttempt(member, model, requestedModel, kind, result.Status, startedAt, wantsStream,
                    context, kind switch
                    {
                        UpstreamErrorKind.Unauthorized => 401,
                        UpstreamErrorKind.RateLimited or UpstreamErrorKind.ModelRateLimited => 429,
                        UpstreamErrorKind.BadRequest => 400,
                        _ => 502,
                    }, attemptsMade);

                // 冷却这个「账号 + 模型」组合。
                //
                // ⚠ 冷却必须按**这个号 + 这个模型**记 —— 这正是 t11 修过的那类
                //   bug（限了 A 却记成 B）。这里两个维度都是当前实际发出去的那个。
                CooldownForRetry(member, model, result, conversationKey);

                lastResult = result;

                if (_log.IsEnabled(LogLevel.Information))
                {
                    var attemptNo = attemptsMade;
                    var accountId = member.Id;
                    var usedModel = model;
                    LogRetrying(attemptNo, kind, result.Status, accountId, usedModel, tried.Count);
                }

                // 5xx / 传输失败：换模型没用（不是模型的问题），直接跳出内层换号。
                if (kind == UpstreamErrorKind.Server) break;

                // 限流：继续内层循环，换下一个模型（这是本层的意义）。
            }

            // 内层跑完（模型耗尽，或服务端错误直接跳出）—— 换号。
            // 把刚试过的号记进 tried，保证下一轮 Pick 不会挑回它。
            tried.Add(member.Id);
        }

        // 循环跑满还没成功 —— 按**最后一次失败的类型**映射。
        // （正常路径上 Pick 会先返回 null，走上面的 WriteNoAccountAsync；
        //   这里是防御性的兜底，保证无论如何都不会漏出一个 undefined。）
        //
        // ⚠ 绝不能返回 undefined/null（旧版踩过：调用方读 .ok 直接 TypeError）。
        var exhaustedIsServer = lastResult?.Kind == UpstreamErrorKind.Server;
        var exhaustedKind = exhaustedIsServer ? UpstreamErrorKind.Server : "soft_rate";
        var exhaustedStatus = exhaustedIsServer ? 502 : 429;

        // ⚠ 文案对齐旧版客户端可见契约：含「试过 N 次」
        var exhaustedMessage = exhaustedIsServer
            ? (lastResult!.Status == 0
                ? $"试了 {tried.Count} 个账号都连不上上游：{Summarize(lastResult.Message)}"
                : $"试了 {tried.Count} 个账号，上游都返回服务端错误：{Summarize(lastResult.Message)}")
            : $"所有可用模型都在限流冷却中（试过 {Math.Max(1, attemptsMade)} 次）";

        // 终局记录 —— 尝试耗尽的结论。
        Observe(new RequestRecord
        {
            At = DateTimeOffset.UtcNow,
            RequestId = RequestIdOf(context),
            Model = requestedModel ?? "",
            UsedModel = resolvedModel,
            Status = exhaustedStatus,
            DurationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
            AccountTag = "",
            ErrorKind = exhaustedKind,
            Outcome = "failed",
            Streamed = wantsStream,
            Attempts = Math.Max(1, attemptsMade),
            SentToUpstream = attemptsMade > 0,
        });

        await WriteErrorAsync(context, exhaustedStatus, exhaustedKind, exhaustedMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// 内层候选模型列表：**当前模型排最前**，其余按池里模型目录的顺序补上。
    ///
    /// 当前模型排最前很重要 —— 它没被限时第一枪就是它，
    /// **行为与「不降级」时逐字节一致**（既有测试靠这个不被打破）。
    ///
    /// 上限 4：旧版内层就是 `for (attempt = 0; attempt &lt; 4; attempt++)`。
    /// </summary>
    private List<string> BuildModelCandidates(PoolMember member, string currentModel)
    {
        _ = member;
        var list = new List<string> { currentModel };

        foreach (var model in _catalog.Models)
        {
            if (list.Count >= ModelFallbackLimit) break;
            if (!list.Contains(model.Id, StringComparer.Ordinal)) list.Add(model.Id);
        }

        return list;
    }

    /// <summary>
    /// 选一个**至少还有一个可用模型**的号。
    ///
    /// ## 为什么不能直接用 <see cref="AccountPool.Pick"/>
    ///
    /// `Pick(model)` 把「该号的这个模型在冷却中」的成员整个排除。
    /// 于是「hy3 被限流」时它会返回 null，外层直接回 429 ——
    /// **内层「换模型」那条路根本走不到**，t28 的核心价值被架空。
    ///
    /// 实测（单号池 + hy3 被冷）：
    /// ```
    /// CanPick(id, "hy3")         = False   ← 这个模型被冷
    /// CanPick(id, "hy4-preview") = True    ← 但这个号还能用别的模型跑
    /// Pick("hy3")                = null    ← 所以按 hy3 选号会误判「没号可用」
    /// ```
    ///
    /// ## 怎么做
    ///
    /// 挨个问成员「你有没有**任意**一个候选模型可用」——
    /// 有就选它，具体用哪个模型交给内层去挑。
    ///
    /// **排序仍然沿用池子的公平规则**（先用 token 快过期的、再选用得最少的），
    /// 保证长期负载均匀不被这次改动打破 —— 只是候选集合从
    /// 「该模型可用的号」放宽成「有任意可用模型的号」。
    /// </summary>
    private PoolMember? PickAccountWithAnyUsableModel(string currentModel, IReadOnlyCollection<string> exclude)
    {
        // 候选号 = 池里所有没被排除、没被冷冻的成员。
        // （冷却**不**在这里排除 —— 内层会按具体模型逐条筛。）
        var viable = Pool.Members
            .Where(m => !exclude.Contains(m.Id))
            .Where(m => !Pool.IsFrozen(m.Id))
            .Where(m => BuildModelCandidates(m, currentModel).Any(model => Pool.CanPick(m.Id, model)))
            .ToList();

        if (viable.Count == 0) return null;

        // ⚠ 这里**必须复用池子的负载排序**，不能自己另写一份。
        //
        // 实测教训（e2e 的 t61，真机）：本方法原先自己写了一套排序
        //   .OrderBy(ExpiresAtMs).ThenBy(SelectionCount)
        // —— 与 `AccountPool.Pick` 里的**同一处错误**：主键是过期时间。
        // 19 个号的过期时刻各不相同、且主号最小 ⇒ 它永远排第一，
        // **负载（SelectionCount / InFlight）永远轮不到生效**
        // ⇒ 6 个并发请求全落同一个号。
        //
        // 更糟的是：这份自写排序**完全绕过了 `AccountPool` 的 InFlight 计数**，
        // 所以即使池子那边改对了，走这条路的请求依然不分散。
        // ⇒ 选号逻辑只有一个真源（池子），这里只负责「筛出候选」。
        return viable
            .OrderBy(m => m.InFlight)
            .ThenBy(m => m.SelectionCount)
            .ThenBy(m => m.Entry.ExpiresAtMs == 0 ? long.MaxValue : m.Entry.ExpiresAtMs)
            .First();
    }

    /// <summary>内层最多换几个模型（对齐旧版 `sendWithFallback` 的 4）。</summary>
    private const int ModelFallbackLimit = 4;

    /// <summary>把 body 里的 `model` 换成另一个（内层换模型时用）。</summary>
    private static string BuildBodyWithModel(string prepared, string model)
    {
        try
        {
            if (JsonNode.Parse(prepared) is not JsonObject obj) return prepared;
            obj["model"] = model;
            return obj.ToJsonString();
        }
        catch (JsonException)
        {
            return prepared;
        }
    }


    /// <summary>
    /// 这个失败类型值不值得换号重试。
    ///
    /// ⚠ 401/403 和 400 **必须**返回 false：
    ///   · 凭证失效是账号本身要重新登录，换号重放没用；
    ///   · 请求本身有问题时，换号重放同样的坏请求只会把健康的号拖进冷却。
    /// </summary>
    private static bool IsRetryable(string kind) => kind switch
    {
        UpstreamErrorKind.RateLimited => true,
        UpstreamErrorKind.ModelRateLimited => true,
        UpstreamErrorKind.Server => true,
        _ => false,
    };

    /// <summary>
    /// 一次失败尝试之后把那个号冷却掉，并让会话粘性让路。
    ///
    /// **冷却必须按实际发出去的模型记**（`model` 参数）—— 记错了会
    /// 把用户真正想用的模型白拉黑，见 <see cref="RunWithRetryAsync"/> 里
    /// 关于「两个模型名」的说明。
    /// </summary>
    private void CooldownForRetry(
        PoolMember member,
        string model,
        UpstreamResult result,
        string? conversationKey)
    {
        var kind = result.Kind ?? UpstreamErrorKind.Unknown;

        if (kind is UpstreamErrorKind.RateLimited or UpstreamErrorKind.ModelRateLimited)
        {
            Pool.MarkRateLimited(member.Id, model, ResetAtFromBody(result.Message));

            // 这个号对这个模型不能用了 —— 粘性必须让路，
            // 否则下一轮还会把同一个会话塞给同一个坏号。
            _affinity?.Revoke(conversationKey);
        }
        else
        {
            // 5xx / 传输失败：记失败次数（连续到阈值会整体冷冻），
            // 但不按模型冷却 —— 那多半是那个节点抽风，不是限流。
            //
            // ⚠ 传 status + kind，**不要用无条件记账的旧重载**。
            //   旧重载会把「连接层失败」（status == 0：DNS / 连接被拒 / TLS / 超时）
            //   也记到账号头上。后果是真机实测过的：
            //     本机系统代理没生效 → 所有请求 status = 0
            //     → 8 个健康账号各被记 5 次 → 全部冷冻
            //     → 之后一律 429「没有可用账号」，界面看不出是环境问题
            //   环境问题不该惩罚账号 —— 判据在 TransportFailure.ShouldCountAgainstAccount。
            //
            // ⚠ status 映射与 `HandleFailureAsync` 里那张表**保持一致**。
            //   （两处各写一份正是本缺陷的成因之一 —— 各写一份就会出现
            //     「一处认为 0 是环境问题、另一处认为是 server 错误」。）
            //   `UpstreamResult.Status` 有真值时优先用它；没有才按 Kind 推。
            var failureStatus = result.Status != 0
                ? result.Status
                : kind switch
                {
                    UpstreamErrorKind.Unauthorized => 401,
                    UpstreamErrorKind.RateLimited or UpstreamErrorKind.ModelRateLimited => 429,
                    UpstreamErrorKind.BadRequest => 400,
                    _ => 502,
                };

            if (Pool.MarkFailure(member.Id, model, failureStatus, result.Kind))
            {
                if (Pool.IsFrozen(member.Id))
                {
                    _affinity?.RevokeAccount(member.Id);
                }
            }
        }
    }

    /// <summary>
    /// 没有可用账号时的输出（统一在一处，避免两套措辞）。
    ///
    /// @param lastResult 最后一次失败的结果。null = 从头就没号。
    ///                   非 null 时按它的类型决定状态码（限流 429 / 服务端 502）。
    /// @param attempts 这次客户端请求已经试过几次（含失败的那些）。
    ///                 界面上「这次请求最终失败了，而且它试了 3 次」靠它。
    /// </summary>
    private async Task WriteNoAccountAsync(
        HttpContext context,
        string resolvedModel,
        string? requestedModel,
        bool wantsStream,
        List<string> tried,
        UpstreamResult? lastResult,
        int attempts = 1)
    {
        // 按最后一次失败的类型决定状态码与错误类型 —— 不能一律 429/soft_rate。
        //
        // ⚠ `soft_rate` 这个名字是**客户端可见契约**：旧版 Node 网关在
        // 「所有号都在限流冷却中」时回的就是 `{status:429, kind:"soft_rate"}`
        // （gateway.js:475 / :497），客户端可能按它做分支。
        // 所以「试光了所有号」这一档继续叫 soft_rate，而不是上游原始的 rate_limit。
        // 换言之：`soft_rate` = 网关侧的「没号可用了」，`rate_limit` = 上游说的「你太快了」。
        var kind = lastResult is null
            ? "soft_rate"
            : lastResult.Kind == UpstreamErrorKind.Server ? UpstreamErrorKind.Server : "soft_rate";
        var status = lastResult is null
            ? 429
            : lastResult.Kind == UpstreamErrorKind.Server ? 502 : 429;

        var message = (lastResult, tried.Count) switch
        {
            (null, _) => $"没有可用账号（{resolvedModel} 正在冷却中，或者所有号都连续失败被冷冻）",
            (_, 0) => $"没有可用账号（{resolvedModel} 正在冷却中，或者所有号都连续失败被冷冻）",
            (_, _) when kind == UpstreamErrorKind.Server =>
                // ⚠ 5xx 和「连不上」要分开说 —— 对用户是两件事：
                // 一个是「上游服务端挂了」，一个是「这台机器根本连不出去」
                // （DNS / 代理 / 防火墙）。提示语不同，排查方向也完全不同。
                lastResult!.Status == 0
                    ? $"试了 {tried.Count} 个账号都连不上上游：{Summarize(lastResult.Message)}"
                    : $"试了 {tried.Count} 个账号，上游都返回服务端错误：{Summarize(lastResult.Message)}",
            _ => $"试了 {tried.Count} 个账号都被上游限流（{resolvedModel}），请稍后重试或换模型",
        };

        // ⚠ 客户端拿到的必须是明确的状态码（配额 429 / 上游故障 502），不是 500。
        // 旧版的坑：所有号都在冷却里时返回 undefined，调用方读 .ok 直接 TypeError。
        //
        // ⚠⚠ 这是**终局**记录（t16 口径，默认 isTerminal=true）：
        //    「试光了所有号」是这次客户端请求的结论，所以要进主口径。
        //    之前那几次失败的重试已经以 isTerminal=false 记过，不重复计入总数。
        //
        // ⚠⚠ `SentToUpstream = false`：最后这一轮 Pick 返回了 null，
        //    **请求压根没发出网**。所以它不该被算进 `AttemptCount`
        //    （那个数是「上游被打了几次」的体检指标，不能被本地短路污染）。
        //    真正发出去过的那些尝试，由 RecordAttempt 那边以 sentToUpstream=true 记过。
        Observe(new RequestRecord
        {
            At = DateTimeOffset.UtcNow,
            RequestId = RequestIdOf(context),
            Model = requestedModel ?? "",
            UsedModel = resolvedModel,
            Status = status,
            DurationMs = 0,
            AccountTag = "",
            ErrorKind = kind,
            Outcome = "failed",
            Streamed = wantsStream,
            Attempts = attempts < 1 ? 1 : attempts,
            SentToUpstream = false,
        });

        await WriteErrorAsync(context, status, kind, message).ConfigureAwait(false);
    }

    /// <summary>
    /// 记一次**失败的中间尝试**（终局失败由 <see cref="HandleUpstreamFailureAsync"/>
    /// 或 <see cref="WriteNoAccountAsync"/> 自己记）。
    ///
    /// 每次尝试都要记 —— observability 的指标和脱敏日志靠它，
    /// 漏记一次「试了 3 次才成功」就会被统计成「1 次成功」。
    ///
    /// ## ⚠ 这里的记录**不是终局**（t16 口径改造）
    ///
    /// 它带 <c>Terminal = false</c>，所以：
    ///   · **会**进 <c>AttemptCount</c>（上游尝试次数，诊断口径）
    ///   · **会**进 <c>Recent</c> 和 <c>ErrorKinds</c>（界面要看得见重试）
    ///   · **不会**进 <c>TotalRequests</c> / 成功率 / 平均耗时（主口径）
    ///
    /// 这样「一次客户端请求 = 一条终局记录」才成立，而重试过程又不丢。
    /// </summary>
    private void RecordAttempt(
        PoolMember member,
        string usedModel,
        string? requestedModel,
        string kind,
        int upstreamStatus,
        DateTimeOffset startedAt,
        bool wantsStream,
        HttpContext context,
        int clientStatus,
        int attempt)
    {
        if (_log.IsEnabled(LogLevel.Warning))
        {
            LogUpstreamFailed(clientStatus, kind, upstreamStatus, member.Id, usedModel);
        }

        Notify(requestedModel ?? "", usedModel, member, "failed", kind, upstreamStatus, startedAt, null,
            wantsStream, context, clientStatus, attempt, isTerminal: false);
    }

    /// <summary>
    /// 上游失败 → 客户端状态码。
    ///
    /// ```
    /// 401/403 → 401（凭证失效，要重新登录）
    /// 429     → 429（并把该号该模型 MarkRateLimited）
    /// 5xx / 传输失败 → 502
    /// 其他 4xx → 400
    /// ```
    /// </summary>
    private async Task HandleUpstreamFailureAsync(
        HttpContext context,
        UpstreamResult result,
        PoolMember member,
        string usedModel,
        string? requestedModel,
        DateTimeOffset startedAt,
        bool wantsStream,
        string? conversationKey,
        int attempt = 1)
    {
        var kind = result.Kind ?? UpstreamErrorKind.Unknown;
        var status = kind switch
        {
            UpstreamErrorKind.Unauthorized => 401,
            UpstreamErrorKind.RateLimited or UpstreamErrorKind.ModelRateLimited => 429,
            UpstreamErrorKind.BadRequest => 400,
            _ => 502,
        };

        // 限流要记账 —— 下次选号就跳过这个号的这个模型。
        // 不分模型的话，一个模型被限就把整个号拉黑，白扔配额。
        if (kind is UpstreamErrorKind.RateLimited or UpstreamErrorKind.ModelRateLimited)
        {
            Pool.MarkRateLimited(member.Id, usedModel, ResetAtFromBody(result.Message));

            // 这个号对这个模型不能用了 —— 会话粘性必须让路，
            // 否则下一轮还会把同一个会话塞给同一个坏号，一直撞到 TTL 到期。
            _affinity?.Revoke(conversationKey);
        }
        else if (kind == UpstreamErrorKind.Unauthorized)
        {
            // 凭证失效不算「这个号坏了」，只是要重新登录 —— 不计失败次数。
            // 但**粘性要清**：这个号现在发不了请求，继续粘着只会一直 401。
            _affinity?.Revoke(conversationKey);
        }
        else
        {
            // ⚠ 同 `CooldownForRetry`：必须传 status + kind，
            //   否则连接层失败（status == 0）会被记到账号头上。
            if (Pool.MarkFailure(member.Id, usedModel, status, result.Kind))
            {
                // 连续失败到阈值 → 整个号被冷冻，它上面的**所有**会话都得让路
                if (Pool.IsFrozen(member.Id))
                {
                    _affinity?.RevokeAccount(member.Id);
                }
            }
        }

        var detail = Summarize(result.Message);
        var message = result.Status == 0
            ? $"连不上上游：{detail}"
            : $"workbuddy upstream {kind} (http {result.Status}): {detail}";

        if (_log.IsEnabled(LogLevel.Warning))
        {
            LogUpstreamFailed(status, kind, result.Status, member.Id, usedModel);
        }

        // ⚠ 这里记的是**回给客户端的**状态码（status），不是上游的（result.Status）。
        // 指标里的「HTTP 成功率」说的是「用户拿到的响应对不对」，
        // 拿上游状态码去算会得到完全不同的结论（上游 429 → 客户端也是 429，
        // 但上游 200+业务错误 → 我们可能回 400）。
        //
        // ⚠⚠ 这是**终局**记录（t16 口径）：不可重试的失败就是这次客户端请求
        //    的结论，所以它进主口径。带 attempt 是为了让界面知道
        //    「这次是在第几次尝试上定局的」（不可重试的类型通常就是第 1 次）。
        Notify(requestedModel ?? "", usedModel, member, "failed", kind, result.Status, startedAt, null, wantsStream,
            context, status, attempt);

        await WriteErrorAsync(context, status, kind, message).ConfigureAwait(false);
    }

    /// <summary>
    /// 从上游错误正文里抠出**冷却截止时刻**。
    ///
    /// ## 两个来源都要试（t28 补的第二个）
    ///
    /// **① 结构化字段**：`resetAt` / `reset_at` / `resetTime`（毫秒或秒，
    /// 用 `Expiry.Normalize` 归一）。有的接口直接给这个。
    ///
    /// **② 人类可读文案**：上游实际发的是这句（`ratelimit.js:10` 实测原文）
    ///
    /// ```
    /// {"code":6004,"msg":"您的使用量已超出频率限制，将在 2026-09-25 17:07:48 UTC+8
    ///                    重置，您也可以切换其他模型继续使用。"}
    /// ```
    ///
    /// **它没有字段，只有文案** —— 所以必须能从文案里把时刻解出来。
    /// 否则只能退回「默认冷却 5 分钟」，那在「还有 30 秒就恢复」时白等 4 分半、
    /// 在「2 小时后恢复」时白撞一次（还污染指标）。
    ///
    /// 解析在 <see cref="RateLimitReset.Parse"/>（逐条对齐旧版 `ratelimit.js:51`，
    /// 含两种文案格式与偏移换算，且有 python 独立复算过的期望值）。
    ///
    /// @returns 截止时刻；**两边都解析不出来时返回 null** ——
    ///          调用方退回默认冷却。返回 null 的语义是「上游没说」，
    ///          不是「立刻可重试」。
    /// </summary>
    private static long? ResetAtFromBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;

        // ① 结构化字段优先（更精确，不依赖文案格式）
        try
        {
            if (JsonNode.Parse(body) is JsonObject obj)
            {
                foreach (var key in (string[])["resetAt", "reset_at", "resetTime"])
                {
                    if (obj[key] is JsonValue v && v.TryGetValue<long>(out var raw) && raw > 0)
                    {
                        return Expiry.Normalize(raw);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 正文不是 JSON —— 下面文案解析那条路照样能用，不算失败
        }

        // ② 文案里的时刻（t28：这是上游实际给的那个）
        return RateLimitReset.Parse(body);
    }

    /// <summary>把上游正文压成一句能给用户看的话。绝不返回空串。</summary>
    private static string Summarize(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "上游没有给出原因";

        var text = body.Trim();

        // 优先用上游自己的 displayMsg —— 业务错误（安全审核、非法请求）
        // 的真实原因在那里，贴整个信封会把它埋掉。
        try
        {
            if (JsonNode.Parse(text) is JsonObject obj)
            {
                foreach (var key in (string[])["displayMsg", "display_msg", "message", "msg", "error"])
                {
                    var value = obj[key] switch
                    {
                        JsonValue v when v.TryGetValue<string>(out var s) => s,
                        JsonObject o => o["message"]?.GetValue<string>(),
                        _ => null,
                    };

                    if (!string.IsNullOrWhiteSpace(value)) return Truncate(value.Trim(), 400);
                }
            }
        }
        catch (JsonException)
        {
        }

        return Truncate(text, 400);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ── 流式 ──────────────────────────────────────────────────

    /// <summary>
    /// 真流式转发。
    ///
    /// **边收边发**：读一行、清洗、立刻写进响应并 flush。
    /// 绝不能先 ReadAsStringAsync 收完再发 —— 那等于把上游的流式抹掉，
    /// 用户要等到最后一个 token 才看到第一个字。
    /// </summary>
    private async Task StreamAsync(
        HttpContext context,
        HttpResponseMessage upstream,
        PoolMember member,
        string usedModel,
        string? requestedModel,
        DateTimeOffset startedAt,
        int attempt = 1)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        // 关掉响应缓冲 —— 不关的话 Kestrel 会攒够了才吐，流式又没了
        context.Response.Headers["X-Accel-Buffering"] = "no";

        JsonNode? usage = null;

        // ★ **流式下的工具调用收集**（用户实测：对话里看不到生图/生视频）。
        //
        // 流式路径与非流式不同：chunk 是**边收边转发**的，收完最后一块时
        // 响应已经发出去大半了。所以不能"收完再决定发什么"，
        // 只能**边转发边攒 tool_calls 分片**，等上游说 `finish_reason: tool_calls`
        // 时再执行、并把产物 URL 作为**额外的 content chunk** 补发。
        //
        // ⚠ 为什么补发 content 而不是补发 tool_calls：
        //   客户端（我们的界面）不会执行工具 —— 它只认正文里的 URL。
        //   补发 tool_calls 等于把没人干的活又丢回去一次。
        var streamCollector = new ChatStreamCollector();

        try
        {
            await using var body = await upstream.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);

            await foreach (var payload in SseReader.ReadDataAsync(body, context.RequestAborted).ConfigureAwait(false))
            {
                var chunk = SseSanitizer.Process(payload);

                if (chunk.Usage is not null) usage = chunk.Usage;

                if (chunk.IsDone)
                {
                    // ★ 在处理 [DONE] **之前**执行工具并补发产物 URL。
                    //   放在这里是因为：到这一刻 tool_calls 才攒齐
                    //   （它的分片散在多个 chunk 里），而响应还没关。
                    var produced = await RunMediaToolsForStreamAsync(
                        streamCollector, context, usedModel).ConfigureAwait(false);

                    if (produced.Count > 0)
                    {
                        foreach (var url in produced)
                        {
                            await WriteSseAsync(context, BuildContentChunk(usedModel, "\n\n" + url))
                                .ConfigureAwait(false);
                        }
                    }

                    await WriteSseAsync(context, "[DONE]").ConfigureAwait(false);
                    break;
                }

                if (chunk.Raw is null) continue;

                // 攒一份用于事后提取 tool_calls（**转发的内容不变**）
                streamCollector.Feed(chunk);

                await WriteSseAsync(context, RenameModel(chunk.Raw, usedModel)).ConfigureAwait(false);
            }

            Pool.MarkSuccess(member.Id, usedModel);

            // 终局 + 真实尝试次数 —— 界面靠 attempts > 1 看出「换号救回来的」
            Notify(requestedModel ?? "", usedModel, member, "ok", null, 200, startedAt, usage, true, context, 200, attempt);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端断开：正常结束，不要往已经关掉的响应里写东西。
            //
            // 粘性留住（号没坏，只是用户走了），但**指标上记成 499** ——
            // 它不该被算进 HTTP 成功率里（响应没有正常发完），
            // 也不该被算成服务端失败。
            //
            // ⚠ **不记账号失败**。这是「用户按了停止」，不是「这个号坏了」。
            //   旧代码在这里调了无条件记账的 MarkFailure ——
            //   后果是用户点几次停止就能把号冻掉。
            //   传 status=499 + kind=Unknown，判据会拒绝把它记到账号头上
            //   （取消既不是传输失败也不是上游错误）。
            Pool.MarkFailure(member.Id, usedModel, 499, UpstreamErrorKind.Unknown);
            Notify(requestedModel ?? "", usedModel, member, "cancelled", null, 200, startedAt, usage, true, context, 499, attempt);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // 流到一半上游断了。响应头已经发出去，改不了状态码 ——
            // 只能把错误以 SSE 的形式告诉客户端，然后关流。
            if (_log.IsEnabled(LogLevel.Warning)) LogStreamFailed(ex, member.Id, usedModel);

            // ⚠ 这里 status 传 **0**（传输层断裂），不是 502。
            //   502 是「上游返回了 5xx」的映射值，而这是「连接断了」——
            //   判据（TransportFailure.ShouldCountAgainstAccount）按 0 把它
            //   认成环境问题，不记到账号头上。传 502 会让它变成账号失败。
            Pool.MarkFailure(member.Id, usedModel, 0, UpstreamErrorKind.Server);
            Notify(requestedModel ?? "", usedModel, member, "failed", UpstreamErrorKind.Server, 0, startedAt, usage, true,
                context, 502, attempt);

            try
            {
                await WriteSseAsync(context, new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["message"] = $"上游流中断：{ex.Message}",
                        ["type"] = UpstreamErrorKind.Server,
                        ["code"] = UpstreamErrorKind.Server,
                    },
                }.ToJsonString()).ConfigureAwait(false);
            }
            catch (Exception inner) when (inner is IOException or ObjectDisposedException)
            {
                // 客户端已经走了，写不进去就算了
            }
        }
    }

    /// <summary>写一条 SSE 事件并**立刻 flush**（不 flush 就不是流式了）。</summary>
    private static async Task WriteSseAsync(HttpContext context, string payload)
    {
        await context.Response.WriteAsync($"data: {payload}\n\n", context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// 把分片里的 model 换成**实际用的那个**。
    ///
    /// ## 它现在为什么是「自洽保障」而不是「错配来源」
    ///
    /// t11 之前这里有个坑：请求体里的 `model` 是**客户端原始别名**（比如 `gpt-4o`），
    /// 而这个方法把**响应**里的 model 改成了 `usedModel`（`hy3`）。
    /// 于是同一个请求，上游跑的是 A、客户端看到的是 B —— 排查时会被彻底带偏。
    ///
    /// t11 之后请求体也按 `usedModel` 发出去了
    /// （见 `RequestPipeline.Prepare(_variant, rawBody, resolvedModel)`），
    /// 所以**请求和响应两边说的都是同一个模型名**，这里的改写从
    /// 「制造不一致」变成了「保证一致」：即使上游回显了它自己的模型名
    /// （有的上游会回基础模型而非别名），客户端看到的仍然是我们实际用的那个。
    ///
    /// 解析失败就原样返回 —— 绝不因为改个名字把流转坏。
    /// </summary>
    private static string RenameModel(string payload, string usedModel)
    {
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject obj) return payload;
            if (obj["model"] is null) return payload;

            obj["model"] = usedModel;

            // ⚠ 必须用宽松编码器：默认的 JsonNode.ToJsonString 会把中文
            // 转义成 \uXXXX。正文里全是中文，转义之后客户端要解回来，
            // 而且日志/抓包看起来是一坨乱码（测试抓到的就是这个）。
            return obj.ToJsonString(RelaxedJson);
        }
        catch (JsonException)
        {
            return payload;
        }
    }

    /// <summary>不转义中文的 JSON 选项。</summary>
    private static readonly JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── 非流式 ────────────────────────────────────────────────

    /// <summary>
    /// 非流式：把整条流收完，重新拼成 `chat.completion`。
    /// 上游只支持流式，所以这是唯一的路（见 <see cref="ChatStreamCollector"/>）。
    /// </summary>
    private async Task CollectAsync(
        HttpContext context,
        HttpResponseMessage upstream,
        PoolMember member,
        string usedModel,
        string? requestedModel,
        DateTimeOffset startedAt,
        int attempt = 1)
    {
        var collector = new ChatStreamCollector();

        try
        {
            await using var body = await upstream.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);

            await foreach (var payload in SseReader.ReadDataAsync(body, context.RequestAborted).ConfigureAwait(false))
            {
                var chunk = SseSanitizer.Process(payload);
                if (chunk.IsDone) break;
                collector.Feed(chunk);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 同上面的取消分支：**不记账号失败**。
            // 用户按停止不该把号冻掉（status=499 + Unknown ⇒ 判据拒绝记账）。
            Pool.MarkFailure(member.Id, usedModel, 499, UpstreamErrorKind.Unknown);
            Notify(requestedModel ?? "", usedModel, member, "cancelled", null, 200, startedAt, null, false, context, 499, attempt);
            return;
        }

        Pool.MarkSuccess(member.Id, usedModel);

        var completion = collector.Build(usedModel, $"chatcmpl-{Guid.NewGuid():N}"[..24]);

        // ★ **网关自己执行媒体工具**（用户实测点名的那条：聊天里看不到生图/生视频）。
        //
        // 上游把 `finish_reason: "tool_calls"` 交给客户端，默认语义是客户端执行。
        // 但我们的界面**执行不了** —— 执行要账号、要调上游、要落盘（只有网关有这些）。
        // 所以在这里跑：把工具产物的 URL 追加成文本，客户端就能照常渲染。
        completion = await RunMediaToolsAsync(completion, context).ConfigureAwait(false);

        Notify(requestedModel ?? "", usedModel, member, "ok", null, 200, startedAt, collector.Usage, false, context, 200, attempt);

        await WriteJsonAsync(context, 200, completion).ConfigureAwait(false);
    }

    /// <summary>
    /// 执行回复里的媒体工具调用，并把产物 URL 追加进正文。
    ///
    /// ## 为什么是「追加文本」而不是「另开一个字段」
    ///
    /// OpenAI 的 `chat.completion` 里没有"媒体结果"这个位置，
    /// 而界面已经会从**文本里识别 URL**（`ChatMedia.TryExtractMediaUrl`）——
    /// 所以把 URL 写进正文是**复用既有链路**，不需要两端同时改协议。
    ///
    /// ## 为什么失败不把整个回复打掉
    ///
    /// 工具失败（额度不足、上游抽风）时，模型说的话仍然有价值。
    /// 整条 500 掉会让用户以为"聊天坏了"，而其实只是图没出来。
    /// </summary>
    private async Task<JsonObject> RunMediaToolsAsync(JsonObject completion, HttpContext context)
    {
        if (_mediaToolExecutor is null) return completion;

        var calls = CollectToolCalls(completion);
        if (calls.Count == 0) return completion;

        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        List<MediaToolExecutor.ToolOutcome> outcomes;
        try
        {
            outcomes = await _mediaToolExecutor
                .ExecuteAsync(calls, fileName => MediaUrlFor(baseUrl, fileName), context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_log.IsEnabled(LogLevel.Warning)) LogMediaToolFailed(ex, calls.Count);
            return completion;
        }

        // 只把**成功**的产物拼进正文。
        //
        // ⚠ `ToolOutcome` 是 `(bool Ok, string Text)` —— 没有单独的 Url 字段。
        //   Text 就是执行结果的人话/URL（生图那个工具产出的正是 URL）。
        //   我一开始按 `o.Url` 写，编译期才发现类型不对。
        var urls = outcomes
            .Where(o => o.Ok && !string.IsNullOrWhiteSpace(o.Text))
            .Select(o => o.Text.Trim())
            .ToList();

        if (urls.Count == 0) return completion;

        var choice = completion["choices"]?.AsArray().FirstOrDefault()?.AsObject();
        var message = choice?["message"]?.AsObject();
        if (message is null) return completion;

        var existing = message["content"]?.GetValue<string>() ?? "";
        message["content"] = string.IsNullOrWhiteSpace(existing)
            ? string.Join("\n", urls)
            : existing.TrimEnd() + "\n\n" + string.Join("\n", urls);

        // finish_reason 改成 stop：工具已经被**我们**执行完了。
        // 继续回 `tool_calls` 会让客户端以为还要它再执行一遍（然后什么也没发生）。
        choice!["finish_reason"] = "stop";

        return completion;
    }

    /// <summary>拼媒体文件的绝对 URL（与 MediaEndpoints 的约定一致）。</summary>
    private static string MediaUrlFor(string baseUrl, string fileName)
        => $"{baseUrl}/v1/images/file/{Uri.EscapeDataString(fileName)}";

    /// <summary>从完成体里取 tool_calls（没有就回空表）。</summary>
    private static List<JsonObject> CollectToolCalls(JsonObject completion)
    {
        var message = completion["choices"]?.AsArray().FirstOrDefault()?["message"]?.AsObject();
        if (message?["tool_calls"] is not JsonArray arr) return [];

        return arr.OfType<JsonObject>().ToList();
    }

    /// <summary>
    /// 流式路径的工具执行：攒齐的 tool_calls 拿去执行，返回产物 URL 列表。
    ///
    /// ⚠ 与非流式那条**共用同一个执行器与同一个"只拼成功产物"的判据** ——
    ///   两条路径各写一份逻辑的话，很容易一边修好、另一边还是坏的
    ///   （这正是"流式看不到图、非流式看得到"这类 bug 的成因）。
    /// </summary>
    private async Task<IReadOnlyList<string>> RunMediaToolsForStreamAsync(
        ChatStreamCollector collector, HttpContext context, string usedModel)
    {
        if (_mediaToolExecutor is null) return [];

        var calls = collector.ToolCalls;
        if (calls.Count == 0) return [];

        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        try
        {
            var outcomes = await _mediaToolExecutor
                .ExecuteAsync(calls, fileName => MediaUrlFor(baseUrl, fileName), context.RequestAborted)
                .ConfigureAwait(false);

            return outcomes
                .Where(o => o.Ok && !string.IsNullOrWhiteSpace(o.Text))
                .Select(o => o.Text.Trim())
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_log.IsEnabled(LogLevel.Warning)) LogMediaToolFailed(ex, calls.Count);
            return [];
        }
    }

    /// <summary>
    /// 造一个只含正文的 SSE chunk（补发工具产物用）。
    ///
    /// 形状照 OpenAI 的 `chat.completion.chunk` —— 与上游发来的那些
    /// **逐字段对齐**，这样客户端不需要为"网关自己补的块"写第二条解析路径。
    ///
    /// ⚠ **只返回 JSON 载荷，不带 `data: ` 前缀** ——
    /// 前缀由 <see cref="WriteSseAsync"/> 统一加。
    /// 这里如果自己也带前缀，产物块会变成 `data: data: {...}`，
    /// 客户端 `TryReadDelta` 解析失败 ⇒ URL 丢失 ⇒ 生图/生视频展示不出来。
    /// </summary>
    private static string BuildContentChunk(string model, string content)
    {
        var obj = new JsonObject
        {
            ["id"] = $"chatcmpl-{Guid.NewGuid():N}"[..24],
            ["object"] = "chat.completion.chunk",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = new JsonObject { ["content"] = content },
                ["finish_reason"] = null,
            }),
        };

        return obj.ToJsonString();
    }

    // ── 观察者（挂载点） ──────────────────────────────────────

    /// <summary>
    /// 通知观察者。**只传元数据**：没有提示词、没有响应正文、没有 token 原值。
    ///
    /// 观察者抛异常不会影响用户 —— <see cref="ChatObservers.Notify"/> 会吞掉。
    ///
    /// ## 同时喂 P2 的指标和脱敏日志
    ///
    /// 三者共用同一份元数据（<see cref="ChatObservation"/> → <see cref="RequestRecord"/>），
    /// 不重复算一遍耗时、不重复取一次账号标签 —— 两条路径上的时间戳/标签
    /// 必须一致，分开算迟早会不一致。
    ///
    /// @param httpStatus 回给客户端的 HTTP 状态；流式路径响应头已经发出去了，用 200。
    ///                   取消时用 499（nginx 的 "client closed request" 约定），
    ///                   它不算 HTTP 成功，但也不该被当成服务端错误。
    /// @param attempts 这次客户端请求内部已经发生的上游尝试次数（含本次）。
    ///                 重试捞回来的终局记录上它 &gt; 1，界面/日志靠它看见
    ///                 「换号机制被触发过」。
    /// @param isTerminal 这条是不是这次客户端请求的**终局**结果。
    ///                 false = 中间失败的那次尝试，只进诊断口径（AttemptCount /
    ///                 Recent / ErrorKinds），**不进**成功率统计。
    ///                 默认 true —— 绝大多数调用点（没账号、鉴权失败、被取消）
    ///                 都是一次请求一条终局记录。
    /// </summary>
    private void Notify(
        string requestedModel,
        string usedModel,
        PoolMember member,
        string outcome,
        string? kind,
        int upstreamStatus,
        DateTimeOffset startedAt,
        JsonNode? usage,
        bool streamed,
        HttpContext? context = null,
        int httpStatus = 200,
        int attempts = 1,
        bool isTerminal = true)
    {
        var accountTag = AccountTagOf(member);
        var elapsedMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

        if (_observers.HasObservers)
        {
            _observers.Notify(new ChatObservation
            {
                RequestedModel = requestedModel,
                UsedModel = usedModel,
                AccountTag = accountTag,
                MemberId = member.Id,
                Outcome = outcome,
                Kind = kind,
                UpstreamStatus = upstreamStatus,
                // ⚠ 这里给的是**真实尝试次数**（t16 之前恒为默认值 1）——
                // 契约里 Attempts 的语义就是「试了几次」，固定成 1 等于没这个字段。
                Attempts = attempts < 1 ? 1 : attempts,
                ElapsedMs = elapsedMs,
                Usage = usage,
                Streamed = streamed,
            });
        }

        if (_metrics is null && _requestLog is null) return;

        var record = new RequestRecord
        {
            At = DateTimeOffset.UtcNow,
            RequestId = context is null ? RequestIds.NewId() : RequestIdOf(context),
            Model = requestedModel,
            UsedModel = usedModel,
            Status = httpStatus,
            DurationMs = elapsedMs,
            AccountTag = accountTag,
            ErrorKind = kind ?? "",
            Outcome = outcome,
            Streamed = streamed,
            UpstreamStatus = upstreamStatus,
            Attempts = attempts < 1 ? 1 : attempts,
            Terminal = isTerminal,
        };

        Observe(record);
    }

    /// <summary>
    /// 把一个请求记录喂给指标和脱敏日志。
    ///
    /// **两条都是旁路** —— 它们抛异常绝不能影响用户已经拿到的响应。
    /// 所以这里把异常吞掉并记日志，和 <see cref="ChatObservers"/> 的处理一致。
    ///
    /// 单独抽出来是因为还有几处（选不到号、鉴权失败）没走完整的 Notify，
    /// 但也该出现在指标和日志里 —— 否则用户看到的「成功率」会把
    /// 那些失败悄悄吃掉。
    /// </summary>
    private void Observe(RequestRecord record)
    {
        try
        {
            _metrics?.Record(record);
            _requestLog?.Record(record);
        }
        catch (Exception ex)
        {
            // 指标/日志是旁路，挂了不能拖累业务。但也别静默 ——
            // 记一条（这条本身是元数据，不含正文）。
            if (_log.IsEnabled(LogLevel.Warning))
            {
                LogObservabilityFailed(ex, record.RequestId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "指标/日志记录失败（请求 {RequestId}），已忽略")]
    private partial void LogObservabilityFailed(Exception ex, string requestId);

    /// <summary>
    /// 账本标识 —— 只取 uid 前 8 位。
    /// 既能区分账号，又不会把完整凭据标识写进账本（和旧版 `accountTag` 一致）。
    /// </summary>
    private static string AccountTagOf(PoolMember member)
    {
        var uid = member.Entry.Uid;
        var head = uid.Length <= 8 ? uid : uid[..8];
        return $"{head}:{member.Entry.EnterpriseId ?? ""}";
    }

    // ── 请求体 ────────────────────────────────────────────────

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        // `application/json; charset=utf-8` 也要认
        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var mime = semicolon >= 0 ? contentType[..semicolon] : contentType;
        return mime.Trim().StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ReadBodyAsync(HttpContext context)
    {
        // 上限：旧版是 64 MB（要传图片 data URI）。没上限的话一个恶意请求
        // 就能把内存吃光。
        var limit = _options.MaxRequestBytes;
        var buffer = new MemoryStream();

        var chunk = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) throw new BodyTooLargeException();
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed class BodyTooLargeException : Exception;

    // ── 输出 ──────────────────────────────────────────────────

    private static async Task WriteJsonAsync(HttpContext context, int status, JsonNode body)
    {
        // 同样用宽松编码器 —— 错误消息里有中文
        var payload = body.ToJsonString(RelaxedJson);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(payload);
        await context.Response.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>统一的错误形状 `{"error":{"message","type","code"}}`。</summary>
    private static Task WriteErrorAsync(HttpContext context, int status, string kind, string message)
    {
        var safe = string.IsNullOrWhiteSpace(message) ? kind : message;
        return WriteJsonAsync(context, status, new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = safe,
                ["type"] = kind,
                ["code"] = kind,
            },
        });
    }

    // ── 日志 ──────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "模型目录刷新失败（{Reason}）：{Detail}")]
    private partial void LogCatalogRefreshFailed(string reason, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "上游失败 → 客户端 {Status}（{Kind}，上游 http {UpstreamStatus}，号 {Member}，模型 {Model}）")]
    private partial void LogUpstreamFailed(int status, string kind, int upstreamStatus, string member, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "第 {Attempt} 次尝试失败（{Kind}，上游 http {UpstreamStatus}，号 {Member}，模型 {Model}）→ 已冷却该「号+模型」，累计排除 {Excluded} 个号")]
    private partial void LogRetrying(int attempt, string kind, int upstreamStatus, string member, string model, int excluded);

    [LoggerMessage(Level = LogLevel.Debug, Message = "跳过「号 {Member} + 模型 {Model}」—— 这个组合还在冷却中，连请求都不发")]
    private partial void LogSkippingCooling(string member, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "上游流中断（号 {Member}，模型 {Model}）")]
    private partial void LogStreamFailed(Exception ex, string member, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "媒体工具执行失败（{Count} 个调用）—— 回复正文照常返回，只是没有产物")]
    private partial void LogMediaToolFailed(Exception ex, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "启动阶段填账号池失败：{Message} —— 首个请求会再补一次")]
    private partial void LogPoolInitFailed(Exception ex, string message);
}
