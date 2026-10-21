using System.Globalization;
using System.Text.Json.Nodes;

namespace BuddyDeck.Core;

/// <summary>
/// 积分（额度）查询与解析（对齐旧版 `upstream.js:750` `fetchCredits`）。
///
/// ## 上游接口
///
/// ```
/// POST {billingBase}/v2/billing/meter/get-user-resource
/// body: {PageNumber:1, PageSize:100, ProductCode:"p_tcaca", Status:[0,3],
///        PackageEndTimeRangeBegin:"YYYY-MM-DD HH:mm:ss",
///        PackageEndTimeRangeEnd:"YYYY-MM-DD HH:mm:ss"}
/// ```
/// 头用 <see cref="UpstreamHeaders.ApplyBilling"/>（与计费链路同一套）。
///
/// ## 三层嵌套，别搞错
///
/// 这个接口的响应比其他接口**多两层**：
///
/// ```
/// { "code":0, "data": { "Response": { "Data": { "TotalCount", "TotalDosage", "Accounts":[…] } } } }
///        ↑ 信封        ↑ 多出来的一层        ↑ 真正的数据
/// ```
/// 旧版是逐层 `isObject` 兜底取下来的（`upstream.js:780-783`）。这里保留同样的
/// 逐层兜底 —— 少一层就整块读不出来，而**它不会报错，只会返回空列表**。
///
/// ## ⚠ 与旧版的一处**有意差异**：用 `*Precise` 字符串字段
///
/// 旧版只读整数（`CapacityRemain` / `CycleCapacityRemain` …）。
/// 新版读**精确字段**（`CapacityRemainPrecise` 等，字符串形态的小数）。
///
/// 理由是**真实数据**：真机实测（2026-10-07，t57）
/// ```
/// "CapacityRemain": 500, "CapacityRemainPrecise": "500"
/// ```
/// 本次两个值恰好相等（整数），但**上游提供 `Precise` 字段就是为了避免取整**
/// —— 一旦某个包出现小数（如 `"499.5"`），读整数会静默丢精度。
/// 整数版本保留为**兜底**（`Precise` 缺失/读不出来时退回它），所以两种形状都认。
///
/// ## ⚠ `grantSource` 藏在 `AccountAttributes` 里，不是顶层字段
///
/// 真机实测：**28 个包没有任何一个顶层有 `grantSource`**。它是
/// `AccountAttributes` 数组里的一个 `{Key, Value}` 对：
///
/// ```jsonc
/// "AccountAttributes": [
///   {"Key":"payerType","Value":"0","Type":5},
///   {"Key":"payerUin","Value":"330…","Type":5},
///   {"Key":"grantSource","Value":"daily_checkin"}     // ← 在这里
/// ]
/// ```
/// 也就是说**它是一张「属性表」，不是结构字段** —— 将来加新的 Key 不需要改响应形状。
/// 读法必须是「在数组里找 Key==grantSource 的那一项」，**不能按位置取**
/// （真机上第 0 项是 payerType，第 2 项才是 grantSource，而有的包根本没有）。
///
/// 真机实测（同一账号 28 个包）：**8 个带 `grantSource="daily_checkin"`、
/// 剩余量合计 800；另 20 个不带、合计 2400**。
/// </summary>
public static class Credits
{
    /// <summary>积分接口路径。</summary>
    public const string ResourcePath = "/v2/billing/meter/get-user-resource";

    /// <summary>签到的积分来源标记 —— 用它把签到发的积分单独辨出来。</summary>
    public const string DailyCheckinSource = "daily_checkin";

    /// <summary>`AccountAttributes` 里标注来源的那个 Key。</summary>
    public const string GrantSourceKey = "grantSource";

    /// <summary>产品代码（旧版硬编码的 `p_tcaca`）。</summary>
    public const string ProductCode = "p_tcaca";

    /// <summary>旧版查询用的状态过滤：0 与 3 都算"在用的包"。</summary>
    public static readonly IReadOnlyList<int> ActiveStatuses = [0, 3];

    /// <summary>
    /// 旧版查询的到期时间窗口上界偏移（毫秒）。
    ///
    /// ⚠ 这个魔数是从旧版逐字抄来的（<c>upstream.js:774</c> 的 <c>3185136e6</c>），
    /// **不是我们算出来的**。
    ///
    /// ⚠⚠ 单位是**毫秒**，且数值是 <c>3185136e6</c> = 3,185,136,000,000 ——
    /// 约等于 **101 年**（不是 36.86 天！）。
    /// 曾经误抄成 <c>3_185_136_000</c>（少了 3 个零 = 36.86 天），
    /// 导致到期时间在 36.86 天之外的包（如新用户大礼包）被查询窗口滤掉，
    /// 界面少算 500 积分。旧版用 101 年本质是“不过滤到期”，
    /// 抄这个数就是为了和旧版行为一致。
    /// </summary>
    public const long EndTimeRangeOffsetMs = 3_185_136_000_000L;

    /// <summary>
    /// 构造查询体。对齐旧版 `upstream.js:768-775`。
    ///
    /// @param now 查询的"现在"。**显式传入**而不是内部读时钟 ——
    ///            否则这个函数无法测（时间窗口会随执行时刻变）。
    ///
    /// ## ⚠ 时区：这里发的是**裸本地时间字符串**，而且经过了实测确认
    ///
    /// `now` 携带的偏移量**原样**被格式化进字符串（不做任何换算）：
    /// ```
    /// BuildQueryBody(new DateTimeOffset(2026,10,7,12,30,45, TimeSpan.Zero))
    ///   → "2026-10-07 12:30:45"
    /// BuildQueryBody(new DateTimeOffset(2026,10,7,12,30,45, +08:00))
    ///   → "2026-10-07 12:30:45"     ← 同样的字面量
    /// ```
    ///
    /// 也就是说 **+00:00 传进去与 +08:00 传进去会得到同一个字符串**
    /// （只要本地时分秒相同）。这正是旧版 JS 的行为
    /// （`date.getHours()` 取的是本地时间）—— 两边都是"把本地时刻写成裸字符串"。
    ///
    /// ### 这是"巧合对"还是"契约对"？—— **实测判定：契约对（上游按裸字符串比较）**
    ///
    /// t57 用真实账号做了边界实验。已知有一个包在 `2026-10-08 08:21:25` 到期，
    /// 把查询窗口的上界卡在它两侧：
    /// ```
    /// end = "2026-10-08 08:21:24"  → TotalCount = 4     ← 不含它
    /// end = "2026-10-08 08:21:26"  → TotalCount = 5     ← 含它
    /// ```
    /// **边界严丝合缝落在字面量上，没有任何时区平移。**
    ///
    /// ⇒ 上游就是**拿字符串当本地时间直接比大小**，它不解析时区、
    ///   我们发什么字面量它就按什么字面量理解。
    /// ⇒ 所以「发本地时间」是**契约对**，不是巧合：
    ///   只要**发送端与接收端对"现在是几点"的理解一致**就成立。
    ///
    /// ### 那风险在哪（为什么要写这一段）
    ///
    /// 风险**不在时区换算**，而在**"本地时间"由谁决定**：
    /// 如果用户的系统时区不是 +08:00，`DateTimeOffset.Now` 得到的是**他的**
    /// 本地时刻，与上游服务器的时刻**差好几个小时**，窗口就会整体偏移。
    ///
    /// 但影响面很小且**偏保守**：
    /// · 窗口是 `[now, now + 36.86 天]`，宽度近 37 天
    /// · 偏差最多 ±14 小时，远小于窗口宽度
    /// · 后果是**可能多/少列出一两个刚到期或即将到期的包**，
    ///   而不是"查不到积分"
    ///
    /// ⇒ 结论：**保持与旧版一致的本地格式化**（契约对，且偏差影响极小），
    ///   但如果将来要做严格的到期告警，应该改用**服务器时刻**
    ///   （或至少固定成 +08:00）而不是调用方的本地时刻。
    ///   **这条记录在此，免得下一个人把"本地格式化"当成随手写的。**
    /// </summary>
    public static JsonObject BuildQueryBody(DateTimeOffset now)
    {
        var format = (DateTimeOffset value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        return new JsonObject
        {
            ["PageNumber"] = 1,
            ["PageSize"] = 100,
            ["ProductCode"] = ProductCode,
            ["Status"] = new JsonArray([.. ActiveStatuses.Select(s => (JsonNode)s)]),
            ["PackageEndTimeRangeBegin"] = format(now),
            ["PackageEndTimeRangeEnd"] = format(now.AddMilliseconds(EndTimeRangeOffsetMs)),
        };
    }

    /// <summary>
    /// 解析积分响应。
    ///
    /// @returns 解析结果。**任何形状不对都返回一个空结果而不是抛** ——
    ///          积分查不出来只该让余额显示为 0，不该让面板崩掉。
    /// </summary>
    public static CreditsSnapshot Parse(JsonNode? root)
    {
        // 逐层兜底（少一层只会返回空，不会报错，所以每层都要单独判）
        if (root is not JsonObject document) return CreditsSnapshot.Empty;
        if (document["data"] is not JsonObject data) return CreditsSnapshot.Empty;
        if (data["Response"] is not JsonObject response) return CreditsSnapshot.Empty;
        if (response["Data"] is not JsonObject inner) return CreditsSnapshot.Empty;

        var rawAccounts = inner["Accounts"] as JsonArray;

        var packages = new List<CreditPackage>();
        if (rawAccounts is not null)
        {
            foreach (var node in rawAccounts)
            {
                if (node is not JsonObject raw) continue;
                packages.Add(ReadPackage(raw));
            }
        }

        return new CreditsSnapshot
        {
            TotalCount = IntOf(inner, "TotalCount"),
            TotalDosage = Precise(inner, "TotalDosage"),
            Packages = packages,
        };
    }

    /// <summary>读单个资源包。</summary>
    private static CreditPackage ReadPackage(JsonObject raw)
    {
        var size = Precise(raw, "CapacitySizePrecise", "CapacitySize");
        var remain = Precise(raw, "CapacityRemainPrecise", "CapacityRemain");
        var used = Precise(raw, "CapacityUsedPrecise", "CapacityUsed");

        // 周期字段单独读 —— 它们与"总量/剩余"是**两套账**：
        //   Capacity*     整个包的账
        //   CycleCapacity* 当前周期的账
        // 旧版在两者之间做选择（upstream.js:793-796），这里把两套都留着，
        // 让调用方自己决定看哪一套。
        var cycleSize = Precise(raw, "CycleCapacitySizePrecise", "CycleCapacitySize");
        var cycleRemain = Precise(raw, "CycleCapacityRemainPrecise", "CycleCapacityRemain");
        var cycleUsed = Precise(raw, "CycleCapacityUsedPrecise", "CycleCapacityUsed");

        var packageName = StringOf(raw, "PackageName");
        if (packageName.Length == 0) packageName = "(unnamed)";

        return new CreditPackage
        {
            PackageName = packageName,
            PackageCode = StringOf(raw, "PackageCode"),
            ProductName = StringOf(raw, "ProductName"),
            CapacityUnit = StringOf(raw, "CapacityUnit"),
            Size = size,
            Remain = remain,
            Used = used,
            CycleSize = cycleSize,
            CycleRemain = cycleRemain,
            CycleUsed = cycleUsed,
            CycleStartTime = StringOf(raw, "CycleStartTime"),
            CycleEndTime = StringOf(raw, "CycleEndTime"),
            Status = IntOf(raw, "Status"),
            GrantSource = ReadGrantSource(raw),
        };
    }

    /// <summary>
    /// 从 `AccountAttributes` 里找 `grantSource`。
    ///
    /// **按 Key 找，不按位置** —— 真机上第 0 项是 `payerType`、
    /// 有的是第 2 项才轮到它，而且**大部分包根本没有这个 Key**。
    /// 按位置取会在真机上读到 `"0"`（payerType 的值）并把它当来源。
    /// </summary>
    private static string ReadGrantSource(JsonObject raw)
    {
        if (raw["AccountAttributes"] is not JsonArray attributes) return "";

        foreach (var node in attributes)
        {
            if (node is not JsonObject attribute) continue;
            if (!string.Equals(StringOf(attribute, "Key"), GrantSourceKey, StringComparison.Ordinal)) continue;

            return StringOf(attribute, "Value");
        }

        return "";
    }

    // ── 宽容读法 ───────────────────────────────────────────────
    //
    // 与 Checkin/CatalogPromotions 同一套约定：上游是"扒出来的接口"，
    // 类型说变就变，读不出就给安全默认，**绝不抛**。

    /// <summary>
    /// 读一个精确数值：优先 `*Precise`（字符串小数），退回整数兜底字段。
    ///
    /// ```
    /// "500"    → 500
    /// "499.5"  → 499.5
    /// 500      → 500      （Precise 缺失时读整数兜底）
    /// "abc"    → 读兜底字段；兜底也没有才 0
    /// 缺失     → 读兜底字段；兜底也没有才 0
    /// ```
    ///
    /// ## ⚠ 两个踩过的坑（都在这里）
    ///
    /// **坑 1：Precise 存在但读不出来时要继续试兜底**，不能直接返回 0 —— 否则
    /// `{"CapacityRemainPrecise":"坏值","CapacityRemain":77}` 得 0 而不是 77。
    /// **上游把同一个量发了两种形态，一种坏了还有另一种可用。**
    ///
    /// **坑 2：`JsonValue.TryGetValue&lt;double&gt;` 对「整数值」返回 `false`。**
    /// 这是 .NET 的既定行为（实测确认）：
    /// ```
    /// JsonValue.Create(42)  → GetValueKind() = Number
    ///                          TryGetValue<double>()  = false   ← ！！
    ///                          TryGetValue<int>()     = true
    /// ```
    /// 所以**只写 `TryGetValue<double>` 会漏掉所有整数** ——
    /// 而真机的 `CapacityRemain`/`CapacitySize` 恰好**全是整数**。
    /// 也就是说：只按 double 读，兜底字段在真机上**永远读不出来**，
    /// 而且**返回 0 不报错**（正是本项目一直在防的那类静默失败）。
    ///
    /// 修法：按 `int → long → double → string` 逐个试。
    /// </summary>
    internal static double Precise(JsonObject obj, string preciseName, string? fallbackName = null)
    {
        if (TryReadNumber(obj[preciseName], out var preciseValue)) return preciseValue;

        if (fallbackName is null) return 0;

        return TryReadNumber(obj[fallbackName], out var fallbackValue) ? fallbackValue : 0;
    }

    /// <summary>
    /// 把一个 JSON 值读成 double，**按实际的后备类型逐个试**。
    ///
    /// 顺序是刻意的：`int` → `long` → `double` → `string`。
    /// 不能只试 `double`（见上面坑 2），也不能只试 `string`
    /// （`TryGetValue&lt;string&gt;` 对数字返回 false）。
    /// </summary>
    private static bool TryReadNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue json) return false;

        if (json.TryGetValue<int>(out var asInt)) { value = asInt; return true; }
        if (json.TryGetValue<long>(out var asLong)) { value = asLong; return true; }
        if (json.TryGetValue<double>(out var asDouble) && double.IsFinite(asDouble)) { value = asDouble; return true; }

        // 字符串形态（`*Precise` 字段就是这种）。读不出来才算失败，
        // 这样"Precise 是坏值"能自然落到兜底字段上。
        if (json.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static int IntOf(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue value) return 0;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l)) return l is >= int.MinValue and <= int.MaxValue ? (int)l : 0;
        if (value.TryGetValue<string>(out var s)
            && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return 0;
    }

    private static string StringOf(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";
}

/// <summary>一个资源包（积分批次）。</summary>
public sealed record CreditPackage
{
    /// <summary>包名。上游没给时为 `(unnamed)`（与旧版一致）。</summary>
    public required string PackageName { get; init; }

    /// <summary>包代码。</summary>
    public string PackageCode { get; init; } = "";

    /// <summary>产品名。</summary>
    public string ProductName { get; init; } = "";

    /// <summary>单位（真机是 `credits`）。</summary>
    public string CapacityUnit { get; init; } = "";

    /// <summary>包总量（**精确字段**）。</summary>
    public double Size { get; init; }

    /// <summary>包剩余量（**精确字段**）。</summary>
    public double Remain { get; init; }

    /// <summary>包已用量（**精确字段**）。</summary>
    public double Used { get; init; }

    /// <summary>当前周期的总量。</summary>
    public double CycleSize { get; init; }

    /// <summary>当前周期的剩余量。</summary>
    public double CycleRemain { get; init; }

    /// <summary>当前周期的已用量（对应上游 <c>CycleCapacityUsed</c>）。</summary>
    public double CycleUsed { get; init; }

    /// <summary>
    /// 周期开始时间。**上游原样字符串**（`"2026-10-01 00:00:00"` —— 空格分隔、无时区）。
    ///
    /// 原样保留的理由与 `CheckinStatus.StartTime` 一致：**它是"上游说是几点"**，
    /// 解析成 DateTime 会引入时区假设，而那个假设我们没验证过。
    /// 需要比较时用 <see cref="CreditsSnapshot.PackagesExpiringWithin"/>（它内部解析）。
    /// </summary>
    public string CycleStartTime { get; init; } = "";

    /// <summary>周期结束时间（同样是上游原样字符串）。**到期的判据就是它。**</summary>
    public string CycleEndTime { get; init; } = "";

    /// <summary>状态码（真机是 0）。</summary>
    public int Status { get; init; }

    /// <summary>
    /// 来源（来自 `AccountAttributes` 的 `grantSource`）。
    /// 真机取值：`"daily_checkin"` 或空串（大部分包没有这个属性）。
    /// </summary>
    public string GrantSource { get; init; } = "";

    /// <summary>
    /// 这个包的**有效剩余** —— 界面和汇总用它，而不是直接读 <see cref="Remain"/>。
    ///
    /// ## 为什么不是 <c>Remain</c>
    ///
    /// 复刻旧版 <c>upstream.js:793-796</c> 的选择逻辑：一个包可能有两套账
    ///   · <c>CapacityRemain</c>     整个包的剩余
    ///   · <c>CycleCapacityRemain</c> 当前周期的剩余
    /// 旧版的规矩：有周期容量（<c>CycleCapacitySize &gt; 0</c>）就用周期剩余；
    /// 否则才退回整个包的剩余。直接读 <c>Remain</c> 会漏掉周期账，
    /// 于是少算（真机实测少 500）。
    /// </summary>
    public double EffectiveRemain
    {
        get
        {
            var cycleSize = CycleSize;
            var cycleRemain = CycleRemain;
            var cycleUsed = CycleUsed;

            double remain;
            if (cycleSize > 0) remain = cycleRemain;
            else if (cycleRemain > 0 || cycleUsed > 0) remain = cycleRemain;
            else remain = Remain;

            return remain < 0 ? 0 : remain;
        }
    }

    /// <summary>是不是签到发的积分。</summary>
    public bool IsDailyCheckin =>
        string.Equals(GrantSource, Credits.DailyCheckinSource, StringComparison.Ordinal);
}

/// <summary>一次积分查询的结果。</summary>
public sealed record CreditsSnapshot
{
    /// <summary>空结果（解析失败时）。</summary>
    public static readonly CreditsSnapshot Empty = new();

    /// <summary>上游报的包总数（`TotalCount`）。**可能大于 <see cref="Packages"/>.Count** —— 分页用。</summary>
    public int TotalCount { get; init; }

    /// <summary>上游报的总量（`TotalDosage`）。</summary>
    public double TotalDosage { get; init; }

    /// <summary>资源包列表（上游原序）。</summary>
    public IReadOnlyList<CreditPackage> Packages { get; init; } = [];

    /// <summary>
    /// 总剩余。
    ///
    /// ⚠ **自己加，不用上游的 <c>TotalDosage</c>** ——
    /// 两者语义不同：<c>TotalDosage</c> 是“发过的总量”，剩余合计才是“还能用的量”。
    /// 旧版同样是自己累加（<c>upstream.js:785-798</c> 的 <c>total += remain</c>）。
    ///
    /// ⚠ 每个包用 <see cref="CreditPackage.EffectiveRemain"/>（周期账优先），
    /// 而不是 <see cref="CreditPackage.Remain"/> —— 后者只认整个包的剩余，会少算。
    /// </summary>
    public double TotalRemaining => Packages.Sum(p => p.EffectiveRemain);

    /// <summary>签到发的积分还剩多少。</summary>
    public double DailyCheckinRemaining => Packages.Where(p => p.IsDailyCheckin).Sum(p => p.EffectiveRemain);

    /// <summary>签到发的包数量。</summary>
    public int DailyCheckinPackageCount => Packages.Count(p => p.IsDailyCheckin);

    /// <summary>
    /// 按到期时间**升序**排序（最快到期的在前）。
    ///
    /// ## 为什么按"最快到期优先"
    ///
    /// 界面要显示「积分余额 + 到期倒计时」，而**积分是先到期先用**的
    /// （上游扣减按批次到期顺序）。所以"下一次会过期多少"这件事，
    /// 只有把最快到期的排在前面才看得出来。
    ///
    /// 解析不出时间的包**排在最后**（它们没有倒计时可言），
    /// 而不是当成"最早到期" —— 后者会制造假的紧迫感。
    /// </summary>
    public IReadOnlyList<CreditPackage> ByExpiryAscending()
        => [.. Packages
            .OrderBy(p => ParseTime(p.CycleEndTime) ?? long.MaxValue)
            .ThenBy(p => p.PackageName, StringComparer.Ordinal)];

    /// <summary>
    /// 在 <paramref name="now"/> 之后 <paramref name="days"/> 天内到期的包数量。
    ///
    /// 供仪表盘告警用（「有 N 个资源包即将到期」）。
    ///
    /// ## 两个边界决定
    ///
    /// · **已经过期的包不计入**（`end &lt; now`）—— 它们不是"即将到期"，
    ///   而是"已经没了"。把它们算进来会让告警永远响着。
    /// · **解析不出时间的包不计入** —— 宁可漏报，不可错报。
    /// </summary>
    public int PackagesExpiringWithin(int days, DateTimeOffset now)
    {
        var limit = now.AddDays(days).ToUnixTimeMilliseconds();
        var floor = now.ToUnixTimeMilliseconds();

        return Packages.Count(p =>
        {
            var end = ParseTime(p.CycleEndTime);
            return end is not null && end.Value >= floor && end.Value <= limit;
        });
    }

    /// <summary>在 <paramref name="now"/> 之后 days 天内到期的包（按到期升序）。</summary>
    public IReadOnlyList<CreditPackage> ExpiringWithin(int days, DateTimeOffset now)
    {
        var limit = now.AddDays(days).ToUnixTimeMilliseconds();
        var floor = now.ToUnixTimeMilliseconds();

        return [.. Packages
            .Where(p =>
            {
                var end = ParseTime(p.CycleEndTime);
                return end is not null && end.Value >= floor && end.Value <= limit;
            })
            .OrderBy(p => ParseTime(p.CycleEndTime) ?? long.MaxValue)];
    }

    /// <summary>
    /// 解析上游那个时间串。
    ///
    /// 形状是 `"2026-10-31 23:59:59"` —— **空格分隔、不带时区**。
    ///
    /// ⚠ 无时区 ⇒ 按 **UTC** 解释（`AssumeUniversal`）。
    /// 上游大概率是北京时间，但我们**没有证据**，而擅自按 +08:00 解释
    /// 会让"还有几小时到期"整体差 8 小时。
    /// 所以：统一按 UTC 解释，**并在文档里写明这个假设** ——
    /// 真要精确到小时，得先用真机数据验证上游时区（目前没验过）。
    ///
    /// 解析不出来返回 null（**不抛**）。
    /// </summary>
    internal static long? ParseTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }

    /// <summary>
    /// 把上游那个时间串解析成 Unix 毫秒（供测试与调用方复用）。
    ///
    /// 与 <see cref="ParseTime"/> 同一实现，只是**公开**出来 ——
    /// 界面要画「到期倒计时」就得解析它，而不该各自再写一份
    /// （写两份就会出现"一边按 UTC、一边按本地"的不一致）。
    /// </summary>
    public static long? TryParseTime(string value) => ParseTime(value);
}
