using System.Net;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.Gateway;
using BuddyDeck.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuddyDeck.E2E.Tests;

/// <summary>
/// 限流、换号、冷却、取消 —— 这一组的重点不是「响应长得对不对」，
/// 而是**状态在多请求之间如何流动**。
///
/// ## 现状（t13 实测，t28 落地后）
///
/// **重试是两层的**（`GatewayApp.RunWithRetryAsync`，`GatewayApp.cs:602-754`）：
///
/// ```
/// 外层：换【号】     ≤ MaxUpstreamAttempts（默认 3）
///   └ 内层：换【模型】 ≤ ModelFallbackLimit（= 4，对齐旧版 sendWithFallback）
/// ```
///
/// **换模型优先于换号** —— 同一个号内先把 4 个模型试完，才换下一个号。
/// 另外：`5xx`/传输失败**不换模型**（`if (kind == Server) break;` —— 不是模型的问题），
/// 直接跳出内层去换号。
///
/// > 历史：t4 时期网关是**单次转发**（不换号、不重试）；t8 加了**单层**换号重试；
/// > t28 改成**两层**（换模型优先）。**本文件里旧的「不重试」表述都已随各轮更正** ——
/// > 留这段是因为它记录了一条链路的三次语义变化，不是因为现状如此。
///
/// 「429 → 自动恢复 → 成功」这条链路在本组里分四层验：
///
/// | 层 | 用例 | 现状 |
/// |---|---|---|
/// | 池 | 429 记冷却 → 带 exclude 换号 → 第 2 次 200 | ✅ 通过 |
/// | 网关 | **单号池**：内层换 4 个模型后耗尽 → 429 错误对象 | ✅ 通过 |
/// | 网关 | **双号池、两号都被限**：耗尽后 429 + `soft_rate` | ✅ 通过 |
/// | 网关 | 换模型 4 枪全败 → 换号成功（HANDOVER P1 的形状） | ✅ 通过 |
///
/// ## ⚠⚠ 本文件里的「数值断言」读之前先看理由
///
/// 有**四处** `fake.Count` 断言，**数值与原因各不相同**，最容易被读错：
///
/// | 断言 | 为什么是这个数 | 何时需复查 |
/// |---|---|---|
/// | <see cref="池层_第一次429记冷却_带exclude换成另一个号_第二次成功"/> 的 `== 2` | **池层**：两次请求由测试侧的 `SwappingChatLoop` 驱动 | 不随网关改动 |
/// | <see cref="网关层_单号池时无处可重试_客户端拿到429错误对象"/> 的 `== 4` | **单号内层换 4 个模型**（`ModelFallbackLimit`）—— **不是账号数** | 内层上限变化时 |
/// | <see cref="网关层_双号池且两号都被限_回429并标注soft_rate"/> 的 `== 8` | **2 号 × 4 模型** | 账号数或内层上限变化时 |
/// | <see cref="网关层_上游429时自动换号并重试到成功"/> 的 `== 5` | **号 A 的 4 枪 + 号 B 的 1 枪** | 同上 |
///
/// ⇒ **别看到 `fake.Count` 就把几处一起改** —— 那是改错。各条上方都有更详细的推导。
///
/// ### 为什么这些数字会被改来改去（一条值得记的规律）
///
/// `== 1` 这条被改过两次，**两次都不是"放宽"，而是跟着实现改语义**：
/// ```
/// t4（无重试）        单号 → 1
/// t8（单层换号）      单号 → 1（无处可换号）
/// t28（两层：先换模型）单号 → 4（同号内试 4 个模型）
/// ```
/// **数值断言的翻转时机，取决于「哪个任务先落地」，而不是「谁的分析更对」。**
/// ⇒ 所以本文件里所有这类标注都写 **「什么条件下需复查」**，
///   而**不写「哪个任务会让它变」** —— 因为任务顺序经常变（限流打断、合并、改派），
///   条件不会变。
/// </summary>
[Collection("GatewayHost")]
public class PoolAndRetryE2ETests
{
    private static string ChatBody(string model = "hy3", bool stream = false)
        => $$"""{"model":"{{model}}","messages":[{"role":"user","content":"在吗"}],"stream":{{(stream ? "true" : "false")}}}""";

    // ── 层 1：池（换号真的发生了）────────────────────────────

    [Fact]
    public async Task 池层_第一次429记冷却_带exclude换成另一个号_第二次成功()
    {
        // 两个号：第一个被限流，第二个正常。
        // 这一条走的是**真的 UpstreamClient + 真的 HTTP**（打到假上游），
        // 不是拿 mock 顶替 —— 只有这样才能确认「换号」体现为
        // Authorization 头真的变了。
        var vault = new TestVault()
            .Add(uid: "user-a", label: "被限流的号")
            .Add(uid: "user-b", label: "正常的号");

        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.ServerError("兜底，不该走到"));
        fake.Script(
            UpstreamResponses.TooManyRequests("账号被限流"),
            UpstreamResponses.SseText(
                StreamScript.RoleFrame(),
                StreamScript.ContentFrame("换号成功"),
                StreamScript.StopFrame()));

        var pool = new AccountPool(TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountPool>.Instance);
        pool.Replace(vault.Entries);

        var attempts = await new SwappingChatLoop(fake, pool, UpstreamVariant.China).RunAsync("hy3");

        // 第 1 轮：被限流
        Assert.Equal(2, attempts.Count);
        Assert.False(attempts[0].Ok);
        Assert.Equal(UpstreamErrorKind.RateLimited, attempts[0].Kind);

        // 第 2 轮：换了号、成功
        Assert.True(attempts[1].Ok, "换号之后应该成功");
        Assert.NotEqual(attempts[0].MemberId, attempts[1].MemberId);

        // ★ 这个 `== 2` 是**池层**的事实，不是网关层的事实 —— 别有任务改网关时误翻这条。
        //
        // 两次请求来自 `SwappingChatLoop`（测试侧的参考重试循环）主动打的两轮，
        // 「换号」在这里是**测试自己驱动的**，与网关是否实现重试无关。
        // 网关那条对应断言是 `网关层_单号池时无处可重试_客户端拿到429错误对象`
        // 里的 `fake.Count == 1` —— **两种原因不同**（这条是测试驱动的两轮，
        // 那条是单号导致重试无处可去），改一处不要顺手改另一处。
        Assert.Equal(2, fake.Count);
        Assert.Equal("Bearer access-token-1", fake.AuthorizationHeaders[0]);
        Assert.Equal("Bearer access-token-2", fake.AuthorizationHeaders[1]);
        Assert.Contains("换号成功", attempts[1].Sse!, StringComparison.Ordinal);

        // ★ 被限流的那个号留下了「冷却」状态，而且只冷却被限的那个模型
        var limited = pool.Members.Single(m => m.Id == attempts[0].MemberId);
        Assert.True(limited.Cooldowns.ContainsKey("hy3"));
        Assert.False(limited.Cooldowns.ContainsKey("glm-5.3"),
            "冷却是按模型的 —— 一个模型被限不该把整个号拉黑");

        // 而且这个号对 hy3 已经选不出来了
        Assert.NotEqual(attempts[0].MemberId, pool.Pick("hy3", [attempts[1].MemberId])?.Id);
    }

    [Fact]
    public async Task 池层_模型级限流6004也记冷却_但换个模型仍可用()
    {
        var vault = new TestVault().Add(uid: "user-a");
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.ModelRateLimited("hy3"));

        var pool = new AccountPool(TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountPool>.Instance);
        pool.Replace(vault.Entries);

        var attempts = await new SwappingChatLoop(fake, pool, UpstreamVariant.China).RunAsync("hy3", maxAttempts: 1);

        Assert.Equal(UpstreamErrorKind.ModelRateLimited, attempts[0].Kind);

        var member = pool.Members.Single();
        Assert.True(member.Cooldowns.ContainsKey("hy3"));
        // ★ 换个模型必须还能选到同一个号 —— 否则白扔配额
        Assert.Equal(member.Id, pool.Pick("glm-5.3")?.Id);
        Assert.Null(pool.Pick("hy3"));
    }

    // ── 层 2：网关（错误对象必须存在）────────────────────────

    [Fact]
    public async Task 网关层_单号池时无处可重试_客户端拿到429错误对象()
    {
        // ⚠ **本用例原名**：`网关层_上游429时客户端拿到429错误对象_且是一次转发不重试`
        //
        // t13 改名，因为**原名里「不重试」这个描述是错的**：
        // 网关**会**重试（t8 落地了 `RunWithRetryAsync`）。
        // 本场景只是**无处可换号** —— 池里只有一个号。
        //
        // 为什么值得单独改名（而不是只改注释）：
        // **测试名是最常被人读到的一行。** 留着「不重试」这个名字，
        // 下一个人 grep 到它就会以为「网关至今不重试」，而注释他不一定读。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.TooManyRequests("账号被限流"));
        await using var e2e = await E2EHost.StartAsync(fake);

        using var response = await e2e.PostChatAsync(ChatBody());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var body = await E2EHost.JsonAsync(response);
        var error = Assert.IsType<JsonObject>(body["error"]);

        // ★ 硬要求：message 必须是**非空字符串**。
        // 旧版踩过的坑就是这里返回 undefined，调用方读 .ok 直接 TypeError。
        Assert.Equal(System.Text.Json.JsonValueKind.String, error["message"]!.GetValueKind());
        var message = error["message"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.NotEqual("undefined", message);
        Assert.False(string.IsNullOrWhiteSpace(error["type"]!.GetValue<string>()));

        // ## 这个 `== 4` 是怎么来的（t13 实测，t28 落地后）
        //
        // ⚠ 这条断言被改过两次，**两次都不是"放宽"，而是跟着实现改语义**：
        //
        // ```
        // t4 时期（无重试）        单号 → 1 次
        // t8 之后（单层换号）      单号 → 1 次（无处可换号 ⇒ 第 2 轮 Pick 返回 null）
        // t28 之后（两层：先换模型）单号 → 4 次（同一个号内试 4 个模型）
        // ```
        //
        // 现在的 `4` = `GatewayApp.ModelFallbackLimit`（内层换模型的候选上限，
        // 对齐旧版 `sendWithFallback` 的 `for (attempt = 0; attempt < 4; attempt++)`）。
        // 实测请求过的模型序列：`hy3, hy4-preview, hy3-x, deepseek-v4.1-flash`。
        //
        // ⇒ 所以**这条用例现在钉住的是「单号内层换 4 个模型后耗尽」**，
        //   而不是它原名暗示的「不重试」。换号场景见
        //   `网关层_双号池且两号都被限_回429并标注soft_rate`。
        //
        // ⚠ **别把它改成 `> 1`**：那会让它变成弱断言 ——
        //   真正的契约是**精确的 4**，不是「大于 1」。数值型断言不放大容差。
        Assert.Equal(4, fake.Count);

        // 同一个号被试了 4 次（换的是模型，不是号）
        Assert.All(fake.AuthorizationHeaders, h => Assert.Equal(fake.AuthorizationHeaders[0], h));

        // 内层确实换过模型（4 个互不相同）
        Assert.Equal(4, fake.RequestedModels.Distinct(StringComparer.Ordinal).Count());

        // ⚠ 中文断言用**解析后的字段**，不用 `Contains`：
        //    上游/网关的 JSON 序列化可能把中文转义成 \uXXXX（observability 实测过），
        //    那样 `Contains("限流")` 会误报失败，而内容其实是对的。
        Assert.Contains("限流", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 网关层_所有号都在冷却时返回429错误对象_不崩不返回_undefined()
    {
        // ## ⚠ 这条用例的触发条件被 t28 改过（不是断言被削弱）
        //
        // **原意不变**：所有号都不可用时，客户端必须拿到**明确的 429 错误对象**
        // （不是 500、不是 undefined —— 旧版就栽在返回 undefined、调用方读 .ok 抛 TypeError）。
        //
        // **但「所有号都不可用」怎么造，变了**：
        //
        // ```
        // t28 之前：只冷 "hy3" 就够了 —— 网关只会用这一个模型
        // t28 之后：内层会**同号换模型**（≤ ModelFallbackLimit=4 个候选），
        //           只冷一个模型，第 2 枪就会换到别的模型**成功** ⇒ 客户端拿到 200
        // ```
        // 实测症状：`Expected: TooManyRequests / Actual: OK`。
        //
        // ⚠ **所以修法是「把触发条件造足」，不是「放宽断言」** ——
        //   上面那几条「不许 500 / 不许 undefined / message 非空」一条都没动。
        //   删掉它们才是把有意义的测试变成没有。
        //
        // ⇒ 正确造法：**账号级限流 + 把每个号的每个候选模型都打满**，
        //   让外层（换号）和内层（换模型）**两层同时耗尽**。
        var vault = new TestVault().Add(uid: "user-a").Add(uid: "user-b");
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("先跑一次"));
        await using var e2e = await E2EHost.StartAsync(fake, vault);

        using (var warmup = await e2e.PostChatAsync(ChatBody()))
        {
            Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
        }

        var pool = e2e.Application.Services.GetRequiredService<AccountPool>();
        var catalog = e2e.Application.Services.GetRequiredService<ModelCatalog>();
        var until = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds();

        // ★ 把**每个号 × 每个目录模型**都冷掉 —— 两层都不给活路。
        //   不写死 4 个模型名：目录变了这条用例也不该失效（候选上限是可调参数，不是契约）。
        foreach (var member in pool.Members)
        {
            foreach (var model in catalog.Models)
            {
                pool.MarkRateLimited(member.Id, model.Id, until);
            }
        }

        var before = fake.Count;
        using var response = await e2e.PostChatAsync(ChatBody());

        // ★ 三不许：不许 500、不许 undefined、不许崩
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var raw = await E2EHost.TextAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(raw), "响应体不能是空的");

        var body = JsonNode.Parse(raw);
        Assert.NotNull(body);
        var error = Assert.IsType<JsonObject>(body["error"]);

        Assert.Equal(System.Text.Json.JsonValueKind.String, error["message"]!.GetValueKind());
        var message = error["message"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(message));

        // ⚠ 这条**不能删**（我改触发条件时一度删掉了它，随即补回）。
        //
        // 它钉的是「**没号可发**」这条路径的文案：
        //   `GatewayApp.cs:1041-1042` → "没有可用账号（…正在冷却中…）"
        // 而不是「号都试过了」那条（`:1050` → "试了 N 个账号都被上游限流"）。
        //
        // 两条路径**语义不同**，客户端/日志靠文案区分：
        //   · 没号可发      → 请求压根没出网（本用例断言 `fake.Count` 不变）
        //   · 试光后耗尽    → 出了网、被限了（见上面那条 429 用例）
        // ⇒ 把文案断言删掉，这两条路径就分不出来了。
        Assert.Contains("没有可用账号", message, StringComparison.Ordinal);

        Assert.Equal("soft_rate", error["type"]!.GetValue<string>());
        Assert.Equal("soft_rate", error["code"]!.GetValue<string>());

        // 一个号都没有可用 —— 压根不该再打上游
        Assert.Equal(before, fake.Count);

        // 宿主还活着：紧接着的合法请求照样能成（证明没把进程搞坏）
        pool.Unfreeze(pool.Members[0].Id);
        using var after = await e2e.PostChatAsync(ChatBody());
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task 网关层_所有号都在冷却时流式请求也返回429错误对象()
    {
        // 流式路径的诱惑是「响应头已经发出去了、只能吐半截」。
        // 但选号发生在写响应之前 —— 所以这里必须是一个**规规矩矩的 429 JSON**。
        //
        // ## ⚠ 触发条件被 t28 改过（与上面那条同因，断言**一条未动**）
        //
        // 原来只冷 `"hy3"` 就够；t28 之后内层会**同号换模型**把第 2 枪救回来，
        // 于是客户端拿到 200。修法是**把两层都耗尽**，不是放宽断言。
        var vault = new TestVault().Add(uid: "user-a");
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("预热"));
        await using var e2e = await E2EHost.StartAsync(fake, vault);

        using (await e2e.PostChatAsync(ChatBody(stream: true))) { }

        var pool = e2e.Application.Services.GetRequiredService<AccountPool>();
        var catalog = e2e.Application.Services.GetRequiredService<ModelCatalog>();
        var until = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds();

        // ★ 单号 × 全部目录模型都冷掉 —— 内层没有可换的，外层也只有这一个号
        foreach (var model in catalog.Models)
        {
            pool.MarkRateLimited(pool.Members[0].Id, model.Id, until);
        }

        using var response = await e2e.PostChatAsync(ChatBody(stream: true));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
        Assert.Contains("application/json", contentType, StringComparison.Ordinal);
        Assert.DoesNotContain("text/event-stream", contentType, StringComparison.Ordinal);

        var body = await E2EHost.JsonAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(body["error"]!["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task 网关层_上游429会把该号该模型标进冷却()
    {
        var vault = new TestVault().Add(uid: "user-a");
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.TooManyRequests());
        await using var e2e = await E2EHost.StartAsync(fake, vault);

        using var response = await e2e.PostChatAsync(ChatBody("hy3"));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var pool = e2e.Application.Services.GetRequiredService<AccountPool>();
        var member = Assert.Single(pool.Members);

        Assert.True(member.Cooldowns.ContainsKey("hy3"), "429 之后该号的 hy3 必须进冷却");
        Assert.False(member.Cooldowns.ContainsKey("kimi-k2.7"), "别的模型不该被牵连");

        // 冷却之后同一个模型选不出这个号 —— 这就是「换号」的**前提条件**
        Assert.Null(pool.Pick("hy3"));
        Assert.NotNull(pool.Pick("kimi-k2.7"));
    }

    [Fact]
    public async Task 网关层_一个号都没有时返回401而不是500()
    {
        // 保险库是空的（用户还没导入账号）。这是**配置问题**不是服务器故障 ——
        // 回 500 会让用户以为是网关坏了。
        var fake = new FakeUpstreamHandler(_ => StreamScript.TextStream("不该走到"));
        await using var e2e = await E2EHost.StartAsync(fake, new TestVault());

        using var response = await e2e.PostChatAsync(ChatBody("hy3"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await E2EHost.JsonAsync(response);
        Assert.Equal("not_signed_in", body["error"]!["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(body["error"]!["message"]!.GetValue<string>()));
        Assert.Equal(0, fake.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 401)]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.InternalServerError, 502)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 502)]
    [InlineData(HttpStatusCode.BadGateway, 502)]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    public async Task 网关层_上游各种失败都映射成带错误对象的客户端状态码(HttpStatusCode upstream, int expected)
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text(
            """{"code":500,"message":"上游不高兴"}""", upstream));
        await using var e2e = await E2EHost.StartAsync(fake);

        using var response = await e2e.PostChatAsync(ChatBody());

        Assert.Equal(expected, (int)response.StatusCode);

        var body = await E2EHost.JsonAsync(response);
        var error = Assert.IsType<JsonObject>(body["error"]);
        Assert.Equal(System.Text.Json.JsonValueKind.String, error["message"]!.GetValueKind());
        Assert.False(string.IsNullOrWhiteSpace(error["message"]!.GetValue<string>()));
    }

    // ── 层 3：网关层的换号重试 ────────────────────────────────

    // ## 这条用例的两次修订（值得留着，因为它记录了两层语义）
    //
    // ```
    // t13 之前：Skip（等 t8）
    // t8 之后：启用，脚本「首号 1 次 429 → 次号成功」，断言 Count==2 且换号   ← 当时对
    // t28 之后：脚本必须改成「首号 4 个模型全限 → 次号成功」，因为换模型优先 ← 本版
    // ```
    //
    // **为什么 t8 版本的脚本在 t28 之后不再成立**：
    // t28 给重试加了**内层换模型**（上限 `ModelFallbackLimit`=4）。
    // 于是「首号被限」之后，网关**先在该号内换 4 个模型**，而不是立刻换号。
    // 原来的脚本只让首号失败 1 次，第 2 枪换的其实是**模型**（还是同一个号）——
    // 所以 `NotEqual(Authorization[0], Authorization[1])` 会红。
    [Fact]
    public async Task 网关层_上游429时自动换号并重试到成功()
    {
        // 这一条就是 HANDOVER P1 承诺的形状：「某个号被限流时自动换号，用户无感」。
        //
        // ## 脚本（按 t28 的两层语义设计）
        //
        //   号 A 的 4 个模型全被限流（内层换模型 4 枪全败）
        //   → 外层换到号 B，第一个模型就成功
        //
        // ## 预期（同时验「换模型优先」与「换号兜底」两件事）
        //   ① 客户端看到 200，而不是 429
        //   ② 上游共收到 **5** 次请求 = 号 A 的 4 个模型 + 号 B 的 1 次
        //   ③ **前 4 次的 Authorization 相同**（号 A 内换了 4 个模型）
        //   ④ **第 5 次的 Authorization 不同**（真的换号了）
        //   ⑤ 客户端拿到的正文来自最后一次请求
        var vault = new TestVault()
            .Add(uid: "user-a", label: "被限流的号")
            .Add(uid: "user-b", label: "正常的号");

        // 前 4 次 429（号 A 的 4 个模型），第 5 次成功（号 B）
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.ServerError("兜底，不该走到"));
        fake.Script(
            UpstreamResponses.TooManyRequests("账号被限流"),   // 号 A 模型 1
            UpstreamResponses.TooManyRequests("账号被限流"),   // 号 A 模型 2
            UpstreamResponses.TooManyRequests("账号被限流"),   // 号 A 模型 3
            UpstreamResponses.TooManyRequests("账号被限流"),   // 号 A 模型 4
            UpstreamResponses.SseText(                         // 号 B：成功
                StreamScript.RoleFrame(),
                StreamScript.ContentFrame("换号之后成功"),
                StreamScript.StopFrame()));

        await using var e2e = await E2EHost.StartAsync(fake, vault);

        using var response = await e2e.PostChatAsync(ChatBody(stream: false));

        // ① 用户无感：拿到 200
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ② 4 枪换模型 + 1 枪换号 —— 精确 5
        Assert.Equal(5, fake.Count);

        // ③ 前 4 次是同一个号（内层换的是模型，不是号）
        var authors = fake.AuthorizationHeaders;
        Assert.Single(authors.Take(4).Distinct(StringComparer.Ordinal));

        // ④ 第 5 次真的换了号
        Assert.NotEqual(authors[0], authors[4]);

        // ⑤ 正文来自最后一次（成功的那次）
        var content = (await E2EHost.JsonAsync(response))["choices"]!.AsArray()[0]!["message"]!["content"]!.GetValue<string>();
        Assert.Equal("换号之后成功", content);
    }

    [Fact]
    public async Task 网关层_双号池且两号都被限_回429并标注soft_rate()
    {
        // ## 这条补的是什么
        //
        // 上面的单号用例验的是「无处可重试」；`自动换号并重试到成功` 验的是「换号成功」。
        // **缺的是中间那一段：号都试过了、全都失败 —— 此时客户端应该看到什么？**
        //
        // ## 契约来源（t8 确立，非我推断）
        //
        // 旧版 `workbuddy_api/src/gateway.js:475` 在「所有号都在限流冷却中」时回的是
        // `{status: 429, kind: "soft_rate"}` —— 那是**客户端可见契约**，t8 保持了它。
        // ⚠ 注意 kind 是 `soft_rate` 而**不是** `rate_limit`：
        //   前者是网关自己的话（「我这边没号可用了」），
        //   后者是上游的话（「上游限流了」）。混用会让客户端把两种情况当成一种。
        //
        // ## 断言要点（t13 实测，t28 落地后）
        //
        // t28 把重试改成了**两层**：外层换号（≤`MaxUpstreamAttempts`=3）、
        // 内层换模型（≤`ModelFallbackLimit`=4）。所以两个号耗尽时：
        //
        //     请求序列 = [号A × 4 个模型] + [号B × 4 个模型] = 8 次
        //
        // ① 两号 × 4 模型 ⇒ `fake.Count == 8`（**精确**，不是「> 1」）
        // ② 前 4 次用同一个号、后 4 次换成另一个号（**真的换号**）
        // ③ 客户端拿到 429（不是 500、不是 undefined）
        // ④ `type`/`code` 都是 `soft_rate`，`message` 非空
        // ⑤ 两个号都被记了冷却（下次请求不会再撞它们）
        var vault = new TestVault()
            .Add(uid: "user-a", label: "被限流的号 A")
            .Add(uid: "user-b", label: "被限流的号 B");

        // 假上游**一律回 429** —— 两个号都会撞上
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.TooManyRequests("账号被限流"));
        await using var e2e = await E2EHost.StartAsync(fake, vault);

        using var response = await e2e.PostChatAsync(ChatBody());

        // ① 两号 × 4 模型 —— 精确相等
        Assert.Equal(8, fake.Count);

        // ② 前 4 次是同一个号（内层换的是**模型**、不是号）、后 4 次换成另一个号
        //
        // ⚠ 注意断言方向：这里要的是「**恰好 1 个**不同的 token」——
        //    前 4 枪共用同一个号。写成 `Assert.Equal(4, ...Distinct().Count())`
        //    是把「4 次请求」和「4 个不同 token」搞混了（我第一版就写错了）。
        var authors = fake.AuthorizationHeaders;
        Assert.Single(authors.Take(4).Distinct(StringComparer.Ordinal));
        Assert.Single(authors.Skip(4).Take(4).Distinct(StringComparer.Ordinal));

        // 而且两半用的是**不同**的号（真的换了号）
        Assert.NotEqual(authors[0], authors[4]);

        // 前 4 枪换了 4 个不同模型（内层确实在换模型）
        Assert.Equal(4, fake.RequestedModels.Take(4).Distinct(StringComparer.Ordinal).Count());

        // ③ 客户端侧：明确的 429，不是 500、不是 undefined
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var body = await E2EHost.JsonAsync(response);
        var error = Assert.IsType<JsonObject>(body["error"]);
        Assert.Equal(System.Text.Json.JsonValueKind.String, error["message"]!.GetValueKind());
        Assert.False(string.IsNullOrWhiteSpace(error["message"]!.GetValue<string>()));

        // ④ 耗尽后的 kind 是 soft_rate（网关自己的话），不是 rate_limit（上游的话）
        Assert.Equal("soft_rate", error["type"]!.GetValue<string>());
        Assert.Equal("soft_rate", error["code"]!.GetValue<string>());

        // ⚠ 中文断言走**解析后的字段**，不用 `Contains`（序列化可能转义成 \uXXXX）
        Assert.Contains("账号", error["message"]!.GetValue<string>(), StringComparison.Ordinal);

        // ⑤ 两个号都被记了冷却 —— 否则下次请求还会撞它们
        var pool = e2e.Application.Services.GetRequiredService<AccountPool>();
        Assert.All(pool.Members, m =>
            Assert.True(m.Cooldowns.ContainsKey("hy3"),
                $"号 {m.Id} 被 429 之后应该进冷却，否则下次还会撞上"));
    }

    // ── 客户端取消 ────────────────────────────────────────────

    [Fact]
    public async Task 客户端取消_取消中的请求不产生500也不产生未处理异常()
    {
        // 客户端按了「停止生成」就断开连接。这不是错误 —— 网关不该
        // 往已经关掉的响应里写东西，也不该把异常漏到宿主外面。
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var fake = new FakeUpstreamHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NeverEndingStream(started, release.Task)),
        });

        await using var e2e = await E2EHost.StartAsync(fake);

        using var cts = new CancellationTokenSource();

        var sendTask = e2e.PostChatAsync(ChatBody(stream: true),
            completion: HttpCompletionOption.ResponseHeadersRead, ct: cts.Token);

        // 等「上游已经开始吐第一片」—— 那是唯一的确定性信号，不猜时间
        using (var response = await sendTask)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStreamAsync();

            // 上游吐了第一片、然后卡住不动；此刻断开
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();

            // 断流本身可能抛（取决于内核看到 FIN 的时机）—— 那是**客户端侧**的正常结果
            try
            {
                var stream = await response.Content.ReadAsStreamAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var buffer = new byte[256];
                await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException
                                           or TimeoutException or ObjectDisposedException)
            {
                // 正常：连接被取消了
            }
        }

        // 放行上游，让它收尾（不然宿主 Dispose 时会卡）
        release.TrySetResult();

        // ★ 网关还活着：紧接着一个正常请求必须成功
        using var afterCancel = await e2e.PostChatAsync(ChatBody(stream: false));
        Assert.Equal(HttpStatusCode.OK, afterCancel.StatusCode);

        // ★ 宿主日志里不许出现「未处理异常」这类崩坏痕迹
        var log = e2e.DumpLog();
        Assert.DoesNotContain("Unhandled exception", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.NullReferenceException", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 客户端取消_请求未完成就取消不留半截错误响应()
    {
        // 第二条取消路径：**响应头都还没回来**就取消（上游还在建立/思考）。
        // 这种取消必须表现为「客户端拿不到响应」，而不是「网关回了 500」。
        var release = new TaskCompletionSource();
        var fake = new FakeUpstreamHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new DelayedStream(release.Task, StreamScript.TextStreamRaw("迟到"))),
        });

        await using var e2e = await E2EHost.StartAsync(fake);

        using var cts = new CancellationTokenSource();
        var sendTask = e2e.PostChatAsync(ChatBody(stream: true), ct: cts.Token);

        cts.Cancel();

        // 取消之后 sendTask 要么抛（连接层），要么拿到一个响应；
        // **唯一不可接受的是网关回了 5xx**。
        try
        {
            using var response = await sendTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True((int)response.StatusCode < 500,
                $"客户端取消不该产生 5xx，实际 {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or HttpRequestException or IOException)
        {
            // 客户端侧取消 —— 预期之内
        }
        finally
        {
            release.TrySetResult();
        }

        using var afterCancel = await e2e.PostChatAsync(ChatBody(stream: false));
        Assert.Equal(HttpStatusCode.OK, afterCancel.StatusCode);
    }

    // ── 上游流中途断掉 ────────────────────────────────────────

    [Fact]
    public async Task 上游中途断流_流式路径不崩且已收到的内容照常给到客户端()
    {
        // 上游吐了一半就关流（没有 [DONE]）。响应头已经发出去了，状态码改不了。
        //
        // 这里钉住的是**最低要求**：不崩、不 5xx、已经收到的内容照常发给客户端，
        // 而且客户端不会永远挂在那里（连接会正常结束，读到 EOF）。
        //
        // 注：网关目前不会补一个 error 帧 —— 因为「上游正常关流但没发 [DONE]」
        // 和「真断了」在 TCP 层是同一个现象，无法可靠区分。SseReader 的处理是
        // 把攒着的那条吐出来然后正常结束（见其末尾注释）。
        await using var e2e = await E2EHost.StartAsync(_ => UpstreamResponses.SseBroken(
            StreamScript.RoleFrame(),
            StreamScript.ContentFrame("吐了一半")));

        using var response = await e2e.PostChatAsync(ChatBody(stream: true),
            completion: HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 能在有上限的时间内读完（不是永远挂着）
        var body = await E2EHost.TextAsync(response).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Contains("吐了一半", body, StringComparison.Ordinal);

        // 网关还活着
        using var after = await e2e.PostChatAsync(ChatBody(stream: false));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.DoesNotContain("Unhandled exception", e2e.DumpLog(), StringComparison.OrdinalIgnoreCase);
    }

    // ── 辅助流 ────────────────────────────────────────────────

    /// <summary>吐一片合法的 SSE 之后**永远不结束**的流（等测试放行）。</summary>
    private sealed class NeverEndingStream(TaskCompletionSource started, Task release) : Stream
    {
        private readonly byte[] _prelude = System.Text.Encoding.UTF8.GetBytes(
            $"data: {StreamScript.RoleFrame()}\n\n");
        private int _offset;
        private bool _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent)
            {
                var take = Math.Min(buffer.Length, _prelude.Length - _offset);
                _prelude.AsSpan(_offset, take).CopyTo(buffer.Span);
                _offset += take;
                if (_offset >= _prelude.Length)
                {
                    _sent = true;
                    started.TrySetResult();
                }
                return take;
            }

            // 一直卡着 —— 客户端此刻断开
            await release.WaitAsync(ct).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>等放行之后才开始吐内容的流 —— 用来制造「响应头还没回来就取消」。</summary>
    private sealed class DelayedStream(Task release, string body) : Stream
    {
        private readonly byte[] _bytes = System.Text.Encoding.UTF8.GetBytes(body);
        private int _offset;
        private bool _released;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_released)
            {
                await release.WaitAsync(ct).ConfigureAwait(false);
                _released = true;
            }

            if (_offset >= _bytes.Length) return 0;

            var take = Math.Min(buffer.Length, _bytes.Length - _offset);
            _bytes.AsSpan(_offset, take).CopyTo(buffer.Span);
            _offset += take;
            return take;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
