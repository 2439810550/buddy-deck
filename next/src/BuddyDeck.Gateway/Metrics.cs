using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace BuddyDeck.Gateway;

/// <summary>
/// 一次请求的元数据记录（**脱敏**）。
///
/// ## 为什么参数就这么多，而且都是值类型/短字符串
///
/// 这是脱敏的**结构性保证**：这个类型里根本没有能装下提示词或响应正文的
/// 字段。调用方即使想误传也传不进来 —— 这比「记得别传」可靠得多。
///
/// 具体地：
///   · <see cref="Model"/>      模型名，客户端给的，不是内容
///   · <see cref="Status"/>     HTTP 状态码
///   · <see cref="DurationMs"/> 耗时
///   · <see cref="AccountTag"/> 账号短标签（<see cref="BuddyDeck.Core.Credential.AccountTag"/>
///                              或池里的 Label），**不是 token**
///   · <see cref="ErrorKind"/>  上游错误分类（枚举式的常量字符串），**不是错误正文**
///
/// 错误正文单独走 <see cref="RequestLog.RedactError"/> 压成一句人话之后
/// 才允许进这个对象 —— 而且只保留分类词，不留原文。
///
/// ## ⚠ 指标口径：这个类型同时承载「尝试」与「客户端请求」两种记录
///
/// 网关在**单次客户端请求内**会换号重试（见 <c>GatewayApp.RunWithRetryAsync</c>）。
/// 每次尝试都会产生一条 <see cref="RequestRecord"/>（否则重试次数就统计不到），
/// 但一次客户端请求只应有一条**终局**记录。两者靠
/// <see cref="Terminal"/> 区分：
///
/// <code>
/// 尝试记录（Terminal=false）→ 只喂给 AttemptCount / Recent / 诊断去看
/// 终局记录（Terminal=true ）→ 喂给 TotalRequests / 成功率 / 平均耗时
/// </code>
///
/// **为什么用字段而不是另立一个类型**：两条记录走的是同一个 <c>Notify</c>
/// 出口、同一份脱敏校验、同一套断言。分成两个类型会让「哪条走了哪条没走」
/// 变成靠自觉 —— 那正是这个类型存在的意义要避免的。
///
/// ⚠ 注意这里**新增的两个字段仍然全是值类型 / 数值**，
/// 脱敏的结构性保证没有被放松：依然没有任何槽位能装下正文。
/// </summary>
public sealed record RequestRecord
{
    /// <summary>请求发生的时刻（UTC）。</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>请求 id。客户端给的或我们生成的（见 <see cref="RequestIds"/>）。</summary>
    public required string RequestId { get; init; }

    /// <summary>客户端要的模型名。空表示没给。</summary>
    public string Model { get; init; } = "";

    /// <summary>实际生效的模型（换过模型时和请求的不一样）。</summary>
    public string UsedModel { get; init; } = "";

    /// <summary>HTTP 状态码。</summary>
    public int Status { get; init; }

    /// <summary>
    /// 端到端耗时（毫秒）。
    ///
    /// ⚠ <see cref="Terminal"/> 为 true 时，这是**用户实际等待的时长**
    /// —— 从这次客户端请求开始算起，**含中间所有失败重试的耗时**，
    /// 不是单次上游往返。因为用户等的就是那么久。
    ///
    /// <see cref="Terminal"/> 为 false 时（中间某次失败的尝试），
    /// 它是「从请求开始到这次尝试失败」的累计耗时。
    /// </summary>
    public long DurationMs { get; init; }

    /// <summary>账号短标签。**绝不是 token、也不是完整 uid。**</summary>
    public string AccountTag { get; init; } = "";

    /// <summary>上游错误分类（rate_limit / unauthorized / …）。成功时空串。</summary>
    public string ErrorKind { get; init; } = "";

    /// <summary>终局结果：ok / failed / cancelled。</summary>
    public string Outcome { get; init; } = "";

    /// <summary>是不是流式。</summary>
    public bool Streamed { get; init; }

    /// <summary>上游 HTTP 状态（0 = 连接层失败）。</summary>
    public int UpstreamStatus { get; init; }

    /// <summary>
    /// 这条记录是不是**真的发出去了一个上游请求**。
    ///
    /// ```
    /// true   正常情况下（发了上游、拿到响应，无论成功或失败）
    /// false  「没号可发」这一类 —— Pick 没选出账号，请求压根没出网
    /// ```
    ///
    /// 为什么要区分：<see cref="MetricsSnapshot.AttemptCount"/> 的口径是
    /// **上游尝试次数**，而「号全在冷却里、一个请求都没发出去」不该算一次尝试
    /// —— 否则「池子被打了几次」这个体检指标会被本地短路污染。
    ///
    /// 默认 true：绝大多数记录都对应一次真实的上游请求。
    /// </summary>
    public bool SentToUpstream { get; init; } = true;

    /// <summary>
    /// 这次客户端请求**内部**已经发生的上游尝试次数（含本次）。从 1 起。
    ///
    /// 用途有两个：
    ///   · <see cref="Terminal"/> 为 false 时，它标出「这是第几次尝试」
    ///   · <see cref="Terminal"/> 为 true 时，它标出「这次请求总共试了几次」
    ///     —— 大于 1 就说明**换号重试机制被触发过**，界面要能看见这个信号
    ///
    /// 健康度体检靠它：请求 100 次 / 累计尝试 137 次，这个比值才是
    /// 「账号池是不是在恶化」的指标。只看客户端成功率是看不到的。
    /// </summary>
    public int Attempts { get; init; } = 1;

    /// <summary>
    /// 这条记录是不是**这次客户端请求的终局结果**。
    ///
    /// ```
    /// true  一次客户端请求恰好有一条 —— 主口径（总数/成功率/平均耗时）只数它
    /// false 中间失败的那几次尝试 —— 只进 AttemptCount 和 Recent 供诊断
    /// ```
    ///
    /// 默认 true：绝大多数调用点（没账号、鉴权失败、参数错误）都是
    /// 一次请求一条终局记录，不用显式写。
    ///
    /// ## ⚠ 为什么它必须叫 `Terminal` 而不是 `IsTerminal`（ui 抓到的坑）
    ///
    /// 这个属性原来叫 `IsTerminal`，`ToJson()` 写出去的键是 `"terminal"`
    /// —— **写得出、读不回来**。
    ///
    /// 根因：<c>System.Text.Json</c> 的 <c>PropertyNameCaseInsensitive</c>
    /// 只做「大小写无关的**完整名字**比较」，**不做 `Is` 前缀推理**。
    /// 所以 `IsTerminal` 只接受 `isTerminal` / `IsTerminal`，
    /// 而 `ToJson()` 发的却是 `terminal`：
    ///
    /// ```
    /// "terminal"   → IsTerminal 留在默认值 true   ❌ 静默错
    /// "isTerminal" → 正确绑定 false               ✅
    /// ```
    ///
    /// **为什么这个 bug 特别危险**：默认值恰好是 `true`，
    /// 于是绑不上时「所有中间失败的尝试都被当成终局」——
    /// 编译不报、运行不报、字段还有值（默认值看起来很合理）。
    ///
    /// 改名成 `Terminal` 之后，`terminal` 键大小写无关地正好对上，
    /// **读写的键名天然对称**，不再依赖任何前缀推理。
    /// （ui 是在他的 App 侧 DTO 里踩到的 —— 同一个 JSON，他读也一样中招。）
    /// </summary>
    public bool Terminal { get; init; } = true;

    /// <summary>
    /// 序列化成 JSON（给 /metrics 和日志用）。
    ///
    /// **这是脱敏的最终出口** —— 测试断言的就是这个字符串里
    /// 不含提示词标记文本、不含 accessToken 原值。
    /// </summary>
    public JsonObject ToJson() => new()
    {
        ["at"] = At.ToString("O", CultureInfo.InvariantCulture),
        ["requestId"] = RequestId,
        ["model"] = Model,
        ["usedModel"] = UsedModel,
        ["status"] = Status,
        ["durationMs"] = DurationMs,
        ["accountTag"] = AccountTag,
        ["errorKind"] = ErrorKind,
        ["outcome"] = Outcome,
        ["streamed"] = Streamed,
        ["upstreamStatus"] = UpstreamStatus,
        ["attempts"] = Attempts,
        ["terminal"] = Terminal,
        ["sentToUpstream"] = SentToUpstream,
    };
}

/// <summary>
/// 请求 id 的分配。
///
/// ## 规则
///
/// ```
/// 客户端给了 X-Request-Id（或 X-Correlation-Id）→ **沿用**（可观测性要求端到端同一个 id）
/// 没给                                        → 生成 UUID
/// ```
///
/// 沿用客户端值很重要：调用方的日志里记的是它自己发的那个 id，
/// 我们换成自己生成的，两边的日志就关联不上了 —— 那正是请求 id 存在的意义。
///
/// ## 为什么不用 Activity.Current
///
/// .NET 的分布式追踪有自己的 id（W3C traceparent）。但**用户的面板、
/// curl、第三方 OpenAI 客户端**都不发 traceparent，它们发的是
/// `X-Request-Id`。这一层的目标读者是人和面板，不是 APM。
/// </summary>
public static class RequestIds
{
    /// <summary>认这几个头（不区分大小写）。</summary>
    private static readonly string[] AcceptedHeaders =
    [
        "X-Request-Id",
        "X-Correlation-Id",
        "X-Trace-Id",
    ];

    /// <summary>客户端给的 id 最长留这么长 —— 防止有人塞 1 MB 的头进来。</summary>
    private const int MaxLength = 128;

    /// <summary>
    /// 取请求 id：优先沿用客户端给的，没有就生成一个。
    ///
    /// @returns 一个非空的 id。**永不抛异常。**
    /// </summary>
    public static string Resolve(HttpContext context)
    {
        foreach (var header in AcceptedHeaders)
        {
            if (!context.Request.Headers.TryGetValue(header, out var values)) continue;

            var value = values.ToString().Trim();
            if (value.Length == 0) continue;

            // 截断而不是拒绝：长 id 是调用方自己的事，不该因此让请求失败
            return value.Length <= MaxLength ? value : value[..MaxLength];
        }

        return NewId();
    }

    /// <summary>生成一个新 id。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// 指标查询入口 —— **界面读的就是这个**。
///
/// 为什么是接口而不是直接给实现类：界面（BuddyDeck.App）的
/// <c>HttpGatewayClient</c> 走 HTTP 拿 JSON，但测试和未来的内嵌宿主
/// （网关跑在界面进程里）可以直接拿实现。两边共用同一个快照形状。
///
/// 所有方法都必须是**线程安全且非阻塞**的 —— 它们可能被界面的
/// 定时刷新线程调用。
/// </summary>
public interface IMetricsSource
{
    /// <summary>取一帧快照。**读的过程不会看到写了一半的状态。**</summary>
    MetricsSnapshot Snapshot();

    /// <summary>记一条请求记录。</summary>
    void Record(RequestRecord record);

    /// <summary>清空（切池、重启统计时用）。</summary>
    void Reset();
}

/// <summary>
/// 指标的一帧快照。
///
/// ## 为什么是快照而不是「读的时候现算」
///
/// 界面每几秒刷新一次，而写侧是每个请求都写。现算的话：
///   · 读的时候要拿锁，把写侧挡住 —— 高并发下这是可观测性拖慢业务
///   · 算出来的数可能来自不同的时刻（总数是 t1 的、成功数是 t2 的）
/// 快照是在**一个瞬间**把一致的一组数取出来，读侧拿到的是自洽的。
///
/// ## ⚠ 两套口径，别混（t16 改造的核心）
///
/// 网关在单次客户端请求内会换号重试，所以「一次请求」有两个含义。
/// 快照里同时给出两套数，各有各的用途：
///
/// <code>
/// 主口径（用户视角）  TotalRequests / HttpSuccessRate / CompletionRate /
///                     AverageLatencyMs / P95LatencyMs / MaxLatencyMs
///                     → **一次客户端请求算一个**，无论内部重试了几次。
///                       回答的是「用户拿到好结果的概率有多大」。
///
/// 诊断口径（池子视角）AttemptCount
///                     → **一次上游尝试算一个**。回答的是「账号池健不健康」。
/// </code>
///
/// **为什么要分开**：按尝试算成功率，会让「换号重试成功」这种事
/// **拉低**成功率 —— 重试越多掉得越狠。而重试恰恰是系统在正常工作。
/// 那会呈现出「系统帮你换号救回来了，所以成功率下降了」这种荒谬信号。
///
/// 但反过来也不能只看客户端成功率：一个请求试了 3 次才成功，
/// 说明池子在恶化，而客户端成功率完全看不见这个信号。
/// 所以 <see cref="AttemptCount"/> 必须保留 ——
/// 「请求 100 次 / 上游尝试 137 次」这个比值才是换号机制的体检指标。
///
/// 字段名对齐界面已经写好的 <c>GatewayMetrics</c> DTO
/// （见 src/BuddyDeck.App/Services/IGatewayClient.cs）—— 那边读的是
/// `/metrics` 的 JSON，键名必须一模一样，否则界面全是默认值。
/// </summary>
public sealed record MetricsSnapshot
{
    /// <summary>
    /// 处理的**客户端请求**总数（主口径）。
    ///
    /// 一次客户端请求算一个 —— 内部换号重试 3 次也还是 1。
    /// 别把它当成「上游被打了几次」，那个数是 <see cref="AttemptCount"/>。
    /// </summary>
    public long TotalRequests { get; init; }

    /// <summary>
    /// 累计**上游尝试**次数（诊断口径）。
    ///
    /// 每次真正发往上游的请求算一个（含失败后换号重试的那些）。
    /// 与 <see cref="TotalRequests"/> 的比值是**换号机制的体检指标**：
    ///
    /// ```
    /// 100 请求 / 100 尝试  → 池子健康，一次就中
    /// 100 请求 / 137 尝试  → 池子里有号在恶化，37% 的请求需要重试
    /// ```
    ///
    /// 必须 ≥ <see cref="TotalRequests"/>（每次客户端请求至少尝试一次）。
    /// 差值是「因重试而多打的上游请求数」。
    /// </summary>
    public long AttemptCount { get; init; }

    /// <summary>HTTP 层成功（状态码 2xx）的比例，0–1。没有请求时是 0。按客户端请求算。</summary>
    public double HttpSuccessRate { get; init; }

    /// <summary>完成成功率（真的把回复吐完了的比例），0–1。没有完成的请求时是 0。按客户端请求算。</summary>
    public double CompletionRate { get; init; }

    /// <summary>
    /// 平均耗时（毫秒），按客户端请求算。
    ///
    /// ⚠ 这是**用户实际等待的时长** —— 含全部重试，不是单次上游往返。
    /// 所以重试会把它拉长，这正是应该的：用户确实多等了。
    /// </summary>
    public double AverageLatencyMs { get; init; }

    /// <summary>最近 N 条（新的在前）。**含中间失败的尝试**（带 <c>Terminal=false</c>）。</summary>
    public IReadOnlyList<RequestRecord> Recent { get; init; } = [];

    /// <summary>按 outcome 的计数（ok / failed / cancelled）。按客户端请求算。</summary>
    public IReadOnlyDictionary<string, long> Outcomes { get; init; } = new Dictionary<string, long>();

    /// <summary>
    /// 上游失败按分类计数。
    ///
    /// 为什么要分开统计：429 和 401 的处理方式完全不同
    /// （前者等一会儿就好，后者要重新登录）。只看一个「失败率」
    /// 是分不出来的。
    ///
    /// **含中间失败的尝试** —— 这里数的是「上游出过几次这类错」，
    /// 被重试掩盖掉的那次同样值得看见（那正是池子恶化的证据）。
    /// </summary>
    public IReadOnlyDictionary<string, long> ErrorKinds { get; init; } = new Dictionary<string, long>();

    /// <summary>P95 耗时（毫秒），按客户端请求算。样本不够时返回最大值。</summary>
    public double P95LatencyMs { get; init; }

    /// <summary>最慢的一次客户端请求（毫秒）。</summary>
    public long MaxLatencyMs { get; init; }

    /// <summary>服务启动以来的字节吞吐 —— 暂时不统计，留着给界面占位。</summary>
    public long StartedAtUnixMs { get; init; }

    public JsonObject ToJson()
    {
        var recent = new JsonArray();
        foreach (var record in Recent) recent.Add(record.ToJson());

        var outcomes = new JsonObject();
        foreach (var (key, value) in Outcomes) outcomes[key] = value;

        var kinds = new JsonObject();
        foreach (var (key, value) in ErrorKinds) kinds[key] = value;

        return new JsonObject
        {
            ["totalRequests"] = TotalRequests,
            ["attemptCount"] = AttemptCount,
            ["httpSuccessRate"] = HttpSuccessRate,
            ["completionRate"] = CompletionRate,
            ["averageLatencyMs"] = AverageLatencyMs,
            ["p95LatencyMs"] = P95LatencyMs,
            ["maxLatencyMs"] = MaxLatencyMs,
            ["recent"] = recent,
            ["outcomes"] = outcomes,
            ["errorKinds"] = kinds,
            ["startedAtUnixMs"] = StartedAtUnixMs,
        };
    }
}

/// <summary>
/// 指标的线程安全实现。
///
/// ## 设计要点
///
/// 1. **环形缓冲**装最近 N 条 —— 定长，不会因为跑了三天就把内存吃光。
///    N 可配（见 <see cref="MetricsOptions.RecentCapacity"/>），默认 50。
///
/// 2. **计数用 Interlocked，环形缓冲用一把短锁。**
///    计数是纯自增，无锁最快；环形缓冲要同时改「写指针 + 槽位 +
///    有序读取」，用 <see cref="ConcurrentQueue{T}"/> 也行，但那样
///    「取最近 N 条」要遍历整个队列。这里用定长数组 + 一把只在
///    写入时持有的短锁，读取时**一次性复制出一个数组**再放手 ——
///    读侧因此永远看不到写了一半的状态。
///
/// 3. **读侧不阻塞写侧。** <see cref="Snapshot"/> 持锁的时间是
///    「复制 N 个引用」，不是「算统计量」。统计全在锁外算。
/// </summary>
public sealed partial class RequestMetrics : IMetricsSource
{
    private readonly RequestRecord?[] _ring;
    private readonly object _ringGate = new();
    private readonly TimeProvider _clock;

    /// <summary>环形缓冲的写指针（下一条写这里）。</summary>
    private int _cursor;

    /// <summary>已经写进环里的条数（可能小于容量）。</summary>
    private int _filled;

    // ── 计数（无锁）──────────────────────────────────────────
    //
    // ⚠ 两套口径各有一组计数器，**不要混用**（见 MetricsSnapshot 的注释）。
    //
    //   主口径（_total / _httpSuccess / _completed / _sumLatencyMs / _maxLatencyMs）
    //     只被 Terminal=true 的记录驱动 —— 一次客户端请求算一个。
    //   诊断口径（_attempts）
    //     被每一条记录驱动 —— 一次上游尝试算一个。
    private long _total;
    private long _httpSuccess;
    private long _completed;
    private long _sumLatencyMs;
    private long _maxLatencyMs;

    /// <summary>累计上游尝试次数（诊断口径，每条记录都算一次）。</summary>
    private long _attempts;

    /// <summary>按 outcome 的计数 —— 只数终局（否则一次请求会被数成多次失败）。</summary>
    private readonly ConcurrentDictionary<string, long> _outcomes = new(StringComparer.Ordinal);

    /// <summary>
    /// 上游失败按分类计数。
    ///
    /// ⚠ 这一条**两条口径都收**（含中间失败的尝试）——
    /// 它回答的是「上游出过几次这类错」，被重试掩盖掉的那次同样要看。
    /// 那正是「池子在恶化」的证据。
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _errorKinds = new(StringComparer.Ordinal);

    private readonly long _startedAtMs;

    public RequestMetrics(TimeProvider clock, MetricsOptions? options = null)
    {
        _clock = clock;
        _startedAtMs = clock.GetUtcNow().ToUnixTimeMilliseconds();

        var capacity = (options ?? new MetricsOptions()).RecentCapacity;
        if (capacity < 1) capacity = 1;
        _ring = new RequestRecord?[capacity];
    }

    /// <summary>环形缓冲容量（最近 N 条里的 N）。</summary>
    public int RecentCapacity => _ring.Length;

    /// <summary>
    /// 记一条请求记录。线程安全，永不抛异常。
    ///
    /// ## 两套口径在这里分流（t16）
    ///
    /// ```
    /// 每次都算：  _attempts   ← 上游尝试次数（诊断口径）
    ///             _errorKinds ← 上游出过什么错（含被重试掩盖的那次）
    ///             _ring       ← 最近 N 条（界面要能看见「这次重试了」）
    ///
    /// 只算终局：  _total / _httpSuccess / _completed / _sumLatencyMs /
    ///             _maxLatencyMs / _outcomes   ← 主口径（用户视角）
    /// ```
    ///
    /// **为什么尝试阶段也要进 _ring**：界面上的「最近请求」如果只显示
    /// 终局，「这个请求换了 2 次号才成功」这件事就完全看不见了 ——
    /// 而那恰恰是运维最需要知道的信号。记录里带 <c>Attempts</c> /
    /// <c>Terminal</c>，界面可以自己决定怎么呈现。
    /// </summary>
    public void Record(RequestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // ① 诊断口径 —— 只有**真的发出去过**的记录才算一次上游尝试。
        //
        //   「号全在冷却里、一个请求都没发出去」（sentToUpstream=false）不算 ——
        //   否则本地短路会把「池子被打了几次」这个体检指标污染掉。
        if (record.SentToUpstream)
        {
            Interlocked.Increment(ref _attempts);
        }

        if (!string.IsNullOrEmpty(record.ErrorKind))
        {
            _errorKinds.AddOrUpdate(record.ErrorKind, 1, static (_, old) => old + 1);
        }

        // ② 主口径 —— 只有终局记录才动计数。
        //
        // 一次客户端请求内部可能换号重试 3 次，但用户只发起了一次。
        // 按尝试算会让「重试成功」这种事**拉低**成功率，
        // 而重试恰恰是系统在正常工作。
        if (record.Terminal)
        {
            Interlocked.Increment(ref _total);

            var httpOk = record.Status is >= 200 and < 300;
            if (httpOk) Interlocked.Increment(ref _httpSuccess);

            if (string.Equals(record.Outcome, "ok", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _completed);
            }

            // ⚠ DurationMs 在终局记录上是「用户实际等待的时长」（含全部重试），
            // 不是单次上游往返 —— 用户等的就是那么久。
            Interlocked.Add(ref _sumLatencyMs, record.DurationMs);
            InterlockedMax(ref _maxLatencyMs, record.DurationMs);

            if (!string.IsNullOrEmpty(record.Outcome))
            {
                _outcomes.AddOrUpdate(record.Outcome, 1, static (_, old) => old + 1);
            }
        }

        // ③ 环形缓冲 —— 两条口径都进（界面要能看见重试过程）
        // 只在改指针和槽位这一瞬间持锁
        lock (_ringGate)
        {
            _ring[_cursor] = record;
            _cursor = (_cursor + 1) % _ring.Length;
            if (_filled < _ring.Length) _filled++;
        }
    }

    /// <summary>
    /// 取一帧快照。
    ///
    /// **持锁时间只够复制引用**，统计在锁外算。这样界面每几秒刷一次
    /// 不会把请求线程挡住。
    ///
    /// 读到的计数和环形缓冲**不是同一个瞬间**的（计数先取、环后取）——
    /// 这是有意的取舍：为了那一丁点一致性去把写侧锁住不划算，
    /// 而且界面上「总数 100 / 最近 50 条」这个量级的误差没有意义。
    /// 真正要保证的是**不读到 null 槽位、不抛异常、不读到一半的数组**。
    /// </summary>
    public MetricsSnapshot Snapshot()
    {
        // ① 先读计数（两套口径各读一组）
        var total = Interlocked.Read(ref _total);
        var attempts = Interlocked.Read(ref _attempts);
        var httpSuccess = Interlocked.Read(ref _httpSuccess);
        var completed = Interlocked.Read(ref _completed);
        var sumLatency = Interlocked.Read(ref _sumLatencyMs);
        var maxLatency = Interlocked.Read(ref _maxLatencyMs);
        var startedAt = _startedAtMs;

        // ② 再复制环形缓冲（有序：最新的在前）
        RequestRecord[] recent;
        lock (_ringGate)
        {
            recent = new RequestRecord[_filled];
            for (var i = 0; i < _filled; i++)
            {
                // 最新的在第 _cursor-1 位，往前推
                var index = (_cursor - 1 - i + _ring.Length * 2) % _ring.Length;
                recent[i] = _ring[index]!;
            }
        }

        // ③ 统计全在锁外算
        //
        // ⚠ 百分位/最大耗时只取**终局**记录 —— 中间失败的尝试耗时短得多，
        //    混进去会把「用户实际等待时长」的分布算歪（P95 被拉低）。
        var terminalLatencies = recent
            .Where(r => r.Terminal)
            .Select(r => r.DurationMs)
            .OrderBy(v => v)
            .ToArray();

        return new MetricsSnapshot
        {
            TotalRequests = total,
            AttemptCount = attempts,
            HttpSuccessRate = Rate(httpSuccess, total),
            CompletionRate = Rate(completed, total),
            AverageLatencyMs = total == 0 ? 0 : (double)sumLatency / total,
            MaxLatencyMs = maxLatency,
            P95LatencyMs = Percentile(terminalLatencies, 0.95),
            Recent = recent,
            Outcomes = new Dictionary<string, long>(_outcomes, StringComparer.Ordinal),
            ErrorKinds = new Dictionary<string, long>(_errorKinds, StringComparer.Ordinal),
            StartedAtUnixMs = startedAt,
        };
    }

    public void Reset()
    {
        lock (_ringGate)
        {
            Array.Clear(_ring);
            _cursor = 0;
            _filled = 0;
        }

        Interlocked.Exchange(ref _total, 0);
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _httpSuccess, 0);
        Interlocked.Exchange(ref _completed, 0);
        Interlocked.Exchange(ref _sumLatencyMs, 0);
        Interlocked.Exchange(ref _maxLatencyMs, 0);
        _outcomes.Clear();
        _errorKinds.Clear();
    }

    /// <summary>比例。分母为 0 时返回 0（不是 NaN —— NaN 会让界面显示 "NaN%"）。</summary>
    private static double Rate(long part, long whole) => whole <= 0 ? 0d : (double)part / whole;

    /// <summary>
    /// 百分位。样本为空返回 0。
    ///
    /// 用「最近邻」而不是插值：指标是给人看的趋势，插值出来的
    /// 小数位在界面上没有意义。
    /// </summary>
    private static double Percentile(long[] sorted, double fraction)
    {
        if (sorted.Length == 0) return 0;

        var index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>
    /// CAS 循环取最大值。
    ///
    /// 为什么不用锁：这是每个请求都会走的一行，为了一个统计量去抢锁
    /// 不划算。CAS 失败重试的代价远小于锁竞争。
    ///
    /// ⚠ <c>Math.Max</c> 不能用在这里 —— 它不是原子的：
    /// 两个线程同时读到旧值，各写各的，小值可能覆盖大值。
    /// </summary>
    private static void InterlockedMax(ref long target, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }
}

/// <summary>指标的可配置参数。</summary>
public sealed record MetricsOptions
{
    /// <summary>
    /// 最近请求保留多少条。默认 50。
    ///
    /// 为什么是 50：界面上的「最近请求」列表一屏能显示十几条，
    /// 50 条够翻好几页，又不至于让 /metrics 的响应体大到影响刷新。
    /// </summary>
    public int RecentCapacity { get; init; } = 50;
}
