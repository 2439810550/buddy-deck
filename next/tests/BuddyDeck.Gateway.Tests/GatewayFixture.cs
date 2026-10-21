using System.Net;
using System.Text;
using BuddyDeck.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 一个跑在**随机端口**上的真网关，上游是假的。
///
/// ## 为什么起真的 Kestrel
///
/// 端点测试要验的东西大半是 HTTP 层面的：Host 头检查、Content-Type、
/// 状态码、SSE 是不是真的边收边发。用 TestServer/内存管道测不出
/// 「有没有整体缓冲」—— 那正是这次必须验的一条。
///
/// 所以：真端口 + 真 HttpClient。
///
/// ## 端口
///
/// 用 `--port 0` 让操作系统分配。绝不写死 8787/3080 ——
/// 那是用户正在跑的东西（见 AGENTS.md 铁律）。
/// </summary>
public sealed class GatewayFixture : IAsyncDisposable
{
    private readonly WebApplication _app;

    public int Port { get; }
    public FakeUpstream Fake { get; }
    public AccountPool Pool { get; }
    public ModelCatalog Catalog { get; }
    public HttpClient Client { get; }
    public string ApiKey { get; }

    /// <summary>测试用账号 —— 不打上游，所以 token 是假的。</summary>
    public static readonly VaultEntry TestEntry = new()
    {
        Id = "uuid-1:ent-1",
        Uid = "u1",
        EnterpriseId = "ent-1",
        Domain = "workbuddy.cn",
        AccessToken = "test-token",
        RefreshToken = "test-refresh",
        ExpiresAtMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
        Label = "测试号",
    };

    private GatewayFixture(
        WebApplication app,
        int port,
        FakeUpstream fake,
        AccountPool pool,
        ModelCatalog catalog,
        string apiKey)
    {
        _app = app;
        Port = port;
        Fake = fake;
        Pool = pool;
        Catalog = catalog;
        ApiKey = apiKey;
        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    /// <summary>
    /// 起一个网关。
    ///
    /// @param handler    假上游的处理逻辑
    /// @param apiKey     要求的 Bearer key（空 = 不鉴权）
    /// @param accounts   保险库里的账号（空 = 没有账号）
    /// @param sourceDir  指纹目录（默认指向临时空目录，避免测试依赖真实源码树）
    /// </summary>
    public static async Task<GatewayFixture> StartAsync(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        string apiKey = "",
        IReadOnlyList<VaultEntry>? accounts = null,
        string? sourceDir = null)
    {
        var fake = new FakeUpstream(handler);

        var options = new GatewayOptions
        {
            Port = 0,
            Host = "127.0.0.1",
            ApiKey = apiKey,
            DefaultModel = "hy3",
            // 日志写临时目录，别污染仓库
            LogDirectory = Path.Combine(Path.GetTempPath(), "buddydeck-gateway-tests"),
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton(UpstreamVariant.China);
        var catalog = new ModelCatalog(UpstreamVariant.China.Id, options.DefaultModel);
        services.AddSingleton(catalog);
        services.AddSingleton(new BuildInfo(sourceDir ?? MakeFingerprintDirectory()));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AccountPool>();

        var entries = accounts ?? [TestEntry];
        services.AddSingleton<ICredentialSource>(new StaticCredentialSource(entries));

        services.AddHttpClient<UpstreamClient>(client => client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => fake);

        services.AddSingleton<IChatObserver, NullChatObserver>();
        services.AddSingleton<ChatObservers>();
        services.AddSingleton<GatewayApp>();

        var app = builder.Build();
        app.Services.GetRequiredService<GatewayApp>().Map(app);

        await app.StartAsync().ConfigureAwait(false);

        var port = new Uri(app.Urls.First()).Port;
        var pool = app.Services.GetRequiredService<AccountPool>();

        return new GatewayFixture(app, port, fake, pool, catalog, apiKey);
    }

    /// <summary>带一个记录型观察者的网关（验挂载点真的会被调用）。</summary>
    public static Task<GatewayFixture> StartWithObserverAsync(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        RecordingObserver observer)
        => StartAsync(handler, "", null, null, observer);

    /// <summary>
    /// 造一个含 .cs 文件的临时源码目录 —— 指纹算得出来，且不依赖真实仓库。
    ///
    /// 为什么要显式建：指纹目录不存在时算出来是 "unknown"（7 个字符），
    /// 而 /healthz 的契约要求 12 位 hex。
    /// </summary>
    public static string MakeFingerprintDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("buddydeck-fingerprint-").FullName;
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "// 假源码\n");
        return dir;
    }

    private static async Task<GatewayFixture> StartAsync(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        string apiKey,
        IReadOnlyList<VaultEntry>? accounts,
        string? sourceDir,
        RecordingObserver observer)
    {
        var fake = new FakeUpstream(handler);
        var options = new GatewayOptions
        {
            Port = 0,
            Host = "127.0.0.1",
            ApiKey = apiKey,
            DefaultModel = "hy3",
            LogDirectory = Path.Combine(Path.GetTempPath(), "buddydeck-gateway-tests"),
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton(UpstreamVariant.China);
        var catalog = new ModelCatalog(UpstreamVariant.China.Id, options.DefaultModel);
        services.AddSingleton(catalog);
        services.AddSingleton(new BuildInfo(sourceDir ?? MakeFingerprintDirectory()));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AccountPool>();
        services.AddSingleton<ICredentialSource>(new StaticCredentialSource(accounts ?? [TestEntry]));

        services.AddHttpClient<UpstreamClient>(client => client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => fake);

        services.AddSingleton<IChatObserver>(observer);
        services.AddSingleton<ChatObservers>();
        services.AddSingleton<GatewayApp>();

        var app = builder.Build();
        app.Services.GetRequiredService<GatewayApp>().Map(app);
        await app.StartAsync().ConfigureAwait(false);

        var port = new Uri(app.Urls.First()).Port;
        return new GatewayFixture(app, port, fake,
            app.Services.GetRequiredService<AccountPool>(), catalog, apiKey);
    }

    /// <summary>发一个聊天请求。</summary>
    public Task<HttpResponseMessage> PostChatAsync(string json, string? apiKey = null, string? contentType = "application/json")
        => SendChatAsync(json, apiKey ?? ApiKey, contentType);

    private Task<HttpResponseMessage> SendChatAsync(string json, string apiKey, string? contentType)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8),
        };
        if (contentType is not null)
        {
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }
        if (apiKey.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

        return Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    /// <summary>发一个聊天请求并带上自定义 Host 头（验 host 检查）。</summary>
    public Task<HttpResponseMessage> PostChatWithHostAsync(string json, string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Host", host);
        return Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>固定账号清单的来源（不走保险库文件）。</summary>
    private sealed class StaticCredentialSource(IReadOnlyList<VaultEntry> entries) : ICredentialSource
    {
        public IReadOnlyList<VaultEntry> Load() => entries;
    }
}

/// <summary>一个会记账的观察者 —— 用来验「挂载点真的被调用了」。</summary>
public sealed class RecordingObserver : IChatObserver
{
    private readonly List<ChatObservation> _events = [];
    private readonly object _gate = new();

    public IReadOnlyList<ChatObservation> Events
    {
        get
        {
            lock (_gate) return _events.ToArray();
        }
    }

    public bool ThrowOnNotify { get; set; }

    public void OnChatCompleted(ChatObservation observation)
    {
        if (ThrowOnNotify) throw new InvalidOperationException("观察者故意炸了");

        lock (_gate) _events.Add(observation);
    }
}
