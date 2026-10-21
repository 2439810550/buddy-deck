using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuddyDeck.TestSupport;

/// <summary>
/// 起一个真的网关。
///
/// ## 为什么是真端口而不是 TestServer
///
/// 端到端要验的东西里有一半是**传输层**的：真流式（边收边发、不缓冲）、
/// `Content-Length`、`Host` 头检查、连接中断。这些用 in-memory 的
/// <c>TestServer</c> 测不出来 —— 它在管道里就把响应攒完了。
///
/// ## 端口：绑 0，由系统分配
///
/// ⚠ 绝不占 8787（用户正在跑的网关）和 3080（DSH Web）。
/// <c>UseUrls("http://127.0.0.1:0")</c> 让内核随便挑一个空闲端口，
/// 起来之后从 <c>IServerAddressesFeature</c> 读回真实端口。
///
/// 这里**不用** Kestrel 的 `ListenLocalhost(0)`：那个写法的端口 0
/// 语义在文档里是含糊的（真绑 0 还是抛错，取决于配置源）。
/// 显式走 `UseUrls` 最稳。
///
/// ## 就绪判定：轮询 /healthz，**绝不 Start-Sleep**
///
/// 「睡 500 毫秒应该起来了」在不同机器上不可靠：慢的时候不够，
/// 快的时候白等。这里改成
/// <see cref="WaitForHealthAsync"/>：拿带总时限的
/// <see cref="CancellationTokenSource"/> + 轮询探测，直到
/// `/healthz` 真的回 200 或者超时。超时**抛异常并带上捕获的日志**，
/// 而不是让后面的断言给出莫名其妙的失败。
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<string> _log = [];
    private readonly object _logGate = new();

    private GatewayHost(WebApplication app, string baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
        Client = new HttpClient(new SocketsHttpHandler())
        {
            BaseAddress = new Uri(baseAddress),
            // 探活/普通请求自己控超时，不要让 HttpClient 在流式长响应上超时
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>宿主的根地址，形如 <c>http://127.0.0.1:53127</c>。</summary>
    public string BaseAddress { get; }

    /// <summary>绑定的端口（系统分配的）。</summary>
    public int Port => new Uri(BaseAddress).Port;

    /// <summary>已经配好 BaseAddress 的 HttpClient。</summary>
    public HttpClient Client { get; }

    /// <summary>宿主内部的应用实例（要读 DI 里的东西时用）。</summary>
    public WebApplication Application => _app;

    /// <summary>宿主捕获到的日志行（探活失败时用来解释原因）。</summary>
    public IReadOnlyList<string> CapturedLog
    {
        get { lock (_logGate) return _log.ToArray(); }
    }

    /// <summary>把捕获的日志拼成一段可读文本。</summary>
    public string DumpLog()
    {
        lock (_logGate) return _log.Count == 0 ? "（宿主没有输出任何日志）" : string.Join(Environment.NewLine, _log);
    }

    /// <summary>
    /// 起一个网关宿主。
    ///
    /// @param fake     上游 —— **必须传**，否则测试真的会去打 WorkBuddy 上游
    /// @param vault    账号；null 表示一个号都没有（测「没登录」路径）
    /// @param apiKey   要求的 Bearer key；null = 不鉴权
    /// @param configure 额外定制 DI（挂 IChatObserver 之类）
    /// </summary>
    public static async Task<GatewayHost> StartAsync(
        FakeUpstreamHandler? fake = null,
        TestVault? vault = null,
        string? apiKey = null,
        Action<IServiceCollection>? configure = null,
        CancellationToken ct = default)
    {
        fake ??= new FakeUpstreamHandler(_ => UpstreamResponses.Text("""{"code":0}"""));
        var entries = vault?.Entries ?? [];

        var builder = WebApplication.CreateSlimBuilder();

        // 端口 0 —— 系统分配。绝不写死 8787 / 3080。
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // 日志进内存缓冲区：探活失败时能解释原因，而且不污染测试输出。
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddProvider(new BufferLoggerProvider(host => host));

        var services = builder.Services;

        services.AddSingleton(new GatewayOptions
        {
            Host = "127.0.0.1",
            Port = 0,
            ApiKey = apiKey ?? "",
            Variant = UpstreamVariant.China.Id,
            DefaultModel = "hy3",
            LogDirectory = Path.Combine(Path.GetTempPath(), "buddydeck-testsupport-logs"),
        });

        services.AddSingleton(UpstreamVariant.China);
        services.AddSingleton(new ModelCatalog(UpstreamVariant.China.Id, "hy3"));

        // 源码目录用测试进程的目录 —— 指纹算出来大概率是 "unknown"，
        // 但 /healthz 照常回（BuildInfo 对读不到的目录是容错的）。
        services.AddSingleton(new BuildInfo(AppContext.BaseDirectory));
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<AccountPool>();
        services.AddSingleton<ICredentialSource>(new ListCredentialSource(entries));

        // 上游 HttpClient 指向假上游。**不走 AddHttpClient** ——
        // 那要拖一个 HttpClientFactory，而这里只需要一个固定 handler。
        services.AddSingleton(new HttpClient(fake, disposeHandler: false));
        services.AddSingleton<UpstreamClient>(sp => new UpstreamClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<UpstreamVariant>(),
            NullLogger<UpstreamClient>.Instance));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatObserver, NullChatObserver>());
        services.AddSingleton<ChatObservers>();

        // ── 目录刷新器（`CatalogRefresh`，t53 抽出的新类型）────────
        //
        // ⚠ **这是第 7 次同类装配缺陷，必须记住这条联动规则：**
        //   `Program.cs` 里每新增一个被 `GatewayApp` 依赖的具体类型，
        //   **这里就要多一个注册**。否则 DI 解析不到 ⇒ 网关侧静默降级
        //   （本例是 `_catalogRefresh is null` ⇒ 直接不刷新目录）。
        //
        // 为什么症状总是「一大片无关测试红」或「功能静默不生效」：
        //   单测不走 DI、也不走真实请求路径，**测不到容器里缺什么**。
        //
        // 本例的具体表现（E2E `models_有机会时用上游刷回来的目录`）：
        //   假上游注册了 `GET /v3/config`，但宿主没注册刷新器
        //   ⇒ 刷新被跳过 ⇒ `/v1/models` 永远回 16 个内置模型
        //   ⇒ 断言 "self-made-model" 找不到。
        services.AddSingleton(sp => new CatalogRefresh(
            sp.GetRequiredService<UpstreamClient>(),
            sp.GetRequiredService<ModelCatalog>(),
            sp.GetRequiredService<UpstreamVariant>(),
            NullLogger<CatalogRefresh>.Instance));

        // ── 脱敏日志（`RequestLog`）──────────────────────────────
        //
        // ⚠ 必须注册，**即使这个宿主默认不采集日志**。
        //
        // 原因：`AnthropicEndpoints` / `ResponsesEndpoints` 的构造函数参数
        // 写成 `RequestLog? requestLog` —— 对 C# 是「可空」，但 **DI 不认可空**：
        // 它看到参数类型就要求容器能解析。没注册的话激活端点的瞬间抛
        //
        //   Unable to resolve service for type 'BuddyDeck.Gateway.RequestLog'
        //   while attempting to activate 'BuddyDeck.Gateway.AnthropicEndpoints'
        //
        // 「参数可空」与「DI 可解析」是**两件事**，这里踩到了。
        services.AddSingleton(sp => new RequestLog(sp.GetRequiredService<TimeProvider>()));

        // ── 请求指标（`GET /metrics` 的数据源）────────────────────
        //
        // ⚠ 不注册的话，宿主起得来、`/metrics` 也回 200 ——
        //   但回的是**空快照**（`GatewayApp.MetricsAsync` 的
        //   `_metrics?.Snapshot() ?? new MetricsSnapshot()` 回退）。
        //
        //   那个回退本身是**对的**（没接指标时给结构完整的空对象，
        //   比给 404 好），但它会让「指标没接上」表现成
        //   「指标全是 0」—— 实测踩到：用 GatewayHost 发了几次真聊天请求后
        //   读 `/metrics`，`TotalRequests` 仍是 0，排查方向直接跑偏到
        //   「端点的 JSON 是不是没写对」。
        //
        //   所以测试宿主**默认就挂上真的指标实现** —— 起一个宿主就该
        //   是个能观测的完整宿主，除非调用方显式要一个裸的。
        services.AddSingleton(sp => new RequestMetrics(
            sp.GetRequiredService<TimeProvider>(),
            new MetricsOptions { RecentCapacity = 50 }));

        services.AddSingleton<IMetricsSource>(sp => sp.GetRequiredService<RequestMetrics>());

        // ── 媒体 / 联网客户端（MediaEndpoints、SearchEndpoints 的依赖）──
        //
        // 同样必须注册：不注册的话端点族激活时抛
        //   Unable to resolve service for type 'BuddyDeck.Core.MioraClient'
        //   while attempting to activate 'BuddyDeck.Gateway.MediaEndpoints'
        //
        // ⚠ 两个都挂**假上游的 handler**（`fake`）—— 否则测试会打真实网络。
        //   实测过一次极具误导性的症状：拿到真 401 而 `fake.Count` 是 0，
        //   看起来像「端点压根没被调用」。
        //
        // ⚠ **不带 ResilienceHandler**：生图绝不能自动重试（会重复扣费、出两张图）。
        services.AddSingleton(sp => new MioraClient(
            new HttpClient(fake, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
            () => FirstCredential(sp),
            sp.GetRequiredService<UpstreamVariant>(),
            Path.Combine(Path.GetTempPath(), "buddydeck-testsupport-media"),
            sp.GetRequiredService<TimeProvider>(),
            NullLogger<MioraClient>.Instance));

        services.AddSingleton(sp => new SearchClient(
            new HttpClient(fake, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
            () => FirstCredential(sp),
            sp.GetRequiredService<UpstreamVariant>(),
            NullLogger<SearchClient>.Instance));

        // ── 端点族：**必须挂上** ─────────────────────────────────
        //
        // ## ⚠ 不挂会怎样（这是一个真发生过的缺陷）
        //
        // `EndpointFamilies.AddAll()` 少一行，`GatewayApp._families` 就是空数组，
        // 于是 `HandleAsync` 里那句 `foreach (var family in _families)`
        // **一次都不跑**，直接落到 404 兜底 ——
        //
        //   ⇒ **所有端点族在测试宿主里完全不可见**（`/v1/messages`、`/admin/*`、
        //     `/v1/images/*`、`/v1/search`、`/v1/responses` 全是 404）。
        //
        // **而真进程完全正常**（`Program.cs` 里传了 families），所以症状是
        // 「手工 `dotnet run` 验一遍是真的 + 测试里全红」—— 那种组合最容易让人
        // 得出「测试环境问题，不是 bug」的结论，**而那正是要防的**。
        //
        // 这个项目里它已经是**第三次**同类缺陷了：
        //   `/admin/*`        写了路由但没挂
        //   `UsageEndpoints`  类写了但没注册
        //   端点族            在测试宿主里没挂   ← 本次
        // 三次都是「测试通过但功能不存在」的同一个形态。
        //
        // ## 顺序：**唯一真源在 `EndpointFamilies.AddAll()`**
        //
        // ⚠ **不要在这里另抄一份注册清单。** 生产装配（`Program.cs`）和测试装配
        //   （这里）都调**同一个方法**，顺序才不会漂移 —— 两边各写一份的话，
        //   加一族时改了一边忘另一边，「真进程正常、测试里 404」会再次上演。
        //
        // ## 为什么必须先注册 AdminEndpoints 具体类型
        //
        // `AddAll()` 内部对 `AdminEndpoints` 用的是**转发**
        // （`AddSingleton<IEndpointFamily>(sp => sp.GetRequiredService<AdminEndpoints>())`），
        // 因为它带可变状态（`_settings`）—— 造第二个实例会导致
        // 「POST /admin/settings 改了一个、GET 读到另一个」。
        //
        // 所以这里必须先把**具体类型**注册好，否则那个工厂一解析就抛
        // `GetRequiredService<AdminEndpoints>` 失败 —— 是**抛异常**，不是静默。
        services.AddSingleton(sp => new AdminEndpoints(
            sp.GetRequiredService<GatewayOptions>(),
            sp.GetRequiredService<UpstreamVariant>(),
            sp.GetRequiredService<ICredentialSource>(),
            // RequestLog 已经注册（见上），这里让它正常解析 ——
            // 管理面的 /admin/logs 因此也能在测试里用。
            sp.GetRequiredService<RequestLog>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AdminEndpoints>>()));

        // ⚠⚠ **`AddAll()` 里每多一个「转发」的族，这里就得多一个具体类型注册。**
        //
        // 写法上 `AddAll` 只用 `GetRequiredService<T>()` 转发那些**有可变状态**
        // 或**不在 Gateway 程序集里实现 `IEndpointFamily`** 的族（AdminEndpoints、
        // UsageEndpoints）。这些族的**具体类型**必须在容器里，否则转发工厂
        // 一解析就抛 —— 而且是**抛异常**，会让**所有**用到端点族的用例一起红。
        //
        // 实测代价：t29 给 `AddAll` 加了 `UsageFamily` 转发之后，本宿主没跟上，
        // **92 条用例**在宿主启动时就炸（`No service for type 'UsageEndpoints'`）。
        // 症状是「一大片无关测试同时红」，很容易被误判成「某个大改动把网关改坏了」。
        //
        // ⇒ **加族时两边都要看**：`AddAll` 有没有新增转发？这里有没有对应具体类型？
        services.AddSingleton(sp => new UsageLedger(
            Path.Combine(Path.GetTempPath(), "buddydeck-testsupport-usage"),
            sp.GetRequiredService<ILogger<UsageLedger>>()));

        services.AddSingleton(sp => new UsageEndpoints(
            sp.GetRequiredService<UsageLedger>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UsageEndpoints>>()));

        // 数据库 + 后台任务（Database 是数据中枢，TaskHost 是任务执行器）。
        // 测试宿主用临时数据库文件，不碰真实数据。
        var db = new Database(
            Path.Combine(Path.GetTempPath(), $"buddydeck-testsupport-{Guid.NewGuid():N}.db"),
            null);
        db.Initialize();
        services.AddSingleton(db);
        services.AddSingleton(sp => new TaskHost(
            db,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TaskHost>>()));
        services.AddSingleton(sp => new TaskEndpoints(
            db,
            sp.GetRequiredService<TaskHost>(),
            sp.GetRequiredService<ICredentialSource>(),
            sp.GetRequiredService<UpstreamClient>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TaskEndpoints>>(),
            checkin: null));

        EndpointFamilies.AddAll(services);

        services.AddSingleton<GatewayApp>();

        configure?.Invoke(services);

        var app = builder.Build();

        // ── 把捕获日志接到宿主上 ──────────────────────────────
        var buffer = (BufferLoggerProvider)app.Services
            .GetRequiredService<IEnumerable<ILoggerProvider>>()
            .First(p => p is BufferLoggerProvider);

        app.Services.GetRequiredService<GatewayApp>().Map(app);

        await app.StartAsync(ct).ConfigureAwait(false);

        var address = ResolveAddress(app);
        var host = new GatewayHost(app, address);
        buffer.Attach(host);

        return host;
    }

    /// <summary>读回 Kestrel 真正绑上的地址。</summary>
    private static string ResolveAddress(WebApplication app)
    {
        var feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("宿主没报出监听地址 —— 起不来");

        var address = feature.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("宿主没有绑定任何地址 —— 起不来");

        // Kestrel 报回来的可能是 http://127.0.0.1:53127/（带尾斜杠），
        // HttpClient 的 BaseAddress 要的也是这个形状，直接用。
        return address;
    }

    /// <summary>
    /// 轮询 `/healthz` 直到就绪。
    ///
    /// **没有 Start-Sleep，也没有 Task.Delay(x) 猜时间** ——
    /// 用带总时限的取消令牌 + 「立刻探测」，探到就返回。
    ///
    /// @param timeout 总时限，默认 15 秒。超时抛
    ///                <see cref="TimeoutException"/>，消息里带宿主日志。
    /// @returns /healthz 的响应体（已经解析好的 JSON）。
    /// </summary>
    public async Task<JsonNode> WaitForHealthAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(15);
        var stopwatch = Stopwatch.StartNew();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);

        Exception? last = null;

        while (!deadline.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();   // 调用方取消 ≠ 超时，原样抛

            try
            {
                using var probe = new HttpRequestMessage(HttpMethod.Get, "/healthz");
                probe.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var response = await Client
                    .SendAsync(probe, HttpCompletionOption.ResponseContentRead, deadline.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var text = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                    return JsonNode.Parse(text)
                        ?? throw new InvalidOperationException("/healthz 回了个空的 JSON");
                }

                last = new HttpRequestException($"探活拿到 {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // 还没起来（连接被拒）—— 继续探。
                // 这里是**轮询**，不是等待：每次探测都会真的打一次端口。
                last = ex;
            }
        }

        throw new TimeoutException(
            $"网关在 {limit.TotalSeconds:F1} 秒内没有就绪（已探 {stopwatch.ElapsedMilliseconds} ms）" +
            $"—— 最后一次错误：{last?.Message ?? "无"}。宿主日志：{Environment.NewLine}{DumpLog()}");
    }

    /// <summary>起一个宿主并等到就绪（最常用的一步到位）。</summary>
    public static async Task<GatewayHost> StartReadyAsync(
        FakeUpstreamHandler? fake = null,
        TestVault? vault = null,
        string? apiKey = null,
        Action<IServiceCollection>? configure = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var host = await StartAsync(fake, vault, apiKey, configure, ct).ConfigureAwait(false);
        await host.WaitForHealthAsync(timeout, ct).ConfigureAwait(false);
        return host;
    }

    /// <summary>给请求带上 Bearer key 的便捷方法。</summary>
    public static HttpRequestMessage BearerRequest(HttpMethod method, string path, string? apiKey = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        }
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(stopDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // 停不下来就算了 —— 测试收尾不该 fail
        }

        await _app.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>把日志收进宿主的缓冲区。</summary>
    internal void Capture(string line)
    {
        lock (_logGate)
        {
            // 有上限 —— 一个跑飞的测试不该把内存吃光
            if (_log.Count < 500) _log.Add(line);
        }
    }

    // ── 内部：日志缓冲 ────────────────────────────────────────

    private sealed class BufferLoggerProvider : ILoggerProvider
    {
        private readonly Func<BufferLoggerProvider, BufferLoggerProvider> _self;
        private GatewayHost? _host;

        public BufferLoggerProvider(Func<BufferLoggerProvider, BufferLoggerProvider> self) => _self = self;

        public void Attach(GatewayHost host) => _host = host;

        public ILogger CreateLogger(string categoryName) => new BufferLogger(categoryName, this);

        public void Dispose()
        {
            // 没有非托管资源
        }

        private void Write(string category, LogLevel level, string message)
            => _host?.Capture($"[{level}] {category}: {message}");

        /// <summary>给 <see cref="BufferLogger"/> 用（嵌套类之间默认能访问彼此的保护成员，
        /// 但这里显式暴露出来，免得读代码时要去查可见性规则）。</summary>
        internal void Emit(string category, LogLevel level, string message) => Write(category, level, message);
    }

    private sealed class BufferLogger : ILogger
    {
        private readonly string _category;
        private readonly BufferLoggerProvider _provider;

        public BufferLogger(string category, BufferLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message = $"{message} | {exception.GetType().Name}: {exception.Message}";
            _provider.Emit(_category, logLevel, message);
        }
    }

    /// <summary>从一个固定的账号列表读凭证（网关的 <c>VaultCredentialSource</c> 的文件版替身）。</summary>
    private sealed class ListCredentialSource : ICredentialSource
    {
        private readonly IReadOnlyList<VaultEntry> _entries;

        public ListCredentialSource(IReadOnlyList<VaultEntry> entries) => _entries = entries;

        public IReadOnlyList<VaultEntry> Load() => _entries;
    }

    /// <summary>
    /// 取「当前账号」的凭证 —— 媒体与联网客户端用（和生产装配里的同名方法一致）。
    ///
    /// 惰性取：每次要用时现取，切号/续期能自动跟上。
    /// 保险库为空时返回空 token 的凭证，让下游报明确的「没有可用凭据」
    /// 而不是 NullReferenceException。
    /// </summary>
    private static Credential FirstCredential(IServiceProvider sp)
    {
        var entries = sp.GetRequiredService<ICredentialSource>().Load();

        return entries.Count > 0
            ? entries[0].ToCredential()
            : new Credential { AccessToken = "", Label = "（保险库为空）" };
    }
}
