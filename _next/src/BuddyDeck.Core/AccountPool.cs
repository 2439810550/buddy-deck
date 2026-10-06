using Microsoft.Extensions.Logging;

namespace BuddyDeck.Core;

/// <summary>一个账号在池里的运行时状态。</summary>
public sealed class PoolMember
{
    public required VaultEntry Entry { get; init; }

    /// <summary>这个账号各个模型的冷却截止时刻（Unix 毫秒）。</summary>
    public Dictionary<string, long> Cooldowns { get; } = new(StringComparer.Ordinal);

    /// <summary>连续失败次数。成功一次就清零。</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>被选中过多少次（用于公平轮询）。</summary>
    public long SelectionCount { get; set; }

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

    /// <summary>连续失败到多少次就整体冷冻这个号。</summary>
    private const int FreezeThreshold = 5;

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

    public AccountPool(TimeProvider clock, ILogger<AccountPool> log)
    {
        _clock = clock;
        _log = log;
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
            var byId = _members.ToDictionary(m => m.Id, StringComparer.Ordinal);
            _members.Clear();

            foreach (var entry in entries)
            {
                // 沿用旧状态 —— 不能因为刷新了账号列表就把冷却清掉，
                // 那样被限流的号会立刻被再次选中，又撞一次 429。
                if (byId.TryGetValue(entry.Id, out var old))
                {
                    foreach (var (model, until) in old.Cooldowns) old.Cooldowns[model] = until;
                    old.ConsecutiveFailures = old.ConsecutiveFailures;
                    _members.Add(old);
                }
                else
                {
                    _members.Add(new PoolMember { Entry = entry });
                }
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

            // 优先快过期的，其次用得最少的
            var picked = candidates
                .OrderBy(m => m.Entry.ExpiresAtMs == 0 ? long.MaxValue : m.Entry.ExpiresAtMs)
                .ThenBy(m => m.SelectionCount)
                .First();

            picked.SelectionCount++;
            picked.LastUsedAtMs = now;
            return picked;
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
        }
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
}
