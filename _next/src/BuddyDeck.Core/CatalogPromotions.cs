using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BuddyDeck.Core;

/// <summary>
/// 模型目录的计费与促销解析（M18，对齐旧版 `upstream.js:361-530`）。
///
/// ## 这一层解决什么问题
///
/// 上游的模型目录里混着**给人看的促销信息**，而它们的形状是历史堆积出来的：
///
/// ```
/// credits       "x0.79"  /  "x0.79 credits"  /  "credits"  /  ""
/// tags          ["badge:限时免费", "badge:新用户", "随便别的标签"]
/// promotions    一堆带 validFrom/validUntil/factor/badge/priority 的对象
/// ```
///
/// 界面要显示的是**「现在这一刻这个模型到底多少钱、有没有促销徽标」**。
/// 直接把这些字段丢给界面不行，原因有三个：
///
/// 1. **`credits` 的写法不统一** —— `"x0.79 credits"` 里的单位词是英文，
///    直接显示会把界面钉死在英文上（旧版注释原话：the unit word would pin
///    the display to English）。所以要先归一到 `"x0.79"`。
/// 2. **`free` 不能从原始字符串判** —— 上游把同一个零写成 `x0.00` 和
///    `x0.00 credits` 两种。判 `=== "x0.00"` 会漏掉一半。
/// 3. **促销是时间盒的，而目录是缓存的** —— 目录缓存一整个进程生命周期，
///    促销却会在 `validUntil` 之后失效。**算一次就冻住的话，过期之后界面
///    还会一直声称「限时免费」。** 所以促销必须**每次读的时候按当前时间重算**
///    （见 <see cref="ApplyCurrentPromotion"/>）。
///
/// ## 为什么全是纯函数
///
/// 这一层**不做任何 IO** —— 输入是已经拿到的 <see cref="JsonNode"/>，
/// 输出是记录。这样它可以被穷举测试（各种脏数据形状），也不需要假上游。
/// 拉数据是 <c>UpstreamClient.FetchCatalogAsync</c> 的活，两边职责分开。
/// </summary>
public static partial class CatalogPromotions
{
    /// <summary>
    /// 促销徽标标签的前缀。上游把徽标塞在 `tags` 里，形如 `badge:限时免费`。
    /// </summary>
    public const string BadgePrefix = "badge:";

    /// <summary>
    /// 促销徽标的**第二数据源**路径（仅国内版）。
    ///
    /// ## 为什么需要第二个请求
    ///
    /// 旧版注释说得很清楚（`variants.js:35-38`）：从 plugin 0.6.1 起目录来自
    /// `/v3/config`，而那个文档**不携带 `badge:*` 标签**；徽标只存在于控制台
    /// 文档里。所以国内版刷目录时**读两个文档再 join**。
    ///
    /// ⚠ 这个路径在旧版是 variant 的字段（`variants.js:51`），而新版
    /// <see cref="UpstreamVariant"/> **没有这个字段**。为了不动那个文件
    /// （它不在本任务 inScope），这里先放成常量。**国际版没有这个路径**
    /// —— 它是国内版专属，见 <see cref="IsPromoBadgeAvailable"/>。
    /// </summary>
    public const string PromoBadgePath = "/console/enterprises/personal/models";

    /// <summary>促销徽标是否只在国内版存在。</summary>
    public static bool IsPromoBadgeAvailable(UpstreamVariant variant) => !variant.IsInternational;

    // ── credits 归一 ──────────────────────────────────────────

    /// <summary>`credits` 末尾的单位词（`x0.79 credits` 的那个 credits）。</summary>
    [GeneratedRegex(@"\s+credits?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreditsUnitSuffix();

    /// <summary>整个字符串就是一坨单位词（`"credits"` / `"credit"`）。</summary>
    [GeneratedRegex(@"^credits?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreditsOnly();

    /// <summary>零倍率的两种写法：`x0.00` / `0.00` / `x0.000`。</summary>
    [GeneratedRegex(@"^x?0(\.0+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZeroMultiplier();

    /// <summary>
    /// 把上游的 `credits` 字符串归一到语言中立的形式。
    ///
    /// ```
    /// "x0.79"          → "x0.79"
    /// "x0.79 credits"  → "x0.79"      ← 剥掉单位词（否则界面被钉死在英文）
    /// "  x0.79  "      → "x0.79"
    /// "credits"        → null         ← 只有单位词，等于没给
    /// ""               → null
    /// null             → null
    /// ```
    ///
    /// 对齐旧版 `normalizeCredits`（`upstream.js:365-372`）逐行。
    /// </summary>
    public static string? NormalizeCredits(string? credits)
    {
        if (credits is null) return null;

        var trimmed = credits.Trim();
        if (trimmed.Length == 0) return null;
        if (CreditsOnly().IsMatch(trimmed)) return null;

        var bare = CreditsUnitSuffix().Replace(trimmed, "").Trim();
        return bare.Length == 0 ? null : bare;
    }

    /// <summary>
    /// 归一化后的倍率是不是零。
    ///
    /// **必须拿归一化后的值判**，不能拿原始字符串 —— 上游把同一个零写成
    /// `x0.00` 和 `x0.00 credits` 两种，直接比字符串会漏掉一半
    /// （旧版 `upstream.js:395-398` 专门注释了这条）。
    /// </summary>
    public static bool IsFreeMultiplier(string? normalized)
        => !string.IsNullOrEmpty(normalized) && ZeroMultiplier().IsMatch(normalized);

    // ── 计费 ──────────────────────────────────────────────────

    /// <summary>
    /// 从一个目录行里解析计费信息。
    ///
    /// @param row        目录行（<c>data.models[i]</c>）
    /// @param extraTags  从**另一个文档**（控制台目录）捞回来的徽标标签。
    ///                   国内版需要它，因为 `/v3/config` 不带 `badge:*`。
    ///                   传 null 表示没有第二数据源。
    /// </summary>
    public static CatalogBilling ResolveBilling(JsonObject row, IReadOnlyList<string>? extraTags = null)
    {
        var rawCredits = row["credits"] is JsonValue cv && cv.TryGetValue<string>(out var cs) ? cs : null;
        var credits = rawCredits is not null && rawCredits.Trim().Length > 0 ? rawCredits.Trim() : null;

        var badges = new List<string>();

        // 行自己的标签在前，第二数据源的在后（旧版就是这个顺序）
        CollectBadges(row["tags"] as JsonArray, badges);
        if (extraTags is not null)
        {
            foreach (var tag in extraTags) AddBadge(tag, badges);
        }

        var multiplier = NormalizeCredits(credits);

        return new CatalogBilling
        {
            Credits = credits,
            Badges = badges,
            Free = IsFreeMultiplier(multiplier),
        };
    }

    private static void CollectBadges(JsonArray? tags, List<string> into)
    {
        if (tags is null) return;
        foreach (var node in tags)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var tag)) AddBadge(tag, into);
        }
    }

    /// <summary>
    /// 认一个标签是不是 `badge:` 徽标，是就加进列表。
    ///
    /// 徽标名取前缀之后、**第一个冒号之前**的部分 —— 旧版 `slice(...).split(":")[0]`。
    /// 所以 `badge:限时免费:额外说明` 只取 `限时免费`。
    /// 去重（同一个徽标可能在两个文档里都出现）。
    /// </summary>
    private static void AddBadge(string? tag, List<string> into)
    {
        if (string.IsNullOrEmpty(tag)) return;
        if (!tag.StartsWith(BadgePrefix, StringComparison.OrdinalIgnoreCase)) return;

        var rest = tag[BadgePrefix.Length..];
        var separator = rest.IndexOf(':', StringComparison.Ordinal);
        var label = separator >= 0 ? rest[..separator] : rest;

        if (label.Length > 0 && !into.Contains(label, StringComparer.Ordinal)) into.Add(label);
    }

    // ── 促销 ──────────────────────────────────────────────────

    /// <summary>
    /// 从 `modelPromotions` 数组里挑出**覆盖这个模型**的促销。
    ///
    /// ## 五重校验（缺一不可，照着旧版 `upstream.js:409-435` 抄）
    ///
    /// ```
    /// ① enabled === true              没启用的跳过
    /// ② modelIds 含这个模型 id          否则不是给它的
    /// ③ schedule/discount/badge 都是对象  缺一个就没法算
    /// ④ discount.displayMode === "replace"  —— 只认「替换价」这一种；别的模式
    ///                                     （比如叠加）语义没验证过，宁可不显示
    /// ⑤ validFrom/validUntil 能解析且 end > start
    ///    factor 是有限数且 >= 0
    /// ```
    ///
    /// **为什么校验这么严**：促销直接决定界面上显示「免费」还是「多少钱」。
    /// 一个字段缺失就退化成「不显示促销」而不是「猜一个」—— 显示错的价钱
    /// 比不显示价钱坏得多。
    /// </summary>
    public static IReadOnlyList<ModelPromotion> ParsePromotions(JsonNode? promotions, string modelId)
    {
        if (promotions is not JsonArray array) return [];

        var result = new List<ModelPromotion>();

        foreach (var node in array)
        {
            if (node is not JsonObject item) continue;
            if (Bool(item, "enabled") != true) continue;

            if (item["modelIds"] is not JsonArray modelIds) continue;
            if (!ContainsString(modelIds, modelId)) continue;

            if (item["schedule"] is not JsonObject schedule) continue;
            if (item["discount"] is not JsonObject discount) continue;
            if (item["badge"] is not JsonObject badge) continue;

            if (String(discount, "displayMode") != "replace") continue;

            var start = ParseTime(String(schedule, "validFrom"));
            var end = ParseTime(String(schedule, "validUntil"));
            if (start is null || end is null || end <= start) continue;

            var factorNode = discount["factor"];
            if (!TryDouble(factorNode, out var factor) || factor < 0) continue;

            result.Add(new ModelPromotion
            {
                StartMs = start.Value,
                EndMs = end.Value,
                Factor = factor,
                Label = String(badge, "label"),
                Priority = TryDouble(item["priority"], out var priority) ? priority : 0,
            });
        }

        return result;
    }

    /// <summary>
    /// 按**当前时刻**重算模型的促销。
    ///
    /// ## 为什么必须每次重算，而不是解析时算一次
    ///
    /// 旧版注释（`upstream.js:497-503`）说得很直白：
    ///
    /// > Promotions are time-boxed while the catalog is cached for the life of
    /// > the process, so a frozen "Free now" would keep claiming a discount
    /// > after `validUntil` had passed.
    ///
    /// 目录缓存一整个进程生命周期，促销却会过期。**解析时算一次 = 冻结**，
    /// 过期之后界面还会一直声称免费 —— 用户看到假的「免费」，用完之后
    /// 发现被扣了钱。
    ///
    /// ## 三种返回
    ///
    /// ```
    /// 命中促销        → 用 factor 算出新倍率、加上促销徽标
    /// 没命中但曾有过   → { free: false, rateUnknown: true }  ← 关键
    /// 没命中且从没有   → 原样返回（不编造）
    /// ```
    ///
    /// 中间那条是**最容易被漏掉的**：促销过期后如果原样返回，
    /// 界面会继续显示旧的「免费」。所以显式标成「费率未知」，
    /// 让界面知道「以前有过促销，现在没了，但新价我不知道」。
    /// </summary>
    /// <param name="model">已解析的模型行（带 <see cref="CatalogModelInfo.Promotions"/> 与 <see cref="CatalogModelInfo.Billing"/>）</param>
    /// <param name="nowMs">当前时刻（Unix 毫秒）。**必须传** —— 不传就等于冻结。</param>
    public static CatalogModelInfo ApplyCurrentPromotion(CatalogModelInfo model, long nowMs)
    {
        if (model.Promotions.Count == 0) return model;

        var promotion = model.Promotions
            .OrderByDescending(p => p.Priority)
            .FirstOrDefault(p => nowMs >= p.StartMs && nowMs < p.EndMs);

        if (promotion is null)
        {
            // 过期了 —— 原本有促销才需要标记，否则原样返回（不编造）
            var hadPromo =
                model.Billing.Free ||
                model.Billing.Badges.Count > 0 ||
                model.Promotions.Any(p => p.Factor != 1);

            if (!hadPromo) return model;

            return model with
            {
                Billing = new CatalogBilling
                {
                    Credits = model.Billing.Credits,
                    Badges = [],
                    Free = false,
                    RateUnknown = true,
                },
            };
        }

        var rate = NormalizeCredits(model.Billing.Credits);
        var original = rate is not null && rate.StartsWith('x')
            && double.TryParse(rate[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : double.NaN;

        // factor 0 = 直接免费，不需要原始费率；否则必须有原始费率才算得出来
        if (promotion.Factor != 0 && !double.IsFinite(original)) return model;

        var value = promotion.Factor == 0 ? 0 : original * promotion.Factor;

        var badges = new List<string>(model.Billing.Badges);
        if (promotion.Label.Length > 0) badges.Add(promotion.Label);

        return model with
        {
            Billing = new CatalogBilling
            {
                Credits = "x" + value.ToString("F2", CultureInfo.InvariantCulture),
                Badges = badges,
                Free = value == 0,
            },
        };
    }

    // ── 宽容读法 ───────────────────────────────────────────────

    private static bool? Bool(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue value) return null;
        if (value.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    private static string String(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";

    private static bool ContainsString(JsonArray array, string target)
    {
        foreach (var node in array)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var s)
                && string.Equals(s, target, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue jsonValue) return false;
        if (jsonValue.TryGetValue<double>(out var d) && double.IsFinite(d))
        {
            value = d;
            return true;
        }
        if (jsonValue.TryGetValue<long>(out var l))
        {
            value = l;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 解析一个 ISO 时间串成 Unix 毫秒。
    ///
    /// 读不出来返回 null（**不抛**）—— 促销的排期字段缺失/写错时，
    /// 正确的处置是「这条促销不算数」，而不是让整个目录解析崩掉。
    /// </summary>
    private static long? ParseTime(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at.ToUnixTimeMilliseconds()
            : null;
    }
}

/// <summary>一个模型的计费信息。</summary>
public sealed record CatalogBilling
{
    /// <summary>上游原样的 `credits` 字符串（**未归一化** —— 归一化是显示层的事）。</summary>
    public string? Credits { get; init; }

    /// <summary>促销徽标（已剥掉 `badge:` 前缀）。</summary>
    public IReadOnlyList<string> Badges { get; init; } = [];

    /// <summary>是不是免费（由**归一化后**的倍率判定）。</summary>
    public bool Free { get; init; }

    /// <summary>
    /// 费率未知。
    ///
    /// 只在一种情况下为 true：**曾经有促销、现在过期了**。这时不能沿用旧价
    /// （那会显示已经失效的「免费」），也不该编一个新价，所以如实说「不知道」。
    /// </summary>
    public bool RateUnknown { get; init; }
}

/// <summary>一条促销。</summary>
public sealed record ModelPromotion
{
    /// <summary>生效时刻（Unix 毫秒）。</summary>
    public required long StartMs { get; init; }

    /// <summary>失效时刻（Unix 毫秒）。</summary>
    public required long EndMs { get; init; }

    /// <summary>折扣系数。`0` 表示直接免费；否则是乘在原始倍率上的。</summary>
    public required double Factor { get; init; }

    /// <summary>徽标文案。</summary>
    public string Label { get; init; } = "";

    /// <summary>优先级。同时命中多条时取最大的。</summary>
    public double Priority { get; init; }
}

/// <summary>一个已解析的模型（带计费与促销）。</summary>
public sealed record CatalogModelInfo
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>上下文窗口（token 数）。</summary>
    public int ContextWindow { get; init; }

    /// <summary>最大输出 token 数。</summary>
    public int MaxTokens { get; init; }

    /// <summary>支不支持图片输入。</summary>
    public bool SupportsImages { get; init; }

    public CatalogBilling Billing { get; init; } = new();

    /// <summary>覆盖这个模型的时间盒促销（**未按当前时间过滤** —— 过滤在 <see cref="CatalogPromotions.ApplyCurrentPromotion"/>）。</summary>
    public IReadOnlyList<ModelPromotion> Promotions { get; init; } = [];
}
