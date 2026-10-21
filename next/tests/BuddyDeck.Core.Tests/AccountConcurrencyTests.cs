using System.Collections.Concurrent;
using System.Diagnostics;
using BuddyDeck.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 账号池并发行为的测试（t19）。
///
/// ## 判据从哪来（这条最要紧）
///
/// **不用池的内部计数当判据。** `SelectionCount` 是历史累计，
/// 回答不了「此刻有几个请求正压在这个号上」—— 拿它判重叠
/// 等于问被测对象「你重叠了吗」。`InFlight` 虽然是我们自己加的，
/// 但它同样是被测对象的一部分。
///
/// **正确的判据在「上游那一侧」**：每个号有独立的 accessToken，
/// 网关/调用方发请求时会把它放进...这里直接看**池选出来的是谁** ——
/// 但那是内部的。所以本文件的做法是：**在假上游里按 token 记账**，
/// 用「同一个 token 同时有几个请求在途」来判断重叠。
///
/// 这是 ui 在 `docs/concurrency-verification-howto.md` 立的方法论
/// （「要验证 A 对 B 做了什么，就去 B 那边取证」）在这里的落法。
///
/// ## 其余三条纪律
///
/// · **撑开并发窗口**：假上游占住请求一小段，否则第一个请求在第二个
///   `Acquire` 之前就结束了，测出来永远是「无重叠」—— 那是**假阳性**。
/// · **多轮**：并发里一次巧合和一条规律长得一样，所以每个场景跑 ≥3 轮。
/// · **不用 Start-Sleep**：团队约定不引入「睡」语义。
///   并发窗口靠**闸门**（`SemaphoreSlim` + 计数）撑开 ——
///   所有任务取完号才放行归还，见 <c>RunOnceAsync</c> 的注释里
///   我第一版栽的坑。
/// </summary>
public class AccountConcurrencyTests
{
    /// <summary>多轮次数（≥3：避免把单次调度巧合当结论）。</summary>
    private const int Rounds = 5;

    private static AccountPool MakePool() =>
        new(TimeProvider.System, NullLogger<AccountPool>.Instance);

    /// <summary>造 n 个号，每个号有独一无二的 accessToken（用来在上游侧认身份）。</summary>
    private static List<VaultEntry> MakeAccounts(int count)
    {
        var list = new List<VaultEntry>();
        for (var i = 0; i < count; i++)
        {
            list.Add(new VaultEntry
            {
                Id = $"acc-{i}",
                Uid = $"uid-{i}",
                EnterpriseId = "ent-1",
                Domain = "workbuddy.cn",
                AccessToken = $"token-{i:D3}",
                ExpiresAtMs = 0,
                Label = $"号 {i}",
            });
        }
        return list;
    }

    /// <summary>
    /// 一次「并发使用」的观测结果。
    /// </summary>
    /// <param name="AccountsUsed">被用到的号数。</param>
    /// <param name="PeakConcurrencyPerAccount">单号峰值并发。</param>
    /// <param name="MaxRequestsOnOneAccount">最忙那个号一共承接了几个请求。</param>
    /// <param name="RejectedCount">因达上限而拿不到号（Acquire 返回 null）的次数。</param>
    /// <param name="Distribution">每个号承接的请求数。</param>
    private sealed record Observation(
        int AccountsUsed,
        int PeakConcurrencyPerAccount,
        int MaxRequestsOnOneAccount,
        int RejectedCount,
        IReadOnlyDictionary<string, int> Distribution);

    /// <summary>
    /// 跑一轮：M 个并发任务**同时**尝试取号，全部取完后再一起归还。
    ///
    /// ## ⚠ 为什么必须「先全取、后全还」，而不是每个任务各自取-用-还
    ///
    /// 第一版我写的是「每个任务 Acquire → 忙等一会儿 → Dispose」。
    /// **那是错的，而且错得很隐蔽**：
    ///
    /// · `Thread.SpinWait` 是**同步忙等**，它占住的是线程池的线程；
    /// · 每个任务在 `Acquire` 后**立刻**在同一个同步块里忙等、又归还，
    ///   所以当第二个任务真正跑到 `Acquire` 时，第一个**已经还完了**；
    /// · 结果：根本不存在瞬时重叠 → 上限永远不触发 → 测试假阳性
    ///   （我实测就是这样：8 号/8 并发「用到 8 个号」看着很漂亮，
    ///     但其实全程都是串行，验不出任何并发行为）。
    ///
    /// 这正是 ui 在 `docs/concurrency-verification-howto.md` §0③ 警告的
    /// 「让并发窗口足够大，否则测的是串行」—— 我第一版栽在同一个坑里。
    ///
    /// **正确做法**：先让所有任务都拿到租约（此时租约**都还没还**，
    /// 于是池里真的背着 M 个在途），**确认全部取完**（用闸门计数），
    /// 最后再一起归还。这样「在途」是真实存在的，上限才有机会触发。
    /// </summary>
    private static async Task<Observation> RunOnceAsync(
        AccountPool pool,
        int accounts,
        int concurrency,
        string model = "hy3")
    {
        var tokenRequests = new ConcurrentDictionary<string, int>();     // token → 承接数
        var tokenInFlight = new ConcurrentDictionary<string, int>();     // token → 当前在途
        var tokenPeak = new ConcurrentDictionary<string, int>();         // token → 峰值
        var rejected = 0;
        var acquired = 0;

        // 闸门：所有任务取完号才放行归还
        using var gate = new SemaphoreSlim(0);

        var leases = new ConcurrentBag<InFlightLease>();

        var tasks = Enumerable.Range(0, concurrency).Select(i => Task.Run(() =>
        {
            var lease = pool.Acquire(model);
            if (lease is null)
            {
                Interlocked.Increment(ref rejected);
                // 没拿到也要过闸门，否则拿到的那些会一直等
                if (Interlocked.Increment(ref acquired) == concurrency) gate.Release(concurrency);
                return;
            }

            leases.Add(lease);

            var token = lease.Member.Entry.AccessToken;
            tokenRequests.AddOrUpdate(token, 1, (_, v) => v + 1);

            // 「上游侧」记账：此刻这个 token 有几个在途。
            // 因为租约还没还，这个数字是**真实**的瞬时并发。
            var now = tokenInFlight.AddOrUpdate(token, 1, (_, v) => v + 1);
            tokenPeak.AddOrUpdate(token, now, (_, v) => Math.Max(v, now));

            // 等所有任务都取完 —— 保证租约在这一刻是**同时**持有的
            if (Interlocked.Increment(ref acquired) == concurrency) gate.Release(concurrency);
            gate.Wait();
        })).ToArray();

        await Task.WhenAll(tasks);

        // 取证完毕，统一归还
        foreach (var lease in leases) lease.Dispose();

        return new Observation(
            AccountsUsed: tokenRequests.Count,
            PeakConcurrencyPerAccount: tokenPeak.IsEmpty ? 0 : tokenPeak.Values.Max(),
            MaxRequestsOnOneAccount: tokenRequests.IsEmpty ? 0 : tokenRequests.Values.Max(),
            RejectedCount: rejected,
            Distribution: new Dictionary<string, int>(tokenRequests));
    }

    // ══ 核心：在途上限真的挡得住 ══════════════════════════════

    [Fact]
    public async Task 单号在途上限_默认是2_且超额的数量被挡住()
    {
        var accounts = MakeAccounts(2);       // 2 个号
        var pool = MakePool();
        pool.Replace(accounts);

        // 默认策略 Reject + 上限 2 → 最多同时 2×2 = 4 个在途，第 5 个起拿不到号
        Assert.Equal(2, pool.InFlightOptions.MaxPerAccount);
        Assert.Equal(InFlightPolicy.Reject, pool.InFlightOptions.Policy);

        var obs = await RunOnceAsync(pool, accounts: 2, concurrency: 6);

        // 6 个并发里最多放行 4 个（2 号 × 上限 2），其余被挡
        Assert.True(obs.RejectedCount >= 2,
            $"2 号 × 上限 2 = 最多 4 个在途；6 并发应该有 ≥2 个被挡，实际挡了 {obs.RejectedCount}");

        // 关键：单号峰值并发**不得超过上限**
        Assert.True(obs.PeakConcurrencyPerAccount <= 2,
            $"单号峰值并发 {obs.PeakConcurrencyPerAccount} 超过上限 2");
    }

    [Fact]
    public async Task 单号在途上限_设为1时绝不出现同号并发()
    {
        var accounts = MakeAccounts(3);
        var pool = MakePool();
        pool.Replace(accounts);
        pool.Configure(new InFlightOptions { MaxPerAccount = 1 });

        var obs = await RunOnceAsync(pool, accounts: 3, concurrency: 6);

        Assert.Equal(1, obs.PeakConcurrencyPerAccount);
    }

    [Fact]
    public async Task 允许超额策略下_不再挡请求()
    {
        // AllowExcess 是给「宁可挤也不能拒」的部署留的可回退选项。
        var accounts = MakeAccounts(1);
        var pool = MakePool();
        pool.Replace(accounts);
        pool.Configure(new InFlightOptions { MaxPerAccount = 1, Policy = InFlightPolicy.AllowExcess });

        var obs = await RunOnceAsync(pool, accounts: 1, concurrency: 4);

        Assert.Equal(0, obs.RejectedCount);
        Assert.True(obs.PeakConcurrencyPerAccount > 1, "超额策略下应该真的允许重叠");
    }

    // ══ 回归保护：号够多时不得退化 ════════════════════════════

    [Fact]
    public async Task 号数大于等于并发数时_零重叠_多轮一致()
    {
        // ★ 这是回归保护。ui 实测当前行为是「6 号 / 6 并发 → 峰值 1（零重叠）」。
        //   上限不能把这个好行为改坏 —— 加在途计数最容易犯的错就是
        //   「为了限流反而把轮转让位」，那会退化成挤同一个号。
        for (var round = 1; round <= Rounds; round++)
        {
            var accounts = MakeAccounts(6);
            var pool = MakePool();
            pool.Replace(accounts);

            var obs = await RunOnceAsync(pool, accounts: 6, concurrency: 6);

            Assert.Equal(0, obs.RejectedCount);
            Assert.Equal(6, obs.AccountsUsed);
            Assert.Equal(1, obs.PeakConcurrencyPerAccount);
            Assert.Equal(1, obs.MaxRequestsOnOneAccount);
        }
    }

    [Fact]
    public async Task 号数小于并发数时_复用被压在单号上限内且分布均匀_多轮()
    {
        // 3 号 / 6 并发：数学上必须有号承担 2 个（ui 证明过这一点），
        // 但**不该出现 3 个**（那正是 soft_rate 的形态）。
        for (var round = 1; round <= Rounds; round++)
        {
            var accounts = MakeAccounts(3);
            var pool = MakePool();
            pool.Replace(accounts);

            var obs = await RunOnceAsync(pool, accounts: 3, concurrency: 6);

            Assert.Equal(InFlightPolicy.Reject, pool.InFlightOptions.Policy);
            Assert.Equal(3, obs.AccountsUsed);
            Assert.True(obs.MaxRequestsOnOneAccount <= 2,
                $"第 {round} 轮：最忙号承接 {obs.MaxRequestsOnOneAccount} 个，超过上限 2");
            Assert.True(obs.PeakConcurrencyPerAccount <= 2,
                $"第 {round} 轮：单号峰值并发 {obs.PeakConcurrencyPerAccount} 超过上限 2");

            // 分布要均匀（不该是「一个号 2 个、另一个 0 个」）
            foreach (var (token, count) in obs.Distribution)
            {
                Assert.True(count >= 1, $"{token} 承接 {count} 个，分布不均");
            }
        }
    }

    // ══ 归还：三条路径都不能漏 ════════════════════════════════

    [Fact]
    public void 正常结束后在途回到零()
    {
        var pool = MakePool();
        pool.Replace(MakeAccounts(3));

        var lease = pool.Acquire("hy3");
        Assert.NotNull(lease);
        Assert.Equal(1, pool.TotalInFlight);

        lease!.Dispose();

        Assert.Equal(0, pool.TotalInFlight);
    }

    [Fact]
    public void 上游失败路径_异常从using里冒出去后在途仍回到零()
    {
        // ★ 最容易漏的一条：异常从 using 作用域里冒出去。
        var pool = MakePool();
        pool.Replace(MakeAccounts(3));

        // ⚠ 必须显式写成 `Action`：只写 lambda 的话重载解析会挑到
        //    `Assert.Throws<T>(Func<Task>)`（已过时），因为 lambda 体
        //    只有 throw、返回类型推断不明确。显式 `Action` 一锤定音。
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var lease = pool.Acquire("hy3");
            Assert.NotNull(lease);
            Assert.Equal(1, pool.TotalInFlight);
            throw new InvalidOperationException("模拟上游炸了");
        }));

        // 异常路径也必须归还 —— 否则那个号被永久占住
        Assert.Equal(0, pool.TotalInFlight);
    }

    [Fact]
    public void 客户端取消路径_OperationCanceledException下在途仍回到零()
    {
        var pool = MakePool();
        pool.Replace(MakeAccounts(3));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
        {
            using var lease = pool.Acquire("hy3");
            cts.Token.ThrowIfCancellationRequested();
        });

        Assert.Equal(0, pool.TotalInFlight);
    }

    [Fact]
    public void 租约重复释放只减一次_不会把计数减成负数()
    {
        // ★ 计数变负数是比漏减更糟的失效：负数永远小于上限，
        //   等于**静默地把保护关掉**。
        var pool = MakePool();
        pool.Replace(MakeAccounts(2));

        var lease = pool.Acquire("hy3")!;
        lease.Dispose();
        lease.Dispose();   // 显式再还一次（幂等）
        lease.Dispose();

        Assert.Equal(0, pool.TotalInFlight);
    }

    [Fact]
    public async Task 大量并发反复取还后在途归零_池子不慢性饿死()
    {
        // 漏归还的后果是「可用容量慢慢缩到零」。这条跑多轮大量取还，
        // 最后断言归零 + 池子仍然拿得到号。
        var pool = MakePool();
        pool.Replace(MakeAccounts(3));

        for (var round = 0; round < 20; round++)
        {
            var tasks = Enumerable.Range(0, 6).Select(async _ =>
            {
                using var lease = pool.Acquire("hy3");
                if (lease is null) return;
                await Task.Yield();
            });
            await Task.WhenAll(tasks);
        }

        Assert.Equal(0, pool.TotalInFlight);

        // 池子没饿死：仍然选得到号
        var again = pool.Acquire("hy3");
        Assert.NotNull(again);
        again!.Dispose();
    }

    // ══ 可观测出口 ════════════════════════════════════════════

    [Fact]
    public void 在途快照按在途降序_最忙的在前()
    {
        var pool = MakePool();
        pool.Replace(MakeAccounts(3));
        pool.Configure(new InFlightOptions { MaxPerAccount = 5 });

        var a = pool.Acquire("hy3")!;
        var b = pool.Acquire("hy3")!;
        var c = pool.Acquire("hy3")!;

        var snapshot = pool.InFlightSnapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(3, snapshot.Sum(s => s.InFlight));
        Assert.Equal(1, snapshot[0].InFlight);   // 每个号各 1（轮转），降序下最前的是其一
        Assert.All(snapshot, s => Assert.False(s.Frozen));

        a.Dispose();
        b.Dispose();
        c.Dispose();
        Assert.Equal(0, pool.TotalInFlight);
    }

    // ══ 与会话粘性/既有语义的关系 ═════════════════════════════

    [Fact]
    public void Acquire达上限时返回null_而不是抛异常()
    {
        // 与 t8 的换号重试循环衔接：拿到 null 就换个模型/换个号再试。
        var pool = MakePool();
        pool.Replace(MakeAccounts(1));
        pool.Configure(new InFlightOptions { MaxPerAccount = 1 });

        var first = pool.Acquire("hy3");
        Assert.NotNull(first);

        var second = pool.Acquire("hy3");
        Assert.Null(second);   // 达上限 → null（不抛）

        first!.Dispose();
        var third = pool.Acquire("hy3");
        Assert.NotNull(third);  // 还了之后又能拿
        third!.Dispose();
    }

    [Fact]
    public void Acquire的模型维度与冷却一致_冷却中的号不被选中()
    {
        var pool = MakePool();
        pool.Replace(MakeAccounts(1));
        var id = pool.Members[0].Id;

        pool.MarkRateLimited(id, "hy3", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds());

        Assert.Null(pool.Acquire("hy3"));          // 这个模型被冷却
        var other = pool.Acquire("glm-5.3");       // 别的模型不受影响
        Assert.NotNull(other);
        other!.Dispose();
    }

    [Fact]
    public void 既有Pick的语义一行未变_不受在途上限影响()
    {
        // ⚠ t2 立的规矩：只增不改语义。
        // 老 Pick 不认识在途上限 —— 它照旧每次都返回号（哪怕已超额）。
        var pool = MakePool();
        pool.Replace(MakeAccounts(1));
        pool.Configure(new InFlightOptions { MaxPerAccount = 1 });

        Assert.NotNull(pool.Pick("hy3"));
        Assert.NotNull(pool.Pick("hy3"));   // 老的 Pick 不受上限约束
        Assert.Equal(0, pool.TotalInFlight); // 老 Pick 也不登记在途
    }

    [Fact]
    public void 冻结的号不被Acquire选中()
    {
        var pool = MakePool();
        pool.Replace(MakeAccounts(1));
        var id = pool.Members[0].Id;

        // 连续失败到阈值 → 冷冻
        for (var i = 0; i < 5; i++) pool.MarkFailure(id, "hy3");

        Assert.True(pool.IsFrozen(id));
        Assert.Null(pool.Acquire("hy3"));
    }

    // ══ 分布数据（给回报用的实际观测）════════════════════════

    [Fact]
    public async Task 分布观测_三种配置下的实际数据()
    {
        // 这条把「实际分布」打出来，供回报里贴证据。
        // 断言写得很宽（只钉不变量），因为它主要是**取证**用途。
        var scenarios = new[] { (accounts: 2, concurrency: 6), (accounts: 3, concurrency: 6), (accounts: 6, concurrency: 6) };

        var lines = new List<string>();

        foreach (var (accounts, concurrency) in scenarios)
        {
            for (var round = 1; round <= Rounds; round++)
            {
                var pool = MakePool();
                pool.Replace(MakeAccounts(accounts));

                var obs = await RunOnceAsync(pool, accounts, concurrency);

                lines.Add(
                    $"{accounts}号/{concurrency}并发 第{round}轮：用到 {obs.AccountsUsed}/{accounts} 个号，" +
                    $"最忙号 {obs.MaxRequestsOnOneAccount} 个，峰值并发 {obs.PeakConcurrencyPerAccount}，" +
                    $"被挡 {obs.RejectedCount}，分布 {string.Join(",", obs.Distribution.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}");

                // 不变量：单号峰值绝不超过上限
                Assert.True(obs.PeakConcurrencyPerAccount <= pool.InFlightOptions.MaxPerAccount);
                // 不变量：在途必须归还干净
                Assert.Equal(0, pool.TotalInFlight);
            }
        }

        // 把观测数据落盘 + 打到测试输出。
        //
        // 为什么要落盘：`Console.WriteLine` 默认不被 dotnet test 捕获，
        // 而这份分布数据是**本次交付的主要证据之一**（契约要求贴出来）。
        // 落到一个固定路径，跑完就能直接读。
        foreach (var line in lines) Console.WriteLine(line);

        var reportPath = Path.Combine(Path.GetTempPath(), "buddydeck-concurrency-observation.txt");
        File.WriteAllText(reportPath, string.Join(Environment.NewLine, lines));

        Assert.Equal(scenarios.Length * Rounds, lines.Count);
    }
}
