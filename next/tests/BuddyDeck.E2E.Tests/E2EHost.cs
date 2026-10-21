using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.Gateway;
using BuddyDeck.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace BuddyDeck.E2E.Tests;

/// <summary>
/// 端到端链路的入口：一个跑在**系统分配端口**上的真网关，上游是脚本化假上游。
///
/// ## 它替测试做掉的三件事
///
/// 1. **端口**：永远绑 <c>127.0.0.1:0</c>，从 <c>IServerAddressesFeature</c> 读回
///    真实端口。绝不写死 8787 / 3080 —— 那是用户的网关和 DSH Web。
/// 2. **就绪判定**：轮询 <c>/healthz</c> 并设总时限，**没有 Start-Sleep**。
/// 3. **上游**：所有出站请求都由 <see cref="FakeUpstreamHandler"/> 接住，
///    一个真连接都不建。
///
/// ## 为什么还要包一层而不是直接用 GatewayHost
///
/// 端到端用例里九成的时间花在「造一段上游行为 + 发一个请求 + 断言响应」上。
/// 那三段写成方法之后，用例本体就只剩断言了 —— 读起来才知道在验什么。
/// </summary>
public sealed class E2EHost : IAsyncDisposable
{
    private readonly GatewayHost _host;

    private E2EHost(GatewayHost host, FakeUpstreamHandler fake, TestVault vault)
    {
        _host = host;
        Fake = fake;
        Vault = vault;
    }

    /// <summary>假上游 —— 断言请求侧（换没换号、带没带对头）时读它。</summary>
    public FakeUpstreamHandler Fake { get; }

    /// <summary>这次宿主用的账号。</summary>
    public TestVault Vault { get; }

    /// <summary>系统分配的端口。</summary>
    public int Port => _host.Port;

    /// <summary>已经配好 BaseAddress 的客户端（Timeout 无限，流式长回答不会被砍）。</summary>
    public HttpClient Client => _host.Client;

    /// <summary>宿主内部的应用实例（要读 DI 里的池时用）。</summary>
    public Microsoft.AspNetCore.Builder.WebApplication Application => _host.Application;

    /// <summary>宿主捕获的日志（失败时用来解释原因）。</summary>
    public string DumpLog() => _host.DumpLog();

    /// <summary>
    /// 起一个宿主并等到 <c>/healthz</c> 通。
    ///
    /// @param fake   假上游处理逻辑（**必传**，不传就会真去打 WorkBuddy 上游）
    /// @param vault  账号；默认一个正常号
    /// @param apiKey 要求的 Bearer key；null = 不鉴权
    /// @param sourceDir 指纹目录；默认造一个临时目录（保证 buildId 是 12 位 hex）
    /// </summary>
    public static async Task<E2EHost> StartAsync(
        Func<CapturedRequest, HttpResponseMessage> fake,
        TestVault? vault = null,
        string? apiKey = null,
        string? sourceDir = null)
    {
        var handler = new FakeUpstreamHandler(fake);
        return await StartAsync(handler, vault, apiKey, sourceDir).ConfigureAwait(false);
    }

    /// <summary>同上，但直接给一个可复用的假上游实例（要读 <c>fake.Requests</c> 时用）。</summary>
    public static Task<E2EHost> StartAsync(
        FakeUpstreamHandler fake,
        TestVault? vault = null,
        string? apiKey = null,
        string? sourceDir = null)
    {
        ArgumentNullException.ThrowIfNull(fake);
        return StartCoreAsync(fake, vault, apiKey, sourceDir, maxRequestBytes: null);
    }

    /// <summary>
    /// 起一个请求体上限**很小**的宿主 —— 用来把 413 的边界压出来，
    /// 而不必真发 64 MB。
    /// </summary>
    public static Task<E2EHost> StartWithBodyLimitAsync(
        long maxRequestBytes,
        Func<CapturedRequest, HttpResponseMessage>? fake = null,
        TestVault? vault = null,
        string? apiKey = null)
        => StartCoreAsync(
            new FakeUpstreamHandler(fake ?? (_ => StreamScript.TextStream("hi"))),
            vault, apiKey, sourceDir: null, maxRequestBytes: maxRequestBytes);

    private static async Task<E2EHost> StartCoreAsync(
        FakeUpstreamHandler fake,
        TestVault? vault,
        string? apiKey,
        string? sourceDir,
        long? maxRequestBytes)
    {
        var accounts = vault ?? new TestVault().Add();

        var host = await GatewayHost.StartAsync(
            fake: fake,
            vault: accounts,
            apiKey: apiKey,
            configure: services =>
            {
                // 指纹目录：默认指向一个含 .cs 的临时目录。
                // 不这么做的话 buildId 会是 "unknown"（7 个字符），
                // 而 /healthz 的契约要求 12 位小写 hex。
                services.AddSingleton(new BuildInfo(sourceDir ?? FingerprintDirectory()));

                // ── ⚠ 端点族注册（t36 补）────────────────────────────
                //
                // **为什么这里必须补**：
                // `tests/BuddyDeck.TestSupport/GatewayHost.cs:148` 只写
                // `services.AddSingleton<GatewayApp>()` —— 它没显式传 `families`。
                // 而 `GatewayApp` 的该参数是**可有默认值的 DI 参数**
                // （`IEnumerable<IEndpointFamily>? families = null`），
                // 所以**容器里没有注册任何 IEndpointFamily 时，它解析成空数组**
                // ⇒ `GatewayApp` 的 `foreach (var family in _families)` 一次都不跑
                // ⇒ 所有端点族（Anthropic / 媒体 / 搜索）走 404 兜底。
                //
                // **后果**：真进程（`dotnet run`，走 `Program.cs`）完全正常，
                // 但**任何用 GatewayHost 起的测试都看不见端点族** ——
                // 症状是「手工验是真的、测试里全红」，人很容易误判成"测试环境问题"。
                //
                // ⚠ 这里是**第 4 次装配事故**（前三次：/admin/*、UsageEndpoints、AnthropicEndpoints），
                //   形态不同（前三次是生产侧漏挂，这次是**测试夹具侧漏挂**），
                //   但根因同一个：**「类型写好了」不等于「它在真实请求路径上」**。
                //
                // ⚠⚠ 顺序必须与 `Program.cs` 一致 —— 那是**唯一的真源**。
                //   本处**故意逐个列出而不是用循环反射**，就是为了让顺序在此**可见**；
                //   若 `Program.cs` 调整了顺序，这里必须同步（两处漂移的症状是
                //   「某个端点被另一个族静默吃掉」，很难查）。
                //
                // ⚠ 端点族有**自己的依赖**，测试夹具里没有（夹具只够跑 `/v1/*` 主链）。
                //   实测缺 `RequestLog` 时 DI 直接抛：
                //     Unable to resolve service for type 'RequestLog'
                //     while attempting to activate 'AnthropicEndpoints'
                //   所以下面把族需要的依赖也补上 —— 它们是**网关自己的基础设施**，
                //   与生产用的是同一批类型（不是测试替身），只是此处手工装配。
                services.AddSingleton<RequestLog>();
                services.AddSingleton<IMetricsSource, RequestMetrics>();

                // ── 只注册 Anthropic 族（t36 的覆盖面）─────────────────
                //
                // ⚠ **刻意不注册 Media/Search 族**，理由具体：
                //
                // 它们依赖 `MioraClient`，而它的构造签名是
                //   `(HttpClient, Func<Credential>, UpstreamVariant, string mediaDirectory, TimeProvider, ILogger, string?)`
                // —— 需要一个「凭据取值函数」和媒体目录，在测试宿主里搭出来
                //   属于 **t20 那条线的测试范围**，不是本任务的覆盖目标。
                //
                // 硬把它们注册进来只会让**本任务的所有用例**因为一个无关依赖而全红，
                // 那会把「Anthropic 没覆盖到」和「媒体没搭好」两件事混成一个失败。
                //
                // ⇒ 所以此处**只注册本组要验的族**；媒体/搜索族的第 ④ 层覆盖
                //   应由它们自己的任务补（各自的夹具依赖不同）。
                //
                // ⚠ 这也意味着：`Program.cs` 里 Media/Search 的**注册顺序**在本夹具里
                //   **没有被验到**。那两条留待 t20 线的端到端任务 —— 我把这个缺口
                //   写在这里，免得后人以为「E2E 全覆盖了」。
                EndpointFamilies.Register<AnthropicEndpoints>(services);

                if (maxRequestBytes is { } limit)
                {
                    // GatewayHost 自己注册了一份 GatewayOptions；DI 里后注册的赢，
                    // 所以这里盖一份带小上限的（其余字段保持和默认一致）。
                    services.AddSingleton(new GatewayOptions
                    {
                        Host = "127.0.0.1",
                        Port = 0,
                        ApiKey = apiKey ?? "",
                        Variant = UpstreamVariant.China.Id,
                        DefaultModel = "hy3",
                        MaxRequestBytes = limit,
                        LogDirectory = Path.Combine(Path.GetTempPath(), "buddydeck-e2e-logs"),
                    });
                }
            }).ConfigureAwait(false);

        await host.WaitForHealthAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        return new E2EHost(host, fake, accounts);
    }

    // ── 造请求 ────────────────────────────────────────────────

    /// <summary>发一个聊天请求。</summary>
    /// <param name="body">请求体 JSON 文本。</param>
    /// <param name="apiKey">Bearer key；null = 不带 Authorization 头。</param>
    /// <param name="contentType">Content-Type；null = 不设（验 415 用）。</param>
    /// <param name="host">Host 头；null = 用默认（回环）。</param>
    /// <param name="completion">读响应体的时机；流式用例传 ResponseHeadersRead。</param>
    /// <param name="ct">取消令牌（验客户端取消用）。</param>
    public Task<HttpResponseMessage> PostChatAsync(
        string body,
        string? apiKey = null,
        string? contentType = "application/json",
        string? host = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, "/v1/chat/completions", body, apiKey, contentType, host, completion, ct);

    /// <summary>发一个 GET。</summary>
    public Task<HttpResponseMessage> GetAsync(string path, string? apiKey = null, string? host = null)
        => SendAsync(HttpMethod.Get, path, null, apiKey, null, host);

    /// <summary>带任意方法 / 路径 / 正文发一个请求。</summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? body = null,
        string? apiKey = null,
        string? contentType = "application/json",
        string? host = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);

            if (contentType is null)
            {
                request.Content.Headers.ContentType = null;   // 完全不设 Content-Type（验 415）
            }
            else
            {
                // ⚠ 不能直接 new MediaTypeHeaderValue("application/json; charset=utf-8") ——
                // 单参构造器把整个字符串当成 media type，遇到分号会抛 FormatException。
                // 用 TryAddWithoutValidation 才能原样把一个**带参数**的 Content-Type 发出去，
                // 而那正是要验的形态（很多客户端就是带 charset 发的）。
                request.Content.Headers.Remove("Content-Type");
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        }

        if (host is not null)
        {
            request.Headers.TryAddWithoutValidation("Host", host);
        }

        return await Client.SendAsync(request, completion, ct).ConfigureAwait(false);
    }

    // ── 用例里最常用的两件事 ──────────────────────────────────

    /// <summary>等宿主就绪（重启场景用；正常流程里 StartAsync 已经等过了）。</summary>
    public Task<JsonNode> WaitForHealthAsync(TimeSpan? timeout = null)
        => _host.WaitForHealthAsync(timeout ?? TimeSpan.FromSeconds(15));

    /// <summary>读响应体并解析成 JSON。</summary>
    public static async Task<JsonNode> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonNode.Parse(text)
            ?? throw new InvalidOperationException($"响应体不是 JSON：{text}");
    }

    /// <summary>读响应体文本。</summary>
    public static Task<string> TextAsync(HttpResponseMessage response)
        => response.Content.ReadAsStringAsync();

    /// <summary>
    /// 一个含 <c>.cs</c> 文件的临时目录 —— 让 buildId 算得出来。
    /// 内容固定，所以同一进程内反复算都是同一个指纹。
    /// </summary>
    public static string FingerprintDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("buddydeck-e2e-fingerprint-").FullName;
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "// e2e 假源码\n");
        return dir;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// 一条被假上游收到并**归一化**过的聊天请求 —— 断言请求侧时读它。
    /// </summary>
    public static JsonNode? BodyOf(CapturedRequest request) => request.Json();

    /// <summary>假上游收到的聊天请求（只看 <c>/v2/chat/completions</c>）。</summary>
    public IReadOnlyList<CapturedRequest> ChatRequests
        => Fake.Requests.Where(r => r.Path == "/v2/chat/completions").ToArray();

    /// <summary>系统分配端口不能是这些 —— 8787 是用户的网关，3080 是 DSH Web。</summary>
    public bool IsReservedPort => Port is 8787 or 3080;
}

/// <summary>常用 HTTP 状态码的一个可读封装（断言里少写魔法数字）。</summary>
internal static class HttpStatus
{
    public static int Code(HttpResponseMessage response) => (int)response.StatusCode;
}
