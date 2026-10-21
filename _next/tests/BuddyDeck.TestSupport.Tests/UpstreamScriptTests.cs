using BuddyDeck.Core;
using BuddyDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.TestSupport.Tests;

/// <summary>
/// 脚本队列的测试 —— 重点是那条**验收场景**：
///
/// ```
/// 第一次 429（账号级限流）→ 换号 → 第二次 200
/// ```
///
/// 这条路径是账号池存在的全部理由。如果夹具连它都跑不通，
/// gateway 和 e2e 两边就没有可用的「上游会变」的模拟手段。
/// </summary>
public class UpstreamScriptTests
{
    /// <summary>一条正常的上游 SSE 分片（含 content）。</summary>
    private static string Chunk(string content, string model = "hy3")
        => "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"model\":\"" + model
           + "\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + content + "\"}}]}";

    // ── 验收场景 ──────────────────────────────────────────────

    [Fact]
    public async Task 首次_429_换号后再次_200_完整跑通()
    {
        // ── 场景装配 ────────────────────────────────────────────
        // 两个账号：第一个被限流、第二个正常。
        var vault = new TestVault()
            .Add(uid: "user-a", label: "被限流的号")
            .Add(uid: "user-b", label: "正常的号");

        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.ServerError("兜底（不该走到）"));

        // ★ 脚本：第 1 次请求 429，第 2 次请求（换了号）200 + SSE
        fake.Script(
            UpstreamResponses.TooManyRequests("账号被限流", resetAtMs: DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds()),
            UpstreamResponses.SseText(Chunk("换号成功")));

        var pool = new AccountPool(TimeProvider.System, NullLogger<AccountPool>.Instance);
        pool.Replace(vault.Entries);

        // ── 跑 ──────────────────────────────────────────────────
        var loop = new SwappingChatLoop(fake, pool, UpstreamVariant.China);
        var attempts = await loop.RunAsync("hy3");

        // ── 断言：两轮，第一轮限流、第二轮成功 ─────────────────
        Assert.Equal(2, attempts.Count);

        Assert.False(attempts[0].Ok);
        Assert.Equal(429, attempts[0].Status);
        Assert.Equal(UpstreamErrorKind.RateLimited, attempts[0].Kind);

        Assert.True(attempts[1].Ok);
        Assert.Equal(200, attempts[1].Status);
        Assert.Contains("换号成功", attempts[1].Sse);

        // ── 断言：真的换了号 ───────────────────────────────────
        Assert.NotEqual(attempts[0].MemberId, attempts[1].MemberId);

        // ── 断言：上游真的收到了两次请求，而且带的是两个不同的 token ──
        Assert.Equal(2, fake.Count);
        Assert.Equal(["/v2/chat/completions", "/v2/chat/completions"], fake.Paths);

        var authorizations = fake.AuthorizationHeaders;
        Assert.Equal("Bearer access-token-1", authorizations[0]);
        Assert.Equal("Bearer access-token-2", authorizations[1]);

        // 请求体里带的 tag 也换人了（这是 SwappingChatLoop 的 bodyFactory 加的）
        Assert.Contains("user-a:", fake.Requests[0].Body);
        Assert.Contains("user-b:", fake.Requests[1].Body);

        // ── 断言：被限流的那个号进了冷却，Pick 会跳过它、选中另一个 ──
        var cooled = pool.Members.Single(m => m.Id == attempts[0].MemberId);
        Assert.True(cooled.Cooldowns.ContainsKey("hy3"), "被限流的号应该进冷却");

        var next = pool.Pick("hy3");
        Assert.NotNull(next);
        Assert.NotEqual(attempts[0].MemberId, next!.Id);
    }

    [Fact]
    public async Task 脚本用完之后一直用最后一步()
    {
        var vault = new TestVault().Add();
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));

        // 只写两步：第一步 429，之后一直 200
        fake.Script(
            UpstreamResponses.TooManyRequests(),
            UpstreamResponses.SseText(Chunk("ok")));

        var script = new UpstreamScript(
            _ => UpstreamResponses.TooManyRequests(),
            _ => UpstreamResponses.SseText(Chunk("ok")));

        // 直接验 StepAt 的越界行为
        Assert.Equal(2, script.Length);
        Assert.NotNull(script.Next(0));
        Assert.NotNull(script.Next(1));
        Assert.Same(script.Next(1), script.Next(99));   // 越界 → 最后一步

        await Task.CompletedTask;
    }

    [Fact]
    public async Task 每一步可以按请求内容分支()
    {
        // 有时脚本的一步要根据「这次请求带的是哪个号」来决定回什么 —— 
        // 那正是 Func<CapturedRequest, HttpResponseMessage> 这个签名的用处
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            request => request.HeaderOrNull("Authorization") == "Bearer access-token-1"
                ? UpstreamResponses.ModelRateLimited("hy3")     // 1 号对 hy3 被限
                : UpstreamResponses.SseText(Chunk("好的")));

        var client = new UpstreamClient(fake.CreateClient(), UpstreamVariant.China, NullLogger<UpstreamClient>.Instance);
        var vault = new TestVault().Add().Add();

        var first = await client.ChatStreamAsync(vault.Entries[0].ToCredential(), "{}", null, null, CancellationToken.None);
        var second = await client.ChatStreamAsync(vault.Entries[1].ToCredential(), "{}", null, null, CancellationToken.None);

        Assert.Equal(UpstreamErrorKind.ModelRateLimited, first.Kind);
        Assert.True(second.Ok);
        second.Response!.Dispose();
    }

    [Fact]
    public async Task 没人可用时返回空列表而不是死循环()
    {
        var vault = new TestVault().Add();
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.TooManyRequests());

        var pool = new AccountPool(TimeProvider.System, NullLogger<AccountPool>.Instance);
        pool.Replace(vault.Entries);

        var loop = new SwappingChatLoop(fake, pool, UpstreamVariant.China);
        var attempts = await loop.RunAsync("hy3", maxAttempts: 10);

        // 一个号被限之后进冷却 → 第二轮就 Pick 不出号了 → 循环结束
        Assert.Single(attempts);
        Assert.False(attempts[0].Ok);
        Assert.Equal(1, fake.Count);
    }

    [Fact]
    public async Task 模型级_6004_限流也会换号_但冷却只记在模型上()
    {
        var vault = new TestVault().Add().Add();
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.TooManyRequests());
        fake.Script(
            UpstreamResponses.ModelRateLimited("hy3"),
            UpstreamResponses.SseText(Chunk("换个号就好了")));

        var pool = new AccountPool(TimeProvider.System, NullLogger<AccountPool>.Instance);
        pool.Replace(vault.Entries);

        var attempts = await new SwappingChatLoop(fake, pool, UpstreamVariant.China).RunAsync("hy3");

        Assert.Equal(2, attempts.Count);
        Assert.Equal(UpstreamErrorKind.ModelRateLimited, attempts[0].Kind);
        Assert.True(attempts[1].Ok);

        // 冷却是按模型存的
        var cooled = pool.Members.Single(m => m.Id == attempts[0].MemberId);
        Assert.True(cooled.Cooldowns.ContainsKey("hy3"));
    }
}
