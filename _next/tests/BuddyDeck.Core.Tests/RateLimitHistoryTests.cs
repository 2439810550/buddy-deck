using Microsoft.Extensions.Logging.Abstractions;
using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 限流历史（<see cref="AccountPool.RateLimitSummaryByModel"/>）的测试。
///
/// ## 为什么需要它（用户实测反馈）
///
/// 用户原话：「20 个号的 deepseek-v4.1-flash 基本都限流了，**但是显示全绿**。
/// 应该告诉用户这些模型限流了、**啥时候解除限流**」。
///
/// 根因：`MarkSuccess` 会 `Cooldowns.Remove(model)` ——
/// 降级救回来之后**什么都不剩**，界面只能显示"全绿"，
/// 而用户体感是"老是换模型"。
///
/// 这组测试钉的就是那条：**限流历史必须在成功之后仍然看得见**。
/// </summary>
public sealed class RateLimitHistoryTests
{
    private static AccountPool Pool(int accounts, DateTimeOffset now, out FakeClock clock)
    {
        clock = new FakeClock(now);
        // ⚠ 参数顺序是 `(clock, log)` —— 我第一版写成 `(log: null, clock: clock)`
        //   会因为 clock 落进 log 位置而 NRE。
        var pool = new AccountPool(clock, NullLogger<AccountPool>.Instance);
        pool.Replace(
            Enumerable.Range(0, accounts).Select(i => new VaultEntry
            {
                Id = $"acct-{i}:",
                Uid = $"uid-{i}",
                AccessToken = "token",
                Domain = "www.workbuddy.cn",
                Label = $"号{i}",
                ExpiresAtMs = now.AddDays(30).ToUnixTimeMilliseconds(),
            }).ToList());
        return pool;
    }

    /// <summary>可拨的时钟 —— 时间相关的行为必须能在测试里推进。</summary>
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    // ══════════════════════════════════════════════════════════
    //  ① 限流被记下来
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 限流历史_单号单模型_被记录()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var pool = Pool(3, now, out _);
        var member = pool.Members[0];

        var resetAt = now.AddMinutes(5).ToUnixTimeMilliseconds();
        pool.MarkRateLimited(member.Id, "deepseek-v4.1-flash", resetAt);

        var summary = pool.RateLimitSummaryByModel();

        Assert.True(summary.ContainsKey("deepseek-v4.1-flash"));
        var s = summary["deepseek-v4.1-flash"];
        Assert.Equal(1, s.LimitedAccounts);
        Assert.Equal(3, s.TotalAccounts);
        Assert.Equal(0, s.RecoveredAccounts);
        Assert.Equal(resetAt, s.LastResetAtMs);
        Assert.True(s.AnyCoolingNow);
    }

    // ══════════════════════════════════════════════════════════
    //  ② ★ 核心：成功之后历史仍在（防"全绿"假象）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 成功之后_冷却清了但限流历史仍在()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var pool = Pool(3, now, out _);
        var member = pool.Members[0];

        pool.MarkRateLimited(member.Id, "deepseek-v4.1-flash", now.AddMinutes(5).ToUnixTimeMilliseconds());

        // 降级救回来了
        pool.MarkSuccess(member.Id, "deepseek-v4.1-flash");

        // 瞬时状态：已经不冷却了
        Assert.True(pool.CanPick(member.Id, "deepseek-v4.1-flash"));
        Assert.Empty(pool.CooldownsOf(member.Id));

        // ★ 但历史还在 —— 这就是用户要的那条信息
        var s = pool.RateLimitSummaryByModel()["deepseek-v4.1-flash"];
        Assert.Equal(1, s.LimitedAccounts);   // 限过就是限过
        Assert.Equal(1, s.RecoveredAccounts); // 而且知道它恢复了
        Assert.False(s.AnyCoolingNow);

        // ⚠ 这一条断言是整个测试的目的：
        //   如果哪天有人"顺手"把限流历史也清了，这里会红 ——
        //   而界面上又会回到"全绿"的假象。
    }

    // ══════════════════════════════════════════════════════════
    //  ③ 多号汇总（用户场景：20 个号里 18 个限流）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 多号限流_汇总出限流号数与总号数()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var pool = Pool(20, now, out _);

        // 18 个号对同一个模型限流
        var resetAt = now.AddMinutes(5).ToUnixTimeMilliseconds();
        foreach (var m in pool.Members.Take(18))
        {
            pool.MarkRateLimited(m.Id, "deepseek-v4.1-flash", resetAt);
        }

        var s = pool.RateLimitSummaryByModel()["deepseek-v4.1-flash"];

        Assert.Equal(18, s.LimitedAccounts);
        Assert.Equal(20, s.TotalAccounts);

        // 可用号数也能对上（CanPick 不受限流历史影响，它只看 Cooldowns）
        var usable = pool.Members.Count(m => pool.CanPick(m.Id, "deepseek-v4.1-flash"));
        Assert.Equal(2, usable);
    }

    // ══════════════════════════════════════════════════════════
    //  ④ 「啥时候解除限流」
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 多个号的解除时刻_取最晚的那个()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var pool = Pool(3, now, out _);

        var early = now.AddMinutes(2).ToUnixTimeMilliseconds();
        var late = now.AddMinutes(30).ToUnixTimeMilliseconds();

        pool.MarkRateLimited(pool.Members[0].Id, "m", early);
        pool.MarkRateLimited(pool.Members[1].Id, "m", late);
        pool.MarkRateLimited(pool.Members[2].Id, "m", now.AddMinutes(10).ToUnixTimeMilliseconds());

        // ⚠ 取**最晚**：用户问"什么时候能全都用上"，答案是最后好的那个，
        //   而不是第一个好的。取最早的会给出一个乐观到错的承诺。
        Assert.Equal(late, pool.RateLimitSummaryByModel()["m"].LastResetAtMs);
    }

    [Fact]
    public void 冷却过期后_不再算作冷却中但历史仍在()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var pool = Pool(2, now, out var clock);

        pool.MarkRateLimited(pool.Members[0].Id, "m", now.AddMinutes(5).ToUnixTimeMilliseconds());
        Assert.True(pool.RateLimitSummaryByModel()["m"].AnyCoolingNow);

        // 时间推到冷却之后（**没有**调用 MarkSuccess —— 只是时间过去了）
        clock.Advance(TimeSpan.FromMinutes(6));

        var s = pool.RateLimitSummaryByModel()["m"];
        Assert.False(s.AnyCoolingNow);      // 不再冷却
        Assert.Equal(1, s.LimitedAccounts); // 但历史还在
        Assert.True(pool.CanPick(pool.Members[0].Id, "m"));
    }

    // ══════════════════════════════════════════════════════════
    //  ⑤ 多次限流累加
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 同一个号同一模型限流两次_计数累加且恢复标记被重置()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var pool = Pool(1, now, out _);
        var id = pool.Members[0].Id;

        pool.MarkRateLimited(id, "m", now.AddMinutes(5).ToUnixTimeMilliseconds());
        pool.MarkSuccess(id, "m");
        Assert.Equal(1, pool.RateLimitSummaryByModel()["m"].RecoveredAccounts);

        // 又限了一次 —— 恢复标记必须变回 false
        pool.MarkRateLimited(id, "m", now.AddMinutes(9).ToUnixTimeMilliseconds());

        var s = pool.RateLimitSummaryByModel()["m"];
        Assert.Equal(1, s.LimitedAccounts);    // 还是 1 个号（不是 2）
        Assert.Equal(0, s.RecoveredAccounts);  // ★ 又限了 ⇒ 不算恢复了
        Assert.True(s.AnyCoolingNow);
    }

    // ══════════════════════════════════════════════════════════
    //  ⑥ 没限流过时是空的（不制造噪音）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 没发生过限流_汇总为空()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var pool = Pool(3, now, out _);

        Assert.Empty(pool.RateLimitSummaryByModel());
    }

    // ══════════════════════════════════════════════════════════
    //  ⑦ ★ 落盘与恢复（t86 核心验收：重启不丢）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 限流历史落盘后_新池能恢复()
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "rl-history-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var path = System.IO.Path.Combine(dir, "rate-limits.json");
            var store = new RateLimitStore(() => path);

            var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
            var clock = new FakeClock(now);

            // 第一个池：限流一次 → 落盘
            var pool = new AccountPool(clock, NullLogger<AccountPool>.Instance, store);
            var entry = new VaultEntry
            {
                Id = "acct-1:",
                Uid = "uid-1",
                AccessToken = "token",
                Domain = "www.workbuddy.cn",
                Label = "号1",
                ExpiresAtMs = now.AddDays(30).ToUnixTimeMilliseconds(),
            };
            pool.Replace([entry]);
            var resetAt = now.AddMinutes(5).ToUnixTimeMilliseconds();
            pool.MarkRateLimited(pool.Members[0].Id, "deepseek-v4.1-flash", resetAt);

            // 第二个池（模拟重启）：用同一个 store，恢复历史
            var clock2 = new FakeClock(now);
            var pool2 = new AccountPool(clock2, NullLogger<AccountPool>.Instance, store);
            pool2.Replace([entry]);

            var s = pool2.RateLimitSummaryByModel()["deepseek-v4.1-flash"];
            Assert.Equal(1, s.LimitedAccounts);   // ★ 重启后仍能看到限流历史
            Assert.Equal(resetAt, s.LastResetAtMs);
            Assert.False(s.RecoveredAccounts > 0); // 还没恢复
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void 不接_store_时退化成纯内存_不抛()
    {
        // 不传 store（默认 null）是合法状态 —— 测试/纯内存场景不落盘。
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var pool = Pool(1, now, out _);

        pool.MarkRateLimited(pool.Members[0].Id, "m", null);

        Assert.Equal(1, pool.RateLimitSummaryByModel()["m"].LimitedAccounts);
    }
}
