using System.Net;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.Gateway;
using BuddyDeck.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// **指标口径**的专门测试（t16）。
///
/// ## 这个文件要钉住的是什么
///
/// 网关在单次客户端请求内会换号重试（见 <c>GatewayApp.RunWithRetryAsync</c>）。
/// 于是「一次请求」有两个含义，而它们回答的是**两个不同的问题**：
///
/// ```
/// 主口径（用户视角）   TotalRequests / 成功率 / 平均耗时
///                     一次客户端请求算一个 —— 回答「用户拿到好结果的概率」
///
/// 诊断口径（池子视角） AttemptCount
///                     一次上游尝试算一个 —— 回答「账号池健不健康」
/// ```
///
/// **为什么必须分开**：按尝试算成功率，会让「换号重试成功」这种事
/// **拉低**成功率（重试越多掉得越狠）—— 而重试恰恰是系统在正常工作。
/// 那会呈现出「系统帮你换号救回来了，所以成功率下降了」这种荒谬信号。
///
/// 但反过来也不能只看客户端成功率：一个请求试了 3 次才成功，
/// 说明池子在恶化，而客户端成功率完全看不见这个信号。
///
/// 所以这个文件里每个用例都**同时断言两套数**，缺一不可。
/// 只断言一套的测试挡不住「改回单一口径」这种回退。
/// </summary>
[Collection("GatewayHost")]
public class MetricsCaliberTests
{
    // ══════════════════════════════════════════════════════════
    // 公共构造
    // ══════════════════════════════════════════════════════════

    /// <summary>造 N 个账号。</summary>
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
                accessToken: $"token-{id}");
        }
        return vault;
    }

    private static JsonObject Body(string firstUser, bool stream = true, string model = "hy3")
        => new()
        {
            ["model"] = model,
            ["stream"] = stream,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "你是助手" },
                new JsonObject { ["role"] = "user", ["content"] = firstUser },
            },
        };

    /// <summary>一次正常的流式回答。</summary>
    ///
    /// ⚠ 用字符串拼接而**不是** JsonObject.ToJsonString()：后者默认
    /// 会把中文转义成 \uXXXX，于是「响应里应该含中文内容」这类断言
    /// 会失败（实测踩过）。也不用内插原始字符串 —— 那里的大括号
    /// 会触发 CS9007（同一个坑在 TestSupport 里也踩过）。
    private static HttpResponseMessage ChatOk(string delta = "你好")
        => UpstreamResponses.SseText(
            "{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + delta + "\"}}]}",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""");

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject body)
        => await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"))
            .ConfigureAwait(false);

    // ══════════════════════════════════════════════════════════
    // ① 核心场景：一次客户端请求，内部换号重试后才成功
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 换号重试成功后_成功率不下降_但尝试数会上升()
    {
        // ★ 这是 t16 的验收场景，也是整个口径改造的理由所在。
        //
        // 场景：3 个号。客户端发**一次**请求。
        //   尝试 1：号 a 撞限流 → 换号
        //   尝试 2：号 b 成功
        // 用户视角：他发了 1 次请求，拿到了完整回答 → 成功率应该是 100%。
        // 池子视角：上游被打到了 2 次 → 说明池子里有号在恶化。
        var fake = new FakeUpstreamHandler(_ => ChatOk());
        fake.Script(
            UpstreamResponses.TooManyRequests(),   // 尝试 1：号 a 被限
            ChatOk("换号救回来的"));                // 尝试 2：号 b 成功

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b", "c"));

        // ⚠ 用**非流式**：流式响应经过 SseSanitizer 会被重新序列化，
        //   中文变成 \uXXXX 转义（既有行为，见 SseSanitizer.cs:99），
        //   直接 Contains("换号救回来的") 会失败。非流式走
        //   ChatStreamCollector，拼出来的是原生中文，断言干净。
        //   RetryTests 也是这么绕开的（它断 JSON 字段而不是原文）。
        using var response = await PostAsync(fx.Client, Body("同一次请求", stream: false));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(
            "换号救回来的",
            body["choices"]!.AsArray()[0]!["message"]!["content"]!.GetValue<string>());

        var snapshot = fx.Metrics.Snapshot();

        // ── 主口径：**一次**客户端请求，且成功了 ────────────────
        //
        // ⚠ 如果这里不是 1，说明口径退回了「按尝试算」——
        //   那会让重试反而拉低成功率，是本任务要修的核心缺陷。
        Assert.Equal(1, snapshot.TotalRequests);
        Assert.Equal(1.0, snapshot.CompletionRate, 3);
        Assert.Equal(1.0, snapshot.HttpSuccessRate, 3);

        // ── 诊断口径：上游被打到了 2 次 ─────────────────────────
        //
        // ⚠ 如果这里不是 2，说明 AttemptCount 没接上或者被吞了 ——
        //   那「池子在恶化」这个信号就彻底看不见了。
        Assert.Equal(2, snapshot.AttemptCount);

        // 两套数的关系：尝试数 ≥ 请求数，差值 = 因重试多打的上游请求数
        Assert.Equal(1, snapshot.AttemptCount - snapshot.TotalRequests);

        // ── Recent 里两条都看得见（诊断不丢过程）───────────────
        Assert.Equal(2, snapshot.Recent.Count);

        // 中间那次失败：非终局、尝试序号 1
        var intermediate = snapshot.Recent.First(r => r.Status == 429);
        Assert.False(intermediate.Terminal);
        Assert.Equal(1, intermediate.Attempts);

        // 终局那次成功：终局、尝试序号 2（界面靠它显示「重试过」）
        var terminal = snapshot.Recent.First(r => r.Outcome == "ok");
        Assert.True(terminal.Terminal);
        Assert.Equal(2, terminal.Attempts);
    }

    [Fact]
    public async Task 重试次数越多_尝试数与请求数的比值越大_而成功率不掉()
    {
        // 这是「比值才是体检指标」这条结论的直接验证。
        //
        // 场景：3 个号，一次客户端请求，前 2 次都被限、第 3 次成功。
        //
        // ⚠ 上限约束：网关默认 `MaxUpstreamAttempts = 3`（见 GatewayOptions），
        //   所以最多只能试 3 轮。要造更多轮得先起一个配了 --max-attempts 的宿主，
        //   那超出本用例的必要范围 —— 3 轮已经足够证明「比值 > 1 而成功率不掉」。
        var fake = new FakeUpstreamHandler(_ => ChatOk());
        fake.Script(
            UpstreamResponses.TooManyRequests(),
            UpstreamResponses.TooManyRequests(),
            ChatOk("第三次成功"));

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b", "c"));

        using var response = await PostAsync(fx.Client, Body("被限两次", stream: false));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        var snapshot = fx.Metrics.Snapshot();

        // 用户视角：发了 1 次、成功了 → 成功率 100%。
        // **注意它完全没有因为重试了 2 次而下降** —— 这正是改造的目的。
        Assert.Equal(1, snapshot.TotalRequests);
        Assert.Equal(1.0, snapshot.CompletionRate, 3);

        // 池子视角：一共打了 3 次上游。
        Assert.Equal(3, snapshot.AttemptCount);

        // 比值 3.0 —— 界面会拿它显示「请求 1 次 / 上游尝试 3 次」，
        // 运维一眼看出池子有大问题（而成功率还是漂亮的 100%）。
        var ratio = (double)snapshot.AttemptCount / snapshot.TotalRequests;
        Assert.Equal(3.0, ratio, 3);

        // 而如果按旧口径（尝试数当分母）算成功率，就会掉到 1/3 ——
        // 重试越多掉得越狠，正是要修的荒谬信号。这里显式钉住「没有掉」。
        Assert.NotEqual(1.0 / 3.0, snapshot.CompletionRate);
    }

    [Fact]
    public async Task 没有重试时_尝试数等于请求数()
    {
        // 对照组：池子健康时两套口径应该重合。
        // 没有这条，「AttemptCount 恒等于某个错误值」也能让上面两条测试通过。
        await using var fx = await ObservabilityFixture.StartAsync(FakeAllOk(), Vault("a", "b", "c"));

        for (var i = 0; i < 3; i++)
        {
            using var response = await PostAsync(fx.Client, Body($"问题 {i}"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync();
        }

        var snapshot = fx.Metrics.Snapshot();

        Assert.Equal(3, snapshot.TotalRequests);
        Assert.Equal(3, snapshot.AttemptCount);
        Assert.Equal(0, snapshot.AttemptCount - snapshot.TotalRequests);
    }

    private static FakeUpstreamHandler FakeAllOk(string delta = "你好") => new(_ => ChatOk(delta));

    /// <summary>
    /// 内层降级会走的候选模型集（`GatewayApp.BuildModelCandidates` 的形状）。
    ///
    /// ```
    /// 候选 = [当前模型] + 目录里前 (ModelFallbackLimit - 1) 个
    ///      = [hy3, hy4-preview, hy3-x, deepseek-v4.1-flash]   ← 本例
    /// ```
    ///
    /// ⚠ 为什么测试里要复刻这个列表而不是遍历整个目录：
    /// `MarkRateLimited` 每次都会把 `ConsecutiveFailures` +1，而
    /// `FreezeThreshold = 5`。冷 17 个模型会把号直接**冷冻**，那是
    /// 另一条路径（冷冻的号不该被选中，指标里什么都记不到 —— 实测踩到，
    /// 表现为 `Recent` 空、`Single()` 抛 "Sequence contains no elements"）。
    ///
    /// 保持 4 个既够把内层堵死，又远低于冷冻阈值。
    /// </summary>
    private static string[] FallbackCandidateModels()
        => ["hy3", "hy4-preview", "hy3-x", "deepseek-v4.1-flash"];

    // ══════════════════════════════════════════════════════════
    // ② 「没号可发」不算上游尝试
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 号全在冷却里导致没发出请求_不算上游尝试()
    {
        // 场景：单号池，那个号被限流冷却 → 重试时选不出可用的「号+模型」→
        //      「没有可用账号」，**后续这些轮压根没出网**。
        //
        // 它应该：
        //   · 进主口径（用户视角：这次请求失败了）→ TotalRequests +1
        //   · **不**进 AttemptCount 的「没出网」那部分
        //
        // 为什么较真这一点：AttemptCount 的语义是「上游被打了几次」。
        // 把本地短路也算进去，这个体检指标就被污染了 ——
        // 池子越糟、本地拒绝越多，AttemptCount 虚高得越离谱。
        //
        // ## ⚠⚠ t28 之后的语义变更（必读，免得以为断言被放宽了）
        //
        // **这条用例原来只冷却 `hy3` 一个模型**，那时它的行为是：
        //   试 1 次（号 A 的 hy3 被限）→ 下一轮 Pick 返回 null → 结束。
        //
        // t28 给内层加了「账号内先换模型（`ModelFallbackLimit = 4`）」——
        // **上游的限流是「账号 + 模型」维度**（上游原话：「您也可以切换其他模型
        // 继续使用」），所以「号 A 的 hy3 被限」≠「号 A 的 glm-5.3 也被限」。
        // 于是只冷却 hy3 时，内层还能接着试**另外 3 个模型**：
        //
        //   试 4 次（号 A × 4 个候选模型）→ 都拿到 429 → 才真的没得试。
        //
        // **所以「只冷却 hy3」再也造不出「没号可发」了** —— 它现在造出的是
        // 「这个号的所有模型都被限」，那是**另一个场景**（正确的结局是 429 +
        // 4 次真实上游尝试）。
        //
        // ⇒ 要构造真正的「本地短路、一轮都没发出去」，必须把那个号
        //   **对所有候选模型都打进冷却**（下面直接改池状态做到，
        //   不用 Sleep —— 用户的测试方法论要求测试自己设置状态、可重复）。
        var fake = new FakeUpstreamHandler(_ => ChatOk());

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("only"));

        // ── 把候选模型全部冷却，让这一轮**发不出去** ──────────────
        //
        // 为什么不用「让它跑 4 次 429 然后自然耗尽」：那样就**真的发了 4 次**
        // （AttemptCount=4），验不到「没出网」这条路径。这里要的是
        // 「本地就把它挡住了」—— 所以直接改池状态。
        //
        // ⚠ 冷却范围要覆盖内层会走的**候选模型**，不能只冷 hy3 ——
        //   只冷 hy3 的话内层会接着试另外几个模型（见上面的语义变更说明）。
        //
        // ⚠⚠ 但**不能**遍历整个模型目录去冷却：`MarkRateLimited` 每次都会
        //   把 `ConsecutiveFailures` +1，而 `FreezeThreshold = 5` ——
        //   冷 17 个模型会把这个号直接**冷冻**，那是另一条路径
        //   （冷冻的号不该被选中，行为完全不同，而且实测下指标里什么都记不到）。
        //   所以只冷「内层真正会走的候选集」，它由 ModelFallbackLimit 决定。
        var pool = fx.Host.Application.Services.GetRequiredService<AccountPool>();

        // ⚠ `AccountPool` 是**惰性填充**的：`_pool.Replace(entries)` 在每次聊天
        //   请求里才跑（这样用户导入/删号之后不用重启网关）。所以起完宿主、
        //   还没发过请求时，`Members` 是**空的** —— 直接 `Single()` 会抛
        //   "Sequence contains no elements"（实测踩到，报错行还指向后面
        //   那句看起来无关的断言，很容易查错方向）。
        //
        //   这里先手动灌一次账号，再改冷却状态。
        pool.Replace(fx.VaultEntries);

        var member = pool.Members.Single();
        var until = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds();

        // 内层最多换 ModelFallbackLimit 个模型 —— 冷这么多个就足够把它堵死，
        // 而且远低于 FreezeThreshold（不会误触冷冻）。
        foreach (var model in FallbackCandidateModels())
        {
            pool.MarkRateLimited(member.Id, model, until);
        }

        using var response = await PostAsync(fx.Client, Body("号全凉了"));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        var snapshot = fx.Metrics.Snapshot();

        // 主口径：1 次客户端请求，失败了
        Assert.Equal(1, snapshot.TotalRequests);
        Assert.Equal(0.0, snapshot.CompletionRate, 3);

        // ★ 诊断口径：**上游一次都没被打过** —— 全被本地冷却挡住了
        //
        // 这是这条用例的核心：AttemptCount 不该被本地短路污染。
        Assert.Equal(0, snapshot.AttemptCount);

        // 假上游那边也印证：一个请求都没收到
        Assert.Equal(0, fake.Count);

        // 终局那条记录标着「没发出去」
        var terminal = snapshot.Recent.Single(r => r.Terminal);
        Assert.Equal("soft_rate", terminal.ErrorKind);
        Assert.False(terminal.SentToUpstream);

        // 而且**只有**终局这一条（没有中间尝试记录 —— 因为一次都没发出去）
        Assert.Single(snapshot.Recent);
    }

    [Fact]
    public async Task 保险库是空的时候_请求数加一但尝试数为零()
    {
        // 保险库空 → 一个请求都没发出去。上游被打 0 次。
        await using var fx = await ObservabilityFixture.StartAsync(FakeAllOk(), new TestVault());

        using var response = await PostAsync(fx.Client, Body("没账号"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        var snapshot = fx.Metrics.Snapshot();

        Assert.Equal(1, snapshot.TotalRequests);   // 用户视角：他确实发了一次请求，失败了
        Assert.Equal(0, snapshot.AttemptCount);    // 上游视角：一次都没被打过

        // ★ 两类「没号可发」必须能从 ErrorKind 上区分开（见下面那条测试的说明）
        var terminal = snapshot.Recent.Single(r => r.Terminal);
        Assert.Equal("not_signed_in", terminal.ErrorKind);
        Assert.Equal(401, terminal.Status);
        Assert.False(terminal.SentToUpstream);
    }

    [Fact]
    public async Task 没号可发的两种成因_能从指标里区分开()
    {
        // ## 为什么单独立一条
        //
        // gateway 提了一个很好的边界问题：`Pick` 返回 null 有**两种成因**，
        // 健康含义完全不同：
        //
        //   ① 号全在冷却/冷冻  → 池子在恶化（**真正的体检信号**）
        //   ② 保险库压根没账号 → 用户还没导入号（**与池子健康无关**）
        //
        // 如果这两者在指标里不可区分，运维看到 `soft_rate` 就没法判断
        // 「是上游在限我」还是「我配置错了」—— 排查方向完全不同。
        //
        // 实测结论：**它们可区分**，靠的是 `ErrorKind`（两条路径各自赋值）：
        //   · 空保险库走 GatewayApp.cs:441  → ErrorKind="not_signed_in", Status=401
        //   · 号全冷却走 GatewayApp.cs:774  → ErrorKind="soft_rate",       Status=429
        //
        // 这条测试把「可区分」这个不变量钉住 —— 免得将来有人把两条路径
        // 合并成同一个 ErrorKind，静默丢掉这个诊断维度。
        //
        // ⚠ 注意：两者都 `SentToUpstream=false`（都没出网），所以**不能靠它区分**。
        //   要靠 ErrorKind。

        // ── ① 号全在冷却里（**所有候选模型**都凉了）────────────────
        //
        // ⚠ t28 语义变更：只冷却 hy3 的话，内层会接着试另外 3 个模型
        //   （上游限流是「账号 + 模型」维度），那就变成「所有模型都被限」
        //   而不是「本地短路」。要造后者必须把候选模型全部冷却。
        var busyFake = new FakeUpstreamHandler(_ => ChatOk());

        await using var busy = await ObservabilityFixture.StartAsync(busyFake, Vault("only"));

        var pool = busy.Host.Application.Services.GetRequiredService<AccountPool>();
        var until = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds();

        // ⚠ 池子是**惰性填充**的（Replace 在首个聊天请求里才跑），
        //   所以要**先灌账号再改冷却** —— 否则 Members 是空的（实测踩到）。
        pool.Replace(busy.VaultEntries);

        foreach (var model in FallbackCandidateModels())
        {
            pool.MarkRateLimited(pool.Members.Single().Id, model, until);
        }

        using (var response = await PostAsync(busy.Client, Body("号全凉了")))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            await response.Content.ReadAsStringAsync();
        }

        var busyTerminal = busy.Metrics.Snapshot().Recent.Single(r => r.Terminal);

        // ── ② 保险库是空的 ────────────────────────────────────
        await using var empty = await ObservabilityFixture.StartAsync(FakeAllOk(), new TestVault());

        using (var response = await PostAsync(empty.Client, Body("没账号")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            await response.Content.ReadAsStringAsync();
        }

        var emptyTerminal = empty.Metrics.Snapshot().Recent.Single(r => r.Terminal);

        // ★ 核心断言：两者的 ErrorKind 不同 —— 运维能分开排查
        Assert.Equal("soft_rate", busyTerminal.ErrorKind);
        Assert.Equal("not_signed_in", emptyTerminal.ErrorKind);
        Assert.NotEqual(busyTerminal.ErrorKind, emptyTerminal.ErrorKind);

        // ★ 状态码也不同（429 = 配额问题，401 = 没登录）
        Assert.Equal(429, busyTerminal.Status);
        Assert.Equal(401, emptyTerminal.Status);

        // ⚠ 它们**都**没出网 —— 所以 SentToUpstream 区分不了这两者，
        //   别用它做这个判断（用 ErrorKind）。
        Assert.False(busyTerminal.SentToUpstream);
        Assert.False(emptyTerminal.SentToUpstream);

        // 两者的 AttemptCount 也不同：
        //   · 「号全凉」那个：**一次都没打过**（全被本地冷却挡住）→ 0
        //   · 空保险库那个：同样一次都没打过 → 0
        // 注意这里**不**能靠 AttemptCount 区分两者（都是 0）——
        // 区分靠的是上面那个 ErrorKind。
        Assert.Equal(0, busy.Metrics.Snapshot().AttemptCount);
        Assert.Equal(0, empty.Metrics.Snapshot().AttemptCount);
    }

    // ══════════════════════════════════════════════════════════
    // ③ 耗时口径：用户实际等待时长（含重试）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 终局记录的耗时是用户实际等待时长_不是单次上游往返()
    {
        // 场景：先失败 2 次、第 3 次成功。终局记录的 DurationMs 应该是
        // **从请求开始到最终成功**的累计，而不是最后一次尝试那一下。
        var fake = new FakeUpstreamHandler(_ => ChatOk());
        fake.Script(
            UpstreamResponses.TooManyRequests(),
            UpstreamResponses.TooManyRequests(),
            ChatOk("终于好了"));

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b", "c"));

        using var response = await PostAsync(fx.Client, Body("试三次"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        var snapshot = fx.Metrics.Snapshot();

        var terminal = snapshot.Recent.First(r => r.Terminal);
        var intermediate = snapshot.Recent.Where(r => !r.Terminal).ToArray();

        Assert.Equal(2, intermediate.Length);

        // 单调性：终局的耗时 ≥ 任何一次中间尝试的耗时。
        //
        // 因为终局记录记的是「从 startedAt 到最终成功」，
        // 而中间记录记的是「从 startedAt 到那次失败」—— 后者一定更早。
        Assert.All(intermediate, r => Assert.True(
            terminal.DurationMs >= r.DurationMs,
            $"终局耗时 {terminal.DurationMs}ms 应 ≥ 中间尝试耗时 {r.DurationMs}ms（终局含全部重试）"));

        // 平均耗时按客户端请求算 —— 只有 1 次请求，所以平均值就是那次的耗时
        Assert.Equal((double)terminal.DurationMs, snapshot.AverageLatencyMs, 3);
    }

    [Fact]
    public async Task 平均耗时只统计终局记录()
    {
        // 如果中间失败的尝试也被算进平均耗时，分母是 3（1 请求的 3 次尝试）、
        // 分子会被那两次短促的失败拉低 —— 平均值就不再是「用户等了多久」了。
        var fake = new FakeUpstreamHandler(_ => ChatOk());
        fake.Script(
            UpstreamResponses.TooManyRequests(),
            UpstreamResponses.TooManyRequests(),
            ChatOk("好了"));

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b", "c"));

        using var response = await PostAsync(fx.Client, Body("试三次"));
        await response.Content.ReadAsStringAsync();

        var snapshot = fx.Metrics.Snapshot();
        var terminal = snapshot.Recent.First(r => r.Terminal);

        // 断言的是等式：平均值 == 唯一那条终局记录的耗时。
        // 若把中间尝试也算进去，这个等式就不成立了（分母变 3）。
        Assert.Equal((double)terminal.DurationMs, snapshot.AverageLatencyMs, 3);

        // 而且总请求数是 1（不是 3）—— 两条断言互相印证
        Assert.Equal(1, snapshot.TotalRequests);
    }

    // ══════════════════════════════════════════════════════════
    // ④ 终局唯一性：一次客户端请求恰好一条终局记录
    // ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]   // 第一次就成功
    [InlineData(1)]   // 换一次号
    [InlineData(2)]   // 换两次号
    public async Task 一次客户端请求恰好产生一条终局记录(int failuresBeforeSuccess)
    {
        var fake = new FakeUpstreamHandler(_ => ChatOk());
        var steps = new List<HttpResponseMessage>();
        for (var i = 0; i < failuresBeforeSuccess; i++) steps.Add(UpstreamResponses.TooManyRequests());
        steps.Add(ChatOk("成功"));
        fake.Script([.. steps]);

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b", "c", "d"));

        using var response = await PostAsync(fx.Client, Body($"失败 {failuresBeforeSuccess} 次后成功"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        var snapshot = fx.Metrics.Snapshot();

        // ★ 不变量：无论内部试了几次，终局记录**恰好一条**
        Assert.Single(snapshot.Recent, r => r.Terminal);
        Assert.Equal(1, snapshot.TotalRequests);

        // ★ 不变量：尝试数 = 失败次数 + 1（每次失败都真的打过上游）
        Assert.Equal(failuresBeforeSuccess + 1, snapshot.AttemptCount);

        // ★ 不变量：Recent 的条数 = 尝试数（过程完整保留，一条不丢）
        Assert.Equal(snapshot.AttemptCount, snapshot.Recent.Count);

        // ★ 终局记录的 Attempts 标出「这次试了几轮」
        Assert.Equal(failuresBeforeSuccess + 1, snapshot.Recent.First(r => r.Terminal).Attempts);
    }

    [Fact]
    public async Task 不可重试的失败_终局记录只有一条且尝试数为_1()
    {
        // 401 不重试（换号也是同一个失效凭证的兄弟）——
        // 所以「1 条终局 + 1 次尝试」，且 Attempts == 1。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Unauthorized());
        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b"));

        using var response = await PostAsync(fx.Client, Body("凭证坏了"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        var snapshot = fx.Metrics.Snapshot();

        Assert.Equal(1, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.AttemptCount);
        Assert.Single(snapshot.Recent, r => r.Terminal);
        Assert.Equal(1, snapshot.Recent.First(r => r.Terminal).Attempts);

        // 只打了一次上游 —— 401 不该触发重试
        Assert.Equal(1, fake.Count);
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ JSON 出口：界面读的键名
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task _metrics_JSON_同时暴露两套口径()
    {
        var fake = new FakeUpstreamHandler(_ => ChatOk());
        fake.Script(UpstreamResponses.TooManyRequests(), ChatOk("救回来"));

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b"));

        using (var response = await PostAsync(fx.Client, Body("一次请求")))
        {
            await response.Content.ReadAsStringAsync();
        }

        var json = await fx.MetricsJsonAsync();

        // 主口径键名（界面 GatewayMetrics DTO 读的就是这些）
        Assert.Equal(1, json["totalRequests"]!.GetValue<long>());

        // 诊断口径键名 —— 界面「请求 N 次 / 上游尝试 M 次」读这个
        Assert.NotNull(json["attemptCount"]);
        Assert.Equal(2, json["attemptCount"]!.GetValue<long>());

        // Recent 里每条记录都带 attempts / terminal，界面可以自己渲染「重试过」
        var recent = json["recent"]!.AsArray();
        Assert.NotEmpty(recent);
        Assert.All(recent, r =>
        {
            Assert.NotNull(r!["attempts"]);
            Assert.NotNull(r!["terminal"]);
        });

        // 终局那条标 attempts=2
        var terminal = recent.First(r => r!["terminal"]!.GetValue<bool>());
        Assert.Equal(2, terminal!["attempts"]!.GetValue<int>());
    }

    [Fact]
    public async Task _reset_把两套口径一起清零()
    {
        var fake = new FakeUpstreamHandler(_ => ChatOk());
        fake.Script(UpstreamResponses.TooManyRequests(), ChatOk("好了"));

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b"));

        using (var response = await PostAsync(fx.Client, Body("一次")))
        {
            await response.Content.ReadAsStringAsync();
        }

        Assert.Equal(1, fx.Metrics.Snapshot().TotalRequests);
        Assert.Equal(2, fx.Metrics.Snapshot().AttemptCount);

        fx.Metrics.Reset();

        // ⚠ 两套都必须清零 —— 只清一套会让比值算出无穷大或者负值
        Assert.Equal(0, fx.Metrics.Snapshot().TotalRequests);
        Assert.Equal(0, fx.Metrics.Snapshot().AttemptCount);
        Assert.Empty(fx.Metrics.Snapshot().Recent);
    }

    // ══════════════════════════════════════════════════════════
    // ⑥ 脱敏防线没被这次改造破坏
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 口径改造没有给_RequestRecord_开自由文本的口子()
    {
        // 这条是**反射级**的守卫：t16 往 RequestRecord 里加了字段，
        // 必须证明加进去的全是值类型/数值/布尔 —— 没有任何一个
        // 能装下提示词或响应正文的字符串槽位是「新增的」。
        //
        // 为什么用反射而不是读代码：读代码挡不住下一次改动。
        // 反射断言会在**任何人**往这个 record 上加字段时失败，
        // 逼他回到这里来确认一遍。
        var properties = typeof(RequestRecord).GetProperties();

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            // 原有字段
            "At", "RequestId", "Model", "UsedModel", "Status", "DurationMs",
            "AccountTag", "ErrorKind", "Outcome", "Streamed", "UpstreamStatus",
            // t16 新增 —— ⚠ 全部是 int/bool，装不下正文
            //
            // ⚠ `Terminal` 原名 `IsTerminal`，改名是因为 ui 抓到一个**静默绑定坑**：
            //   ToJson() 写出的键是 "terminal"，而 System.Text.Json 的
            //   PropertyNameCaseInsensitive **不做 `Is` 前缀推理** ——
            //   所以 `IsTerminal` 读不回 `terminal`，会静默留在默认值 true
            //   （= 所有中间尝试都被当成终局）。改名后键名读写对称。
            //   **这条反射守卫当时就把改名拦下来了** —— 它按预期工作了。
            "Attempts", "Terminal", "SentToUpstream",
        };

        var actual = properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.True(
            expected.SetEquals(actual),
            $"RequestRecord 的字段集合变了。新增：{string.Join(",", actual.Except(expected))}；"
            + $"移除：{string.Join(",", expected.Except(actual))}。"
            + "加字段前请确认：它是否可能装下提示词/响应正文？如果是，这道脱敏防线就被破坏了。");

        // t16 新增的三个字段必须是值类型 —— 这是「装不下正文」的机械保证
        foreach (var name in (string[])["Attempts", "Terminal", "SentToUpstream"])
        {
            var type = typeof(RequestRecord).GetProperty(name)!.PropertyType;
            Assert.True(
                type == typeof(int) || type == typeof(bool),
                $"{name} 的类型是 {type.Name} —— t16 新增字段只允许 int/bool（不能是 string）");
        }

        // ★ 额外钉死：**不许出现 `Is` 前缀的布尔属性**（ui 抓到的静默绑定坑）
        //
        // 规律：`Is*` 属性的 ToJson() 键通常写成去掉前缀的形式（`terminal`），
        // 而反序列化端（App 的 DTO、任何读 /metrics 的程序）
        // **读不回来** —— 因为 case-insensitive 只比对完整名字。
        // 这个错编译不报、运行不报、字段还有默认值，属于最阴的一类。
        foreach (var p in properties.Where(p => p.PropertyType == typeof(bool)))
        {
            Assert.False(
                p.Name.StartsWith("Is", StringComparison.Ordinal) && p.Name.Length > 2 && char.IsUpper(p.Name[2]),
                $"{p.Name} 是 `Is` 前缀的布尔属性 —— 它序列化出去是 `{char.ToLowerInvariant(p.Name[2])}{p.Name[3..]}`，"
                + "但 System.Text.Json 的 PropertyNameCaseInsensitive 不做 `Is` 前缀推理，"
                + "反序列化端**读不回来**（会静默停在默认值）。请改名为不带 `Is` 的形式（如 Terminal）。");
        }
    }

    [Fact]
    public void _LogEntry_的字符串字段集合也必须被锁住()
    {
        // ## 为什么这条是补上的（队长提醒）
        //
        // 上面那条守卫只守 `RequestRecord`。但**写进日志的是 `LogEntry`** ——
        // 它才是一行一行落盘、被面板读出来显示的东西。
        //
        // 实测风险是真实的：`LogEntry` 是 `public sealed record`，所有
        // 字符串字段都是 `public ... { get; init; }`。也就是说**任何代码**
        // 都能写：
        //
        //     new LogEntry { RequestId = "x", Detail = rawPrompt }   // ← 编译通过
        //
        // `Detail` 的注释说它是「受控的短串」，但那个约束**只靠约定**，
        // 类型上没有任何拦阻。`RequestRecord` 那边有反射守卫兜着，
        // `LogEntry` 这边原本是敞的。
        //
        // 这条测试把字段集合钉住：任何人加字段都会失败，逼他回答
        // 「这个字段装不装得下正文」。**它不阻止加 string 字段**
        //（`Model`/`AccountTag` 本来就是 string 且合法），
        // 它强制的是「加字段必须是一次显式的、被 review 到的决定」。
        var properties = typeof(LogEntry).GetProperties();

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "At", "Level", "RequestId", "Model", "UsedModel", "Status",
            "DurationMs", "AccountTag", "ErrorKind", "Outcome", "Streamed",
            "Detail", "Aborted",
            // t16 新增 —— ⚠ 全部是 int/bool，装不下正文
            "Attempts", "Terminal",
        };

        var actual = properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.True(
            expected.SetEquals(actual),
            $"LogEntry 的字段集合变了。新增：{string.Join(",", actual.Except(expected))}；"
            + $"移除：{string.Join(",", expected.Except(actual))}。"
            + "加字段前请确认：它是否可能装下提示词/响应正文？如果是，脱敏防线就被破坏了。");

        // t16 新增的两个字段必须是值类型
        foreach (var name in (string[])["Attempts", "Terminal"])
        {
            var type = typeof(LogEntry).GetProperty(name)!.PropertyType;
            Assert.True(
                type == typeof(int) || type == typeof(bool),
                $"{name} 的类型是 {type.Name} —— t16 新增字段只允许 int/bool（不能是 string）");
        }

        // ★ 把「哪些 string 字段是允许存在的」也显式列出来 —— 这是那份
        //   「受控短串」白名单，从注释变成可执行的断言。
        var allowedStrings = new HashSet<string>(StringComparer.Ordinal)
        {
            "At",          // ISO 时间戳，我们自己生成的
            "Level",       // Information/Warning/Error，三选一
            "RequestId",   // 客户端给或我们生成的（已截断到 128）
            "Model",       // 模型名，客户端给的，不是内容
            "UsedModel",   // 同上
            "AccountTag",  // uid 前 8 位
            "ErrorKind",   // 枚举式分类词
            "Outcome",     // ok/failed/cancelled，三选一
            "Detail",      // 由 DetailFor 用受控片段拼成
        };

        var actualStrings = properties
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            allowedStrings.SetEquals(actualStrings),
            $"LogEntry 的 string 字段集合变了。新增：{string.Join(",", actualStrings.Except(allowedStrings))}；"
            + $"移除：{string.Join(",", allowedStrings.Except(actualStrings))}。"
            + "新增 string 字段前必须确认它的值来自受控来源（不是用户正文）。");
    }

    [Fact]
    public async Task 有人往_LogEntry_里塞提示词也能被这条防线发现()
    {
        // 证明上一条守卫不是摆设 —— 造一个「被污染」的 LogEntry，
        // 确认脱敏断言真的会响。
        //
        // ⚠ 这是**反向验证**：如果这条测试挂了，说明我们的脱敏断言
        //   其实抓不住污染，那前面所有「正文没进日志」的断言都是假的。
        const string prompt = "ZZTOP_INJECTED_PROMPT_SHOULD_BE_CAUGHT";

        // 证实这个口子**在类型上确实是开的**（可编译、可构造）
        var polluted = new LogEntry
        {
            At = "2026-10-07T00:00:00.000Z",
            Level = "Information",
            RequestId = "req-1",
            Detail = prompt,          // ← 编译通过。这就是风险敞口本身。
        };

        var line = polluted.ToLine();

        // 脱敏断言抓得到 —— 说明检测手段是有效的
        Assert.Contains(prompt, line, StringComparison.Ordinal);

        // 反转：同样的检测手段用在一条**正常**的记录上就不会误报
        var clean = new LogEntry
        {
            At = "2026-10-07T00:00:00.000Z",
            Level = "Information",
            RequestId = "req-1",
            Detail = "流式完成",
        };

        Assert.DoesNotContain(prompt, clean.ToLine(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 重试过程记进指标时_正文和_token_一个都不进去()
    {
        // 口径改造新增了字段，那些字段会被序列化进 /metrics。
        // 必须确认新增的出口没有把别的东西带出去。
        const string marker = "ZZTOP_CALIBER_PROMPT_MARKER";
        const string token = "sk-CALIBER-SECRET-0123456789abcdefghij";

        var vault = new TestVault().Add(
            uid: "uid-x", enterpriseId: "ent-x", domain: "workbuddy.cn",
            label: "号-x", accessToken: token, refreshToken: "rt");

        var fake = new FakeUpstreamHandler(_ => ChatOk());
        fake.Script(UpstreamResponses.TooManyRequests(), ChatOk("救回来"));

        await using var fx = await ObservabilityFixture.StartAsync(fake, vault);

        using (var response = await PostAsync(fx.Client, Body(marker)))
        {
            await response.Content.ReadAsStringAsync();
        }

        // /metrics 全文
        using var metricsResponse = await fx.Client.GetAsync("/metrics");
        var metricsText = await metricsResponse.Content.ReadAsStringAsync();

        // 脱敏日志全文（重试过程也进了日志）
        var logText = string.Join("\n", fx.Log.Entries.Select(e => e.ToLine()));

        foreach (var (name, text) in new (string Name, string Text)[]
                 {
                     ("/metrics", metricsText),
                     ("日志", logText),
                 })
        {
            Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }

        // 但重试这件事本身必须看得见（脱敏不等于把信号也删了）
        Assert.Contains("attempts", metricsText, StringComparison.Ordinal);
        Assert.Contains("attemptCount", metricsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 日志里能看出这次请求是重试捞回来的()
    {
        // 口径改造同时改了 RequestLog.Detail —— 重试成功的要显式说出来，
        // 否则日志看起来一切正常，而池子里已经有号在恶化了。
        var fake = new FakeUpstreamHandler(_ => ChatOk());
        fake.Script(UpstreamResponses.TooManyRequests(), ChatOk("救回来"));

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b"));

        using (var response = await PostAsync(fx.Client, Body("一次请求")))
        {
            await response.Content.ReadAsStringAsync();
        }

        var entries = fx.Log.Entries;

        // 中间失败那条：标出「准备换号重试」
        var intermediate = entries.First(e => e.Attempts == 1 && !e.Terminal);
        Assert.Contains("换号重试", intermediate.Detail, StringComparison.Ordinal);

        // ⚠ 中间失败是 429（4xx）→ 基础级别是 Warning，
        //   但它是**中间尝试**，要降一级到 Information ——
        //   否则一次被重试捞回来的请求会在日志里留下一条刺眼的 Warning。
        Assert.Equal("Information", intermediate.Level);

        // 终局那条：标出「共尝试 N 次」，级别 Information（成功了）
        var terminal = entries.First(e => e.Terminal);
        Assert.Equal(2, terminal.Attempts);
        Assert.Contains("换号重试 2 次后成功", terminal.Detail, StringComparison.Ordinal);
        Assert.Equal("Information", terminal.Level);
    }

    [Fact]
    public async Task 中间尝试的五xx_降级到_Warning_而终局失败仍是_Error()
    {
        // 5xx 的中间尝试如果按原级别记成 Error，一次被重试捞回来的请求
        // 会在日志里留下刺眼的 Error —— 而用户其实拿到了完整回答。
        var fake = new FakeUpstreamHandler(_ => ChatOk());
        fake.Script(UpstreamResponses.ServerError("上游抽风"), ChatOk("救回来"));

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b"));

        using (var response = await PostAsync(fx.Client, Body("一次请求")))
        {
            await response.Content.ReadAsStringAsync();
        }

        var intermediate = fx.Log.Entries.First(e => !e.Terminal);
        Assert.Equal("Warning", intermediate.Level);   // 不是 Error
    }

    // ══════════════════════════════════════════════════════════
    // ⑦ JSON 键名读写对称（ui 抓到的静默绑定坑）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task _metrics_的_JSON_键名读写必须对称()
    {
        // ## 为什么单独立这一条（ui 在 App 侧踩到的坑）
        //
        // 原来的 `RequestRecord.IsTerminal` 的 ToJson() 写出的键是 `"terminal"`
        // —— **写得出、读不回来**。
        //
        // 根因：System.Text.Json 的 `PropertyNameCaseInsensitive`
        // 只做「大小写无关的**完整名字**比较」，**不做 `Is` 前缀推理**。
        // 所以 `IsTerminal` 只接受 `isTerminal`/`IsTerminal`，不接受 `terminal`。
        //
        // **为什么危险**：默认值是 `true`，绑不上时静默留在 `true`
        // → 所有中间失败的尝试都被当成终局结果。
        // 编译不报、运行不报、字段还有值。
        //
        // ui 的 App DTO 读同一份 JSON，也中了同一个招。
        //
        // ## 这条测试做什么
        //
        // 拿**真网关**吐出的 `/metrics`，用一个「和 App DTO 同形状」的
        // 类型反序列化回来，断言**终局标记能正确读回 false**。
        // 而不是只断言「写出去有 terminal 键」—— 那正是漏掉这个 bug 的写法。
        var fake = new FakeUpstreamHandler(_ => ChatOk());
        fake.Script(UpstreamResponses.TooManyRequests(), ChatOk("救回来"));

        await using var fx = await ObservabilityFixture.StartAsync(fake, Vault("a", "b"));

        using (var response = await PostAsync(fx.Client, Body("一次请求")))
        {
            await response.Content.ReadAsStringAsync();
        }

        var json = await fx.MetricsJsonAsync();

        // 反序列化回一个「消费方视角」的对象（模拟 App 的 DTO 读法）
        var recentJson = json["recent"]!.AsArray();
        var options = new System.Text.Json.JsonSerializerOptions
        {
            // ⚠ 故意开大小写不敏感 —— 这正是 App 侧的配置，
            //   也是那个坑**看起来应该管用但实际不管用**的原因
            PropertyNameCaseInsensitive = true,
        };

        var parsed = recentJson
            .Select(node => System.Text.Json.JsonSerializer.Deserialize<ConsumerView>(node!.ToJsonString(), options)!)
            .ToArray();

        Assert.Equal(2, parsed.Length);

        // ★ 核心断言：中间尝试必须读回 Terminal=false
        //
        // 如果 `Is*` 前缀那个坑还在，这里会是 True（静默默认值）——
        // 于是「中间尝试」和「终局」在消费方眼里完全分不出来。
        var intermediate = parsed.Single(p => p.Status == 429);
        Assert.False(intermediate.Terminal, "中间失败的尝试读回来必须是 false，不能静默停在默认值 true");

        var terminal = parsed.Single(p => p.Status == 200);
        Assert.True(terminal.Terminal);

        // 另外两个新字段也要能读回来（它们本来就没有 Is 前缀，应当正常）
        Assert.Equal(1, intermediate.Attempts);
        Assert.Equal(2, terminal.Attempts);
    }

    /// <summary>
    /// 「消费方视角」的 DTO —— 模拟 App 的 <c>RecentRequest</c> 读法。
    ///
    /// 故意**不加** <c>[JsonPropertyName]</c>：这正是 App 侧最初的写法。
    /// 属性叫 `Terminal`（不带 Is 前缀）时它工作正常；
    /// 若改回 `IsTerminal`，本测试会红。
    /// </summary>
    private sealed record ConsumerView
    {
        public int Status { get; init; }
        public int Attempts { get; init; }
        public bool Terminal { get; init; }
        public bool SentToUpstream { get; init; }
    }
}
