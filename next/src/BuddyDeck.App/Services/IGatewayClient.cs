using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuddyDeck.App.Services;

/// <summary>
/// 网关状态的一帧快照。
///
/// 这是界面唯一认识的「网关长什么样」——视图不直接碰 HTTP，也不解析 JSON。
/// </summary>
public sealed record GatewayHealth
{
    /// <summary>网关在不在（能连上并且 /healthz 回了 ok）。</summary>
    public required bool Online { get; init; }

    /// <summary>探测的端口（不管在不在线都填，界面上要显示「我找的是哪个端口」）。</summary>
    public required int Port { get; init; }

    /// <summary>网关报的产物名，如 workbuddy。</summary>
    public string Provider { get; init; } = "";

    /// <summary>模型个数。</summary>
    public int Models { get; init; }

    /// <summary>
    /// 端口上那个进程加载的代码指纹。
    ///
    /// 用户遇到「改了代码但功能没变」时靠它判断端口上跑的是不是旧代码。
    /// </summary>
    public string BuildId { get; init; } = "";

    /// <summary>网关进程启动时刻。</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>磁盘上的代码和正在跑的是否一致。</summary>
    public bool Stale { get; init; }

    /// <summary>不在线时的原因，用一句人话说明（不要堆异常类型名）。</summary>
    public string? Error { get; init; }

    /// <summary>离线快照的工厂 —— 界面上「未连接」是常态之一，不是异常。</summary>
    public static GatewayHealth Offline(int port, string reason) => new()
    {
        Online = false,
        Port = port,
        Error = reason,
    };
}

/// <summary>
/// 仪表盘上「最近请求」那一块的数据。
///
/// 有两套口径，别混：
///   · **用户视角**（<see cref="TotalRequests"/> / 成功率 / 完成率 / 平均耗时）
///     —— 他发了几次请求、拿到好结果的概率有多大
///   · **池子视角**（<see cref="AttemptCount"/>）
///     —— 上游实际被打了几次
/// 恒有 AttemptCount ≥ TotalRequests，**差值就是「因换号重试多打的上游请求数」**。
/// </summary>
public sealed record GatewayMetrics
{
    /// <summary>客户端请求数。**一次请求算一个** —— 内部换号重试 3 次仍然是 1。</summary>
    public long TotalRequests { get; init; }

    /// <summary>
    /// 累计上游尝试次数（诊断口径）。
    ///
    /// 与 <see cref="TotalRequests"/> 的比值是**账号池的体检指标**：
    /// 一个请求试了 3 次才成功 = 池子在恶化。
    ///
    /// 为什么这个字段必须单独看：**它提供的信号在成功率上完全看不见** ——
    /// 重试成功会让成功率保持 100%，池子其实已经不行了，但指标是漂亮的。
    /// </summary>
    public long AttemptCount { get; init; }

    /// <summary>HTTP 层成功（状态码 2xx）的比例，0–1。</summary>
    public double HttpSuccessRate { get; init; }

    /// <summary>完成成功率（真的把回复吐完了的比例），0–1。</summary>
    public double CompletionRate { get; init; }

    /// <summary>
    /// 平均耗时（毫秒）。**含全部换号重试** —— 是用户真正等的时间，不是单次上游往返。
    /// </summary>
    public double AverageLatencyMs { get; init; }

    /// <summary>
    /// 最近若干次请求的简要记录（脱敏，只有元数据）。
    ///
    /// ⚠ **里面既有终局记录、也有中间失败的尝试**（网关有意都保留，
    /// 否则用户看不见重试过程）。要只看真结果就按 <see cref="RecentRequest.IsTerminal"/> 过滤。
    /// </summary>
    public IReadOnlyList<RecentRequest> Recent { get; init; } = [];

    /// <summary>拿不到指标时为 true —— 界面用它决定显示「暂无数据」。</summary>
    public bool IsEmpty => TotalRequests == 0 && Recent.Count == 0;

    /// <summary>
    /// 因换号重试而多打的上游请求数。
    ///
    /// 0 = 每个请求都一次打中；数字越大池子越不顺。
    /// </summary>
    public long RetryOverhead => Math.Max(0, AttemptCount - TotalRequests);

    public static readonly GatewayMetrics Empty = new();
}

/// <summary>一次请求（或一次尝试）的元数据。**不含**提示词、响应正文、凭证。</summary>
public sealed record RecentRequest
{
    public DateTimeOffset At { get; init; }
    public string Model { get; init; } = "";
    public int Status { get; init; }
    public long DurationMs { get; init; }
    /// <summary>用的是哪个号（脱敏后的短标签，不是 uid）。</summary>
    public string AccountTag { get; init; } = "";
    /// <summary>
    /// 这次是不是流式。
    ///
    /// ⚠ 显式映射（网关键 `streamed`，见 `Metrics.cs` 的 `ToJson`）。
    /// 属性名 `Streamed` 与键名只因首字母大小写而"看起来一致" ——
    /// 那是 `PropertyNameCaseInsensitive` 的巧合，不是契约。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("streamed")]
    public bool Streamed { get; init; }

    /// <summary>
    /// 这条记录内部试了几次上游（含本次）。
    ///
    /// 终局记录上 `&gt; 1` 就表示**这条是换号重试捞回来的** —— 一次没成、
    /// 换了号才成功。界面上值得标出来：用户会以为一切正常，其实池子已经不稳了。
    /// </summary>
    public int Attempts { get; init; } = 1;

    /// <summary>
    /// 是不是终局记录。
    ///
    /// false = 这次**尝试失败**了，后面还会重试（中间态）。
    /// 网关把它也放进 Recent 是为了让重试过程可见，但**它不是一次「请求」**——
    /// 统计请求数、成功率都不该算它。
    ///
    /// ⚠ **必须显式映射**：网关卡发的键是 `terminal`（没有 `is` 前缀），
    /// System.Text.Json 的 `PropertyNameCaseInsensitive` **不会**把
    /// `terminal` 匹配到 `IsTerminal` 上 —— 它只比对完整名字（大小写无关），
    /// 不做「Is 前缀」推理。少了这一行，这个字段会**静默永远为 true**，
    /// 表现是「所有失败的中间尝试都被当成终局结果」。
    ///
    /// 这个是实测出来的，不是推理：见 docs/concurrency-verification-howto.md
    /// 同类「静默绑定失败」的教训 —— 编译期与运行期都不报错。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("terminal")]
    public bool IsTerminal { get; init; } = true;

    /// <summary>这次有没有真的发到上游。false = 还没出门就废了（如本地就判了不可用）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sentToUpstream")]
    public bool SentToUpstream { get; init; } = true;

    /// <summary>这次是一条「重试捞回来的」终局记录。</summary>
    public bool RecoveredByRetry => IsTerminal && Attempts > 1;
}

/// <summary>保险库里的一个号在界面上的样子。token 字段不在这个模型里。</summary>
public sealed record AccountSummary
{
    public required string Id { get; init; }
    public string Label { get; init; } = "";
    public string Uid { get; init; } = "";
    public string? EnterpriseId { get; init; }
    public string Domain { get; init; } = "";
    public long ExpiresAtMs { get; init; }
    public long? RefreshExpiresAtMs { get; init; }
    /// <summary>
    /// 当前网关正在用的那个。
    ///
    /// ⚠ 显式映射（网关键 `active`，见 `AdminEndpoints.cs` 的账号序列化）。
    /// 这个尤其要标：**默认值是 `false`**，一旦因为改名失配，
    /// 界面会把"当前在用的号"标成"没在用"，而 `false` 是合法取值 —— 静默。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("active")]
    public bool Active { get; init; }

    /// <summary>
    /// 这个号**当前有哪些模型在冷却**（网关键 `cooldowns`）。
    ///
    /// ## 为什么要有这个（用户实测反馈）
    ///
    /// 「每一个账号的模型冷却情况也没有」—— 界面原先只能看到号在不在用，
    /// 看不到「这个号的 hy3 被限流了、再过 12 分钟才好」。
    /// 而限流是**按「号+模型」二元组**记的，同一个号可能 hy3 冷却、hy4 正常。
    /// 用户看不到这层时，只会感觉"这个号时好时坏"。
    ///
    /// ⚠ 显式映射（网关键 `cooldowns`）—— 与 `Active` 同一个理由：
    ///   少标一个 `JsonPropertyName` 就是**静默拿到空数组**，
    ///   而"没有冷却"与"没读到"在界面上长得一样。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("cooldowns")]
    public IReadOnlyList<ModelCooldown> Cooldowns { get; init; } = [];

    /// <summary>连续失败次数（网关键 `failureCount`）—— 界面上解释"为什么它被冷冻了"。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("failureCount")]
    public int FailureCount { get; init; }

    /// <summary>是否被冷冻（网关键 `frozen`）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("frozen")]
    public bool Frozen { get; init; }
}

/// <summary>一个模型在这个号上的冷却情况。</summary>
public sealed record ModelCooldown
{
    [System.Text.Json.Serialization.JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("untilMs")]
    public long UntilMs { get; init; }

    /// <summary>
    /// 还剩多少秒 —— **网关已经算好了**，界面不要自己换算。
    ///
    /// 为什么要网关算：时区/单位换算是界面最容易出错的一环，
    /// 而且客户端时钟与网关可能不一致（跨机器部署时）。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("remainSeconds")]
    public long RemainSeconds { get; init; }
}

/// <summary>
/// 一个积分包（界面上的一行）。
///
/// 形状对齐网关 /admin/credits 的输出；字段名与 Core 的 `CreditPackage` 一致
/// （那边解析上游，这边只搬运到界面）。
/// </summary>
public sealed record CreditPackageInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("packageName")]
    public string PackageName { get; init; } = "";

    /// <summary>还能用多少。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("remain")]
    public double Remain { get; init; }

    /// <summary>这一批一共有多少（画进度条用）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("size")]
    public double Size { get; init; }

    /// <summary>这一批的止期。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("cycleEndTime")]
    public string CycleEndTime { get; init; } = "";

    /// <summary>
    /// 积分来源。`daily_checkin` = 签到发的。
    ///
    /// 为什么界面要用它：能区分「签到挣的」和「活动送的」——
    /// 用户对这两种的心态完全不同（前者可以慢慢用，后者常是限时的）。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("grantSource")]
    public string GrantSource { get; init; } = "";
}

/// <summary>
/// 某个账号的积分快照。
///
/// ## ⚠ 字段名必须与网关 `/admin/credits` 的**实际输出**逐字对齐
///
/// 实测踩过（用户反馈「账号看不到还有多少积分」）：
/// 这里原来写的是 `id` / `totalRemaining` / `checkinRemaining`，
/// 而网关实际回的是 `accountTag` / `accountId` / `totalDosage` ——
/// **名字对不上 ⇒ 反序列化后全是默认值 ⇒ 界面显示 0 积分**，
/// 而且**一条异常都没有**（System.Text.Json 对不上的字段默默忽略）。
///
/// ⇒ 这一族的共同指纹：**不是"值错了"，而是"值压根没被写过"**。
///   以后改网关的序列化键名，必须同步改这里。
/// </summary>
public sealed record AccountCredits
{
    /// <summary>
    /// 账号标识（网关键 `accountTag`，形如 `uuid:`）——
    /// 界面用它把积分挂到对应的卡上。
    ///
    /// ⚠ 用 `accountTag` 而不是 `accountId`：前者与 `/metrics` 的 recent
    ///   里那个 `accountTag` **是同一个值**，两边才对得上。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("accountTag")]
    public string Id { get; init; } = "";

    /// <summary>账号标签（网关键 `label`）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("label")]
    public string Label { get; init; } = "";

    /// <summary>
    /// 还能用的总量（网关键 `totalDosage`）。
    /// <summary>
    /// **能用余额** —— 各包 remain 累加。
    ///
    /// ⚠ 映射的是网关键 `totalRemaining`，**不是 `totalDosage`**：
    ///   · totalDosage    上游的“累计发过多少”（含已用/已过期）
    ///   · totalRemaining 各包 remain 累加 = “现在还能用多少” ← 界面要的就是它
    /// 拿 totalDosage 当余额会显示成“还剩一大堆”（已用的不算）——
    /// 这正是用户反馈“积分全都对不上”的根因。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalRemaining")]
    public double TotalRemaining { get; init; }

    /// <summary>上游累计发放量（网关键 `totalDosage`）——只是参考，不当余额。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalDosage")]
    public double TotalDosage { get; init; }

    /// <summary>积分包个数（网关键 `totalCount`）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    /// <summary>积分包（上游原序；界面自己按到期排）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("packages")]
    public IReadOnlyList<CreditPackageInfo> Packages { get; init; } = [];

    /// <summary>查失败时的原因（单个账号可能查不到，不影响别的账号）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// 用量账本里的一条明细（`/usage/recent` 返回的项）。
///
/// 字段与网关 UsageEndpoints 的 <c>Entry()</c> 逐字对齐（camelCase）。
/// 实现 <see cref="StatsPresentation.ILedgerRow"/> —— 这样
/// <see cref="StatsPresentation.Aggregate"/> 能直接吃它（依赖倒置：
/// StatsPresentation 不引用 App/Core，只认窄接口）。
/// </summary>
public sealed record UsageRecord : StatsPresentation.ILedgerRow
{
    /// <summary>账目发生时刻（Unix 毫秒）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("ts")]
    public long Ts { get; init; }

    /// <summary>ISO 时间字符串（展示用）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("iso")]
    public string Iso { get; init; } = "";

    /// <summary>客户端要的模型名。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("model")]
    public string Model { get; init; } = "";

    /// <summary>实际用的模型（被降级/换号时与 Model 不同）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("usedModel")]
    public string? UsedModel { get; init; }

    /// <summary>账号标签（脱敏短标签）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("account")]
    public string Account { get; init; } = "";

    /// <summary>ok / failed / cancelled。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("outcome")]
    public string Outcome { get; init; } = "ok";

    /// <summary>输入 token。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("promptTokens")]
    public long? PromptTokens { get; init; }

    /// <summary>输出 token。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("completionTokens")]
    public long? CompletionTokens { get; init; }

    /// <summary>总 token。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalTokens")]
    public long? TotalTokens { get; init; }

    /// <summary>上游实际扣费（积分）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("credit")]
    public double? Credit { get; init; }
}

/// <summary>一个账号的签到结果（`/admin/checkin` 返回的项）。</summary>
public sealed record CheckinResultDto
{
    [System.Text.Json.Serialization.JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("label")]
    public string Label { get; init; } = "";

    /// <summary>这次是不是真的新签了（false = 今天已签过）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("claimed")]
    public bool Claimed { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string Message { get; init; } = "";
}

/// <summary>任务库里的一个任务（`/admin/tasks` 返回的项）。</summary>
public sealed record TaskInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string Title { get; init; } = "";

    /// <summary>pending / running / done / failed / cancelled。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; init; } = "pending";

    /// <summary>0-100。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("progress")]
    public int Progress { get; init; }

    /// <summary>实时进度描述（如「正在拉取积分：3/20 主号」）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("error")]
    public string? Error { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("createdAtMs")]
    public long CreatedAtMs { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("finishedAtMs")]
    public long? FinishedAtMs { get; init; }
}

/// <summary>任务详情（`/admin/tasks/{id}` 返回）。</summary>
public sealed record TaskDetail
{
    [System.Text.Json.Serialization.JsonPropertyName("task")]
    public TaskInfo? Task { get; init; }

    /// <summary>任务流事件（按时间升序）——“它做了什么”。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("events")]
    public IReadOnlyList<TaskEventInfo> Events { get; init; } = [];
}

/// <summary>任务流里的一条事件。</summary>
public sealed record TaskEventInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("seq")]
    public int Seq { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("ts")]
    public long Ts { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("level")]
    public string Level { get; init; } = "info";

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("progress")]
    public int? Progress { get; init; }
}

/// <summary>
/// 池健康矩阵（`GET /admin/pool-health`）—— 按**模型**看的可用性快照。
///
/// 字段名与网关逐字对齐（同 <see cref="AccountCredits"/> 那条注释的教训：
/// 对不上就是静默显示默认值）。
/// </summary>
public sealed record PoolHealthSnapshot
{
    /// <summary>池里总共有几个号。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("accountCount")]
    public int AccountCount { get; init; }

    /// <summary>
    /// 所有模型都健康吗（**只要有任何一个模型有号在冷却就是 false**）。
    ///
    /// ⚠ 不要用它当"一切正常"的唯一判据 —— 它只看**此刻**的冷却。
    ///   判断"某个模型是不是基本废了"要看
    ///   <see cref="ModelHealth.RateLimitedAccounts"/>。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("allHealthy")]
    public bool AllHealthy { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("models")]
    public IReadOnlyList<ModelHealth> Models { get; init; } = [];
}

/// <summary>一个模型在全池范围内的健康情况。</summary>
public sealed record ModelHealth
{
    [System.Text.Json.Serialization.JsonPropertyName("model")]
    public string Model { get; init; } = "";

    /// <summary>此刻还能用这个模型的号数。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("usableCount")]
    public int UsableCount { get; init; }

    /// <summary>池里总共几个号。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("total")]
    public int Total { get; init; }

    /// <summary>此刻正在冷却的号名（用户可以去查那些号）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("cooling")]
    public IReadOnlyList<string> Cooling { get; init; } = [];

    /// <summary>
    /// **曾经**被限流过的号数 —— 「哪些模型被限流了」问的就是它。
    ///
    /// ⚠ 它与 <see cref="Cooling"/> 是两回事：降级救回来之后冷却会被清掉，
    ///   而这个计数**保留**。只看前者会得到"全绿"的假象。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("rateLimitedAccounts")]
    public int RateLimitedAccounts { get; init; }

    /// <summary>其中已经恢复的号数。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("recoveredAccounts")]
    public int RecoveredAccounts { get; init; }

    /// <summary>
    /// 最晚的解除时刻（"全都好了"的时间）—— 「啥时候解除限流」问的就是它。
    ///
    /// 取的是**最晚**那个：用户问的是"什么时候能全都用上"。
    /// 0 表示没有限流记录。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("lastResetAtMs")]
    public long LastResetAtMs { get; init; }

    /// <summary>此刻还有号在冷却吗。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("coolingNow")]
    public bool CoolingNow { get; init; }

    /// <summary>最近 200 次请求里，这个模型被降级了多少次（"平时老换模型"的证据）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("fallbackCount")]
    public int FallbackCount { get; init; }
}

/// <summary>一套账号 + 一个接入地址。</summary>
public sealed record PoolSummary
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public required string Id { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("port")]
    public int Port { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("apiKey")]
    public string ApiKey { get; init; } = "";
    /// <summary>
    /// 这个池的网关在不在跑。
    ///
    /// ⚠ 显式映射（网关键 `running`，见 `AdminEndpoints.cs:529`）。
    /// 默认 `false`，失配方向隐蔽（「在跑」被显示成「没在跑」）。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("running")]
    public bool Running { get; init; }
    public int AccountCount { get; init; }
    /// <summary>
    /// 是不是默认池。
    ///
    /// ⚠ **必须显式映射** —— 网关卡发的键是 `isDefault`（小写开头），
    /// 而属性叫 `IsDefault`。`PropertyNameCaseInsensitive` 只做
    /// 「大小写无关的**完整名字**比较」，**不做 `Is` 前缀推理** ——
    /// 现在能绑上纯粹是因为首字母大小写差异恰好被忽略，**是巧合，不是契约**。
    ///
    /// 真实案例（同文件、同形状，已发生过）：`IsTerminal` ← JSON 键 `terminal`
    /// （**没有** is 前缀）→ 绑不上 → 留在默认值 `true`
    /// → 「所有失败的中间尝试都被当成终局结果」。
    ///
    /// 这里的默认值是 `false`，所以失配方向**相反但同样隐蔽**：
    /// 静默变成 `false`，而 `false` 恰好也是「不是默认池」的合法取值 ——
    /// 界面会把默认池显示成非默认池，且不报任何错。
    ///
    /// 回归测试见 tests/BuddyDeck.App.Presentation.Tests/BooleanJsonBindingTests.cs
    /// （两个方向都断言 —— 默认 false 的字段难点在 `true` 方向）。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("isDefault")]
    public bool IsDefault { get; init; }

    /// <summary>保险库路径（面板拉起这个池的网关子进程用）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("vaultPath")]
    public string VaultPath { get; init; } = "";
    public string? LastError { get; init; }
}

/// <summary>网关暴露的模型目录里的一项。</summary>
public sealed record ModelInfo
{
    public required string Id { get; init; }
    public string DisplayName { get; init; } = "";
}

/// <summary>网关的持久设置。</summary>
public sealed record GatewaySettings
{
    /// <summary>
    /// ⚠ 全部字段必须显式标 camelCase 键名 ——
    /// POST 出去时网关按 camelCase 读（见 AdminEndpoints.HandleSettingsAsync），
    /// 不标的话序列化出来是 PascalCase（Port/ApiKey/...），网关读不到，
    /// 表现为「保存按钮点了、提示已保存、实际什么都没改」。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("port")]
    public int Port { get; init; } = 8791;
    [System.Text.Json.Serialization.JsonPropertyName("apiKey")]
    public string ApiKey { get; init; } = "";
    /// <summary>国内版 / 国际版。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("variantId")]
    public string VariantId { get; init; } = "workbuddy";
    [System.Text.Json.Serialization.JsonPropertyName("vaultPath")]
    public string VaultPath { get; init; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("logPath")]
    public string LogPath { get; init; } = "";
}

/// <summary>日志里的一行。**只有元数据**，正文和凭证不进日志。</summary>
public sealed record LogLine
{
    public DateTimeOffset At { get; init; }
    /// <summary>Trace / Debug / Information / Warning / Error / Fatal。</summary>
    public string Level { get; init; } = "Information";
    public string Message { get; init; } = "";
}

/// <summary>
/// 网关调用的一次结果。
///
/// 为什么不直接抛异常：网关没起是这个界面的**正常状态**之一，
/// 用异常表达正常状态会逼着每个调用点写 try/catch —— 那正是「视图里有业务逻辑」。
/// 所以所有网关调用都返回这个：要么有值，要么带一句给人看的原因。
/// </summary>
public sealed record GatewayResult<T>
{
    private GatewayResult(bool ok, T? value, string? error)
    {
        Ok = ok;
        Value = value;
        Error = error;
    }

    public bool Ok { get; }
    public T? Value { get; }
    public string? Error { get; }

    public static GatewayResult<T> Success(T value) => new(true, value, null);
    public static GatewayResult<T> Failure(string error) => new(false, default, error);

    /// <summary>拿值或者兜底 —— 视图里写 `result.Or(fallback)`，不写 if。</summary>
    public T Or(T fallback) => Ok && Value is not null ? Value : fallback;
}

/// <summary>
/// 一段取回来的媒体字节 + 它的 MIME。
///
/// 为什么把 MIME 一起带回来：界面要决定用「图片」还是「视频」渲染，
/// 而那个判断**不该靠 URL 后缀猜**（后缀可以是假的，也可以没有）。
///
/// ⚠ 字节数组：调用方负责在合适的时机释放对它的引用。
/// 我们不做流式解码（图/视频都不大，且有字节上限兜底）。
/// </summary>
public sealed record MediaBytes(byte[] Data, string ContentType)
{
    /// <summary>按 MIME 判是不是视频 —— 界面据此选播放器还是图片框。</summary>
    public bool IsVideo => ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    /// <summary>按 MIME 判是不是图片。</summary>
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 媒体 URL 的白名单守卫。
///
/// ## 为什么需要它
///
/// 聊天里出现的媒体 URL **来自网关，而网关的 URL 来自上游** ——
/// 也就是**网络数据**。虽然当前实现下它总是
/// `http://127.0.0.1:端口/v1/images/file/xxx.png`（网关自己生成的），
/// 但**契约上它是外部输入**：`deliver: "url"` 那条分支会把上游的原始 URL
/// 直接透出来（见 `MediaEndpoints.cs:29, 268`）。
///
/// 拿它去发请求、去喂 `Image.Source`，等于让远端决定我们连哪里。
/// **「当前值无害」不是安全论证** —— 与 t52 的 `action_button.Action` 同一条道理。
///
/// ## 本期的策略：只允许网关自身 host
///
/// 我们**故意不做「允许 https 任何 host」**那种宽松白名单：
///   · 这个界面的媒体只有一个正当来源 —— 本机网关
///   · 放开到任意 https 就等于把界面变成能访问外网的客户端，
///     会泄露「用户开了这个面板」这件事（DNS 查询本身也是泄露）
///   · 真需要直连上游 URL 时，让网关自己下载落盘（它已经有这条路径），
///     界面永远只跟网关打交道
/// </summary>
public static class MediaUrlGuard
{
    /// <summary>
    /// 单次媒体下载的字节上限。**16 MB。**
    ///
    /// 为什么是 16 MB：一张 4K PNG 大约 5–10 MB，一段几秒的短视频也就这个量级。
    /// 16 MB 足够覆盖正当用途，同时保证「最坏情况下界面内存多占 16 MB」——
    /// 而不是被一个 500 MB 的响应打爆。
    /// </summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    /// <summary>
    /// 媒体下载的超时。**20 秒。**
    ///
    /// 比普通 API 调用（3 秒）宽松得多 —— 视频文件本来就大。
    /// 但不能不设：没有超时的话，一个挂着不返回的连接会让那一块永远停在加载态。
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 这个 URL 能不能去下载。
    ///
    /// 判定：**必须是 http/https**，且 **host 必须是回环地址**。
    /// 回环判断直接复用网关侧 `LoopbackHosts` 的同一套集合
    /// （`127.0.0.1` / `localhost` / `[::1]` / `::1`），保持两端一致 ——
    /// 那边防 DNS rebinding，这边防「界面去连别处」。
    /// </summary>
    public static bool IsAllowedMediaUrl(string? url, int expectedPort)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (!uri.IsAbsoluteUri) return false;

        // 只允许 http/https —— 挡掉 file: / ms-settings: / javascript: 之类
        var schemeOk = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!schemeOk) return false;

        // host 必须是回环
        if (!IsLoopbackHost(uri.Host)) return false;

        // 端口必须是当前网关的端口。
        //
        // 为什么连端口也卡：本机上别的服务也监听 127.0.0.1。
        // 只判「回环」的话，`http://127.0.0.1:8080/随便什么` 也能过 ——
        // 那等于允许上游让我们去戳本机任意服务。
        return uri.Port == expectedPort;
    }

    private static bool IsLoopbackHost(string host)
        => host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);

    /// <summary>给日志用的拒绝原因（不回显完整 URL —— 它可能带查询串）。</summary>
    public static string DescribeRejection(string? url, int expectedPort)
    {
        if (string.IsNullOrWhiteSpace(url)) return "媒体地址是空的。";

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
            return "媒体地址不是合法的绝对 URL，已忽略。";

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return $"媒体地址用了不被允许的 scheme「{uri.Scheme}:」，已忽略。";
        }

        if (!IsLoopbackHost(uri.Host))
            return $"媒体地址指向了非本机的 host「{uri.Host}」，已忽略。";

        if (uri.Port != expectedPort)
            return $"媒体地址指向了本机的另一个端口 {uri.Port}（当前网关是 {expectedPort}），已忽略。";

        return "媒体地址未通过校验，已忽略。";
    }
}

/// <summary>
/// 界面到网关的全部交互。
///
/// 为什么要有这个抽象：界面层不允许知道 httpx 细节，也不允许在网关没起时抛异常。
/// 真实实现是 <see cref="HttpGatewayClient"/>，未连接实现是 <see cref="NotConnectedGatewayClient"/>，
/// 测试里可以塞任意假实现。
/// </summary>
public interface IGatewayClient
{
    /// <summary>当前以为的网关端口（设置里改，或从配置读）。</summary>
    int Port { get; set; }

    /// <summary>
    /// 客户端要向网关出示的 Bearer key。
    ///
    /// ## 为什么接口层要有它（不只是实现属性）
    ///
    /// 设置页保存了网关的新 key 之后，**客户端自己手里的 key 必须跟着换**
    /// —— 否则下一次请求还是用旧 key（或空 key）→ 401「网关要求 API key」。
    /// 接口暴露它，SettingsViewModel 才能保存后同步（见 SaveAsync）。
    /// </summary>
    string ApiKey { get; set; }

    /// <summary>探活 + 取 /healthz 的那一帧。**永不抛**：连不上就返回离线快照。</summary>
    Task<GatewayHealth> GetHealthAsync(CancellationToken ct = default);

    /// <summary>取请求指标。网关没起或端点还没实现时返回空指标。</summary>
    Task<GatewayResult<GatewayMetrics>> GetMetricsAsync(CancellationToken ct = default);

    /// <summary>账号列表（从网关读保险库，界面自己不解析 token 文件）。</summary>
    Task<GatewayResult<IReadOnlyList<AccountSummary>>> GetAccountsAsync(CancellationToken ct = default);

    /// <summary>把网关的当前账号切成某一条。</summary>
    Task<GatewayResult<bool>> ActivateAccountAsync(string id, CancellationToken ct = default);

    /// <summary>重命名一个号（只改标签，不动 token）。</summary>
    Task<GatewayResult<bool>> RenameAccountAsync(string id, string label, CancellationToken ct = default);

    /// <summary>删掉一个号。</summary>
    Task<GatewayResult<bool>> RemoveAccountAsync(string id, CancellationToken ct = default);

    /// <summary>手动续期某个号的 token。</summary>
    Task<GatewayResult<bool>> RefreshTokenAsync(string id, CancellationToken ct = default);

    /// <summary>导入一段桌面 App 的登录文件内容。</summary>
    Task<GatewayResult<int>> ImportAccountsAsync(string payload, CancellationToken ct = default);

    /// <summary>
    /// 导出全部账号到加密文件（**需要口令**）。
    ///
    /// @param path       写到哪个文件（绝对路径）
    /// @param passphrase 加密口令 —— 界面**不保存**它，用完即弃
    /// @returns 导出的账号条数
    ///
    /// ⚠ 为什么是让网关写盘、而不是界面收下内容自己存：
    ///   导出文件含全部凭据，走 HTTP 回给客户端等于多一次暴露面。
    ///   而且加密与原子写在 Core 里已经实现过（`AccountTransfer`），
    ///   界面重做一遍只会多一处能写错的地方。
    /// </summary>
    Task<GatewayResult<int>> ExportAccountsAsync(string path, string passphrase, CancellationToken ct = default);

    /// <summary>
    /// 池健康矩阵（`GET /admin/pool-health`）—— **按模型**看可用性。
    ///
    /// ## 为什么单独一个接口（用户实测反馈）
    ///
    /// 用户原话：「20 个号的 deepseek-v4.1-flash 基本都限流了，
    /// **但是显示全绿**。应该告诉用户这些模型限流了、**啥时候解除限流**」。
    ///
    /// 账号列表回答的是**账号维度**（"这个号还好不好"），
    /// 而这个回答的是**模型维度**（"deepseek 是不是废了、什么时候能再用"）。
    /// **两个问题不同**：同一个号上某个模型废掉、别的模型照样能用，是常态。
    ///
    /// ⚠ 判据是**历史**（成功不清），不是瞬时冷却 —— 后者在降级救回来之后
    ///   什么都不剩，那正是"显示全绿"的成因。
    /// </summary>
    Task<GatewayResult<PoolHealthSnapshot>> GetPoolHealthAsync(CancellationToken ct = default);

    /// <summary>池列表。</summary>
    Task<GatewayResult<IReadOnlyList<PoolSummary>>> GetPoolsAsync(CancellationToken ct = default);

    /// <summary>读某个池里的账号列表（层级导航：点池看账号）。</summary>
    Task<GatewayResult<IReadOnlyList<AccountSummary>>> GetPoolAccountsAsync(string poolId, CancellationToken ct = default);

    /// <summary>启停一个池。</summary>
    Task<GatewayResult<bool>> SetPoolRunningAsync(string id, bool running, CancellationToken ct = default);

    /// <summary>新增或修改一个池。</summary>
    Task<GatewayResult<bool>> UpsertPoolAsync(PoolSummary pool, CancellationToken ct = default);

    /// <summary>删掉一个池。</summary>
    Task<GatewayResult<bool>> RemovePoolAsync(string id, CancellationToken ct = default);

    /// <summary>模型目录。</summary>
    Task<GatewayResult<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// 聊天补全。
    ///
    /// 流式：每收到一个增量 yield 一次。视图只负责把增量拼到界面上。
    /// 网关没起时 yield 一条说明，不抛。
    /// </summary>
    IAsyncEnumerable<string> ChatStreamAsync(
        string model,
        string prompt,
        CancellationToken ct = default);

    /// <summary>
    /// 取一段媒体的字节。
    ///
    /// **安全边界都在这里**（见实现里的注释）：
    ///   · URL 必须过白名单（只允许网关自身 host）
    ///   · 有字节上限（防一张巨图卡死界面）
    ///   · 有超时（防一直挂着）
    ///
    /// 任何一条不满足都返回带原因的失败，**绝不抛** —— 与其它 Gateway 调用一致。
    /// </summary>
    Task<GatewayResult<MediaBytes>> FetchMediaAsync(string url, CancellationToken ct = default);

    /// <summary>读网关的脱敏日志尾部若干行。</summary>
    Task<GatewayResult<IReadOnlyList<LogLine>>> ReadLogAsync(
        int maxLines,
        CancellationToken ct = default);

    /// <summary>
    /// 读各账号的积分（余额 + 积分包明细）。
    ///
    /// ⚠ **网关端点可能还没实现**（t70 在做）——
    /// 那时它返回失败，界面**走空态而不是崩**，与其它调用一致。
    /// </summary>
    Task<GatewayResult<IReadOnlyList<AccountCredits>>> GetCreditsAsync(CancellationToken ct = default);

    /// <summary>
    /// 读**全部账号**的积分（`/admin/credits/all`）。
    ///
    /// 与 <see cref="GetCreditsAsync"/> 的区别：那个只回一个账号
    /// （activeId 或第一个），这个回全部 —— 账号页每张卡都要积分，
    /// 池子总积分也要靠它汇总。单个号查失败不挡别的号。
    /// </summary>
    Task<GatewayResult<IReadOnlyList<AccountCredits>>> GetAllCreditsAsync(CancellationToken ct = default);

    /// <summary>
    /// 读用量账本明细（`/usage/recent?limit=N`）—— 每笔请求的实际扣费、
    /// token、模型、账号。给「每个账号的积分消耗情况」用。
    ///
    /// ⚠ 账本明细含 credit（上游实际扣费）—— 这是「每个请求消耗了多少积分」
    /// 的唯一数据源（`/metrics` 的 recent 没有 credit）。
    /// </summary>
    Task<GatewayResult<IReadOnlyList<UsageRecord>>> GetUsageRecentAsync(int limit, CancellationToken ct = default);

    /// <summary>跨池总积分：所有池的积分总和（数据库联表 pool_accounts）。</summary>
    Task<GatewayResult<double>> GetPoolCreditTotalAsync(CancellationToken ct = default);

    /// <summary>读设置。</summary>
    Task<GatewayResult<GatewaySettings>> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>写设置。</summary>
    Task<GatewayResult<bool>> SaveSettingsAsync(GatewaySettings settings, CancellationToken ct = default);

    /// <summary>
    /// 一键签到全部账号（或指定账号）。
    ///
    /// @param accountId 指定账号 id；null = 全部。
    /// @returns 每个账号的结果（已签 / 新签 / 失败含原因）。
    /// </summary>
    Task<GatewayResult<IReadOnlyList<CheckinResultDto>>> CheckinAsync(string? accountId, CancellationToken ct = default);

    /// <summary>读任务库列表（含进度/描述）。<paramref name="limit"/> 控制条数（分页用）。</summary>
    Task<GatewayResult<IReadOnlyList<TaskInfo>>> GetTasksAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>触发一个后台任务（refresh-credits / checkin-all / renew-all）。返回任务 id。</summary>
    Task<GatewayResult<string>> StartTaskAsync(string kind, CancellationToken ct = default);

    /// <summary>读一个任务的详情（含事件流）—— 点开任务看它每一步做了什么。</summary>
    Task<GatewayResult<TaskDetail>> GetTaskDetailAsync(string taskId, CancellationToken ct = default);
}

/// <summary>
/// 真实实现：一个 HttpClient，打网关的本地端口。
///
/// 两条纪律：
///  ① 任何失败都翻译成 <see cref="GatewayResult{T}"/> 或离线快照 —— 不往上抛。
///  ② 超时短（默认 3 秒）：网关没起时 connect refused 是即时的，
///     但如果端口被人占着不回包，界面不能卡住。
/// </summary>
public sealed class HttpGatewayClient : IGatewayClient
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public HttpGatewayClient(
        HttpClient http,
        int port = 8791,
        TimeSpan? timeout = null,
        string apiKey = "",
        string hostName = "127.0.0.1")
    {
        _http = http;
        Port = port;
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
        ApiKey = apiKey ?? "";
        if (!string.IsNullOrWhiteSpace(hostName)) HostName = hostName;
    }

    public int Port { get; set; }

    /// <summary>
    /// 客户端要出示的凭据。
    ///
    /// ⚠ **这不是 `GatewaySettings.ApiKey`。** 两者名字一样，但完全是两回事：
    ///   · `GatewaySettings.ApiKey`（<see cref="GetSettingsAsync"/> 读回来的）
    ///     = **网关自己**配置的、它用来校验来访者的那把 key。是服务端配置。
    ///   · 这个属性 = **本客户端**要向网关出示的那把 key。是客户端凭据。
    ///
    /// 正常情况下两者值相同（同一个部署里的同一把 key），所以特别容易混。
    /// 但用途不同：前者是「我要求别人出示什么」，后者是「我出示什么」。
    /// 从 GetSettingsAsync 拿这个值是不行的 —— 拿它本身就要先鉴权（鸡生蛋）。
    /// 所以它由外部注入（见 App.xaml.cs 读 BUDDYDECK_API_KEY）。
    ///
    /// 空串的语义见 <see cref="ApplyAuth"/>。
    /// </summary>
    public string ApiKey { get; set; }

    private string BaseUrl => $"http://{HostName}:{Port}";

    /// <summary>
    /// 用哪个主机名连本机网关。
    ///
    /// **实测结论（t18 验证，不是推断）**：`127.0.0.1` 与 `localhost` 都可以。
    /// 网关的 DNS-rebinding 防护白名单是四个，且<strong>大小写不敏感</strong>：
    /// <c>{ "127.0.0.1", "localhost", "[::1]", "::1" }</c>
    /// 所以 <c>localhost</c> 不会误撞 403 host_not_allowed。
    ///
    /// 那为什么默认仍然是 <c>127.0.0.1</c> 而不是 <c>localhost</c>：
    ///   · 报文里字面出现 IP，排查时一眼知道连的是本机哪个地址，不用先去解析名字
    ///   · `localhost` 在某些机器上会被解析到 <c>::1</c> 而不是 <c>127.0.0.1</c>，
    ///     而网关若只绑了 IPv4（默认绑定就是 127.0.0.1），那时会连不上
    ///
    /// 保留可覆盖是为了排查：真有环境两者不等价时，能不改代码直接换。
    /// </summary>
    public string HostName { get; set; } = "127.0.0.1";

    /// <summary>
    /// 给请求挂上鉴权头。
    ///
    /// **空 key 时不发头**，理由来自网关的实际判定（不是猜的）：
    /// <c>GatewayApp.Authorized</c> 第一句就是
    /// <c>if (string.IsNullOrEmpty(_options.ApiKey)) return true;</c>
    /// —— 网关**没配 key 时根本不校验**。所以本地默认部署（没设 key）下：
    ///   · 不发头 → 网关放行 ✅
    ///   · 发 `Bearer `（空值）→ 网关那边 header 非空但比较失败 → 反而 401 ❌
    /// 空头比不发头更坏，所以这里选择不发。
    ///
    /// 反过来，网关**配了** key 而客户端没设 → 会 401，这是正确的失败：
    /// 用户需要把 BUDDYDECK_API_KEY 设成网关那把。
    /// </summary>
    private void ApplyAuth(HttpRequestMessage request)
    {
        // /healthz 不需要鉴权（网关特意把它排在鉴定之前，好让面板在没 key 时也能探活）。
        // 不发头是刻意的：探活路径尽量少依赖配置。
        if (request.RequestUri?.AbsolutePath == "/healthz") return;

        if (string.IsNullOrEmpty(ApiKey)) return;

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
    }

    /// <summary>
    /// 统一的 GET。**不用 `_http.GetAsync` 是因为那个重载没法挂头** ——
    /// 要挂 Authorization 就必须自己构造 HttpRequestMessage。
    /// </summary>
    private async Task<GatewayResult<T>> GetJsonAsync<T>(
        string path,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? _timeout);

            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            ApplyAuth(req);

            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return GatewayResult<T>.Failure(Describe(resp.StatusCode));

            var value = await resp.Content
                .ReadFromJsonAsync<T>(JsonOptions, cts.Token)
                .ConfigureAwait(false);
            return value is null
                ? GatewayResult<T>.Failure("网关回了空内容")
                : GatewayResult<T>.Success(value);
        }
        catch (OperationCanceledException)
        {
            return GatewayResult<T>.Failure("网关响应超时");
        }
        catch (HttpRequestException)
        {
            return GatewayResult<T>.Failure("网关没有启动");
        }
        catch (JsonException)
        {
            return GatewayResult<T>.Failure("网关回的格式看不懂（可能版本不匹配）");
        }
    }

    /// <summary>
    /// 把状态码翻译成一句能指导下一步的话。
    ///
    /// 401 和 404 在这里必须分开说，因为它们要用户做的事完全不同：
    ///   · 401 = 网关要 key，而我们的 key 不对/没给 → 去设 BUDDYDECK_API_KEY
    ///   · 404 = 网关根本没有这个端点 → 是版本不匹配（网关比界面旧），不是配置问题
    /// 都归成「网关回了 4xx」的话，用户在两个完全不同的修法之间没法选。
    /// </summary>
    private static string Describe(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.Unauthorized =>
            "网关要求 API key，而当前没有或不对（检查 BUDDYDECK_API_KEY）",
        System.Net.HttpStatusCode.Forbidden =>
            "网关拒绝了这次请求（Host 头不在回环白名单里）",
        System.Net.HttpStatusCode.NotFound =>
            "网关没有这个端点（网关版本可能比界面旧）",
        _ => $"网关回了 {(int)status}",
    };

    private async Task<GatewayResult<bool>> PostAsync<TBody>(string path, TBody body, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
            {
                Content = JsonContent.Create(body, options: JsonOptions),
            };
            ApplyAuth(req);

            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? GatewayResult<bool>.Success(true)
                : GatewayResult<bool>.Failure(Describe(resp.StatusCode));
        }
        catch (OperationCanceledException)
        {
            return GatewayResult<bool>.Failure("网关响应超时");
        }
        catch (HttpRequestException)
        {
            return GatewayResult<bool>.Failure("网关没有启动");
        }
    }

    /// <summary>
    /// POST 并**反序列化响应体**（`PostAsync` 只回"成功了吗"）。
    ///
    /// 为什么单独一个方法而不是给 `PostAsync` 加泛型重载：
    /// 90% 的 POST 调用方只关心成败（改设置、删账号），
    /// 让它们都去指定一个用不上的响应类型是噪音。
    /// 只有「需要读回服务端算出来的值」时才用这个（目前只有导出条数）。
    /// </summary>
    private async Task<GatewayResult<T>> PostJsonAsync<T>(string path, object body, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
            {
                Content = JsonContent.Create(body, options: JsonOptions),
            };
            ApplyAuth(req);

            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return GatewayResult<T>.Failure(Describe(resp.StatusCode));
            }

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var value = System.Text.Json.JsonSerializer.Deserialize<T>(text, JsonOptions);
            return value is null
                ? GatewayResult<T>.Failure("网关返回了空的响应体")
                : GatewayResult<T>.Success(value);
        }
        catch (OperationCanceledException)
        {
            return GatewayResult<T>.Failure("网关响应超时");
        }
        catch (HttpRequestException)
        {
            return GatewayResult<T>.Failure("网关没有启动");
        }
        catch (System.Text.Json.JsonException ex)
        {
            return GatewayResult<T>.Failure($"网关响应不是合法 JSON：{ex.Message}");
        }
    }

    public async Task<GatewayHealth> GetHealthAsync(CancellationToken ct = default)
    {
        var port = Port;
        var result = await GetJsonAsync<HealthDto>("/healthz", ct).ConfigureAwait(false);
        if (!result.Ok || result.Value is null)
            return GatewayHealth.Offline(port, result.Error ?? "网关没有启动");

        var dto = result.Value;
        return new GatewayHealth
        {
            Online = dto.Ok,
            Port = port,
            Provider = dto.Provider ?? "",
            Models = dto.Models,
            BuildId = dto.BuildId ?? "",
            StartedAt = dto.StartedAt,
            Stale = dto.Stale,
        };
    }

    public Task<GatewayResult<GatewayMetrics>> GetMetricsAsync(CancellationToken ct = default)
        => GetJsonAsync<GatewayMetrics>("/metrics", ct);

    public async Task<GatewayResult<IReadOnlyList<AccountSummary>>> GetAccountsAsync(CancellationToken ct = default)
    {
        var r = await GetJsonAsync<List<AccountSummary>>("/admin/accounts", ct).ConfigureAwait(false);
        return r.Ok && r.Value is not null
            ? GatewayResult<IReadOnlyList<AccountSummary>>.Success(r.Value)
            : GatewayResult<IReadOnlyList<AccountSummary>>.Failure(r.Error ?? "读不到账号");
    }

    public Task<GatewayResult<bool>> ActivateAccountAsync(string id, CancellationToken ct = default)
        => PostAsync($"/admin/accounts/{Uri.EscapeDataString(id)}/activate", new { }, ct);

    public Task<GatewayResult<bool>> RenameAccountAsync(string id, string label, CancellationToken ct = default)
        => PostAsync($"/admin/accounts/{Uri.EscapeDataString(id)}/rename", new { label }, ct);

    public Task<GatewayResult<bool>> RemoveAccountAsync(string id, CancellationToken ct = default)
        => PostAsync($"/admin/accounts/{Uri.EscapeDataString(id)}/remove", new { }, ct);

    public Task<GatewayResult<bool>> RefreshTokenAsync(string id, CancellationToken ct = default)
        => PostAsync($"/admin/accounts/{Uri.EscapeDataString(id)}/refresh", new { }, ct);

    public async Task<GatewayResult<int>> ImportAccountsAsync(string payload, CancellationToken ct = default)
    {
        var r = await PostAsync("/admin/accounts/import", new { payload }, ct).ConfigureAwait(false);
        return r.Ok ? GatewayResult<int>.Success(1) : GatewayResult<int>.Failure(r.Error ?? "导入失败");
    }

    public async Task<GatewayResult<int>> ExportAccountsAsync(
        string path, string passphrase, CancellationToken ct = default)
    {
        // ⚠ 用 `PostAsync` 拿的是 bool（"成功了吗"），拿不到条数 ——
        //   要条数就得走能反序列化的那条路。这里单独发一次请求收 JSON。
        var r = await PostJsonAsync<ExportResultDto>(
            "/admin/accounts/export", new { path, passphrase }, ct).ConfigureAwait(false);

        if (!r.Ok || r.Value is null)
        {
            return GatewayResult<int>.Failure(r.Error ?? "导出失败");
        }

        return GatewayResult<int>.Success(r.Value.Count);
    }

    public async Task<GatewayResult<PoolHealthSnapshot>> GetPoolHealthAsync(CancellationToken ct = default)
    {
        var r = await GetJsonAsync<PoolHealthSnapshot>("/admin/pool-health", ct).ConfigureAwait(false);
        return r.Ok && r.Value is not null
            ? GatewayResult<PoolHealthSnapshot>.Success(r.Value)
            : GatewayResult<PoolHealthSnapshot>.Failure(r.Error ?? "读不到池健康");
    }

    /// <summary>导出端点回的形状：`{ok, count, path}`。</summary>
    private sealed record ExportResultDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("count")]
        public int Count { get; init; }
    }

    public async Task<GatewayResult<IReadOnlyList<PoolSummary>>> GetPoolsAsync(CancellationToken ct = default)
    {
        var r = await GetJsonAsync<List<PoolSummary>>("/admin/pools", ct).ConfigureAwait(false);
        return r.Ok && r.Value is not null
            ? GatewayResult<IReadOnlyList<PoolSummary>>.Success(r.Value)
            : GatewayResult<IReadOnlyList<PoolSummary>>.Failure(r.Error ?? "读不到池列表");
    }

    public async Task<GatewayResult<IReadOnlyList<AccountSummary>>> GetPoolAccountsAsync(string poolId, CancellationToken ct = default)
    {
        var r = await GetJsonAsync<List<AccountSummary>>(
            $"/admin/pools/{Uri.EscapeDataString(poolId)}/accounts", ct).ConfigureAwait(false);
        return r.Ok && r.Value is not null
            ? GatewayResult<IReadOnlyList<AccountSummary>>.Success(r.Value)
            : GatewayResult<IReadOnlyList<AccountSummary>>.Failure(r.Error ?? "读不到这个池的账号");
    }

    public Task<GatewayResult<bool>> SetPoolRunningAsync(string id, bool running, CancellationToken ct = default)
        => PostAsync($"/admin/pools/{Uri.EscapeDataString(id)}/state", new { running }, ct);

    public Task<GatewayResult<bool>> UpsertPoolAsync(PoolSummary pool, CancellationToken ct = default)
        => PostAsync("/admin/pools", pool, ct);

    public Task<GatewayResult<bool>> RemovePoolAsync(string id, CancellationToken ct = default)
        => PostAsync($"/admin/pools/{Uri.EscapeDataString(id)}/remove", new { }, ct);

    public async Task<GatewayResult<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken ct = default)
    {
        var r = await GetJsonAsync<ModelListDto>("/v1/models", ct).ConfigureAwait(false);
        if (!r.Ok || r.Value?.Data is null)
            return GatewayResult<IReadOnlyList<ModelInfo>>.Failure(r.Error ?? "读不到模型列表");

        IReadOnlyList<ModelInfo> models = [.. r.Value.Data.Select(m => new ModelInfo
        {
            Id = m.Id ?? "",
            DisplayName = string.IsNullOrWhiteSpace(m.Name) ? m.Id ?? "" : m.Name!,
        })];
        return GatewayResult<IReadOnlyList<ModelInfo>>.Success(models);
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string model,
        string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // C# 规范不允许在带 catch 的 try 块里 yield —— 所以网络调用和异常处理
        // 都收进 SendAsync 这个普通方法，迭代器这边只管 yield。
        var (resp, error) = await SendAsync(model, prompt, ct).ConfigureAwait(false);

        if (error is not null)
        {
            yield return error;
            yield break;
        }
        if (resp is null) yield break;   // 用户取消了，不提示也不报错

        // resp 持有网络流，必须释放 —— 不然每条流式连接都会漏。流式不能被缓冲，
        // 所以只能包在这里，不能改成「先收集再 yield」。
        using (resp)
        {
            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line[5..].Trim();
                if (payload.Length == 0) continue;
                if (payload == "[DONE]") break;

                string? delta = TryReadDelta(payload);
                if (delta is { Length: > 0 }) yield return delta;
            }
        }
    }

    /// <summary>
    /// 发一次流式请求，把「响应」和「给人看的原因」二选一交出去。
    ///
    /// 单独一个方法是为了绕开「迭代器里不能 try/catch + yield」的限制。
    /// 失败时返回 (null, 原因)，取消时返回 (null, null) —— 取消不是错误。
    /// </summary>
    private async Task<(HttpResponseMessage? Response, string? Error)> SendAsync(
        string model,
        string prompt,
        CancellationToken ct)
    {
        try
        {
            var body = new
            {
                model,
                stream = true,
                messages = new[] { new { role = "user", content = prompt } },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            ApplyAuth(req);

            var resp = await _http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var message = Describe(resp.StatusCode);
                resp.Dispose();
                return (null, $"{message}，没有正文可显示。");
            }

            return (resp, null);
        }
        catch (HttpRequestException)
        {
            return (null, "网关没有启动，聊天测试发不出去。先去「设置」看端口，或者启动网关。");
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
    }

    private static string? TryReadDelta(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)) return null;
            if (choices.GetArrayLength() == 0) return null;
            if (!choices[0].TryGetProperty("delta", out var delta)) return null;
            if (!delta.TryGetProperty("content", out var content)) return null;
            return content.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 取媒体字节。**三条安全边界都在这里，一个都不能省。**
    ///
    /// ① **URL 白名单**（`MediaUrlGuard`）：只允许 http/https + 回环 host + 当前网关端口。
    ///    这个 URL 最终来自网络，拿它发请求等于让远端决定我们连哪里。
    ///
    /// ② **字节上限**（`MediaUrlGuard.MaxBytes`，16 MB）：
    ///    先看 `Content-Length`，超了直接拒 —— **不要开始下载**。
    ///    但没有 `Content-Length` 时（分块传输）不能只靠它，
    ///    所以**边读边计数**，一旦超过上限立刻断开。
    ///    两个都要：只信头会被伪造的头骗，只信计数则要先白下载 500 MB。
    ///
    /// ③ **超时**（`MediaUrlGuard.Timeout`，20 秒）：
    ///    比普通 API 宽松（视频本来就大），但不能不设 ——
    ///    否则一个挂着的连接会让那一块永远停在加载态。
    /// </summary>
    public async Task<GatewayResult<MediaBytes>> FetchMediaAsync(string url, CancellationToken ct = default)
    {
        // ① 白名单。**在发请求之前** —— 这一步不能放到拿到响应之后，
        //    那样已经连出去了，白名单就失去意义了。
        if (!MediaUrlGuard.IsAllowedMediaUrl(url, Port))
            return GatewayResult<MediaBytes>.Failure(MediaUrlGuard.DescribeRejection(url, Port));

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(MediaUrlGuard.Timeout);

            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return GatewayResult<MediaBytes>.Failure($"媒体回 {Describe(resp.StatusCode)}");

            // ②a 先看声明长度。**有且超标就直接拒，不开始下载。**
            if (resp.Content.Headers.ContentLength is { } declared && declared > MediaUrlGuard.MaxBytes)
            {
                var mb = declared / 1024.0 / 1024.0;
                return GatewayResult<MediaBytes>.Failure(
                    $"媒体有 {mb:F1} MB，超过 {MediaUrlGuard.MaxBytes / 1024 / 1024} MB 上限，没有下载。");
            }

            // ②b 边读边计数 —— 防「没有 Content-Length 或头是假的」
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            await using (var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
            {
                int read;
                while ((read = await stream.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MediaUrlGuard.MaxBytes)
                    {
                        // 立刻停下。已经读到的部分丢掉 —— 半个媒体没法用。
                        return GatewayResult<MediaBytes>.Failure(
                            $"媒体超过 {MediaUrlGuard.MaxBytes / 1024 / 1024} MB 上限，已中止下载。");
                    }
                    buffer.Write(chunk, 0, read);
                }
            }

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            return GatewayResult<MediaBytes>.Success(new MediaBytes(buffer.ToArray(), contentType));
        }
        catch (OperationCanceledException)
        {
            // 用户取消 vs 超时：两者都落到这里。区分一下，因为提示不一样。
            return GatewayResult<MediaBytes>.Failure(
                ct.IsCancellationRequested ? "媒体下载已取消。" : "媒体下载超时。");
        }
        catch (HttpRequestException)
        {
            return GatewayResult<MediaBytes>.Failure("网关没有启动");
        }
    }

    public async Task<GatewayResult<IReadOnlyList<AccountCredits>>> GetCreditsAsync(CancellationToken ct = default)
    {
        // ⚠ **网关 `/admin/credits` 回的是"一个账号的积分**，不是数组**
        //
        // 实测踩过（用户反馈「账号卡片没有积分余额和明细」）：
        // <code>
        // 网关回：{ ok, accountTag, label, totalCount, totalDosage, packages:[…] }
        // </code>
        // 而这里原来按 `{ accounts:[…] }` 解析 ⇒ `Accounts` 恒为 null
        // ⇒ 走 Failure ⇒ 界面整块空态（**且不报错**，看起来像"没做"）。
        //
        // ⇒ 直接按单个对象解析，包成一个只有一个元素的列表返回。
        //   （保留列表形状是为了给将来"批量积分端点"留出余地，且调用侧不用改。）
        var r = await GetJsonAsync<AccountCredits>("/admin/credits", ct).ConfigureAwait(false);

        if (!r.Ok || r.Value is null)
        {
            // 端点没实现（404）时走这里 —— 「网关没有这个端点」。
            // 界面据此走空态 —— **不是崩溃**，也不是假装"积分是 0"。
            return GatewayResult<IReadOnlyList<AccountCredits>>.Failure(
                r.Error ?? "网关没有给出积分数据");
        }

        IReadOnlyList<AccountCredits> one = [r.Value];
        return GatewayResult<IReadOnlyList<AccountCredits>>.Success(one);
    }

    public async Task<GatewayResult<IReadOnlyList<AccountCredits>>> GetAllCreditsAsync(CancellationToken ct = default)
    {
        // ★ 优先走 `/admin/credits/cached`（数据库秒读，毫秒级返回）。
        //   这是用户需求的架构升级：积分快照存库，界面不再逐个查上游。
        //   快照可能比实时旧一点，但账号页只是展示，不是精确对账。
        var cached = await GetJsonAsync<AllCreditsDto>(
            "/admin/credits/cached", ct, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        if (cached.Ok && cached.Value?.Accounts is { Count: > 0 })
        {
            return GatewayResult<IReadOnlyList<AccountCredits>>.Success(cached.Value.Accounts);
        }

        // 回退：缓存为空（第一次运行/还没查过）→ 走实时批量端点。
        // ⚠ 超时给 90 秒：批量端点要**逐个账号向上游查**，20 个号可能
        //   要几十秒 —— 默认 3 秒必然超时（实测：返回「网关响应超时」）。
        var r = await GetJsonAsync<AllCreditsDto>(
            "/admin/credits/all", ct, TimeSpan.FromSeconds(90)).ConfigureAwait(false);

        if (!r.Ok || r.Value?.Accounts is null)
        {
            return GatewayResult<IReadOnlyList<AccountCredits>>.Failure(
                r.Error ?? "网关没有给出积分数据");
        }

        return GatewayResult<IReadOnlyList<AccountCredits>>.Success(r.Value.Accounts);
    }

    public async Task<GatewayResult<IReadOnlyList<UsageRecord>>> GetUsageRecentAsync(int limit, CancellationToken ct = default)
    {
        // /usage/recent 回 `{ items:[…], total, returned }`。
        // 账本明细可能很多，给 30 秒。
        var r = await GetJsonAsync<UsageRecentDto>(
            $"/usage/recent?limit={limit}", ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        if (!r.Ok || r.Value?.Items is null)
        {
            return GatewayResult<IReadOnlyList<UsageRecord>>.Failure(
                r.Error ?? "网关没有给出用量明细");
        }

        return GatewayResult<IReadOnlyList<UsageRecord>>.Success(r.Value.Items);
    }

    public async Task<GatewayResult<double>> GetPoolCreditTotalAsync(CancellationToken ct = default)
    {
        var r = await GetJsonAsync<PoolCreditTotalDto>("/admin/credits/pool-total", ct).ConfigureAwait(false);
        return r.Ok && r.Value is not null
            ? GatewayResult<double>.Success(r.Value.Total)
            : GatewayResult<double>.Failure(r.Error ?? "读不到跨池总积分");
    }

    public async Task<GatewayResult<IReadOnlyList<CheckinResultDto>>> CheckinAsync(string? accountId, CancellationToken ct = default)
    {
        var path = accountId is null
            ? "/admin/checkin"
            : $"/admin/checkin/{Uri.EscapeDataString(accountId)}";

        var r = await PostJsonAsync<CheckinListDto>(path, new { }, ct).ConfigureAwait(false);

        if (!r.Ok || r.Value?.Results is null)
        {
            return GatewayResult<IReadOnlyList<CheckinResultDto>>.Failure(
                r.Error ?? "签到失败");
        }

        return GatewayResult<IReadOnlyList<CheckinResultDto>>.Success(r.Value.Results);
    }

    public async Task<GatewayResult<IReadOnlyList<TaskInfo>>> GetTasksAsync(int limit = 50, CancellationToken ct = default)
    {
        var r = await GetJsonAsync<TaskListDto>($"/admin/tasks?limit={limit}", ct).ConfigureAwait(false);

        if (!r.Ok || r.Value?.Tasks is null)
        {
            return GatewayResult<IReadOnlyList<TaskInfo>>.Failure(r.Error ?? "读不到任务列表");
        }

        return GatewayResult<IReadOnlyList<TaskInfo>>.Success(r.Value.Tasks);
    }

    public async Task<GatewayResult<string>> StartTaskAsync(string kind, CancellationToken ct = default)
    {
        var r = await PostJsonAsync<StartTaskDto>(
            $"/admin/tasks/{Uri.EscapeDataString(kind)}", new { }, ct).ConfigureAwait(false);

        if (!r.Ok || r.Value is null || string.IsNullOrEmpty(r.Value.TaskId))
        {
            return GatewayResult<string>.Failure(r.Error ?? "启动任务失败");
        }

        return GatewayResult<string>.Success(r.Value.TaskId);
    }

    public async Task<GatewayResult<TaskDetail>> GetTaskDetailAsync(string taskId, CancellationToken ct = default)
    {
        var r = await GetJsonAsync<TaskDetail>(
            $"/admin/tasks/{Uri.EscapeDataString(taskId)}", ct).ConfigureAwait(false);

        if (!r.Ok || r.Value is null)
        {
            return GatewayResult<TaskDetail>.Failure(r.Error ?? "读不到任务详情");
        }

        return GatewayResult<TaskDetail>.Success(r.Value);
    }

    public async Task<GatewayResult<IReadOnlyList<LogLine>>> ReadLogAsync(int maxLines, CancellationToken ct = default)
    {
        var r = await GetJsonAsync<LogListDto>($"/admin/logs?lines={maxLines}", ct).ConfigureAwait(false);
        if (!r.Ok || r.Value?.Lines is null)
            return GatewayResult<IReadOnlyList<LogLine>>.Failure(r.Error ?? "读不到日志");

        IReadOnlyList<LogLine> lines = [.. r.Value.Lines.Select(l => new LogLine
        {
            At = l.At ?? DateTimeOffset.MinValue,
            Level = string.IsNullOrWhiteSpace(l.Level) ? "Information" : l.Level!,
            Message = l.Message ?? "",
        })];
        return GatewayResult<IReadOnlyList<LogLine>>.Success(lines);
    }

    public Task<GatewayResult<GatewaySettings>> GetSettingsAsync(CancellationToken ct = default)
        => GetJsonAsync<GatewaySettings>("/admin/settings", ct);

    public Task<GatewayResult<bool>> SaveSettingsAsync(GatewaySettings settings, CancellationToken ct = default)
        => PostAsync("/admin/settings", settings, ct);

    // ---- 网关 JSON 的形状。用 private 嵌套类，别让它们漏到界面层。----

    private sealed class AllCreditsDto
    {
        [JsonPropertyName("accounts")] public List<AccountCredits>? Accounts { get; set; }
    }

    private sealed class UsageRecentDto
    {
        [JsonPropertyName("items")] public List<UsageRecord>? Items { get; set; }
    }

    private sealed class CheckinListDto
    {
        [JsonPropertyName("results")] public List<CheckinResultDto>? Results { get; set; }
    }

    private sealed class TaskListDto
    {
        [JsonPropertyName("tasks")] public List<TaskInfo>? Tasks { get; set; }
    }

    private sealed class StartTaskDto
    {
        [JsonPropertyName("taskId")] public string? TaskId { get; set; }
    }

    private sealed class PoolCreditTotalDto
    {
        [JsonPropertyName("total")] public double Total { get; set; }
    }

    private sealed class HealthDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("provider")] public string? Provider { get; set; }
        [JsonPropertyName("models")] public int Models { get; set; }
        [JsonPropertyName("buildId")] public string? BuildId { get; set; }
        [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; set; }
        [JsonPropertyName("stale")] public bool Stale { get; set; }
    }

    private sealed class ModelListDto
    {
        [JsonPropertyName("data")] public List<ModelDto>? Data { get; set; }
    }

    private sealed class ModelDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class LogListDto
    {
        [JsonPropertyName("lines")] public List<LogLineDto>? Lines { get; set; }
    }

    private sealed class LogLineDto
    {
        [JsonPropertyName("at")] public DateTimeOffset? At { get; set; }
        [JsonPropertyName("level")] public string? Level { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}

/// <summary>
/// 「网关未连接」实现。
///
/// 用途：网关没起时不崩，而且界面照样能跑起来给人看 —— 每个调用返回一句
/// 说清楚「去做什么才能连上」的话，而不是异常。
/// 用途二：截图/演示时用它，保证画面稳定不依赖外部进程。
/// </summary>
public sealed class NotConnectedGatewayClient : IGatewayClient
{
    private const string Reason = "网关没有启动 —— 到「设置」确认端口，或者先启动 BuddyDeck.Gateway。";

    public NotConnectedGatewayClient(int port = 8791) => Port = port;

    public int Port { get; set; }

    /// <summary>离线实现里 key 无实际用途 —— 保留属性以满足接口契约。</summary>
    public string ApiKey { get; set; } = "";

    public Task<GatewayHealth> GetHealthAsync(CancellationToken ct = default)
        => Task.FromResult(GatewayHealth.Offline(Port, Reason));

    public Task<GatewayResult<GatewayMetrics>> GetMetricsAsync(CancellationToken ct = default)
        => Task.FromResult(GatewayResult<GatewayMetrics>.Failure(Reason));

    public Task<GatewayResult<IReadOnlyList<AccountSummary>>> GetAccountsAsync(CancellationToken ct = default)
        => Task.FromResult(GatewayResult<IReadOnlyList<AccountSummary>>.Failure(Reason));

    public Task<GatewayResult<bool>> ActivateAccountAsync(string id, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<bool>.Failure(Reason));

    public Task<GatewayResult<bool>> RenameAccountAsync(string id, string label, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<bool>.Failure(Reason));

    public Task<GatewayResult<bool>> RemoveAccountAsync(string id, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<bool>.Failure(Reason));

    public Task<GatewayResult<bool>> RefreshTokenAsync(string id, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<bool>.Failure(Reason));

    public Task<GatewayResult<int>> ImportAccountsAsync(string payload, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<int>.Failure(Reason));

    /// <summary>
    /// 未连接时的导出 —— **返回失败，不抛**。
    ///
    /// `GatewayClientNeverThrowsTests` 钉死了「网关没起来时所有操作都不抛」——
    /// 抛异常会让界面在离线时崩，而离线是**最常见**的状态
    /// （用户先开界面再开网关）。
    /// </summary>
    public Task<GatewayResult<int>> ExportAccountsAsync(
        string path, string passphrase, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<int>.Failure(Reason));

    /// <summary>未连接时的池健康 —— 返回失败，**不抛**（同上）。</summary>
    public Task<GatewayResult<PoolHealthSnapshot>> GetPoolHealthAsync(CancellationToken ct = default)
        => Task.FromResult(GatewayResult<PoolHealthSnapshot>.Failure(Reason));

    public Task<GatewayResult<IReadOnlyList<PoolSummary>>> GetPoolsAsync(CancellationToken ct = default)
        => Task.FromResult(GatewayResult<IReadOnlyList<PoolSummary>>.Failure(Reason));

    public Task<GatewayResult<IReadOnlyList<AccountSummary>>> GetPoolAccountsAsync(string poolId, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<IReadOnlyList<AccountSummary>>.Failure(Reason));

    public Task<GatewayResult<bool>> SetPoolRunningAsync(string id, bool running, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<bool>.Failure(Reason));

    public Task<GatewayResult<bool>> UpsertPoolAsync(PoolSummary pool, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<bool>.Failure(Reason));

    public Task<GatewayResult<bool>> RemovePoolAsync(string id, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<bool>.Failure(Reason));

    public Task<GatewayResult<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken ct = default)
        => Task.FromResult(GatewayResult<IReadOnlyList<ModelInfo>>.Failure(Reason));

    public async IAsyncEnumerable<string> ChatStreamAsync(
        string model,
        string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return "网关没有启动 —— 聊天测试连不上去。启动网关后重试。";
    }

    public Task<GatewayResult<MediaBytes>> FetchMediaAsync(string url, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<MediaBytes>.Failure(Reason));

    public Task<GatewayResult<IReadOnlyList<LogLine>>> ReadLogAsync(int maxLines, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<IReadOnlyList<LogLine>>.Failure(Reason));

    public Task<GatewayResult<IReadOnlyList<AccountCredits>>> GetCreditsAsync(CancellationToken ct = default)
        => Task.FromResult(GatewayResult<IReadOnlyList<AccountCredits>>.Failure(Reason));

    public Task<GatewayResult<IReadOnlyList<AccountCredits>>> GetAllCreditsAsync(CancellationToken ct = default)
        => Task.FromResult(GatewayResult<IReadOnlyList<AccountCredits>>.Failure(Reason));

    public Task<GatewayResult<IReadOnlyList<UsageRecord>>> GetUsageRecentAsync(int limit, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<IReadOnlyList<UsageRecord>>.Failure(Reason));

    public Task<GatewayResult<double>> GetPoolCreditTotalAsync(CancellationToken ct = default)
        => Task.FromResult(GatewayResult<double>.Failure(Reason));

    public Task<GatewayResult<IReadOnlyList<CheckinResultDto>>> CheckinAsync(string? accountId, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<IReadOnlyList<CheckinResultDto>>.Failure(Reason));

    public Task<GatewayResult<IReadOnlyList<TaskInfo>>> GetTasksAsync(int limit = 50, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<IReadOnlyList<TaskInfo>>.Failure(Reason));

    public Task<GatewayResult<string>> StartTaskAsync(string kind, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<string>.Failure(Reason));

    public Task<GatewayResult<TaskDetail>> GetTaskDetailAsync(string taskId, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<TaskDetail>.Failure(Reason));

    public Task<GatewayResult<GatewaySettings>> GetSettingsAsync(CancellationToken ct = default)
        => Task.FromResult(GatewayResult<GatewaySettings>.Failure(Reason));

    public Task<GatewayResult<bool>> SaveSettingsAsync(GatewaySettings settings, CancellationToken ct = default)
        => Task.FromResult(GatewayResult<bool>.Failure(Reason));
}
