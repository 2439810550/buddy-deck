using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 模型漂移的回归测试（t11）。
///
/// ## 这个 bug 是什么
///
/// 客户端写死一个我们不认识的别名（`gpt-4o`）时，网关把「选号用的模型」
/// 落到了默认模型（`hy3`），但**请求体里的 model 没跟着落** ——
/// 于是同一个请求里有两个模型名：
///   · 选号 / 冷却记账：hy3
///   · 上游实际收到：gpt-4o
///
/// ## 为什么这几条断言值得单独存在
///
/// 第 2 条（两本账）是最要命的：429 时网关按 `hy3` 记冷却，而真被限的是
/// `gpt-4o` —— **用户真正想用的 hy3 白进冷却最长 5 分钟**。
/// 这正是 HANDOVER 坑 4（冷却是按模型分的）要避免的配额浪费。
/// 光断言「上游收到 hy3」抓不到它，必须**同时**断言冷却记的是同一个名字。
/// </summary>
public class ModelDriftTests
{
    private const string BodyWithAlias = """{"model":"gpt-4o","messages":[{"role":"user","content":"你好"}],"stream":false}""";
    private const string BodyWithKnown = """{"model":"hy3","messages":[{"role":"user","content":"你好"}],"stream":false}""";

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static HttpResponseMessage OkSse() => UpstreamResponses.SseText(
        """{"id":"c1","object":"chat.completion.chunk","model":"hy3","choices":[{"index":0,"delta":{"content":"好"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""");

    private static FakeUpstreamHandler Fake() => new(OkSse());

    // ── ① 管线契约：model = null 必须完全不动 body ─────────────

    [Fact]
    public void 管线不传_model_时原样保留_body_里的模型()
    {
        // ★ 这是队长点名的硬要求：`Prepare` 是 Core 公开 API，
        //   别的调用方可能只想做 stream/角色/tool_choice 归一。
        //   若 null 被当成「清空模型」，它们会**静默**丢模型，
        //   而且编译期完全看不出来（可选参数，不传就是 null）。
        var source = """{"model":"gpt-4o","messages":[{"role":"user","content":"hi"}]}""";

        var result = RequestPipeline.Prepare(UpstreamVariant.China, source);

        var body = JsonNode.Parse(result)!.AsObject();
        Assert.Equal("gpt-4o", body["model"]!.GetValue<string>());
    }

    [Fact]
    public void 管线显式传_null_时也原样保留()
    {
        var source = """{"model":"claude-3-5-sonnet","messages":[]}""";

        var result = RequestPipeline.Prepare(UpstreamVariant.China, source, null);

        var body = JsonNode.Parse(result)!.AsObject();
        Assert.Equal("claude-3-5-sonnet", body["model"]!.GetValue<string>());
    }

    [Fact]
    public void 管线传空字符串时视为没指定_不删模型()
    {
        // "" 是「调用方没给」，不是「把模型删掉」—— 两回事。
        var source = """{"model":"gpt-4o","messages":[]}""";

        var result = RequestPipeline.Prepare(UpstreamVariant.China, source, "");

        var body = JsonNode.Parse(result)!.AsObject();
        Assert.Equal("gpt-4o", body["model"]!.GetValue<string>());
    }

    [Fact]
    public void 管线传了模型时改写_body()
    {
        var source = """{"model":"gpt-4o","messages":[]}""";

        var result = RequestPipeline.Prepare(UpstreamVariant.China, source, "hy3");

        var body = JsonNode.Parse(result)!.AsObject();
        Assert.Equal("hy3", body["model"]!.GetValue<string>());
    }

    [Fact]
    public void 管线传了模型时其余改写照旧生效()
    {
        // 改写模型不能把管线的其它职责（强制 stream / 角色归一）弄丢。
        var source = """{"model":"gpt-4o","messages":[{"role":"developer","content":"规矩"}],"stream":false}""";

        var result = RequestPipeline.Prepare(UpstreamVariant.China, source, "hy3");

        var body = JsonNode.Parse(result)!.AsObject();
        Assert.Equal("hy3", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal("system", body["messages"]!.AsArray()[0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void 没有_model_字段的_body_也能被补上模型()
    {
        var source = """{"messages":[{"role":"user","content":"hi"}]}""";

        var result = RequestPipeline.Prepare(UpstreamVariant.China, source, "hy3");

        var body = JsonNode.Parse(result)!.AsObject();
        Assert.Equal("hy3", body["model"]!.GetValue<string>());
    }

    [Fact]
    public void 畸形_JSON_时原样返回_不因改写模型而崩()
    {
        var result = RequestPipeline.Prepare(UpstreamVariant.China, "{不是 JSON", "hy3");

        Assert.Equal("{不是 JSON", result);
    }

    // ── ② 端到端：别名必须落到默认模型发出去 ───────────────────

    [Fact]
    public async Task 未知名模型_发给上游的_body_是默认模型()
    {
        var fake = Fake();
        var vault = new TestVault().AddMany(1);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var response = await host.Client.PostAsync("/v1/chat/completions", Json(BodyWithAlias));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sent = JsonNode.Parse(fake.Requests.Single().Body!)!.AsObject();
        Assert.Equal("hy3", sent["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task 认识模型_原样发给上游()
    {
        var fake = Fake();
        var vault = new TestVault().AddMany(1);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        await host.Client.PostAsync("/v1/chat/completions", Json(BodyWithKnown));

        var sent = JsonNode.Parse(fake.Requests.Single().Body!)!.AsObject();
        Assert.Equal("hy3", sent["model"]!.GetValue<string>());
    }

    // ── ③ 两本账必须对上（最要命的一条）────────────────────────

    [Fact]
    public async Task 限流冷却记的模型_与上游实际收到的模型一致()
    {
        // ★★ 这条是 t11 的核心价值所在（t28 后仍成立，只是要按新链路读）。
        //
        // bug 版：请求发 gpt-4o，冷却却记 hy3 →
        //         把用户真正想用的 hy3 白白拉黑 5 分钟。
        // 只断言「上游收到 hy3」抓不到它，必须**两边同时断言**。
        //
        // ## ⚠ t28 之后的语义变更
        //
        // 原来用 `fake.Requests.Single()` —— 因为那时一枪就定局。
        // **t28 加了内层换模型**后，持续 429 会让内层把候选模型逐个试完，
        // 所以会有多条请求、多个不同的模型各自进冷却。
        //
        // 断言改成：**每一条发出去的模型，都要在冷却表里找到对应项** ——
        // 这比原来更强：原版只验了「第一条」，现在验了「每一条」。
        // 而「别名绝不能进冷却表」这条核心不变量原样保留。
        var fake = Fake();
        fake.Script(UpstreamResponses.TooManyRequests());

        var vault = new TestVault().AddMany(1);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var pool = host.Application.Services.GetRequiredService<AccountPool>();
        pool.Replace(vault.Entries);

        using var response = await host.Client.PostAsync("/v1/chat/completions", Json(BodyWithAlias));
        await response.Content.ReadAsStringAsync();

        var member = pool.Members.Single();

        // ★ 每一条实际发出去的模型，都必须能在冷却表里找到（两本账逐条对上）
        var sentModels = fake.Requests
            .Select(r => JsonNode.Parse(r.Body!)!.AsObject()["model"]!.GetValue<string>())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(sentModels);
        foreach (var model in sentModels)
        {
            Assert.True(member.Cooldowns.ContainsKey(model),
                $"「{model}」发出去过并被限流，却没进冷却；实际记的是：{string.Join(",", member.Cooldowns.Keys)}");
        }

        // ★★ 核心不变量：客户端写的别名（gpt-4o）绝不能被当成记账对象。
        //   它从来没被发出去过，凭什么进账本？记到它头上等于「用户想用的模型被拉黑」。
        Assert.DoesNotContain("gpt-4o", member.Cooldowns.Keys);

        // 而且发出去的每一条都**不是**那个别名（t11 的原始修复点）
        Assert.DoesNotContain("gpt-4o", sentModels);
        Assert.Equal("hy3", sentModels[0]);   // 第一枪就是落下来的默认模型
    }

    [Fact]
    public async Task 别名请求被限流后_默认模型本身进冷却而非别名()
    {
        // 与上一条互补：从「别名」这个角度看，它根本不该出现在任何账本里。
        var fake = Fake();
        fake.Script(UpstreamResponses.TooManyRequests());

        var vault = new TestVault().AddMany(1);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var pool = host.Application.Services.GetRequiredService<AccountPool>();
        pool.Replace(vault.Entries);

        using var response = await host.Client.PostAsync("/v1/chat/completions", Json(BodyWithAlias));
        await response.Content.ReadAsStringAsync();

        Assert.All(pool.Members, m => Assert.DoesNotContain("gpt-4o", m.Cooldowns.Keys));
    }

    [Fact]
    public async Task 观察者看到的模型_就是实际发出去的那个()
    {
        // 指标 / 脱敏日志据此统计「用的是哪个模型」—— 记错等于统计失真。
        var fake = Fake();
        var recorder = new RecordingObserver();
        var vault = new TestVault().AddMany(1);

        await using var host = await GatewayHost.StartReadyAsync(fake, vault,
            configure: services => services.AddSingleton<IChatObserver>(recorder));

        await host.Client.PostAsync("/v1/chat/completions", Json(BodyWithAlias));

        var evt = Assert.Single(recorder.Events);
        Assert.Equal("hy3", evt.UsedModel);
        // requestedModel 保留客户端原本写的，便于排查「他想用什么」
        Assert.Equal("gpt-4o", evt.RequestedModel);
    }

    // ── ④ 请求 / 响应两边模型名自洽 ───────────────────────────

    [Fact]
    public async Task 响应里的模型名_与请求发出去的一致()
    {
        // t11 之前：请求发 gpt-4o、响应被改写成 hy3 → 同一请求两个名字。
        // 现在两边都该是 hy3。
        var fake = Fake();
        var vault = new TestVault().AddMany(1);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        using var response = await host.Client.PostAsync("/v1/chat/completions", Json(BodyWithAlias));
        var returned = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        var sentModel = JsonNode.Parse(fake.Requests.Single().Body!)!.AsObject()["model"]!.GetValue<string>();

        Assert.Equal(sentModel, returned["model"]!.GetValue<string>());
        Assert.Equal("hy3", returned["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task 流式响应里的模型名_也与请求发出去的一致()
    {
        var fake = Fake();
        var vault = new TestVault().AddMany(1);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        var streamBody = """{"model":"gpt-4o","messages":[{"role":"user","content":"你好"}],"stream":true}""";
        using var response = await host.Client.PostAsync("/v1/chat/completions", Json(streamBody));

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"model\":\"hy3\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"model\":\"gpt-4o\"", text, StringComparison.Ordinal);
    }

    // ── ⑤ 上游不认识的别名绝不能泄漏出去 ──────────────────────

    [Fact]
    public async Task 任何情况下别名都不会出现在发给上游的请求体里()
    {
        var fake = Fake();
        var vault = new TestVault().AddMany(2);
        await using var host = await GatewayHost.StartReadyAsync(fake, vault);

        foreach (var alias in new[] { "gpt-4o", "claude-3-5-sonnet", "qwen-max", "完全不存在的模型" })
        {
            var body = $$"""{"model":"{{alias}}","messages":[{"role":"user","content":"hi"}],"stream":false}""";
            using var response = await host.Client.PostAsync("/v1/chat/completions", Json(body));
            await response.Content.ReadAsStringAsync();
        }

        Assert.All(fake.Requests, r =>
        {
            var sent = JsonNode.Parse(r.Body!)!.AsObject()["model"]!.GetValue<string>();
            Assert.Equal("hy3", sent);
        });
    }
}
