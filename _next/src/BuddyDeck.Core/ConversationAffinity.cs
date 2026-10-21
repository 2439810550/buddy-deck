using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Core;

/// <summary>
/// 会话粘性：让多轮对话**一直落在同一个账号**上。
///
/// ## 它解决什么问题
///
/// 账号池是公平轮询的（<see cref="AccountPool.Pick"/> 优先挑用得最少的号）。
/// 这对单次请求是好事，对多轮对话是灾难：
///
/// ```
/// 第 1 轮  → 号 A   （上游在 A 上建了上下文）
/// 第 2 轮  → 号 B   （上游在 B 上是全新的会话 —— 上下文全丢）
/// 第 3 轮  → 号 C   （又从头开始）
/// ```
///
/// 用户看到的现象是「聊到一半突然失忆」，而且**偶发**（取决于轮询走到了哪个号），
/// 极难归因。所以要在池之上加一层亲和表：同一个会话优先还给上次那个号。
///
/// ## 会话键怎么来的
///
/// 上游的协议里有一个 `conversation_id` 可以用，但它**不是每个客户端都发**
/// （第三方 OpenAI 兼容客户端根本不知道有这个东西）。所以：
///
/// ```
/// 客户端发了 conversation_id  → 用它（加 d- 前缀），最准确
/// 没发                       → 用 system 消息 + 首条 user 消息的内容派生
/// ```
///
/// 为什么是这两个字段的组合：
///   · **system** 是会话的「人格/系统提示」，同一个会话里它是不变的
///   · **首条 user** 是会话的起点，多轮之后它还在 messages[0/1] 里没被丢掉
///   · 后续轮次新增的 assistant/user 消息**不参与** —— 参与了的话
///     每加一轮哈希就变一次，等于没有粘性
///
/// 前缀 `d-`（derived，派生）和旧版网关保持一致 —— 那样两边的日志/账本
/// 能对得上，一眼就看得出「这个键是算出来的，不是客户端给的」。
///
/// ## 亲和表的淘汰
///
/// 一条映射只能活 <see cref="AffinityOptions.TimeToLive"/>（默认 30 分钟）。
/// 会话结束之后不清理的话，表会跟着用户量无限长 —— 这是长跑服务里
/// 最典型的内存泄漏形状。
///
/// 另外：账号被上游拒了（冷却/失效）的时候**必须主动淘汰**映射，
/// 不然下次还会把请求塞给那个已经用不了的号，直到 TTL 到期为止。
/// </summary>
public static class ConversationKey
{
    /// <summary>
    /// 派生键的前缀。
    ///
    /// `d-` = derived。和旧版网关一致 —— 日志里一眼能区分
    /// 「客户端给的会话 id」和「我们算出来的」。
    /// </summary>
    public const string DerivedPrefix = "d-";

    /// <summary>客户端显式给的会话 id 用的前缀（也带 d-，见 <see cref="FromProductId"/>）。</summary>
    private const int HashLength = 32;

    /// <summary>
    /// 从一条请求体算出会话键。
    ///
    /// 三种来源，优先级从高到低：
    ///   1. `conversation_id` / `conversationId`（客户端显式给的，最准）
    ///   2. `metadata.conversation_id`（OpenAI 客户端常见的挂载点）
    ///   3. **派生**：`system 消息` + `首条 user 消息` 的内容哈希
    ///
    /// 一个都给不出来（没有 messages、也没有会话 id）时返回 null ——
    /// 那种情况没有「同一个会话」可言，调用方应该老老实实走池的轮询。
    /// </summary>
    /// <param name="requestBody">客户端发来的原始请求体 JSON 文本。</param>
    /// <returns>会话键（总是带 <see cref="DerivedPrefix"/>）；算不出来时 null。</returns>
    public static string? Derive(string? requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody)) return null;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(requestBody);
        }
        catch (JsonException)
        {
            // 请求体不是合法 JSON：走池的默认轮询，别在这里抛
            return null;
        }

        if (node is not JsonObject body) return null;

        // ① 客户端显式给的会话 id —— 有就直接用
        var explicitId = ReadExplicitId(body);
        if (!string.IsNullOrEmpty(explicitId)) return FromProductId(explicitId);

        // ② 派生
        return DeriveFromMessages(body);
    }

    /// <summary>
    /// 把一个已经拿到的会话 id 规范成带 `d-` 前缀的键。
    ///
    /// 已经是 `d-` 开头就原样返回 —— 幂等，重复调用不会变成 `d-d-xxx`。
    /// </summary>
    public static string FromProductId(string id)
    {
        var trimmed = id.Trim();
        return trimmed.StartsWith(DerivedPrefix, StringComparison.Ordinal)
            ? trimmed
            : DerivedPrefix + trimmed;
    }

    /// <summary>
    /// 从 messages 里取 `system` 与**首条** `user`，拼起来做哈希。
    ///
    /// ## 为什么 system 取「全部」而不是「第一条」
    ///
    /// OpenAI 规范里 system 可以有多条（尤其是 `developer` 角色归一成
    /// `system` 之后，历史消息里可能混着好几条）。全部按顺序拼进去
    /// 才是这个会话完整的人格定义 —— 只取第一条的话，两条内容不同
    /// 但首条相同的会话会撞到同一个键上，两个会话就串号了。
    ///
    /// ## 为什么 user 只取「首条」
    ///
    /// 见类型注释：取全部的话每加一轮哈希就变，粘性直接失效。
    ///
    /// 内容可以是字符串，也可以是 OpenAI 的多模态数组
    /// （`[{"type":"text","text":"..."}]`）—— 两种都要认，不然带图片的
    /// 客户端永远命中不了粘性。数组里只取 text 部分，**不把 base64
    /// 图片数据算进哈希**：那是几 MB 的负载，算哈希既慢又没必要。
    /// </summary>
    private static string? DeriveFromMessages(JsonObject body)
    {
        if (body["messages"] is not JsonArray messages) return null;

        var system = new StringBuilder();
        string? firstUser = null;

        foreach (var message in messages)
        {
            if (message is not JsonObject obj) continue;

            var role = obj["role"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : "";
            var content = ExtractText(obj["content"]);

            if (string.IsNullOrEmpty(content)) continue;

            switch (role)
            {
                case "system":
                case "developer":
                    // 用分隔符连接，避免 ["ab"]["c"] 和 ["a"]["bc"] 撞键
                    system.Append('\u0000').Append(content);
                    break;

                case "user" when firstUser is null:
                    firstUser = content;
                    break;

                default:
                    // assistant / tool / 后续的 user 一律不参与
                    break;
            }
        }

        if (system.Length == 0 && firstUser is null) return null;

        // system 和 user 之间也要有分隔符（同上，防拼接歧义）
        var material = system.Append('\u0000').Append(firstUser ?? "").ToString();
        return FromProductId(Hash(material));
    }

    /// <summary>
    /// 从 `content` 字段里抠出纯文本。
    ///
    /// 认三种形态：
    ///   · 字符串           —— 最常见
    ///   · 数组（多模态）    —— 只取 type=text / input_text 的 text 字段
    ///   · 对象 `{text:""}` —— 少数客户端的形态
    /// </summary>
    private static string? ExtractText(JsonNode? content)
    {
        switch (content)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text;

            case JsonObject obj when obj["text"] is JsonValue tv && tv.TryGetValue<string>(out var single):
                return single;

            case JsonArray parts:
                var sb = new StringBuilder();
                foreach (var part in parts)
                {
                    if (part is not JsonObject po) continue;

                    // ⚠ 只认文本部件。image_url 的 data URI 有几 MB ——
                    // 它也算进哈希的话，每次请求光算哈希就要几十毫秒，
                    // 而且它对「这是哪个会话」没有任何区分意义。
                    var type = po["type"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : "";
                    if (type is not ("text" or "input_text")) continue;

                    if (po["text"] is JsonValue pv && pv.TryGetValue<string>(out var partText))
                    {
                        sb.Append(partText);
                    }
                }
                return sb.Length == 0 ? null : sb.ToString();

            default:
                return null;
        }
    }

    /// <summary>显式会话 id 的三种挂载点。</summary>
    private static string? ReadExplicitId(JsonObject body)
    {
        foreach (var key in (string[])["conversation_id", "conversationId"])
        {
            if (body[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }

        if (body["metadata"] is JsonObject meta &&
            meta["conversation_id"] is JsonValue mv &&
            mv.TryGetValue<string>(out var ms) &&
            !string.IsNullOrWhiteSpace(ms))
        {
            return ms;
        }

        return null;
    }

    /// <summary>
    /// SHA-256 前 16 字节 → 32 位小写 hex。
    ///
    /// 为什么用密码学哈希而不是 `string.GetHashCode`：
    ///   ① `GetHashCode` 的实现在 .NET 各版本之间**不保证稳定** ——
    ///      升级运行时之后所有会话键会一起变，用户全部掉粘性
    ///   ② 它的输出短（32 位），会话一多就撞键 —— 撞键 = 两个会话串号，
    ///      比没有粘性更糟（用户会看到别人的上下文）
    /// </summary>
    private static string Hash(string material)
    {
        var bytes = Encoding.UTF8.GetBytes(material);

        // 只在栈上算 —— 不分配中间数组
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);

        // 取前 16 字节（128 位）够了：撞键概率约 2^-64，比中彩票低得多，
        // 而键短一半，日志里好读。
        //
        // 为什么不用 Convert.ToHexStringLower：那是 .NET 9 才有的 API，
        // 本项目 target net8.0（见 Directory.Build.props）。ToHexString 在
        // net8 上是**大写**的，所以显式转小写 —— 会话键会进日志和账本，
        // 大小写不一致会让同一段对话在日志里看起来是两个会话。
        return Convert.ToHexString(digest[..16]).ToLowerInvariant();
    }
}

/// <summary>亲和表的参数。</summary>
public sealed record AffinityOptions
{
    /// <summary>
    /// 一条映射活多久。默认 30 分钟。
    ///
    /// 为什么是这个量级：多轮对话里用户读一段回答、想一会儿再问下一句，
    /// 十几分钟很常见；半小时足够覆盖一轮完整对话，又不会让表无限长。
    /// </summary>
    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 表最多存多少条。默认 4096。
    ///
    /// 满了之后淘汰**最久没用过**的那条 —— 不是全清、也不是拒绝新写入。
    /// 全清会让所有活跃会话同时掉粘性（一次尖峰）；拒绝写入会让
    /// 新会话永远粘不住。
    /// </summary>
    public int Capacity { get; init; } = 4096;
}

/// <summary>
/// 会话 → 账号的亲和表。
///
/// ## 用法
///
/// ```
/// var key = ConversationKey.Derive(rawBody);
/// var member = affinity.Select(key, model, exclude)   // 命中就直接给，没命中才走池
///              ?? pool.Pick(model, exclude);
/// affinity.Remember(key, member.Id);                   // 记下来
/// ```
///
/// <see cref="Select"/> 把「查表、命中，否则选号、然后记下来」合成一步 ——
/// 分开写的话调用方很容易漏掉 <see cref="Remember"/>，漏了的症状就是
/// 粘性静默失效（不报错、只是偶发跳号）。
///
/// ## 线程安全
///
/// 网关是并发的：同一个会话的两轮请求完全可能同时在飞。
/// 所有读写都在 <see cref="_gate"/> 下完成，读侧给的是**值快照**，
/// 不会读到改写中的状态。
/// </summary>
public sealed partial class ConversationAffinity
{
    private readonly Dictionary<string, AffinityEntry> _map = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly AccountPool _pool;
    private readonly TimeProvider _clock;
    private readonly AffinityOptions _options;
    private readonly ILogger<ConversationAffinity> _log;

    /// <summary>淘汰计数 —— 测试和诊断要看「到底清没清」。</summary>
    private long _evictions;

    private sealed record AffinityEntry(string MemberId, string Model, long LastSeenMs);

    public ConversationAffinity(
        AccountPool pool,
        TimeProvider clock,
        ILogger<ConversationAffinity> log,
        AffinityOptions? options = null)
    {
        _pool = pool;
        _clock = clock;
        _log = log;
        _options = options ?? new AffinityOptions();
    }

    /// <summary>表里现在有多少条映射（**只算还没过期的**）。</summary>
    public int Count
    {
        get
        {
            var now = Now();
            lock (_gate) return _map.Count(kv => !Expired(kv.Value, now));
        }
    }

    /// <summary>被淘汰过多少条（过期 + 容量驱逐 + 显式吊销）。</summary>
    public long Evictions => Interlocked.Read(ref _evictions);

    /// <summary>
    /// 挑号：先查亲和表，命中就还给它那个号；没命中才走池。
    ///
    /// **命中但那个号已经不能用了**（冷却/冷冻/被 exclude 掉）时，
    /// 落到池上重新选 —— 而且会把这条死映射**当场淘汰**，
    /// 免得下一轮又白查一次。
    ///
    /// @param key     <see cref="ConversationKey.Derive"/> 的结果。null = 没有会话概念，直接走池。
    /// @param model   要用的模型（冷却按模型记，所以查表也要带模型）
    /// @param exclude 点名不要的号（刚失败过的）。命中被排除的号视为未命中。
    /// </summary>
    public PoolMember? Select(string? key, string model, IReadOnlyCollection<string>? exclude = null)
    {
        var member = SelectInternal(key, model, exclude);
        if (member is not null) Remember(key, member.Id, model);
        return member;
    }

    private PoolMember? SelectInternal(string? key, string model, IReadOnlyCollection<string>? exclude)
    {
        if (string.IsNullOrEmpty(key)) return _pool.Pick(model, exclude);

        var now = Now();
        string? pinned = null;

        lock (_gate)
        {
            if (_map.TryGetValue(key, out var entry))
            {
                if (Expired(entry, now))
                {
                    // 过期了就当场清掉，不要留在表里等下次批量清理
                    _map.Remove(key);
                    Interlocked.Increment(ref _evictions);
                }
                else
                {
                    entry = entry with { LastSeenMs = now };
                    _map[key] = entry;
                    pinned = entry.MemberId;
                }
            }
        }

        if (pinned is null) return _pool.Pick(model, exclude);

        // ⚠ 亲和表只记「上次是谁」，**不保证那个号现在还能用** ——
        // 它可能已经进了冷却、或者连续失败被冷冻、或者刚好在 exclude 里。
        //
        // 先做一次**只读**探测（CanPick，不动任何计数），再决定：
        //   · pinned 还能用 → 就用它（这才叫粘性）
        //   · pinned 不能用了 → 淘汰映射，老老实实走 Pick
        //
        // 为什么不能用 Pick 的结果做这个判断：Pick 会推进它选中那个号的
        // SelectionCount。如果最后发现 pinned 更好、把 Pick 的结果丢掉，
        // 那个号就白背了一次计数 —— 负载会慢慢歪掉。
        if (_pool.CanPick(pinned, model, exclude))
        {
            var specific = _pool.SelectSpecific(pinned, model);
            if (specific is not null) return specific;

            // 极窄的竞态窗口：CanPick 和 SelectSpecific 之间那个号被
            // Replace 掉了。退化到 Pick，不能让请求卡在这里。
        }

        // pinned 已经不可用 —— 当场淘汰死映射，别留到下一轮再白查一次
        Revoke(key);
        return _pool.Pick(model, exclude);
    }

    /// <summary>
    /// 记下「这个会话现在落在哪个号上」。
    ///
    /// 同一个会话重复调用没问题（更新一下时间戳就行）——
    /// 换号重试之后会调到它，把映射改到新号上。
    /// </summary>
    public void Remember(string? key, string memberId, string model)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(memberId)) return;

        var now = Now();

        lock (_gate)
        {
            _map[key] = new AffinityEntry(memberId, model, now);

            if (_map.Count > _options.Capacity) EvictLocked(now);
        }
    }

    /// <summary>
    /// 淘汰一个会话的映射。
    ///
    /// **上游说这个号不能用了（冷却/失效）时必须调它** —— 不调的话，
    /// 下一轮还会把请求塞给同一个坏号，一直撞到 TTL 到期为止。
    ///
    /// 幂等：不存在的键调用它不抛异常（并发下两个请求同时吊销同一个键
    /// 是很常见的，不能因此炸掉）。
    /// </summary>
    public bool Revoke(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;

        lock (_gate)
        {
            if (!_map.Remove(key)) return false;
            Interlocked.Increment(ref _evictions);
            return true;
        }
    }

    /// <summary>
    /// 把落在某个账号上的**全部**会话映射都吊销掉。
    ///
    /// 用在「这个号整个不能用了」的场合（连续失败被冷冻）。
    /// 按会话逐个吊销的话，得先把表遍历一遍才知道哪些会话在它上面 ——
    /// 与其让调用方自己遍历，不如在这里一次做完。
    ///
    /// @returns 吊销了几条。
    /// </summary>
    public int RevokeAccount(string memberId)
    {
        if (string.IsNullOrEmpty(memberId)) return 0;

        lock (_gate)
        {
            var keys = _map.Where(kv => kv.Value.MemberId == memberId).Select(kv => kv.Key).ToList();
            foreach (var key in keys) _map.Remove(key);

            if (keys.Count > 0) Interlocked.Add(ref _evictions, keys.Count);
            return keys.Count;
        }
    }

    /// <summary>看看某个会话现在粘在哪个号上（诊断/测试用）。</summary>
    public string? Peek(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;

        var now = Now();
        lock (_gate)
        {
            return _map.TryGetValue(key, out var entry) && !Expired(entry, now) ? entry.MemberId : null;
        }
    }

    /// <summary>清掉所有过期映射（后台定时调，避免表被不活跃会话撑满）。</summary>
    public int PruneExpired()
    {
        var now = Now();

        lock (_gate)
        {
            var expired = _map.Where(kv => Expired(kv.Value, now)).Select(kv => kv.Key).ToList();
            foreach (var key in expired) _map.Remove(key);

            if (expired.Count > 0) Interlocked.Add(ref _evictions, expired.Count);
            return expired.Count;
        }
    }

    /// <summary>全部清掉（切池、换保险库时用）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Interlocked.Add(ref _evictions, _map.Count);
            _map.Clear();
        }
    }

    private bool Expired(AffinityEntry entry, long now) => now - entry.LastSeenMs > (long)_options.TimeToLive.TotalMilliseconds;

    private long Now() => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>
    /// 容量驱逐：淘汰**最久没用过**的那批。
    ///
    /// 为什么先按过期筛、再按 LastSeen 排序：过期的那些本来就不该占位，
    /// 优先扔掉它们，活跃会话一条都不动。
    /// </summary>
    private void EvictLocked(long now)
    {
        var target = Math.Max(1, _options.Capacity / 8);   // 一次扔 1/8，避免每写一条就驱逐一次

        var victims = _map
            .Where(kv => Expired(kv.Value, now))
            .Select(kv => kv.Key)
            .ToList();

        if (victims.Count < target)
        {
            // 过期的还不够多 —— 补上最久没用的（不含已经选出来的）
            var chosen = new HashSet<string>(victims, StringComparer.Ordinal);
            victims.AddRange(_map
                .Where(kv => !chosen.Contains(kv.Key))
                .OrderBy(kv => kv.Value.LastSeenMs)
                .Take(target - victims.Count)
                .Select(kv => kv.Key));
        }

        foreach (var key in victims) _map.Remove(key);
        if (victims.Count > 0) Interlocked.Add(ref _evictions, victims.Count);

        if (_log.IsEnabled(LogLevel.Debug) && victims.Count > 0)
        {
            // ⚠ CA1873：不要往实参位置塞计算 —— 这里全是局部变量
            var removed = victims.Count;
            var size = _map.Count;
            LogEvicted(removed, size);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "会话亲和表驱逐 {Removed} 条，现存 {Size} 条")]
    private partial void LogEvicted(int removed, int size);
}
