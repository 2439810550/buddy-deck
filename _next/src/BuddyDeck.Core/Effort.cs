using System.Text.Json.Nodes;

namespace BuddyDeck.Core;

/// <summary>
/// 思考档位（reasoning effort）的词汇表与解析（M17/M18 共用）。
///
/// ## 为什么单独一个文件
///
/// 「有哪些档位」这件事**同时被两处需要**，而它们的用途完全相反：
///
/// ```
/// M18（解析）  目录里写「这个模型支持 low/medium/high」，我们要**照抄**它
/// M17（探测）  实际发一个带档位的请求，看上游**认不认**这个值
/// ```
///
/// 两边必须用**同一份词汇表**。各写一份的话，出现「目录说有 `xhigh`、
/// 探测却不认识 `xhigh`」这种不一致时，没人会立刻发现 —— 症状是
/// 「目录里能选 xhigh，选了报错」。
///
/// ## 为什么是白名单而不是「什么字符串都收」
///
/// 上游目录是**扒出来的、不是文档的**。它可能返回我们没见过的档位名
/// （新版本加了档位），也可能返回脏数据。做法是：**认识的照抄，不认识的丢掉**。
///
/// 丢掉而不是透传，理由：档位名最后会变成**请求体里的
/// `reasoning_effort`**。把一个没见过的字符串原样发上去，上游要么报错、
/// 要么**静默当成默认档** —— 后者更坏，用户以为选了「最高档」，
/// 实际跑的是默认档，而他不会知道。
/// </summary>
public static class Effort
{
    /// <summary>
    /// 上游在协议上真实暴露的档位拼写（旧版 `upstream.js:54` 的 `EFFORT_VALUES`）。
    ///
    /// **顺序有意义**：从低到高。界面按这个顺序排下拉框。
    /// </summary>
    public static readonly IReadOnlyList<string> Values = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>这个拼写是不是已知档位（**大小写敏感** —— 上游只认小写）。</summary>
    public static bool IsKnown(string? effort)
        => !string.IsNullOrEmpty(effort) && Values.Contains(effort, StringComparer.Ordinal);

    /// <summary>
    /// 从一个模型行里解析它的思考能力。
    ///
    /// 对齐旧版 `resolveUpstreamReasoning`（`upstream.js` 内）。
    ///
    /// ## 三个独立字段，别混
    ///
    /// ```
    /// supportsReasoning    true  = 这个模型支持思考
    /// onlyReasoning        true  = **只**能思考，关不掉（如 o1 那类）
    /// canDisableThinking   true  = 允许把它关掉
    /// ```
    ///
    /// `onlyReasoning = true` 时 `canDisableThinking` 实际上没有意义 ——
    /// 上游就是这么发的（两个字段独立性不够干净）。**照抄，不擅自归一**。
    ///
    /// ## ⚠ `canDisableThinking` 的"默认值"是个陷阱（照抄旧版，但要知道）
    ///
    /// ```
    /// var canDisableThinking = true;                                       // 初值
    /// if (row["reasoning"] is JsonObject reasoning) {
    ///     canDisableThinking = BoolOf(reasoning, "canDisableThinking") == true;   // 无条件覆盖
    /// }
    /// ```
    ///
    /// **只要 `reasoning` 是个对象，就会用 `== true` 覆盖初值** ——
    /// 字段缺失 ⇒ `null == true` ⇒ `false`。
    /// 所以那个初值 `true` 只在 `reasoning` **完全不存在**时才有意义。
    ///
    /// **真机实测（2026-10-07，t48）**：37 个模型行的 `reasoning` 都是
    /// `{"effort":"high","summary":"auto"}` —— **不带 `canDisableThinking`**。
    /// 于是**真机上每个模型的 `CanDisableThinking` 都是 false**。
    ///
    /// 这与旧版 `upstream.js:335/:347` **逐字一致**，所以不是移植引入的错误。
    /// 但它意味着：**界面若按此字段渲染"能不能关思考"，真机会一律显示"不能关"。**
    /// 若要偏离旧版改成"缺字段就视为允许关"，那是一次**有意的行为变更**，
    /// 需要单独论证（并同步改测试）—— 不要顺手改。
    /// </summary>
    public static ModelReasoning ResolveReasoning(JsonObject row)
    {
        var supports = BoolOf(row, "supportsReasoning") == true;
        var onlyReasoning = BoolOf(row, "onlyReasoning") == true;

        var supportedEfforts = new List<string>();
        var defaultEffort = "";
        var canDisableThinking = true;

        if (row["reasoning"] is JsonObject reasoning)
        {
            // 支持的档位：**逐个过白名单**，不认识的丢掉
            if (reasoning["supportedEfforts"] is JsonArray rawEfforts)
            {
                foreach (var node in rawEfforts)
                {
                    if (node is JsonValue value && value.TryGetValue<string>(out var effort)
                        && IsKnown(effort) && !supportedEfforts.Contains(effort, StringComparer.Ordinal))
                    {
                        supportedEfforts.Add(effort);
                    }
                }
            }

            // 默认档位：两个字段名都认（新的是 defaultEffort，旧的是 effort）
            var candidate = StringOf(reasoning, "defaultEffort");
            if (!IsKnown(candidate)) candidate = StringOf(reasoning, "effort");
            if (IsKnown(candidate)) defaultEffort = candidate;

            canDisableThinking = BoolOf(reasoning, "canDisableThinking") == true;
        }

        return new ModelReasoning
        {
            Supports = supports,
            OnlyReasoning = onlyReasoning,
            SupportedEfforts = supportedEfforts,
            DefaultEffort = defaultEffort,
            CanDisableThinking = canDisableThinking,
        };
    }

    private static bool? BoolOf(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue value) return null;
        if (value.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    private static string StringOf(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";
}

/// <summary>一个模型的思考能力。</summary>
public sealed record ModelReasoning
{
    /// <summary>支持思考。</summary>
    public bool Supports { get; init; }

    /// <summary>只能思考（关不掉）。</summary>
    public bool OnlyReasoning { get; init; }

    /// <summary>
    /// 支持的档位。**已过白名单** —— 只包含 <see cref="Effort.Values"/> 里认识的。
    /// 空列表表示「目录没说」（不是「不支持」—— 那看 <see cref="Supports"/>）。
    /// </summary>
    public IReadOnlyList<string> SupportedEfforts { get; init; } = [];

    /// <summary>默认档位。空字符串表示目录没说。</summary>
    public string DefaultEffort { get; init; } = "";

    /// <summary>
    /// 允许关掉思考。
    ///
    /// ## ⚠ 这个属性**当前不经序列化器**
    ///
    /// 它的值由 <c>BoolOf(reasoning, "canDisableThinking")</c> **手工抽取**
    /// （见 <see cref="ResolveReasoning"/>）—— 属性名不参与绑定，当前安全。
    ///
    /// **为什么还要标注**：这里的情况比 `Checkin.IsStreakDay` 更微妙 ——
    /// **键名恰好和属性名同形**（都是 `canDisableThinking`，只是大小写首字母差），
    /// 所以现在"看起来没问题"完全是巧合。哪天有人把它挪进一个走序列化器的
    /// DTO，或者改了属性名，就会静默失配。
    ///
    /// 前缀/双词布尔的「写出去」与「读回来」键名天然容易不对称；
    /// 显式标注让这层约定**从巧合变成契约**。
    ///
    /// 真实案例（同形状，已发生过）：`IsTerminal` ← JSON 键 `terminal`，
    /// `PropertyNameCaseInsensitive` 不做 `Is` 前缀推理 → 绑不上 →
    /// 留在默认值 `true`。这里若失配，默认值同样是 `true`，
    /// 表现会是「以为能关思考、其实关不掉」。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("canDisableThinking")]
    public bool CanDisableThinking { get; init; } = true;
}
