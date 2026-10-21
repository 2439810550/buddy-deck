using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BuddyDeck.Core;

/// <summary>
/// 账本里的一条成本账目。
///
/// ## 它和 <c>RequestRecord</c>（Gateway/RequestLog.cs）的区别
///
/// 这是**成本账本**，不是请求日志。两者记的是同一批请求，但问的是
/// 完全不同的问题：
///
/// ```
/// RequestLog / RequestRecord       「刚才那几次请求怎么样了？」
///   · 环形缓冲（内存，定长，会被覆盖）
///   · 目的：界面上显示「最近请求」列表
///   · 关心：状态码、耗时、outcome —— 给**当下**排障用
///
/// UsageLedgerRecord（本类型）       「我这个月到底烧了多少额度？」
///   · 追加落盘（JSONL，永不覆盖）
///   · 目的：算总量、算钱、按模型/账号分摊
///   · 关心：token 数、**credit（上游实际扣费）**、缓存命中 —— 给**回头看账**用
/// ```
///
/// **两者不能互相替代**（docs/feature-parity.md §2.4 已核实）：
/// `RequestLog` 是定长环形缓冲 —— 存满就把最老的挤掉，用它算总量
/// 得到的是「最近 500 次」而不是「总共」；它也不落盘，进程重启即清零。
/// 反过来，账本没有状态码/错误分类那些排障字段，也绝不该每来一个请求
/// 就把它整份读出来给界面刷一次。
///
/// ## 为什么 credit 是核心
///
/// 上游的 usage 里带一个 `credit`，是**这次请求实际扣了多少积分**。
/// 它已经把输入价、输出价、缓存折扣全算好了 —— 实测差异极大：
///
/// ```
/// 第一次（全 miss，2541 prompt）   credit 0.22
/// 第二次（命中 2304，miss 237）    credit 0.03
///                                   ↑ 命中 91%，只花 14%
/// ```
///
/// 不记它，就永远不知道每笔请求是赚还是亏。所以这个字段是账本存在的理由，
/// 不是「顺便多记一个数」。
///
/// ⚠ 上游把 `credit` 和缓存字段塞在 usage 里一起下发，而
/// <c>SseSanitizer.SanitizeUsage</c> 会把它们从**发给客户端的那份**里删掉
/// （客户端只认标准 OpenAI 三字段）。它同时 DeepClone 了一份原值给记账用 ——
/// 记账必须拿那份拷贝，拿被裁过的就没有 credit 了（旧版踩过这个坑，
/// 见 SseSanitizer.cs:134 的注释）。
/// </summary>
public sealed record UsageLedgerRecord
{
    /// <summary>账目发生时刻（UTC）。JSONL 里是 ISO 8601 字符串，人可读。</summary>
    [JsonPropertyName("iso")]
    public required string Iso { get; init; }

    /// <summary>同上，Unix 毫秒。给程序排序/分桶用（字符串排序在跨时区时不可靠）。</summary>
    [JsonPropertyName("ts")]
    public required long Ts { get; init; }

    /// <summary>客户端要的模型名。</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    /// <summary>实际用的模型（被限流换过号/换过模型时和 <see cref="Model"/> 不同）。</summary>
    [JsonPropertyName("usedModel")]
    public string? UsedModel { get; init; }

    /// <summary>用的哪个账号（脱敏短标签，**不是 token、不是完整 uid**）。</summary>
    [JsonPropertyName("account")]
    public string? Account { get; init; }

    /// <summary>请求类型：chat / images / …（对齐旧版 <c>kind</c>）。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "chat";

    /// <summary>ok / failed / cancelled。</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = "ok";

    /// <summary>这次请求花了多久（毫秒）。</summary>
    [JsonPropertyName("elapsedMs")]
    public long? ElapsedMs { get; init; }

    /// <summary>尝试了几次（含首次）。被限流重试过的请求这个数会大于 1。</summary>
    [JsonPropertyName("attempts")]
    public int? Attempts { get; init; }

    /// <summary>输入 token 数。</summary>
    [JsonPropertyName("promptTokens")]
    public long? PromptTokens { get; init; }

    /// <summary>输出 token 数。</summary>
    [JsonPropertyName("completionTokens")]
    public long? CompletionTokens { get; init; }

    /// <summary>总 token 数（上游给的那个；没给时由前两者相加推）。</summary>
    [JsonPropertyName("totalTokens")]
    public long? TotalTokens { get; init; }

    /// <summary>
    /// **上游实际扣费**。账本存在的理由，见类型注释。
    ///
    /// 用 <c>double</c> 而不是 <c>decimal</c>：上游给的 credit 本身是
    /// 浮点数（0.22 / 0.03），用 decimal 反序列化还得处理转换，而且
    /// 聚合求和时 double 的误差远小于 credit 本身的分辨率。JSONL 里
    /// 用 <c>G17</c> 往返格式写，读回来和写出去完全相等。
    /// </summary>
    [JsonPropertyName("credit")]
    public double? Credit { get; init; }

    /// <summary>缓存命中 token 数。命中率高 → 实际成本远低于标称价。</summary>
    [JsonPropertyName("cacheHit")]
    public long? CacheHit { get; init; }

    /// <summary>缓存未命中 token 数。</summary>
    [JsonPropertyName("cacheMiss")]
    public long? CacheMiss { get; init; }

    /// <summary>从缓存读的 token 数（另一种上游口径）。</summary>
    [JsonPropertyName("cacheRead")]
    public long? CacheRead { get; init; }

    /// <summary>写进缓存的 token 数。</summary>
    [JsonPropertyName("cacheWrite")]
    public long? CacheWrite { get; init; }

    /// <summary>
    /// 思考型模型的内部推理 token。
    ///
    /// 单独记的原因：这类 token **可能不计入 completion**，
    /// 只看 completion 会低估这类模型的真实开销。
    /// </summary>
    [JsonPropertyName("reasoning")]
    public long? Reasoning { get; init; }

    /// <summary>这次请求是不是成功的（<see cref="Outcome"/> 的便捷视图）。</summary>
    [JsonIgnore]
    public bool Ok => string.Equals(Outcome, "ok", StringComparison.Ordinal);

    /// <summary>总 token（上游没给就自己加）。</summary>
    [JsonIgnore]
    public long EffectiveTotalTokens => TotalTokens ?? (PromptTokens ?? 0) + (CompletionTokens ?? 0);

    /// <summary>缓存相关的总 token（算命中率用）。</summary>
    [JsonIgnore]
    public long CacheTotalTokens => (CacheHit ?? 0) + (CacheMiss ?? 0);

    /// <summary>序列化成一行 JSON（**不含换行**，调用方自己加）。</summary>
    public string ToLine() => JsonSerializer.Serialize(this, LedgerJson.WriteOptions);

    /// <summary>JSONL 文件写读共用的序列化选项。</summary>
    internal static class LedgerJson
    {
        /// <summary>
        /// 写：不缩进（一行一条）、中文不转义、null 不写出来。
        ///
        /// 为什么 null 不写出来：旧版的账本就是这个形状，
        /// 而且省掉一堆 <c>"cacheRead":null</c> 让文件小得多 ——
        /// 这个文件是要长年累月追加的。
        /// </summary>
        internal static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>
        /// 读：宽容。
        ///
        /// 账本是**外部可编辑**的文件（用户会拿 jq/awk/Python 去分析它，
        /// 甚至手工改）。所以读的时候允许尾逗号、注释、大小写不敏感 ——
        /// 手改坏一点不该让整个账本读不出来。
        ///
        /// ⚠ 但**数字类型不宽容**：`credit` 必须是数字。若有人把
        /// credit 写成字符串 "0.22"，那条**整行**会被跳过并记警告 ——
        /// 静默当成 0 会把账算错，那比跳过它危险得多。
        /// </summary>
        internal static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = JsonNumberHandling.Strict,
        };
    }
}

/// <summary>
/// 从上游 usage 对象里抽出该记的字段 —— 对齐旧版 <c>summarizeUsage</c>。
///
/// ## 为什么要单独一个提取函数而不是直接存整个 usage
///
/// 上游的 usage 里有一堆我们不需要的私有计数器，而且**形状会变**
/// （不同模型带的字段不一样）。直接存整个对象的话：
///   · 账本文件膨胀（每条多几百字节，长年累月很可观）
///   · 上游哪天改字段名，历史数据的聚合逻辑就一起坏掉
///
/// 抽成定长字段之后，账本是**稳定的自描述格式**，
/// 上游怎么改都不影响已落盘的数据。
///
/// ## 数字类型的宽容度
///
/// 上游偶尔会把数字给成字符串（`"2541"`）。这里两种都认 ——
/// 但**只认能解析成数字的**，认不出来的当「没给」（null），
/// 绝不当 0：0 会参与聚合，null 不会，后者不会把总量算歪。
/// </summary>
public static class UsageSummary
{
    /// <summary>
    /// 抽字段。
    ///
    /// @param usage 上游返回的 usage（**裁过的也行**，但没有 credit）。
    /// @returns 精简后的用量摘要；传 null 时返回空摘要。
    /// </summary>
    public static UsageSummaryResult From(JsonNode? usage)
    {
        if (usage is not JsonObject obj) return default;

        return new UsageSummaryResult
        {
            PromptTokens = Number(obj, "prompt_tokens"),
            CompletionTokens = Number(obj, "completion_tokens"),
            TotalTokens = Number(obj, "total_tokens"),
            Credit = Double(obj, "credit"),
            CacheHit = Number(obj, "prompt_cache_hit_tokens"),
            CacheMiss = Number(obj, "prompt_cache_miss_tokens"),
            CacheRead = Number(obj, "cache_read_input_tokens"),
            CacheWrite = Number(obj, "prompt_cache_write_tokens"),

            // 推理 token 有两个可能的位置 —— 上游自己的字段优先，
            // 退回 OpenAI 标准的 completion_tokens_details.reasoning_tokens。
            Reasoning = Number(obj, "completion_thinking_tokens")
                ?? Number(obj["completion_tokens_details"] as JsonObject, "reasoning_tokens"),
        };
    }

    /// <summary>抽一个整数型字段。认数字，也认能解析成数字的字符串。</summary>
    private static long? Number(JsonObject? obj, string key)
    {
        if (obj?[key] is not JsonNode node) return null;

        if (node is JsonValue value)
        {
            // ⚠ 顺序有讲究，而且**必须先试 string 再试数字**。
            //
            // `JsonValue.TryGetValue<T>` 要求底层类型**精确匹配** ——
            // 装的是 int 就只认 int，装的是 long 就只认 long。
            // 所以不能只试 long：`{"prompt_tokens": 2541}` 里那个 2541
            // 是 **int**，TryGetValue<long> 会返回 false。
            // （这是被自己的测试抓到的：期望 2541、实际 null。）
            //
            // 也不能只试 double：2541 是 int，TryGetValue<double> 同样 false。
            //
            // 所以逐类型试一遍，谁认就返回谁。
            if (value.TryGetValue<int>(out var asInt)) return asInt;
            if (value.TryGetValue<long>(out var asLong)) return asLong;
            if (value.TryGetValue<double>(out var asDouble) && double.IsFinite(asDouble)) return (long)asDouble;
            if (value.TryGetValue<decimal>(out var asDecimal)) return (long)asDecimal;

            // 上游偶尔把数字给成字符串
            if (value.TryGetValue<string>(out var text) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>抽一个浮点字段（credit 用）。</summary>
    private static double? Double(JsonObject? obj, string key)
    {
        if (obj?[key] is not JsonNode node) return null;

        if (node is JsonValue value)
        {
            // 同样要逐类型试 —— credit 常见是 double（0.22），
            // 但也可能是 int（上游给 0 或 1 时）或 decimal。
            if (value.TryGetValue<double>(out var asDouble) && double.IsFinite(asDouble)) return asDouble;
            if (value.TryGetValue<decimal>(out var asDecimal)) return (double)asDecimal;
            if (value.TryGetValue<int>(out var asInt)) return asInt;
            if (value.TryGetValue<long>(out var asLong)) return asLong;

            if (value.TryGetValue<string>(out var text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                double.IsFinite(parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}

/// <summary><see cref="UsageSummary.From"/> 的结果 —— 一个小的不可变载体。</summary>
public readonly record struct UsageSummaryResult
{
    public long? PromptTokens { get; init; }
    public long? CompletionTokens { get; init; }
    public long? TotalTokens { get; init; }
    public double? Credit { get; init; }
    public long? CacheHit { get; init; }
    public long? CacheMiss { get; init; }
    public long? CacheRead { get; init; }
    public long? CacheWrite { get; init; }
    public long? Reasoning { get; init; }

    /// <summary>一个字段都没抽到（空 usage / null）。</summary>
    public bool IsEmpty =>
        PromptTokens is null && CompletionTokens is null && TotalTokens is null &&
        Credit is null && CacheHit is null && CacheMiss is null &&
        CacheRead is null && CacheWrite is null && Reasoning is null;
}

/// <summary>
/// 账本的读取结果 —— 条目 + 跳过了多少坏行。
///
/// 为什么要把坏行数暴露出来：调用方（界面/端点）需要能区分
/// 「账本是空的」和「账本读得到东西但有一半行坏了」。前者正常，
/// 后者说明文件被外部工具改坏了，该提示用户。
/// 只回一个条目列表的话，这两种情况长得一模一样。
/// </summary>
public sealed record LedgerReadResult
{
    /// <summary>成功解析出来的账目（**按文件里的顺序**）。</summary>
    public required IReadOnlyList<UsageLedgerRecord> Entries { get; init; }

    /// <summary>跳过了多少行坏行（空行不算）。</summary>
    public int SkippedLines { get; init; }

    /// <summary>坏行的行号（从 1 开始）—— 给用户定位用。</summary>
    public IReadOnlyList<int> SkippedLineNumbers { get; init; } = [];

    /// <summary>有没有坏行。</summary>
    public bool HasCorruption => SkippedLines > 0;
}

/// <summary>
/// 用量账本 —— 把每次请求的真实成本追加落盘，并支持聚合查账。
///
/// ## 文件格式：JSONL
///
/// 一行一个 JSON 对象，放在与保险库同目录的 `usage.jsonl`。
///
/// **为什么不是把整个账本读出来、加一条、再整体写回**：
/// 那每次记账都要重写整个文件（几万条时每条几十毫秒），
/// 而且中途崩溃就把整本账毁了。追加写只碰文件尾，代价恒定。
///
/// **为什么不用 sqlite**：零第三方依赖是整个项目的承诺
/// （HANDOVER 第九节）。而且 JSONL 用 jq / awk / Python 都能直接分析，
/// 用户不需要额外工具。
///
/// **为什么不用日志文件代替**：日志是给人看、会滚动、格式不稳定；
/// 账本要做机器聚合，必须是稳定的可解析格式。见
/// <see cref="UsageLedgerRecord"/> 的类型注释里与 RequestLog 的区分。
///
/// ## 追加写的原子性（硬要求）
///
/// ⚠ 这一条容易想当然。`Vault.Write` 用的是「写临时文件 + 原子改名」——
/// 那对**整体重写**是对的，但**对追加写不适用**：追加时改名等于把
/// 已有内容再复制一遍，几万条时会把磁盘写爆，而且并发追加会互相覆盖。
///
/// 追加写的正确做法是保证「**一次写入调用写完整行**」：
///
/// ```
/// File.AppendAllText(path, oneCompleteLine, UTF8)
/// ```
///
/// 它在底层走一次 `open(O_APPEND)` + 一次 `write()`。POSIX 保证
/// 对 `O_APPEND` 的写入是原子的（写完之后文件偏移才前进），
/// Windows 上 .NET 内部同样以追加模式打开、单次 `WriteFile` 落盘。
/// 关键约束是：**先把整行拼好（含结尾换行），再一次调用写下去**。
/// 绝不能分两次写「JSON」和「\n」—— 那就给了崩溃落在中间的机会，
/// 留下半行 JSON 把后面所有行都连累坏。
///
/// 所以下面的 <see cref="Append"/> 先把 <c>line + "\n"</c> 拼成**一个**
/// 字符串，再一次性追加。这不是风格问题，是正确性问题。
///
/// ## 内存里的写缓冲
///
/// 每记一笔就开一次文件太慢（每个请求一次 syscall + 打开关闭）。
/// 这里用一个队列 + 一个后台消费者批量落盘 —— 但**批量崩掉不丢数据**：
/// 队列在进程内存里，进程被 kill 时最多丢掉最后未落盘的那一小批。
/// 账本是**统计用途**，不是计费依据（旧版注释也写明「不用它做实时计费」），
/// 丢最后几笔的代价可以接受；反过来，为了「一笔都不丢」而让每个请求
/// 同步等磁盘 IO，是拿业务可用性换统计精度，不划算。
/// </summary>
public sealed partial class UsageLedger : IDisposable
{
    /// <summary>账本文件名。和旧版一致。</summary>
    public const string FileName = "usage.jsonl";

    /// <summary>写到这个大小就轮转（10 MB，约几万条）。和旧版一致。</summary>
    public const long RotateBytes = 10 * 1024 * 1024;

    private readonly string _path;

    /// <summary>落盘时用的锁 —— 见 <see cref="WriteOne"/> 为什么必须有它。</summary>
    private readonly object _writeGate = new();

    private readonly object _gate = new();

    /// <summary>
    /// 长期持有的追加流。
    ///
    /// 为什么不每次开关文件：并发下会互相截断（见 <see cref="WriteOne"/>）。
    /// 这个字段只在 <see cref="_writeGate"/> 下碰。
    /// </summary>
    private FileStream? _stream;

    /// <summary>
    /// 日志。**非空**（默认 <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger"/>）。
    ///
    /// 为什么不用可空 + 每次判 null：源生成的 `LoggerMessage` 会直接
    /// 解引用这个字段，可空的话生成代码报 CS8602（本项目警告即错误）。
    /// 用 NullLogger 占位，语义等价而且没有分支。
    /// </summary>
    private readonly Microsoft.Extensions.Logging.ILogger _log;

    /// <summary>待落盘的条目（批量写用）。null 表示同步模式。</summary>
    private readonly Queue<UsageLedgerRecord>? _pending;

    /// <summary>落盘线程（异步模式）。</summary>
    private readonly Task? _pump;

    private readonly CancellationTokenSource? _stop;

    /// <summary>已记的条目数（含未落盘的）—— 轮转判断用。</summary>
    private long _written;

    /// <summary>落盘失败的次数。非零说明磁盘有问题，调用方该提示用户。</summary>
    private long _writeFailures;

    /// <summary>
    /// @param path      账本文件路径。
    /// @param log       日志（null = 不记日志，用 NullLogger）。
    /// @param buffered  true = 后台批量落盘（生产）；false = 每笔同步落盘（测试/关键场合）。
    /// </summary>
    public UsageLedger(string path, Microsoft.Extensions.Logging.ILogger? log = null, bool buffered = false)
    {
        _path = path;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        if (buffered)
        {
            _pending = new Queue<UsageLedgerRecord>();
            _stop = new CancellationTokenSource();
            _pump = Task.Run(() => PumpAsync(_stop.Token));
        }
    }

    /// <summary>账本文件路径。</summary>
    public string Path => _path;

    /// <summary>已记多少笔（含还没落盘的）。</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>落盘失败过几次。非零 = 磁盘写不进去，界面上该报警。</summary>
    public long WriteFailures => Interlocked.Read(ref _writeFailures);

    /// <summary>
    /// 记一笔。
    ///
    /// **绝不抛异常** —— 记账是旁路，它挂了不能影响用户的请求
    /// （旧版 <c>recordUsage</c> 的注释：「记账重要，但比业务可用性次要」）。
    /// 失败会累加 <see cref="WriteFailures"/> 并记日志。
    /// </summary>
    public void Append(UsageLedgerRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Interlocked.Increment(ref _written);

        if (_pending is not null)
        {
            // 异步模式：入队，由后台线程批量落盘
            lock (_gate)
            {
                _pending.Enqueue(record);

                // 队列太长说明落盘跟不上（磁盘慢/量大）——
                // 同步落一批，别让内存无限涨
                if (_pending.Count <= 512) return;
            }

            FlushPending();
            return;
        }

        WriteOne(record);
    }

    /// <summary>
    /// 记一笔（从上游 usage 抽字段的便捷入口）。
    ///
    /// @param model     客户端要的模型
    /// @param account   账号短标签（脱敏）
    /// @param usage     上游返回的 usage（**要那份 DeepClone 的原值**，见 SseSanitizer）
    /// </summary>
    public UsageLedgerRecord Append(
        string model,
        string? account,
        JsonNode? usage,
        DateTimeOffset at,
        string? usedModel = null,
        string kind = "chat",
        string outcome = "ok",
        long? elapsedMs = null,
        int? attempts = null)
    {
        var summary = UsageSummary.From(usage);

        var record = new UsageLedgerRecord
        {
            Iso = at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Ts = at.ToUnixTimeMilliseconds(),
            Model = model,
            UsedModel = string.Equals(usedModel, model, StringComparison.Ordinal) ? null : usedModel,
            Account = string.IsNullOrEmpty(account) ? null : account,
            Kind = kind,
            Outcome = outcome,
            ElapsedMs = elapsedMs,
            Attempts = attempts,
            PromptTokens = summary.PromptTokens,
            CompletionTokens = summary.CompletionTokens,
            TotalTokens = summary.TotalTokens,
            Credit = summary.Credit,
            CacheHit = summary.CacheHit,
            CacheMiss = summary.CacheMiss,
            CacheRead = summary.CacheRead,
            CacheWrite = summary.CacheWrite,
            Reasoning = summary.Reasoning,
        };

        Append(record);
        return record;
    }

    /// <summary>
    /// 把待落盘的条目全部写下去（异步模式用；同步模式下是空操作）。
    /// </summary>
    public void FlushPending()
    {
        if (_pending is null) return;

        var batch = new List<UsageLedgerRecord>();
        lock (_gate)
        {
            while (_pending.Count > 0) batch.Add(_pending.Dequeue());
        }

        foreach (var record in batch) WriteOne(record);
    }

    /// <summary>
    /// 写一条到文件。
    ///
    /// ⚠ **整行一次写完**（含结尾换行），并且**全程持写锁**。
    /// 见类型注释里对原子性的说明 —— 拆成两次写就给了崩溃落在中间的机会，
    /// 留下半行 JSON 把后面所有行连累坏。
    ///
    /// ## 为什么不是一句 File.AppendAllText
    ///
    /// 第一版就是 <c>File.AppendAllText</c>，被自己的并发测试抓到了：
    /// 8 线程 × 50 条（期望 400）**落不齐**。
    ///
    /// ⚠ **失败形态依平台而异，别只记一种**（这一点踩过，说明清楚）：
    ///
    /// ```
    /// Windows（本机实测，三次：263/360/184 行，其余全部抛 IOException）
    ///   → 直接抛 IOException: The process cannot access the file
    ///     '…' because it is being used by another process.
    ///   即：**会报错的失败**，不是静默的
    ///
    /// 更接近 POSIX 语义的实现
    ///   → 打开不报错，但关闭时按「它以为的长度」截断，静默丢行
    /// ```
    ///
    /// 两种的**根因是同一个**：<c>File.AppendAllText</c> 每次调用都
    /// **自己 open/close 一次**文件句柄。并发调用时 A 关闭会按「它以为的
    /// 长度」截断，把 B 刚写的部分切掉；而「单次 write 是原子的」这条
    /// 保证**只在同一个句柄上**成立 —— 每次重开句柄就等于放弃了它。
    /// 平台差异只决定它是「撞锁报错」还是「悄悄截断」。
    ///
    /// ⚠ **因此不要给这里加「吞掉 IOException 继续跑」的容错。**
    /// 那会把「会报错的失败」变成「真的静默失败」—— 正是本段要防的东西。
    /// 本类的做法是消除竞争本身（一把锁 + 长期持有的句柄），
    /// 而不是把竞争产生的异常吃掉。
    ///
    /// 所以改成：一把锁 + 一个**长期持有的**追加流。
    /// 同一个句柄上顺序写，既没有截断竞争，也省掉了每次开关文件的开销。
    /// **两种失败形态都被这一次修复一起挡住了。**
    ///
    /// ## 关于 O_APPEND
    ///
    /// <see cref="FileMode.Append"/> 在 .NET 里映射到追加语义
    /// （POSIX 上是 <c>O_APPEND</c>，Windows 上是
    /// <c>FILE_APPEND_DATA</c>）—— 每次写都定位到文件末尾，
    /// 不会覆盖已有内容。配上这把锁，整个进程内就是严格串行的。
    ///
    /// （跨进程并发不在保证范围内：那是另一个进程在改用户的账本，
    /// 本来就该由用户自己保证不这么干。）
    /// </summary>
    private void WriteOne(UsageLedgerRecord record)
    {
        try
        {
            // 先把整行拼好（含 \n）—— 拼在锁外，减少持锁时间
            var line = record.ToLine() + "\n";

            lock (_writeGate)
            {
                EnsureDirectory();

                if (ShouldRotate()) Rotate();

                // 长期持有句柄，只在需要时重开（文件被轮转/删除后）
                var stream = EnsureStream();
                if (stream is null) return;   // 打不开：失败已计入

                var bytes = Utf8NoBom.GetBytes(line);

                // 单次 Write —— 整行一次落盘
                stream.Write(bytes, 0, bytes.Length);

                // ⚠ 必须 Flush 到**流缓冲**之外，但不必每笔都 fsync 到盘。
                // 不 Flush 的话进程崩溃会丢掉整段缓冲里的内容
                //（那比丢最后一笔严重得多）；每笔都 fsync 又会让每个请求
                // 都等一次磁盘 IO —— 账本是统计用途，不需要那个保证。
                stream.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            Interlocked.Increment(ref _writeFailures);
            ResetStream();
            if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning))
            {
                // ⚠ CA1873：参数在实参位置上算会被分析器拦 —— 都在局部算好。
                var reason = ex.GetType().Name;
                LogAppendFailed(reason);
            }
        }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 拿到追加流（没有就开一个）。只在 <see cref="_writeGate"/> 下调用。
    ///
    /// 用 <see cref="FileMode.Append"/> —— 每次写都定位到文件末尾。
    ///
    /// ## ⚠ 共享模式必须是 ReadWrite（踩过）
    ///
    /// 第一版写的是 <c>FileShare.Read</c>，结果**整个进程自己都读不了
    /// 这个账本**：写流一直开着，Windows 上任何后来的 <c>File.ReadAllLines</c>
    /// 都抛 `IOException: The process cannot access the file ... because it is
    /// being used by another process`。
    ///
    /// 症状极隐蔽：网关跑着的时候，面板的「用量」页永远是空的，
    /// 而一停网关就能读到 —— 看起来像「界面有 bug」，
    /// 实际上是文件共享模式写错了。
    ///
    /// 所以共享要放到 ReadWrite：别人（界面、jq）随时能读，
    /// **跨进程**的另一个写者也不至于直接失败。
    /// 进程内我们自己用 <see cref="_writeGate"/> 串行，
    /// 不依赖文件系统的锁 —— 那才是可靠的。
    /// </summary>
    private FileStream? EnsureStream()
    {
        if (_stream is not null) return _stream;

        try
        {
            _stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.None);

            return _stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Interlocked.Increment(ref _writeFailures);

            if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning))
            {
                var reason = ex.GetType().Name;
                LogAppendFailed(reason);
            }

            return null;
        }
    }

    /// <summary>关掉并丢弃当前流（轮转、写失败之后调）。</summary>
    private void ResetStream()
    {
        try
        {
            _stream?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 关不掉就算了
        }

        _stream = null;
    }

    /// <summary>创建账本所在目录（收紧权限 —— 里面有账号标签和用量）。</summary>
    private void EnsureDirectory()
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir) || Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);

        // Unix 上收紧到 0700 —— 和保险库同一套策略。
        // Windows 不起作用（NTFS 的继承 ACE 已经够严），所以跳过。
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 某些文件系统不支持 —— 不能因为设不上权限就写不进去
        }
    }

    /// <summary>
    /// 该不该轮转。文件超过 <see cref="RotateBytes"/> 就换一个。
    ///
    /// ## ⚠ 为什么「读不到大小」必须留下信号（t84 修的静默失败）
    ///
    /// 原来这里 catch 之后直接 `return false`，没记任何日志。那个写法
    /// 把两件**完全不同的事**变成了同一个结果：
    ///
    /// ```
    /// ① 文件很小，不用轮转          ← 正常
    /// ② 读不到文件大小，所以不知道   ← 异常，而且后果严重
    /// ```
    ///
    /// ② 的后果是：**账本文件无界增长**。轮转的判据就是「文件超过 10 MB」，
    /// 而读大小失败时永远返回「不轮转」—— 于是文件只会一直长下去，
    /// 直到磁盘满。而且全程**没有任何信号**。
    ///
    /// 这与 `/admin/pools` 的 `accountCount` 恒为 0 是**同一个形态**：
    /// 「读不到」和「本来就没有」长得一样（见 docs/silent-failure-guards.md §2⑤）。
    ///
    /// ## 两种情形怎么区分
    ///
    /// ```
    /// 文件不存在 → 返回 false，**不记日志**
    ///              首次写入前的正常状态；记了就是噪音
    /// 读失败     → 返回 false，记 **Warning**
    ///              IO / 权限异常；必须让人看见「本次不轮转」
    /// ```
    ///
    /// 注意 `FileInfo.Exists` 对「不存在」返回 false 而**不抛**，
    /// 所以正常路径不会走到 catch —— catch 里出现的一定是真异常。
    /// </summary>
    private bool ShouldRotate()
    {
        try
        {
            var info = new FileInfo(_path);

            // 不存在 = 首次写入，正常状态。**不记日志**（记了就是噪音）。
            if (!info.Exists) return false;

            return info.Length >= RotateBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ⚠ 读不到大小 ≠ 文件很小。继续写原文件（不阻断记账是对的），
            //   但**必须留下信号** —— 否则「无界增长」这件事没人看得见。
            LogRotateCheckFailed(_path, ex);
            return false;
        }
    }

    /// <summary>
    /// 「读不到文件大小时该记什么」—— **public 是为了可测**，见下面的说明。
    ///
    /// ## 为什么单独一个方法（而不是塞在 catch 里）
    ///
    /// `ShouldRotate()` 里的 catch 分支**在测试里很难触发**：
    /// 要走进去，得让 `FileInfo.Exists` 为 true 而 `Length` 抛异常 ——
    /// 而实测（用真 C# 探针跑过四种坏路径）在本平台上：
    ///
    /// ```
    /// 真实目录        Exists=False   ← 走不到 Length
    /// 父目录已删      Exists=False   ← 同上
    /// 不存在          Exists=False   ← 同上
    /// 超长路径        Exists=False   ← 同上
    /// ```
    ///
    /// `Exists` 返回 **false 而不抛**，所以那些情形都进了「文件不存在」那条
    /// **正确**的路 —— 也就是说**造不出「Exists 为 true 但 Length 抛」**。
    ///
    /// 与其为了测试写一个依赖平台细节的脆弱构造（那种测试会变成
    /// 「在别人机器上偶发红」的噪音源），不如**把「该记什么」抽成可调用的方法**：
    ///
    /// ```
    /// ShouldRotate() 的 catch  ──调用──▶  LogRotateCheckFailed(path, ex)
    ///                                              ▲
    ///                       测试直接调这个 ─────────┘
    /// ```
    ///
    /// **测试验的是真实的生产代码路径**（catch 里就是调它），
    /// 而不是复刻一份「我以为 catch 里会这么做」的逻辑。
    ///
    /// ## 为什么是 public 而不是 internal + InternalsVisibleTo
    ///
    /// 本工程当前**没有** `InternalsVisibleTo`（`Credits` 的那些 internal
    /// 成员是靠 public 包装被间接测到的）。为一个日志方法引入一套新的
    /// 可见性机制，代价大于收益 —— 而这个方法本身**没有任何秘密**
    /// （路径 + 异常类型 + 一句固定文案），公开它不泄露任何东西。
    ///
    /// 判据：`Warning` 级，含**路径**与**异常类型**，说清「本次不轮转」。
    /// </summary>
    public void LogRotateCheckFailed(string path, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (!_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning)) return;

        // CA1873：实参位置上不能有计算 —— 日志关掉时那些计算就是白做的。
        // 先算好放局部变量，最后才传（HANDOVER 坑 6）。
        var reason = ex.GetType().Name;
        var detail = ex.Message;

        LogRotateUnreadable(path, reason, detail);
    }

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "读不到账本文件大小（{Path}，{Reason}：{Detail}），本次不轮转 —— 文件会继续增长")]
    private partial void LogRotateUnreadable(string path, string reason, string detail);

    /// <summary>
    /// 轮转：把当前文件改名归档，下次写新的。
    ///
    /// 归档名带日期和序号，保证不撞名（同一天轮转两次很常见）。
    /// </summary>
    private void Rotate()
    {
        try
        {
            // ⚠ 先关流再改名 —— 句柄还开着的话 Windows 上 File.Move 会
            // 因为文件被占用而失败（POSIX 允许，Windows 不允许）。
            // 这是跨平台行为差异，只在 Windows 上跑才暴露得出来。
            ResetStream();

            var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var dir = System.IO.Path.GetDirectoryName(_path) ?? ".";
            var baseName = System.IO.Path.GetFileNameWithoutExtension(_path);
            var archived = System.IO.Path.Combine(dir, $"{baseName}-{stamp}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.jsonl");

            File.Move(_path, archived);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 轮转失败就接着写原文件 —— 账本不会因此丢数据
        }
    }

    /// <summary>后台批量落盘线程。</summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            FlushPending();
        }

        // 退出前把剩下的写完 —— 正常关闭不该丢数据
        FlushPending();
    }

    /// <summary>停止后台线程、落盘剩余条目并关掉文件句柄。</summary>
    public void Dispose()
    {
        if (_stop is not null)
        {
            try
            {
                _stop.Cancel();
                _pump?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
            {
                // 停不下来就算了 —— 收尾不该抛
            }

            FlushPending();
            _stop.Dispose();
        }

        // ⚠ 关句柄。不关的话文件会一直被这个进程占着，
        // 用户（或界面）想读都读不了 —— 尤其是 Windows 上。
        lock (_writeGate) ResetStream();
    }

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "记账落盘失败（{Reason}），已跳过这一笔")]
    private partial void LogAppendFailed(string reason);

    // ── 读 ────────────────────────────────────────────────────

    /// <summary>
    /// 读整个账本。
    ///
    /// **坏行跳过，不整本失败** —— 这是硬要求。账本是外部可编辑的文件
    /// （用户会拿 jq/awk 分析甚至手工改），一行坏了就让整个账本读不出来，
    /// 等于把用户的历史数据全锁死。跳过 + 记警告是正确取舍。
    ///
    /// 文件不存在时返回空结果（不是异常）—— 「还没记过账」是正常状态。
    /// </summary>
    public LedgerReadResult Read()
    {
        // ⚠ 先把写缓冲刷出去再读。
        //
        // 账本用的是一个长期持有的追加流（见 WriteOne），写进去的内容
        // 可能还在流缓冲里。不刷就读的话，「刚记的这一笔读不出来」——
        // 那会让并发测试和界面都看到过期的数字。
        // 也要先清掉没落盘的队列（缓冲模式）。
        FlushPending();

        lock (_writeGate)
        {
            try
            {
                _stream?.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 刷不下去不影响已落盘的内容
            }
        }

        if (!File.Exists(_path)) return new LedgerReadResult { Entries = [] };

        string[] lines;
        try
        {
            // ⚠ 不能用 File.ReadAllLines —— 它默认以 FileShare.Read 打开，
            // 而我们自己那个**长期持有的写流**还开着，Windows 上会直接抛
            //「文件正被另一个进程使用」。这会导致「网关跑着的时候
            // 面板永远读不到用量，一停网关就正常」这种极难归因的症状。
            //
            // 所以显式用 FileShare.ReadWrite 打开来读。
            using var stream = new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var collected = new List<string>();

            while (reader.ReadLine() is { } line) collected.Add(line);
            lines = [.. collected];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning))
            {
                var reason = ex.GetType().Name;
                LogReadFailed(reason);
            }
            return new LedgerReadResult { Entries = [] };
        }

        return Parse(lines);
    }

    /// <summary>
    /// 解析 JSONL 行 —— 对齐旧版 <c>parseLedger</c>，但多记了坏行信息。
    ///
    /// 空行**不算坏行**（文件结尾常常多一个换行，那是正常的）。
    /// </summary>
    public static LedgerReadResult Parse(IEnumerable<string> lines)
    {
        var entries = new List<UsageLedgerRecord>();
        var skipped = new List<int>();
        var lineNumber = 0;

        foreach (var line in lines)
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var record = TryParseLine(trimmed);
            if (record is null) skipped.Add(lineNumber);
            else entries.Add(record);
        }

        return new LedgerReadResult
        {
            Entries = entries,
            SkippedLines = skipped.Count,
            SkippedLineNumbers = skipped,
        };
    }

    /// <summary>解析一行。坏行返回 null（**不抛**）。</summary>
    private static UsageLedgerRecord? TryParseLine(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UsageLedgerRecord>(json, UsageLedgerRecord.LedgerJson.ReadOptions);
        }
        catch (JsonException)
        {
            // 半截的行（正在写）、被外部工具改坏的行、或者 credit 被写成了字符串。
            // 三种都跳过 —— 猜它是什么意思比跳过危险得多。
            return null;
        }
    }

    /// <summary>
    /// 解析一份 JSONL 文本（路径无关的版本，给端点/测试用）。
    /// </summary>
    public static LedgerReadResult ParseText(string? text)
        => string.IsNullOrEmpty(text)
            ? new LedgerReadResult { Entries = [] }
            : Parse(text.Split('\n'));

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "账本读取失败（{Reason}）")]
    private partial void LogReadFailed(string reason);
}

/// <summary>
/// 聚合出来的一行（按模型或按账号）。
///
/// 字段对齐旧版 <c>aggregate</c> 的 <c>byModel</c>/<c>byAccount</c> ——
/// 除了原始累加值，还算了几个**派生指标**，因为「哪个模型贵」
/// 光看 credit 总量是看不出来的（贵可能只是因为用得多）。
/// </summary>
public sealed record UsageAggregateRow
{
    /// <summary>模型名或账号标签。</summary>
    public required string Key { get; init; }

    /// <summary>调用了多少次。</summary>
    public long Calls { get; init; }

    /// <summary>失败次数（outcome != ok）。</summary>
    public long Failures { get; init; }

    /// <summary>累计扣费。</summary>
    public double Credit { get; init; }

    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public long CacheHit { get; init; }
    public long CacheMiss { get; init; }

    /// <summary>总 token（prompt + completion）。</summary>
    public long Tokens => PromptTokens + CompletionTokens;

    /// <summary>
    /// 每千 token 花多少积分。
    ///
    /// 没有 token 时为 null（不是 0）——「0 积分/token」和「没有数据」
    /// 是两回事，混起来会让界面显示一个看起来很好的假数字。
    /// </summary>
    public double? CreditPerKToken => Tokens > 0 ? Credit / Tokens * 1000 : null;

    /// <summary>一积分能换多少 token。</summary>
    public long? TokensPerCredit => Credit > 0 ? (long)Math.Round(Tokens / Credit) : null;

    /// <summary>缓存命中率，0–1。没有缓存数据时为 null。</summary>
    public double? CacheHitRate
    {
        get
        {
            var total = CacheHit + CacheMiss;
            return total > 0 ? (double)CacheHit / total : null;
        }
    }
}

/// <summary>
/// 整本账的聚合结果 —— 对齐旧版 <c>aggregate</c> 的返回形状
/// （<c>{totals, byModel, byAccount}</c>）。
/// </summary>
public sealed record UsageAggregate
{
    /// <summary>总计。**永远非 null** —— 空账本时是全 0 的行（不是 null）。</summary>
    public required UsageAggregateRow Totals { get; init; }

    /// <summary>按模型，**按 credit 从多到少排**（和旧版一致）。</summary>
    public required IReadOnlyList<UsageAggregateRow> ByModel { get; init; }

    /// <summary>按账号，同样按 credit 排序。</summary>
    public required IReadOnlyList<UsageAggregateRow> ByAccount { get; init; }

    /// <summary>参与聚合的条目数（不含被跳过的坏行）。</summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// 时间范围（最早/最晚一条）。空账本时为 null。
    ///
    /// 为什么要它：界面上说「总共花了 X」时，用户第一个问题就是
    /// 「这是哪段时间的」。没有它，那个数字没有意义。
    /// </summary>
    public long? FirstTs { get; init; }
    public long? LastTs { get; init; }

    /// <summary>空账本的聚合结果（全 0，不是 null）—— 界面直接显示「还没有数据」。</summary>
    public static UsageAggregate Empty => new()
    {
        Totals = new UsageAggregateRow { Key = "总计" },
        ByModel = [],
        ByAccount = [],
    };
}

/// <summary>
/// 账本聚合 —— 对齐旧版 <c>usage.js</c> 的 <c>aggregate</c>。
///
/// 纯函数：给一批条目，出一份汇总。不碰文件、不碰网络，
/// 所以单元测试能直接喂构造数据（见 UsageLedgerTests）。
/// </summary>
public static class UsageAggregation
{
    /// <summary>聚合。</summary>
    public static UsageAggregate Aggregate(IReadOnlyList<UsageLedgerRecord> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0) return UsageAggregate.Empty;

        var models = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var accounts = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var totals = new Accumulator("总计");

        long firstTs = long.MaxValue;
        long lastTs = long.MinValue;

        foreach (var entry in entries)
        {
            totals.Add(entry);

            if (entry.Ts > 0)
            {
                if (entry.Ts < firstTs) firstTs = entry.Ts;
                if (entry.Ts > lastTs) lastTs = entry.Ts;
            }

            var modelKey = string.IsNullOrEmpty(entry.Model) ? "(未知模型)" : entry.Model;
            if (!models.TryGetValue(modelKey, out var modelAcc))
            {
                modelAcc = new Accumulator(modelKey);
                models[modelKey] = modelAcc;
            }
            modelAcc.Add(entry);

            // 没账号标签的条目不进 byAccount —— 和旧版一致
            //（旧版 `if (e.account)` 判断的就是「有没有值」）。
            // 造一个 "(未知账号)" 的桶会把它的用量全归到一个不存在的号上。
            if (!string.IsNullOrEmpty(entry.Account))
            {
                if (!accounts.TryGetValue(entry.Account, out var accountAcc))
                {
                    accountAcc = new Accumulator(entry.Account);
                    accounts[entry.Account] = accountAcc;
                }
                accountAcc.Add(entry);
            }
        }

        return new UsageAggregate
        {
            Totals = totals.Build(),
            ByModel = [.. models.Values.Select(a => a.Build()).OrderByDescending(r => r.Credit).ThenBy(r => r.Key, StringComparer.Ordinal)],
            ByAccount = [.. accounts.Values.Select(a => a.Build()).OrderByDescending(r => r.Credit).ThenBy(r => r.Key, StringComparer.Ordinal)],
            EntryCount = entries.Count,
            FirstTs = firstTs == long.MaxValue ? null : firstTs,
            LastTs = lastTs == long.MinValue ? null : lastTs,
        };
    }

    /// <summary>读文件 + 聚合一步到位。</summary>
    public static (UsageAggregate Aggregate, LedgerReadResult Read) ReadAndAggregate(UsageLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var read = ledger.Read();
        return (Aggregate(read.Entries), read);
    }

    /// <summary>
    /// 累加器。
    ///
    /// 为什么要一个可变累加器而不是直接 LINQ GroupBy：
    /// 缺字段的语义不一样 —— `<c>?? 0</c>` 参与求和时「没给」和「给了 0」
    /// 结果相同，但对 <c>Failures</c> 之类的计数就不一样。
    /// 显式累加器让「缺字段当 0」这件事只有一个发生地点，好审。
    /// </summary>
    private sealed class Accumulator(string key)
    {
        private long _calls;
        private long _failures;
        private double _credit;
        private long _promptTokens;
        private long _completionTokens;
        private long _cacheHit;
        private long _cacheMiss;

        public void Add(UsageLedgerRecord entry)
        {
            _calls++;
            if (!entry.Ok) _failures++;

            _credit += entry.Credit ?? 0;
            _promptTokens += entry.PromptTokens ?? 0;
            _completionTokens += entry.CompletionTokens ?? 0;
            _cacheHit += entry.CacheHit ?? 0;
            _cacheMiss += entry.CacheMiss ?? 0;
        }

        public UsageAggregateRow Build() => new()
        {
            Key = key,
            Calls = _calls,
            Failures = _failures,

            // 四舍五入到 4 位 —— 和旧版 `Number(row.credit.toFixed(4))` 一致。
            // 浮点累加的最后几位是噪声，显示出来只会让人怀疑数字。
            Credit = Math.Round(_credit, 4),

            PromptTokens = _promptTokens,
            CompletionTokens = _completionTokens,
            CacheHit = _cacheHit,
            CacheMiss = _cacheMiss,
        };
    }
}
