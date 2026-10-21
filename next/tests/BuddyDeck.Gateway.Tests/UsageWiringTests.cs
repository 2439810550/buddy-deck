using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 用量账本的**装配**测试（t29）。
///
/// ## 为什么这类测试必须存在（它验的是第 ② 层）
///
/// 「写完了」有三个层次，任何一个断了都等于没做：
///
/// ```
/// ① 类型/实现存在            ← 单测能过（UsageLedgerTests 就是这层）
/// ② 被装配（DI 注册、路由挂载、调用点接线）  ← ★ 本文件验的
/// ③ 在真实请求路径上被走到     ← 只有端到端能验
/// ```
///
/// **M10 卡在第 ② 层，而第 ① 层的单测永远验不出它** ——
/// 实测：t29 之前 `UsageLedger` / `UsageEndpoints` 单测全绿，
/// 但跑了几十次 chat 请求 `usage.jsonl` 一个字节都没落盘、`/usage/*` 全 404。
///
/// 所以这里**必须起真宿主 + 发真请求**，断言「账本真的被写了」。
/// 断言内部方法调用（`Append` 被调过）是不够的 —— 那还是第 ① 层。
/// </summary>
public class UsageWiringTests
{
    private const string ChatBody =
        """{"model":"hy3","messages":[{"role":"user","content":"你好"}],"stream":false}""";

    /// <summary>一次正常的流式回复。</summary>
    private static HttpResponseMessage OkSse() => UpstreamResponses.SseText(
        """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""",
        """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{"content":"好"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""");

    /// <summary>
    /// 起一个**挂了账本**的真宿主。
    ///
    /// 账本路径指向临时目录（每个用例一个），互相不干扰。
    /// </summary>
    private sealed class LedgerHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public HttpClient Client { get; }
        public string LedgerPath { get; }
        public FakeUpstreamHandler Fake { get; }

        private LedgerHost(WebApplication app, string baseAddress, string ledgerPath, FakeUpstreamHandler fake)
        {
            _app = app;
            LedgerPath = ledgerPath;
            Fake = fake;
            Client = new HttpClient(new SocketsHttpHandler())
            {
                BaseAddress = new Uri(baseAddress),
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        /// <summary>账本里现在有几行**非空**行。</summary>
        public int LedgerLines() => ReadLines().Count(l => l.Trim().Length > 0);

        /// <summary>账本里能成功解析成 JSON 的行数（用来断言「0 坏行」）。</summary>
        public int ValidJsonLines()
        {
            var ok = 0;
            foreach (var line in ReadLines())
            {
                if (line.Trim().Length == 0) continue;
                try { JsonNode.Parse(line); ok++; }
                catch (System.Text.Json.JsonException) { }
            }
            return ok;
        }

        /// <summary>
        /// 读账本的全部行。
        ///
        /// ## ⚠ 为什么不能用 <c>File.ReadAllLines</c>
        ///
        /// `UsageLedger` **长期持有写句柄**（`UsageLedger.cs:624` 的设计：
        /// 「长期持有句柄，只在需要时重开」—— 每笔都重开文件的开销不值）。
        ///
        /// 而 `File.ReadAllLines` 默认以 `FileShare.Read` 打开，
        /// 遇到别人持着写句柄时会抛
        /// `IOException: The process cannot access the file … being used by another process`。
        ///
        /// 所以这里必须显式用 `FileShare.ReadWrite` —— **和账本自己读文件时的
        /// 打开方式一致**（它要能边写边读自己的账本）。
        /// </summary>
        private List<string> ReadLines()
        {
            if (!File.Exists(LedgerPath)) return [];

            using var fs = new FileStream(
                LedgerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return [.. reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r'))];
        }

        /// <summary>读账本第一行有效条目（JSON）。</summary>
        public string? FirstLine() => ReadLines().FirstOrDefault(l => l.Trim().Length > 0);

        public Task<HttpResponseMessage> ChatAsync(string? apiKey = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent(ChatBody, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            }
            return Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        public Task<HttpResponseMessage> GetUsageAsync(string path, string? apiKey = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            }
            return Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        public static async Task<LedgerHost> StartAsync(
            FakeUpstreamHandler fake,
            string? apiKey = null,
            Action<IServiceCollection>? configure = null)
        {
            var dir = Directory.CreateTempSubdirectory("buddydeck-usage-").FullName;
            var ledgerPath = Path.Combine(dir, "usage.jsonl");

            var options = new GatewayOptions
            {
                Host = "127.0.0.1",
                Port = 0,
                ApiKey = apiKey ?? "",
                Variant = UpstreamVariant.China.Id,
                DefaultModel = "hy3",
                LogDirectory = dir,
            };

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            var services = builder.Services;
            services.AddSingleton(options);
            services.AddSingleton(UpstreamVariant.China);
            services.AddSingleton(new ModelCatalog(UpstreamVariant.China.Id, "hy3"));
            // GatewayApp 的构造依赖它（/healthz 的 buildId）—— 漏了会在
            // 「activate GatewayApp」时抛「Unable to resolve service」。
            services.AddSingleton(new BuildInfo(AppContext.BaseDirectory));
            services.AddSingleton<AccountPool>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<ICredentialSource>(new FakeVault());
            services.AddSingleton(new HttpClient(fake, disposeHandler: false));
            services.AddSingleton(sp => new UpstreamClient(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<UpstreamVariant>(),
                sp.GetRequiredService<ILogger<UpstreamClient>>()));
            services.AddSingleton<ChatObservers>();
            services.AddSingleton(sp => new RequestLog(sp.GetRequiredService<TimeProvider>()));

            // ★ 与 Program.cs 同款的记账接线（这三行就是 t29 的核心）
            services.AddSingleton(sp => new UsageLedger(
                ledgerPath, sp.GetRequiredService<ILogger<UsageLedger>>()));
            services.AddSingleton<IChatObserver>(sp => new UsageLedgerObserver(
                sp.GetRequiredService<UsageLedger>(),
                sp.GetRequiredService<ILogger<UsageLedgerObserver>>()));
            services.AddSingleton(sp => new UsageEndpoints(
                sp.GetRequiredService<UsageLedger>(),
                sp.GetRequiredService<ILogger<UsageEndpoints>>()));

            services.AddSingleton<IEndpointFamily>(sp => new UsageFamilyAdapter(
                sp.GetRequiredService<UsageEndpoints>()));

            services.AddSingleton<GatewayApp>();

            // ⚠ 观察者必须**同时**挂上：`TryAddEnumerable` 的那条是默认 Null 实现，
            //   这里显式注册本测试用的那个（与 Program.cs 同形）。
            configure?.Invoke(services);

            var app = builder.Build();
            app.Services.GetRequiredService<GatewayApp>().Map(app);
            await app.StartAsync().ConfigureAwait(false);

            return new LedgerHost(app, app.Urls.First(), ledgerPath, fake);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        private sealed class FakeVault : ICredentialSource
        {
            public IReadOnlyList<VaultEntry> Load() =>
            [
                new VaultEntry
                {
                    Id = "acc-1:ent-1",
                    Uid = "uid-1",
                    EnterpriseId = "ent-1",
                    Domain = "workbuddy.cn",
                    AccessToken = "token-1",
                    ExpiresAtMs = 0,
                    Label = "测试号",
                },
            ];
        }
    }

    /// <summary>测试里的适配器（与 `EndpointFamilies.UsageFamily` 同形 —— 那个是 private）。</summary>
    private sealed class UsageFamilyAdapter(UsageEndpoints inner) : IEndpointFamily
    {
        public Task<bool> TryHandleAsync(HttpContext context) => inner.TryHandleAsync(context);
    }

    // ══ ① 记账钩子：跑 N 次 → 账本 N 行 ═══════════════════════

    [Fact]
    public async Task 跑一次_chat_请求_账本新增一行()
    {
        // ★ 这条是 t29 的核心 —— t29 之前这里是 0 行（文件压根不存在）。
        var fake = new FakeUpstreamHandler(_ => OkSse());
        await using var host = await LedgerHost.StartAsync(fake);

        Assert.Equal(0, host.LedgerLines());

        using var response = await host.ChatAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        Assert.Equal(1, host.LedgerLines());
    }

    [Fact]
    public async Task 跑_N_次_chat_请求_账本恰好_N_行()
    {
        // ★ 契约明确要求**精确相等**，不是「增加了」。
        //
        // 理由（队长转述 observability 的实测）：`AppendAllText` 有并发截断问题
        // （8 线程 × 50 条只落盘 326 行）—— 那种失败**不崩、只是安静地少写几行**，
        // 弱断言（「行数 > 0」）验不出来。
        const int n = 5;

        var fake = new FakeUpstreamHandler(_ => OkSse());
        await using var host = await LedgerHost.StartAsync(fake);

        for (var i = 0; i < n; i++)
        {
            using var response = await host.ChatAsync();
            await response.Content.ReadAsStringAsync();
        }

        Assert.Equal(n, host.LedgerLines());
        // 而且 0 坏行
        Assert.Equal(n, host.ValidJsonLines());
    }

    [Fact]
    public async Task 账本条目里_模型与用量都被记下()
    {
        var fake = new FakeUpstreamHandler(_ => OkSse());
        await using var host = await LedgerHost.StartAsync(fake);

        using (var response = await host.ChatAsync())
        {
            await response.Content.ReadAsStringAsync();
        }

        var line = host.FirstLine()!;
        var json = JsonNode.Parse(line)!.AsObject();

        // 记的是**请求的模型**（账本回答「我要的模型烧了多少额度」）
        Assert.Equal("hy3", json["model"]!.GetValue<string>());
        // usage 来自上游（10 / 5 / 15）
        Assert.Equal(15, json["totalTokens"]!.GetValue<long>());
        Assert.Equal("ok", json["outcome"]!.GetValue<string>());
    }

    // ══ ② 记账失败不得影响用户请求 ═══════════════════════════

    [Fact]
    public async Task 账本写不进去时_用户请求照常成功()
    {
        // ★ 契约第 ④ 条：记账是**旁路**，写不进去是运维问题，
        //   不该变成用户看到的 500。
        //
        // 怎么造「写不进去」：把账本路径指到一个**不存在的目录下的文件**
        // （父目录不存在 → 打开文件失败）。这比 mock 一个会抛的 ledger 更真实
        // —— 它走的是真正的 IO 失败路径。
        var fake = new FakeUpstreamHandler(_ => OkSse());
        await using var host = await LedgerHost.StartAsync(fake);

        // 等第一条写好（确认正常情况下能写）
        using (var warm = await host.ChatAsync())
        {
            await warm.Content.ReadAsStringAsync();
        }
        Assert.Equal(1, host.LedgerLines());

        // 删掉账本文件所在目录，让后续写入失败（父目录没了）
        var dir = Path.GetDirectoryName(host.LedgerPath)!;
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }

        // 再发请求 —— 客户端必须照常拿到 200
        using var response = await host.ChatAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ══ ③ 四个端点可达 + 鉴权 ════════════════════════════════

    [Theory]
    [InlineData("/usage/summary")]
    [InlineData("/usage/by-model")]
    [InlineData("/usage/by-account")]
    [InlineData("/usage/recent")]
    public async Task 四个端点都可达且返回可解析_JSON(string path)
    {
        // ★ t29 之前这四个全是 404 —— 因为 UsageEndpoints 没被注册成端点族。
        var fake = new FakeUpstreamHandler(_ => OkSse());
        await using var host = await LedgerHost.StartAsync(fake);

        using var response = await host.GetUsageAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.NotNull(JsonNode.Parse(text));   // 能反序列化
    }

    [Fact]
    public async Task usage_端点不豁免鉴权_无key_401()
    {
        // 账本里有账号标签与用量，与 /admin/* 同级敏感 —— 绝不能漏在鉴权之前。
        var fake = new FakeUpstreamHandler(_ => OkSse());
        await using var host = await LedgerHost.StartAsync(fake, apiKey: "secret-key");

        using var response = await host.GetUsageAsync("/usage/summary");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task usage端点_错key也401()
    {
        var fake = new FakeUpstreamHandler(_ => OkSse());
        await using var host = await LedgerHost.StartAsync(fake, apiKey: "secret-key");

        using var response = await host.GetUsageAsync("/usage/summary", apiKey: "wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 记完账后_端点上能读到刚才那次请求()
    {
        // 端到端闭环：**写进去的**能被**读出来**。
        // 分开验「写了」和「能读」是不够的 —— 中间可能写到了不同路径。
        var fake = new FakeUpstreamHandler(_ => OkSse());
        await using var host = await LedgerHost.StartAsync(fake);

        using (var chat = await host.ChatAsync())
        {
            await chat.Content.ReadAsStringAsync();
        }

        using var summary = await host.GetUsageAsync("/usage/summary");
        var json = JsonNode.Parse(await summary.Content.ReadAsStringAsync())!.AsObject();

        // ★ 闭环的关键：**写进去的**能被**读出来**。
        //   分开验「写了」和「能读」不够 —— 中间可能写到了不同路径/不同账本实例。
        //
        //   键名以 UsageEndpoints 的实际输出为准（:127-129）：
        //   `entryCount` = 账本里的条目数，`totals.totalTokens` = 累计 token。
        Assert.Equal(1, json["entryCount"]!.GetValue<int>());

        // 精确断言（键名以 UsageEndpoints 的 `Row(...)` 为准，不猜）：
        //   `totals.tokens`  = 累计总 token
        //   `totals.promptTokens` / `totals.completionTokens` = 明细
        // 上游给的 usage 是 prompt=10 / completion=5 / total=15。
        var totals = json["totals"]!.AsObject();
        Assert.Equal(15, totals["tokens"]!.GetValue<long>());
        Assert.Equal(10, totals["promptTokens"]!.GetValue<long>());
        Assert.Equal(5, totals["completionTokens"]!.GetValue<long>());
    }
}
