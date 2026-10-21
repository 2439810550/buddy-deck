using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.Gateway;
using BuddyDeck.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 一个**接了 P1/P2 三个模块**的真网关。
///
/// ## 为什么不复用 <see cref="GatewayHost"/> 直接写用例
///
/// <see cref="GatewayHost"/> 起的是「裸网关」—— 它注册的是
/// <see cref="NullChatObserver"/>，没有粘性/指标/日志。
/// 而这三个模块的测试**必须看到实例**（断言指标数值、遍历日志条目），
/// 所以这里在它上面包一层：用 <c>configure</c> 把三个模块塞进 DI，
/// 再把实例暴露出来。
///
/// 宿主本身仍然是 <see cref="GatewayHost"/> —— 真 Kestrel、端口 0、
/// 轮询 `/healthz` 就绪（零睡眠）。**绝不碰 8787 / 3080。**
/// </summary>
public sealed class ObservabilityFixture : IAsyncDisposable
{
    private readonly GatewayHost _host;

    private ObservabilityFixture(
        GatewayHost host,
        RequestMetrics metrics,
        RequestLog log,
        ConversationAffinity affinity,
        IReadOnlyList<VaultEntry> vaultEntries)
    {
        _host = host;
        Metrics = metrics;
        Log = log;
        Affinity = affinity;
        VaultEntries = vaultEntries;
    }

    public GatewayHost Host => _host;

    public HttpClient Client => _host.Client;

    public RequestMetrics Metrics { get; }

    public RequestLog Log { get; }

    public ConversationAffinity Affinity { get; }

    /// <summary>
    /// 这个夹具灌进网关的账号。
    ///
    /// ⚠ 为什么需要把它暴露出来：<c>AccountPool</c> 是**惰性填充**的
    /// （<c>Replace(entries)</c> 在每次聊天请求里才跑，这样用户导入/删号
    /// 之后不用重启网关）。所以「起完宿主、还没发过请求」时池子是空的 ——
    /// 测试若想**先改池状态再发请求**（比如预热冷却），必须自己先灌一次。
    /// </summary>
    public IReadOnlyList<VaultEntry> VaultEntries { get; }

    /// <summary>
    /// 起一个挂了 observability 的网关，并等到 /healthz 通。
    ///
    /// @param fake           假上游（**必给**，否则测试真的会去打 WorkBuddy）
    /// @param vault          账号
    /// @param apiKey         要求的 Bearer key（null = 不鉴权）
    /// @param recentCapacity 最近请求保留条数
    /// @param throwOnRecord  指标故意抛异常 —— 验「旁路挂了不影响用户响应」
    /// </summary>
    public static async Task<ObservabilityFixture> StartAsync(
        FakeUpstreamHandler fake,
        TestVault vault,
        string? apiKey = null,
        int recentCapacity = 50,
        bool throwOnRecord = false,
        CancellationToken ct = default)
    {
        var host = await GatewayHost.StartReadyAsync(
            fake: fake,
            vault: vault,
            apiKey: apiKey,
            configure: services =>
            {
                // ── P1 会话粘性 ──────────────────────────────
                services.AddSingleton(sp => new ConversationAffinity(
                    sp.GetRequiredService<AccountPool>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ConversationAffinity>>(),
                    new AffinityOptions
                    {
                        // 测试里 TTL 拉长 —— 免得跑得慢一点就「过期了」，变成偶发失败
                        TimeToLive = TimeSpan.FromHours(1),
                        Capacity = 256,
                    }));

                // ── P2 指标 + 脱敏日志 ───────────────────────
                var metrics = new RequestMetrics(
                    TimeProvider.System,
                    new MetricsOptions { RecentCapacity = recentCapacity });

                services.AddSingleton(metrics);
                services.AddSingleton<IMetricsSource>(
                    throwOnRecord ? new ExplodingMetrics(metrics) : metrics);

                services.AddSingleton(sp => new RequestLog(sp.GetRequiredService<TimeProvider>()));

                // ⚠ 用工厂**替换**掉 GatewayHost 里那个不带这三个模块的
                // AddSingleton<GatewayApp>()。宿主是先注册的，这里后注册 ——
                // DI 解析单例时取最后一条注册，所以这个赢。
                // （configure 在宿主 Build 之前跑，见 GatewayHost.StartAsync。）
                services.AddSingleton(sp => new GatewayApp(
                    sp.GetRequiredService<GatewayOptions>(),
                    sp.GetRequiredService<UpstreamVariant>(),
                    sp.GetRequiredService<ModelCatalog>(),
                    sp.GetRequiredService<BuildInfo>(),
                    sp.GetRequiredService<UpstreamClient>(),
                    sp.GetRequiredService<AccountPool>(),
                    sp.GetRequiredService<ICredentialSource>(),
                    sp.GetRequiredService<ChatObservers>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GatewayApp>>(),
                    sp.GetRequiredService<ConversationAffinity>(),
                    sp.GetRequiredService<IMetricsSource>(),
                    sp.GetRequiredService<RequestLog>()));
            },
            ct: ct).ConfigureAwait(false);

        // 把实例捞回来 —— 断言要直接读它们
        var services = host.Application.Services;
        return new ObservabilityFixture(
            host,
            services.GetRequiredService<RequestMetrics>(),
            services.GetRequiredService<RequestLog>(),
            services.GetRequiredService<ConversationAffinity>(),
            vault?.Entries ?? []);
    }

    /// <summary>发一个聊天请求。</summary>
    /// <param name="json">请求体</param>
    /// <param name="requestId">要带的 X-Request-Id（null = 不带，让网关生成）</param>
    public Task<HttpResponseMessage> ChatAsync(string json, string? requestId = null)
        => ChatAsync(json, requestId, apiKey: null);

    /// <summary>发一个聊天请求（指定 key，验鉴权路径）。</summary>
    public async Task<HttpResponseMessage> ChatAsync(string json, string? requestId, string? apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(requestId))
        {
            request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        }

        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        }

        return await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
    }

    /// <summary>取 /metrics 并解析。</summary>
    public async Task<JsonObject> MetricsJsonAsync()
    {
        using var response = await Client.GetAsync("/metrics").ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonNode.Parse(text)!.AsObject();
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>
    /// 一个「记录就炸」的指标实现 —— 验旁路隔离。
    ///
    /// 为什么要这个：指标/日志挂了绝不能影响用户已经拿到的响应。
    /// 光靠读代码「看起来是 try/catch 包住的」不够 ——
    /// 得真的让它炸一次，看用户的响应还在不在。
    /// </summary>
    private sealed class ExplodingMetrics(IMetricsSource inner) : IMetricsSource
    {
        public MetricsSnapshot Snapshot() => inner.Snapshot();

        public void Record(RequestRecord record)
            => throw new InvalidOperationException("指标故意炸了（测试旁路隔离）");

        public void Reset() => inner.Reset();
    }
}
