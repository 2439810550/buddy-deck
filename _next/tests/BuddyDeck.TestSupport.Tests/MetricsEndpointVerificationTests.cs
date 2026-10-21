using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BuddyDeck.TestSupport;
using Xunit;

namespace BuddyDeck.TestSupport.Tests;

/// <summary>
/// **独立验证** `GET /metrics` —— 用真宿主 + 真 HTTP，并且用
/// **界面那边的 DTO 形状**逐字段反序列化。
///
/// ## 为什么这份验证要独立做（而不是信实现方的自测）
///
/// 实现方（observability 的 t2）自己写了一批 metrics 测试，全绿。
/// 但「实现方自己验自己」有一个结构性盲区：
///
/// ```
/// 它的测试断言的是「我发出去的 JSON 里有什么」
/// 而真正要保证的是「**别人按契约读的时候读得对不对**」
/// ```
///
/// 两者只在「写出去的键名 == 读回来的属性名」时才等价 —— 而**那正是
/// 本项目已经栽过一次的地方**：`IsTerminal` ← `terminal` 键，
/// `System.Text.Json` 的 `PropertyNameCaseInsensitive` **不做 `Is` 前缀推理**，
/// 于是字段静默永远为默认值（编译不报、运行不报）。
///
/// 所以这里刻意**复制一份界面侧的 DTO 形状**（`UiGatewayMetrics` /
/// `UiRecentRequest`，逐字段对着 `src/BuddyDeck.App/Services/IGatewayClient.cs`
/// 抄），用它去读真网关的响应。**读不出来就是缺陷。**
///
/// ## ⚠ 为什么要复制 DTO 而不是引用 App 项目
///
/// `tests/BuddyDeck.TestSupport.Tests/` 引用 App 项目的话，
/// 这个测试项目就绑死在 WPF 上（App 是 `UseWPF`），而且会拖进整个
/// App 的依赖树。测试夹层不该有这种依赖。
///
/// 代价：App 的 DTO 改了，这份副本要跟着改 —— 所以下面每个 DTO 都标了
/// 「对着哪个文件抄的」，改的时候能找对地方。
/// </summary>
[Collection("GatewayHost")]   // 宿主要真绑端口，串行跑
public class MetricsEndpointVerificationTests
{
    // ══════════════════════════════════════════════════════════
    // 界面侧 DTO 的副本（对着 src/BuddyDeck.App/Services/IGatewayClient.cs 抄）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 副本来源：`src/BuddyDeck.App/Services/IGatewayClient.cs` 的 `GatewayMetrics`。
    ///
    /// ⚠ 这里**故意不写 `[JsonPropertyName]`**（与 App 那份一致）——
    /// 因为要验的正是「不写显式映射时，默认的大小写不敏感匹配够不够用」。
    /// 如果某个键读不回来，这里会暴露出来。
    /// </summary>
    private sealed record UiGatewayMetrics
    {
        public long TotalRequests { get; init; }
        public long AttemptCount { get; init; }
        public double HttpSuccessRate { get; init; }
        public double CompletionRate { get; init; }
        public double AverageLatencyMs { get; init; }
        public IReadOnlyList<UiRecentRequest> Recent { get; init; } = [];
    }

    /// <summary>
    /// 副本来源：同文件的 `RecentRequest`。
    ///
    /// ⚠ `Streamed` / `IsTerminal` / `SentToUpstream` 三个**必须显式映射** ——
    /// 键名是 `streamed` / `terminal` / `sentToUpstream`，而
    /// `IsTerminal` ← `terminal` 是**匹配不上的**（无 `Is` 前缀推理）。
    /// App 那份就是靠这三行 `[JsonPropertyName]` 才对的。
    /// </summary>
    private sealed record UiRecentRequest
    {
        public DateTimeOffset At { get; init; }
        public string Model { get; init; } = "";
        public int Status { get; init; }
        public long DurationMs { get; init; }
        public string AccountTag { get; init; } = "";

        [JsonPropertyName("streamed")]
        public bool Streamed { get; init; }

        public int Attempts { get; init; } = 1;

        [JsonPropertyName("terminal")]
        public bool IsTerminal { get; init; } = true;

        [JsonPropertyName("sentToUpstream")]
        public bool SentToUpstream { get; init; } = true;
    }

    /// <summary>和 App 一样的反序列化选项（`PropertyNameCaseInsensitive = true`）。</summary>
    private static readonly JsonSerializerOptions UiOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static UiGatewayMetrics ParseUi(string json)
        => JsonSerializer.Deserialize<UiGatewayMetrics>(json, UiOptions)
           ?? throw new InvalidOperationException("反序列化返回 null");

    // ══════════════════════════════════════════════════════════
    // ① 可达性 + 鉴权
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 带对_key_时_可达且返回200()
    {
        await using var host = await GatewayHost.StartReadyAsync(apiKey: "metrics-secret");

        using var request = GatewayHost.BearerRequest(HttpMethod.Get, "/metrics", "metrics-secret");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Content-Type 必须是 JSON（界面按 JSON 解析）
        Assert.Contains("application/json", response.Content.Headers.ContentType!.ToString());

        await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task 无_key_时返回401()
    {
        await using var host = await GatewayHost.StartReadyAsync(apiKey: "metrics-secret");

        using var response = await host.Client.GetAsync("/metrics");

        // ★ 鉴权策略与 `/v1/models` 一致 —— **不豁免**
        //   （指标里有账号标签，不该像 /healthz 那样裸奔）
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task 错_key_时返回401()
    {
        await using var host = await GatewayHost.StartReadyAsync(apiKey: "metrics-secret");

        using var request = GatewayHost.BearerRequest(HttpMethod.Get, "/metrics", "wrong-key");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task 与_v1_models_的鉴权策略一致()
    {
        // 钉住「两者同策略」这个不变量 —— 免得将来有人把 /metrics 挪到免鉴权区。
        await using var host = await GatewayHost.StartReadyAsync(apiKey: "metrics-secret");

        using var models = await host.Client.GetAsync("/v1/models");
        using var metrics = await host.Client.GetAsync("/metrics");

        Assert.Equal(models.StatusCode, metrics.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, metrics.StatusCode);

        await models.Content.ReadAsStringAsync();
        await metrics.Content.ReadAsStringAsync();
    }

    // ══════════════════════════════════════════════════════════
    // ② 空状态：不能 500
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 还没收到任何请求时_返回全零与空数组_而不是500()
    {
        await using var host = await GatewayHost.StartReadyAsync(apiKey: "metrics-secret");

        using var request = GatewayHost.BearerRequest(HttpMethod.Get, "/metrics", "metrics-secret");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var metrics = ParseUi(json);

        // 全 0 + 空数组，而不是 500
        Assert.Equal(0, metrics.TotalRequests);
        Assert.Equal(0, metrics.AttemptCount);
        Assert.Equal(0d, metrics.HttpSuccessRate);
        Assert.Equal(0d, metrics.CompletionRate);
        Assert.Equal(0d, metrics.AverageLatencyMs);
        Assert.Empty(metrics.Recent);
    }

    // ══════════════════════════════════════════════════════════
    // ③ 字段随真实请求变化（不是「全 0 常量」的假绿）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 发一次真实请求后_字段按期望变化()
    {
        // ★ 这条是防「看起来对、其实是常量」的关键 ——
        //   只断言「能反序列化」的话，一个永远回全 0 的端点也能过。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.SseText(
            """{"choices":[{"index":0,"delta":{"content":"ok"}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));

        await using var host = await GatewayHost.StartReadyAsync(
            fake: fake,
            vault: new TestVault().AddMany(2),
            apiKey: "metrics-secret");

        // 先发两次真实的聊天请求
        for (var i = 0; i < 2; i++)
        {
            using var chat = GatewayHost.BearerRequest(HttpMethod.Post, "/v1/chat/completions", "metrics-secret");
            chat.Content = new StringContent(
                "{\"model\":\"hy3\",\"stream\":false,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
                Encoding.UTF8, "application/json");

            using var chatResponse = await host.Client.SendAsync(chat);
            Assert.Equal(HttpStatusCode.OK, chatResponse.StatusCode);
            await chatResponse.Content.ReadAsStringAsync();
        }

        using var request = GatewayHost.BearerRequest(HttpMethod.Get, "/metrics", "metrics-secret");
        using var response = await host.Client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        var metrics = ParseUi(raw);

        // ★ 主口径：2 次客户端请求
        Assert.Equal(2, metrics.TotalRequests);
        Assert.Equal(1.0, metrics.HttpSuccessRate, 3);
        Assert.Equal(1.0, metrics.CompletionRate, 3);
        Assert.True(metrics.AverageLatencyMs >= 0);

        // ★ Recent 里有记录，而且**能在界面 DTO 上读出来**
        Assert.NotEmpty(metrics.Recent);

        var first = metrics.Recent[0];
        Assert.Equal("hy3", first.Model);
        Assert.Equal(200, first.Status);
        Assert.True(first.IsTerminal, "成功的终局记录读回来必须是 true");

        // 顺便确认 JSON 里确实有这些键（否则「读出来」可能只是默认值巧合）
        var node = JsonNode.Parse(raw)!;
        Assert.True(node["totalRequests"]!.GetValue<long>() == 2);
        Assert.NotNull(node["recent"]);
        Assert.NotEmpty(node["recent"]!.AsArray());
    }

    [Fact]
    public async Task 上游失败时_指标如实反映_且_recent_能在界面形状上读出终局性()
    {
        // 这条覆盖的是「中间尝试 vs 终局」在 **HTTP JSON 层**能不能被区分 ——
        // 界面靠 `IsTerminal` 过滤中间态，读错的话「重试过程」与「真结果」会混在一起。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            UpstreamResponses.TooManyRequests(),
            UpstreamResponses.SseText(
                """{"choices":[{"index":0,"delta":{"content":"救回来了"}}]}""",
                """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));

        await using var host = await GatewayHost.StartReadyAsync(
            fake: fake,
            vault: new TestVault().AddMany(2),
            apiKey: "metrics-secret");

        using (var chat = GatewayHost.BearerRequest(HttpMethod.Post, "/v1/chat/completions", "metrics-secret"))
        {
            chat.Content = new StringContent(
                "{\"model\":\"hy3\",\"stream\":false,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
                Encoding.UTF8, "application/json");

            using var chatResponse = await host.Client.SendAsync(chat);
            Assert.Equal(HttpStatusCode.OK, chatResponse.StatusCode);
            await chatResponse.Content.ReadAsStringAsync();
        }

        using var request = GatewayHost.BearerRequest(HttpMethod.Get, "/metrics", "metrics-secret");
        using var response = await host.Client.SendAsync(request);
        var metrics = ParseUi(await response.Content.ReadAsStringAsync());

        // 1 次客户端请求（重试过，但用户只发了一次）
        Assert.Equal(1, metrics.TotalRequests);

        // 有过重试 → AttemptCount > TotalRequests
        Assert.True(metrics.AttemptCount >= metrics.TotalRequests,
            $"AttemptCount({metrics.AttemptCount}) 应 ≥ TotalRequests({metrics.TotalRequests})");

        // ★ 中间失败的尝试也在 recent 里，而且**读回来 IsTerminal=false**
        //
        // 如果 JSON 键名对不上（`terminal` vs `IsTerminal`），这里会是 true。
        var intermediate = metrics.Recent.Where(r => !r.IsTerminal).ToList();
        Assert.NotEmpty(intermediate);

        // 终局那条存在
        Assert.Contains(metrics.Recent, r => r.IsTerminal);
    }

    // ══════════════════════════════════════════════════════════
    // ④ 原始 JSON 形状（键名逐字）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task JSON_键名是_camelCase_且逐个存在()
    {
        // 界面 DTO 靠**大小写不敏感匹配**读这些键。把键名钉住，
        // 免得有人把它改成 PascalCase / snake_case。
        await using var host = await GatewayHost.StartReadyAsync(apiKey: "metrics-secret");

        using var request = GatewayHost.BearerRequest(HttpMethod.Get, "/metrics", "metrics-secret");
        using var response = await host.Client.SendAsync(request);

        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        foreach (var key in (string[])
                 [
                     "totalRequests", "attemptCount", "httpSuccessRate", "completionRate",
                     "averageLatencyMs", "p95LatencyMs", "maxLatencyMs",
                     "recent", "outcomes", "errorKinds", "startedAtUnixMs",
                 ])
        {
            Assert.True(node.ContainsKey(key), $"缺少键 `{key}`（实际键：{string.Join(",", node.Select(kv => kv.Key))}）");
        }
    }

    [Fact]
    public async Task recent_里每条记录的键名与界面子结构一致()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.SseText(
            """{"choices":[{"index":0,"delta":{"content":"ok"}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));

        await using var host = await GatewayHost.StartReadyAsync(
            fake: fake, vault: new TestVault().Add(), apiKey: "metrics-secret");

        using (var chat = GatewayHost.BearerRequest(HttpMethod.Post, "/v1/chat/completions", "metrics-secret"))
        {
            chat.Content = new StringContent(
                "{\"model\":\"hy3\",\"stream\":false,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
                Encoding.UTF8, "application/json");
            using var r = await host.Client.SendAsync(chat);
            await r.Content.ReadAsStringAsync();
        }

        using var request = GatewayHost.BearerRequest(HttpMethod.Get, "/metrics", "metrics-secret");
        using var response = await host.Client.SendAsync(request);

        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var first = node["recent"]!.AsArray()[0]!.AsObject();

        // 界面 `RecentRequest` 读的键（三个显式映射的尤其重要）
        foreach (var key in (string[])
                 [
                     "at", "model", "status", "durationMs", "accountTag",
                     "streamed", "attempts", "terminal", "sentToUpstream",
                 ])
        {
            Assert.True(first.ContainsKey(key), $"recent[0] 缺少键 `{key}`（实际：{string.Join(",", first.Select(kv => kv.Key))}）");
        }

        // ⚠ 这三个键名**不能**带 `is` 前缀 —— 界面是用 `terminal` 去读
        //   `[JsonPropertyName("terminal")] IsTerminal` 的。
        Assert.False(first.ContainsKey("isTerminal"), "键名不该是 isTerminal（界面按 terminal 读）");
    }
}
