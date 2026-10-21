using System.Net;
using BuddyDeck.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// t79 的回归测试：**连接层失败不该冻账号**。
///
/// ## 缺陷现场（真机实测，captain 排查代理问题时发现）
///
/// ```
/// 本机系统代理未生效 → 所有请求连接层失败（http 0）
/// → 被归类成 server 错误 → 触发账号冷冻
/// → 8 个健康账号在几十秒内全被冷冻
/// → 之后所有请求返回 429「没有可用账号」
/// → 界面上完全看不出是环境问题
/// ```
///
/// **不可自愈**（冷冻要手动解）。所以这是一条必须钉死的边界：
/// **环境问题不能惩罚账号。**
/// </summary>
public class TransportFailureTests
{
    /// <summary>造一条账号（照 AccountPoolTests 的形状，不引 TestSupport）。</summary>
    private static VaultEntry Entry(string id, string label = "")
        => new()
        {
            Id = id,
            AccessToken = $"tok-{id}",
            Uid = $"u-{id}",
            Label = label,
            ExpiresAtMs = 0,
        };

    private static (AccountPool Pool, VaultEntry[] Entries) MakePool(int accounts = 1)
    {
        var entries = Enumerable.Range(1, accounts)
            .Select(i => Entry($"acct-{i}", $"号 {i}"))
            .ToArray();

        var pool = new AccountPool(TimeProvider.System, NullLogger<AccountPool>.Instance);
        pool.Replace(entries);
        return (pool, entries);
    }

    // ══ ① 判据本身 ═══════════════════════════════════════════════

    [Fact]
    public void status_0_是传输失败()
    {
        // 0 是 UpstreamClient 的哨兵值 —— 不是 HTTP 状态码。
        // 它由 SendAsync 在 DNS/TLS/超时/连接被拒时产生。
        Assert.True(TransportFailure.IsTransport(0));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void 真实的_HTTP_状态码都不是传输失败(int status)
    {
        Assert.False(TransportFailure.IsTransport(status));
    }

    [Fact]
    public void 传输失败不该记到账号头上()
    {
        // ★ 这是整个修复的核心判据
        Assert.False(TransportFailure.ShouldCountAgainstAccount(0, UpstreamErrorKind.Server));
    }

    [Fact]
    public void 上游_5xx_该记到账号头上()
    {
        // 反向：真有响应体的 5xx 可能与这个号/这条链路有关，仍要记
        Assert.True(TransportFailure.ShouldCountAgainstAccount(500, UpstreamErrorKind.Server));
        Assert.True(TransportFailure.ShouldCountAgainstAccount(502, UpstreamErrorKind.Server));
        Assert.True(TransportFailure.ShouldCountAgainstAccount(503, UpstreamErrorKind.Server));
    }

    [Fact]
    public void 限流不该按账号质量问题记()
    {
        // 限流是临时的，走 MarkRateLimited（按模型冷却）；
        // 就算误走到这里，也不该当"账号坏了"
        Assert.False(TransportFailure.ShouldCountAgainstAccount(429, UpstreamErrorKind.RateLimited));
        Assert.False(TransportFailure.ShouldCountAgainstAccount(429, UpstreamErrorKind.ModelRateLimited));
    }

    [Fact]
    public void 判据只看_status_不看_kind_因为两者_kind_相同()
    {
        // ⚠ 这条解释了**为什么不能用 Kind 判传输层**：
        //    传输失败和真实 5xx 的 Kind **都是 Server**（UpstreamClient.Classify
        //    对 status 0 和 5xx 都给 Server）。所以只有 status 能区分它们。
        //    这正是缺陷的成因 —— 原来的代码按 Kind 走，把两者混成一类。
        Assert.Equal(
            UpstreamErrorKind.Server,
            UpstreamClient.ClassifyCreditFailure(0, "transport error"));
        Assert.Equal(
            UpstreamErrorKind.Server,
            UpstreamClient.ClassifyCreditFailure(503, "boom"));

        // Kind 相同，但处罚判断相反 ⇒ 判据必须是 status
        Assert.False(TransportFailure.ShouldCountAgainstAccount(0, UpstreamErrorKind.Server));
        Assert.True(TransportFailure.ShouldCountAgainstAccount(503, UpstreamErrorKind.Server));
    }

    // ══ ② 账号池：传输失败不冷冻 ═════════════════════════════════

    [Fact]
    public void 反复传输失败不会冷冻账号()
    {
        // ★★ 这条直接复现 t79 的缺陷场景 ★★
        //
        // 原来：8 个号 × 每个 5 次连接失败 → 整池冷冻。
        // 现在：连接失败一次都不该记。
        var (pool, entries) = MakePool(accounts: 8);
        var targets = entries.Select(e => e.Id).ToArray();

        // 远超阈值的次数（比真机上那次还多）
        for (var round = 0; round < 20; round++)
        {
            foreach (var id in targets)
            {
                var counted = pool.MarkFailure(id, "hy3", status: 0, kind: UpstreamErrorKind.Server);
                Assert.False(counted, "传输失败不该被记账");
            }
        }

        // 一个号都不该被冷冻
        foreach (var entry in entries)
        {
            Assert.False(pool.IsFrozen(entry.Id), $"账号 {entry.Label} 不该被冷冻");
        }
    }

    [Fact]
    public void 传输失败后池仍认为该号可用()
    {
        var (pool, entries) = MakePool();
        var id = entries[0].Id;

        for (var i = 0; i < 10; i++)
        {
            pool.MarkFailure(id, "hy3", status: 0, kind: UpstreamErrorKind.Server);
        }

        // Pick 仍能选到它（没被排除）
        var picked = pool.Pick("hy3");
        Assert.NotNull(picked);
        Assert.Equal(id, picked!.Id);
    }

    [Fact]
    public void 传输失败不累计计数()
    {
        // 直接查计数：跳过时要**一个计数都不动**
        var (pool, entries) = MakePool();
        var id = entries[0].Id;

        for (var i = 0; i < 4; i++) pool.MarkFailure(id, "hy3", status: 0, kind: "server");

        var snapshot = pool.FailureSnapshot();
        var member = snapshot.Single(m => m.MemberId == id);
        Assert.Equal(0, member.ConsecutiveFailures);
        Assert.False(member.Frozen);
    }

    [Fact]
    public void 传输失败不重置已有的失败计数()
    {
        // ⚠ 一个容易搞反的设计：跳过时**不是**把计数清零。
        //    清零 = "洗白历史"，会让真正坏掉的号重新被选中。
        //    语义是"这次不算"，不是"以前白算"。
        var (pool, entries) = MakePool();
        var id = entries[0].Id;

        // 先记 2 次真实失败
        pool.MarkFailure(id, "hy3", status: 500, kind: UpstreamErrorKind.Server);
        pool.MarkFailure(id, "hy3", status: 500, kind: UpstreamErrorKind.Server);
        Assert.Equal(2, pool.FailureSnapshot().Single(m => m.MemberId == id).ConsecutiveFailures);

        // 再来一批传输失败
        for (var i = 0; i < 10; i++) pool.MarkFailure(id, "hy3", status: 0, kind: "server");

        // 计数停在 2（既没涨，也没被清零）
        Assert.Equal(2, pool.FailureSnapshot().Single(m => m.MemberId == id).ConsecutiveFailures);
    }

    // ══ ③ 反向：真 5xx 仍会冷冻 ══════════════════════════════════

    [Fact]
    public void 上游真_5xx_仍会累计并冷冻()
    {
        // ★ 证明"区分真的生效"—— 不是把冷冻机制整个关掉了
        var (pool, entries) = MakePool();
        var id = entries[0].Id;

        var counted = 0;
        for (var i = 0; i < 5; i++)
        {
            if (pool.MarkFailure(id, "hy3", status: 502, kind: UpstreamErrorKind.Server)) counted++;
        }

        Assert.Equal(5, counted);                       // 每次都记了
        Assert.True(pool.IsFrozen(id));                 // 到阈值就冷冻
        Assert.Equal(5, pool.FailureSnapshot().Single(m => m.MemberId == id).ConsecutiveFailures);
    }

    [Fact]
    public void 混合场景_只有_5xx_推高计数()
    {
        // 真机那种混合情况：一部分连接失败、一部分真 5xx。
        // 只有后者该推高计数。
        var (pool, entries) = MakePool();
        var id = entries[0].Id;

        for (var i = 0; i < 100; i++) pool.MarkFailure(id, "hy3", status: 0, kind: "server");
        Assert.Equal(0, pool.FailureSnapshot().Single(m => m.MemberId == id).ConsecutiveFailures);
        Assert.False(pool.IsFrozen(id));

        for (var i = 0; i < 4; i++) pool.MarkFailure(id, "hy3", status: 503, kind: "server");
        Assert.Equal(4, pool.FailureSnapshot().Single(m => m.MemberId == id).ConsecutiveFailures);
        Assert.False(pool.IsFrozen(id));   // 还没到 5

        pool.MarkFailure(id, "hy3", status: 503, kind: "server");
        Assert.True(pool.IsFrozen(id));    // 第 5 次到阈值
    }

    [Fact]
    public void 传输失败期间成功的请求能救回计数()
    {
        // 环境恢复之后，一次成功就该清掉计数（既有行为，别被修复破坏）
        var (pool, entries) = MakePool();
        var id = entries[0].Id;

        pool.MarkFailure(id, "hy3", status: 502, kind: "server");
        pool.MarkFailure(id, "hy3", status: 502, kind: "server");
        pool.MarkFailure(id, "hy3", status: 0, kind: "server");

        pool.MarkSuccess(id, "hy3");

        Assert.Equal(0, pool.FailureSnapshot().Single(m => m.MemberId == id).ConsecutiveFailures);
        Assert.False(pool.IsFrozen(id));
    }

    // ══ ④ 旧的无参重载仍可用（向后兼容）═════════════════════════

    [Fact]
    public void 旧重载仍无条件记账()
    {
        // 保留它是给"调用方已经自己判过"的场景（比如明确知道这是 5xx）。
        // 这条钉住它没被改语义 —— 否则会静默影响现有调用点。
        var (pool, entries) = MakePool();
        var id = entries[0].Id;

        for (var i = 0; i < 5; i++) pool.MarkFailure(id, "hy3");

        Assert.True(pool.IsFrozen(id));
    }

    // ══ ⑥ ⚠ 缺陷仍可通过「旧重载」复现（网关当前就是这么调的）═════

    [Fact]
    public void 用旧重载喂传输失败_仍会把整池冻掉()
    {
        // ⚠⚠⚠ 这条**故意复现 t79 的原始缺陷**，用来证明：
        //     修复只在**新重载**上生效，而**网关当前走的是旧重载**。
        //
        // 网关 `GatewayApp.CooldownForRetry` 里：
        //     var kind = result.Kind;                 // 传输失败与 5xx 都是 Server
        //     ... else { _pool.MarkFailure(member.Id, model); }   // ← 不带 status
        //
        // 所以本任务交付的是**修复所需的接口**，而**端到端修复还差网关换调用**。
        // 这条用例是那个事实的**可执行证据** —— 一旦网关改了调用，它应该被
        // 改成断言"不再冷冻"（或直接删掉）。
        var (pool, entries) = MakePool(accounts: 8);

        // 模拟真机：所有请求都是连接层失败，网关用旧重载记账
        for (var round = 0; round < 5; round++)
        {
            foreach (var entry in entries)
            {
                pool.MarkFailure(entry.Id, "hy3");     // ← 旧重载（网关现状）
            }
        }

        // 8 个健康号全部被冻 —— 这正是真机事故的形态
        Assert.All(entries, e => Assert.True(pool.IsFrozen(e.Id), $"{e.Label} 被冻了"));

        // 而池已经选不出任何号 ⇒ 之后所有请求都会是「没有可用账号」
        Assert.Null(pool.Pick("hy3"));
    }

    [Fact]
    public void 同一场景换成新重载_一个号都不会被冻()
    {
        // 与上一条**完全同样的输入**，唯一区别是用新重载（带 status）。
        // 两条摆在一起，把"修复的边界在哪"钉死。
        var (pool, entries) = MakePool(accounts: 8);

        for (var round = 0; round < 5; round++)
        {
            foreach (var entry in entries)
            {
                pool.MarkFailure(entry.Id, "hy3", status: 0, kind: UpstreamErrorKind.Server);
            }
        }

        Assert.All(entries, e => Assert.False(pool.IsFrozen(e.Id)));
        Assert.NotNull(pool.Pick("hy3"));
    }

    // ══ ⑤ 端到端：UpstreamClient 产生的 status 0 形状 ════════════

    [Fact]
    public async Task 连接被拒时_UpstreamClient_产生_status_0_且_kind_为_server()
    {
        // 真机那条路径的起点：HttpRequestException → status 0
        var fake = new FakeUpstream(_ => throw new HttpRequestException("connection refused"));
        var client = new UpstreamClient(
            new HttpClient(fake), UpstreamVariant.China,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpstreamClient>.Instance);

        var result = await client.ChatStreamAsync(
            new Credential { AccessToken = "t" }, "{}", null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, result.Status);
        Assert.Equal(UpstreamErrorKind.Server, result.Kind);

        // ★ 关键：这个 result 喂给账号池的**新重载**时不该记账
        var (pool, entries) = MakePool();
        var counted = pool.MarkFailure(entries[0].Id, "hy3", result.Status, result.Kind);

        Assert.False(counted);
        Assert.False(pool.IsFrozen(entries[0].Id));
    }

    [Fact]
    public async Task 超时也产生_status_0()
    {
        // 超时在 SendAsync 里被编成 TaskCanceledException → status 0
        var fake = new FakeUpstream(_ => throw new TaskCanceledException("timeout"));
        var client = new UpstreamClient(
            new HttpClient(fake), UpstreamVariant.China,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpstreamClient>.Instance);

        var result = await client.ChatStreamAsync(
            new Credential { AccessToken = "t" }, "{}", null, null, CancellationToken.None);

        Assert.Equal(0, result.Status);
        Assert.False(TransportFailure.ShouldCountAgainstAccount(result.Status, result.Kind));
    }

    [Fact]
    public async Task 真_5xx_产生非零_status_仍会记账()
    {
        // 反向端到端：上游真的回了 503（有状态码）
        var fake = new FakeUpstream(_ => FakeUpstream.Status(HttpStatusCode.ServiceUnavailable, "boom"));
        var client = new UpstreamClient(
            new HttpClient(fake), UpstreamVariant.China,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpstreamClient>.Instance);

        var result = await client.ChatStreamAsync(
            new Credential { AccessToken = "t" }, "{}", null, null, CancellationToken.None);

        Assert.Equal(503, result.Status);

        var (pool, entries) = MakePool();
        var counted = pool.MarkFailure(entries[0].Id, "hy3", result.Status, result.Kind);

        Assert.True(counted);
    }
}
