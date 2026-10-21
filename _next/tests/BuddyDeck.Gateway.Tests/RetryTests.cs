using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 限流降级链的测试（t8 建，t28 扩成两层）。
///
/// ## 两层结构（t28）
///
/// ```
/// 外层：换【号】   ≤ MaxUpstreamAttempts（默认 3）
///   └ 内层：换【模型】 ≤ ModelFallbackLimit（= 4，对齐旧版 sendWithFallback）
/// 5xx/传输失败 → 不换模型，直接跳出内层换号
/// ```
///
/// **为什么内层优先**：上游限流按「账号 + 模型」算（`ratelimit.js:16` 实测），
/// 它自己的文案就说「您也可以**切换其他模型**继续使用」。
/// 换模型不用重走凭证链路，比换号便宜。
///
/// ## 为什么这些用例必须真跑
///
/// 断言的是「客户端拿到什么」「上游收到几次、用的哪个号、哪个模型」，
/// 不是「内部调了什么方法」。全部走**真 Kestrel + 真端口 + 脚本化假上游**。
///
/// ## 用的夹具
///
/// `BuddyDeck.TestSupport` 的 `GatewayHost` / `FakeUpstreamHandler` /
/// `UpstreamScript` / `TestVault` / `UpstreamResponses`。
/// 不自己另造一套 —— 见 t7 的夹具收敛结论。
/// </summary>
public class RetryTests
{
    private const string ChatBody = """{"model":"hy3","messages":[{"role":"user","content":"你好"}],"stream":false}""";

    private static StringContent Json(string body)
        => new(body, Encoding.UTF8, "application/json");

    /// <summary>一次正常的流式回复（重试成功后要看到它）。</summary>
    private static HttpResponseMessage OkSse()
        => UpstreamResponses.SseText(
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}""",
            """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{"content":"换号成功"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}""");

    /// <summary>造一个假上游（默认回一条空 SSE，用 Script 覆盖）。</summary>
    private static FakeUpstreamHandler Fake() => new(UpstreamResponses.SseText("{}"));

    /// <summary>从假上游记录的请求里取 Authorization 头。</summary>
    private static string AuthOf(CapturedRequest request) => request.HeaderOrNull("Authorization") ?? "";

    /// <summary>
    /// 内层可能试的**全部候选模型**（内置目录的 16 个）。
    ///
    /// 为什么要这个：要构造「真的没号可用」，必须把那个号对这些模型**全部**冷却
    /// —— 只冷一个是不够的，内层会换到别的模型（那是 t28 的正确行为）。
    /// 这里用内置目录而不是读 `_catalog`，是因为测试不该依赖网关内部状态。
    /// </summary>
    private static string[] AllCandidateModels() =>
    [
        "hy4-preview", "hy3", "hy3-x", "deepseek-v4.1-flash",
        "glm-5.3", "glm-5.3-flash", "glm-5.2", "glm-5.1",
        "glm-5v-turbo", "minimax-m3", "minimax-m2.7", "kimi-k3-1",
        "kimi-k2.8-preview", "kimi-k2.7", "kimi-k2.6", "deepseek-v4-pro",
    ];

    // ── 场景 1：第 1 次 429 → 第 2 次 200 ──────────────────────

    [Fact]
    public async Task 第一次限流第二次成功_换的是模型而不是号()
    {
        // ## ⚠ t28 之后的语义变更（必读）
        //
        // 这条原来叫「…换了号」，断言两次请求的 Authorization 不同。
        //
        // **t28 把降级链改成两层**：内层先在**同一账号内换模型**，耗尽后才换号。
        // 依据是上游自己的报错文案：「您也可以**切换其他模型**继续使用」
        // （ratelimit.js:10 实测原文）—— 限流是按「账号+模型」算的。
        //
        // 换模型更便宜：同一个 credential、同一个 token，
        // **不用重走凭证链路、不用让会话粘性让路**。
        //
        // 所以这条现在断言 **模型变了、号没变** —— 这正是新行为该有的样子。
        // 换号的场景由下面 `换号_当前账号所有模型都限流时才发生` 覆盖。
        var fake = Fake();
        fake.Script(UpstreamResponses.TooManyRequests(), OkSse());

        var vault = new TestVault().AddMany(2);

        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, fake.Count);

        var first = fake.Requests[0];
        var second = fake.Requests[1];

        // ★ 号**没**变（内层换模型，不该动账号）
        Assert.Equal(AuthOf(first), AuthOf(second));

        // ★ 模型变了（这才是降级的实质）
        var firstModel = first.Json()!["model"]!.GetValue<string>();
        var secondModel = second.Json()!["model"]!.GetValue<string>();
        Assert.NotEqual(firstModel, secondModel);
    }

    [Fact]
    public async Task 换号_当前账号所有模型都限流时才发生()
    {
        // ★ 外层换号的条件：**内层所有候选模型都被限了**。
        //
        // 上游一直回 429 → 内层把候选模型逐个试完并分别冷却 →
        // 才轮到外层换号。所以断言：用到 >1 个号，即**号确实变了**。
        var fake = Fake();
        fake.Script(UpstreamResponses.TooManyRequests());

        var vault = new TestVault().AddMany(2);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        using var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));
        await response.Content.ReadAsStringAsync();

        var tokens = fake.Requests.Select(AuthOf).Distinct(StringComparer.Ordinal).ToList();
        Assert.True(tokens.Count >= 2,
            $"内层模型耗尽后应该换号；实际只用了 {tokens.Count} 个号（{string.Join(",", tokens)}）");
    }

    [Fact]
    public async Task 换号之后响应正文正常返回()
    {
        var fake = Fake();
        fake.Script(UpstreamResponses.TooManyRequests(), OkSse());

        var vault = new TestVault().AddMany(2);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));
        var body = await response.Content.ReadAsStringAsync();

        var json = JsonNode.Parse(body)!;
        Assert.Equal("chat.completion", json["object"]!.GetValue<string>());
        Assert.Equal("换号成功", json["choices"]!.AsArray()[0]!["message"]!["content"]!.GetValue<string>());
    }

    // ── 场景 2：连续 3 次 429 ─────────────────────────────────

    [Fact]
    public async Task 全被限流_客户端拿到429且kind是soft_rate()
    {
        // ## ⚠ t28 之后的语义变更（必读）
        //
        // 原来这条断言「恰好试 3 次」（3 个号 × 1 个模型）。
        //
        // **t28 加了内层换模型**后，尝试次数变成 **号数 × 内层候选模型数**：
        // 3 个号 × 4 个模型 = 12 次。**这不是无限重试，是两层各自的穷尽。**
        //
        // 所以断言改成「有上限、且真的把两层都试过」而不是钉死一个数字 ——
        // 钉死数字会让下次调 `ModelFallbackLimit` 时又要改一遍，
        // 而那个数字本来就不该是契约（契约是「不会无限试」）。
        var fake = Fake();
        fake.Script(UpstreamResponses.TooManyRequests());

        var vault = new TestVault().AddMany(3);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        // 必须给一个**明确的**错误对象，绝不能返回 undefined/null
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.NotNull(json["error"]);
        Assert.False(string.IsNullOrWhiteSpace(json["error"]!["message"]!.GetValue<string>()),
            "error.message 不能为空");

        // ⚠ kind 必须是 soft_rate —— 这是**客户端可见契约**（旧版 gateway.js:475 同形）
        Assert.Equal("soft_rate", json["error"]!["type"]!.GetValue<string>());
        // 文案含「试过 N 次」（旧版同款措辞）
        Assert.Contains("试过", json["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);

        // 有上限：不可能无限试（3 号 × 4 模型 = 12 是当前上界）
        Assert.True(fake.Count <= 12, $"尝试了 {fake.Count} 次，超过两层的上界 12");
        Assert.True(fake.Count > 3, $"只试了 {fake.Count} 次 —— 内层换模型没生效？");

        // 三个号都用过（外层换号确实发生了）
        var tokens = fake.Requests.Select(AuthOf).Distinct(StringComparer.Ordinal).ToList();
        Assert.Equal(3, tokens.Count);
    }

    [Fact]
    public async Task 模型级限流_也在账号内先换模型()
    {
        // t28 之后：模型级限流（429 + 6004）同样走「内层换模型」——
        // 上游文案自己就说「切换其他模型继续使用」。
        var fake = Fake();
        fake.Script(UpstreamResponses.ModelRateLimited("hy3"), OkSse());

        var vault = new TestVault().AddMany(2);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, fake.Count);

        // 号不变、模型变（内层行为）
        Assert.Equal(AuthOf(fake.Requests[0]), AuthOf(fake.Requests[1]));
        Assert.NotEqual(
            fake.Requests[0].Json()!["model"]!.GetValue<string>(),
            fake.Requests[1].Json()!["model"]!.GetValue<string>());
    }

    // ── 场景 3：401 / 403 / 400 不重试 ────────────────────────

    [Fact]
    public async Task 上游401_不重试且只收到一次()
    {
        // ★ 凭证失效重试是**纯浪费**：换个号重放同一个失效凭证，
        //   或者更糟 —— 把一个健康号也拖下水。
        var fake = Fake();
        fake.Script(UpstreamResponses.Unauthorized(), OkSse());

        var vault = new TestVault().AddMany(3);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, fake.Count);   // ← 一次都不重试
    }

    [Fact]
    public async Task 上游403_不重试()
    {
        var fake = Fake();
        fake.Script(UpstreamResponses.Forbidden(), OkSse());

        var vault = new TestVault().AddMany(3);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);  // 403 → 401
        Assert.Equal(1, fake.Count);
    }

    [Fact]
    public async Task 上游400_不重试()
    {
        // 请求本身有问题时，换个号重放同样的坏请求只会浪费配额。
        var fake = Fake();
        fake.Script(UpstreamResponses.Text("""{"code":40001,"message":"缺少 model"}""", HttpStatusCode.BadRequest));

        var vault = new TestVault().AddMany(3);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, fake.Count);
    }

    // ── 5xx 可重试 ────────────────────────────────────────────

    [Fact]
    public async Task 上游500可重试_换个号能成功()
    {
        var fake = Fake();
        fake.Script(UpstreamResponses.ServerError(), OkSse());

        var vault = new TestVault().AddMany(2);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, fake.Count);
    }

    // ── 场景 4：没有可用号 ────────────────────────────────────

    [Fact]
    public async Task 所有号都在冷却_客户端拿到429而不是500()
    {
        // ## ⚠ t28 之后的语义变更（必读）
        //
        // 原来只把 `hy3` 打进冷却就期望 429。**加了内层换模型后这不成立了** ——
        // 那个号的 hy3 被冷，但它还能用 hy4-preview 跑（上游的限流本来就是
        // 「账号+模型」维度）。所以只冷一个模型 → 换模型成功 → 200。
        //
        // **要构造「真的没号可用」，必须把那个号的所有候选模型都冷掉。**
        var fake = Fake();
        fake.Script(OkSse());

        var vault = new TestVault().AddMany(1);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var pool = host.Application.Services.GetRequiredService<AccountPool>();
        pool.Replace(vault.Entries);
        var member = pool.Members.Single();

        // 把这个号对**每一个候选模型**都打进冷却 —— 内层换无可换。
        var far = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds();
        foreach (var model in AllCandidateModels())
        {
            pool.MarkRateLimited(member.Id, model, far);
        }

        var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        // ⚠ 429（配额问题）而不是 500（我们崩了）
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.NotNull(json["error"]);
        Assert.False(string.IsNullOrWhiteSpace(json["error"]!["message"]!.GetValue<string>()));

        // 一个请求都没发出去（没号可用）
        Assert.Equal(0, fake.Count);
    }

    [Fact]
    public async Task 保险库里没有账号_401而不是500()
    {
        var fake = Fake();
        fake.Script(OkSse());

        await using var host = await GatewayHost.StartReadyAsync(fake, new TestVault());

        var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── 场景 5：客户端取消 ────────────────────────────────────

    [Fact]
    public async Task 客户端取消_不产生500()
    {
        var fake = Fake();
        fake.Script(OkSse());

        var vault = new TestVault().AddMany(3);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // 客户端在请求发出前就取消 —— 网关必须原样中止，不能变成 500
        HttpResponseMessage response;
        try
        {
            response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 这是**正确**结果：取消原样抛出，没有被包成 500
            return;
        }

        using (response)
        {
            // 没抛也是可以的（请求可能已经完成）—— 但绝不能是 500
            Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        }
    }

    [Fact]
    public async Task 取消之后不重试_不会继续换号烧配额()
    {
        var fake = Fake();
        fake.Script(UpstreamResponses.TooManyRequests(), OkSse());

        var vault = new TestVault().AddMany(3);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        try
        {
            await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 预期
        }

        // 取消之后不该看到「多次尝试」——最多 1 次（甚至在发出前就被取消）
        Assert.True(fake.Count <= 1, $"取消后仍发了 {fake.Count} 次请求，重试没有及时中止");
    }

    // ── 观察者：每次尝试都要记 ────────────────────────────────

    [Fact]
    public async Task 每次尝试都经过观察者_重试两次能收到两个事件()
    {
        // ★ 漏记的话，observability 的「上游成功率 / 重试次数」统计就是错的。
        var fake = Fake();
        fake.Script(UpstreamResponses.TooManyRequests(), OkSse());

        var recorder = new RecordingObserver();
        var vault = new TestVault().AddMany(2);

        await using var host = await GatewayHost.StartReadyAsync(fake, vault,
            configure: services => services.AddSingleton<IChatObserver>(recorder));

        await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        // 第一次失败 + 第二次成功 = 两个事件
        Assert.Equal(2, recorder.Events.Count);

        var failed = recorder.Events[0];
        Assert.Equal("failed", failed.Outcome);
        Assert.Equal(UpstreamErrorKind.RateLimited, failed.Kind);
        Assert.Equal(429, failed.UpstreamStatus);

        var ok = recorder.Events[1];
        Assert.Equal("ok", ok.Outcome);
    }

    [Fact]
    public async Task 三次全失败时观察者收到三个失败事件()
    {
        var fake = Fake();
        fake.Script(UpstreamResponses.TooManyRequests());

        var recorder = new RecordingObserver();
        var vault = new TestVault().AddMany(3);

        await using var host = await GatewayHost.StartReadyAsync(fake, vault,
            configure: services => services.AddSingleton<IChatObserver>(recorder));

        await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        // ## ⚠ t28 之后的语义变更
        //
        // 原来断言「恰好 3 个事件」。加了两层降级后，事件数 = 中间失败尝试数 + 1 条终局。
        // 这里改成断言**结构**而不是数字：
        //   · 每个事件都是 failed（全被限流）
        //   · 至少 2 个事件（有重试就有「中间」）
        //   · 用的是**多个不同的号**（外层确实换了号）
        Assert.True(recorder.Events.Count >= 2,
            $"应该有多次尝试；实际只有 {recorder.Events.Count} 个事件");
        Assert.All(recorder.Events, e => Assert.Equal("failed", e.Outcome));

        var accounts = recorder.Events.Select(e => e.MemberId).Distinct(StringComparer.Ordinal).ToList();
        Assert.True(accounts.Count >= 2,
            $"外层应该换过号；实际只用过 {accounts.Count} 个号");
    }

    // ── 冷却按模型记（不许把别的模型一起拉黑）────────────────

    [Fact]
    public async Task 限流之后只冷却那个模型_不牵连别的模型()
    {
        var fake = Fake();
        fake.Script(UpstreamResponses.TooManyRequests(), OkSse());

        var vault = new TestVault().AddMany(2);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var pool = host.Application.Services.GetRequiredService<AccountPool>();
        pool.Replace(vault.Entries);

        await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        // 被限过的号：hy3 进冷却
        var cooled = pool.Members.Where(m => m.Cooldowns.ContainsKey("hy3")).ToList();
        Assert.NotEmpty(cooled);

        // 但没有任何号因为它连**别的**模型一起被拉黑
        Assert.All(pool.Members, m => Assert.DoesNotContain("glm-5.3", m.Cooldowns.Keys));
    }

    // ── 单号池：不重复撞同一个号 ──────────────────────────────

    [Fact]
    public async Task 单号池_一个模型被限时_换模型而不是直接失败()
    {
        // ## ⚠ t28 之后的语义变更
        //
        // 原来这条叫「单号池被限_只试一次就返回429」—— 断言 1 次就放弃。
        //
        // **t28 之后单号池也能救**：第一个模型被限 → 内层换模型 → 第二个模型成功。
        // 这正是「换模型优先于换号」在**单号场景**下的价值：
        // 只有一个号时，换号无路可走，换模型是唯一的出路。
        var fake = Fake();
        fake.Script(UpstreamResponses.TooManyRequests(), OkSse());

        var vault = new TestVault().AddMany(1);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, fake.Count);

        // 同一个号（只有一个），换的是模型
        Assert.Equal(AuthOf(fake.Requests[0]), AuthOf(fake.Requests[1]));
        Assert.NotEqual(
            fake.Requests[0].Json()!["model"]!.GetValue<string>(),
            fake.Requests[1].Json()!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task 单号池_所有模型都被限_才回429()
    {
        // 上一条的对照：一个号 + **所有候选模型**都被限 → 才是真的没救。
        var fake = Fake();
        fake.Script(UpstreamResponses.TooManyRequests());

        var vault = new TestVault().AddMany(1);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        using var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("soft_rate", json["error"]!["type"]!.GetValue<string>());
    }

    // ══ t28 新增：三层保护的具体行为 ═══════════════════════════

    [Fact]
    public async Task 冷却中的账号模型组合_连请求都不发()
    {
        // ★ t28 契约第 ③ 条。旧版注释：`跳过 ${current}（冷却中）`。
        //
        // ## 这条为什么值得单独测
        //
        // 不跳过的话，那个请求会**照发出去**：出网 → 被上游 429 → 再记一次失败。
        // 三个坏处：
        //   · 白占一个在途名额（t19 的上限被无谓消耗）
        //   · `AttemptCount` 被污染（体检指标失真）
        //   · 实测表现是「0ms 秒回失败」—— 极难查，因为它看起来像网络问题
        //
        // 所以这里**预先**把「唯一那个号 + 唯一那个模型」打进冷却，
        // 然后断言：**上游一次请求都没收到**（`fake.Count == 0`）。
        var fake = Fake();
        fake.Script(OkSse());

        var vault = new TestVault().AddMany(1);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var pool = host.Application.Services.GetRequiredService<AccountPool>();
        pool.Replace(vault.Entries);

        // 把这个号**对所有候选模型**都打进冷却 —— 这样内层换模型也换无可换。
        // 只冷却 hy3 是不够的：内层还会去试 glm-5.3 等（那是 t28 的正确行为）。
        var member = pool.Members.Single();
        var far = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds();
        foreach (var model in AllCandidateModels())
        {
            pool.MarkRateLimited(member.Id, model, far);
        }

        using var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        // 客户端拿到明确的 429
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        // ★★ 核心断言：一个请求都没发出去
        Assert.Equal(0, fake.Count);
    }

    [Fact]
    public async Task 部分模型冷却时_换到没被冷的模型而不是直接失败()
    {
        // 上一条的对照 —— 证明「跳过冷却组合」不等于「什么都不试」。
        // 只有 hy3 被冷 → 内层换到别的模型 → 成功。
        var fake = Fake();
        fake.Script(OkSse());

        var vault = new TestVault().AddMany(1);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var pool = host.Application.Services.GetRequiredService<AccountPool>();
        pool.Replace(vault.Entries);

        var member = pool.Members.Single();
        pool.MarkRateLimited(member.Id, "hy3", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds());

        using var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 只发了 1 次 —— hy3 被跳过，第一次实际发出去的就是别的模型
        Assert.Equal(1, fake.Count);
        Assert.NotEqual("hy3", fake.Requests[0].Json()!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task 限流文案里的重置时间_被写进冷却截止时刻()
    {
        // ★ t28 契约第 ⑦ 条（M13）。
        //
        // 上游的 429 文案里带着**明确的恢复时刻**：
        // 「您的使用量已超出频率限制，将在 2026-09-25 17:07:48 UTC+8 重置」
        //
        // 不解析的话只能退回「默认冷却 5 分钟」，两个方向都错：
        //   · 上游说 30 秒后恢复 → 白等 4 分半（可用容量平白少一截）
        //   · 上游说 2 小时后恢复 → 5 分钟后又来撞（白撞一次还污染指标）
        var fake = Fake();
        fake.Script(UpstreamResponses.Text(
            """{"code":6004,"msg":"您的使用量已超出频率限制，将在 2026-09-25 17:07:48 UTC+8 重置，您也可以切换其他模型继续使用。"}""",
            HttpStatusCode.TooManyRequests));

        var vault = new TestVault().AddMany(1);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var pool = host.Application.Services.GetRequiredService<AccountPool>();
        pool.Replace(vault.Entries);

        using var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));
        await response.Content.ReadAsStringAsync();

        var member = pool.Members.Single();

        // 上游给的那个时刻（python 独立复算过：17:07:48 UTC+8 = 1790327268000）
        const long expectedReset = 1790327268000;

        // 那个模型必须进冷却，且截止时刻**就是上游说的那个** —— 不是「默认 5 分钟后」
        Assert.True(member.Cooldowns.ContainsKey("hy3"), "hy3 应该进冷却");

        // ⚠ 这里断言的是「约等于上游给的值」，不是「等于 now + 5min」。
        //   如果解析失效退回了默认冷却，这个值会差出好几个月，断言立刻抓到。
        var cooled = member.Cooldowns["hy3"];
        Assert.Equal(expectedReset, cooled);
    }

    [Fact]
    public async Task 无法解析重置时间时_退回默认冷却而不是立刻可重试()
    {
        // 上游没说时刻时的行为：**退回默认冷却**，而不是「当作已恢复」。
        // 当作已恢复的话，下一枪立刻又撞同一个限流 —— 白撞且污染指标。
        var fake = Fake();
        fake.Script(UpstreamResponses.Text(
            """{"code":6004,"msg":"您的使用量已超出频率限制"}""",   // ← 没有时刻
            HttpStatusCode.TooManyRequests));

        var vault = new TestVault().AddMany(1);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var pool = host.Application.Services.GetRequiredService<AccountPool>();
        pool.Replace(vault.Entries);

        using var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));
        await response.Content.ReadAsStringAsync();

        var member = pool.Members.Single();

        Assert.True(member.Cooldowns.ContainsKey("hy3"));
        // 应该是一个**未来**的时刻（默认 5 分钟），不是 0 也不是过去
        Assert.True(member.Cooldowns["hy3"] > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "冷却截止时刻必须在未来 —— 否则等于没冷却");
    }

    [Fact]
    public async Task 不可重试的错误_一枪定局不换号不换模型()
    {
        // ★ t28 契约第 ⑤ 条 + t8 的原始约束。
        //
        // 401/403（凭证失效）、400（请求有问题）**绝不能重试**：
        //   · 凭证失效：换号也是同一个失效凭证的兄弟，得重新登录
        //   · 请求有问题：换个号/换个模型重放同样的坏请求只是浪费配额
        //
        // 所以断言「只发出去 1 次」—— 内层没换模型、外层没换号。
        foreach (var (upstream, expectedStatus) in new[]
        {
            (UpstreamResponses.Unauthorized(), HttpStatusCode.Unauthorized),
            (UpstreamResponses.Forbidden(), HttpStatusCode.Unauthorized),
            (UpstreamResponses.Text("""{"code":40001,"message":"缺少 model"}""", HttpStatusCode.BadRequest),
             HttpStatusCode.BadRequest),
        })
        {
            var fake = Fake();
            fake.Script(upstream);

            var vault = new TestVault().AddMany(3);   // 故意给多号，证明不是「没号可换」
            await using var host = await GatewayHost.StartReadyAsync(fake, vault);

            using var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

            Assert.Equal(expectedStatus, response.StatusCode);
            Assert.Equal(1, fake.Count);   // ← 一枪定局
        }
    }

    [Fact]
    public async Task 服务端错误_不换模型直接换号()
    {
        // 5xx 不是模型的问题（换模型没用），所以内层 break、直接换号。
        //
        // ⚠ 脚本要按**实际请求数**给足步骤（这是我第一版写错的地方）：
        //   2 个号 × 每个号 1 枪（5xx 不换模型）= 2 枪失败，
        //   第 3 枪应该由**第 3 个号**成功。
        //   所以要用 3 个号 + 3 步脚本。
        //   （`UpstreamScript` 超出长度时**重复最后一步** —— 给少了会让
        //    成功那一步永远用不到，表现是 502 而不是 200。）
        var fake = Fake();
        fake.Script(UpstreamResponses.ServerError(), UpstreamResponses.ServerError(), OkSse());

        var vault = new TestVault().AddMany(3);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var response = await host.Client.PostAsync("/v1/chat/completions", Json(ChatBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, fake.Count);

        // ★ 前两枪是**两个不同的号**（换号），而不是同一个号换模型
        var first = AuthOf(fake.Requests[0]);
        var second = AuthOf(fake.Requests[1]);
        Assert.NotEqual(first, second);

        // ★ 而且两次的模型**相同** —— 证明 5xx 确实没触发内层换模型
        Assert.Equal(
            fake.Requests[0].Json()!["model"]!.GetValue<string>(),
            fake.Requests[1].Json()!["model"]!.GetValue<string>());
    }
}
