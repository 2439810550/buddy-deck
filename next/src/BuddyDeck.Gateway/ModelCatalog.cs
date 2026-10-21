using System.Text.Json.Nodes;
using BuddyDeck.Core;

namespace BuddyDeck.Gateway;

/// <summary>
/// 模型目录里的一项。
///
/// ## 为什么元数据字段全是**可空**的
///
/// 上游目录里每个模型带一堆元数据（计费倍率、推理档位、上下文窗口…），
/// 但它们**不是每个模型都有** —— 真机上有些行没有 `reasoning`、
/// 有些没有 `billing`。
///
/// ```
/// 缺失 ⇒ null → /v1/models 里**省略这个键**（不是填 0 / false / ""）
/// ```
///
/// 为什么这条要紧：**「这个模型上下文是 0」和「上游没告诉我上下文」是两回事。**
/// 填 0 的话界面会显示「0 tokens 上下文」—— 一个看起来像故障的假数字；
/// 而省略键之后界面可以自己决定「不显示这一列」。
/// 这与 `IsDefault` 那批字段的处理是同一条纪律（本项目反复踩过「静默得默认值」）。
///
/// ⚠ **`Id`/`Name` 是仅有的两个必填字段** —— 只读这两者的既有客户端不受影响。
/// </summary>
public sealed record CatalogModel(string Id, string Name)
{
    /// <summary>上下文窗口（token 数）。上游没给 → null。</summary>
    public int? ContextWindow { get; init; }

    /// <summary>最大输出 token 数。上游没给 → null。</summary>
    public int? MaxTokens { get; init; }

    /// <summary>支不支持图片输入。上游没给 → null。</summary>
    public bool? SupportsImages { get; init; }

    /// <summary>计费倍率（**已归一化**，如 `"x0.79"` —— 单位词已剥掉）。上游没给 → null。</summary>
    public string? Credits { get; init; }

    /// <summary>是不是免费。由**归一化后**的倍率判定（见 <see cref="CatalogPromotions.IsFreeMultiplier"/>）。</summary>
    public bool? Free { get; init; }

    /// <summary>促销徽标（已剥掉 `badge:` 前缀）。空/缺失 → null（省略键）。</summary>
    public IReadOnlyList<string>? Badges { get; init; }

    /// <summary>支持的思考档位（已过白名单）。空/缺失 → null。</summary>
    public IReadOnlyList<string>? SupportedEfforts { get; init; }

    /// <summary>默认思考档位。上游没给 → null。</summary>
    public string? DefaultEffort { get; init; }

    /// <summary>支不支持思考。上游没给 → null。</summary>
    public bool? SupportsReasoning { get; init; }

    /// <summary>是不是只能思考（不能关）。上游没给 → null。</summary>
    public bool? OnlyReasoning { get; init; }

    /// <summary>
    /// 能不能关掉思考。
    ///
    /// ⚠ **真机上这个字段是 false 居多**：目录里 37 个模型行的 `reasoning`
    /// 都是 `{"effort":"high","summary":"auto"}` —— **不带 `canDisableThinking`**。
    /// 而旧版（以及 `Effort.ResolveReasoning`）的读法是「字段缺失 ⇒ false」。
    /// 这里**照抄那个语义**，**不擅自改成「缺失即允许关」** ——
    /// 那是一次有意的行为变更，需要单独论证。
    ///
    /// ⚠ 但 `reasoning` **整个对象都不存在**时，旧版会保留初值 `true`。
    /// 这里如实取 null（省略键）而不是编造 true —— 「没有这个信息」比
    /// 「显示一个猜的 true」诚实。
    /// </summary>
    public bool? CanDisableThinking { get; init; }

    /// <summary>
    /// 序列化成 `/v1/models` 里的一项。
    ///
    /// ## 两条纪律
    ///
    /// 1. **缺失的元数据省略键**，不写 null —— 见类型注释。
    /// 2. **四个既有键原样保留**（`id` / `object` / `created` / `owned_by`）——
    ///    只读它们的客户端（绝大多数 OpenAI 兼容客户端）**完全不受影响**。
    /// </summary>
    /// <param name="ownedBy">`owned_by` 的值（变体 id）。</param>
    public JsonObject ToJson(string ownedBy)
    {
        // ① 既有四键 —— 一个都不能少、顺序不变
        var json = new JsonObject
        {
            ["id"] = Id,
            ["object"] = "model",
            ["created"] = 0,
            ["owned_by"] = ownedBy,
        };

        // ② `name` 保持总是给 —— 既有客户端/测试都读它，
        //    去掉它属于另一个决策（不是本任务要动的东西）。
        json["name"] = Name;

        // ③ 元数据：只在有值时才加键
        if (ContextWindow is { } contextWindow) json["contextWindow"] = contextWindow;
        if (MaxTokens is { } maxTokens) json["maxTokens"] = maxTokens;
        if (SupportsImages is { } supportsImages) json["supportsImages"] = supportsImages;

        if (!string.IsNullOrEmpty(Credits)) json["credits"] = Credits;
        if (Free is { } free) json["free"] = free;
        if (Badges is { Count: > 0 } badges) json["badges"] = ToArray(badges);

        if (SupportedEfforts is { Count: > 0 } efforts) json["supportedEfforts"] = ToArray(efforts);
        if (!string.IsNullOrEmpty(DefaultEffort)) json["defaultEffort"] = DefaultEffort;
        if (SupportsReasoning is { } supports) json["supportsReasoning"] = supports;
        if (OnlyReasoning is { } onlyReasoning) json["onlyReasoning"] = onlyReasoning;
        if (CanDisableThinking is { } canDisable) json["canDisableThinking"] = canDisable;

        return json;
    }

    private static JsonArray ToArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }
}

/// <summary>
/// 模型元数据的解析 —— 从上游目录的一行里读出可展示的字段。
///
/// ## 为什么单独一个静态类而不是塞进构造函数
///
/// 它是**纯解析**（输入一行 JSON，输出一组可空字段），可以穷举测试
/// 各种脏数据形状，不需要起网关。`CatalogModel` 是数据载体，两件事分开。
///
/// ## 两处「派单样例与真实文件不符」（实测纠正）
///
/// 任务书给的样例与实际文件（`~/.workbuddy-api/catalog.workbuddy.json`）不一致：
///
/// ```
/// supportedEfforts   任务书："low high max"（字符串）
///                    真实：["high"]（**数组**）
/// badges             任务书："夜间免费"（字符串）
///                    真实：["夜间免费"]（**数组**）
///
/// 顶层结构            任务书：{ models: [...] }
///                    真实：{ version, entries: { "<accountId>": { models: [...] } } }
/// ```
///
/// ⇒ **只认字符串会解析出 null。** 这里两种形态都收。
///
/// ⚠ **兼容字符串是防御性的，真实是数组** —— 写清楚免得下一个人
/// 以为数组才是异常形态。（旧版 `upstream.js` 早期确实发过空格分隔的
/// 字符串形态，所以兼容它不是凭空防御。）
/// </summary>
public static class CatalogMetadata
{
    /// <summary>
    /// 从目录的一行里解析元数据。
    ///
    /// @param row 目录行（`models[i]`）
    /// @param id   已取好的 id
    /// @param name 已取好的 name（缺失时调用方传 id）
    /// @param extraBadges 从**第二个文档**（控制台目录）捞回来的徽标标签。
    ///                    国内版的 `/v3/config` 不携带 `badge:*`。
    /// </summary>
    /// <returns>填充好元数据的模型项（缺失的一律为 null）。</returns>
    public static CatalogModel Parse(
        JsonObject row,
        string id,
        string name,
        IReadOnlyList<string>? extraBadges = null)
        => new(id, name)
        {
            ContextWindow = IntOf(row, "contextWindow"),
            MaxTokens = IntOf(row, "maxTokens"),
            SupportsImages = BoolOf(row, "supportsImages"),

            // 计费：复用 CatalogPromotions（它负责剥单位词、认促销标签）
            Credits = NormalizedCreditsOf(row),
            Free = FreeOf(row),
            Badges = BadgesOf(row, extraBadges),

            // 推理档位：复用 Effort 的词汇表（只认它认识的档位）
            SupportedEfforts = SupportedEffortsOf(row),
            DefaultEffort = DefaultEffortOf(row),
            SupportsReasoning = ReasoningFlagOf(row, "supports"),
            OnlyReasoning = ReasoningFlagOf(row, "onlyReasoning"),

            // ⚠ 照抄旧版语义：reasoning 存在但没这个字段 ⇒ false
            CanDisableThinking = CanDisableThinkingOf(row),
        };

    // ── 计费 ──────────────────────────────────────────────────

    /// <summary>
    /// 计费倍率的原始字符串。
    ///
    /// ⚠ **与 <see cref="CatalogPromotions.ResolveBilling"/> 同一读法**
    /// （`row["credits"]`，顶层）—— 真实目录里 `credits` 就在顶层，
    /// `billing` 只是个平行对象。两处各读一套会漏掉其中一种形状。
    /// 顶层没有再退回 `billing.credits`（兼容少数行）。
    /// </summary>
    private static string? RawCreditsOf(JsonObject row)
    {
        foreach (var source in (JsonObject?[])[row, row["billing"] as JsonObject])
        {
            var text = StringOf(source, "credits");
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return null;
    }

    /// <summary>
    /// 归一化后的计费倍率。
    ///
    /// **复用 <see cref="CatalogPromotions.NormalizeCredits"/>**，不重写 ——
    /// 它负责剥掉 `"x0.79 credits"` 末尾的单位词（否则界面被钉死在英文上）。
    /// </summary>
    private static string? NormalizedCreditsOf(JsonObject row)
        => CatalogPromotions.NormalizeCredits(RawCreditsOf(row));

    /// <summary>
    /// 是不是免费。
    ///
    /// ⚠ **由归一化后的倍率判定，不看 `billing.free`** ——
    /// 与 <see cref="CatalogPromotions.ResolveBilling"/> 保持**同一个口径**
    /// （它也是 `Free = IsFreeMultiplier(multiplier)`）。
    ///
    /// 为什么不读上游给的 `billing.free`：两个数据源可能不同步
    /// （倍率是 `x0.29` 而 free 写着 true）。以倍率为准，是因为
    /// **倍率是真正参与计费的那个值** —— 显示"免费"而实际扣费，
    /// 比显示"收费"而实际免费糟得多。
    /// </summary>
    private static bool? FreeOf(JsonObject row)
    {
        var normalized = NormalizedCreditsOf(row);
        return normalized is null ? null : CatalogPromotions.IsFreeMultiplier(normalized);
    }

    private static List<string>? BadgesOf(JsonObject row, IReadOnlyList<string>? extraBadges)
    {
        var badges = new List<string>();

        // ⚠ 两个数据源的**判据不同**，不能共用一个方法：
        //
        //   `tags`           是**混合标签**（`badge:限时免费` + `随便一个别的标签`）
        //                    ⇒ **必须**带 `badge:` 前缀才算徽标
        //   `billing.badges` 是**纯徽标名**（`"夜间免费"`，不带前缀）
        //                    ⇒ 裸值就收
        //
        // 共用一个方法时无法区分「无前缀的普通标签」和「裸徽标名」——
        // 两者长得一样（都是 `随便一个别的标签` 这种形态）。
        // 这正是我第一版写错的地方：`tags` 里的普通标签被当成徽标显示了。

        if (row["tags"] is JsonArray tags)
        {
            foreach (var node in tags)
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var tag))
                {
                    AddPrefixedBadge(badges, tag);
                }
            }
        }

        if (row["billing"] is JsonObject billing && billing["badges"] is JsonArray billBadges)
        {
            foreach (var node in billBadges)
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var badge))
                {
                    AddBareBadge(badges, badge);
                }
            }
        }

        // 第二数据源给的是**带 `badge:` 前缀的标签**（与 `tags` 同形）
        if (extraBadges is not null)
        {
            foreach (var tag in extraBadges) AddPrefixedBadge(badges, tag);
        }

        return badges.Count == 0 ? null : badges;
    }

    /// <summary>
    /// 收一个**带 `badge:` 前缀**的标签（`tags` 与第二数据源用）。
    ///
    /// 不带前缀的一律不收 —— `tags` 里混着普通标签，
    /// 收进来的话界面上会多出一堆莫名其妙的"徽章"。
    /// 与 <c>CatalogPromotions.AddBadge</c> 同一判据。
    /// </summary>
    private static void AddPrefixedBadge(List<string> badges, string tag)
    {
        var text = tag.Trim();
        if (!text.StartsWith(CatalogPromotions.BadgePrefix, StringComparison.Ordinal)) return;

        text = text[CatalogPromotions.BadgePrefix.Length..];

        // 徽标名取第一个冒号之前的部分（`限时免费:额外说明` → `限时免费`）
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0) text = text[..colon];

        AddBareBadge(badges, text);
    }

    /// <summary>收一个**裸徽标名**（`billing.badges` 里的值就是这种形态）。</summary>
    private static void AddBareBadge(List<string> badges, string badge)
    {
        var text = badge.Trim();
        if (text.Length == 0) return;
        if (!badges.Contains(text, StringComparer.Ordinal)) badges.Add(text);
    }

    // ── 推理档位 ──────────────────────────────────────────────

    private static List<string>? SupportedEffortsOf(JsonObject row)
    {
        if (row["reasoning"] is not JsonObject reasoning) return null;

        var efforts = new List<string>();
        foreach (var effort in StringsOf(reasoning, "supportedEfforts"))
        {
            // 逐个过白名单：上游发一个新档位而我们原样透出，
            // 界面会显示一个用户点了会报错的按钮
            if (Effort.IsKnown(effort) && !efforts.Contains(effort, StringComparer.Ordinal))
            {
                efforts.Add(effort);
            }
        }

        return efforts.Count == 0 ? null : efforts;
    }

    private static string? DefaultEffortOf(JsonObject row)
    {
        if (row["reasoning"] is not JsonObject reasoning) return null;

        // 两个字段名都认（新的是 defaultEffort，旧的是 effort）
        var candidate = StringOf(reasoning, "defaultEffort");
        if (!Effort.IsKnown(candidate)) candidate = StringOf(reasoning, "effort");

        return Effort.IsKnown(candidate) ? candidate : null;
    }

    /// <summary>
    /// 读 `reasoning` 里的一个布尔标志。
    ///
    /// ⚠ **优先从 `reasoning` 里面读**：真机上 `supports` / `onlyReasoning`
    /// 是**嵌在 `reasoning` 对象里**的，顶层没有同名字段。
    /// 顶层也给的话也认（兼容旧形状），但内部优先。
    /// </summary>
    private static bool? ReasoningFlagOf(JsonObject row, string name)
    {
        if (row["reasoning"] is JsonObject reasoning)
        {
            var nested = BoolOf(reasoning, name);
            if (nested is not null) return nested;
        }

        // 顶层兼容：`supports` 的历史名字是 `supportsReasoning`
        return name == "supports"
            ? BoolOf(row, "supports") ?? BoolOf(row, "supportsReasoning")
            : BoolOf(row, "onlyReasoning");
    }

    /// <summary>
    /// 能不能关掉思考。
    ///
    /// 照抄旧版语义：**只要 `reasoning` 是对象，缺失即 false**。
    /// `reasoning` 整个不存在时返回 null（省略键，不编造 true）。
    /// </summary>
    private static bool? CanDisableThinkingOf(JsonObject row)
    {
        if (row["reasoning"] is not JsonObject reasoning) return null;
        return BoolOf(reasoning, "canDisableThinking") == true;
    }

    // ── 通用读值 ──────────────────────────────────────────────

    private static string StringOf(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue value) return "";
        return value.TryGetValue<string>(out var text) ? text : "";
    }

    private static bool? BoolOf(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue value) return null;
        return value.TryGetValue<bool>(out var b) ? b : null;
    }

    /// <summary>
    /// 读一个 int。**数字给成字符串也认**（上游偶尔这么干），
    /// 认不出来返回 null（不是 0 —— 见 <see cref="CatalogModel"/> 的注释）。
    /// </summary>
    private static int? IntOf(JsonObject row, string name)
    {
        if (row[name] is not JsonValue value) return null;

        if (value.TryGetValue<int>(out var asInt)) return asInt;
        if (value.TryGetValue<long>(out var asLong) && asLong is >= int.MinValue and <= int.MaxValue) return (int)asLong;
        if (value.TryGetValue<double>(out var asDouble) && double.IsFinite(asDouble)) return (int)asDouble;

        if (value.TryGetValue<string>(out var text) &&
            int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// 读一个「字符串列表」字段。
    ///
    /// ⚠ **数组与字符串两种形态都收**（防御性的 —— 真实是数组）：
    /// ```
    /// ["high", "low"]      → high, low
    /// "low high max"       → low, high, max     ← 旧版的空格分隔形态
    /// ```
    /// 只认数组的话，旧形状的数据会**静默变成 null**（不是报错）。
    /// </summary>
    private static IEnumerable<string> StringsOf(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonNode node) yield break;

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    yield return text.Trim();
                }
            }
            yield break;
        }

        if (node is JsonValue single && single.TryGetValue<string>(out var whole) && !string.IsNullOrWhiteSpace(whole))
        {
            foreach (var part in whole.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                yield return part.Trim();
            }
        }
    }
}

/// <summary>
/// 模型目录。
///
/// ## 起点是编译内置的清单
///
/// 移植自旧版 `models.cn.json` 的 17 个 id（含 space-bunny）。为什么要编译进去而不是每次
/// 启动去拉：上游拉目录要凭证、要网络，而 `/v1/models` 是**客户端启动时
/// 第一件调的事** —— 拉不到就没有模型可选，用户看到空列表。
///
/// 内置清单是「最后一道兜底」，能拉到就用拉的。
///
/// ⚠ **内置清单没有元数据**（只有 id/name）。所以网关**没刷到目录时**
/// `/v1/models` 只有 id/name 而没有倍率/档位 —— 那是如实反映"我们不知道"，
/// 不是 bug。
///
/// ## 刷新
///
/// 拉到目录后用 <see cref="Replace"/> 换掉。刷新失败**不抛** ——
/// 保留旧清单继续服务，比把端点打挂强。
/// </summary>
public sealed class ModelCatalog
{
    /// <summary>
    /// 编译内置的清单（17 个 id，顺序与 catalog 文件一致）。
    /// 顺序有意义：客户端一般按列表顺序显示，用户习惯了原来的排列。
    /// </summary>
    public static readonly IReadOnlyList<CatalogModel> BuiltIn =
    [
        new("hy4-preview", "Hy4 preview"),
        new("hy3", "Hy3"),
        new("hy3-x", "Hy3-X"),
        new("space-bunny", "Space Bunny"),
        new("deepseek-v4.1-flash", "Deepseek-V4.1-Flash"),
        new("glm-5.3", "GLM-5.3"),
        new("glm-5.3-flash", "GLM-5.3-Flash"),
        new("glm-5.2", "GLM-5.2"),
        new("glm-5.1", "GLM-5.1"),
        new("glm-5v-turbo", "GLM-5v-Turbo"),
        new("minimax-m3", "MiniMax-M3"),
        new("minimax-m2.7", "MiniMax-M2.7"),
        new("kimi-k3-1", "Kimi-K3"),
        new("kimi-k2.8-preview", "Kimi-K2.8-Preview"),
        new("kimi-k2.7", "Kimi-K2.7-Code"),
        new("kimi-k2.6", "Kimi-K2.6"),
        new("deepseek-v4-pro", "Deepseek-V4-Pro"),
    ];

    private readonly object _gate = new();
    private volatile IReadOnlyList<CatalogModel> _models = BuiltIn;

    /// <summary>内部标识（旧版是 `workbuddy`）—— 它也是 `/v1/models` 里的 owned_by。</summary>
    public string Provider { get; }

    /// <summary>用户没指定模型、或者指定了目录里没有的模型时用哪个。</summary>
    public string DefaultModel { get; }

    public ModelCatalog(string provider, string defaultModel)
    {
        Provider = provider;
        DefaultModel = defaultModel;
    }

    /// <summary>当前清单快照。</summary>
    public IReadOnlyList<CatalogModel> Models => _models;

    public int Count => _models.Count;

    /// <summary>换一份清单。空清单忽略 —— 不能因为上游返回空就把客户端的选择打没。</summary>
    public void Replace(IEnumerable<CatalogModel> models)
    {
        var list = models.Where(m => !string.IsNullOrWhiteSpace(m.Id)).ToList();
        if (list.Count == 0) return;
        lock (_gate) _models = list;
    }

    /// <summary>
    /// 把当前清单渲染成 `/v1/models` 的 `data` 数组。
    ///
    /// **放在这里而不是端点里**：端点（`GatewayApp.cs`）不属于本任务的
    /// inScope，而「每个模型该透出哪些键」需要有测试覆盖 ——
    /// 放在目录类里，测试可以不经网关直接验。
    /// 端点接线只需把原来的手写循环换成 `_catalog.ToJsonArray()`。
    /// </summary>
    public JsonArray ToJsonArray()
    {
        var data = new JsonArray();
        foreach (var model in Models) data.Add(model.ToJson(Provider));
        return data;
    }

    /// <summary>
    /// 客户端要的模型是不是在清单里。不在就落到默认模型。
    ///
    /// 为什么不是「不在就报 400」：客户端里常常写死一个我们不认识的
    /// 别名（`gpt-4o`、`claude-3-5-sonnet`……）。报错的话用户得去改客户端
    /// 配置，而落到默认模型至少能用 —— 旧版网关就是这么做的。
    /// </summary>
    public string Resolve(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return DefaultModel;
        if (_models.Any(m => string.Equals(m.Id, requested, StringComparison.Ordinal))) return requested;
        return DefaultModel;
    }
}
