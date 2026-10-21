using Microsoft.EntityFrameworkCore;

namespace BuddyDeck.Core.Data;

/// <summary>
/// EF Core DbContext —— 数据中枢的 ORM 层。
///
/// 数据库文件：保险库同目录的 `buddydeck.db`。
/// Code First：实体在 <see cref="Entities"/> 里，表结构由
/// <see cref="OnModelCreating"/> 声明，`EnsureCreated` 时建表。
///
/// 旧版 `Database.cs`（手写 SQL）保留公开 API，内部逐步迁到
/// 这个 DbContext；新的跨池关联查询直接走 EF。
/// </summary>
public sealed class BuddyDeckDbContext : DbContext
{
    public BuddyDeckDbContext(DbContextOptions<BuddyDeckDbContext> options) : base(options) { }

    public DbSet<PoolEntity> Pools => Set<PoolEntity>();
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    public DbSet<CreditEntity> Credits => Set<CreditEntity>();
    public DbSet<CreditPackageEntity> CreditPackages => Set<CreditPackageEntity>();
    public DbSet<CreditHistoryEntity> CreditHistory => Set<CreditHistoryEntity>();
    public DbSet<UsageEntity> Usage => Set<UsageEntity>();
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<TaskEventEntity> TaskEvents => Set<TaskEventEntity>();
    public DbSet<RateLimitEntity> RateLimits => Set<RateLimitEntity>();
    public DbSet<AccountCooldownEntity> AccountCooldowns => Set<AccountCooldownEntity>();
    public DbSet<MetaEntity> Meta => Set<MetaEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // ── 号池 ──────────────────────────────────────────────
        modelBuilder.Entity<PoolEntity>(e =>
        {
            e.ToTable("pools");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DisplayName).HasColumnName("display_name");
            e.Property(x => x.Port).HasColumnName("port");
            e.Property(x => x.ApiKey).HasColumnName("api_key");
            e.Property(x => x.VaultPath).HasColumnName("vault_path");
            e.Property(x => x.DirectoryName).HasColumnName("directory_name");
            e.Property(x => x.IsDefault).HasColumnName("is_default");
            e.Property(x => x.Running).HasColumnName("running");
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.Property(x => x.UpdatedAtMs).HasColumnName("updated_at");
        });

        // ── 账号 ──────────────────────────────────────────────
        modelBuilder.Entity<AccountEntity>(e =>
        {
            e.ToTable("accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Uid).HasColumnName("uid");
            e.Property(x => x.EnterpriseId).HasColumnName("enterprise_id");
            e.Property(x => x.Label).HasColumnName("label");
            e.Property(x => x.Domain).HasColumnName("domain");
            e.Property(x => x.ExpiresAtMs).HasColumnName("expires_at_ms");
            e.Property(x => x.RefreshExpiresAtMs).HasColumnName("refresh_expires_at_ms");
            e.Property(x => x.Active).HasColumnName("active");
            e.Property(x => x.Frozen).HasColumnName("frozen");
            e.Property(x => x.FailureCount).HasColumnName("failure_count");
            e.Property(x => x.UpdatedAtMs).HasColumnName("updated_at");
        });

        // ── 池 ↔ 账号 多对多 ─────────────────────────────────
        modelBuilder.Entity<PoolEntity>()
            .HasMany(p => p.Accounts)
            .WithMany(a => a.Pools)
            .UsingEntity<Dictionary<string, object>>(
                "pool_accounts",
                j => j.HasOne<AccountEntity>().WithMany().HasForeignKey("account_id"),
                j => j.HasOne<PoolEntity>().WithMany().HasForeignKey("pool_id"));

        // ── 积分快照 ─────────────────────────────────────────
        modelBuilder.Entity<CreditEntity>(e =>
        {
            e.ToTable("credits");
            e.HasKey(x => x.AccountId);
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.AccountTag).HasColumnName("account_tag");
            e.Property(x => x.Label).HasColumnName("label");
            e.Property(x => x.TotalDosage).HasColumnName("total_dosage");
            e.Property(x => x.TotalRemaining).HasColumnName("total_remaining");
            e.Property(x => x.TotalCount).HasColumnName("total_count");
            e.Property(x => x.PackagesJson).HasColumnName("packages");
            e.Property(x => x.FetchedAtMs).HasColumnName("fetched_at");
            e.Property(x => x.PoolId).HasColumnName("pool_id").HasDefaultValue("");
        });

        // ── 积分包明细 ───────────────────────────────────────
        modelBuilder.Entity<CreditPackageEntity>(e =>
        {
            e.ToTable("credit_packages");
            e.HasKey(x => new { x.AccountId, x.PackageCode });
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.PackageCode).HasColumnName("package_code");
            e.Property(x => x.PackageName).HasColumnName("package_name");
            e.Property(x => x.ProductName).HasColumnName("product_name");
            e.Property(x => x.CapacityUnit).HasColumnName("capacity_unit");
            e.Property(x => x.Size).HasColumnName("size");
            e.Property(x => x.Remain).HasColumnName("remain");
            e.Property(x => x.Used).HasColumnName("used");
            e.Property(x => x.CycleStart).HasColumnName("cycle_start");
            e.Property(x => x.CycleEnd).HasColumnName("cycle_end");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.GrantSource).HasColumnName("grant_source");
            e.Property(x => x.UpdatedAtMs).HasColumnName("updated_at");
            e.Property(x => x.PoolId).HasColumnName("pool_id").HasDefaultValue("");
        });

        // ── 积分历史 ─────────────────────────────────────────
        modelBuilder.Entity<CreditHistoryEntity>(e =>
        {
            e.ToTable("credit_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.AccountTag).HasColumnName("account_tag");
            e.Property(x => x.Label).HasColumnName("label");
            e.Property(x => x.Ts).HasColumnName("ts");
            e.Property(x => x.TotalDosage).HasColumnName("total_dosage");
            e.Property(x => x.TotalCount).HasColumnName("total_count");
            e.Property(x => x.Delta).HasColumnName("delta");
            e.Property(x => x.PoolId).HasColumnName("pool_id").HasDefaultValue("");
        });

        // ── 用量 ─────────────────────────────────────────────
        modelBuilder.Entity<UsageEntity>(e =>
        {
            e.ToTable("usage");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.Ts).HasColumnName("ts");
            e.Property(x => x.Iso).HasColumnName("iso");
            e.Property(x => x.Model).HasColumnName("model");
            e.Property(x => x.UsedModel).HasColumnName("used_model");
            e.Property(x => x.Account).HasColumnName("account");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Outcome).HasColumnName("outcome");
            e.Property(x => x.ElapsedMs).HasColumnName("elapsed_ms");
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.PromptTokens).HasColumnName("prompt_tokens");
            e.Property(x => x.CompletionTokens).HasColumnName("completion_tokens");
            e.Property(x => x.TotalTokens).HasColumnName("total_tokens");
            e.Property(x => x.Credit).HasColumnName("credit");
            e.Property(x => x.CacheHit).HasColumnName("cache_hit");
            e.Property(x => x.CacheMiss).HasColumnName("cache_miss");
            e.Property(x => x.CacheRead).HasColumnName("cache_read");
            e.Property(x => x.CacheWrite).HasColumnName("cache_write");
            e.Property(x => x.Reasoning).HasColumnName("reasoning");
            e.Property(x => x.PoolId).HasColumnName("pool_id").HasDefaultValue("");
        });

        // ── 任务 + 事件 ──────────────────────────────────────
        modelBuilder.Entity<TaskEntity>(e =>
        {
            e.ToTable("tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Progress).HasColumnName("progress");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.ResultJson).HasColumnName("result");
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.CreatedAtMs).HasColumnName("created_at_ms");
            e.Property(x => x.StartedAtMs).HasColumnName("started_at_ms");
            e.Property(x => x.FinishedAtMs).HasColumnName("finished_at_ms");

            // ★ 显式配置一对多：Task 的 Events 由 TaskEventEntity.TaskId 关联。
            //   不配的话 EF 按约定推断外键名为 TaskEntityId，而表里只有 task_id，
            //   写库时直接 SQLite Error: table task_events has no column named TaskEntityId。
            e.HasMany(t => t.Events)
                .WithOne()
                .HasForeignKey(ev => ev.TaskId)
                .HasPrincipalKey(t => t.Id);
        });

        modelBuilder.Entity<TaskEventEntity>(e =>
        {
            e.ToTable("task_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.TaskId).HasColumnName("task_id");
            e.Property(x => x.Seq).HasColumnName("seq");
            e.Property(x => x.Ts).HasColumnName("ts");
            e.Property(x => x.Level).HasColumnName("level");
            e.Property(x => x.Message).HasColumnName("message");
            e.Property(x => x.Progress).HasColumnName("progress");
            e.Property(x => x.DataJson).HasColumnName("data");
        });

        // ── 限流 ─────────────────────────────────────────────
        modelBuilder.Entity<RateLimitEntity>(e =>
        {
            e.ToTable("rate_limits");
            e.HasKey(x => new { x.Uid, x.Model });
            e.Property(x => x.Uid).HasColumnName("uid");
            e.Property(x => x.Model).HasColumnName("model");
            e.Property(x => x.Count).HasColumnName("count");
            e.Property(x => x.LastAtMs).HasColumnName("last_at_ms");
            e.Property(x => x.LastResetAtMs).HasColumnName("last_reset_at_ms");
            e.Property(x => x.Recovered).HasColumnName("recovered");
        });

        // ── 元数据 ───────────────────────────────────────────
        modelBuilder.Entity<MetaEntity>(e =>
        {
            e.ToTable("meta");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.Value).HasColumnName("value");
        });

        // ── 账号模型冷却 ────────────────────────────────────
        modelBuilder.Entity<AccountCooldownEntity>(e =>
        {
            e.ToTable("account_cooldowns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.Model).HasColumnName("model");
            e.Property(x => x.UntilMs).HasColumnName("until_ms");
            e.Property(x => x.UpdatedAtMs).HasColumnName("updated_at");
        });
    }
}

/// <summary>DbContext 工厂 —— 免 DI 直接构造（Database.cs 内部用）。</summary>
public static class BuddyDeckDb
{
    /// <summary>
    /// 建一个指向指定 db 文件的上下文。
    ///
    /// ⚠ <c>Pooling=False</c> + <c>Default Timeout=30</c> 是**防死锁**的关键：
    /// SQLite 是单写者，EF 连接池默认会复用连接，多个 DbContext 并发写
    /// 时可能一个连接持着写事务不放、另一个等锁等到永远（busy_timeout=0）。
    /// 关掉池化让每次写用全新连接、用完即关，事务不会跨 DbContext 残留。
    /// </summary>
    public static BuddyDeckDbContext Open(string dbPath)
    {
        var options = new DbContextOptionsBuilder<BuddyDeckDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=False;Default Timeout=30")
            .Options;
        return new BuddyDeckDbContext(options);
    }
}
