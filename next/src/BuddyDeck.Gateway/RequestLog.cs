using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;

namespace BuddyDeck.Gateway;

/// <summary>
/// 一个**脱敏的**日志条目。
///
/// ## 为什么单独立一个类型，而不是直接往 ILogger 里塞字符串
///
/// 因为「别把提示词写进日志」这条规矩，靠自觉是守不住的：
/// 今天有人为了排查问题插一行 <c>LogDebug("body: {Body}", rawBody)</c>，
/// 明天它就会在生产上把用户的对话内容写成文件。
///
/// 所以这里从**类型上**杜绝：
///
/// · 这个 record 的字段里**没有**任何能装下请求体/响应体的槽位
/// · 它是 <c>readonly record struct</c> 风格的定长元数据，字段都是
///   短字符串和数值
/// · 想记正文的话，得先改这个类型 —— 那是一次显式的、会被 review 到的改动，
///   而不是顺手加一个参数
///
/// 调用方唯一能做的「自由输入」是 <see cref="Detail"/>，
/// 而它只能由 <see cref="RequestLog.Classify"/> / <see cref="RequestLog.RedactError"/>
/// 产出 —— 那两个方法做的是「把上游错误压成一句分类话」，
/// 输出是受控的短串，而且**不包含**原始正文。
/// </summary>
public sealed record LogEntry
{
    /// <summary>时刻（UTC）。ISO 8601。</summary>
    public required string At { get; init; }

    /// <summary>级别：Information / Warning / Error。</summary>
    public required string Level { get; init; }

    /// <summary>请求 id（能和调用方的日志对上）。</summary>
    public required string RequestId { get; init; }

    /// <summary>客户端要的模型。</summary>
    public string Model { get; init; } = "";

    /// <summary>实际用的模型。</summary>
    public string UsedModel { get; init; } = "";

    /// <summary>HTTP 状态码。</summary>
    public int Status { get; init; }

    /// <summary>耗时（毫秒）。</summary>
    public long DurationMs { get; init; }

    /// <summary>账号短标签（uid 前 8 位或池里的 Label）——**不是 token**。</summary>
    public string AccountTag { get; init; } = "";

    /// <summary>上游错误分类（rate_limit / unauthorized / …）——**不是错误正文**。</summary>
    public string ErrorKind { get; init; } = "";

    /// <summary>终局结果：ok / failed / cancelled。</summary>
    public string Outcome { get; init; } = "";

    /// <summary>是不是流式。</summary>
    public bool Streamed { get; init; }

    /// <summary>
    /// 一句受控的补充说明（由 <see cref="RequestLog"/> 产出，不含任何正文）。
    /// </summary>
    public string Detail { get; init; } = "";

    /// <summary>
    /// 这次客户端请求内部发生的上游尝试次数（含本次）。从 1 起。
    ///
    /// 大于 1 说明**换号重试被触发过** —— 面板的日志页要能一眼看出来
    /// 「这条是重试捞回来的」，否则运维会以为池子一切正常。
    /// </summary>
    public int Attempts { get; init; } = 1;

    /// <summary>
    /// 这条是不是这次客户端请求的**终局**记录。
    ///
    /// false = 中间失败的那次尝试（只登记，不参与成功率统计）。
    /// </summary>
    public bool Terminal { get; init; } = true;

    /// <summary>有没有超时/取消导致的不完整记录。</summary>
    public bool Aborted { get; init; }

    /// <summary>
    /// 序列化成**一行** JSON。
    ///
    /// 为什么是一行：日志文件按行读（面板的「日志」页就是这么读的），
    /// 多行 JSON 会把一行日志切成好几行，界面上一团乱。
    ///
    /// ⚠ 用宽松编码器 —— 默认会把中文转义成 <c>\uXXXX</c>，
    /// 日志里全是转义码就没人看得懂了（t1 在 SSE 那边踩过同一个坑）。
    /// </summary>
    public string ToLine() => ToJson().ToJsonString(RelaxedJson);

    /// <summary>结构化形式（测试断言脱敏时直接遍历它，比字符串匹配可靠）。</summary>
    public JsonObject ToJson() => new()
    {
        ["at"] = At,
        ["level"] = Level,
        ["requestId"] = RequestId,
        ["model"] = Model,
        ["usedModel"] = UsedModel,
        ["status"] = Status,
        ["durationMs"] = DurationMs,
        ["accountTag"] = AccountTag,
        ["errorKind"] = ErrorKind,
        ["outcome"] = Outcome,
        ["streamed"] = Streamed,
        ["detail"] = Detail,
        ["attempts"] = Attempts,
        ["terminal"] = Terminal,
        ["aborted"] = Aborted,
    };

    internal static readonly System.Text.Json.JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// 脱敏请求日志。
///
/// ## 它的唯一职责
///
/// 把一次请求的**元数据**（时间、请求 id、模型、状态、耗时、账号标签、
/// 错误分类）记下来，**绝不记**：
///
/// ```
/// ✗ 提示词 / 消息内容    ✗ 响应正文 / 补全内容
/// ✗ accessToken / refreshToken / Authorization 头
/// ✗ 上游错误正文（可能回显请求内容）
/// ```
///
/// ## ⚠ 它和 <see cref="BuddyDeck.Core.UsageLedger"/> 的分工（别合并它们）
///
/// 两者记的是**同一批请求**，但回答的是完全不同的两个问题。
/// 下表与 <c>UsageLedger.cs</c> 里 <c>UsageLedgerRecord</c> 的对照说明
/// **是同一份说法的两面** —— 改这里的话，那边要一起改（两份不一致比
/// 只有一份更坏）：
///
/// ```
/// RequestLog / RequestRecord       「刚才那几次请求怎么样了？」
///   · 环形缓冲（内存，定长，会被覆盖）
///   · 目的：界面上显示「最近请求」列表
///   · 关心：状态码、耗时、outcome —— 给**当下**排障用
///
/// UsageLedgerRecord                「我这个月到底烧了多少额度？」
///   · 追加落盘（JSONL，永不覆盖）
///   · 目的：算总量、算钱、按模型/账号分摊
///   · 关心：token 数、**credit（上游实际扣费）**、缓存命中 —— 给**回头看账**用
/// ```
///
/// **两者不能互相替代**（docs/feature-parity.md §2.4 已核实）：
/// <see cref="RequestLog"/> 是定长环形缓冲 —— 存满就把最老的挤掉，
/// 用它算总量得到的是「最近 500 次」而不是「总共」；它也不落盘，
/// 进程重启即清零。反过来，账本没有状态码/错误分类那些排障字段，
/// 也绝不该每来一个请求就把它整份读出来给界面刷一次。
///
/// 所以：**排障看 <see cref="RequestLog"/>，算钱看 <see cref="BuddyDeck.Core.UsageLedger"/>。**
/// 要加「最近请求」相关的字段加到 <see cref="RequestRecord"/>；
/// 要加成本相关的字段加到 <c>UsageLedgerRecord</c>。两边的字段都不要往对方挪。
///
/// ## 硬性做法：不靠自觉，靠类型
///
/// <see cref="Record"/> 收的是 <see cref="RequestRecord"/> ——
/// 那个类型里没有任何能装下正文的字段（见它的注释）。
/// 想传正文进来，编译都过不了。
///
/// ## 错误分类怎么做到「有信息但不泄露」
///
/// <see cref="RedactError"/> 把上游正文压成**分类词 + 状态码**，
/// 例如：
///
/// ```
/// 输入（可能含提示词回显）：{"displayMsg":"内容不合规：请勿提问 XXX","code":6004}
/// 输出（进日志的）：        "rate_limit"
/// ```
///
/// 也就是说：**原文一个字都不进日志**，调用方要看细节去看上游状态码
/// 和分类 —— 那两样足够定位问题，而它们不是用户内容。
///
/// ## 日志往哪去
///
/// 这里只负责**组装**和**落到内存缓冲**（给面板的「日志」页读）。
/// 真正的文件输出由 Serilog 负责（见 Program.cs）—— 本类通过
/// <see cref="LogEntry.ToLine"/> 交出脱敏后的一行，写文件的是它。
/// </summary>
public sealed partial class RequestLog
{
    /// <summary>内存里保留多少行（给面板读）。</summary>
    private const int BufferCapacity = 500;

    private readonly LogBuffer _buffer;
    private readonly TimeProvider _clock;

    public RequestLog(TimeProvider clock, LogBuffer? buffer = null)
    {
        _clock = clock;
        _buffer = buffer ?? new LogBuffer(BufferCapacity);
    }

    /// <summary>当前缓冲里的全部条目（快照，新的在后）。</summary>
    public IReadOnlyList<LogEntry> Entries => _buffer.Snapshot();

    /// <summary>现在几点（测试用假时钟时也走这里）。</summary>
    public DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>
    /// 把一条请求记录转成脱敏日志条目并记下来。
    ///
    /// **参数类型就是安全边界** —— <see cref="RequestRecord"/> 装不下正文。
    /// </summary>
    public LogEntry Record(RequestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var entry = new LogEntry
        {
            At = record.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Level = LevelFor(record),
            RequestId = record.RequestId,
            Model = record.Model,
            UsedModel = record.UsedModel,
            Status = record.Status,
            DurationMs = record.DurationMs,
            AccountTag = record.AccountTag,
            ErrorKind = record.ErrorKind,
            Outcome = record.Outcome,
            Streamed = record.Streamed,
            Detail = DetailFor(record),
            Attempts = record.Attempts < 1 ? 1 : record.Attempts,
            Terminal = record.Terminal,
            Aborted = string.Equals(record.Outcome, "cancelled", StringComparison.Ordinal),        };

        _buffer.Add(entry);
        return entry;
    }

    /// <summary>
    /// 级别怎么定。
    ///
    /// ```
    /// ok + 2xx                  → Information
    /// 用户主动取消               → Information（不是错误，别刷 Warning）
    /// 4xx（客户端自己的问题）    → Warning
    /// 5xx / 上游挂了             → Error
    /// ```
    ///
    /// ⚠ 中间失败的尝试（<c>Terminal=false</c>）**封顶到 Warning**：
    /// 它只是「这一枪没打中，换一个号继续」，不是这次请求的结论。
    /// 让它按 5xx 记 Error 的话，一次被重试捞回来的请求会在日志里
    /// 留下一条刺眼的 Error —— 而用户其实拿到了完整回答。
    /// 真正的结论由那条终局记录（Information）表示。
    /// </summary>
    private static string LevelFor(RequestRecord record)
    {
        // 用户按「停止生成」是正常操作，不该在日志里变成一片黄色
        if (string.Equals(record.Outcome, "cancelled", StringComparison.Ordinal)) return "Information";

        var severity = SeverityOf(record);

        // 中间尝试降一级：Error → Warning，Warning → Information
        if (!record.Terminal)
        {
            return severity == "Error" ? "Warning" : "Information";
        }

        return severity;
    }

    /// <summary>按 outcome 和状态码算严重度，不含「终局与否」的降级。</summary>
    private static string SeverityOf(RequestRecord record)
    {
        if (string.Equals(record.Outcome, "ok", StringComparison.Ordinal)) return "Information";
        return record.Status >= 500 ? "Error" : "Warning";
    }

    /// <summary>
    /// 一句话说明。**只描述分类、状态和重试次数，不含任何正文。**
    ///
    /// ⚠ 输出全部由**受控片段**拼成：固定的中文短语 + <see cref="RequestRecord"/>
    /// 里的枚举式分类词和数值。没有任何一处拼接调用方传来的自由文本 ——
    /// 这是「只记元数据」在 Detail 这一层的保证。
    /// </summary>
    private static string DetailFor(RequestRecord record)
    {
        if (string.Equals(record.Outcome, "ok", StringComparison.Ordinal))
        {
            var how = record.Streamed ? "流式完成" : "整条完成";

            // 重试捞回来的要显式说出来 —— 否则日志看起来一切正常，
            // 而实际上池子里已经有号在恶化了。
            return record.Attempts > 1 ? $"{how}（换号重试 {record.Attempts} 次后成功）" : how;
        }

        if (string.Equals(record.Outcome, "cancelled", StringComparison.Ordinal))
        {
            return "客户端断开";
        }

        if (!string.IsNullOrEmpty(record.ErrorKind))
        {
            var head = $"上游错误分类 {record.ErrorKind}（上游 http {record.UpstreamStatus}）";

            // 中间失败的尝试标出「还要继续试」；终局的标出总次数。
            return record.Terminal
                ? (record.Attempts > 1 ? $"{head}，共尝试 {record.Attempts} 次" : head)
                : $"{head}，准备换号重试（第 {record.Attempts} 次尝试）";
        }

        return $"请求失败（http {record.Status}）";
    }

    /// <summary>
    /// 把上游错误正文压成**一个分类词**。
    ///
    /// ## 为什么不记原文
    ///
    /// 上游的错误正文经常回显请求内容（「内容不合规：请勿提问 ＜用户的提问＞」），
    /// 直接记下来等于把提示词写进了日志 —— 那正是这条硬要求要挡住的。
    ///
    /// 所以这里只输出分类词（<see cref="UpstreamErrorKind"/> 里的常量），
    /// 需要细节的话看 <see cref="LogEntry.Status"/> 和
    /// <see cref="LogEntry.ErrorKind"/> 就够定位了。
    ///
    /// @param rawUpstreamMessage 上游正文。**读它，但绝不把它写进返回值和日志。**
    /// @returns 一个分类词；判不出来时返回 <see cref="UpstreamErrorKind.Unknown"/>。
    /// </summary>
    public static string RedactError(string? rawUpstreamMessage)
    {
        if (string.IsNullOrWhiteSpace(rawUpstreamMessage)) return UpstreamErrorKind.Unknown;

        // ⚠ 只看它的结构，不取它的内容。哪怕正文里带业务码，
        // 我们也只把它映射成分类词，原文一律丢弃。
        if (rawUpstreamMessage.Contains("6004", StringComparison.Ordinal))
        {
            return UpstreamErrorKind.ModelRateLimited;
        }

        if (rawUpstreamMessage.Contains("6005", StringComparison.Ordinal) ||
            rawUpstreamMessage.Contains("6006", StringComparison.Ordinal))
        {
            return UpstreamErrorKind.RateLimited;
        }

        return UpstreamErrorKind.Unknown;
    }

    /// <summary>
    /// 把任意一段文本压成「可以安全进日志」的形式。
    ///
    /// 用途：万一有调用方手里只有一段自由文本（比如异常消息），
    /// 走这里洗一遍再记 —— 而不是直接塞进日志。
    ///
    /// 做法：
    ///   · 丢掉所有看起来像 token 的长 base64/hex 串
    ///   · 丢掉看起来像 JWT 的三段式
    ///   · 只留前 <paramref name="maxLength"/> 个字符，且**按行截断**
    ///
    /// 注意这**不是**主防线 —— 主防线是 <see cref="RequestRecord"/> 的类型。
    /// 这是给「不得不用自由文本」的少数场合兜底的。
    /// </summary>
    public static string Sanitize(string? text, int maxLength = 96)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var sb = new StringBuilder(Math.Min(text.Length, maxLength));

        foreach (var ch in text)
        {
            if (sb.Length >= maxLength) break;

            // 换行会把一条日志切成两行 —— 面板按行读，必须压掉
            if (ch is '\r' or '\n' or '\t') { sb.Append(' '); continue; }

            sb.Append(ch);
        }

        var result = sb.ToString().Trim();

        // 命中了可疑形状就整体丢掉，不做局部替换 ——
        // 局部替换很容易漏掉一段，整体丢掉最安全。
        if (LooksLikeSecret(result)) return "[已脱敏]";

        return result;
    }

    /// <summary>看起来像不像凭据。</summary>
    private static bool LooksLikeSecret(string text)
    {
        // JWT：三段用点分隔、每段都是 base64url
        if (text.Count(c => c == '.') == 2 && text.Split('.').All(seg => seg.Length >= 8))
        {
            return true;
        }

        // 长且没有空格的连续串（token 的典型形状）
        var longest = 0;
        var current = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { current = 0; continue; }
            current++;
            if (current > longest) longest = current;
        }

        return longest >= 32;
    }
}

/// <summary>
/// 定长的日志缓冲。
///
/// ## 为什么不用 List + 手动裁剪
///
/// 每次 Add 之后 <c>RemoveAt(0)</c> 是 O(n) 的内存搬移，500 行下
/// 每个请求都要搬一次——白烧 CPU。这里用环形数组，O(1)。
///
/// ## 线程安全
///
/// 网关并发记录，而面板的日志页随时可能来读。
/// 写侧持短锁改指针；读侧在锁内**复制一份出来**再放手，
/// 所以读到的永远是完整的一行数组，不会看到写了一半的状态。
/// </summary>
public sealed class LogBuffer
{
    private readonly LogEntry?[] _ring;
    private readonly object _gate = new();
    private int _cursor;
    private int _filled;

    public LogBuffer(int capacity)
    {
        if (capacity < 1) capacity = 1;
        _ring = new LogEntry?[capacity];
    }

    public int Capacity => _ring.Length;

    public int Count
    {
        get { lock (_gate) return _filled; }
    }

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _ring[_cursor] = entry;
            _cursor = (_cursor + 1) % _ring.Length;
            if (_filled < _ring.Length) _filled++;
        }
    }

    /// <summary>取一份快照 —— 新的在后（按时间顺序，方便面板直接往下追加）。</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            var result = new LogEntry[_filled];
            for (var i = 0; i < _filled; i++)
            {
                // 最旧的一个在第 _cursor - _filled 位（环形回绕要 + 长度再取模）
                var index = ((_cursor - _filled + i) % _ring.Length + _ring.Length) % _ring.Length;
                result[i] = _ring[index]!;
            }
            return result;
        }
    }

    /// <summary>取最后 N 条（面板的「读日志尾部」用）。</summary>
    public IReadOnlyList<LogEntry> Tail(int count)
    {
        if (count <= 0) return [];
        var all = Snapshot();
        return all.Count <= count ? all : all.Skip(all.Count - count).ToArray();
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _cursor = 0;
            _filled = 0;
        }
    }
}
