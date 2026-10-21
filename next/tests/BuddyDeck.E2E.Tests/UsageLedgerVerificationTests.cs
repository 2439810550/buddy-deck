using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.E2E.Tests;

/// <summary>
/// M10 用量账本的**类型级独立验证**（t13-C）。
///
/// ## 为什么单独一组、而且不经 HTTP
///
/// `UsageLedger`（38,567 B）会**往磁盘写用户的历史用量数据**。
/// 如果追加/轮转逻辑有 bug，后果是**用户的历史账本被损坏** —— 不可逆。
/// 这比「某个端点 404」严重得多（404 只是不能用）。
///
/// 所以这一组**直接 new `UsageLedger`**，绕开网关装配：
/// **装配问题归装配，数据安全归数据**。等 `UsageEndpoints` 接进 `Program.cs` 之后，
/// 端点层（金丝雀脱敏、坏行容错）再由 `tools/verify_usage_ledger.py` 端到端补验。
///
/// ## ⚠ 这组测试针对的两个**已修复**的静默失败（作者 observability 实测）
///
/// **① `File.AppendAllText` 并发截断**：8 线程 × 50 条**只落盘 326 行**（期望 400）。
/// 原因：`AppendAllText` 每次自己 open/close 句柄，并发时 A 关闭会按「它以为的长度」截断，
/// 把 B 刚写的切掉。**「单次 write 原子」只在同一个句柄上成立。**
///
/// **② `JsonValue.TryGetValue&lt;long&gt;` 对 int 背书的 JsonValue 返回 false** ——
/// 于是所有 token/credit 都抽成 null，**整个账本记的全是空值且不报错**。
///
/// **两个 bug 的共同点：不崩、不报错，只是数字不对。**
/// ⇒ 所以本组的断言**只认精确数值**，不写「大于 0」「不为 null」这种弱形式。
///
/// ## 断言强度自检（这条保证测试真的能抓到 bug）
///
/// 见 <see cref="错误实现_用FileAppendAllText并发写会丢行_证明本组断言能抓到它"/> ——
/// 它复现「错误实现会失败」，证明上面的精确断言不是凑数的。
/// </summary>
public class UsageLedgerVerificationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _ledgerPath;

    public UsageLedgerVerificationTests()
    {
        _dir = Directory.CreateTempSubdirectory("buddydeck-m10-verify-").FullName;
        _ledgerPath = Path.Combine(_dir, "usage.jsonl");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不该 fail 测试
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>造一条记录（字段齐全，便于验聚合）。</summary>
    private static UsageLedgerRecord Record(
        string model = "hy3",
        string? account = "acct-1",
        long prompt = 10,
        long completion = 5,
        double credit = 0.25)
        => new()
        {
            Iso = "2026-10-07T00:00:00.0000000+00:00",
            Ts = 1790000000000,
            Model = model,
            Account = account,
            Kind = "chat",
            Outcome = "ok",
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = prompt + completion,
            Credit = credit,
        };

    /// <summary>数账本文件里的**非空行**。</summary>
    private int LedgerLineCount()
    {
        if (!File.Exists(_ledgerPath)) return 0;
        return File.ReadAllLines(_ledgerPath).Count(l => l.Trim().Length > 0);
    }

    // ── ② 落盘追加语义 ────────────────────────────────────────

    [Fact]
    public void 追加_写N条就是N行_不是N减几()
    {
        // ★ 精确相等。弱断言（"行数增加了"）验不出静默丢行。
        const int N = 50;

        using (var ledger = new UsageLedger(_ledgerPath))
        {
            for (var i = 0; i < N; i++) ledger.Append(Record());
            ledger.FlushPending();
        }

        Assert.Equal(N, LedgerLineCount());
    }

    [Fact]
    public void 追加_不会覆盖已有内容()
    {
        // 追加的语义是「不破坏已有内容」。第一版 AppendAllText 的截断 bug
        // 表现正是"把前面写的切掉"。
        using (var first = new UsageLedger(_ledgerPath))
        {
            first.Append(Record(model: "第一批"));
            first.FlushPending();
        }

        var firstLine = File.ReadAllLines(_ledgerPath).First(l => l.Trim().Length > 0);

        using (var second = new UsageLedger(_ledgerPath))
        {
            for (var i = 0; i < 10; i++) second.Append(Record(model: "第二批"));
            second.FlushPending();
        }

        var lines = File.ReadAllLines(_ledgerPath).Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(11, lines.Count);
        Assert.Equal(firstLine, lines[0]);   // 第一条一字未变
    }

    // ── ⑤ 原子性：并发写 ──────────────────────────────────────

    [Fact]
    public void 并发写_8线程乘50条必须精确落盘400行且零坏行()
    {
        // ★★ 这是抓出 AppendAllText 截断 bug 的**同一个场景**：
        //    作者实测错误实现只落 326/400（丢 74 行，且不报任何错）。
        const int Workers = 8;
        const int PerWorker = 50;
        const int Total = Workers * PerWorker;

        using (var ledger = new UsageLedger(_ledgerPath))
        {
            Parallel.For(0, Total, new ParallelOptions { MaxDegreeOfParallelism = Workers },
                i => ledger.Append(Record(model: "m-" + (i % 4))));
            ledger.FlushPending();
        }

        // ① 行数必须精确
        var lines = File.ReadAllLines(_ledgerPath).Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(Total, lines.Count);

        // ② 零坏行（交错写会留下半行 JSON）
        var result = UsageLedger.Parse(lines);
        Assert.Empty(result.SkippedLineNumbers);
        Assert.Equal(Total, result.Entries.Count);
    }

    [Fact]
    public void 并发写_每一条都能被解析回原始数值()
    {
        // 不只是"能解析"—— 数值必须对得上。
        // 这防的是 TryGetValue<long> 那类「能解析但全变 null」的静默 bug。
        const int Total = 120;

        using (var ledger = new UsageLedger(_ledgerPath))
        {
            Parallel.For(0, Total, i => ledger.Append(
                Record(prompt: 100 + i, completion: 10, credit: 1.5)));
            ledger.FlushPending();
        }

        var result = UsageLedger.Parse(File.ReadAllLines(_ledgerPath));
        Assert.Empty(result.SkippedLineNumbers);
        Assert.Equal(Total, result.Entries.Count);

        // ★ 精确断言：每条的 prompt 都在 100..219 且**非 null**
        var prompts = result.Entries.Select(e => e.PromptTokens).ToList();
        Assert.DoesNotContain(null, prompts);                       // 防 TryGetValue<long> 那个 bug
        Assert.Equal(Total, prompts.Distinct().Count());            // 120 个互不相同的值，一个不丢
        Assert.Equal(100, prompts.Min());
        Assert.Equal(100 + Total - 1, prompts.Max());

        var credits = result.Entries.Select(e => e.Credit).ToList();
        Assert.DoesNotContain(null, credits);
        Assert.All(credits, c => Assert.Equal(1.5, c!.Value, 6));
    }

    // ── ③ 聚合正确性 ──────────────────────────────────────────

    [Fact]
    public void 聚合_totals的credit等于手算值()
    {
        using (var ledger = new UsageLedger(_ledgerPath))
        {
            // hy3: 3 笔 × 0.25 = 0.75
            for (var i = 0; i < 3; i++) ledger.Append(Record(model: "hy3", credit: 0.25));
            // glm: 2 笔 × 1.00 = 2.00
            for (var i = 0; i < 2; i++) ledger.Append(Record(model: "glm-5.3", credit: 1.00));
            ledger.FlushPending();
        }

        var (aggregate, read) = UsageAggregation.ReadAndAggregate(new UsageLedger(_ledgerPath));
        Assert.Empty(read.SkippedLineNumbers);
        Assert.Equal(5, aggregate.EntryCount);

        // 手算：0.75 + 2.00 = 2.75
        Assert.Equal(2.75, aggregate.Totals.Credit, 6);
        Assert.Equal(5, aggregate.Totals.Calls);
    }

    [Fact]
    public void 聚合_byModel的分组与排序正确()
    {
        using (var ledger = new UsageLedger(_ledgerPath))
        {
            for (var i = 0; i < 3; i++) ledger.Append(Record(model: "hy3", credit: 0.25));
            for (var i = 0; i < 2; i++) ledger.Append(Record(model: "glm-5.3", credit: 1.00));
            ledger.FlushPending();
        }

        var (aggregate, _) = UsageAggregation.ReadAndAggregate(new UsageLedger(_ledgerPath));

        // ⚠ UsageAggregateRow 的键叫 `Key`（模型名或账号标签共用同一个字段），不是 `Model`
        var byModel = aggregate.ByModel.ToDictionary(r => r.Key, StringComparer.Ordinal);
        Assert.Equal(2, byModel.Count);
        Assert.Equal(0.75, byModel["hy3"].Credit, 6);
        Assert.Equal(2.00, byModel["glm-5.3"].Credit, 6);
        Assert.Equal(3, byModel["hy3"].Calls);
        Assert.Equal(2, byModel["glm-5.3"].Calls);

        // ★ 排序契约：credit 从多到少（glm 在前）
        Assert.Equal("glm-5.3", aggregate.ByModel[0].Key);
        Assert.Equal("hy3", aggregate.ByModel[1].Key);

        // ★ Σ(byModel) 必须等于 totals —— 这是聚合自洽性
        Assert.Equal(aggregate.Totals.Credit, aggregate.ByModel.Sum(r => r.Credit), 6);
    }

    [Fact]
    public void 聚合_byAccount的分组正确()
    {
        using (var ledger = new UsageLedger(_ledgerPath))
        {
            ledger.Append(Record(account: "acct-1", credit: 1.0));
            ledger.Append(Record(account: "acct-1", credit: 1.0));
            ledger.Append(Record(account: "acct-2", credit: 5.0));
            ledger.FlushPending();
        }

        var (aggregate, _) = UsageAggregation.ReadAndAggregate(new UsageLedger(_ledgerPath));
        var byAccount = aggregate.ByAccount.ToDictionary(r => r.Key, StringComparer.Ordinal);

        Assert.Equal(2, byAccount.Count);
        Assert.Equal(2.0, byAccount["acct-1"].Credit, 6);
        Assert.Equal(5.0, byAccount["acct-2"].Credit, 6);
        Assert.Equal(aggregate.Totals.Credit, aggregate.ByAccount.Sum(r => r.Credit), 6);
    }

    // ── ④ 坏行容错（类型级）────────────────────────────────────

    [Fact]
    public void 坏行_半截JSON与空行都被跳过_好行照常解析()
    {
        using (var ledger = new UsageLedger(_ledgerPath))
        {
            ledger.Append(Record(credit: 1.0));
            ledger.Append(Record(credit: 2.0));
            ledger.FlushPending();
        }

        // 手工插入坏内容（模拟：断电写到一半 / 手改坏）
        File.AppendAllText(_ledgerPath,
            "{\"iso\":\"2026-01-01T00:00:00.000Z\",\"ts\":1,\"mod\n" +   // 半截 JSON
            "这不是 JSON，是一句话\n" +                                     // 完全不是 JSON
            "\n" +                                                         // 空行
            "{\"iso\":\"2026-01-01T00:00:00.000Z\",\"ts\":2,\"model\":\"ok\",\"credit\":3.0}\n");

        var result = UsageLedger.Parse(File.ReadAllLines(_ledgerPath));

        // 好行（2 条原始 + 1 条手插的合法 JSON）全部保留
        Assert.Equal(3, result.Entries.Count);
        // 坏行被记下但**不抛**
        Assert.Equal(2, result.SkippedLineNumbers.Count);

        var aggregate = UsageAggregation.Aggregate(result.Entries);
        Assert.Equal(6.0, aggregate.Totals.Credit, 6);   // 1 + 2 + 3
    }

    // ── 断言强度自检：证明上面的精确断言真有辨别力 ────────────

    [Fact]
    public void 错误实现_用FileAppendAllText并发写_达不到精确值_证明本组断言有辨别力()
    {
        // ## 这个测试为什么存在
        //
        // 上面所有断言都是「精确相等」。**但"精确"本身不能证明测试有效** ——
        // 万一这个场景根本触发不了缺陷，断言再严也是摆设。
        //
        // 所以这里**故意复现那版被废弃的实现**（`File.AppendAllText`），
        // 证明：**同一个并发场景下，错误实现确实达不到精确值**。
        // ⇒ 上面 `并发写_...` 那条若换成 AppendAllText 就会红。
        //
        // 这是 harness「断言强度自检」/「故意注入自证」的同一思路：
        // **能复现缺陷，测试才算有效。**
        //
        // ## ⚠ 本机实测：失败形态与作者记录的**不一样**（重要）
        //
        // 作者在 `UsageLedger.WriteOne` 注释里写的是**静默丢行**：
        //   「8 线程 × 50 条只落盘了 326 行（期望 400）」
        //
        // **我在本机（Windows）跑同一场景，观测到另一种形态**：
        //   ```
        //   期望 400 / 实际 249 / 成功调用 249 / 抛异常 151 次
        //   IOException: ... because it is being used by another process
        //   ```
        //   ⇒ **抛 `IOException`（文件被占用），而不是安静地少写。**
        //
        // 两种形态不矛盾，取决于平台与 .NET 内部的共享模式/重试：
        //   · 更接近 POSIX 语义时 → 静默截断（作者观察到的）
        //   · Windows 上 → 打开时撞锁直接抛（我观察到的）
        // **但结论一致：它达不到精确值。** 而 `UsageLedger` 两种都防住了
        // （一把锁 + 长期持有的句柄 ⇒ 既不撞锁也不截断）。
        //
        // ⇒ 所以本自检断言「**不可能既零异常又行数精确**」，
        //   不去猜平台给哪一种形态。
        const int Workers = 8;
        const int PerWorker = 50;
        const int Total = Workers * PerWorker;

        var badPath = Path.Combine(_dir, "bad-implementation.jsonl");

        var succeeded = 0;
        var failed = 0;

        Parallel.For(0, Total, new ParallelOptions { MaxDegreeOfParallelism = Workers }, i =>
        {
            try
            {
                // ⚠ 这正是被废弃的第一版写法：每次调用自己 open/close 句柄，
                //    「单次 write 原子」只在同一个句柄上成立 —— 重开即放弃。
                File.AppendAllText(badPath, $"{{\"i\":{i}}}\n");
                Interlocked.Increment(ref succeeded);
            }
            catch (IOException)
            {
                // Windows 上最常见的形态：文件被别的线程持着 → 直接抛
                Interlocked.Increment(ref failed);
            }
        });

        var lineCount = File.Exists(badPath)
            ? File.ReadAllLines(badPath).Count(l => l.Trim().Length > 0)
            : 0;

        // ★ 自检的核心断言：错误实现**不可能**做到「零异常 + 行数精确」。
        //   做到了就说明本场景没有辨别力，上面那些精确断言也就证明不了任何东西。
        Assert.False(failed == 0 && lineCount == Total,
            $"错误实现居然写全了 {lineCount}/{Total} 行且零异常 —— 本场景没有辨别力，" +
            $"上面那些「精确相等」的断言也就证明不了任何东西。" +
            $"（若平台/负载让它偶发写全，需加大并发或条数再来）");

        // 顺带：确实写进去了一部分 —— 0 行说明场景压根没成立
        Assert.True(lineCount > 0, "至少该写进去一部分 —— 0 行说明场景不成立");
    }
}
