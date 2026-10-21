using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 目录刷新的测试（t53）。
///
/// ## 这一批测的是一个「从来没成功过、而且没人知道」的缺陷
///
/// ```
/// GatewayApp.cs:405  FetchCatalogAsync(credential, null, ct)   ← 不带 UA
///                    而实测（t48）：不带 UA 访问 /v3/config → 400 / code 12403
/// GatewayApp.cs:413  失败日志压在 Debug 级                       ← 生产里看不见
/// ⇒ 永久回落到 17 个内置模型，界面毫无异常，用户永远看不到真机目录
/// ```
///
/// 所以断言分三块：
///   ① **UA 形态正确**（不是「非空就行」—— 上游按 UA 分流花名册）
///   ② **失败不再静默**（Warning 级，且含足够定位信息）
///   ③ 失败时**仍回落内置目录**（可用性优先，这条不能被修坏）
/// </summary>
public class CatalogRefreshTests
{
    /// <summary>造一个能观察日志的 logger。</summary>
    private sealed class CapturingLogger : ILogger<CatalogRefresh>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static Credential TestCredential = new()
    {
        AccessToken = "test-token",
        Uid = "u1",
        EnterpriseId = "ent-1",
        Domain = "workbuddy.cn",
        Label = "测试号",
    };

    /// <summary>造一个记录请求的假上游 + 一个刷新器。</summary>
    private static (CatalogRefresh Refresh, UpstreamHandler Fake, ModelCatalog Catalog, CapturingLogger Log)
        Make(Func<HttpRequestMessage, HttpResponseMessage> handler, UpstreamVariant? variant = null)
    {
        var fake = new UpstreamHandler(handler);
        var http = new HttpClient(fake);
        var v = variant ?? UpstreamVariant.China;
        var upstream = new UpstreamClient(http, v, Microsoft.Extensions.Logging.Abstractions.NullLogger<UpstreamClient>.Instance);
        var catalog = new ModelCatalog(v.Id, "hy3");
        var log = new CapturingLogger();

        return (new CatalogRefresh(upstream, catalog, v, log), fake, catalog, log);
    }

    /// <summary>一个能记录 UA 与请求 URI 的假处理器。</summary>
    private sealed class UpstreamHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public List<string?> UserAgents { get; } = [];

        /// <summary>收到的请求（只留 URI —— 断言端点用）。</summary>
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // ⚠ 这里**不能**用 string.Join(",") 把多个 part 拼起来。
            //
            // HttpRequestHeaders.TryGetValues 对 User-Agent 返回的是**按空格切分的多个 part**，
            // 不是原始那一整行：
            //     发出 "CLI/2.63.2 CodeBuddy/2.63.2"
            //     读回 ["CLI/2.63.2", "CodeBuddy/2.63.2"]   ← 两个
            // 用 "," 拼就变成 "CLI/2.63.2,CodeBuddy/2.63.2"，与产品代码里的常量对不上，
            // 于是测试报「Strings differ」—— 而**产品代码完全正确**，是这里读错了。
            //
            // 空格才是原始分隔符（HTTP 头里 UA 的各 part 由空格连接），所以用 " "。
            UserAgents.Add(request.Headers.TryGetValues("User-Agent", out var v)
                ? string.Join(" ", v)
                : null);
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }

    // ══ ① UA 形态（不是「非空」就行）═══════════════════════════

    [Fact]
    public void 国内版对目录用_CLI_形态的_UA()
    {
        // ★ 值取自旧版 `upstream.js:26` 的 `CLIENT_UA`。
        //   注意它**不是** `WorkBuddy/…` 形态 —— 旧版对目录/刷新/计费
        //   这类非聊天请求统一用 CLI 形态，聊天才用 App 形态。
        Assert.Equal("CLI/2.63.2 CodeBuddy/2.63.2", CatalogRefresh.UserAgentFor(UpstreamVariant.China));
    }

    [Fact]
    public void 国际版用_App_形态的_UA()
    {
        // 旧版注释（upstream.js:644-645）：国际版**没有 CLI 身份**，保持 App 形态。
        // 一个常量套两个变体是错的 —— 国际版会拿到国内版的名册。
        Assert.NotEqual(
            CatalogRefresh.UserAgentFor(UpstreamVariant.China),
            CatalogRefresh.UserAgentFor(UpstreamVariant.Global));
    }

    [Fact]
    public async Task 刷新时真的把_UA_发出去了()
    {
        // ★ 这条是 t53 的核心回归：断言**请求头上真有 UA**。
        var (refresh, fake, _, _) = Make(_ => OkCatalog("hy3", "glm-5.3"));

        await refresh.TryRefreshAsync(TestCredential, CancellationToken.None);

        var ua = Assert.Single(fake.UserAgents);
        Assert.False(string.IsNullOrEmpty(ua), "目录请求必须带 UA —— 不带会 400/12403");
        Assert.Equal(CatalogRefresh.CliFormUserAgent, ua);
    }

    [Fact]
    public async Task 刷新打到_billing_域名的_v3_config()
    {
        var (refresh, fake, _, _) = Make(_ => OkCatalog("hy3"));

        await refresh.TryRefreshAsync(TestCredential, CancellationToken.None);

        Assert.Equal("https://www.codebuddy.cn/v3/config", fake.Requests.Single().RequestUri!.ToString());
    }

    // ══ ② 失败不再静默 ════════════════════════════════════════

    [Fact]
    public async Task 上游_400_时_以_Warning_记日志并带原始正文()
    {
        // ★ 契约明确要求：**若上游仍返回 400，须给出原始响应体而非「失败」二字**。
        //   也用这条钉住「日志级别是 Warning 而不是 Debug」。
        const string upstreamBody = """{"code":12403,"message":"missing user-agent"}""";
        var (refresh, _, _, log) = Make(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(upstreamBody, Encoding.UTF8, "application/json"),
        });

        var ok = await refresh.TryRefreshAsync(TestCredential, CancellationToken.None);

        Assert.False(ok);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);          // ← 不是 Debug

        // 足够定位的四样东西都要在
        Assert.Contains("codebuddy.cn/v3/config", entry.Message, StringComparison.Ordinal);  // 端点
        Assert.Contains("CLI/2.63.2", entry.Message, StringComparison.Ordinal);               // 用的 UA
        Assert.Contains("400", entry.Message, StringComparison.Ordinal);                       // 状态码
        Assert.Contains("12403", entry.Message, StringComparison.Ordinal);                     // 原始正文
        Assert.Contains("内置目录", entry.Message, StringComparison.Ordinal);                  // 回落说明
    }

    [Fact]
    public async Task 刷新失败时_仍回落到内置目录_可用性优先()
    {
        // 失败**不抛**（端点可用性比目录实时性重要）—— 这条不能被「响亮失败」改坏。
        var (refresh, _, catalog, _) = Make(_ => throw new HttpRequestException("DNS 挂了"));

        var ok = await refresh.TryRefreshAsync(TestCredential, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(17, catalog.Count);   // 内置目录原样保留
    }

    [Fact]
    public async Task 拿到空列表时_不算成功且回落到内置()
    {
        // 「拿到了但一个模型都没有」也是失败 —— 替换成空目录会
        // 让客户端看到 0 个模型，比用内置的还糟。
        var (refresh, _, catalog, log) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"models":[]}""", Encoding.UTF8, "application/json"),
        });

        var ok = await refresh.TryRefreshAsync(TestCredential, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(17, catalog.Count);
        Assert.Equal(LogLevel.Warning, Assert.Single(log.Entries).Level);
    }

    // ══ ③ 成功路径 ════════════════════════════════════════════

    [Fact]
    public async Task 刷新成功时_目录被替换成上游的()
    {
        var (refresh, _, catalog, log) = Make(_ => OkCatalog("hy3", "hy4-preview", "glm-5.3"));

        var ok = await refresh.TryRefreshAsync(TestCredential, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(3, catalog.Count);
        Assert.Equal("hy3", catalog.Models[0].Id);
        Assert.Empty(log.Entries);   // 成功不该有 Warning
    }

    [Fact]
    public void 解析_信封里的_data_models()
    {
        var node = JsonNode.Parse("""{"code":0,"data":{"models":[{"id":"a"},{"id":"b"}]}}""");
        var models = CatalogRefresh.ExtractModels(node);

        Assert.Equal(2, models.Count);
        Assert.Equal("a", models[0].Id);
    }

    [Fact]
    public void 解析_顶层_models()
    {
        var node = JsonNode.Parse("""{"models":[{"id":"x","name":"X 模型"}]}""");
        var models = CatalogRefresh.ExtractModels(node);

        Assert.Single(models);
        Assert.Equal("x", models[0].Id);
        Assert.Equal("X 模型", models[0].Name);
    }

    [Fact]
    public void 解析_没有_id_的条目被跳过()
    {
        var node = JsonNode.Parse("""{"models":[{"name":"没有 id"},{"id":"ok"}]}""");
        var models = CatalogRefresh.ExtractModels(node);

        Assert.Single(models);
        Assert.Equal("ok", models[0].Id);
    }

    [Fact]
    public void Summarize_空正文也返回非空串()
    {
        Assert.False(string.IsNullOrWhiteSpace(CatalogRefresh.Summarize(null)));
        Assert.False(string.IsNullOrWhiteSpace(CatalogRefresh.Summarize("")));
    }

    [Fact]
    public void Summarize_超长时截断()
    {
        var huge = new string('x', 1000);
        var result = CatalogRefresh.Summarize(huge, max: 100);

        Assert.True(result.Length <= 101, $"实际 {result.Length}");
    }

    // ── 便捷 ──────────────────────────────────────────────────

    private static HttpResponseMessage OkCatalog(params string[] ids)
    {
        var models = new JsonArray();
        foreach (var id in ids) models.Add(new JsonObject { ["id"] = id });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject { ["models"] = models }.ToJsonString(),
                Encoding.UTF8, "application/json"),
        };
    }
}
