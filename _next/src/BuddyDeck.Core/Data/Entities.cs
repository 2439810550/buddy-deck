using Microsoft.EntityFrameworkCore;

namespace BuddyDeck.Core.Data;

// ══════════════════════════════════════════════════════════════════
// 实体类 —— Code First。数据库表由这些实体生成。
//
// 关系设计（用户需求：号池 / 账号 / 账号详情 / 积分明细 互相关联）：
//
//   Pool ──< PoolAccount >── Account ──< Credit
//                                        │
//                                        ├──< CreditPackage   （积分包明细）
//                                        └──< CreditHistory   （积分历史快照）
//
//   Account ──< Usage       （每笔请求扣费）
//   Pool   ──< Usage       （每笔请求属于哪个池）
//
//   这样「某池某账号的积分明细 / 扣费情况」一条联表就出。
// ══════════════════════════════════════════════════════════════════

/// <summary>号池：一套账号 + 独立保险库 + 独立端口。</summary>
[Index(nameof(IsDefault))]
public sealed class PoolEntity
{
    /// <summary>PoolIdOf（默认池 = "default"）。</summary>
    public string Id { get; set; } = "";

    public string DisplayName { get; set; } = "";
    public int Port { get; set; }
    public string ApiKey { get; set; } = "";

    /// <summary>保险库文件绝对路径。</summary>
    public string VaultPath { get; set; } = "";

    /// <summary>池目录名（唯一标识，默认池为空串）。</summary>
    public string DirectoryName { get; set; } = "";

    public bool IsDefault { get; set; }
    public bool Running { get; set; }
    public string? LastError { get; set; }
    public long UpdatedAtMs { get; set; }

    /// <summary>这个池里的账号（多对多，经 PoolAccount）。</summary>
    public List<AccountEntity> Accounts { get; } = [];
}

/// <summary>账号：保险库里的一条登录身份。</summary>
public sealed class AccountEntity
{
    /// <summary>完整 id（uuid:enterpriseId）。</summary>
    public string Id { get; set; } = "";

    /// <summary>纯 uid（uuid）。</summary>
    public string Uid { get; set; } = "";
    public string? EnterpriseId { get; set; }
    public string Label { get; set; } = "";
    public string Domain { get; set; } = "";
    public long ExpiresAtMs { get; set; }
    public long? RefreshExpiresAtMs { get; set; }
    public bool Active { get; set; }
    public bool Frozen { get; set; }
    public int FailureCount { get; set; }
    public long UpdatedAtMs { get; set; }

    /// <summary>这个号属于哪些池（多对多，经 PoolAccount）。</summary>
    public List<PoolEntity> Pools { get; } = [];
}

/// <summary>积分快照：一个账号的最新余额。</summary>
[Index(nameof(AccountTag))]
public sealed class CreditEntity
{
    /// <summary>完整 account id。</summary>
    public string AccountId { get; set; } = "";

    /// <summary>uid 前 8 位 + ':'。</summary>
    public string AccountTag { get; set; } = "";
    public string Label { get; set; } = "";
    public double TotalDosage { get; set; }
    public double TotalRemaining { get; set; }
    public int TotalCount { get; set; }

    /// <summary>积分包 JSON（冗余，与 CreditPackage 表并存，快速读）。</summary>
    public string PackagesJson { get; set; } = "[]";
    public long FetchedAtMs { get; set; }

    /// <summary>所属池（空 = 未归属）。</summary>
    public string PoolId { get; set; } = "";
}

/// <summary>积分包明细：一个账号多个包，逐个存。</summary>
public sealed class CreditPackageEntity
{
    public string AccountId { get; set; } = "";
    public string PackageCode { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string CapacityUnit { get; set; } = "";
    public double Size { get; set; }
    public double Remain { get; set; }
    public double Used { get; set; }
    public string CycleStart { get; set; } = "";
    public string CycleEnd { get; set; } = "";
    public int Status { get; set; }
    public string GrantSource { get; set; } = "";
    public long UpdatedAtMs { get; set; }
    public string PoolId { get; set; } = "";
}

/// <summary>积分历史快照：每次拉取写一条（不覆盖）。</summary>
[Index(nameof(AccountId), nameof(Ts))]
public sealed class CreditHistoryEntity
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public string AccountTag { get; set; } = "";
    public string Label { get; set; } = "";
    public long Ts { get; set; }
    public double TotalDosage { get; set; }
    public int TotalCount { get; set; }

    /// <summary>相对上一次的变化（负 = 消耗）。</summary>
    public double? Delta { get; set; }
    public string PoolId { get; set; } = "";
}

/// <summary>用量账本：每笔请求的实际扣费。</summary>
[Index(nameof(Ts))]
[Index(nameof(Account))]
public sealed class UsageEntity
{
    public long Id { get; set; }
    public long Ts { get; set; }
    public string Iso { get; set; } = "";
    public string Model { get; set; } = "";
    public string? UsedModel { get; set; }
    public string? Account { get; set; }
    public string Kind { get; set; } = "chat";
    public string Outcome { get; set; } = "ok";
    public long? ElapsedMs { get; set; }
    public int? Attempts { get; set; }
    public long? PromptTokens { get; set; }
    public long? CompletionTokens { get; set; }
    public long? TotalTokens { get; set; }
    public double? Credit { get; set; }
    public long? CacheHit { get; set; }
    public long? CacheMiss { get; set; }
    public long? CacheRead { get; set; }
    public long? CacheWrite { get; set; }
    public long? Reasoning { get; set; }
    public string PoolId { get; set; } = "";
}

/// <summary>后台任务。</summary>
[Index(nameof(CreatedAtMs))]
[Index(nameof(Status))]
public sealed class TaskEntity
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int Progress { get; set; }
    public string Description { get; set; } = "";
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public long CreatedAtMs { get; set; }
    public long? StartedAtMs { get; set; }
    public long? FinishedAtMs { get; set; }

    public List<TaskEventEntity> Events { get; } = [];
}

/// <summary>任务流事件：每一步。</summary>
[Index(nameof(TaskId), nameof(Seq))]
public sealed class TaskEventEntity
{
    public long Id { get; set; }
    public string TaskId { get; set; } = "";
    public int Seq { get; set; }
    public long Ts { get; set; }
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
    public int? Progress { get; set; }
    public string? DataJson { get; set; }
}

/// <summary>限流历史（按 号+模型 二元组）。</summary>
public sealed class RateLimitEntity
{
    public string Uid { get; set; } = "";
    public string Model { get; set; } = "";
    public int Count { get; set; }
    public long LastAtMs { get; set; }
    public long LastResetAtMs { get; set; }
    public bool Recovered { get; set; }
}

/// <summary>
/// 账号模型冷却（用户需求：每个号的模型冷却情况要入库、更新、显示）。
/// 一个号可能有多个模型同时冷却，每个模型一条记录。
/// </summary>
[Index(nameof(AccountId))]
[Index(nameof(UntilMs))]
public sealed class AccountCooldownEntity
{
    public long Id { get; set; }

    /// <summary>账号完整 id（uuid:enterpriseId）。</summary>
    public string AccountId { get; set; } = "";

    /// <summary>模型 id。</summary>
    public string Model { get; set; } = "";

    /// <summary>解锁时刻（Unix 毫秒）。</summary>
    public long UntilMs { get; set; }

    /// <summary>最后更新时间。</summary>
    public long UpdatedAtMs { get; set; }
}

/// <summary>键值元数据（设置等）。</summary>
public sealed class MetaEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
