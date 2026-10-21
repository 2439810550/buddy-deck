using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 用量账本的测试。
///
/// ## 这一块错了为什么很贵
///
/// 账本是**唯一能回答「我烧了多少额度」的东西**。它错了不会崩、
/// 不会报警，只会**安静地给出一个错的数字** —— 用户照那个数字做决定
/// （换哪个模型、续不续费），然后发现对不上。所以测试要压得比
/// 其它模块密：聚合算术、缺字段语义、并发、坏行，一个都不能漏。
///
/// ## 不碰真实文件
///
/// 每个用例用独立的临时目录，跑完删掉。**绝不去碰用户真实的
/// `usage.jsonl`** —— 那是他的历史账，测试写坏了就没了。
/// </summary>
public class UsageLedgerTests
{
    /// <summary>一个用一次就自己清理的临时账本目录。</summary>
    private sealed class TempLedger : IDisposable
    {
        public string Directory { get; }
        public string Path { get; }

        public TempLedger()
        {
            Directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "buddydeck-usage-" + Guid.NewGuid().ToString("N")[..12]);

            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, UsageLedger.FileName);
        }

        /// <summary>建一个账本（默认同步落盘 —— 测试要确定性，不要后台线程）。</summary>
        public UsageLedger Open(bool buffered = false) => new(Path, null, buffered);

        /// <summary>
        /// 文件的**原始文本**（不存在时空串）。
        ///
        /// ⚠ 为什么不用 <c>File.ReadAllText</c>：账本持有一个长期开着的
        /// 写流（见 UsageLedger.WriteOne —— 那是为了并发追加不交错），
        /// 而 <c>File.ReadAllText</c> 默认以 `FileShare.Read` 打开，
        /// Windows 上会和那个写流冲突，直接抛
        /// 「文件正被另一个进程使用」。
        ///
        /// 这里显式用 `FileShare.ReadWrite` 打开 —— 和
        /// <c>UsageLedger.Read()</c> 内部的做法一致。
        /// 这样测试**读的是磁盘上的真实字节**（而不是走 ledger.Read()
        /// 的解析路径），能验到「文件格式本身对不对」、
        /// 「每行是不是独立合法 JSON」这类 ledger.Read() 验不到的东西。
        /// </summary>
        public string Text
        {
            get
            {
                if (!File.Exists(Path)) return "";

                using var stream = new FileStream(
                    Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
        }

        /// <summary>文件里的非空行数（同样走共享读）。</summary>
        public int LineCount => Text
            .Split('\n')
            .Count(l => l.Trim().Length > 0);

        /// <summary>文件的原始行（共享读）。</summary>
        public string[] Lines => Text.Split('\n');

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不该让测试 fail
            }
        }
    }

    /// <summary>造一条账目。</summary>
    private static UsageLedgerRecord Record(
        string model = "hy3",
        string? account = "acct-1",
        double? credit = 0.22,
        long prompt = 100,
        long completion = 50,
        long? cacheHit = null,
        long? cacheMiss = null,
        string outcome = "ok",
        long ts = 0)
        => new()
        {
            Iso = "2026-10-07T12:00:00.0000000+00:00",
            Ts = ts == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : ts,
            Model = model,
            Account = account,
            Outcome = outcome,
            Credit = credit,
            PromptTokens = prompt,
            CompletionTokens = completion,
            CacheHit = cacheHit,
            CacheMiss = cacheMiss,
        };

    /// <summary>
    /// 把日志收集下来的 ILogger —— 用来断言「某条日志到底有没有出现」。
    ///
    /// ## 为什么需要它（而不是只看文件长没长）
    ///
    /// 本任务修的就是**「读不到大小」不留信号**。要验「信号真的发出来了」，
    /// 只能去读日志 —— 文件本身看不出差别（两种情况都是「不轮转」）。
    ///
    /// ⚠ 按 (级别, 消息) 收集，**保留级别** —— 因为「该记 Warning」
    /// 和「不该记任何东西」是两条不同的断言，不能只看消息条数。
    /// </summary>
    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<UsageLedger>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public bool HasWarning => Entries.Any(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);

        public string AllText => string.Join(" | ", Entries.Select(e => $"[{e.Level}] {e.Message}"));
    }

    // ══════════════════════════════════════════════════════════
    // ① 追加写
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 记一笔就落盘一行()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        ledger.Append(Record());

        Assert.True(File.Exists(temp.Path));
        Assert.Equal(1, temp.LineCount);
        Assert.Equal(1, ledger.Written);

        // 文件末尾**必须**有换行 —— 没有的话下一次追加会把两行粘成一行，
        // 整个账本从那一行起就废了
        Assert.EndsWith("\n", temp.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 追加不会覆盖已有的行()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        for (var i = 0; i < 5; i++)
        {
            ledger.Append(Record(credit: 0.1 * (i + 1)));
        }

        Assert.Equal(5, temp.LineCount);

        // 五行都在，而且顺序没乱
        var read = ledger.Read();
        Assert.Equal(5, read.Entries.Count);
        Assert.Equal(0, read.SkippedLines);
        Assert.Equal(0.1, read.Entries[0].Credit!.Value, 6);
        Assert.Equal(0.5, read.Entries[4].Credit!.Value, 6);
    }

    [Fact]
    public void 目录不存在时自动创建()
    {
        using var temp = new TempLedger();
        var nested = System.IO.Path.Combine(temp.Directory, "deep", "nested", UsageLedger.FileName);

        using var ledger = new UsageLedger(nested);
        ledger.Append(Record());

        Assert.True(File.Exists(nested));
        Assert.Equal(0, ledger.WriteFailures);
    }

    [Fact]
    public void 每行都是合法且独立的JSON()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        ledger.Append(Record(model: "hy3"));
        ledger.Append(Record(model: "glm-5.3"));

        foreach (var line in temp.Lines)
        {
            if (line.Trim().Length == 0) continue;

            // 逐行解析 —— 一行一个对象，不能有跨行的 JSON
            var obj = JsonNode.Parse(line);
            Assert.NotNull(obj);
            Assert.NotNull(obj!["model"]);
        }
    }

    [Fact]
    public void 空字段不写进文件()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        // 只有模型和必需字段
        ledger.Append(new UsageLedgerRecord { Iso = "2026-10-07T00:00:00Z", Ts = 1, Model = "hy3" });

        var line = temp.Lines[0];

        // null 字段省略（DefaultIgnoreCondition.WhenWritingNull）——
        // 账本要长年累月追加，省掉一串 "cacheRead":null 很可观
        Assert.DoesNotContain("\"credit\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("null", line, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"hy3\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 从上游usage抽字段的便捷入口能落盘()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        var usage = new JsonObject
        {
            ["prompt_tokens"] = 2541,
            ["completion_tokens"] = 120,
            ["total_tokens"] = 2661,
            ["credit"] = 0.22,
            ["prompt_cache_hit_tokens"] = 2304,
            ["prompt_cache_miss_tokens"] = 237,
            ["completion_thinking_tokens"] = 88,
        };

        ledger.Append(
            model: "hy3",
            account: "acct-1",
            usage: usage,
            at: DateTimeOffset.UnixEpoch,
            usedModel: "hy3",
            elapsedMs: 1234,
            attempts: 2);

        var entry = ledger.Read().Entries.Single();

        Assert.Equal(2541, entry.PromptTokens);
        Assert.Equal(120, entry.CompletionTokens);
        Assert.Equal(2661, entry.TotalTokens);
        Assert.Equal(0.22, entry.Credit!.Value, 6);
        Assert.Equal(2304, entry.CacheHit);
        Assert.Equal(237, entry.CacheMiss);
        Assert.Equal(88, entry.Reasoning);
        Assert.Equal(1234, entry.ElapsedMs);
        Assert.Equal(2, entry.Attempts);
    }

    [Fact]
    public void usedModel相同时不重复记()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        ledger.Append("hy3", "acct-1", null, DateTimeOffset.UnixEpoch, usedModel: "hy3");
        ledger.Append("hy3", "acct-1", null, DateTimeOffset.UnixEpoch, usedModel: "glm-5.3");

        var entries = ledger.Read().Entries;

        // 和请求模型一样就没必要再记一份（旧版也是这么省的）
        Assert.Null(entries[0].UsedModel);
        // 真的换过模型才记
        Assert.Equal("glm-5.3", entries[1].UsedModel);
    }

    [Fact]
    public void 写不进去时不抛异常只记失败数()
    {
        // 用一个不可能写成功的路径（拿一个已存在的**文件**当目录）
        using var temp = new TempLedger();
        var blocker = System.IO.Path.Combine(temp.Directory, "blocker");
        File.WriteAllText(blocker, "我是文件，不是目录");

        using var ledger = new UsageLedger(System.IO.Path.Combine(blocker, "sub", UsageLedger.FileName));

        // 记账是旁路 —— 它挂了绝不能拖累用户的请求
        ledger.Append(Record());

        Assert.Equal(1, ledger.Written);
        Assert.True(ledger.WriteFailures > 0, "写失败应该被计数");
    }

    // ══════════════════════════════════════════════════════════
    // ② 聚合正确性
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 空账本的聚合是全零而不是空引用()
    {
        // 空账本是正常状态（新装的用户）—— 界面不该因此崩
        var aggregate = UsageAggregation.Aggregate([]);

        Assert.NotNull(aggregate.Totals);
        Assert.Equal(0, aggregate.Totals.Calls);
        Assert.Equal(0, aggregate.Totals.Credit);
        Assert.Equal(0, aggregate.Totals.Tokens);
        Assert.Empty(aggregate.ByModel);
        Assert.Empty(aggregate.ByAccount);
        Assert.Equal(0, aggregate.EntryCount);

        // 没有数据时派生指标是 null 而不是 0 ——
        // 0 会被界面显示成「0 积分/token，超便宜」，那是假的好消息
        Assert.Null(aggregate.Totals.CreditPerKToken);
        Assert.Null(aggregate.Totals.TokensPerCredit);
        Assert.Null(aggregate.Totals.CacheHitRate);
        Assert.Null(aggregate.FirstTs);
        Assert.Null(aggregate.LastTs);
    }

    [Fact]
    public void 没有文件的账本读到空结果()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        // 一次都没记过的账本 —— 读它不该抛
        var read = ledger.Read();

        Assert.Empty(read.Entries);
        Assert.Equal(0, read.SkippedLines);
        Assert.False(read.HasCorruption);
    }

    [Fact]
    public void 总计把各字段加对()
    {
        var entries = new List<UsageLedgerRecord>
        {
            Record(model: "hy3", account: "a", credit: 0.22, prompt: 100, completion: 50, cacheHit: 80, cacheMiss: 20),
            Record(model: "hy3", account: "b", credit: 0.03, prompt: 200, completion: 30, cacheHit: 180, cacheMiss: 20),
            Record(model: "glm-5.3", account: "a", credit: 1.50, prompt: 500, completion: 100, cacheHit: 0, cacheMiss: 500),
        };

        var totals = UsageAggregation.Aggregate(entries).Totals;

        Assert.Equal(3, totals.Calls);
        Assert.Equal(0, totals.Failures);
        Assert.Equal(1.75, totals.Credit, 6);
        Assert.Equal(800, totals.PromptTokens);
        Assert.Equal(180, totals.CompletionTokens);
        Assert.Equal(980, totals.Tokens);
        Assert.Equal(260, totals.CacheHit);
        Assert.Equal(540, totals.CacheMiss);
    }

    [Fact]
    public void 按模型聚合且按credit从多到少排()
    {
        var entries = new List<UsageLedgerRecord>
        {
            Record(model: "hy3", credit: 0.22),
            Record(model: "glm-5.3", credit: 1.50),
            Record(model: "hy3", credit: 0.03),
        };

        var aggregate = UsageAggregation.Aggregate(entries);

        Assert.Equal(2, aggregate.ByModel.Count);
        // 贵的在前 —— 用户第一眼要看的是「哪个模型在烧钱」
        Assert.Equal("glm-5.3", aggregate.ByModel[0].Key);
        Assert.Equal("hy3", aggregate.ByModel[1].Key);

        Assert.Equal(1.50, aggregate.ByModel[0].Credit, 6);
        Assert.Equal(0.25, aggregate.ByModel[1].Credit, 6);
        Assert.Equal(2, aggregate.ByModel[1].Calls);
    }

    [Fact]
    public void 按账号聚合()
    {
        var entries = new List<UsageLedgerRecord>
        {
            Record(account: "acct-1", credit: 0.22),
            Record(account: "acct-2", credit: 0.80),
            Record(account: "acct-1", credit: 0.10),
        };

        var byAccount = UsageAggregation.Aggregate(entries).ByAccount;

        Assert.Equal(2, byAccount.Count);
        Assert.Equal("acct-2", byAccount[0].Key);
        Assert.Equal(0.80, byAccount[0].Credit, 6);
        Assert.Equal(1, byAccount[0].Calls);

        Assert.Equal("acct-1", byAccount[1].Key);
        Assert.Equal(0.32, byAccount[1].Credit, 6);
        Assert.Equal(2, byAccount[1].Calls);
    }

    [Fact]
    public void 没有账号标签的条目不进按账号聚合()
    {
        var entries = new List<UsageLedgerRecord>
        {
            Record(account: "acct-1", credit: 0.22),
            Record(account: null, credit: 9.99),
        };

        var aggregate = UsageAggregation.Aggregate(entries);

        // 造一个「（未知账号）」的桶会把用量归到一个不存在的号上 ——
        // 那比不显示更糟。总计里仍然包含它（它确实花了钱）。
        Assert.Single(aggregate.ByAccount);
        Assert.Equal("acct-1", aggregate.ByAccount[0].Key);
        Assert.Equal(10.21, aggregate.Totals.Credit, 6);
    }

    [Fact]
    public void 失败次数单独统计()
    {
        var entries = new List<UsageLedgerRecord>
        {
            Record(outcome: "ok"),
            Record(outcome: "failed"),
            Record(outcome: "cancelled"),
            Record(outcome: "ok"),
        };

        var totals = UsageAggregation.Aggregate(entries).Totals;

        // 「失败」的定义是 outcome != ok —— cancelled（用户按停止）
        // 也算非成功：它确实没产出结果
        Assert.Equal(4, totals.Calls);
        Assert.Equal(2, totals.Failures);
    }

    [Fact]
    public void 派生指标算得对()
    {
        // 1000 prompt + 1000 completion = 2000 token，花 2 积分
        var entries = new List<UsageLedgerRecord>
        {
            Record(credit: 2.0, prompt: 1000, completion: 1000, cacheHit: 750, cacheMiss: 250),
        };

        var row = UsageAggregation.Aggregate(entries).Totals;

        Assert.Equal(2000, row.Tokens);
        // 2 积分 / 2000 token * 1000 = 1 积分每千 token
        Assert.Equal(1.0, row.CreditPerKToken!.Value, 6);
        // 2000 token / 2 积分 = 1000 token 每积分
        Assert.Equal(1000, row.TokensPerCredit);
        // 750 / (750+250) = 0.75
        Assert.Equal(0.75, row.CacheHitRate!.Value, 6);
    }

    [Fact]
    public void 零积分时每积分token是null不是除零()
    {
        var entries = new List<UsageLedgerRecord> { Record(credit: 0, prompt: 100, completion: 100) };

        var row = UsageAggregation.Aggregate(entries).Totals;

        // 除零会得到 Infinity，序列化成 JSON 会变成非法值
        Assert.Null(row.TokensPerCredit);
        // 但「每千 token 花多少」是 0（确实没花钱）—— 这个 0 是真信息
        Assert.Equal(0.0, row.CreditPerKToken!.Value);
    }

    [Fact]
    public void 缺字段当零处理不影响其它字段()
    {
        var entries = new List<UsageLedgerRecord>
        {
            new() { Iso = "x", Ts = 1, Model = "hy3" },          // 什么都没有
            Record(credit: 0.5, prompt: 100, completion: 50),     // 完整
        };

        var totals = UsageAggregation.Aggregate(entries).Totals;

        Assert.Equal(2, totals.Calls);
        Assert.Equal(0.5, totals.Credit, 6);
        Assert.Equal(100, totals.PromptTokens);
        Assert.Equal(150, totals.Tokens);
    }

    [Fact]
    public void 时间范围取最早最晚()
    {
        var entries = new List<UsageLedgerRecord>
        {
            Record(ts: 3000),
            Record(ts: 1000),
            Record(ts: 2000),
        };

        var aggregate = UsageAggregation.Aggregate(entries);

        // 界面上说「总共花了 X」时，用户第一个问题就是「这是哪段时间的」
        Assert.Equal(1000, aggregate.FirstTs);
        Assert.Equal(3000, aggregate.LastTs);
    }

    [Fact]
    public void 读文件加聚合一步到位()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        ledger.Append(Record(model: "hy3", credit: 0.22));
        ledger.Append(Record(model: "glm-5.3", credit: 1.50));

        var (aggregate, read) = UsageAggregation.ReadAndAggregate(ledger);

        Assert.Equal(2, aggregate.Totals.Calls);
        Assert.Equal(1.72, aggregate.Totals.Credit, 6);
        Assert.Equal(0, read.SkippedLines);
    }

    [Fact]
    public void credit的精度在往返后不丢()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        // 这几个值都是上游真实会给的形状
        foreach (var value in (double[])[0.22, 0.03, 1.5, 0.123456789])
        {
            ledger.Append(Record(credit: value));
        }

        var entries = ledger.Read().Entries;

        // JSON 往返不能把 credit 改掉 —— 它是钱，差一点都是错的
        Assert.Equal(0.22, entries[0].Credit!.Value, 12);
        Assert.Equal(0.03, entries[1].Credit!.Value, 12);
        Assert.Equal(1.5, entries[2].Credit!.Value, 12);
        Assert.Equal(0.123456789, entries[3].Credit!.Value, 12);
    }

    // ══════════════════════════════════════════════════════════
    // ③ 损坏行容错
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 坏行被跳过其余照常读出()
    {
        using var temp = new TempLedger();

        var good1 = Record(credit: 0.22).ToLine();
        var good2 = Record(credit: 0.03).ToLine();

        // 中间夹一行被外部工具改坏的
        File.WriteAllText(temp.Path,
            good1 + "\n" +
            "{\"model\":\"hy3\",\"credit\":" + "\n" +      // 半截 JSON（写到一半崩了）
            "这不是 JSON\n" +
            good2 + "\n",
            new UTF8Encoding(false));

        using var ledger = temp.Open();
        var read = ledger.Read();

        // ⚠ 核心断言：一行坏了不能让整个账本读不出来
        Assert.Equal(2, read.Entries.Count);
        Assert.Equal(0.22, read.Entries[0].Credit!.Value, 6);
        Assert.Equal(0.03, read.Entries[1].Credit!.Value, 6);

        // 坏行要能被定位（行号从 1 开始）
        Assert.Equal(2, read.SkippedLines);
        Assert.Equal([2, 3], read.SkippedLineNumbers);
        Assert.True(read.HasCorruption);
    }

    [Fact]
    public void 空行不算坏行()
    {
        using var temp = new TempLedger();

        // 文件结尾常见多一个换行，那是正常的，不该报「损坏」
        File.WriteAllText(temp.Path, Record().ToLine() + "\n\n\n", new UTF8Encoding(false));

        using var ledger = temp.Open();
        var read = ledger.Read();

        Assert.Single(read.Entries);
        Assert.Equal(0, read.SkippedLines);
        Assert.False(read.HasCorruption);
    }

    [Fact]
    public void 整个文件都是坏行时不抛异常()
    {
        using var temp = new TempLedger();
        File.WriteAllText(temp.Path, "垃圾\n更多垃圾\n{{{", new UTF8Encoding(false));

        using var ledger = temp.Open();
        var read = ledger.Read();

        Assert.Empty(read.Entries);
        Assert.Equal(3, read.SkippedLines);

        // 而且聚合照常工作（全零）
        var aggregate = UsageAggregation.Aggregate(read.Entries);
        Assert.Equal(0, aggregate.Totals.Calls);
    }

    [Fact]
    public void credit被写成字符串时整行跳过而不是当零()
    {
        using var temp = new TempLedger();
        var bad = """{"iso":"x","ts":1,"model":"hy3","credit":"0.22"}""";
        File.WriteAllText(temp.Path, bad + "\n" + Record(credit: 0.03).ToLine() + "\n", new UTF8Encoding(false));

        using var ledger = temp.Open();
        var read = ledger.Read();

        // 把 "0.22" 静默当 0 会把账算错 —— 那比跳过这一行危险得多。
        // 跳过 + 用户能看到 skippedLines，是诚实的做法。
        Assert.Single(read.Entries);
        Assert.Equal(1, read.SkippedLines);
        Assert.Equal(0.03, read.Entries[0].Credit!.Value, 6);
    }

    [Fact]
    public void 直接从文本解析()
    {
        var text = Record(credit: 0.22).ToLine() + "\n" + "坏的\n" + Record(credit: 0.03).ToLine();

        var read = UsageLedger.ParseText(text);

        Assert.Equal(2, read.Entries.Count);
        Assert.Equal(1, read.SkippedLines);
    }

    [Fact]
    public void 解析空文本不抛()
    {
        Assert.Empty(UsageLedger.ParseText(null).Entries);
        Assert.Empty(UsageLedger.ParseText("").Entries);
        Assert.Empty(UsageLedger.ParseText("   \n  \n").Entries);
    }

    [Fact]
    public void 坏行不影响后续追加()
    {
        using var temp = new TempLedger();
        File.WriteAllText(temp.Path, "坏行\n", new UTF8Encoding(false));

        using var ledger = temp.Open();
        ledger.Append(Record(credit: 0.5));

        var read = ledger.Read();

        // 坏行还在（我们不去动它 —— 静默改写用户的账本更糟），
        // 但新的一笔好好写进去了
        Assert.Single(read.Entries);
        Assert.Equal(0.5, read.Entries[0].Credit!.Value, 6);
        Assert.Equal(1, read.SkippedLines);
    }

    // ══════════════════════════════════════════════════════════
    // ④ 并发追加
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 并发追加不丢条不串行()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        const int threads = 8;
        const int perThread = 50;

        Parallel.For(0, threads, i =>
        {
            for (var j = 0; j < perThread; j++)
            {
                ledger.Append(Record(model: $"模型-{i}", credit: 0.01));
            }
        });

        Assert.Equal(threads * perThread, ledger.Written);

        var read = ledger.Read();

        // ⚠ 每一条都要能读出来，而且**一行都没坏** ——
        // 这是「整行一次写完」那条原子性约定的直接验证：
        // 拆成两次写的话，并发下必然出现两行交错的半截 JSON。
        Assert.Equal(threads * perThread, read.Entries.Count);
        Assert.Equal(0, read.SkippedLines);

        // 每个线程的条数都对
        var aggregate = UsageAggregation.Aggregate(read.Entries);
        Assert.Equal(threads * perThread, aggregate.Totals.Calls);

        foreach (var row in aggregate.ByModel)
        {
            Assert.Equal(perThread, row.Calls);
        }
    }

    [Fact]
    public void 并发追加之后聚合的credit总额精确()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        // 每条 0.01，一共 400 条 → 总额应当是 4.00
        Parallel.For(0, 400, _ => ledger.Append(Record(credit: 0.01)));

        var read = ledger.Read();
        var totals = UsageAggregation.Aggregate(read.Entries).Totals;

        Assert.Equal(400, totals.Calls);
        Assert.Equal(0, read.SkippedLines);
        Assert.Equal(4.0, totals.Credit, 4);
    }

    [Fact]
    public void 并发读和写同时进行不抛异常()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        using var stop = new CancellationTokenSource();

        // 一个线程持续读
        var reader = Task.Run(() =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                var read = ledger.Read();

                // 读侧永远不该看到 null 条目
                foreach (var entry in read.Entries)
                {
                    Assert.NotNull(entry);
                    Assert.False(string.IsNullOrEmpty(entry.Model));
                }
                reads++;
            }
            return reads;
        });

        Parallel.For(0, 200, i => ledger.Append(Record(model: $"m{i % 5}")));

        stop.Cancel();
        var readCount = reader.GetAwaiter().GetResult();

        Assert.True(readCount > 0, "读侧一次都没跑起来");
        Assert.Equal(200, ledger.Read().Entries.Count);
    }

    [Fact]
    public void 缓冲模式下的条目会被落盘()
    {
        using var temp = new TempLedger();
        using (var ledger = temp.Open(buffered: true))
        {
            for (var i = 0; i < 30; i++) ledger.Append(Record(credit: 0.1));

            // Dispose 会把剩下的一次性写完 —— 正常关闭不该丢数据
        }

        var read = UsageLedger.ParseText(temp.Text);

        Assert.Equal(30, read.Entries.Count);
        Assert.Equal(0, read.SkippedLines);
    }

    [Fact]
    public void 缓冲模式下超过阈值会同步落一批()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open(buffered: true);

        // 512 是队列上限，超过它会同步 Flush
        for (var i = 0; i < 600; i++) ledger.Append(Record());

        // 不 Dispose 也应该已经落了一部分（不是全堆在内存里）
        Assert.True(File.Exists(temp.Path));
        Assert.True(temp.LineCount > 0, "超过队列上限后应该已经落盘一部分");
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ summarizeUsage（对齐旧版）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 抽取上游usage的各个字段()
    {
        var usage = new JsonObject
        {
            ["prompt_tokens"] = 100,
            ["completion_tokens"] = 50,
            ["total_tokens"] = 150,
            ["credit"] = 0.22,
            ["prompt_cache_hit_tokens"] = 80,
            ["prompt_cache_miss_tokens"] = 20,
            ["cache_read_input_tokens"] = 80,
            ["prompt_cache_write_tokens"] = 5,
            ["completion_thinking_tokens"] = 30,
        };

        var summary = UsageSummary.From(usage);

        Assert.Equal(100, summary.PromptTokens);
        Assert.Equal(50, summary.CompletionTokens);
        Assert.Equal(150, summary.TotalTokens);
        Assert.Equal(0.22, summary.Credit!.Value, 6);
        Assert.Equal(80, summary.CacheHit);
        Assert.Equal(20, summary.CacheMiss);
        Assert.Equal(80, summary.CacheRead);
        Assert.Equal(5, summary.CacheWrite);
        Assert.Equal(30, summary.Reasoning);
    }

    [Fact]
    public void 推理token有第二个位置()
    {
        // 上游不给 completion_thinking_tokens 时退回 OpenAI 标准位置
        var usage = new JsonObject
        {
            ["completion_tokens"] = 100,
            ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 42 },
        };

        Assert.Equal(42, UsageSummary.From(usage).Reasoning);
    }

    [Fact]
    public void 数字给成字符串也认()
    {
        // 上游偶尔这么干
        var usage = new JsonObject
        {
            ["prompt_tokens"] = "2541",
            ["credit"] = "0.22",
        };

        var summary = UsageSummary.From(usage);

        Assert.Equal(2541, summary.PromptTokens);
        Assert.Equal(0.22, summary.Credit!.Value, 6);
    }

    [Fact]
    public void 认不出的值当没给而不是当零()
    {
        var usage = new JsonObject
        {
            ["prompt_tokens"] = "不是数字",
            ["credit"] = new JsonArray(1, 2),
        };

        var summary = UsageSummary.From(usage);

        // 当 0 会参与聚合把总数带歪；当「没给」不会
        Assert.Null(summary.PromptTokens);
        Assert.Null(summary.Credit);
    }

    [Fact]
    public void 空usage得到空摘要()
    {
        Assert.True(UsageSummary.From(null).IsEmpty);
        Assert.True(UsageSummary.From(new JsonObject()).IsEmpty);
    }

    [Fact]
    public void 没有total时自己算总量()
    {
        var entry = new UsageLedgerRecord
        {
            Iso = "x",
            Ts = 1,
            Model = "hy3",
            PromptTokens = 100,
            CompletionTokens = 50,
        };

        Assert.Equal(150, entry.EffectiveTotalTokens);
    }

    // ══════════════════════════════════════════════════════════
    // ⑥ 脱敏：账本不该成为泄露通道
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 正常账号标签能原样读出()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        // 真实标签的形状：uid 前 8 位 + 企业 id
        ledger.Append(Record(account: "ak9f2b1c:ent-12345"));

        var entry = ledger.Read().Entries.Single();

        Assert.Equal("ak9f2b1c:ent-12345", entry.Account);
    }

    [Fact]
    public void 记账入口不要求脱敏由调用方保证()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        // ⚠ 这是刻意的：UsageLedger 是**通用账本**，它不知道什么是 token。
        // 脱敏的责任在写侧（GatewayApp.AccountTagOf 只取 uid 前 8 位）
        // 和读侧的出口兜底（UsageEndpoints.SafeLabel）。
        // 这条测试把这个约定钉住 —— 将来谁想在这里加脱敏，
        // 会先看到它并知道那是有意为之的分层。
        const string tag = "ak9f2b1c:ent-12345";
        ledger.Append(Record(account: tag));

        Assert.Equal(tag, ledger.Read().Entries.Single().Account);
    }

    [Fact]
    public void 账本文件里不出现提示词或正文()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        // 账本记的是成本数字，不是内容。造一条带标记的账目，
        // 确认落盘的 JSON 里只有约定的那些字段。
        ledger.Append(Record(model: "hy3", account: "ak9f2b1c:ent-1", credit: 0.22));

        var raw = temp.Text;

        // 字段白名单 —— 出现任何别的键都说明有人往账本里塞了东西
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "iso", "ts", "model", "usedModel", "account", "kind", "outcome",
            "elapsedMs", "attempts", "promptTokens", "completionTokens",
            "totalTokens", "credit", "cacheHit", "cacheMiss", "cacheRead",
            "cacheWrite", "reasoning",
        };

        var obj = JsonNode.Parse(raw.Split('\n').First(l => l.Trim().Length > 0))!.AsObject();

        foreach (var (key, _) in obj)
        {
            Assert.True(allowed.Contains(key), $"账本里出现了非约定字段：{key}");
        }

        // 没有能装下提示词/正文的字段
        Assert.DoesNotContain("prompt\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("content", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("token\"", raw.Replace("\"promptTokens\"", "").Replace("\"completionTokens\"", "").Replace("\"totalTokens\"", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void 落盘内容不含accessToken字段()
    {
        using var temp = new TempLedger();
        using var ledger = temp.Open();

        ledger.Append(Record(account: "ak9f2b1c:ent-1"));

        // 账本**绝不能**出现凭据字段 —— 它是长期落盘的文件，
        // 写进去就等于永久留下
        var raw = temp.Text;

        Assert.DoesNotContain("accessToken", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshToken", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", raw, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════
    // ③ 轮转判定的静默失败（t84 修的缺陷）
    //
    // ## 这一组在防什么
    //
    // `ShouldRotate()` 原来 catch 之后直接 `return false` **不记日志**，
    // 把两件完全不同的事变成同一个结果：
    //   ① 文件很小，不用轮转        ← 正常
    //   ② 读不到大小，所以不知道     ← 异常，后果是**文件无界增长**
    //
    // ② 没有信号 → 没人看得见 → 磁盘满之前都不会有人发现。
    //
    // 这与 `/admin/pools` 的 `accountCount` 恒 0 是同一形态
    // （见 docs/silent-failure-guards.md §2⑤）。
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 造一个「能写进去、但读文件大小会失败」的账本路径。
    ///
    /// ## 为什么不能简单地「传一个目录」
    ///
    /// 第一版我传了个目录，结果失败在**写入**那一步
    /// （`File.WriteAllText(目录)` → `UnauthorizedAccessException`），
    /// 报的是「记账落盘失败」——**根本没走到 ShouldRotate**。
    /// 断言因此报「Warning 里没有路径」，而真实原因是压根没产生那条 Warning。
    ///
    /// ## 真正要造的形状
    ///
    /// `ShouldRotate()` 读的是 `new FileInfo(_path)` 的 `Length`。
    /// 要让**读大小**失败而**写入**成功，用「账本文件本身正常，
    /// 但把它的父目录换成一个会让 FileInfo 抛异常的路径」不现实。
    ///
    /// 更直接的办法：**让路径指向一个真实存在的目录，但账本文件先建好**。
    /// 不行 —— 路径就是一个东西，不能既当文件又当目录。
    ///
    /// ⇒ 所以这里换一个**稳定且跨平台**的思路：
    /// 用一个**名字里带非法字符之外、但父目录会被删除**的路径。
    /// 删掉父目录后，`FileInfo.Length` 在多数平台上抛
    /// `DirectoryNotFoundException`（`IOException` 的子类）。
    ///
    /// ⚠ 但写入也会失败。**这不是问题** —— 因为本用例要验的是
    /// `ShouldRotate()` 的日志，而它是在**写入之前**被调用的
    /// （见 `WriteOne` 的顺序）。所以先删目录再 Append，
    /// `ShouldRotate` 先抛异常路径 → 记 Warning → 然后写入才失败。
    ///
    /// ## ⚠ 为什么不构造真异常，而是直接调 catch 里那个方法
    ///
    /// 我试过构造 —— 用真 C# 探针跑了四种坏路径，**四种的
    /// `FileInfo.Exists` 全是 `False`**：
    ///
    /// ```
    /// 真实目录        Exists=False   ← 走不到 Length
    /// 父目录已删      Exists=False   ← 同上
    /// 不存在          Exists=False   ← 同上
    /// 超长路径        Exists=False   ← 同上
    /// ```
    ///
    /// 而生产代码是 `if (!info.Exists) return false;` ——
    /// 那四种都走了**「文件不存在」那条正确的路**，压根到不了 catch。
    ///
    /// ⇒ **在本平台上造不出「Exists 为 true 但 Length 抛」。**
    ///
    /// 硬造只能依赖平台细节（那种测试会变成「在别人机器上偶发红」的
    /// 噪音源，而**偶尔红的检查会侵蚀信任**）。
    ///
    /// 所以改成：**catch 分支把「该记什么」委托给一个可调用的方法**，
    /// 测试直接调它。验的仍是真实生产路径（`ShouldRotate` 的 catch
    /// 里就是调 `LogRotateCheckFailed`），不是复刻一份
    /// 「我以为 catch 会这么做」的逻辑。
    /// </summary>
    [Fact]
    public void 读不到文件大小时_必须记Warning说明本次不轮转()
    {
        using var temp = new TempLedger();
        var logger = new RecordingLogger();
        using var ledger = new UsageLedger(temp.Path, logger);

        // 这是 ShouldRotate() 的 catch 分支**实际调用的那个方法**
        ledger.LogRotateCheckFailed(temp.Path, new IOException("句柄被占用"));

        // ★ 核心断言：**必须**有 Warning
        Assert.True(
            logger.HasWarning,
            "读不到文件大小时必须记 Warning —— 否则「不轮转」和「不用轮转」不可区分，"
            + "账本会无界增长而没人知道。实际日志：" + logger.AllText);
    }

    [Fact]
    public void 读不到文件大小的Warning含路径与异常类型()
    {
        using var temp = new TempLedger();
        var logger = new RecordingLogger();
        using var ledger = new UsageLedger(temp.Path, logger);

        ledger.LogRotateCheckFailed(temp.Path, new UnauthorizedAccessException("拒绝访问"));

        var warning = logger.Entries
            .FirstOrDefault(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);

        Assert.False(string.IsNullOrEmpty(warning.Message), "应该有 Warning；实际：" + logger.AllText);

        // ★ 含**路径** —— 不然多账本场景下不知道是哪一个
        Assert.Contains(temp.Path, warning.Message, StringComparison.Ordinal);

        // ★ 含**异常类型** —— 否则「IO 失败」和「权限失败」分不出来，排查方向不同
        Assert.Contains(nameof(UnauthorizedAccessException), warning.Message, StringComparison.Ordinal);

        // ★ 说清后果：本次不轮转
        Assert.Contains("不轮转", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 文件不存在时不记Warning_避免噪音()
    {
        // ★ 反向用例：首次写入前的正常状态**不该刷日志**。
        //
        // 这条和上一条一样重要 —— 如果「文件不存在」也报 Warning，
        // 那么每次建新账本、每个新池第一次记账都会刷一条，
        // 真正的「读失败」就会被淹没。
        // （这正是 docs/silent-failure-guards.md 里
        //   AdminEndpoints 那条修法的同款取舍：
        //   文件不存在 → Debug；读失败 → Warning。）
        using var temp = new TempLedger();
        var logger = new RecordingLogger();

        // 文件还不存在（TempLedger 只建目录，不建文件）
        Assert.False(File.Exists(temp.Path), "前提：这个用例要求账本文件一开始不存在");

        using (var ledger = new UsageLedger(temp.Path, logger))
        {
            ledger.Append(Record());
        }

        Assert.False(
            logger.HasWarning,
            "文件不存在是正常状态（首次写入），不该记 Warning —— 记了就是噪音。"
            + "实际日志：" + logger.AllText);

        // 而且确实记进去了（证明它真的走到了 ShouldRotate 且正常工作）
        Assert.Equal(1, temp.LineCount);
    }

    [Fact]
    public void 各种IO异常都能被记为Warning_不只IOException()
    {
        // 权限失败与 IO 失败是**不同的排查方向**，两种都要留下信号。
        using var temp = new TempLedger();
        var logger = new RecordingLogger();
        using var ledger = new UsageLedger(temp.Path, logger);

        ledger.LogRotateCheckFailed(temp.Path, new IOException("磁盘错误"));
        ledger.LogRotateCheckFailed(temp.Path, new UnauthorizedAccessException("权限不足"));

        var warnings = logger.Entries
            .Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .ToList();

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains(nameof(IOException), StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains(nameof(UnauthorizedAccessException), StringComparison.Ordinal));
    }

    [Fact]
    public void 传null异常时_明确拒绝而不是静默乱记()
    {
        // 调用方传错东西时要**明确失败**，而不是产出一条没有异常类型的
        // 半截日志 —— 那会让排查的人以为「读失败但不知道为什么」。
        using var temp = new TempLedger();
        using var ledger = new UsageLedger(temp.Path, null);

        Assert.Throws<ArgumentNullException>(() => ledger.LogRotateCheckFailed(temp.Path, null!));
    }
}
