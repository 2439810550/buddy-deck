using Microsoft.Extensions.Logging;

namespace BuddyDeck.Core;

/// <summary>
/// 一个「账号 × 模型」的限流记录。
///
/// 与 <see cref="PoolMember.Cooldowns"/> 的区别：
///   · `Cooldowns` 是**瞬时**的（成功就清）→ 决定"现在选谁"
///   · 这个是**历史累积**的（成功不清）→ 告诉用户"这个模型到底行不行"
///
/// @param Count         累计被限流多少次（本进程生命周期内）
/// @param LastAtMs     最近一次被限流的时刻
/// @param LastResetAtMs 最近一次的**解除时刻**（上游给的就用上游的，
///                      没给就按默认冷却算）——「啥时候解除限流」问的就是它
/// @param Recovered    最近一次限流之后**是否已经成功过**
///                     （用来区分「还限着」和「限过但已恢复」）
/// </summary>
public readonly record struct ModelRateLimit(
    int Count,
    long LastAtMs,
    long LastResetAtMs,
    bool Recovered)
{
    /// <summary>
    /// 此刻还在冷却吗。
    ///
    /// ⚠ 判据是**两个**，缺一不可：
    ///   · `Recovered` —— 之后成功过（说明它已经好了，哪怕 `LastResetAtMs` 还在未来）
    ///   · `LastResetAtMs > nowMs` —— 上游说的解除时刻还没到
    ///
    /// **只看第二个是错的**：`MarkSuccess` 会标 `Recovered = true`，
    /// 但它**不动 `LastResetAtMs`**（那是"上游说的时刻"，是个事实记录，
    /// 不该被我们改写）。于是"已经恢复"的号会因为 `LastResetAtMs` 还在未来
    /// 而被判成"仍在冷却" —— 界面就会显示一个假的红。
    ///
    /// （这条是测试逼出来的：`成功之后_冷却清了但限流历史仍在` 一开始就是红的。）
    /// </summary>
    public bool IsCoolingAt(long nowMs) => !Recovered && LastResetAtMs > nowMs;
}

/// <summary>
/// 一个模型在**全池范围**内的限流汇总 —— 界面「哪些模型被限流了、什么时候解除」
/// 那一行就是它。
///
/// ## 为什么按模型汇总而不是按账号（用户实测反馈）
///
/// 用户原话：「20 个号的 deepseek-v4.1-flash 基本都限流了，**但是显示全绿**。
/// 应该告诉用户这些模型限流了、**啥时候解除限流**」。
///
/// ⇒ 他问的是**模型维度**：「deepseek 是不是废了、什么时候能再用」。
///   而账号列表回答的是**账号维度**（"这个号还好不好"）——
///   两个问题不同：同一个号上某个模型废掉、别的模型照样能用，是常态。
///
/// @param LimitedAccounts   被限流过的号数
/// @param TotalAccounts     池里总共几个号
/// @param RecoveredAccounts 其中已经恢复的号数
/// @param LastResetAtMs     最晚的解除时刻（"全都好了"的时间）
/// @param AnyCoolingNow     此刻还有号在冷却吗
/// </summary>
public readonly record struct ModelRateLimitSummary(
    int LimitedAccounts,
    int TotalAccounts,
    int RecoveredAccounts,
    long LastResetAtMs,
    bool AnyCoolingNow);


/// <summary>一个账号在池里的运行时状态。</summary>
public sealed class PoolMember
{
    public required VaultEntry Entry { get; init; }

    /// <summary>这个账号各个模型的冷却截止时刻（Unix 毫秒）。</summary>
    public Dictionary<string, long> Cooldowns { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 每个模型**最近一次被限流**的记录 —— **成功后不清**。
    ///
    /// ## 为什么必须单独存（用户实测反馈）
    ///
    /// 用户原话：「20 个号的 deepseek-v4.1-flash 基本都限流了，
    /// **但是显示全绿**」。他还要求：**「应该告诉用户这些模型限流了、
    /// 啥时候解除限流」**。
    ///
    /// 而 `<see cref="Cooldowns"/>` **做不到这件事**：
    /// `MarkSuccess` 成功一次就 `Cooldowns.Remove(model)` ——
    /// 于是「刚才被限流、靠换模型救回来了」**不留任何痕迹**：
    /// <code>
    /// 用户看到的：全绿、池健康
    /// 实际发生的：请求 deepseek-flash → 连续 3 次被换成 glm-5.3
    /// </code>
    ///
    /// ⇒ `Cooldowns` 回答「**现在**能不能用」（瞬时状态），
    ///   这份回答「**最近**有多常不能用、上次什么时候解除的」（历史）。
    ///   两者都要：前者决定选号，后者决定用户要不要换个模型用。
    /// </summary>
    public Dictionary<string, ModelRateLimit> RateLimits { get; } = new(StringComparer.Ordinal);

    /// <summary>连续失败次数。成功一次就清零。</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>被选中过多少次（用于公平轮询）。</summary>
    public long SelectionCount { get; set; }

    /// <summary>
    /// 此刻正压在这个号上的请求数（瞬时负载）。
    ///
    /// ## 它和 <see cref="SelectionCount"/> 是两个维度，都要
    ///
    /// · `SelectionCount` = **历史累计**，用于长期公平（防偏袒某个号）
    /// · `InFlight`       = **瞬时在途**，用于当下分流（防挤爆某个号）
    ///
    /// 只有前者时，`Pick` 看到的永远是「谁被选得少」，
    /// 而**不知道谁此刻正忙** —— 于是并发下的几个请求会按同一份历史
    /// 挑到同一批号。ui 的实测印证：3 号 / 6 并发时每号承担 2 个。
    ///
    /// ⚠ 它是**只读暴露**给外部看的计数（可观测出口），
    /// 只能由 <see cref="AccountPool.Acquire"/> / <c>Release</c> 改动
    /// —— 直接赋值会让「租约成对」的保证失效。
    /// </summary>
    public int InFlight { get; internal set; }

    /// <summary>上次被用到的时刻。</summary>
    public long LastUsedAtMs { get; set; }

    public string Id => Entry.Id;
    public string Label => string.IsNullOrEmpty(Entry.Label) ? Entry.Uid[..Math.Min(8, Entry.Uid.Length)] : Entry.Label;
}

/// <summary>
/// 账号池。
///
/// ## 它解决的问题
///
/// 多个账号共用一套配额。要保证：
///   · 某个号被限流时**自动换号**，用户无感
///   · 某个号挂了不会一直撞它（冷却）
///   · 冷却是**按模型**的 —— 上游的限流常常只针对单个模型
///   · 多轮对话不跳号（会话粘性）
///   · 负载均匀（公平轮询）
///
/// ## 冷却为什么按模型分
///
/// 上游的 429 有两种：整个账号被限，或者只有某个模型被限。
/// 后者很常见（热门模型大家抢）。如果不分模型，一个模型被限就把
/// 整个号拉黑 —— 白扔配额。
///
/// ## 冷冻（Freeze）和冷却（Cooldown）的区别
///
/// · **冷却**：上游说「你太快了」，等一会儿就好，按模型记
/// · **冷冻**：本地判断这个号有问题（连续失败太多次），整个号停用，
///   等一轮余额刷新确认它恢复了再解冻
///
/// 后者是为了防「一个坏号被反复选中」—— 连续失败计数到阈值就整体停。
/// </summary>
public sealed partial class AccountPool
{
    private readonly List<PoolMember> _members = [];
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly ILogger<AccountPool> _log;

    /// <summary>
    /// 限流历史的落盘存储（可选）。为 null 时退化成纯内存 ——
    /// 测试和不想落盘的调用方可以不给。
    /// </summary>
    private readonly RateLimitStore? _rateLimits;

    /// <summary>是否已经从 store 恢复过一次历史（只读盘一次，不每请求都 IO）。</summary>
    private bool _rateLimitsRestored;

    /// <summary>从 store 读回的历史快照（键 = uid）。</summary>
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, ModelRateLimit>>? _storedRateLimits;

    /// <summary>连续失败到多少次就整体冷冻这个号。</summary>
    internal const int FreezeThreshold = 5;

    /// <summary>默认冷却时长（上游没说恢复时间时用）。</summary>
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(5);

    [LoggerMessage(Level = LogLevel.Information, Message = "账号 {Label} 的 {Model} 进入冷却，{Until} 后恢复")]
    private partial void LogCooldown(string label, string model, DateTimeOffset until);

    // 为什么要判 IsEnabled 再算参数：DateTimeOffset.FromUnixTimeMilliseconds
    // 是纯计算，日志关掉的时候不该白做（分析器 CA1873 就是管这个的）。
    // 单次开销小，但这是每个 429 都会走的路。
    [LoggerMessage(Level = LogLevel.Information, Message = "账号 {Label} 的 {Model} 进入冷却，{Minutes} 分钟后恢复")]
    private partial void LogCooldownMinutes(string label, string model, int minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "账号 {Label} 连续失败 {Count} 次，整体冷冻")]
    private partial void LogFreeze(string label, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "限流历史落盘失败：{Message}")]
    private partial void LogRateLimitPersistFailed(string message);

    /// <summary>
    /// @param rateLimitStore 限流历史的落盘存储。可空 —— 不给就纯内存
    ///         （测试、以及不想落盘的调用方用默认 null）。给的话：
    ///         · 首次 <see cref="Replace"/> 时从盘上恢复一次历史
    ///         · 每次 <see cref="MarkRateLimited"/> / <see cref="MarkSuccess"/>
    ///           之后把历史写回盘（重启不丢）。
    /// </summary>
    public AccountPool(TimeProvider clock, ILogger<AccountPool> log, RateLimitStore? rateLimitStore = null)
    {
        _clock = clock;
        _log = log;
        _rateLimits = rateLimitStore;
    }

    /// <summary>池里现在有哪些账号（快照，改不到内部状态）。</summary>
    public IReadOnlyList<PoolMember> Members
    {
        get
        {
            lock (_gate) return _members.ToArray();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate) return _members.Count;
        }
    }

    /// <summary>换一批账号（保险库变了、切池了）。冷却状态会尽量保留。</summary>
    public void Replace(IEnumerable<VaultEntry> entries)
    {
        lock (_gate)
        {
            // ★ 首次 Replace 时从盘上恢复一次限流历史（只读盘一次）。
            //   必须在任何成员建好之前做，这样下面 foreach 里能按 uid 合并。
            RestoreRateLimitsOnceLocked();

            var byId = _members.ToDictionary(m => m.Id, StringComparer.Ordinal);
            _members.Clear();

            foreach (var entry in entries)
            {
                if (byId.TryGetValue(entry.Id, out var old))
                {
                    // ★ 必须建**新对象**、用**新的 Entry**（含新 token）——
                    //   `PoolMember.Entry` 是 init（不可改），直接复用 old 的话
                    //   池里存的还是旧 token ⇒ 用户重新登录/导入后调上游仍 401。
                    //
                    //   运行时状态照旧搬（冷却、限流历史、失败计数、选中计数）：
                    //   不能因为刷新账号列表就把冷却清掉，否则被限流的号
                    //   会立刻被再次选中，又撞一次 429。
                    //
                    //   InFlight 也照搬：正在跑着请求的号换 Entry 不该丢租约。
                    var fresh = new PoolMember { Entry = entry };
                    foreach (var (model, until) in old.Cooldowns) fresh.Cooldowns[model] = until;
                    foreach (var (model, rl) in old.RateLimits) fresh.RateLimits[model] = rl;
                    fresh.ConsecutiveFailures = old.ConsecutiveFailures;
                    fresh.SelectionCount = old.SelectionCount;
                    fresh.InFlight = old.InFlight;
                    fresh.LastUsedAtMs = old.LastUsedAtMs;
                    _members.Add(fresh);
                }
                else
                {
                    var fresh = new PoolMember { Entry = entry };
                    // 新号：从盘上历史里找回它曾经的限流记录（若有）。
                    ApplyStoredRateLimits(fresh, entry);
                    _members.Add(fresh);
                }
            }
        }
    }

    /// <summary>
    /// 只读盘一次：把落盘历史读进 <see cref="_storedRateLimits"/>。
    /// 必须在 <see cref="_gate"/> 锁内调。
    /// </summary>
    private void RestoreRateLimitsOnceLocked()
    {
        if (_rateLimits is null || _rateLimitsRestored) return;
        _rateLimitsRestored = true;

        try
        {
            _storedRateLimits = _rateLimits.Load();
        }
        catch (Exception ex)
        {
            // 盘读不到不致命 —— 历史降级成纯内存。但失败要留日志。
            _storedRateLimits = null;
            if (_log.IsEnabled(LogLevel.Warning))
            {
                LogRateLimitPersistFailed(ex.Message);
            }
        }
    }

    /// <summary>把盘上的历史合并进一个新号（按 uid 找）。锁内调用。</summary>
    private void ApplyStoredRateLimits(PoolMember member, VaultEntry entry)
    {
        if (_storedRateLimits is null || string.IsNullOrEmpty(entry.Uid)) return;

        if (_storedRateLimits.TryGetValue(entry.Uid, out var models))
        {
            foreach (var (model, rl) in models)
            {
                member.RateLimits[model] = rl;
            }
        }
    }

    /// <summary>
    /// 把当前内存里的限流历史写盘（重启不丢）。
    ///
    /// ⚠ 失败只记 Warning、绝不抛 —— 落盘失败不该拖垮一个聊天请求。
    /// 键用 uid（不是 id）：store 的格式是「uid × model」。
    /// </summary>
    private void PersistRateLimits()
    {
        if (_rateLimits is null) return;

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ModelRateLimit>> snapshot;
        lock (_gate)
        {
            snapshot = _members
                .Where(m => !string.IsNullOrEmpty(m.Entry.Uid))
                .ToDictionary(
                    m => m.Entry.Uid,
                    m => (IReadOnlyDictionary<string, ModelRateLimit>)
                        new Dictionary<string, ModelRateLimit>(m.RateLimits, StringComparer.Ordinal),
                    StringComparer.Ordinal);
        }

        try
        {
            _rateLimits.Save(snapshot);
        }
        catch (Exception ex)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                LogRateLimitPersistFailed(ex.Message);
            }
        }
    }

    /// <summary>
    /// 挑一个号。
    ///
    /// ## 排序规则（依次比较）
    ///
    /// 1. **排除在冷却里的**（针对这个模型）
    /// 2. **排除冷冻的**（连续失败太多）
    /// 3. **排除调用方点名不要的**（比如刚试过的那个）
    /// 4. **优先 token 快过期的** —— 反正要刷新，先用掉
    /// 5. **其次选用得最少的** —— 负载均匀
    ///
    /// 第 4 条的理由：token 快过期的号迟早要刷新，与其晾着不如用掉；
    /// 而且刷新是有成本的（一次额外请求），集中用完再统一刷新更省。
    ///
    /// @param model 要用的模型名（冷却按模型记）
    /// @param exclude 排除这些 id（刚失败过的）
    /// @returns 选中的成员；没有可用的返回 null
    /// </summary>
    public PoolMember? Pick(string model, IReadOnlyCollection<string>? exclude = null)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var skip = exclude ?? [];

        lock (_gate)
        {
            var candidates = _members
                .Where(m => !skip.Contains(m.Id))
                .Where(m => !IsCoolingLocked(m, model, now))
                .Where(m => m.ConsecutiveFailures < FreezeThreshold)
                .ToList();

            if (candidates.Count == 0) return null;

            // ⚠ **负载必须是主键，不能是次键**。
            //
            // 实测教训（e2e 的 t61 + captain 的 t66 复验，均为真机）：
            // 这里原本是 `.OrderBy(ExpiresAtMs).ThenBy(SelectionCount)`。
            // 19 个号的过期时刻**各不相同**、且主号最小 ⇒ 它永远排第一，
            // **次键 SelectionCount 永远轮不到生效**
            // ⇒ 6 个并发请求全落同一个号（连跑 3 轮一致）。
            //
            // 所以"把负载加进排序"是不够的 —— 只要它排在某个
            // 「各成员互不相同」的键之后，就等于没加。
            var picked = candidates
                // ① 谁现在最闲（InFlight 由 Acquire 的租约维护；Pick 路径下恒为 0，
                //    此时退化为 ②）
                .OrderBy(m => m.InFlight)
                // ② 谁被选得最少 —— Pick 路径下这才是真正起作用的那个
                .ThenBy(m => m.SelectionCount)
                // ③ 快过期的优先用掉（原意图保留，但它只是平局裁决）
                .ThenBy(m => m.Entry.ExpiresAtMs == 0 ? long.MaxValue : m.Entry.ExpiresAtMs)
                .First();

            picked.SelectionCount++;
            picked.LastUsedAtMs = now;
            return picked;
        }
    }

    /// <summary>
    /// 看一眼「池会不会选它」，**不改变任何状态**。
    ///
    /// 为什么需要这个：会话粘性命中时要先确认 pinned 那个号现在还能用。
    /// 用 <see cref="Pick"/> 去确认有两个问题：
    ///   ① Pick 会推进它选中的那个号的 SelectionCount —— 如果最后没用它
    ///      （因为 pinned 更优先），那个号就白白背了一次计数，负载会歪
    ///   ② Pick 每次可能返回不同的号（看 SelectionCount 排序），
    ///      拿它当「可用性探测」用本身就不对
    ///
    /// 所以单独给一个只读探测：条件跟 <see cref="Pick"/> 完全一致
    /// （不冷却、不冷冻、不在排除名单里），但一个字节都不写。
    /// </summary>
    /// <returns>这个号现在能不能被选中。</returns>
    public bool CanPick(string memberId, string model, IReadOnlyCollection<string>? exclude = null)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();

        lock (_gate)
        {
            var member = _members.FirstOrDefault(m => m.Id == memberId);
            if (member is null) return false;
            if (exclude is not null && exclude.Contains(memberId)) return false;
            if (member.ConsecutiveFailures >= FreezeThreshold) return false;

            // 冷却过期的判断会顺带清理过期记录 —— 那是 Pick 也有的行为，
            // 保持两边的观察结果一致（否则 Pick 认为可用、CanPick 认为不可用）。
            return !IsCoolingLocked(member, model, now);
        }
    }

    /// <summary>
    /// 点名要一个号（会话粘性用）。
    ///
    /// 和 <see cref="Pick"/> 的区别：不参与公平轮询的排序，直接给指定的那个，
    /// 但**同样会推进它自己的 SelectionCount / LastUsedAtMs**。
    ///
    /// 为什么必须推进：粘性是「同一个会话一直用 A」，A 的选中次数会因此
    /// 涨得比别的号快。如果不记，池会以为 A 一直闲着 —— 等这个会话结束、
    /// 轮到正常轮询时，A 会被优先选中一大段，负载反而更不均。
    ///
    /// 号不存在时返回 null（并发下刚被 Replace 掉的窗口期），
    /// **不抛异常** —— 调用方（网关）会退化到用 Pick 的结果。
    ///
    /// @returns 指定的成员；它不在池里时 null。
    /// </summary>
    public PoolMember? SelectSpecific(string memberId, string model)
    {
        _ = model;   // 冷却不在这里判 —— 调用方（ConversationAffinity）已经查过 CanPick
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();

        lock (_gate)
        {
            var member = _members.FirstOrDefault(m => m.Id == memberId);
            if (member is null) return null;

            member.SelectionCount++;
            member.LastUsedAtMs = now;
            return member;
        }
    }

    /// <summary>记一次成功。清掉这个号的失败计数和该模型的冷却。</summary>
    public void MarkSuccess(string memberId, string model)
    {
        lock (_gate)
        {
            var m = _members.FirstOrDefault(x => x.Id == memberId);
            if (m is null) return;

            m.ConsecutiveFailures = 0;
            m.Cooldowns.Remove(model);

            // ⚠ **但限流历史不删**（`RateLimits`），只标记它已恢复。
            //
            // 这正是用户要的那条信息：「这个模型刚才是限流了、现在好了」——
            // 删掉的话界面上永远是"全绿"，而用户体感是"老是换模型"。
            if (m.RateLimits.TryGetValue(model, out var rl))
            {
                m.RateLimits[model] = rl with { Recovered = true };
            }
        }

        PersistRateLimits();
    }

    /// <summary>
    /// 记一次限流。
    ///
    /// @param resetAtMs 上游给的恢复时刻。不给我们按默认 5 分钟算 ——
    ///                  保守一点，别一直去撞。
    /// </summary>
    public void MarkRateLimited(string memberId, string model, long? resetAtMs)
    {
        var now = _clock.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var until = resetAtMs ?? now.Add(DefaultCooldown).ToUnixTimeMilliseconds();

        lock (_gate)
        {
            var m = _members.FirstOrDefault(x => x.Id == memberId);
            if (m is null) return;

            m.Cooldowns[model] = until;
            m.ConsecutiveFailures++;

            // ★ **累积限流历史**（成功不清，只标 Recovered）——
            //   这是"啥时候解除限流"这条信息的唯一来源。
            var previous = m.RateLimits.TryGetValue(model, out var had) ? had : default;
            m.RateLimits[model] = new ModelRateLimit(
                Count: previous.Count + 1,
                LastAtMs: nowMs,
                LastResetAtMs: until,
                // 又限了一次 ⇒ 它当前**没有**恢复
                Recovered: false);

            if (m.ConsecutiveFailures >= FreezeThreshold && _log.IsEnabled(LogLevel.Warning))
            {
                LogFreeze(m.Label, m.ConsecutiveFailures);
            }

            if (_log.IsEnabled(LogLevel.Information))
            {
                // ⚠ 这些值都在真正的日志调用**之前**算好放进局部变量。
                //
                // 为什么：CA1873 会把「实参位置上的任何计算」当成潜在浪费 ——
                // m.Label 是计算属性（没标签时要 Substring uid），
                // Math.Round 也是计算。日志关掉时它们都不该做。
                // 单次开销很小，但这是每个 429 都会走的路。
                var label = m.Label;
                var minutesLeft = (until - nowMs) / 60_000;
                LogCooldownMinutes(label, model, (int)minutesLeft);
            }
        }

        PersistRateLimits();
    }

    /// <summary>记一次普通失败（不是限流）。</summary>
    public void MarkFailure(string memberId, string model)
    {
        lock (_gate)
        {
            var m = _members.FirstOrDefault(x => x.Id == memberId);
            if (m is null) return;
            m.ConsecutiveFailures++;
        }
    }

    /// <summary>
    /// 按**这次失败的性质**决定记不记账。**t79 的修复点。**
    ///
    /// ## 为什么需要这个重载
    ///
    /// <see cref="MarkFailure"/> 无条件 `ConsecutiveFailures++` ——
    /// 调用方把「连接层失败」和「上游 5xx」都喂给它。后果是真机实测过的：
    ///
    /// ```
    /// 系统代理没生效 → 所有请求 status = 0（连不上）
    /// → 8 个健康账号各被记 5 次 → 全部冷冻
    /// → 之后一律 429「没有可用账号」，界面看不出是环境问题
    /// ```
    /// **几十秒冻掉一整池，且不可自愈**（冷冻要手动解）。
    ///
    /// ## 判据见 <see cref="TransportFailure.ShouldCountAgainstAccount"/>
    ///
    /// ```
    /// status == 0（连不上）  → **不记**（环境问题，惩罚账号没有意义）
    /// 上游 5xx（有响应体）    → 记（可能是这个号/这条链路的问题）
    /// ```
    ///
    /// @returns 真的记了返回 true；因为"这是环境问题"而跳过返回 false。
    ///          返回值是给调用方用的：**跳过时不要再去查 / 处理冷冻**，
    ///          否则会做出「因为环境问题而把这个号停用」的错误动作。
    ///
    /// ## 为什么保留旧的无参重载
    ///
    /// 它仍然有用：有些调用点已经**自己判过**了（比如明确知道这是 5xx）。
    /// 但**新代码应该优先用这个** —— 让判断只有一处真源。
    /// </summary>
    public bool MarkFailure(string memberId, string model, int status, string? kind)
    {
        if (!TransportFailure.ShouldCountAgainstAccount(status, kind))
        {
            // 环境问题：一个计数都不动。
            //
            // ⚠ 这里**不重置** ConsecutiveFailures —— 那是"洗白历史"，
            //    会让真正坏掉的号重新被选中。只是"这次不算"，不是"以前白算"。
            return false;
        }

        MarkFailure(memberId, model);
        return true;
    }

    /// <summary>
    /// 这个号是不是**已经被冷冻**（连续失败到阈值，整个号停用）。
    ///
    /// 为什么单独给一个查询而不是把 <see cref="FreezeThreshold"/> 公开出去：
    /// 网关（会话粘性）需要知道的是「刚才这次失败有没有把这个号整个干掉」，
    /// 而不是「阈值是几」。把阈值暴露出去等于邀请调用方自己算 ——
    /// 那意味着阈值一改，外部逻辑就悄悄错了。
    /// </summary>
    public bool IsFrozen(string memberId)
    {
        lock (_gate)
        {
            var m = _members.FirstOrDefault(x => x.Id == memberId);
            return m is not null && m.ConsecutiveFailures >= FreezeThreshold;
        }
    }

    /// <summary>
    /// 这个号**当前有哪些模型正在冷却**，以及各自何时解锁。
    ///
    /// ## 为什么要暴露这个（用户实测反馈）
    ///
    /// 「每一个账号的模型冷却情况也没有」—— 界面只能看到号在不在用，
    /// 看不到「这个号的 hy3 被限流了、再过 12 分钟才好」。
    /// 而**限流是按「号+模型」二元组记的**（`CanPick(memberId, model)`），
    /// 所以同一个号可能 hy3 冷却、hy4 正常 —— 用户看不到这层，
    /// 只会感觉"这个号时好时坏"。
    ///
    /// ⚠ 返回的是**快照**（复制一份）：调用方拿到的字典与内部状态解耦，
    /// 不会因为后台又打了个冷却而在枚举时抛「集合被修改」。
    ///
    /// @returns 模型 id → 解锁时刻（Unix 毫秒）。**不含已过期的条目**
    ///          —— 过期的冷却在语义上等于没有，透出去只会让界面显示"已过期"的噪音。
    /// </summary>
    public IReadOnlyDictionary<string, long> CooldownsOf(string memberId, TimeProvider? clock = null)
    {
        var now = (clock ?? _clock).GetUtcNow().ToUnixTimeMilliseconds();

        lock (_gate)
        {
            var m = _members.FirstOrDefault(x => x.Id == memberId);
            if (m is null) return new Dictionary<string, long>();

            var live = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (model, until) in m.Cooldowns)
            {
                if (until > now) live[model] = until;
            }

            return live;
        }
    }

    /// <summary>这个号连续失败了几次（界面上用来解释"为什么它被冷冻了"）。</summary>
    public int FailuresOf(string memberId)
    {
        lock (_gate)
        {
            return _members.FirstOrDefault(x => x.Id == memberId)?.ConsecutiveFailures ?? 0;
        }
    }

    /// <summary>
    /// 全池的**限流历史汇总**：每个模型被多少个号限流过、最近一次何时解除。
    ///
    /// ## 为什么是"按模型"而不是"按账号"（用户实测反馈）
    ///
    /// 用户原话：「20 个号的 deepseek-v4.1-flash 基本都限流了，
    /// **但是显示全绿**。应该告诉用户这些模型限流了、**啥时候解除限流**」。
    ///
    /// ⇒ 他问的是**模型维度**：「deepseek 这个模型是不是废了、什么时候能再用」。
    ///   而账号列表回答的是**账号维度**（"这个号还好不好"）——
    ///   两个问题不同，同一个号上某个模型废掉、别的模型照样能用是常态。
    ///
    /// ⚠ 判据用 `RateLimits`（历史，成功不清）而不是 `Cooldowns`（瞬时，成功即清）——
    ///   后者在降级救回来之后就什么都不剩了，那正是"显示全绿"的成因。
    ///
    /// @returns 模型 id → 汇总（限流号数 / 总号数 / 最近解除时刻 / 恢复数）
    /// </summary>
    public IReadOnlyDictionary<string, ModelRateLimitSummary> RateLimitSummaryByModel()
    {
        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();

        lock (_gate)
        {
            var acc = new Dictionary<string, (int Hit, int Recovered, long LastReset, bool AnyCooling)>(
                StringComparer.Ordinal);

            foreach (var m in _members)
            {
                foreach (var (model, rl) in m.RateLimits)
                {
                    acc.TryGetValue(model, out var cur);
                    var cooling = rl.IsCoolingAt(nowMs);

                    acc[model] = (
                        Hit: cur.Hit + 1,
                        Recovered: cur.Recovered + (rl.Recovered ? 1 : 0),
                        // 取**最晚**的解除时刻：用户关心的是"什么时候全都好了"，
                        // 而只要还有一个号在冷却，那个号的时间才是瓶颈。
                        LastReset: Math.Max(cur.LastReset, rl.LastResetAtMs),
                        AnyCooling: cur.AnyCooling || cooling);
                }
            }

            return acc.ToDictionary(
                kv => kv.Key,
                kv => new ModelRateLimitSummary(
                    kv.Value.Hit, _members.Count, kv.Value.Recovered,
                    kv.Value.LastReset, kv.Value.AnyCooling));
        }
    }

    /// <summary>
    /// 解冻一个号（余额刷新发现它恢复了）。
    ///
    /// 为什么要有这个：冷冻是为了「别一直撞坏号」，但如果那个号其实
    /// 只是临时抽风，一直冻着就白扔了一个号。所以后台定期查余额，
    /// 查得到就解冻。
    /// </summary>
    public void Unfreeze(string memberId)
    {
        lock (_gate)
        {
            var m = _members.FirstOrDefault(x => x.Id == memberId);
            if (m is null) return;
            m.ConsecutiveFailures = 0;
            m.Cooldowns.Clear();
        }
    }

    /// <summary>清理已经过期的冷却记录（避免词典无限增长）。</summary>
    public int PruneExpired()
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var removed = 0;

        lock (_gate)
        {
            foreach (var m in _members)
            {
                var expired = m.Cooldowns.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
                foreach (var model in expired)
                {
                    m.Cooldowns.Remove(model);
                    removed++;
                }
            }
        }
        return removed;
    }

    /// <summary>这个号对这个模型是不是在冷却里。顺带清掉过期的。</summary>
    private static bool IsCoolingLocked(PoolMember member, string model, long now)
    {
        if (!member.Cooldowns.TryGetValue(model, out var until)) return false;
        if (until <= now)
        {
            member.Cooldowns.Remove(model);
            return false;
        }
        return true;
    }

    // ══ 在途租约（t19）════════════════════════════════════════
    //
    // 上面全部方法**语义一行未动**（t2 立的规矩）。这一节是纯新增：
    // 给「单号在途上限」这层保护，以及它的可观测出口。
    //
    // ## 为什么不是改 Pick 的排序
    //
    // 任务书原本的想法是「让 Pick 优先选 InFlight 最少的」。
    // 但实施时发现那样**不够**，而且是错的：
    //
    // · **不够**：Pick 只负责「挑」，不负责「登记」。
    //   挑完不登记的话，第二个并发请求看到的 InFlight 还是 0，
    //   照样挑同一个号 —— 排序改了个寂寞。
    //
    // · **错**：Pick 是既有公开方法，改它的排序会**动到既有语义**
    //   （它现在的 `ThenBy(SelectionCount)` 已经被 ui 实测证明是有效的：
    //   6 号 / 6 并发时零重叠）。改排序有把那个好行为改坏的风险。
    //
    // 所以做成**新的一层**：`Acquire` = 「Pick + 登记」，`using` 归还。
    // 老的 `Pick` 原样保留、行为不变 —— 不想用上限的调用方（含既有测试）
    // 完全不受影响。

    /// <summary>在途配置。默认 <see cref="InFlightOptions.Default"/>。</summary>
    private InFlightOptions _inFlight = InFlightOptions.Default;

    /// <summary>
    /// 配置在途上限与策略。
    ///
    /// 单独一个方法而不是构造参数：这样子类/测试/网关都能在起好之后调，
    /// 不必改 `AccountPool` 的构造函数签名（那会波及所有现有构造点）。
    /// </summary>
    public void Configure(InFlightOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            _inFlight = options with { MaxPerAccount = Math.Max(1, options.MaxPerAccount) };
        }
    }

    /// <summary>当前在途配置（快照）。</summary>
    public InFlightOptions InFlightOptions
    {
        get
        {
            lock (_gate) return _inFlight;
        }
    }

    /// <summary>
    /// 池子里此刻的在途总数（**可观测出口**）。
    ///
    /// 为什么必须有这个：漏归还的后果是「可用容量慢性缩到零」，
    /// 而**没有任何地方会报错**。有了这个数字，测试可以断言
    /// 「异常/取消路径下它回到 0」，运维也能看出来池子在漏。
    /// </summary>
    public int TotalInFlight
    {
        get
        {
            lock (_gate) return _members.Sum(m => m.InFlight);
        }
    }

    /// <summary>
    /// 取一个号并**登记在途**，返回一个 `using` 就能归还的租约。
    ///
    /// ## 选号规则（在 <see cref="Pick"/> 的规则之上加一层）
    ///
    /// ```
    /// 1. 排除冷却中的        （同 Pick）
    /// 2. 排除冷冻的          （同 Pick）
    /// 3. 排除 exclude 里的   （同 Pick）
    /// 4. 【新】排除已达在途上限的（Policy=Reject 时）
    /// 5. 排序：优先快过期的 → 其次在途少的 → 再次历史选得少的
    ///           ↑ 新        ↑ 新（这是「当下分流」的关键）
    /// ```
    ///
    /// **第 5 条的次序**：在途少的排在历史选得少的前面 ——
    /// 「谁现在最闲」优先于「谁历史上被选得最少」。
    /// 因为历史不均可以慢慢追平（下一轮就轮到了），
    /// 而瞬时挤爆会**当场**触发上游限流。
    ///
    /// @returns 租约；没有可用号（含全部达上限且策略为 Reject）时返回 null。
    ///          返回的租约**必须** `using`（或显式 `Dispose`）——
    ///          漏了就把那个号永久占了。
    /// </summary>
    public InFlightLease? Acquire(string model, IReadOnlyCollection<string>? exclude = null)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var skip = exclude ?? [];

        lock (_gate)
        {
            var options = _inFlight;

            var candidates = _members
                .Where(m => !skip.Contains(m.Id))
                .Where(m => !IsCoolingLocked(m, model, now))
                .Where(m => m.ConsecutiveFailures < FreezeThreshold)
                .Where(m => options.Policy == InFlightPolicy.AllowExcess
                            || m.InFlight < options.MaxPerAccount)
                .ToList();

            if (candidates.Count == 0) return null;

            var picked = candidates
                // ① **主键：谁现在最闲** —— 这一条必须是主键，不能是次键。
                //
                // ⚠ 实测教训（e2e 的 t61，真机）：这里原本主键是 ExpiresAtMs、
                //    InFlight（当时还没有）与 SelectionCount 只是次键。
                //    19 个号的过期时刻**各不相同**、且主号最小 ⇒ 它永远排第一，
                //    **次键永远轮不到生效** ⇒ 6 个并发请求全落同一个号。
                //
                //    所以"把负载加进排序"是不够的 —— 只要它排在某个
                //    「各成员互不相同」的键之后，就等于没加。
                .OrderBy(m => m.InFlight)
                // ② 历史：谁被选得最少（防长期偏袒某个号）
                .ThenBy(m => m.SelectionCount)
                // ③ 快过期的优先用掉（保留原意图，但它只是平局裁决）
                .ThenBy(m => m.Entry.ExpiresAtMs == 0 ? long.MaxValue : m.Entry.ExpiresAtMs)
                .First();

            // ⚠ 「挑」和「登记」必须在**同一个锁内**完成。
            // 分两步（挑 → 出锁 → 登记）的话，两个并发请求会同时
            // 看到同一个 InFlight=0 的号，双双挑中它 —— 那正是要防的事。
            picked.InFlight++;
            picked.SelectionCount++;
            picked.LastUsedAtMs = now;

            return new InFlightLease(this, picked, model);
        }
    }

    /// <summary>
    /// 归还一个在途名额。由 <see cref="InFlightLease.Dispose"/> 调，
    /// **一般不该直接调**（直接调就绕开了「租约成对」的结构保证）。
    ///
    /// 幂等由租约那边保证；这里再夹一层下限（防负数）——
    /// 计数变成负数会让上限**永远不触发**（负数永远小于上限），
    /// 那是比漏减更糟的失效方式：静默地把保护关掉。
    /// </summary>
    internal void Release(string memberId, string model)
    {
        _ = model;   // 目前只减计数；留着参数是为了将来按模型分在途

        lock (_gate)
        {
            var m = _members.FirstOrDefault(x => x.Id == memberId);
            if (m is null) return;   // 并发下刚被 Replace 掉的窗口期

            if (m.InFlight > 0) m.InFlight--;
        }
    }

    /// <summary>
    /// 选一个「**至少还有一个可用模型**」的号并登记在途。
    ///
    /// ## 为什么不用 <see cref="Acquire"/>
    ///
    /// `Acquire(model)` 只认「该号的**这个模型**没冷却」——
    /// 于是「hy3 被限流」时它直接跳过那个号，即使那个号的 hy4 还能用。
    /// 这会让网关的「换模型」降级链（内层换模型比换号便宜）根本没机会跑。
    ///
    /// 所以这个方法与 <see cref="Acquire"/> 的区别只在候选判定：
    /// 不看「指定模型是否可用」，只看「这个号**有没有任意**可用模型」
    /// （具体用哪个模型由调用方内层决定）。
    ///
    /// 其余全部一致：排除冷冻、排除 exclude、达在途上限则拒绝、
    /// 负载排序（在途少优先）、同锁内登记。
    /// </summary>
    public InFlightLease? AcquireAnyUsableModel(
        string currentModel,
        Func<PoolMember, string, bool> canUse,
        IReadOnlyCollection<string>? exclude = null)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var skip = exclude ?? [];

        lock (_gate)
        {
            var options = _inFlight;

            var candidates = _members
                .Where(m => !skip.Contains(m.Id))
                .Where(m => m.ConsecutiveFailures < FreezeThreshold)
                .Where(m => options.Policy == InFlightPolicy.AllowExcess
                            || m.InFlight < options.MaxPerAccount)
                .Where(m => canUse(m, currentModel))
                .ToList();

            if (candidates.Count == 0) return null;

            var picked = candidates
                .OrderBy(m => m.InFlight)
                .ThenBy(m => m.SelectionCount)
                .ThenBy(m => m.Entry.ExpiresAtMs == 0 ? long.MaxValue : m.Entry.ExpiresAtMs)
                .First();

            // 同 Acquire：挑和登记必须同一个锁内完成。
            picked.InFlight++;
            picked.SelectionCount++;
            picked.LastUsedAtMs = now;

            return new InFlightLease(this, picked, currentModel);
        }
    }

    /// <summary>
    /// 对**已经选好的号**登记在途（亲和路径 / 别处选完号后补登记用）。
    ///
    /// 与 <see cref="Acquire"/> 的区别：不选号、不排序，只登记。
    /// 用在「号是粘性/别的原因选定的」场景 —— 选号逻辑已经定死了，
    /// 但并发保护（在途上限）仍然要有。
    ///
    /// @returns 租约；号不在池里（并发下刚被 Replace）时返回 null。
    ///          返回的租约**必须** `using` 归还。
    /// </summary>
    public InFlightLease? Lease(PoolMember member, string model)
    {
        lock (_gate)
        {
            var m = _members.FirstOrDefault(x => x.Id == member.Id);
            if (m is null) return null;

            var options = _inFlight;

            // 达上限且策略是 Reject：不能登记 —— 调用方要换号/换模型。
            if (options.Policy == InFlightPolicy.Reject && m.InFlight >= options.MaxPerAccount)
            {
                return null;
            }

            m.InFlight++;
            m.SelectionCount++;
            m.LastUsedAtMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();

            return new InFlightLease(this, m, model);
        }
    }

    /// <summary>
    /// 在途快照（每个号一行）—— 给 `/metrics` 或管理面用的可观测出口。
    ///
    /// @returns (号 id, 标签, 当前在途, 是否冷冻)。按在途降序，最忙的在前。
    /// </summary>
    public IReadOnlyList<(string MemberId, string Label, int InFlight, bool Frozen)> InFlightSnapshot()
    {
        lock (_gate)
        {
            return _members
                .OrderByDescending(m => m.InFlight)
                .Select(m => (m.Id, m.Label, m.InFlight, m.ConsecutiveFailures >= FreezeThreshold))
                .ToArray();
        }
    }

    /// <summary>
    /// 连续失败快照（每个号一行）—— **为什么单独加这个出口**。
    ///
    /// ## 它是 t79 缺陷的"可见性"补丁
    ///
    /// 真机那次事故里，8 个号全被冻掉，而**界面上完全看不出来** ——
    /// 只看到 429「没有可用账号」。`InFlightSnapshot` 只给 `Frozen` 布尔，
    /// 看不出"离冻还有几次"，也看不出失败是怎么涨上去的。
    ///
    /// 有了连续失败计数，排障时能直接问：
    /// ```
    /// 所有号的 ConsecutiveFailures 一起在涨  → 环境问题（代理/DNS/网络）
    /// 某一个号在涨，其余是 0                → 那个号自己的问题
    /// ```
    /// **这两种情况在原来只有 Frozen 布尔的出口上是分不开的** ——
    /// 而它们的处置完全相反（查网络 vs 换号）。
    ///
    /// @returns (号 id, 标签, 连续失败数, 是否冷冻)。按失败数降序（问题号在前）。
    /// </summary>
    public IReadOnlyList<(string MemberId, string Label, int ConsecutiveFailures, bool Frozen)> FailureSnapshot()
    {
        lock (_gate)
        {
            return _members
                .OrderByDescending(m => m.ConsecutiveFailures)
                .Select(m => (m.Id, m.Label, m.ConsecutiveFailures, m.ConsecutiveFailures >= FreezeThreshold))
                .ToArray();
        }
    }
}
