using System.Net;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.Gateway;
using BuddyDeck.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// P1/P2 三个模块在**真网关**上的端到端测试。
///
/// ## 为什么不用纯单元测试
///
/// 指标/粘性/日志的价值全在「和真实请求路径连上」这一件事上：
///   · 指标算得再对，没接进终局点就是一片零
///   · 粘性逻辑再对，没接进选号点就永远不生效
///   · 日志脱敏再对，网关顺手多传了一个字段进去就全白做
///
/// 所以这里起真 Kestrel、真 HttpClient、假上游，把整条链路走通。
///
/// 夹具用共享的 GatewayHost + FakeUpstreamHandler（见 docs/test-support.md）——
/// 端口永远由系统分配（127.0.0.1:0），绝不碰 8787/3080。
/// </summary>
[Collection("GatewayHost")]
public class GatewayObservabilityTests
{
    /// <summary>一段**独特标记**的提示词。脱敏断言靠它。</summary>
    private const string PromptMarker = "ZZTOP_SECRET_PROMPT_9f3a7c";

    /// <summary>一个假 token 标记串。断言它不出现在日志里。</summary>
    private const string SecretToken = "sk-SUPERSECRET-access-token-0123456789abcdef";

    /// <summary>
    /// 造 N 个账号。每个的 accessToken 里都带 SecretToken 标记，
    /// 这样「日志里不含 accessToken 原值」这条断言才有东西可查。
    /// </summary>
    private static TestVault Vault(params string[] ids)
    {
        var vault = new TestVault();
        foreach (var id in ids)
        {
            vault.Add(
                uid: $"uid-{id}",
                enterpriseId: $"ent-{id}",
                domain: "workbuddy.cn",
                label: $"号-{id}",
                accessToken: $"{SecretToken}-{id}",
                refreshToken: "refresh-secret");
        }
        return vault;
    }

    /// <summary>造一个聊天请求体。</summary>
    private static JsonObject Body(string firstUser, string system = "你是助手", bool stream = true, string model = "hy3")
        => new()
        {
            ["model"] = model,
            ["stream"] = stream,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = firstUser },
            },
        };

    /// <summary>一次正常的流式回答（一个内容分片 + 一个收尾分片）。</summary>
    private static HttpResponseMessage ChatOk(string delta = "你好")
        => UpstreamResponses.SseText(
            new JsonObject
            {
                ["choices"] = new JsonArray
                {
                    new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["content"] = delta } },
                },
            }.ToJsonString(Json.Relaxed),
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}""");

    private static FakeUpstreamHandler FakeOk(string delta = "你好")
        => new(_ => ChatOk(delta));

    /// <summary>发一个聊天请求（用夹具的 HttpClient）。</summary>
    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        JsonObject body,
        string? requestId = null,
        string? apiKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(Json.Relaxed), System.Text.Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(requestId))
        {
            request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        }

        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
    }

    // ══════════════════════════════════════════════════════════
    // ② 指标
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 指标能报出总数和成功率()
    {
        var fake = FakeOk();
        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b", "c"));

        for (var i = 0; i < 3; i++)
        {
            using var response = await PostAsync(fx.Client, Body($"问题 {i}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync();
        }

        var snapshot = fx.Metrics.Snapshot();

        Assert.Equal(3, snapshot.TotalRequests);
        Assert.Equal(1.0, snapshot.HttpSuccessRate, 3);
        Assert.Equal(1.0, snapshot.CompletionRate, 3);
        Assert.True(snapshot.AverageLatencyMs >= 0);
        Assert.Equal(3, snapshot.Recent.Count);
        Assert.Equal(3, snapshot.Outcomes["ok"]);
    }

    [Fact]
    public async Task 上游失败时成功率和完成率都掉下来()
    {
        // ## ⚠ 这个用例被 t28 改过 —— 说明白「验什么」，不是「改什么值」
        //
        // ### 原意（没变）
        //
        // 「上游确实失败时，指标有没有如实记下来」—— 成功率和完成率要**掉**，
        // 且失败的**分类**要看得见（否则「为什么失败」就查不出来）。
        //
        // ### 为什么造不出「失败」了 —— 被能力增强推翻过两次
        //
        // 【原本】网关不重试 → 「第 1 枪脚本回 429」必然造成失败。
        // 【t8】网关会**换号重试** → 池里还有第二个号就一路换到成功。
        //        当时改用**单号池**（没第二个号可换 → 真的失败）。
        // 【t28】网关在**同一个号内先换模型**（内层 ≤ ModelFallbackLimit=4）
        //        → 单号池也不管用了：号 A 的 hy3 被限，但它还有别的模型可用，
        //          内层换个模型就成功 → 客户端拿 200。
        //
        // ### t28 之后：把**该号所有候选模型**都限掉，才会走到失败
        //
        // 内层会遍历 4 个候选模型（当前模型排最前，其余按目录补）。
        // 只要还有一个模型没被限，它就会换过去成功。所以脚本必须让
        // **头 4 枪全部 429** —— 内层 4 个候选全灭，外层又只剩这一个号
        // ⇒ 两层同时耗尽 ⇒ 客户端真的拿到 429。
        //
        // ⚠ 为什么是 4：`ModelFallbackLimit = 4`（GatewayApp.cs:939），
        //   对齐旧版 `sendWithFallback`。**它是可调参数、不是契约** ——
        //   所以下面用「够多枪」的写法（给足 4 枪 429），而不是把 4 当成契约钉死。
        var fake = new FakeUpstreamHandler(_ => ChatOk());
        fake.Script(
            UpstreamResponses.TooManyRequests(),
            UpstreamResponses.TooManyRequests(),
            UpstreamResponses.TooManyRequests(),
            UpstreamResponses.TooManyRequests(),
            ChatOk());

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a"));

        using var failed = await PostAsync(fx.Client, Body("会失败的那次"));
        Assert.Equal(HttpStatusCode.TooManyRequests, failed.StatusCode);
        await failed.Content.ReadAsStringAsync();

        // ★ 内层确实把 4 个模型都试过了（这是「内层在工作」的证据）
        //
        // 上下界写法（不是钉死 4）：
        //   · 下界 > 1：至少换过一次模型，否则说明内层没跑
        //   · 上界 ≤ 4：不会无限试（4 = ModelFallbackLimit，可调参数）
        Assert.True(
            fake.Count is > 1 and <= 4,
            $"内层换模型的枪数应在 (1, 4] 之间（4 = ModelFallbackLimit，可调参数），实际 {fake.Count}");

        // 这 4 枪都是**同一个号**（证明是内层换模型，不是外层换号 —— 池里也只有它）
        //
        // ⚠ 这里不能用 `Assert.Single(collection, message)`：那个重载的第二参数
        //   是**谓词**（Func<T,bool>），不是消息 —— 传字符串会被当成
        //   「找一个等于该字符串的元素」，于是永远匹配不到、报
        //   "did not contain any matching items"。用 `Assert.Equal(1, ...)` 更直白。
        Assert.Equal(
            1,
            fake.AuthorizationHeaders.Distinct(StringComparer.Ordinal).Count());

        // ★ 而且 4 枪用的是**不同的模型** —— 这正是内层换模型的定义
        var models = fake.RequestedModels.Distinct(StringComparer.Ordinal).ToList();
        Assert.True(
            models.Count > 1,
            $"内层应该换过模型（否则就是把同一个模型重试 4 次），实际只用了：{string.Join(",", models)}");

        // 清掉刚刚那次限流的冷却 —— 模拟「上游缓过来了」。
        // 不用 Sleep：直接改状态，测试独立可重复（用户的测试方法论要求）。
        //
        // ⚠ 必须用 `Unfreeze` 而不是 `Replace`：`Replace` 的设计是
        // **刻意保留冷却**（AccountPool.cs:110 的注释写明了理由 ——
        // 刷新账号列表不能把冷却清掉，否则被限的号立刻又被选中）。
        var pool = fx.Host.Application.Services.GetRequiredService<AccountPool>();
        pool.Unfreeze(pool.Members.Single().Id);

        // 换一个会话键 —— 免得粘性又把它送回被限流的那个号上
        using var ok = await PostAsync(fx.Client, Body("会成功的那次"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        await ok.Content.ReadAsStringAsync();

        var snapshot = fx.Metrics.Snapshot();

        // ## ★★ 口径已按 t16 改造完成 —— 这里断言的是**新**口径（客户端请求） ★★
        //
        // 【改之前（t8~t15）】指标按「上游尝试」记，本用例是 3 条 = 2 failed + 1 ok。
        // 【改之后（t16）】指标按「客户端请求」记，本用例是 2 条 = 1 failed + 1 ok。
        //
        // 本用例的实际过程（单号池，两次客户端请求）：
        //   请求 1（"会失败的那次"）：
        //     · 内层尝试 1..4：号 A 的 4 个候选模型**逐个**撞 429
        //       （每枪都只冷「该号 + 该模型」，所以内层能继续换下一个）
        //     · 内层耗尽 → 外层换号 → 池里只有这一个号，没有第二个可换
        //     ⇒ 两层同时耗尽 → 客户端拿到 429（**终局**，IsTerminal=true）
        //   请求 2（"会成功的那次"，已 Unfreeze）：
        //     · 尝试 1：成功 → 记 1 条 ok（终局）
        //     ⇒ 1 条终局
        //
        //   ⇒ 主口径 TotalRequests = 2（1 failed + 1 ok），成功率 1/2
        //
        // ⚠ 为什么这样才对：按尝试算会让**重试成功**这种事拉低成功率
        //   （重试越多掉得越狠），而重试恰恰是系统在正常工作。
        //   这个指标要回答的是「用户拿到好结果的概率」，不是「上游被打了几次」。
        //   上游被打了几次由 `AttemptCount` 回答（下面单独断言）。
        //
        // ⚠ 断言值变了，但**没有放宽容差、没有删断言** —— 计数口径变了，
        //   期望值本来就该跟着变（3 → 2）。语义变更的原因就在上面这几行。
        Assert.Equal(2, snapshot.TotalRequests);

        // 2 条终局里 1 条 2xx → 1/2
        Assert.Equal(1.0 / 2.0, snapshot.HttpSuccessRate, 3);
        // 完成成功率同为 1/2
        Assert.Equal(1.0 / 2.0, snapshot.CompletionRate, 3);

        // ★ 诊断口径：上游总共被打了几次。
        //
        //   请求 1（失败那次）：内层把 4 个候选模型逐个试完 ⇒ 打了 4 枪
        //   请求 2（成功那次）：第 1 枪就成功 ⇒ 打了 1 枪
        //   ⇒ AttemptCount = 5
        //
        //   ⚠ **上下界写法，不钉死 4 或 5**：这个数由两个可调参数决定 ——
        //     内层 `ModelFallbackLimit`（=4，GatewayApp.cs:939）和外层
        //     `MaxUpstreamAttempts`（=3，GatewayOptions，命令行可配）。
        //     它们都不是契约，钉死会让下次调参又得改这里。
        //
        //   契约本身是：
        //     · 下界 > TotalRequests —— 重试真的发生过（否则这个诊断口径没意义）
        //     · 上界 ≤ 4 + 1 —— 请求 1 最多 4 枪（内层上限）、请求 2 是 1 枪
        Assert.Equal(5, snapshot.AttemptCount);
        Assert.True(
            snapshot.AttemptCount > snapshot.TotalRequests,
            $"重试后尝试数({snapshot.AttemptCount}) 应大于请求数({snapshot.TotalRequests})，否则重试没被记下来");
        Assert.True(
            snapshot.AttemptCount <= 4 + 1,
            $"尝试数({snapshot.AttemptCount}) 超出「请求1≤4枪 + 请求2=1枪」的上界（4 是 ModelFallbackLimit，可调参数）");

        // 上游错误分类要能看见 —— 否则「为什么失败」就查不出来了。
        // ⚠ 这个字典**两条口径都收**（含被重试掩盖掉的那次失败尝试），
        //   因为「池子里有号在恶化」正是需要被看见的信号。
        Assert.True(
            snapshot.ErrorKinds.ContainsKey(UpstreamErrorKind.RateLimited)
            || snapshot.ErrorKinds.ContainsKey(UpstreamErrorKind.ModelRateLimited),
            $"错误分类里应该有 rate_limit，实际：{string.Join(",", snapshot.ErrorKinds.Keys)}");
    }

    [Fact]
    public async Task 指标端点回JSON且键名对齐界面()
    {
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), Vault("a"));

        using var response = await fx.Client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var text = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(text)!.AsObject();

        // 这些键名必须和 BuddyDeck.App 的 GatewayMetrics DTO 一模一样
        // （见 src/BuddyDeck.App/Services/IGatewayClient.cs）——
        // 键名错了界面会静默显示 0，而且不报任何错，极难发现。
        foreach (var key in (string[])
                 ["totalRequests", "httpSuccessRate", "completionRate", "averageLatencyMs", "recent"])
        {
            Assert.True(json.ContainsKey(key), $"/metrics 缺少界面要读的键：{key}");
        }

        Assert.True(json["recent"] is JsonArray);
    }

    [Fact]
    public async Task 最近请求是环形缓冲且N可配()
    {
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), Vault("a", "b", "c"), recentCapacity: 3);

        for (var i = 0; i < 7; i++)
        {
            using var response = await PostAsync(fx.Client, Body($"问题 {i}"));
            await response.Content.ReadAsStringAsync();
        }

        var snapshot = fx.Metrics.Snapshot();

        // 总数是 7，但「最近」只留 3 条 —— 环形缓冲的意义就在这
        Assert.Equal(7, snapshot.TotalRequests);
        Assert.Equal(3, snapshot.Recent.Count);
        Assert.Equal(3, fx.Metrics.RecentCapacity);
        Assert.False(string.IsNullOrEmpty(snapshot.Recent[0].RequestId));
    }

    [Fact]
    public async Task 并发写入下读快照不抛异常且计数不丢()
    {
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), Vault("a", "b", "c"));

        // ★ 本测试要 16 个并发请求**都到达**指标层（验证并发下计数不丢）。
        // 默认账号池每号最多 2 在途（MaxPerAccount=2，策略 Reject），
        // 3 个号只能放行 6 个，其余 10 个会被拒在选号阶段 ——
        // 那会让 TotalRequests 断言不到 16。所以把上限提上来。
        var pool = fx.Host.Application.Services.GetRequiredService<AccountPool>();
        pool.Configure(new InFlightOptions { MaxPerAccount = 16 });

        using var stop = new CancellationTokenSource();

        // 一个线程不断读快照
        var reader = Task.Run(async () =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                var snapshot = fx.Metrics.Snapshot();

                // 快照必须自洽：不能读到 null 槽位、NaN、负计数
                Assert.True(snapshot.TotalRequests >= 0);
                Assert.False(double.IsNaN(snapshot.HttpSuccessRate));
                Assert.False(double.IsNaN(snapshot.AverageLatencyMs));
                Assert.False(double.IsNaN(snapshot.P95LatencyMs));
                foreach (var record in snapshot.Recent)
                {
                    Assert.NotNull(record);
                    Assert.False(string.IsNullOrEmpty(record.RequestId));
                }

                reads++;
                await Task.Yield();
            }
            return reads;
        });

        // 16 个并发请求
        var writers = Enumerable.Range(0, 16).Select(async i =>
        {
            using var response = await PostAsync(fx.Client, Body($"并发问题 {i}"));
            await response.Content.ReadAsStringAsync();
        });

        await Task.WhenAll(writers);
        await stop.CancelAsync();
        var readCount = await reader;

        Assert.True(readCount > 0, "读侧一次都没跑起来，这个测试没验到东西");
        Assert.Equal(16, fx.Metrics.Snapshot().TotalRequests);
        Assert.Equal(16, fx.Metrics.Snapshot().Outcomes["ok"]);
    }

    [Fact]
    public async Task 没有账号时的失败也计入指标()
    {
        // 空保险库
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), new TestVault());

        using var response = await PostAsync(fx.Client, Body("你好"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        var snapshot = fx.Metrics.Snapshot();

        // 「没登录」是最常见的失败之一，指标里必须看得见
        Assert.Equal(1, snapshot.TotalRequests);
        Assert.Equal(0.0, snapshot.HttpSuccessRate, 3);
        Assert.Equal(1, snapshot.Outcomes["failed"]);
    }

    [Fact]
    public void 比例的分母为零时不是NaN()
    {
        // NaN 会让界面显示 "NaN%"，而且和任何数比较都是 false
        var metrics = new RequestMetrics(TimeProvider.System);
        var snapshot = metrics.Snapshot();

        Assert.Equal(0, snapshot.TotalRequests);
        Assert.Equal(0.0, snapshot.HttpSuccessRate);
        Assert.Equal(0.0, snapshot.CompletionRate);
        Assert.Equal(0.0, snapshot.AverageLatencyMs);
        Assert.False(double.IsNaN(snapshot.P95LatencyMs));
    }

    [Fact]
    public void 环形缓冲回绕后顺序仍然正确()
    {
        var metrics = new RequestMetrics(TimeProvider.System, new MetricsOptions { RecentCapacity = 3 });
        var baseTime = DateTimeOffset.UtcNow;

        for (var i = 0; i < 10; i++)
        {
            metrics.Record(new RequestRecord
            {
                At = baseTime.AddSeconds(i),
                RequestId = $"r{i}",
                Status = 200,
                DurationMs = i,
                Outcome = "ok",
            });
        }

        var recent = metrics.Snapshot().Recent;

        // 回绕之后最近的三条应该是 r9 / r8 / r7，而且新的在前
        Assert.Equal(3, recent.Count);
        Assert.Equal("r9", recent[0].RequestId);
        Assert.Equal("r8", recent[1].RequestId);
        Assert.Equal("r7", recent[2].RequestId);
    }

    [Fact]
    public void 平均耗时和P95算得对()
    {
        var metrics = new RequestMetrics(TimeProvider.System, new MetricsOptions { RecentCapacity = 100 });

        foreach (var ms in (long[])[10, 20, 30, 40, 100])
        {
            metrics.Record(new RequestRecord
            {
                At = DateTimeOffset.UtcNow,
                RequestId = $"r{ms}",
                Status = 200,
                DurationMs = ms,
                Outcome = "ok",
            });
        }

        var snapshot = metrics.Snapshot();

        Assert.Equal(40, snapshot.AverageLatencyMs, 3);   // (10+20+30+40+100)/5
        Assert.Equal(100, snapshot.MaxLatencyMs);
        Assert.InRange(snapshot.P95LatencyMs, 40, 100);
    }

    // ══════════════════════════════════════════════════════════
    // X-Request-Id
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 响应带XRequestId且是生成的()
    {
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), Vault("a"));

        using var response = await PostAsync(fx.Client, Body("你好"));

        Assert.True(response.Headers.TryGetValues("X-Request-Id", out var values));
        var id = values.First();

        Assert.False(string.IsNullOrWhiteSpace(id));
        // 生成的是 32 位无连字符 GUID
        Assert.Equal(32, id.Length);
        Assert.True(Guid.TryParseExact(id, "N", out _), $"id 不是 N 格式的 GUID：{id}");

        await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task 客户端给了请求id就沿用()
    {
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), Vault("a"));

        const string mine = "trace-abc-123";
        using var response = await PostAsync(fx.Client, Body("你好"), requestId: mine);

        Assert.True(response.Headers.TryGetValues("X-Request-Id", out var values));
        // 必须**原样沿用**：调用方日志里记的是它发的那个 id，
        // 我们换成自己生成的，两边就关联不上了 —— 那正是请求 id 的意义
        Assert.Equal(mine, values.First());

        await response.Content.ReadAsStringAsync();

        // 指标里的记录也要用同一个 id
        Assert.Equal(mine, fx.Metrics.Snapshot().Recent[0].RequestId);
    }

    [Fact]
    public async Task 健康检查也带请求id()
    {
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), Vault("a"));

        using var response = await fx.Client.GetAsync("/healthz");
        Assert.True(response.Headers.Contains("X-Request-Id"));
    }

    [Fact]
    public async Task 鉴权失败也带请求id()
    {
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), Vault("a"), apiKey: "right-key");

        using var response = await PostAsync(fx.Client, Body("你好"), apiKey: "wrong-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // 被拒的请求也要能追溯 —— 用户报「我 key 没错啊」时靠这个找日志
        Assert.True(response.Headers.Contains("X-Request-Id"));
    }

    [Fact]
    public async Task 超长请求id被截断而不是拒绝()
    {
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), Vault("a"));

        var huge = new string('x', 500);
        using var response = await PostAsync(fx.Client, Body("你好"), requestId: huge);

        // 请求照样成功；id 被截到上限（不让一个头把日志撑爆）
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Request-Id", out var values));
        Assert.Equal(128, values.First().Length);

        await response.Content.ReadAsStringAsync();
    }

    // ══════════════════════════════════════════════════════════
    // ③ 脱敏日志 —— 硬要求
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 日志里不含提示词标记文本()
    {
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), Vault("a"));

        using var response = await PostAsync(fx.Client, Body(PromptMarker));
        await response.Content.ReadAsStringAsync();

        var all = fx.Log.Entries;
        Assert.NotEmpty(all);

        foreach (var entry in all)
        {
            var line = entry.ToLine();

            // ① 提示词一个字都不能有
            Assert.DoesNotContain(PromptMarker, line, StringComparison.Ordinal);
            // ② 假 token 原值也不能有
            Assert.DoesNotContain(SecretToken, line, StringComparison.Ordinal);
            Assert.DoesNotContain("refresh-secret", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 日志里不含accessToken原值()
    {
        var vault = Vault("a");
        var realToken = vault.Entries[0].AccessToken;
        Assert.Contains(SecretToken, realToken, StringComparison.Ordinal);

        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), vault);

        using var response = await PostAsync(fx.Client, Body("普通提问"));
        await response.Content.ReadAsStringAsync();

        // 逐字段遍历 —— 比整体字符串匹配更能定位是哪个字段漏了
        foreach (var entry in fx.Log.Entries)
        {
            var json = entry.ToJson();
            foreach (var (_, value) in json)
            {
                var text = value?.ToJsonString() ?? "";
                Assert.DoesNotContain(realToken, text, StringComparison.Ordinal);
                Assert.DoesNotContain(SecretToken, text, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(realToken, entry.ToLine(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 日志里有该有的元数据()
    {
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), Vault("a"));

        using var response = await PostAsync(fx.Client, Body("你好"), requestId: "req-1");
        await response.Content.ReadAsStringAsync();

        var entry = Assert.Single(fx.Log.Entries);

        Assert.Equal("req-1", entry.RequestId);
        Assert.Equal("hy3", entry.UsedModel);
        Assert.Equal(200, entry.Status);
        Assert.Equal("ok", entry.Outcome);
        Assert.True(entry.Streamed);
        Assert.True(entry.DurationMs >= 0);
        Assert.False(string.IsNullOrEmpty(entry.AccountTag));
        Assert.False(string.IsNullOrEmpty(entry.At));

        // 账号标签是**短标签**，不是完整 uid、更不是 token
        Assert.DoesNotContain("accessToken", entry.AccountTag, StringComparison.OrdinalIgnoreCase);
        Assert.True(entry.AccountTag.Length <= 40, $"账号标签太长了：{entry.AccountTag}");
    }

    [Fact]
    public async Task 上游错误正文不整段进日志()
    {
        // 上游错误正文里**回显了提示词** —— 这是真实会发生的
        //（「内容不合规：请勿提问 XXX」）。必须一个字都不进日志。
        var errorBody = new JsonObject
        {
            ["code"] = 400,
            ["displayMsg"] = $"内容不合规：请勿提问 {PromptMarker}",
            ["msg"] = PromptMarker,
        }.ToJsonString(Json.Relaxed);

        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text(errorBody, HttpStatusCode.BadRequest));

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a"));

        using var response = await PostAsync(fx.Client, Body(PromptMarker));
        await response.Content.ReadAsStringAsync();

        foreach (var entry in fx.Log.Entries)
        {
            Assert.DoesNotContain(PromptMarker, entry.ToLine(), StringComparison.Ordinal);
            Assert.DoesNotContain(PromptMarker, entry.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 错误分类把正文压成一个分类词()
    {
        // 输入里带业务码和可能含用户内容的正文；输出只该是分类词
        Assert.Equal(
            UpstreamErrorKind.ModelRateLimited,
            RequestLog.RedactError("""{"displayMsg":"内容不合规：请勿提问 机密内容","code":6004}"""));

        Assert.Equal(
            UpstreamErrorKind.RateLimited,
            RequestLog.RedactError("""{"code":6005,"msg":"slow down"}"""));

        Assert.Equal(UpstreamErrorKind.Unknown, RequestLog.RedactError("随便一段话"));
        Assert.Equal(UpstreamErrorKind.Unknown, RequestLog.RedactError(null));
        Assert.Equal(UpstreamErrorKind.Unknown, RequestLog.RedactError(""));
    }

    [Fact]
    public void 清洗会丢掉疑似凭据的串()
    {
        // 长且没有空格的连续串 = token 的典型形状 → 整体丢掉
        Assert.Equal("[已脱敏]", RequestLog.Sanitize("sk-0123456789abcdef0123456789abcdef"));

        // JWT 三段式
        Assert.Equal("[已脱敏]", RequestLog.Sanitize("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abcdefghij"));

        // 换行会被压掉（日志按行读，一行就是一条）
        Assert.DoesNotContain('\n', RequestLog.Sanitize("第一行\n第二行"));

        // 普通的短文本原样保留
        Assert.Equal("连接被拒绝", RequestLog.Sanitize("连接被拒绝"));

        // 超长文本被截断
        Assert.True(RequestLog.Sanitize(new string('a', 500)).Length <= 96);
    }

    [Fact]
    public void 日志缓冲是定长的且回绕后顺序正确()
    {
        var buffer = new LogBuffer(3);

        for (var i = 0; i < 7; i++)
        {
            buffer.Add(new LogEntry
            {
                At = $"t{i}",
                Level = "Information",
                RequestId = $"r{i}",
            });
        }

        var all = buffer.Snapshot();

        Assert.Equal(3, all.Count);
        Assert.Equal("r4", all[0].RequestId);
        Assert.Equal("r5", all[1].RequestId);
        Assert.Equal("r6", all[2].RequestId);
        Assert.Equal(3, buffer.Capacity);
    }

    [Fact]
    public void 取消的请求记成Information而不是错误()
    {
        var log = new RequestLog(TimeProvider.System);

        var entry = log.Record(new RequestRecord
        {
            At = DateTimeOffset.UtcNow,
            RequestId = "r1",
            Status = 499,
            DurationMs = 5,
            Outcome = "cancelled",
        });

        // 用户按「停止生成」是正常操作，不该在日志里变成一片警告
        Assert.Equal("Information", entry.Level);
        Assert.True(entry.Aborted);
    }

    [Fact]
    public void 服务端错误记成Error()
    {
        var log = new RequestLog(TimeProvider.System);

        var entry = log.Record(new RequestRecord
        {
            At = DateTimeOffset.UtcNow,
            RequestId = "r1",
            Status = 502,
            DurationMs = 5,
            Outcome = "failed",
            ErrorKind = UpstreamErrorKind.Server,
        });

        Assert.Equal("Error", entry.Level);
    }

    // ══════════════════════════════════════════════════════════
    // ① 会话粘性 —— 在真网关上验
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 同一个会话两次请求打到同一个账号()
    {
        var fake = FakeOk();
        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b", "c"));

        using (var first = await PostAsync(fx.Client, Body("同一个会话")))
        {
            await first.Content.ReadAsStringAsync();
        }

        // 第二轮：带上上一轮的回答和新的问题
        var secondTurn = new JsonObject
        {
            ["model"] = "hy3",
            ["stream"] = true,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "你是助手" },
                new JsonObject { ["role"] = "user", ["content"] = "同一个会话" },
                new JsonObject { ["role"] = "assistant", ["content"] = "你好" },
                new JsonObject { ["role"] = "user", ["content"] = "接着说" },
            },
        };

        using (var second = await PostAsync(fx.Client, secondTurn))
        {
            await second.Content.ReadAsStringAsync();
        }

        var tokens = fake.AuthorizationHeaders;

        Assert.Equal(2, tokens.Count);
        // 这是粘性的核心断言：两轮必须用**同一个账号的 token**
        Assert.Equal(tokens[0], tokens[1]);
    }

    [Fact]
    public async Task 穿插不同会话不串号()
    {
        var fake = FakeOk();
        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b", "c"));

        // 会话一第一轮
        using (var r = await PostAsync(fx.Client, Body("会话甲"))) await r.Content.ReadAsStringAsync();
        var tokenA = fake.Last.HeaderOrNull("Authorization");

        // 会话二第一轮（应该拿到**另一个**号）
        using (var r = await PostAsync(fx.Client, Body("会话乙"))) await r.Content.ReadAsStringAsync();
        var tokenB = fake.Last.HeaderOrNull("Authorization");

        Assert.NotEqual(tokenA, tokenB);

        // 会话一第二轮 —— 必须回到 A
        using (var r = await PostAsync(fx.Client, Body("会话甲"))) await r.Content.ReadAsStringAsync();
        Assert.Equal(tokenA, fake.Last.HeaderOrNull("Authorization"));

        // 会话二第二轮 —— 必须回到 B
        using (var r = await PostAsync(fx.Client, Body("会话乙"))) await r.Content.ReadAsStringAsync();
        Assert.Equal(tokenB, fake.Last.HeaderOrNull("Authorization"));
    }

    [Fact]
    public async Task 账号被限流后会话换号且不抛异常()
    {
        // ## ⚠ t8 → t28 的语义变更（必读，免得以为断言被削弱了）
        //
        // 本用例要证明的是「会话粘着的号被限流之后，会换到别的号，且不抛异常」。
        //
        // t8 之前：网关不重试，被限的那次请求**直接失败**（客户端拿 429），
        //          换号发生在**下一次**客户端请求。
        //         原断言：第 2 次 429；第 3 次换到别的号成功。
        //
        // t8 之后：换号发生在**同一个客户端请求内** → 第 2 次当场换号成功（200）。
        //         断言改成「第 2 次就换号成功」。
        //
        // t28 之后：**换号之前多了一层「同号内换模型」**（内层 ≤ ModelFallbackLimit=4）。
        //         ⇒ 只冷 `hy3` **走不到换号**：内层换个模型就成功了，
        //           号根本没换（实测：两枪 Authorization 相同）。
        //
        // ## 所以脚本必须让「该号**所有候选模型**都被限」
        //
        // 这正是 t28 生效后「换号」的**新触发条件**：外层换号只在
        // 「这个号一个可用模型都不剩」时才轮到。用 `TooManyRequests`
        // （**账号级**限流）表达这件事 —— 它语义就是「整号不能用」，
        // 而 `ModelRateLimited`（模型级）恰好是被内层救回来的那种。
        //
        // ⚠ 脚本为什么要给 4 枪「首号全限」：内层要逐个试完 4 个候选模型
        //   才会放弃这个号（4 = ModelFallbackLimit，可调参数，不是契约）。
        //   给少了的后果是内层还没耗尽就换到成功那一步 —— 断言会变得不稳定。
        //   `UpstreamScript` 超出长度时重复最后一步，所以这里给足 4 枪。
        var fake = new FakeUpstreamHandler(_ => ChatOk());
        fake.Script(
            ChatOk(),                                        // 第 1 次：占住一个号（会话粘上去）
            UpstreamResponses.TooManyRequests(),             // 第 2 次：这个号的第 1 个模型被限
            UpstreamResponses.TooManyRequests(),             // 第 3 次：内层换模型，又被限
            UpstreamResponses.TooManyRequests(),             // 第 4 次：再换，又被限
            UpstreamResponses.TooManyRequests(),             // 第 5 次：第 4 个模型也被限 → 内层耗尽
            ChatOk("换号成功"));                              // 第 6 次：外层换号 → 成功

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b", "c"));

        using (var first = await PostAsync(fx.Client, Body("同一个会话")))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            await first.Content.ReadAsStringAsync();
        }
        var firstToken = fake.Last.HeaderOrNull("Authorization");

        // 第 2 次：粘着的号**所有模型**被限 → 内层耗尽 → **外层换号** → 客户端拿 200
        using (var second = await PostAsync(fx.Client, Body("同一个会话")))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            await second.Content.ReadAsStringAsync();
        }

        // ★★ 核心不变量：**换号分支真的被执行了** —— 最后一枪用的是别的号
        //
        // 这是 t37 要保住的性质：粘性遇到「这个号彻底不能用」时必须让路。
        // 断言落在「换号发生了」而不是「没抛异常」——后者是弱断言。
        Assert.NotEqual(firstToken, fake.Last.HeaderOrNull("Authorization"));

        // ★ 那次 429 仍然如实进了指标（这是要保留的证据，不是要抹掉的东西）
        //
        // 【口径已按 t16 改造完成 —— 本段是「改之后」的事实】
        //
        // t8 给网关加了「单请求内换号重试」，重试过程必须被记下来
        //（否则「试了 3 次才成功」会被统计成「1 次成功」）。
        // t16 把这件事拆成两套口径：
        //
        //   主口径（TotalRequests / 成功率 / 平均耗时）
        //     → 一次客户端请求算一个，内部重试 2 次也还是 1。
        //   诊断口径（AttemptCount）
        //     → 一次上游尝试算一个，重试会把它推高。
        //
        // 本用例里第 2 次请求的尝试序列（t28 两层结构下）：
        //   号 A 内层 4 枪全 429（逐个候选模型）→ 内层耗尽 → 外层换号
        //   号 B 第 1 枪成功                                        ⇒ 合计 5 次尝试
        //
        // ⇒ 所以下面这几条断言：
        //   · `Recent` 里**仍然能看到那次 429**（诊断要看得见重试，不能抹掉）
        //   · 但那条 429 的记录 `IsTerminal == false`（它不进主口径）
        //   · 而成功的终局记录 `Attempts == 5`（界面靠它知道「这是被重试救回来的」）
        //
        // ⚠ 这不是「把断言放宽」—— 是在新的口径下**增加**了更精确的断言
        //   （旧口径下这两条根本无从表达）。
        //
        // ⚠ 为什么是 5 而不是 2：t28 之前只有「外层换号」一层，所以是
        //   「1 次失败 + 1 次成功 = 2」。t28 加了内层换模型，
        //   失败那个号要先被内层试满 4 个候选模型才轮到换号 ⇒ 4 + 1 = 5。
        //   这里写精确值 + 说明；`4` 本身是 ModelFallbackLimit（可调参数），
        //   所以下面另有一条**上下界**断言防止它变成隐式契约。
        var snapshot = fx.Metrics.Snapshot();

        Assert.Contains(snapshot.Recent, r => r.Status == 429);

        Assert.True(
            snapshot.ErrorKinds.ContainsKey(UpstreamErrorKind.ModelRateLimited)
            || snapshot.ErrorKinds.ContainsKey(UpstreamErrorKind.RateLimited),
            $"错误分类里应该有 rate_limit，实际：{string.Join(",", snapshot.ErrorKinds.Keys)}");

        // ★ 诊断口径：那次被重试掩盖的失败仍然是 IsTerminal=false，没进主口径
        var retried = snapshot.Recent.First(r => r.Status == 429);
        Assert.False(retried.Terminal, "中间失败的尝试不该是终局记录");
        Assert.True(retried.Attempts >= 1, "中间尝试也要带自己的序号");

        // ★ 主口径：换号后成功的那条是终局，且 Attempts 反映「真的重试过很多次」
        var recovered = snapshot.Recent.First(r => r.Status == 200 && r.Outcome == "ok");
        Assert.True(recovered.Terminal, "成功的终局记录必须是 IsTerminal");
        Assert.Equal(5, recovered.Attempts);

        // ★ 上下界兜底：不去钉死「4」这个可调参数
        //
        //   契约本身是：内层真的换过模型（下界 > 1 次尝试），
        //   且不会无限试（上界 ≤ 内层上限 4 + 外层再多一个号 1 枪）。
        //   把 4/5 钉死会让下次调 ModelFallbackLimit 时这里又要改 —— 那是
        //   「参数泄漏进测试」的味道。上面那条精确值是为了**可读性**
        //   （让人一眼知道当前实际是 5），这条是为了**抗调参**。
        Assert.True(
            recovered.Attempts > 1,
            $"换号救回来的请求应当记录多次尝试（内层至少换过模型），实际 {recovered.Attempts}");
        Assert.True(
            recovered.Attempts <= 4 + 1,
            $"尝试数({recovered.Attempts}) 超出「内层≤4 + 外层换号1枪」的上界（4 = ModelFallbackLimit，可调参数）");
    }

    [Fact]
    public async Task 没有会话概念时仍然正常选号()
    {
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), Vault("a", "b", "c"));

        // 只有 assistant 消息 —— 派生不出会话键，应该退化到池的轮询，而不是报错
        var body = new JsonObject
        {
            ["model"] = "hy3",
            ["stream"] = true,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "assistant", ["content"] = "嗨" },
            },
        };

        using var response = await PostAsync(fx.Client, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        Assert.Equal(1, fx.Metrics.Snapshot().TotalRequests);
    }

    [Fact]
    public async Task 坏JSON的请求体不会让网关崩()
    {
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), Vault("a"));

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{这不是合法 JSON", System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await fx.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        // 而且网关还活着
        using var health = await fx.Client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task 指标和日志是旁路挂了也不影响响应()
    {
        await using var fx = await ObservabilityFixture.StartAsync(FakeOk(), Vault("a"), throwOnRecord: true);

        using var response = await PostAsync(fx.Client, Body("你好"));

        // 指标故意炸了，用户照样拿到完整回答
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("data:", body, StringComparison.Ordinal);
        Assert.Contains("[DONE]", body, StringComparison.Ordinal);
    }
}

/// <summary>JSON 序列化选项 —— 不转义中文，读起来才像人话。</summary>
internal static class Json
{
    internal static readonly System.Text.Json.JsonSerializerOptions Relaxed = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
