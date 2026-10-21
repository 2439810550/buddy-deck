using System.Globalization;

namespace BuddyDeck.App.Services;

/// <summary>
/// 「用量统计」页的**纯逻辑**：时间范围、分桶、聚合、格式化。
///
/// ## 为什么它独立于界面
///
/// 照 `AccountPresentation` / `TimeText` / `ImageInput` / `AccountImport`
/// 的先例（这些文件的注释都写明「逻辑放这里是为了能脱离界面测」）。
///
/// 更具体的一条理由：`StatsViewModel` 要用 `[ObservableProperty]` /
/// `[RelayCommand]`，而 **CommunityToolkit 的源生成器不会为
/// `&lt;Compile Include&gt;` 链接进测试程序集的文件运行** ——
/// 链接 ViewModel 必然 CS0246 + CS0759（本项目已实测确认多次）。
/// 所以**要测的逻辑必须放这里**，VM 只留绑定 glue。
///
/// ## 三个维度从哪里来
///
/// 全部来自**已有的**数据源，本页不新增任何数据源：
/// ```
/// 请求数 / Token  / 积分   ← UsageLedger 的 JSONL 账本（t24）
/// 成功率 / 尝试次数        ← /metrics（t16 口径）
/// 余额                    ← FetchCreditsAsync（t65）
/// ```
/// 这个文件只处理**账本**那三个维度 —— 它是最全的那个来源
/// （账本里本来就带着 token 与 credit），所以不需要再和 /metrics 对账。
/// </summary>
public static class StatsPresentation
{
    // ══════════════════════════════════════════════════════════
    // 时间范围
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 时间范围。
    ///
    /// ## 为什么是「向后看 N 天」而不是「自然日」
    ///
    /// 「近 7 天」按**过去 168 小时**算，不按「本周一到今天」：
    ///   · 用户问「我最近花了多少」时想的是滚动窗口，不是日历周
    ///   · 自然周/自然月会在周一/月初**突然清零**，看起来像数据丢了
    ///
    /// **唯一的例外是「本月」** —— 那个词的字面意思就是自然月，
    /// 用户拿它和账单对账，滚动 30 天对不上。
    /// </summary>
    public enum Range
    {
        /// <summary>今天（本地时间的 00:00 起）。</summary>
        Today = 0,

        /// <summary>近 7 天（滚动 168 小时）。</summary>
        Last7Days = 1,

        /// <summary>近 30 天（滚动 720 小时）。</summary>
        Last30Days = 2,

        /// <summary>本月（本地时间的 1 号 00:00 起）—— 唯一按自然月的。</summary>
        ThisMonth = 3,
    }

    /// <summary>全部范围（界面的切换按钮按这个顺序排）。</summary>
    public static IReadOnlyList<Range> AllRanges { get; } =
        [Range.Today, Range.Last7Days, Range.Last30Days, Range.ThisMonth];

    /// <summary>范围的中文名（直接进界面）。</summary>
    public static string Label(Range range) => range switch
    {
        Range.Today => "今天",
        Range.Last7Days => "近 7 天",
        Range.Last30Days => "近 30 天",
        Range.ThisMonth => "本月",
        _ => "未知",
    };

    /// <summary>
    /// 算范围的起点。
    ///
    /// ⚠ **用本地时间算，不用 UTC**：账本里的 `Ts` 是 Unix 毫秒（UTC 无关），
    /// 但「今天」对用户来说是**他所在时区的今天**。
    /// 用 UTC 的话，东八区的用户在早上 8 点之前会看到「今天」是空的
    /// —— 那正是最常看的时候。
    /// </summary>
    public static DateTimeOffset StartOf(Range range, DateTimeOffset now)
    {
        var local = now.ToLocalTime();

        return range switch
        {
            Range.Today => new DateTimeOffset(local.Date, local.Offset),

            Range.Last7Days => now - TimeSpan.FromDays(7),
            Range.Last30Days => now - TimeSpan.FromDays(30),

            // 本月：本地时间当月 1 号 00:00
            Range.ThisMonth => new DateTimeOffset(
                new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified),
                local.Offset),

            _ => now - TimeSpan.FromDays(7),
        };
    }

    // ══════════════════════════════════════════════════════════
    // 聚合结果
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 聚合只用得到的**那几个字段**，抽成一个窄接口。
    ///
    /// ## ⚠ 为什么不直接用 `UsageLedgerRecord`（Core 的类型）
    ///
    /// 因为本测试项目**刻意不 `ProjectReference` 任何产品项目** ——
    /// 它用 `&lt;Compile Include&gt;` 逐文件链接纯逻辑源码。
    /// 那个设计的前提是「被链接的文件只依赖 BCL」。
    ///
    /// `StatsPresentation` 如果直接吃 `UsageLedgerRecord`，就得给测试项目
    /// 加一条对 `BuddyDeck.Core` 的 `ProjectReference` —— **那会破坏
    /// 这个项目最值钱的性质**：它现在是「不引用 App、不引用 Core，
    /// 界面代码写坏了也不影响它跑」。
    ///
    /// 所以这里定义一个**结构上兼容**的窄接口：它只有聚合真正用到的
    /// 7 个成员（时间戳 + outcome + 三个 token 字段 + credit）。
    /// `UsageLedgerRecord` 是 `record`，**属性名与类型逐一对上**，
    /// 所以调用方用 `Totals Aggregate` 时零摩擦 —— 在
    /// `StatsViewModel` 里那一侧（App 项目，能看见 Core）直接传即可。
    ///
    /// 这是**依赖倒置**在这里的具体用法：不是"为了解耦而解耦"，
    /// 而是"让可测的那一份不依赖不可测的那一份"。
    /// </summary>
    public interface ILedgerRow
    {
        /// <summary>账目发生时刻（Unix 毫秒）。</summary>
        long Ts { get; }

        /// <summary>ok / failed / cancelled。</summary>
        string Outcome { get; }

        /// <summary>输入 token 数（上游没给时为 null）。</summary>
        long? PromptTokens { get; }

        /// <summary>输出 token 数（上游没给时为 null）。</summary>
        long? CompletionTokens { get; }

        /// <summary>总 token 数（上游没给时为 null）。</summary>
        long? TotalTokens { get; }

        /// <summary>上游实际扣费。</summary>
        double? Credit { get; }
    }

    /// <summary>三个维度的聚合结果（一个时间范围一份）。</summary>
    public sealed record Totals
    {
        /// <summary>范围内一共多少条账目。</summary>
        public int Requests { get; init; }

        /// <summary>其中成功的（`outcome == "ok"`）。</summary>
        public int Ok { get; init; }

        /// <summary>失败的（`outcome == "failed"`）。</summary>
        public int Failed { get; init; }

        /// <summary>被取消的（`outcome == "cancelled"`）。</summary>
        public int Cancelled { get; init; }

        /// <summary>输入 token 合计。</summary>
        public long PromptTokens { get; init; }

        /// <summary>输出 token 合计。</summary>
        public long CompletionTokens { get; init; }

        /// <summary>总 token 合计。</summary>
        public long TotalTokens { get; init; }

        /// <summary>积分（credit）合计。</summary>
        public double Credit { get; init; }

        /// <summary>
        /// 有多少条账目**缺** token 数。
        ///
        /// ⚠ 这个字段存在的理由：缺失与 0 **不是一回事**。
        /// 上游有些响应不带 usage，那时把 token 当 0 会让总量偏低，
        /// 而且**看不出来**。把缺失数报出来，用户才知道
        /// 「这个合计是部分的」。
        /// </summary>
        public int RowsMissingTokens { get; init; }

        /// <summary>有没有数据（空账本时界面要走空态）。</summary>
        public bool IsEmpty => Requests == 0;

        /// <summary>成功率（0–100）。没有请求时返回 null 而不是 0 —— 见 <see cref="PercentText"/>。</summary>
        public double? SuccessRate => Requests == 0 ? null : Ok * 100.0 / Requests;
    }

    /// <summary>
    /// 按范围过滤并聚合。
    ///
    /// @param records 账本里的全部记录（顺序无所谓，内部不依赖顺序）。
    /// @param range   时间范围。
    /// @param now     当前时刻（注入是为了可测 —— 不要在这里读 DateTimeOffset.Now）。
    /// </summary>
    public static Totals Aggregate(
        IEnumerable<ILedgerRow>? records,
        Range range,
        DateTimeOffset now)
    {
        if (records is null) return new Totals();

        var startMs = StartOf(range, now).ToUnixTimeMilliseconds();

        var requests = 0;
        var ok = 0;
        var failed = 0;
        var cancelled = 0;
        long prompt = 0, completion = 0, total = 0;
        double credit = 0;
        var missingTokens = 0;

        foreach (var record in records)
        {
            if (record is null) continue;

            // 未来时间的记录也收进来 —— 那不是"数据脏"，是**时钟偏差**
            // （网关和界面在不同机器，或者系统时间被调过）。
            // 丢掉的话用户会看到「明明有请求但统计里没有」。
            if (record.Ts < startMs) continue;

            requests++;

            switch (record.Outcome)
            {
                case "ok": ok++; break;
                case "failed": failed++; break;
                case "cancelled": cancelled++; break;
                // 未知 outcome：不计入任何一档，但**计入总数** ——
                // 它确实发生过。丢掉会让「请求数」和账本行数对不上。
                default: break;
            }

            // ⚠ token 用 `?? 0` 累加，但**同时记下缺了几条**（见 RowsMissingTokens）。
            //   直接 `+= record.PromptTokens ?? 0` 而不记数的话，
            //   「上游没给 usage」会被静默算成 0，用户看不出合计是部分的。
            var rowHasTokens = false;

            if (record.PromptTokens is { } p) { prompt += p; rowHasTokens = true; }
            if (record.CompletionTokens is { } c) { completion += c; rowHasTokens = true; }
            if (record.TotalTokens is { } t) { total += t; rowHasTokens = true; }

            if (!rowHasTokens) missingTokens++;

            if (record.Credit is { } cr) credit += cr;
        }

        // total 缺失时用前两者补 —— 上游有些响应只给 prompt/completion。
        // ⚠ 只在 total 完全没被填过时才补，避免把已有的 total 翻倍。
        if (total == 0 && (prompt > 0 || completion > 0)) total = prompt + completion;

        return new Totals
        {
            Requests = requests,
            Ok = ok,
            Failed = failed,
            Cancelled = cancelled,
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = total,
            Credit = credit,
            RowsMissingTokens = missingTokens,
        };
    }

    // ══════════════════════════════════════════════════════════
    // 格式化
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 大数字说成人话：`12345` → `1.2 万`。
    ///
    /// ## 为什么要在 1 万处切换
    ///
    /// token 的数轻易上百万。原样显示的话：
    ///   · `1284932` 这一串用户得数位数
    ///   · 仪表盘的格子放不下
    /// 用「万」之后 `128.5 万` 一眼就有量级感。**不精确到个位是有意的** ——
    /// 看用量统计时用户要的是量级，不是个位数。
    ///
    /// 小于 1 万时保留原样（那时的精确值是有意义的，比如 1200 个 token）。
    /// </summary>
    public static string FormatTokens(long value) => value switch
    {
        // 负数不该出现，但真出现了也要能显示（别抛）
        < 0 => "-" + FormatTokens(-value),
        < 10_000 => value.ToString("N0", CultureInfo.InvariantCulture),
        < 100_000_000 => Trim(value / 10_000.0) + " 万",
        _ => Trim(value / 100_000_000.0) + " 亿",
    };

    /// <summary>
    /// 积分说成人话。
    ///
    /// 上游的 credit 是浮点（0.22 / 0.03），**小数位不能丢** ——
    /// 它对账时就是那个精度。但也不要 `0.22000000000000003` 那种浮点尾巴。
    /// </summary>
    public static string FormatCredit(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "—";

        return value switch
        {
            < 0 => "-" + FormatCredit(-value),
            < 1000 => value.ToString("0.##", CultureInfo.InvariantCulture),
            < 100_000 => Trim(value / 1000.0) + " 千",
            _ => Trim(value / 10_000.0) + " 万",
        };
    }

    /// <summary>
    /// 百分比。
    ///
    /// ⚠ `null`（没有请求）返回 `—` 而**不是 `0%`**：
    /// 0% 的意思是「全都失败了」，而实际是「什么都没发生」。
    /// 这两个对用户是完全相反的信息 —— 空账本上显示 0% 会让他以为
    /// 网关全挂了。
    /// </summary>
    public static string PercentText(double? percent)
        => percent is { } p ? p.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";

    /// <summary>去掉小数末尾的 0：`1.20` → `1.2`、`1.00` → `1`。</summary>
    private static string Trim(double value)
    {
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return rounded == Math.Floor(rounded)
            ? ((long)rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.#", CultureInfo.InvariantCulture);
    }

    // ══════════════════════════════════════════════════════════
    // 空态
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 空账本时的文案。
    ///
    /// 「暂无数据」是**最没用的一种空态** —— 它没说为什么空、也没说怎么办。
    /// 这里的文案要说清两件事：
    ///   · 为什么是空的（还没发过请求 / 这个范围里没有）
    ///   · 怎么看得到数据（去聊天测试发一句）
    ///
    /// @param hasAnyRecords 账本里**总共**有没有记录（跨所有范围）。
    ///        「从没发过请求」和「这段时间没有」是两种空态，文案不同。
    /// </summary>
    public static string EmptyHint(Range range, bool hasAnyRecords)
    {
        if (!hasAnyRecords)
        {
            return "还没有任何用量记录。去「聊天测试」发一句话，这里就会有数据了。";
        }

        return $"{Label(range)}没有请求记录。换一个时间范围看看 —— 更早的数据还在账本里。";
    }

    /// <summary>
    /// 「统计的是哪段时间」的那句说明（放在标题旁边）。
    ///
    /// 用户看到「今天 0 个请求」时的第一反应是「坏了」，
    /// 所以要说清这个「今天」是从**几点**算起的。
    /// </summary>
    public static string RangeCaption(Range range, DateTimeOffset now)
    {
        var start = StartOf(range, now);
        var local = start.ToLocalTime();

        return range switch
        {
            Range.Today => $"今天 00:00 起（{local:MM-dd HH:mm}）",
            Range.ThisMonth => $"本月 1 号起（{local:MM-dd HH:mm}）",

            // ⚠ 这里**不能**用 `Label(range)[1..]` 去掉「近」字 ——
            //   「近 7 天」去掉首字是「 7 天」（带前导空格），
            //   拼出来就是「近  7 天」两个空格。**测试抓到的。**
            //   直接把范围名整个用上就对了。
            _ => $"{Label(range)}（{local:MM-dd HH:mm} 起）",
        };
    }
}
