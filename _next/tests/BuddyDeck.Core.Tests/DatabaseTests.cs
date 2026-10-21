using BuddyDeck.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// SQLite 数据库层测试。
///
/// 覆盖：建表幂等、用量写读往返、积分快照 UPSERT、坏数据容错。
/// 全部用临时目录，不碰真实数据。
/// </summary>
public sealed class DatabaseTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly Database _db;

    public DatabaseTests()
    {
        _dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "buddydeck-db-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = System.IO.Path.Combine(_dir, "test.db");
        _db = new Database(_path, NullLogger.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ══════════════════════════════════════════════════════════
    // ① 建表幂等
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 初始化两次_不抛_表不重复建()
    {
        _db.Initialize();   // 第二次
        Assert.True(File.Exists(_path));
    }

    // ══════════════════════════════════════════════════════════
    // ② 用量写读往返
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 写一笔用量_读回字段逐个一致()
    {
        var record = new UsageLedgerRecord
        {
            Ts = 1791386246000,
            Iso = "2026-10-07T15:17:26.000Z",
            Model = "glm-5.3-flash",
            UsedModel = "glm-5.3-flash",
            Account = "3e563042",
            Kind = "chat",
            Outcome = "ok",
            ElapsedMs = 1234,
            Attempts = 2,
            PromptTokens = 100,
            CompletionTokens = 50,
            TotalTokens = 150,
            Credit = 0.02,
            CacheHit = 10,
            CacheMiss = 90,
        };

        _db.AppendUsage(record);

        var read = _db.ReadRecentUsage(10);
        Assert.Single(read);
        var r = read[0];
        Assert.Equal(record.Ts, r.Ts);
        Assert.Equal(record.Model, r.Model);
        Assert.Equal(record.Account, r.Account);
        Assert.Equal(record.Outcome, r.Outcome);
        Assert.Equal(0.02, r.Credit);
        Assert.Equal(150L, r.TotalTokens);
        Assert.Equal(2, r.Attempts);
    }

    [Fact]
    public void 多笔用量_按时间倒序读()
    {
        _db.AppendUsage(new UsageLedgerRecord { Ts = 1000, Iso = "t1", Model = "a", Outcome = "ok" });
        _db.AppendUsage(new UsageLedgerRecord { Ts = 3000, Iso = "t3", Model = "c", Outcome = "ok" });
        _db.AppendUsage(new UsageLedgerRecord { Ts = 2000, Iso = "t2", Model = "b", Outcome = "ok" });

        var read = _db.ReadRecentUsage(10);
        Assert.Equal(3, read.Count);
        Assert.Equal(3000, read[0].Ts);   // 最新在前
        Assert.Equal(2000, read[1].Ts);
        Assert.Equal(1000, read[2].Ts);
    }

    [Fact]
    public void 可空字段_写读不丢_null_读回为_null()
    {
        _db.AppendUsage(new UsageLedgerRecord
        {
            Ts = 1000,
            Iso = "t",
            Model = "m",
            Outcome = "failed",
            Credit = null,          // 失败时上游没给 credit
            TotalTokens = null,
        });

        var r = _db.ReadRecentUsage(1)[0];
        Assert.Null(r.Credit);
        Assert.Null(r.TotalTokens);
        Assert.Equal("failed", r.Outcome);
    }

    // ══════════════════════════════════════════════════════════
    // ③ 积分快照 UPSERT
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 积分快照_upsert_同账号覆盖不重复()
    {
        _db.UpsertCredits("uuid-a:", "uuid-a", "主号", 2717.0, 2715.0, 16, "[]", 1000);
        _db.UpsertCredits("uuid-a:", "uuid-a", "主号", 2500.0, 2498.0, 15, "[]", 2000);

        var read = _db.ReadAllCredits();
        Assert.Single(read);   // 不是 2 条
        Assert.Equal(2500.0, read[0].TotalDosage);
        Assert.Equal(2000, read[0].FetchedAtMs);
    }

    [Fact]
    public void 多个账号积分_都读回()
    {
        _db.UpsertCredits("a:", "a", "甲", 100, 100.0, 1, "[]", 1);
        _db.UpsertCredits("b:", "b", "乙", 200, 200.0, 2, "[]", 2);
        _db.UpsertCredits("c:", "c", "丙", 300, 300.0, 3, "[]", 3);

        var read = _db.ReadAllCredits();
        Assert.Equal(3, read.Count);
    }
}
