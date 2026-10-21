using System.Text.Json.Nodes;
using BuddyDeck.Gateway;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// `/v1/models` 元数据透出的测试。
///
/// ## 这组测试的输入**照真实文件抄**
///
/// 上游目录的真实形状（实测 `~/.workbuddy-api/catalog.workbuddy.json`，46 个模型）：
///
/// ```jsonc
/// { "version": 1,
///   "entries": { "<accountId>": { "source": "workbuddy:cli", "models": [ … ] } } }
/// ```
/// ```jsonc
/// { "id": "hy4-preview", "name": "Hy4 preview",
///   "contextWindow": 1000000, "maxTokens": 64000, "supportsImages": true,
///   "reasoning": { "supports": true, "onlyReasoning": true,
///                  "supportedEfforts": ["high"],      // ← 数组
///                  "defaultEffort": "high", "canDisableThinking": false },
///   "billing": { "credits": "x0.29 credits",
///                "badges": ["夜间免费"],               // ← 数组
///                "free": false } }
/// ```
///
/// ⚠ **派单样例把 `supportedEfforts`/`badges` 写成了字符串** —— 按那样解析会得 null。
/// 下面既验数组（真实形状），也验字符串（防御性兼容，旧版早期形态）。
/// </summary>
public class ModelCatalogMetadataTests
{
    /// <summary>照真实文件造一行（数组形态）。</summary>
    private static JsonObject RealRow(
        string id = "hy4-preview",
        string name = "Hy4 preview",
        int? contextWindow = 1_000_000,
        int? maxTokens = 64_000,
        bool? supportsImages = true,
        string? credits = "x0.29 credits",
        bool? free = null,
        string[]? badges = null,
        string[]? efforts = null,
        string? defaultEffort = "high",
        bool? canDisableThinking = false,
        bool? supports = true,
        bool? onlyReasoning = true)
    {
        var row = new JsonObject { ["id"] = id, ["name"] = name };

        if (contextWindow is { } cw) row["contextWindow"] = cw;
        if (maxTokens is { } mt) row["maxTokens"] = mt;
        if (supportsImages is { } si) row["supportsImages"] = si;

        if (credits is not null || badges is not null || free is not null)
        {
            var billing = new JsonObject();
            if (credits is not null) billing["credits"] = credits;
            if (free is { } f) billing["free"] = f;
            if (badges is not null)
            {
                var array = new JsonArray();
                foreach (var b in badges) array.Add(b);
                billing["badges"] = array;
            }
            row["billing"] = billing;
        }

        if (efforts is not null || defaultEffort is not null || canDisableThinking is not null
            || supports is not null || onlyReasoning is not null)
        {
            var reasoning = new JsonObject();
            if (supports is { } s) reasoning["supports"] = s;
            if (onlyReasoning is { } o) reasoning["onlyReasoning"] = o;
            if (defaultEffort is not null) reasoning["defaultEffort"] = defaultEffort;
            if (canDisableThinking is { } cd) reasoning["canDisableThinking"] = cd;
            if (efforts is not null)
            {
                var array = new JsonArray();
                foreach (var e in efforts) array.Add(e);
                reasoning["supportedEfforts"] = array;
            }
            row["reasoning"] = reasoning;
        }

        return row;
    }

    private static CatalogModel Parse(JsonObject row, IReadOnlyList<string>? extraBadges = null)
        => CatalogMetadata.Parse(row, row["id"]!.GetValue<string>(),
            row["name"]?.GetValue<string>() ?? "", extraBadges);

    // ══════════════════════════════════════════════════════════
    // ① 计费：credits / free / badges
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 计费倍率被归一化_剥掉单位词()
    {
        var model = Parse(RealRow(credits: "x0.29 credits"));

        // ★ 单位词必须剥掉 —— 否则界面被钉死在英文上
        //   （复用 CatalogPromotions.NormalizeCredits，不重写）
        Assert.Equal("x0.29", model.Credits);
    }

    [Fact]
    public void 计费倍率已是裸形态时原样保留()
    {
        Assert.Equal("x0.79", Parse(RealRow(credits: "x0.79")).Credits);
    }

    [Fact]
    public void 免费由归一化后的倍率判定()
    {
        // 上游把同一个零写成两种 —— 只比字符串会漏掉一半
        Assert.True(Parse(RealRow(credits: "x0.00", free: null)).Free);
        Assert.True(Parse(RealRow(credits: "x0.00 credits", free: null)).Free);
        Assert.False(Parse(RealRow(credits: "x0.29 credits", free: null)).Free);
    }

    [Fact]
    public void 免费以倍率为准_不采信billing_free()
    {
        // ⚠ 与 CatalogPromotions.ResolveBilling 同一口径：Free = IsFreeMultiplier(倍率)。
        //
        // 为什么不信上游的 billing.free：两个数据源可能不同步
        // （倍率 x0.29 而 free 写着 true）。以倍率为准 ——
        // **显示"免费"而实际扣费，比显示"收费"而实际免费糟得多。**
        var model = Parse(RealRow(credits: "x0.29 credits", free: true));

        Assert.False(model.Free);
    }

    [Fact]
    public void 徽标从billing_badges读出()
    {
        var model = Parse(RealRow(badges: ["夜间免费"]));

        Assert.Equal(["夜间免费"], model.Badges);
    }

    [Fact]
    public void 徽标从tags的badge前缀读出并剥前缀()
    {
        var row = RealRow(badges: null);
        row["tags"] = new JsonArray("badge:限时免费", "随便一个别的标签", "badge:新用户");

        var model = Parse(row);

        // ⚠ 只认 badge: 前缀的 —— `tags` 里混着普通标签，
        //   不加前缀判断的话那些会被当成徽标显示出来
        Assert.Equal(["限时免费", "新用户"], model.Badges);
        Assert.DoesNotContain("随便一个别的标签", model.Badges!);
    }

    [Fact]
    public void 带冒号的非badge标签不算徽标()
    {
        var row = RealRow(badges: null);
        row["tags"] = new JsonArray("category:premium", "badge:限时免费");

        // `category:premium` 是 `类型:值` 形状，不是徽标名
        Assert.Equal(["限时免费"], Parse(row).Badges);
    }

    [Fact]
    public void 徽标名只取第一个冒号之前()
    {
        var row = RealRow(badges: null);
        row["tags"] = new JsonArray("badge:限时免费:额外说明");

        Assert.Equal(["限时免费"], Parse(row).Badges);
    }

    [Fact]
    public void 徽标去重()
    {
        var row = RealRow(badges: ["限时免费"]);
        row["tags"] = new JsonArray("badge:限时免费");

        // 同一个徽标可能在两个数据源里都出现
        Assert.Single(Parse(row).Badges!);
    }

    [Fact]
    public void 第二数据源的徽标也能合并进来()
    {
        var model = Parse(RealRow(badges: null), extraBadges: ["badge:控制台专有"]);

        Assert.Equal(["控制台专有"], model.Badges);
    }

    // ══════════════════════════════════════════════════════════
    // ② 推理：supportedEfforts / defaultEffort / canDisableThinking / onlyReasoning
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 档位数组形态能读出_这是真实形态()
    {
        // ★ 真实文件是这样（数组）—— 派单样例写成字符串是错的
        var model = Parse(RealRow(efforts: ["high"]));

        Assert.Equal(["high"], model.SupportedEfforts);
    }

    [Fact]
    public void 档位字符串形态也能读出_防御性兼容()
    {
        // 旧版早期发过空格分隔的字符串形态。
        // ⚠ 兼容它是防御性的，**真实是数组**（见类型注释）。
        var row = RealRow();
        row["reasoning"]!["supportedEfforts"] = "low high max";

        var model = Parse(row);

        Assert.Equal(["low", "high", "max"], model.SupportedEfforts);
    }

    [Fact]
    public void 不认识的档位被丢掉()
    {
        // 上游发一个新档位而我们原样透出，界面会显示一个点了会报错的按钮
        var model = Parse(RealRow(efforts: ["high", "ultra-mega", "low"]));

        Assert.Equal(["high", "low"], model.SupportedEfforts);
    }

    [Fact]
    public void 档位去重且保序()
    {
        var model = Parse(RealRow(efforts: ["high", "low", "high"]));

        Assert.Equal(["high", "low"], model.SupportedEfforts);
    }

    [Fact]
    public void 默认档位从defaultEffort读()
    {
        Assert.Equal("high", Parse(RealRow(defaultEffort: "high")).DefaultEffort);
    }

    [Fact]
    public void 默认档位旧字段名effort也认()
    {
        var row = RealRow(defaultEffort: null);
        row["reasoning"]!["effort"] = "low";

        Assert.Equal("low", Parse(row).DefaultEffort);
    }

    [Fact]
    public void 默认档位不认识时不给()
    {
        Assert.Null(Parse(RealRow(defaultEffort: "不存在的档位")).DefaultEffort);
    }

    [Fact]
    public void supports与onlyReasoning从reasoning里面读_这是真实位置()
    {
        // ★ 真实文件里这两个字段**嵌在 reasoning 里**，顶层没有
        var model = Parse(RealRow(supports: true, onlyReasoning: true));

        Assert.True(model.SupportsReasoning);
        Assert.True(model.OnlyReasoning);
    }

    [Fact]
    public void supports也在顶层时也认_兼容旧形状()
    {
        var row = RealRow(supports: null, onlyReasoning: null);
        row["reasoning"]!.AsObject().Remove("onlyReasoning");
        row["supportsReasoning"] = true;

        Assert.True(Parse(row).SupportsReasoning);
    }

    [Fact]
    public void canDisableThinking照抄旧版语义_字段缺失时为false()
    {
        // 真机 37 个模型行的 reasoning 都是 {"effort":"high","summary":"auto"}
        // —— 不带 canDisableThinking。旧版读法是「缺失 ⇒ false」。
        var row = RealRow(canDisableThinking: null);
        row["reasoning"]!["summary"] = "auto";

        var model = Parse(row);

        Assert.False(model.CanDisableThinking);
    }

    [Fact]
    public void reasoning整个不存在时canDisableThinking是null()
    {
        // 不编造 true —— 「没有这个信息」比「显示一个猜的 true」诚实
        var row = new JsonObject { ["id"] = "x", ["name"] = "X" };

        Assert.Null(Parse(row).CanDisableThinking);
    }

    // ══════════════════════════════════════════════════════════
    // ③ contextWindow / maxTokens / supportsImages
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 上下文窗口与最大输出透出()
    {
        var model = Parse(RealRow(contextWindow: 1_000_000, maxTokens: 64_000));

        Assert.Equal(1_000_000, model.ContextWindow);
        Assert.Equal(64_000, model.MaxTokens);
    }

    [Fact]
    public void 数字给成字符串也认()
    {
        var row = RealRow();
        row["contextWindow"] = "192000";

        Assert.Equal(192_000, Parse(row).ContextWindow);
    }

    [Fact]
    public void 图片支持透出()
    {
        Assert.True(Parse(RealRow(supportsImages: true)).SupportsImages);
        Assert.False(Parse(RealRow(supportsImages: false)).SupportsImages);
    }

    // ══════════════════════════════════════════════════════════
    // ④ 缺失字段**省略键**（不是填 null/0）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 全空的模型只有四个既有键加name()
    {
        var model = Parse(new JsonObject { ["id"] = "bare", ["name"] = "Bare" });

        var json = model.ToJson("workbuddy");

        // ★ 没有任何元数据键
        foreach (var key in (string[])["contextWindow", "maxTokens", "supportsImages",
                                      "credits", "free", "badges", "supportedEfforts",
                                      "defaultEffort", "supportsReasoning", "onlyReasoning",
                                      "canDisableThinking"])
        {
            Assert.False(json.ContainsKey(key), $"缺失的字段不该出现键：{key}");
        }

        // 但既有四键 + name 必须在
        Assert.Equal("bare", json["id"]!.GetValue<string>());
        Assert.Equal("model", json["object"]!.GetValue<string>());
        Assert.Equal(0, json["created"]!.GetValue<int>());
        Assert.Equal("workbuddy", json["owned_by"]!.GetValue<string>());
        Assert.True(json.ContainsKey("name"));
    }

    [Fact]
    public void 缺失的数值不写成0()
    {
        // ★★ 这条是核心：「上下文是 0」和「上游没告诉我上下文」是两回事。
        //    填 0 的话界面会显示「0 tokens」—— 一个看起来像故障的假数字。
        var model = Parse(new JsonObject { ["id"] = "x", ["name"] = "X" });

        var json = model.ToJson("workbuddy");

        Assert.False(json.ContainsKey("contextWindow"));
        Assert.False(json.ContainsKey("maxTokens"));
        Assert.Null(model.ContextWindow);
    }

    [Fact]
    public void 缺失的布尔不写成false()
    {
        var model = Parse(new JsonObject { ["id"] = "x", ["name"] = "X" });

        var json = model.ToJson("workbuddy");

        Assert.False(json.ContainsKey("supportsImages"));
        Assert.False(json.ContainsKey("free"));
        Assert.Null(model.SupportsImages);
        Assert.Null(model.Free);
    }

    [Fact]
    public void 空徽标列表省略键而不是空数组()
    {
        // 空数组会让界面渲染一个空的行；省略键它就知道"没有这一项"
        var model = Parse(RealRow(badges: []));

        var json = model.ToJson("workbuddy");

        Assert.False(json.ContainsKey("badges"));
    }

    [Fact]
    public void 序列化后不含null值()
    {
        // 整份 JSON 里不该出现 null —— 「省略键」意味着真的一个 null 都没有
        var model = Parse(new JsonObject { ["id"] = "x", ["name"] = "X" });

        var text = model.ToJson("workbuddy").ToJsonString();

        Assert.DoesNotContain("null", text, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ 既有客户端兼容
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 只读id与name的客户端不受影响()
    {
        var model = Parse(RealRow());
        var json = model.ToJson("workbuddy");

        // 既有四键的**取值类型与语义**都没变
        Assert.Equal("hy4-preview", json["id"]!.GetValue<string>());
        Assert.Equal("model", json["object"]!.GetValue<string>());
        Assert.Equal(0, json["created"]!.GetValue<int>());
        Assert.Equal("workbuddy", json["owned_by"]!.GetValue<string>());
        Assert.Equal("Hy4 preview", json["name"]!.GetValue<string>());
    }

    [Fact]
    public void 元数据是额外键_不影响既有键的存在()
    {
        var json = Parse(RealRow()).ToJson("workbuddy");

        // 元数据齐了，四个既有键仍然在
        Assert.True(json.ContainsKey("contextWindow"));
        Assert.True(json.ContainsKey("id"));
        Assert.True(json.ContainsKey("object"));
    }

    // ══════════════════════════════════════════════════════════
    // ⑥ 目录整体渲染
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 内置清单没有元数据_如实反映不知道()
    {
        var catalog = new ModelCatalog("workbuddy", "hy3");

        var array = catalog.ToJsonArray();

        Assert.Equal(ModelCatalog.BuiltIn.Count, array.Count);

        // 内置清单只有 id/name —— 不该凭空编造元数据
        var first = array[0]!.AsObject();
        Assert.False(first.ContainsKey("contextWindow"));
        Assert.False(first.ContainsKey("credits"));
    }

    [Fact]
    public void 刷入带元数据的目录后能透出()
    {
        var catalog = new ModelCatalog("workbuddy", "hy3");

        // ⚠ `RealRow()` 的 `efforts` 参数默认是 null —— 不传就不会写出
        //   `supportedEfforts` 键，于是下面那条断言拿到 null 抛 NRE。
        //   （captain 修：测试数据与断言不匹配，不是实现缺陷 ——
        //     实现侧 `ModelCatalog.cs:107` 对空列表**故意省略该键**，
        //     这与「字段缺失省略键」的既定纪律一致。）
        catalog.Replace([Parse(RealRow(efforts: ["high"]))]);

        var array = catalog.ToJsonArray();

        // ⚠ 用 Count + 索引，不用 Assert.Single —— JsonArray 装的是 JsonNode?，
        //   泛型重载会推断成可空而拿到 null（我第一版就栽在这）
        Assert.Equal(1, array.Count);
        var model = array[0]!.AsObject();

        Assert.Equal("x0.29", model["credits"]!.GetValue<string>());
        Assert.Equal(1_000_000, model["contextWindow"]!.GetValue<int>());

        var efforts = model["supportedEfforts"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["high"], efforts);
    }

    [Fact]
    public void 渲染用的是传进来的ownedBy()
    {
        var catalog = new ModelCatalog("workbuddy-ai", "hy3");

        var first = catalog.ToJsonArray()[0]!.AsObject();

        Assert.Equal("workbuddy-ai", first["owned_by"]!.GetValue<string>());
    }

    [Fact]
    public void Resolve与元数据无关仍然只看id()
    {
        var catalog = new ModelCatalog("workbuddy", "hy3");
        catalog.Replace([Parse(RealRow(id: "hy4-preview", name: "Hy4 preview"))]);

        Assert.Equal("hy4-preview", catalog.Resolve("hy4-preview"));
        Assert.Equal("hy3", catalog.Resolve("不存在的模型"));
    }
}
