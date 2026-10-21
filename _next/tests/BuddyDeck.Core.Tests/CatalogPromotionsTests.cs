using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// M18 计费/促销解析的测试。对齐旧版 `upstream.js:361-530`。
///
/// 全部是**纯解析**测试 —— 不需要假上游、不需要网络（这一层没有 IO）。
/// 输入是手工构造的 JsonNode，穷举各种真实出现过的脏数据形状。
/// </summary>
public class CatalogPromotionsTests
{
    // 一个固定的"现在"，让促销的生效期可预期（2026-03-10T12:00:00Z）
    private static readonly long NowMs = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero)
        .ToUnixTimeMilliseconds();

    private static long Hours(int h) => NowMs + (h * 3600_000L);

    private static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms)
        .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    // ══ normalizeCredits ═════════════════════════════════════════

    [Theory]
    [InlineData("x0.79", "x0.79")]
    [InlineData("x0.79 credits", "x0.79")]      // ← 单位词必须剥掉
    [InlineData("x0.79 credit", "x0.79")]
    [InlineData("x0.79 CREDITS", "x0.79")]      // 大小写不敏感
    [InlineData("  x0.79  ", "x0.79")]
    [InlineData("x0.79   credits", "x0.79")]
    [InlineData("x1.00", "x1.00")]
    [InlineData("0.5", "0.5")]
    [InlineData("credits", null)]               // 只有单位词 = 没给
    [InlineData("credit", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void credits_归一化(string? input, string? expected)
    {
        Assert.Equal(expected, CatalogPromotions.NormalizeCredits(input));
    }

    [Fact]
    public void 单位词剥离后为空也要返回_null()
    {
        // " credits " 这种 —— 剥完什么都不剩
        Assert.Null(CatalogPromotions.NormalizeCredits(" credits "));
    }

    // ══ ★ 真机形状（t48 实测，2026-10-07）════════════════════════
    //
    // 下面几条的输入是**真实 /v3/config 返回的原文**（37 个模型行）。
    // 它们钉住「真机形状被 M18 的解析路径正确处理」。

    [Fact]
    public void 真机_badge_标签的完整形态是_标签加颜色()
    {
        // ⚠ 真机实测：`/v3/config` 里 hy3 的 tags 是
        //      ["badge:限时折扣:#FF0000"]
        //
        // 注意**有两级冒号**：`badge:` + 标签 + `:` + 颜色。
        // CatalogPromotions.AddBadge 取「前缀后、第一个冒号前」→ "限时折扣"，
        // 颜色被丢掉。**当前实现是对的**（徽标只要文案），
        // 但颜色信息确实丢失了 —— 如果界面想按上游给的颜色渲染徽标，
        // 这是一个真实缺口。记录在此。
        var row = new JsonObject
        {
            ["tags"] = new JsonArray("badge:限时折扣:#FF0000"),
        };

        var billing = CatalogPromotions.ResolveBilling(row);

        Assert.Equal(["限时折扣"], billing.Badges);
    }

    [Fact]
    public void 真机_badge_颜色被丢弃是已知行为()
    {
        // 把上面那条的另一面单独钉住：颜色**不会**出现在结果里。
        // 这样将来若有人想保留颜色，会看到这条并知道需要改 AddBadge。
        var row = new JsonObject { ["tags"] = new JsonArray("badge:限时折扣:#FF0000") };

        var badges = CatalogPromotions.ResolveBilling(row).Badges;

        Assert.DoesNotContain("#FF0000", badges);
        Assert.DoesNotContain("限时折扣:#FF0000", badges);
    }

    [Fact]
    public void 真机_目录里_credits_字段缺失()
    {
        // ⚠ 真机实测（2026-10-07，37 个模型行）：**没有任何一行带 credits 字段**。
        //
        // 影响：CatalogBilling.Credits 全为 null、Free 全为 false、
        //       倍率类徽标（"x0.79"）一个都不会出现。
        //       normalizeCredits / IsFreeMultiplier 在真机上**一次都没被触发**。
        //
        // 这不是 bug（上游当前就是这么发的），但意味着：
        // **M18 的 credits 归一化逻辑目前只有单测覆盖，真机未验证过**。
        var row = new JsonObject { ["id"] = "hy3", ["name"] = "Hy3" };

        var billing = CatalogPromotions.ResolveBilling(row);

        Assert.Null(billing.Credits);
        Assert.False(billing.Free);
        Assert.Empty(billing.Badges);
    }

    [Fact]
    public void 真机_目录不含_modelPromotions()
    {
        // 真机实测：`data` 的 18 个顶层键里**没有 modelPromotions**。
        // 所以 ParsePromotions 在真机上也没被触发过（返回空数组）。
        var data = new JsonObject { ["models"] = new JsonArray(), ["agents"] = new JsonArray() };

        Assert.False(data.ContainsKey("modelPromotions"));
        Assert.Empty(CatalogPromotions.ParsePromotions(data["modelPromotions"], "hy3"));
    }

    [Fact]
    public void 真机_reasoning_结构用的是_effort_而不是_defaultEffort()
    {
        // ⚠ 真机实测：模型行的 reasoning 对象长这样
        //      {"effort":"high","summary":"auto"}
        //
        // 用的是 **`effort`**（旧字段名），**没有 `defaultEffort`、也没有
        // `supportedEfforts`、`canDisableThinking`**。
        //
        // Effort.ResolveReasoning 两个字段名都认（defaultEffort 优先、effort 兜底），
        // 所以真机上能读出 defaultEffort="high" —— **这条路径真机验证通过**。
        var row = new JsonObject
        {
            ["supportsReasoning"] = true,
            ["onlyReasoning"] = true,
            ["reasoning"] = new JsonObject { ["effort"] = "high", ["summary"] = "auto" },
        };

        var reasoning = Effort.ResolveReasoning(row);

        Assert.True(reasoning.Supports);
        Assert.True(reasoning.OnlyReasoning);
        Assert.Equal("high", reasoning.DefaultEffort);
        // supportedEfforts 上游没发 → 空列表（"目录没说"，不是"不支持"）
        Assert.Empty(reasoning.SupportedEfforts);

        // ⚠⚠ 这一条反直觉，但**与旧版逐字一致**，所以是"对的行为"：
        //
        // 上游**没发** `canDisableThinking`，而结果却是 **false**（不是 true）。
        //
        // 原因（旧版 upstream.js:335 / :347）：
        //     let canDisableThinking = true;                              ← 初值 true
        //     if (isObject(rawReasoning)) {
        //       canDisableThinking = rawReasoning["canDisableThinking"] === true;   ← 无条件覆盖
        //     }
        // **只要 `reasoning` 是个对象，就会用 `=== true` 覆盖初值** ——
        // 缺字段 ⇒ undefined === true ⇒ false。
        // 所以那个初值 `true` 只在 `reasoning` **完全不存在**时起作用。
        //
        // 真机实测（2026-10-07）：37 个模型行的 reasoning 都是
        // `{"effort":"high","summary":"auto"}` —— **没有 canDisableThinking**，
        // 于是**真机上每一个模型的 CanDisableThinking 都是 false**。
        //
        // 这不是我们引入的 bug（照抄旧版），但它意味着：
        // **界面若按 CanDisableThinking 渲染"能不能关思考"，真机上会一律显示"不能关"。**
        // 记录在此，供后续决定是否偏离旧版（那会是**有意**的行为变更，要单独论证）。
        Assert.False(reasoning.CanDisableThinking);
    }

    [Fact]
    public void 真机_canDisableThinking_缺字段时为_false_与旧版一致()
    {
        // 上面那条的另一面：把「reasoning 存在但缺字段」与「reasoning 完全不存在」
        // 两种情况的差异单独钉住 —— 这正是旧版那个初值容易让人误解的地方。
        var withReasoning = new JsonObject
        {
            ["reasoning"] = new JsonObject { ["effort"] = "high" },   // 有对象、无该字段
        };
        var withoutReasoning = new JsonObject
        {
            ["supportsReasoning"] = true,                              // 没有 reasoning 对象
        };

        // 有对象 ⇒ 被 `=== true` 覆盖 ⇒ false
        Assert.False(Effort.ResolveReasoning(withReasoning).CanDisableThinking);
        // 没对象 ⇒ 保留初值 ⇒ true
        Assert.True(Effort.ResolveReasoning(withoutReasoning).CanDisableThinking);
    }

    // ══ free 判定 ════════════════════════════════════════════════

    [Theory]
    [InlineData("x0.00", true)]
    [InlineData("x0.0", true)]
    [InlineData("x0.000", true)]
    [InlineData("0.00", true)]
    [InlineData("x0", true)]
    [InlineData("x0.01", false)]     // ← 不是零
    [InlineData("x1.00", false)]
    [InlineData("x10.00", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 免费倍率判定(string? normalized, bool expected)
    {
        Assert.Equal(expected, CatalogPromotions.IsFreeMultiplier(normalized));
    }

    [Fact]
    public void 免费判定必须基于归一化后的值而非原始字符串()
    {
        // ⚠ 这条是旧版注释专门点名的陷阱：
        // 上游把同一个零写成 "x0.00" 和 "x0.00 credits" 两种。
        // 直接比原始字符串会漏掉带单位词的那一半。

        var bare = CatalogPromotions.NormalizeCredits("x0.00");
        var withUnit = CatalogPromotions.NormalizeCredits("x0.00 credits");

        Assert.Equal(bare, withUnit);                                     // 归一后相等
        Assert.True(CatalogPromotions.IsFreeMultiplier(bare));
        Assert.True(CatalogPromotions.IsFreeMultiplier(withUnit));        // ← 关键：带单位词也算免费

        // 而直接比原始字符串会得到不同结果：
        Assert.NotEqual("x0.00", "x0.00 credits");
    }

    // ══ resolveUpstreamBilling ═══════════════════════════════════

    [Fact]
    public void 计费_从行里读出_credits_与徽标()
    {
        var row = new JsonObject
        {
            ["credits"] = "x0.79 credits",
            ["tags"] = new JsonArray("badge:限时折扣", "普通标签", "badge:新模型"),
        };

        var billing = CatalogPromotions.ResolveBilling(row);

        Assert.Equal("x0.79 credits", billing.Credits);        // 原样保留（归一化是显示层的事）
        Assert.Equal(["限时折扣", "新模型"], billing.Badges);
        Assert.False(billing.Free);
    }

    [Fact]
    public void 计费_徽标名只取第一个冒号之前()
    {
        // 旧版 slice(...).split(":")[0] —— badge:标签:附加说明 只取 标签
        var row = new JsonObject
        {
            ["tags"] = new JsonArray("badge:限时免费:每周三", "badge:新用户:首月"),
        };

        var billing = CatalogPromotions.ResolveBilling(row);

        Assert.Equal(["限时免费", "新用户"], billing.Badges);
    }

    [Fact]
    public void 计费_徽标去重且保持行内优先()
    {
        var row = new JsonObject
        {
            ["tags"] = new JsonArray("badge:限时免费", "badge:新用户"),
        };

        // 第二数据源（控制台目录）里又出现了同一个徽标
        var extra = new[] { "badge:限时免费", "badge:控制台专属" };

        var billing = CatalogPromotions.ResolveBilling(row, extra);

        // 行内的在前，第二数据源的补充在后，重复的不再出现
        Assert.Equal(["限时免费", "新用户", "控制台专属"], billing.Badges);
    }

    [Fact]
    public void 计费_非_badge_前缀的标签被忽略()
    {
        var row = new JsonObject
        {
            ["tags"] = new JsonArray("免费", "badge", "Badge:nope", "promo:限时", "badge:对"),
        };

        var billing = CatalogPromotions.ResolveBilling(row);

        // "badge"（无冒号）、"promo:限时" 都不是；"Badge:nope" 前缀大小写不敏感 → 算
        Assert.Equal(["nope", "对"], billing.Badges);
    }

    [Fact]
    public void 计费_空_credits_与无徽标()
    {
        var billing = CatalogPromotions.ResolveBilling(new JsonObject());

        Assert.Null(billing.Credits);
        Assert.Empty(billing.Badges);
        Assert.False(billing.Free);
    }

    [Fact]
    public void 计费_免费行()
    {
        var row = new JsonObject { ["credits"] = "x0.00 credits", ["tags"] = new JsonArray() };

        var billing = CatalogPromotions.ResolveBilling(row);

        Assert.True(billing.Free);
    }

    // ══ parsePromotions ══════════════════════════════════════════

    /// <summary>
    /// 造一条促销。
    ///
    /// ⚠ `factor` / `priority` 用 **<c>double?</c>** 而不是 <c>object?</c>。
    ///
    /// 第一版是 `object?`，想的是「传得进 double 也传得进 int」。但
    /// `JsonValue.Create(object)` 对装箱值走的路径和泛型重载不一样 ——
    /// 结果是 `factor: 0.5` 写进去之后 `TryGetValue&lt;double&gt;` 读不出来，
    /// 促销被校验规则当成「factor 不是有限数」丢掉；而 `priority: 3` 读出来是 0。
    ///
    /// **这类 bug 的特征是「解析逻辑看起来完全正确，测试却红」** ——
    /// 错在测试夹具的造数据方式，不在被测代码。用强类型就没有这个歧义。
    /// </summary>
    private static JsonObject PromoRow(
        bool enabled = true,
        string[]? modelIds = null,
        string? validFrom = null,
        string? validUntil = null,
        double? factor = null,
        string displayMode = "replace",
        string label = "限时免费",
        double? priority = null)
    {
        var discount = new JsonObject { ["displayMode"] = displayMode };
        if (factor is not null) discount["factor"] = JsonValue.Create(factor.Value);

        var row = new JsonObject
        {
            ["enabled"] = enabled,
            ["modelIds"] = new JsonArray((modelIds ?? ["hy3"]).Select(id => (JsonNode)id!).ToArray()),
            ["schedule"] = new JsonObject
            {
                ["validFrom"] = validFrom ?? Iso(Hours(-1)),
                ["validUntil"] = validUntil ?? Iso(Hours(1)),
            },
            ["discount"] = discount,
            ["badge"] = new JsonObject { ["label"] = label },
        };

        if (priority is not null) row["priority"] = JsonValue.Create(priority.Value);

        return row;
    }

    [Fact]
    public void 促销_正常解析()
    {
        var promotions = new JsonArray(PromoRow(factor: 0.5, label: "半价", priority: 3));

        var parsed = CatalogPromotions.ParsePromotions(promotions, "hy3");

        var promo = Assert.Single(parsed);
        Assert.Equal(0.5, promo.Factor);
        Assert.Equal("半价", promo.Label);
        Assert.Equal(3, promo.Priority);
        Assert.True(promo.EndMs > promo.StartMs);
    }

    [Fact]
    public void 促销_只挑覆盖这个模型的()
    {
        var promotions = new JsonArray(
            PromoRow(modelIds: ["hy3"], factor: 0.5, label: "给 hy3"),
            PromoRow(modelIds: ["other"], factor: 0.1, label: "给 other"),
            PromoRow(modelIds: ["hy3", "other"], factor: 0.7, label: "两个都有"));

        var parsed = CatalogPromotions.ParsePromotions(promotions, "hy3");

        Assert.Equal(["给 hy3", "两个都有"], parsed.Select(p => p.Label));
    }

    [Theory]
    [InlineData("disabled")]        // enabled = false
    [InlineData("no-modelids")]     // 缺 modelIds
    [InlineData("wrong-model")]     // modelIds 不含它
    [InlineData("no-schedule")]     // 缺 schedule
    [InlineData("no-discount")]     // 缺 discount
    [InlineData("no-badge")]        // 缺 badge
    [InlineData("wrong-displaymode")]  // displayMode 不是 replace
    [InlineData("bad-start")]       // validFrom 解析不了
    [InlineData("bad-end")]         // validUntil 解析不了
    [InlineData("end-before-start")]   // 时间区间反了
    [InlineData("negative-factor")] // factor < 0
    [InlineData("no-factor")]       // factor 不是数字
    public void 促销_五重校验任一不满足就丢弃(string scenario)
    {
        var row = scenario switch
        {
            "disabled" => PromoRow(enabled: false, factor: 0.5),
            "no-modelids" => new JsonObject
            {
                ["enabled"] = true,
                ["schedule"] = new JsonObject { ["validFrom"] = Iso(Hours(-1)), ["validUntil"] = Iso(Hours(1)) },
                ["discount"] = new JsonObject { ["displayMode"] = "replace", ["factor"] = 0.5 },
                ["badge"] = new JsonObject { ["label"] = "x" },
            },
            "wrong-model" => PromoRow(modelIds: ["other"], factor: 0.5),
            "no-schedule" => new JsonObject
            {
                ["enabled"] = true,
                ["modelIds"] = new JsonArray("hy3"),
                ["discount"] = new JsonObject { ["displayMode"] = "replace", ["factor"] = 0.5 },
                ["badge"] = new JsonObject { ["label"] = "x" },
            },
            "no-discount" => new JsonObject
            {
                ["enabled"] = true,
                ["modelIds"] = new JsonArray("hy3"),
                ["schedule"] = new JsonObject { ["validFrom"] = Iso(Hours(-1)), ["validUntil"] = Iso(Hours(1)) },
                ["badge"] = new JsonObject { ["label"] = "x" },
            },
            "no-badge" => new JsonObject
            {
                ["enabled"] = true,
                ["modelIds"] = new JsonArray("hy3"),
                ["schedule"] = new JsonObject { ["validFrom"] = Iso(Hours(-1)), ["validUntil"] = Iso(Hours(1)) },
                ["discount"] = new JsonObject { ["displayMode"] = "replace", ["factor"] = 0.5 },
            },
            "wrong-displaymode" => PromoRow(factor: 0.5, displayMode: "multiply"),
            "bad-start" => PromoRow(factor: 0.5, validFrom: "not-a-date"),
            "bad-end" => PromoRow(factor: 0.5, validUntil: ""),
            "end-before-start" => PromoRow(factor: 0.5, validFrom: Iso(Hours(2)), validUntil: Iso(Hours(1))),
            "negative-factor" => PromoRow(factor: -1),
            "no-factor" => PromoRow(),   // factor 不传
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };

        var promotions = new JsonArray(row);

        Assert.Empty(CatalogPromotions.ParsePromotions(promotions, "hy3"));
    }

    [Fact]
    public void 促销_非数组返回空()
    {
        Assert.Empty(CatalogPromotions.ParsePromotions(null, "hy3"));
        Assert.Empty(CatalogPromotions.ParsePromotions(JsonValue.Create(1), "hy3"));
        Assert.Empty(CatalogPromotions.ParsePromotions(new JsonObject(), "hy3"));
    }

    [Fact]
    public void 促销_zero_factor_合法()
    {
        // factor = 0 表示直接免费，是合法的（不是"缺失"）
        var promotions = new JsonArray(PromoRow(factor: 0, label: "免费"));

        var promo = Assert.Single(CatalogPromotions.ParsePromotions(promotions, "hy3"));
        Assert.Equal(0, promo.Factor);
    }

    // ══ modelWithCurrentPromotion ════════════════════════════════

    private static CatalogModelInfo Model(
        string credits = "x1.00",
        string[]? badges = null,
        params ModelPromotion[] promotions)
        => new()
        {
            Id = "hy3",
            Billing = new CatalogBilling
            {
                Credits = credits,
                Badges = badges ?? [],
                Free = CatalogPromotions.IsFreeMultiplier(CatalogPromotions.NormalizeCredits(credits)),
            },
            Promotions = promotions,
        };

    [Fact]
    public void 促销重算_生效期内按系数折算()
    {
        var model = Model("x1.00", promotions: [new ModelPromotion
        {
            StartMs = Hours(-1), EndMs = Hours(1), Factor = 0.5, Label = "半价", Priority = 1,
        }]);

        var result = CatalogPromotions.ApplyCurrentPromotion(model, NowMs);

        Assert.Equal("x0.50", result.Billing.Credits);
        Assert.False(result.Billing.Free);
        Assert.Contains("半价", result.Billing.Badges);
    }

    [Fact]
    public void 促销重算_factor_为零则免费()
    {
        var model = Model("x1.00", promotions: [new ModelPromotion
        {
            StartMs = Hours(-1), EndMs = Hours(1), Factor = 0, Label = "限时免费", Priority = 1,
        }]);

        var result = CatalogPromotions.ApplyCurrentPromotion(model, NowMs);

        Assert.Equal("x0.00", result.Billing.Credits);
        Assert.True(result.Billing.Free);
        Assert.Contains("限时免费", result.Billing.Badges);
    }

    [Fact]
    public void 促销重算_过期后标记费率未知而不是沿用旧价()
    {
        // ⚠ 这条是核心：目录被缓存，促销会过期。
        // 沿用旧价 = 界面继续显示已经失效的「免费」，用户以为还能白嫖。
        var model = Model("x1.00", badges: ["限时免费"], promotions: [new ModelPromotion
        {
            StartMs = Hours(-48), EndMs = Hours(-24), Factor = 0, Label = "限时免费", Priority = 1,
        }]);

        var result = CatalogPromotions.ApplyCurrentPromotion(model, NowMs);

        Assert.False(result.Billing.Free);        // ← 不再声称免费
        Assert.True(result.Billing.RateUnknown);  // ← 如实说"新价我不知道"
        Assert.Empty(result.Billing.Badges);      // ← 过期徽标要撤掉
    }

    [Fact]
    public void 促销重算_从没有过促销则原样返回()
    {
        // 没有促销的模型不该被编造出 RateUnknown
        var model = Model("x1.00", promotions: []);

        var result = CatalogPromotions.ApplyCurrentPromotion(model, NowMs);

        Assert.False(result.Billing.RateUnknown);
        Assert.Equal("x1.00", result.Billing.Credits);
        Assert.Same(model, result);   // 完全没动
    }

    [Fact]
    public void 促销重算_过期且从未有过促销时原样返回()
    {
        // 有 promotions 记录，但全部 factor == 1 且无徽标、不免费 → hadPromo 为 false
        var model = Model("x1.00", promotions: [new ModelPromotion
        {
            StartMs = Hours(-48), EndMs = Hours(-24), Factor = 1, Label = "", Priority = 1,
        }]);

        var result = CatalogPromotions.ApplyCurrentPromotion(model, NowMs);

        Assert.False(result.Billing.RateUnknown);
        Assert.Same(model, result);
    }

    [Fact]
    public void 促销重算_多條命中取优先级最高的()
    {
        var model = Model("x1.00", promotions:
        [
            new ModelPromotion { StartMs = Hours(-1), EndMs = Hours(1), Factor = 0.9, Label = "低优先", Priority = 1 },
            new ModelPromotion { StartMs = Hours(-1), EndMs = Hours(1), Factor = 0.1, Label = "高优先", Priority = 9 },
        ]);

        var result = CatalogPromotions.ApplyCurrentPromotion(model, NowMs);

        Assert.Equal("x0.10", result.Billing.Credits);
        Assert.Contains("高优先", result.Billing.Badges);
        Assert.DoesNotContain("低优先", result.Billing.Badges);
    }

    [Fact]
    public void 促销重算_边界_生效时刻含起始不含结束()
    {
        var model = Model("x1.00", promotions: [new ModelPromotion
        {
            StartMs = NowMs, EndMs = Hours(1), Factor = 0.5, Label = "边界", Priority = 1,
        }]);

        // now == start → 命中（>=）
        Assert.Equal("x0.50", CatalogPromotions.ApplyCurrentPromotion(model, NowMs).Billing.Credits);

        // now == end → 不命中（<），退化成 RateUnknown
        var atEnd = CatalogPromotions.ApplyCurrentPromotion(model, Hours(1));
        Assert.NotEqual("x0.50", atEnd.Billing.Credits);
    }

    [Fact]
    public void 促销重算_没有原始费率且非零系数时原样返回()
    {
        // factor != 0 但 credits 读不出倍率 → 算不出来，不能编
        var model = Model("not-a-rate", promotions: [new ModelPromotion
        {
            StartMs = Hours(-1), EndMs = Hours(1), Factor = 0.5, Label = "x", Priority = 1,
        }]);

        var result = CatalogPromotions.ApplyCurrentPromotion(model, NowMs);

        Assert.Same(model, result);
    }

    [Fact]
    public void 促销重算_factor_为零时不需要原始费率()
    {
        // factor == 0 → 直接 0，不看原价
        var model = Model("not-a-rate", promotions: [new ModelPromotion
        {
            StartMs = Hours(-1), EndMs = Hours(1), Factor = 0, Label = "免费", Priority = 1,
        }]);

        var result = CatalogPromotions.ApplyCurrentPromotion(model, NowMs);

        Assert.True(result.Billing.Free);
        Assert.Equal("x0.00", result.Billing.Credits);
    }

    [Fact]
    public void 促销重算_无单位词的倍率也能算()
    {
        // credits = "0.5"（没有 x 前缀）—— 旧版要求 startsWith("x")，
        // 所以这种算不出来，原样返回
        var model = Model("0.5", promotions: [new ModelPromotion
        {
            StartMs = Hours(-1), EndMs = Hours(1), Factor = 0.5, Label = "x", Priority = 1,
        }]);

        Assert.Same(model, CatalogPromotions.ApplyCurrentPromotion(model, NowMs));
    }

    // ══ promoBadgePath ═══════════════════════════════════════════

    [Fact]
    public void 促销徽标路径与旧版一致()
    {
        Assert.Equal("/console/enterprises/personal/models", CatalogPromotions.PromoBadgePath);
    }

    [Fact]
    public void 促销徽标只在国内版可用()
    {
        // 旧版 variants.js:51 —— promoBadgePath 只在 CN_VARIANT 上定义
        Assert.True(CatalogPromotions.IsPromoBadgeAvailable(UpstreamVariant.China));
        Assert.False(CatalogPromotions.IsPromoBadgeAvailable(UpstreamVariant.Global));
    }
}
