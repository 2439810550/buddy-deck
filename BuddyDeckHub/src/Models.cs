namespace BuddyDeckHub;

/// <summary>
/// 渠道 = 一个独立的 workbuddy_api 实例。
///
/// 为什么用「一个实例一个渠道」而不是「一个账号一个渠道」：
/// workbuddy_api 内部已经有多账号管理（保险库 + 自动刷新 + 签到），
/// 让它自己管一池账号，Hub 只跟实例对话，边界最干净，也最不容易出错。
/// 实例之间靠 WORKBUDDY_API_HOME 隔离，彼此完全看不见对方的账号。
/// </summary>
public sealed class Channel
{
    public required string Id { get; init; }

    /// <summary>给人看的名字，比如「账号池 A」。</summary>
    public required string Name { get; set; }

    /// <summary>本机回环地址。workbuddy_api 的 Host 校验只认 loopback，所以只能填这些。</summary>
    public required string BaseUrl { get; set; }

    /// <summary>该实例的 --api-key。</summary>
    public required string ApiKey { get; set; }

    /// <summary>该实例的 WORKBUDDY_API_HOME，用于重启时还原环境。</summary>
    public string DataHome { get; set; } = "";

    /// <summary>网关可执行文件（node）与脚本路径，用于自动拉起。</summary>
    public string NodePath { get; set; } = "";
    public string ScriptDir { get; set; } = "";

    /// <summary>分组：免费组只能走免费模型，全量组走全部。留空 = 不限制。</summary>
    public List<string> Groups { get; set; } = [];

    /// <summary>这个渠道提供哪些模型。留空 = 用上游花名册。</summary>
    public List<string> Models { get; set; } = [];

    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;

    // ── 运行时健康状态（不持久化到配置文件，由 HealthMonitor 维护）──

    public int ConsecutiveFailures;
    public DateTimeOffset? DisabledUntil;
    public DateTimeOffset? LastOk;
    public string? LastError;
    public int InFlight;

    /// <summary>此刻能不能接活。</summary>
    public bool IsAvailable(DateTimeOffset now) =>
        Enabled && (DisabledUntil is null || DisabledUntil <= now);
}

/// <summary>
/// 成员 —— 用这个服务的人。一个成员可以有多把令牌。
/// </summary>
public sealed class Member
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string Note { get; set; } = "";

    /// <summary>成员走哪个分组。决定他能用到哪些渠道、哪些模型。</summary>
    public string Group { get; set; } = "default";

    /// <summary>每天允许消耗的额度（单位：配额，见 QuotaCalculator）。</summary>
    public long DailyQuota { get; set; } = 5_000_000;

    /// <summary>总额度。0 = 不限。</summary>
    public long TotalQuota { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>限速：每分钟最多几个请求。0 = 不限。</summary>
    public int RateLimitPerMinute { get; set; } = 60;

    public List<ApiToken> Tokens { get; set; } = [];
}

/// <summary>
/// 令牌 = 发给对方的 key。
///
/// 只存哈希，明文只在创建那一刻显示一次 —— 和中转站一致。
/// 这样即使数据库泄了，也没法直接拿去用。
/// </summary>
public sealed class ApiToken
{
    public required string Id { get; init; }

    /// <summary>令牌前缀，用于日志里认人（明文前 8 位）。</summary>
    public required string Prefix { get; init; }

    /// <summary>PBKDF2 哈希，格式见 TokenHasher。</summary>
    public required string Hash { get; init; }

    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>独立的可用模型白名单。留空 = 继承成员设置。</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>这把令牌自己的额度。0 = 用成员的额度。</summary>
    public long Quota { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }
    public bool NeverExpires { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>IP 白名单，支持单个 IP 和 CIDR。留空 = 不限。</summary>
    public List<string> AllowedIps { get; set; } = [];
}

/// <summary>分组 —— 决定成员能看到哪些渠道。</summary>
public sealed class Group
{
    public required string Id { get; init; }
    public required string Name { get; set; }

    /// <summary>允许使用的渠道 id。空 = 全部。</summary>
    public List<string> Channels { get; set; } = [];

    /// <summary>允许使用的模型。支持 "*" 通配。</summary>
    public List<string> Models { get; set; } = ["*"];

    /// <summary>这个组的倍率。0 = 完全免费（不扣配额）。</summary>
    public double Rate { get; set; } = 1.0;

    public string Description { get; set; } = "";
}
