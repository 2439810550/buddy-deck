using BuddyDeck.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 账号池的测试。
///
/// 这是整个系统里逻辑最密的一块 —— 选号顺序、按模型冷却、冷冻、
/// 状态保留，每一条搞错都会表现为「偶发地用不了」或者「白扔配额」，
/// 很难从现象倒推。所以测试要密。
/// </summary>
public class AccountPoolTests
{
    /// <summary>可控时钟 —— 不靠 Thread.Sleep，测试才快且可重复。</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan span) => _now = _now.Add(span);
        public void Set(DateTimeOffset when) => _now = when;
    }

    /// <summary>
    /// 造一条账号。
    ///
    /// ⚠ label 留空时**不要**填 id —— 第一版我填了，结果
    /// 「没有标签时用 uid 前八位」那条测试永远测不到（Label 永远非空）。
    /// </summary>
    private static VaultEntry Entry(
        string id,
        string label = "",
        long expiresAtMs = 0,
        string uid = "u")
        => new()
        {
            Id = id,
            AccessToken = $"tok-{id}",
            Uid = uid,
            Label = label,
            ExpiresAtMs = expiresAtMs,
        };

    private static (AccountPool Pool, FakeClock Clock) Make(params VaultEntry[] entries)
    {
        var clock = new FakeClock();
        var pool = new AccountPool(clock, NullLogger<AccountPool>.Instance);
        pool.Replace(entries);
        return (pool, clock);
    }

    // ── 基本选择 ──────────────────────────────────────────────

    [Fact]
    public void 空池选不出东西()
    {
        var (pool, _) = Make();
        Assert.Null(pool.Pick("m1"));
    }

    [Fact]
    public void 只有一个号时选它()
    {
        var (pool, _) = Make(Entry("a"));
        Assert.Equal("a", pool.Pick("m1")?.Id);
    }

    [Fact]
    public void 负载均匀_轮着用()
    {
        // 三次选取应该把三个号都用一遍，不该一直用同一个。
        var (pool, _) = Make(Entry("a"), Entry("b"), Entry("c"));

        var picks = new List<string?>();
        for (var i = 0; i < 3; i++) picks.Add(pool.Pick("m1")?.Id);

        Assert.Equal(3, picks.Distinct().Count());
    }

    [Fact]
    public void 优先选快过期的()
    {
        // token 快过期的号迟早要刷新 —— 与其晾着不如先用掉。
        // ⚠ 第一版这里写反了：9e12 < 1e15，所以数字小的才是**更早**过期。
        // 造数据时把「早」给了个大数字，于是断言失败 —— 实现是对的。
        var (pool, _) = Make(
            Entry("早", expiresAtMs: 1_700_000_000_000),
            Entry("晚", expiresAtMs: 1_900_000_000_000));

        Assert.Equal("早", pool.Pick("m1")?.Id);
    }

    [Fact]
    public void 没有过期时间的排最后()
    {
        var (pool, _) = Make(
            Entry("无", expiresAtMs: 0),
            Entry("有", expiresAtMs: 9_000_000_000_000));

        Assert.Equal("有", pool.Pick("m1")?.Id);
    }

    // ── 冷却 ──────────────────────────────────────────────────

    [Fact]
    public void 冷却中的号会被跳过()
    {
        var (pool, _) = Make(Entry("a"), Entry("b"));
        pool.MarkRateLimited("a", "m1", resetAtMs: null);

        Assert.Equal("b", pool.Pick("m1")?.Id);
    }

    [Fact]
    public void 全部冷却时选不出来()
    {
        var (pool, _) = Make(Entry("a"), Entry("b"));
        pool.MarkRateLimited("a", "m1", null);
        pool.MarkRateLimited("b", "m1", null);

        Assert.Null(pool.Pick("m1"));
    }

    [Fact]
    public void 冷却是按模型分的()
    {
        // 上游的限流常常只针对单个模型。不分模型的话，
        // 一个模型被限就把整个号拉黑 —— 白扔配额。
        var (pool, _) = Make(Entry("a"));
        pool.MarkRateLimited("a", "m1", null);

        Assert.Null(pool.Pick("m1"));
        Assert.Equal("a", pool.Pick("m2")?.Id);   // 别的模型还能用
    }

    [Fact]
    public void 冷却到期后自动恢复()
    {
        var (pool, clock) = Make(Entry("a"));
        pool.MarkRateLimited("a", "m1", null);

        Assert.Null(pool.Pick("m1"));

        clock.Advance(TimeSpan.FromMinutes(6));   // 默认冷却 5 分钟
        Assert.Equal("a", pool.Pick("m1")?.Id);
    }

    [Fact]
    public void 上游给了恢复时间就按它算()
    {
        var (pool, clock) = Make(Entry("a"));
        var resetAt = clock.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds();

        pool.MarkRateLimited("a", "m1", resetAt);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(pool.Pick("m1"));   // 还没到

        clock.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal("a", pool.Pick("m1")?.Id);   // 到了
    }

    [Fact]
    public void 成功一次会清掉这个模型的冷却()
    {
        var (pool, _) = Make(Entry("a"));
        pool.MarkRateLimited("a", "m1", null);
        pool.MarkSuccess("a", "m1");

        Assert.Equal("a", pool.Pick("m1")?.Id);
    }

    // ── 排除 ──────────────────────────────────────────────────

    [Fact]
    public void 能排除指定的号()
    {
        var (pool, _) = Make(Entry("a"), Entry("b"));
        Assert.Equal("b", pool.Pick("m1", exclude: ["a"])?.Id);
    }

    [Fact]
    public void 全部被排除时选不出来()
    {
        var (pool, _) = Make(Entry("a"), Entry("b"));
        Assert.Null(pool.Pick("m1", exclude: ["a", "b"]));
    }

    // ── 冷冻 ──────────────────────────────────────────────────

    [Fact]
    public void 连续失败到阈值后整体冷冻()
    {
        var (pool, _) = Make(Entry("a"));
        // 默认阈值 5 次
        for (var i = 0; i < 5; i++) pool.MarkFailure("a", "m1");

        Assert.Null(pool.Pick("m1"));
        Assert.Null(pool.Pick("m别的"));   // 冷冻是整体的，不只针对某个模型
    }

    [Fact]
    public void 成功一次就解冻()
    {
        var (pool, _) = Make(Entry("a"));
        for (var i = 0; i < 4; i++) pool.MarkFailure("a", "m1");
        Assert.NotNull(pool.Pick("m1"));   // 还没到阈值

        pool.MarkSuccess("a", "m1");
        for (var i = 0; i < 4; i++) pool.MarkFailure("a", "m1");
        Assert.NotNull(pool.Pick("m1"));   // 又没到阈值（计数被清零过）
    }

    [Fact]
    public void 能手动解冻()
    {
        var (pool, _) = Make(Entry("a"));
        for (var i = 0; i < 5; i++) pool.MarkFailure("a", "m1");
        Assert.Null(pool.Pick("m1"));

        pool.Unfreeze("a");
        Assert.Equal("a", pool.Pick("m1")?.Id);
    }

    // ── 替换（保险库刷新 / 切池）──────────────────────────────

    [Fact]
    public void 替换时保留冷却状态()
    {
        // 这条很重要：刷新账号列表时如果清掉冷却，
        // 被限流的号会立刻被再次选中，又撞一次 429。
        var (pool, _) = Make(Entry("a"), Entry("b"));
        pool.MarkRateLimited("a", "m1", null);

        pool.Replace([Entry("a"), Entry("b")]);

        Assert.Equal("b", pool.Pick("m1")?.Id);
    }

    [Fact]
    public void 替换时保留失败计数()
    {
        var (pool, _) = Make(Entry("a"));
        for (var i = 0; i < 4; i++) pool.MarkFailure("a", "m1");

        pool.Replace([Entry("a")]);
        pool.MarkFailure("a", "m1");   // 第 5 次

        Assert.Null(pool.Pick("m1"));
    }

    [Fact]
    public void 替换时新账号的计数从零开始()
    {
        var (pool, _) = Make(Entry("a"));
        pool.Replace([Entry("a"), Entry("新")]);

        Assert.Equal(2, pool.Count);
        Assert.NotNull(pool.Pick("m1"));
    }

    [Fact]
    public void 替换会丢掉不在列表里的号()
    {
        var (pool, _) = Make(Entry("a"), Entry("b"));
        pool.Replace([Entry("a")]);

        Assert.Equal(1, pool.Count);
        Assert.Equal("a", pool.Members[0].Id);
    }

    // ── 清理 ──────────────────────────────────────────────────

    [Fact]
    public void 清理过期冷却()
    {
        var (pool, clock) = Make(Entry("a"), Entry("b"));
        pool.MarkRateLimited("a", "m1", null);
        pool.MarkRateLimited("b", "m2", null);

        Assert.Equal(0, pool.PruneExpired());   // 还没过期

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(2, pool.PruneExpired());
        Assert.Equal(0, pool.PruneExpired());    // 清过了
    }

    // ── 展示 ──────────────────────────────────────────────────

    [Fact]
    public void 没有标签时用_uid_前八位()
    {
        var member = new PoolMember { Entry = Entry("a", uid: "1234567890abcdef") };
        Assert.Equal("12345678", member.Label);
    }

    [Fact]
    public void Members_返回的是快照()
    {
        // 调用方拿到之后改不了池的内部状态。
        var (pool, _) = Make(Entry("a"));
        var snapshot = pool.Members;

        pool.Replace([Entry("a"), Entry("b")]);

        Assert.Single(snapshot);           // 老快照不变
        Assert.Equal(2, pool.Members.Count);  // 重新拿是新的
    }
}
