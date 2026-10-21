using System.Text;
using BuddyDeck.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Core;

/// <summary>
/// 本机 SQLite 数据库 —— 项目的数据中枢。
///
/// ## 为什么上数据库（用户需求的架构升级）
///
/// 之前的存储是「文件各管一摊」：用量是追加式 JSONL（全量读，越读越慢）、
/// 积分每次现查上游（20 个号要几十秒）、限流历史是单独的 JSON。
/// 用户指出的方向对：**把每笔任务明细、积分消耗、账号信息、鉴权信息
/// 都存进数据库，界面从库里读** —— 秒回，且能按任意维度聚合。
///
/// ## 表设计
///
/// ```
/// usage        每笔请求的明细（模型/账号/积分消耗/token/缓存/结果/耗时）
/// credits      每个账号的积分快照（查一次存一次，界面秒读）
/// accounts     账号信息快照（label/uid/过期时刻，供界面离线读）
/// rate_limits  限流历史（号×模型，重启不丢）
/// ```
///
/// ## 与既有文件的关系（迁移期双写）
///
/// · `usage.jsonl`（UsageLedger）**继续写** —— 它是外部可编辑、可被
///   jq/awk 分析的格式，用户已经习惯。数据库是**查询加速层**，
///   记账时同步写两条，读端点优先走库。
/// · 以后逐步把读端点切到库，文件只作为可移植备份。
///
/// ## 连接管理
///
/// SQLite 的写是**单写者**，所以用一个共享连接 + 写锁串行写。
/// 读走独立连接（每次开，用完关），避免长连接占用。
/// </summary>
public sealed partial class Database : IDisposable
{
    private readonly string _path;
    private readonly ILogger _log;
    private readonly object _writeGate = new();

    private SqliteConnection? _writeConnection;

    /// <param name="path">数据库文件路径（.db）。父目录不存在会自动建。</param>
    /// <param name="log">日志。</param>
    public Database(string path, ILogger? log = null)
    {
        _path = path;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>数据库文件路径。</summary>
    public string Path => _path;

    [LoggerMessage(Level = LogLevel.Information, Message = "数据库就绪：{Path}")]
    private partial void LogReady(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "用量入库失败：{Message}")]
    private partial void LogUsageWriteFailed(string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "积分入库失败：{Message}")]
    private partial void LogCreditsWriteFailed(string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "读用量明细失败：{Message}")]
    private partial void LogUsageReadFailed(string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "读积分快照失败：{Message}")]
    private partial void LogCreditsReadFailed(string message);

    /// <summary>
    /// 初始化（建表）。**幂等** —— 已存在的库不会重建。
    ///
    /// 为什么启动时调用一次而不是惰性：启动时建表失败要立刻暴露
    /// （磁盘只读/权限问题），而不是等到第一笔记账才崩。
    /// </summary>
    public void Initialize()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // ★ 用 EF Core Code First 建表（实体定义在 Data/Entities.cs，
        //   关系配置在 Data/BuddyDeckDbContext.cs）。表结构不再手写 SQL。
        //   EnsureCreated：数据库不存在则建全部表；已存在则跳过。
        using (var db = Data.BuddyDeckDb.Open(_path))
        {
            db.Database.EnsureCreated();
        }

        // 旧库原地升级：补上后来新增的列（SQLite 不支持列级 IF NOT EXISTS，
        // EF 的 EnsureCreated 也不改已有表，所以迁移列仍用手写 ALTER）。
        var conn = GetWriteConnection();
        lock (_writeGate)
        {
            EnsureColumn(conn, "credits", "total_remaining", "REAL NOT NULL DEFAULT 0");
            EnsureColumn(conn, "usage", "pool_id", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "credit_history", "pool_id", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "credits", "pool_id", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "credit_packages", "pool_id", "TEXT NOT NULL DEFAULT ''");
        }

        LogReady(_path);
    }

    /// <summary>表里没这列就加上（旧库原地升级用）。</summary>
    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    /// <summary>
    /// 记一笔用量（与 UsageLedger 同步写）。**绝不抛** —— 记账是旁路。
    /// </summary>
    public void AppendUsage(UsageLedgerRecord record)
    {
        try
        {
            RunWrite(db => db.Usage.Add(ToEntity(record)));
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageWriteFailed(ex.Message);
        }
    }

    /// <summary>
    /// 串行化执行一次数据库写操作。
    ///
    /// ⚠ 为什么必须：SQLite 是**单写者**，旧手写 SQL 用 lock(_writeGate)
    /// 保证所有写串行。迁移 EF 后每个写方法各自 Open 新 DbContext，
    /// 多个后台任务并发写（report 回调写 tasks + 主任务写 credits）会
    /// 互相抢写锁 —— busy_timeout=0 时立即抛错被吞、或无限等待。
    /// 这个 helper 把「开连接 → 改数据 → SaveChanges」整段锁住，
    /// 恢复单写者的不变量。任务卡死（progress 停在 5%）就是并发写锁导致。
    /// </summary>
    private void RunWrite(Action<Data.BuddyDeckDbContext> mutate)
    {
        lock (_writeGate)
        {
            using var db = Data.BuddyDeckDb.Open(_path);
            mutate(db);
            db.SaveChanges();
        }
    }

    private static Data.UsageEntity ToEntity(UsageLedgerRecord r) => new()    {
        Ts = r.Ts,
        Iso = r.Iso,
        Model = r.Model,
        UsedModel = r.UsedModel,
        Account = r.Account,
        Kind = r.Kind,
        Outcome = r.Outcome,
        ElapsedMs = r.ElapsedMs,
        Attempts = r.Attempts,
        PromptTokens = r.PromptTokens,
        CompletionTokens = r.CompletionTokens,
        TotalTokens = r.TotalTokens,
        Credit = r.Credit,
        CacheHit = r.CacheHit,
        CacheMiss = r.CacheMiss,
        CacheRead = r.CacheRead,
        CacheWrite = r.CacheWrite,
        Reasoning = r.Reasoning,
    };

    private static UsageLedgerRecord ToRecord(Data.UsageEntity e) => new()
    {
        Ts = e.Ts,
        Iso = e.Iso,
        Model = e.Model,
        UsedModel = e.UsedModel,
        Account = e.Account,
        Kind = e.Kind,
        Outcome = e.Outcome,
        ElapsedMs = e.ElapsedMs,
        Attempts = e.Attempts,
        PromptTokens = e.PromptTokens,
        CompletionTokens = e.CompletionTokens,
        TotalTokens = e.TotalTokens,
        Credit = e.Credit,
        CacheHit = e.CacheHit,
        CacheMiss = e.CacheMiss,
        CacheRead = e.CacheRead,
        CacheWrite = e.CacheWrite,
        Reasoning = e.Reasoning,
    };

    /// <summary>
    /// 记一个账号的积分快照（UPSERT）+ 历史快照追加。
    /// </summary>
    public void UpsertCredits(
        string accountId,
        string accountTag,
        string label,
        double totalDosage,
        double totalRemaining,
        int totalCount,
        string packagesJson,
        long fetchedAtMs)
    {
        try
        {
            RunWrite(db =>
            {
                // ① 当前快照（UPSERT）
                var existing = db.Credits.Find(accountId);
                double? previous = existing?.TotalRemaining;
                if (existing is null)
                {
                    db.Credits.Add(new Data.CreditEntity
                    {
                        AccountId = accountId,
                        AccountTag = accountTag,
                        Label = label,
                        TotalDosage = totalDosage,
                        TotalRemaining = totalRemaining,
                        TotalCount = totalCount,
                        PackagesJson = packagesJson,
                        FetchedAtMs = fetchedAtMs,
                    });
                }
                else
                {
                    existing.AccountTag = accountTag;
                    existing.Label = label;
                    existing.TotalDosage = totalDosage;
                    existing.TotalRemaining = totalRemaining;
                    existing.TotalCount = totalCount;
                    existing.PackagesJson = packagesJson;
                    existing.FetchedAtMs = fetchedAtMs;
                }

                // ② 历史快照（追加）
                db.CreditHistory.Add(new Data.CreditHistoryEntity
                {
                    AccountId = accountId,
                    AccountTag = accountTag,
                    Label = label,
                    Ts = fetchedAtMs,
                    TotalDosage = totalRemaining,
                    TotalCount = totalCount,
                    Delta = previous is null ? null : totalRemaining - previous.Value,
                });
            });
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogCreditsWriteFailed(ex.Message);
        }
    }

    /// <summary>
    /// 写积分包明细（规范化，替掉该账号的旧包行）。
    /// </summary>
    public void ReplaceCreditPackages(string accountId, IReadOnlyList<CreditPackageRow> packages, long nowMs)
    {
        try
        {
            RunWrite(db =>
            {
                var old = db.CreditPackages.Where(p => p.AccountId == accountId).ToList();
                db.CreditPackages.RemoveRange(old);

                foreach (var p in packages)
                {
                    db.CreditPackages.Add(new Data.CreditPackageEntity
                    {
                        AccountId = accountId,
                        PackageCode = p.PackageCode,
                        PackageName = p.PackageName,
                        ProductName = p.ProductName,
                        CapacityUnit = p.CapacityUnit,
                        Size = p.Size,
                        Remain = p.Remain,
                        Used = p.Used,
                        CycleStart = p.CycleStartTime,
                        CycleEnd = p.CycleEndTime,
                        Status = p.Status,
                        GrantSource = p.GrantSource,
                        UpdatedAtMs = nowMs,
                    });
                }
            });
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogCreditsWriteFailed(ex.Message);
        }
    }

    /// <summary>记一条任务流事件（任务详情）。</summary>
    public void AppendTaskEvent(string taskId, int seq, string level, string message, int? progress, string? dataJson)
    {
        try
        {
            RunWrite(db => db.TaskEvents.Add(new Data.TaskEventEntity
            {
                TaskId = taskId,
                Seq = seq,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Level = level,
                Message = message,
                Progress = progress,
                DataJson = dataJson,
            }));
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageWriteFailed(ex.Message);
        }
    }

    /// <summary>读一个账号最近的积分历史（倒序）。</summary>
    public IReadOnlyList<CreditHistoryRow> ReadCreditHistory(string accountId, int limit)
    {
        try
        {
            using var db = Data.BuddyDeckDb.Open(_path);
            return db.CreditHistory
                .Where(h => h.AccountId == accountId)
                .OrderByDescending(h => h.Ts)
                .ThenByDescending(h => h.Id)
                .Take(Math.Clamp(limit, 1, 500))
                .Select(h => new CreditHistoryRow(
                    h.Id, h.AccountId, h.AccountTag, h.Label,
                    h.Ts, h.TotalDosage, h.TotalCount, h.Delta))
                .ToList();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogCreditsReadFailed(ex.Message);
            return [];
        }
    }

    /// <summary>读一个任务的事件流（按 seq 升序）。</summary>
    public IReadOnlyList<TaskEventRow> ReadTaskEvents(string taskId, int limit)
    {
        try
        {
            using var db = Data.BuddyDeckDb.Open(_path);
            return db.TaskEvents
                .Where(e => e.TaskId == taskId)
                .OrderBy(e => e.Seq)
                .ThenBy(e => e.Id)
                .Take(Math.Clamp(limit, 1, 2000))
                .Select(e => new TaskEventRow(
                    e.Seq, e.Ts, e.Level, e.Message, e.Progress, e.DataJson))
                .ToList();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageReadFailed(ex.Message);
            return [];
        }
    }

    /// <summary>
    /// 读最近 N 笔用量明细（按时间倒序）。界面按账号/模型过滤展示。
    /// </summary>
    public IReadOnlyList<UsageLedgerRecord> ReadRecentUsage(int limit)
    {
        try
        {
            using var db = Data.BuddyDeckDb.Open(_path);
            return db.Usage
                .OrderByDescending(u => u.Ts)
                .ThenByDescending(u => u.Id)
                .Take(Math.Clamp(limit, 1, 100000))
                .Select(u => ToRecord(u))
                .ToList();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageReadFailed(ex.Message);
            return [];
        }
    }

    /// <summary>
    /// 读全部积分快照（界面账号页秒读，不再逐个查上游）。
    /// </summary>
    public IReadOnlyList<CreditSnapshotRow> ReadAllCredits()
    {
        try
        {
            using var db = Data.BuddyDeckDb.Open(_path);
            return db.Credits
                .OrderByDescending(c => c.FetchedAtMs)
                .Select(c => new CreditSnapshotRow(
                    c.AccountId, c.AccountTag, c.Label,
                    c.TotalDosage, c.TotalRemaining, c.TotalCount,
                    c.PackagesJson, c.FetchedAtMs))
                .ToList();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogCreditsReadFailed(ex.Message);
            return [];
        }
    }

    // ══ 任务表（费时操作的任务库）════════════════════════

    /// <summary>新建一个任务（返回任务 id）。</summary>
    public string CreateTask(string kind, string title, long nowMs)
    {
        var id = Guid.NewGuid().ToString("N");
        try
        {
            RunWrite(db => db.Tasks.Add(new Data.TaskEntity
            {
                Id = id,
                Kind = kind,
                Title = title,
                Status = "pending",
                Progress = 0,
                Description = "排队中",
                CreatedAtMs = nowMs,
            }));
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageWriteFailed(ex.Message);
        }
        return id;
    }

    /// <summary>更新任务进度（状态 + 0-100 进度 + 实时描述）。</summary>
    public void UpdateTask(string id, string status, int progress, string description)
    {
        try
        {
            RunWrite(db =>
            {
                var task = db.Tasks.Find(id);
                if (task is null) return;
                task.Status = status;
                task.Progress = Math.Clamp(progress, 0, 100);
                task.Description = description;
                task.StartedAtMs ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            });
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageWriteFailed(ex.Message);
        }
    }

    /// <summary>任务完成（带结果 JSON）或失败（带错误）。</summary>
    public void FinishTask(string id, string status, string resultJson, string? error)
    {
        try
        {
            RunWrite(db =>
            {
                var task = db.Tasks.Find(id);
                if (task is null) return;
                task.Status = status;
                task.ResultJson = resultJson;
                task.Error = error;
                if (status == "done") task.Progress = 100;
                task.FinishedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            });
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageWriteFailed(ex.Message);
        }
    }

    /// <summary>清理旧任务：只保留最近 keep 条，删更老的连同事件流。</summary>
    public void PruneTasks(int keep)
    {
        if (keep < 1) return;
        try
        {
            RunWrite(db =>
            {
                var oldIds = db.Tasks
                    .OrderByDescending(t => t.CreatedAtMs)
                    .Skip(keep)
                    .Select(t => t.Id)
                    .ToList();
                if (oldIds.Count == 0) return;

                var oldEvents = db.TaskEvents.Where(e => oldIds.Contains(e.TaskId));
                db.TaskEvents.RemoveRange(oldEvents);
                var oldTasks = db.Tasks.Where(t => oldIds.Contains(t.Id));
                db.Tasks.RemoveRange(oldTasks);
            });
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageWriteFailed(ex.Message);
        }
    }

    /// <summary>读任务列表（最近 N 个，按创建时间倒序）。</summary>
    public IReadOnlyList<TaskRow> ReadTasks(int limit)
    {
        try
        {
            using var db = Data.BuddyDeckDb.Open(_path);
            return db.Tasks
                .OrderByDescending(t => t.CreatedAtMs)
                .Take(Math.Clamp(limit, 1, 200))
                .Select(t => new TaskRow(
                    t.Id, t.Kind, t.Title, t.Status, t.Progress,
                    t.Description, t.ResultJson, t.Error,
                    t.CreatedAtMs, t.FinishedAtMs))
                .ToList();
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageReadFailed(ex.Message);
            return [];
        }
    }

    /// <summary>批量导入用量记录（JSONL → 数据库迁移用，幂等按 ts 去重）。</summary>
    public void ImportUsage(IEnumerable<UsageLedgerRecord> records)
    {
        try
        {
            RunWrite(db =>
            {
                foreach (var record in records)
                {
                    var exists = db.Usage.Any(u =>
                        u.Ts == record.Ts && u.Iso == record.Iso &&
                        u.Model == record.Model && u.Account == record.Account);
                    if (exists) continue;
                    db.Usage.Add(ToEntity(record));
                }
            });
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageWriteFailed(ex.Message);
        }
    }

    // ── 池 ↔ 账号 关联存取（跨池汇总用）────────────────

    /// <summary>写/更新一个池的快照（UPSERT）。</summary>
    public void UpsertPool(string id, string displayName, int port, string apiKey,
        string vaultPath, string directoryName, bool isDefault, bool running,
        string? lastError, long nowMs)
    {
        try
        {
            RunWrite(db =>
            {
                var pool = db.Pools.Find(id);
                if (pool is null)
                {
                    db.Pools.Add(new Data.PoolEntity
                    {
                        Id = id,
                        DisplayName = displayName,
                        Port = port,
                        ApiKey = apiKey,
                        VaultPath = vaultPath,
                        DirectoryName = directoryName,
                        IsDefault = isDefault,
                        Running = running,
                        LastError = lastError,
                        UpdatedAtMs = nowMs,
                    });
                }
                else
                {
                    pool.DisplayName = displayName;
                    pool.Port = port;
                    pool.ApiKey = apiKey;
                    pool.VaultPath = vaultPath;
                    pool.DirectoryName = directoryName;
                    pool.IsDefault = isDefault;
                    pool.Running = running;
                    pool.LastError = lastError;
                    pool.UpdatedAtMs = nowMs;
                }
            });
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageWriteFailed(ex.Message);
        }
    }

    /// <summary>建立池 ↔ 账号 关联（幂等）。</summary>
    public void LinkPoolAccount(string poolId, string accountId)
    {
        try
        {
            RunWrite(db =>
            {
                var pool = db.Pools.Include(p => p.Accounts).FirstOrDefault(p => p.Id == poolId);
                if (pool is null) return;
                var account = db.Accounts.Find(accountId);
                if (account is null) return;
                if (!pool.Accounts.Contains(account))
                {
                    pool.Accounts.Add(account);
                }
            });
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageWriteFailed(ex.Message);
        }
    }

    /// <summary>跨池积分汇总：所有池的 credits.total_remaining 加总。</summary>
    public double SumAllPoolCredits()
    {
        try
        {
            using var db = Data.BuddyDeckDb.Open(_path);
            return db.Credits.Sum(c => c.TotalRemaining);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageReadFailed(ex.Message);
            return 0;
        }
    }

    // ── 账号模型冷却（用户需求：入库 + 更新 + 显示）────────────────

    /// <summary>替换一个账号的全部冷却记录（先删旧、再插新）。</summary>
    public void ReplaceAccountCooldowns(string accountId, IReadOnlyDictionary<string, long> cooldowns, long nowMs)
    {
        try
        {
            RunWrite(db =>
            {
                var old = db.AccountCooldowns.Where(c => c.AccountId == accountId).ToList();
                db.AccountCooldowns.RemoveRange(old);

                foreach (var (model, until) in cooldowns)
                {
                    db.AccountCooldowns.Add(new Data.AccountCooldownEntity
                    {
                        AccountId = accountId,
                        Model = model,
                        UntilMs = until,
                        UpdatedAtMs = nowMs,
                    });
                }
            });
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageWriteFailed(ex.Message);
        }
    }

    /// <summary>读一个账号的冷却（模型 → 解锁时刻，只含未过期的）。</summary>
    public IReadOnlyDictionary<string, long> ReadAccountCooldowns(string accountId)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            using var db = Data.BuddyDeckDb.Open(_path);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var c in db.AccountCooldowns
                .Where(c => c.AccountId == accountId && c.UntilMs > now))
            {
                result[c.Model] = c.UntilMs;
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogUsageReadFailed(ex.Message);
        }
        return result;
    }

    // ── 设置存取（meta 表）────────────────────────────────

    /// <summary>写一个键值对进 meta 表（UPSERT）。</summary>
    public void SetMeta(string key, string value)
    {
        try
        {
            RunWrite(db =>
            {
                var meta = db.Meta.Find(key);
                if (meta is null) db.Meta.Add(new Data.MetaEntity { Key = key, Value = value });
                else meta.Value = value;
            });
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogCreditsWriteFailed(ex.Message);
        }
    }

    /// <summary>读一个键的值。不存在或读失败返回 null。</summary>
    public string? GetMeta(string key)
    {
        try
        {
            using var db = Data.BuddyDeckDb.Open(_path);
            return db.Meta.Find(key)?.Value;
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            LogCreditsReadFailed(ex.Message);
            return null;
        }
    }

    // ── 连接管理 ──────────────────────────────────────────────
    private SqliteConnection GetWriteConnection()
    {
        if (_writeConnection is null)
        {
            _writeConnection = new SqliteConnection($"Data Source={_path}");
            _writeConnection.Open();
            // WAL 模式：读写不互相阻塞，界面读时不影响网关写。
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            cmd.ExecuteNonQuery();
        }
        return _writeConnection;
    }

    /// <summary>每次读开一个独立连接（用完即关）。</summary>
    private SqliteConnection OpenReadConnection()
    {
        var conn = new SqliteConnection($"Data Source={_path}");
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        try
        {
            _writeConnection?.Close();
            _writeConnection?.Dispose();
        }
        catch
        {
            // 关库失败不拦进程退出
        }
        _writeConnection = null;
    }
}

/// <summary>积分快照的一行（读库用）。</summary>
/// <param name="AccountId">完整账号 id（uuid:enterpriseId）。</param>
/// <param name="AccountTag">uid 前 8 位 + ':'。</param>
/// <param name="Label">账号标签。</param>
/// <param name="TotalDosage">上游 TotalDosage（累计发放量）。</param>
/// <param name="TotalRemaining">各包 remain 累加（能用余额）——**界面用这个**。</param>
/// <param name="TotalCount">积分包个数。</param>
/// <param name="PackagesJson">积分包明细（JSON 数组）。</param>
/// <param name="FetchedAtMs">查询时刻（Unix 毫秒）。</param>
public sealed record CreditSnapshotRow(
    string AccountId,
    string AccountTag,
    string Label,
    double TotalDosage,
    double TotalRemaining,
    int TotalCount,
    string PackagesJson,
    long FetchedAtMs);

/// <summary>任务库的一行（读库用）。</summary>
/// <param name="Id">任务 id。</param>
/// <param name="Kind">任务类型（credits / checkin / renew / ...）。</param>
/// <param name="Title">任务标题。</param>
/// <param name="Status">pending / running / done / failed / cancelled。</param>
/// <param name="Progress">0-100。</param>
/// <param name="Description">实时进度描述。</param>
/// <param name="Result">结果 JSON（完成时）。</param>
/// <param name="Error">失败原因。</param>
/// <param name="CreatedAtMs">创建时刻。</param>
/// <param name="FinishedAtMs">完成时刻（未完成时为 null）。</param>
public sealed record TaskRow(
    string Id,
    string Kind,
    string Title,
    string Status,
    int Progress,
    string Description,
    string? Result,
    string? Error,
    long CreatedAtMs,
    long? FinishedAtMs);

/// <summary>一个积分包的规范化行（写/读 credit_packages 用）。</summary>
public sealed record CreditPackageRow(
    string PackageCode,
    string PackageName,
    string ProductName,
    string CapacityUnit,
    double Size,
    double Remain,
    double Used,
    string CycleStartTime,
    string CycleEndTime,
    int Status,
    string GrantSource);

/// <summary>积分历史快照的一行（读 credit_history 用）。</summary>
/// <param name="Id">自增主键。</param>
/// <param name="AccountId">账号 id（uid）。</param>
/// <param name="AccountTag">账号标签（uid 前 8 位 + 冒号）。</param>
/// <param name="Label">账号显示名。</param>
/// <param name="Ts">快照时刻（Unix 毫秒）。</param>
/// <param name="TotalDosage">当时的总积分。</param>
/// <param name="TotalCount">当时的包数。</param>
/// <param name="Delta">相对上一次拉取的变化（负 = 消耗）。首次为 null。</param>
public sealed record CreditHistoryRow(
    long Id,
    string AccountId,
    string AccountTag,
    string Label,
    long Ts,
    double TotalDosage,
    int TotalCount,
    double? Delta);

/// <summary>任务流里的一条事件（读 task_events 用）。</summary>
/// <param name="Seq">步序（从 1 开始）。</param>
/// <param name="Ts">时刻（Unix 毫秒）。</param>
/// <param name="Level">info / ok / warn / error。</param>
/// <param name="Message">这一步干了什么。</param>
/// <param name="Progress">当时的进度 0-100（可能为空）。</param>
/// <param name="DataJson">附加数据（如某账号的结果 JSON）。</param>
public sealed record TaskEventRow(
    int Seq,
    long Ts,
    string Level,
    string Message,
    int? Progress,
    string? DataJson);
