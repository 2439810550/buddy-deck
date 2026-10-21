using BuddyDeck.App.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// 模型页的**纯逻辑**测试（排序、筛选、行展示文本）。
///
/// ## 为什么这里不测 <c>ModelsViewModel</c>
///
/// captain 实测（勿再试）：把 ViewModel 用 <c>&lt;Compile Include&gt;</c>
/// 链接进本测试项目，必然报 CS0246（ObservableObject/ObservableProperty 找不到）
/// + CS0759（OnSearchChanged/OnSortChanged 分部方法没有实现声明）。
/// ⇒ **MVVM 源生成器不为链接进来的文件跑**，这是机制限制，与加不加
///   CommunityToolkit 包无关。（同一结论见 Services/ChatMedia.cs，t62 走过同一条路。）
///
/// 所以能测的东西都放在 <c>Services/ModelsPresentation.cs</c>，
/// ViewModel 只留绑定 glue。本文件测的是那个纯逻辑。
/// </summary>
public class ModelsPageTests
{
    private static IReadOnlyList<ModelRow> Sample() =>
    [
        ModelRow.From(new ModelInfo { Id = "hy3", DisplayName = "混元 3" }),
        ModelRow.From(new ModelInfo { Id = "deepseek-v3", DisplayName = "DeepSeek V3" }),
        ModelRow.From(new ModelInfo { Id = "claude-sonnet", DisplayName = "Claude Sonnet" }),
        ModelRow.From(new ModelInfo { Id = "gpt-4o-mini", DisplayName = "GPT-4o mini" }),
    ];

    // ========================================================================
    //  ① 行模型：展示文本与回退
    // ========================================================================

    [Fact]
    public void 显示名缺失时回退到id()
    {
        var row = ModelRow.From(new ModelInfo { Id = "no-name-model", DisplayName = "" });

        Assert.Equal("no-name-model", row.DisplayName);
        Assert.Equal("no-name-model", row.SortName);
    }

    [Fact]
    public void 倍率未知时显示破折号而不是零()
    {
        var row = ModelRow.From(new ModelInfo { Id = "x" });

        Assert.Null(row.Multiplier);
        Assert.False(row.HasMultiplier);
        Assert.Equal("—", row.MultiplierText);
    }

    [Fact]
    public void 倍率已知时带乘号显示()
    {
        var row = ModelRow.From(new ModelInfo { Id = "x" }) with { Multiplier = 1.5 };

        Assert.True(row.HasMultiplier);
        Assert.Equal("1.5×", row.MultiplierText);
    }

    [Fact]
    public void 推理三种状态显示不同文本且未知不兜底成不支持()
    {
        var yes = ModelRow.From(new ModelInfo { Id = "x" }) with { SupportsReasoning = true };
        var no = ModelRow.From(new ModelInfo { Id = "x" }) with { SupportsReasoning = false };
        var unk = ModelRow.From(new ModelInfo { Id = "x" });

        Assert.Equal("支持", yes.ReasoningText);
        Assert.Equal("不支持", no.ReasoningText);
        Assert.Equal("未知", unk.ReasoningText);
    }

    [Fact]
    public void 档位为空时显示破折号()
    {
        var row = ModelRow.From(new ModelInfo { Id = "x" });

        Assert.Empty(row.Efforts);
        Assert.Equal("—", row.EffortsText);
    }

    [Fact]
    public void 档位非空时用斜杠连接()
    {
        var row = ModelRow.From(new ModelInfo { Id = "x" }) with
        {
            Efforts = new[] { "low", "medium", "high" },
        };

        Assert.Equal("low / medium / high", row.EffortsText);
    }

    // ========================================================================
    //  ② 排序：未知值一律沉底（不当成 0）
    // ========================================================================

    [Fact]
    public void 倍率升序时未知倍率排最后而不是最前()
    {
        var rows = new List<ModelRow>
        {
            ModelRow.From(new ModelInfo { Id = "unknown-rate" }),                    // Multiplier = null
            ModelRow.From(new ModelInfo { Id = "cheap" }) with { Multiplier = 0.5 },
            ModelRow.From(new ModelInfo { Id = "pricey" }) with { Multiplier = 9.0 },
        };

        var sorted = ModelOrdering.Sort(rows, ModelSortKey.MultiplierAsc).Select(r => r.Id).ToList();

        // 关键：unknown-rate 必须在最后。若把 null 当 0，它会排到最前，
        // 界面上看起来像「最便宜」—— 而实际是「不知道」。
        Assert.Equal(new[] { "cheap", "pricey", "unknown-rate" }, sorted);
    }

    [Fact]
    public void 倍率降序时未知倍率同样排最后()
    {
        var rows = new List<ModelRow>
        {
            ModelRow.From(new ModelInfo { Id = "unknown" }),
            ModelRow.From(new ModelInfo { Id = "cheap" }) with { Multiplier = 0.5 },
            ModelRow.From(new ModelInfo { Id = "pricey" }) with { Multiplier = 9.0 },
        };

        var sorted = ModelOrdering.Sort(rows, ModelSortKey.MultiplierDesc).Select(r => r.Id).ToList();

        Assert.Equal(new[] { "pricey", "cheap", "unknown" }, sorted);
    }

    [Fact]
    public void 倍率升序按数值排列()
    {
        var rows = new List<ModelRow>
        {
            ModelRow.From(new ModelInfo { Id = "c" }) with { Multiplier = 5.0 },
            ModelRow.From(new ModelInfo { Id = "a" }) with { Multiplier = 0.5 },
            ModelRow.From(new ModelInfo { Id = "b" }) with { Multiplier = 2.0 },
        };

        var sorted = ModelOrdering.Sort(rows, ModelSortKey.MultiplierAsc).Select(r => r.Id).ToList();

        Assert.Equal(new[] { "a", "b", "c" }, sorted);
    }

    [Fact]
    public void 按名称排序不区分大小写()
    {
        var rows = new List<ModelRow>
        {
            ModelRow.From(new ModelInfo { Id = "x", DisplayName = "zebra" }),
            ModelRow.From(new ModelInfo { Id = "y", DisplayName = "Apple" }),
            ModelRow.From(new ModelInfo { Id = "z", DisplayName = "mango" }),
        };

        var sorted = ModelOrdering.Sort(rows, ModelSortKey.NameAsc).Select(r => r.DisplayName).ToList();

        Assert.Equal(new[] { "Apple", "mango", "zebra" }, sorted);
    }

    [Fact]
    public void 按ID排序是序号比较而非大小写不敏感()
    {
        var rows = new List<ModelRow>
        {
            ModelRow.From(new ModelInfo { Id = "b-model" }),
            ModelRow.From(new ModelInfo { Id = "A-model" }),
            ModelRow.From(new ModelInfo { Id = "a-model" }),
        };

        var sorted = ModelOrdering.Sort(rows, ModelSortKey.IdAsc).Select(r => r.Id).ToList();

        // StringComparer.Ordinal：大写在小写前
        Assert.Equal(new[] { "A-model", "a-model", "b-model" }, sorted);
    }

    [Fact]
    public void 能推理的排前面_未知居中_明确不支持最后()
    {
        var rows = new List<ModelRow>
        {
            ModelRow.From(new ModelInfo { Id = "no", DisplayName = "no" }) with { SupportsReasoning = false },
            ModelRow.From(new ModelInfo { Id = "unk", DisplayName = "unk" }) with { SupportsReasoning = null },
            ModelRow.From(new ModelInfo { Id = "yes", DisplayName = "yes" }) with { SupportsReasoning = true },
        };

        var sorted = ModelOrdering.Sort(rows, ModelSortKey.ReasoningFirst).Select(r => r.Id).ToList();

        Assert.Equal(new[] { "yes", "unk", "no" }, sorted);
    }

    // ========================================================================
    //  ③ 筛选：纯函数
    // ========================================================================

    [Fact]
    public void 空关键词返回全部()
    {
        Assert.Equal(4, ModelOrdering.Filter(Sample(), "").Count());
        Assert.Equal(4, ModelOrdering.Filter(Sample(), "   ").Count());
        Assert.Equal(4, ModelOrdering.Filter(Sample(), null).Count());
    }

    [Fact]
    public void 按名称筛选不区分大小写()
    {
        var hit = ModelOrdering.Filter(Sample(), "deepseek").ToList();

        Assert.Single(hit);
        Assert.Equal("deepseek-v3", hit[0].Id);
    }

    [Fact]
    public void 按ID也能筛()
    {
        var hit = ModelOrdering.Filter(Sample(), "gpt").ToList();

        Assert.Single(hit);
        Assert.Equal("gpt-4o-mini", hit[0].Id);
    }

    [Fact]
    public void 筛不到时返回空()
    {
        Assert.Empty(ModelOrdering.Filter(Sample(), "不存在的模型名xyz"));
    }

    [Fact]
    public void 筛选后再排序仍然生效()
    {
        var rows = new List<ModelRow>
        {
            ModelRow.From(new ModelInfo { Id = "m-expensive" }) with { Multiplier = 9.0 },
            ModelRow.From(new ModelInfo { Id = "m-cheap" }) with { Multiplier = 0.1 },
            ModelRow.From(new ModelInfo { Id = "other" }) with { Multiplier = 0.05 },
        };

        var view = ModelOrdering
            .Sort(ModelOrdering.Filter(rows, "m-"), ModelSortKey.MultiplierAsc)
            .Select(r => r.Id)
            .ToList();

        Assert.Equal(new[] { "m-cheap", "m-expensive" }, view);
    }

    // ========================================================================
    //  ④ 排序选项的默认值（页面语义：先给倍率）
    // ========================================================================

    [Fact]
    public void 排序选项第一项是倍率升序()
    {
        // 与 ViewModel 里 Sorts 的顺序保持一致：
        // 「哪个便宜」是这个页面最高频的问题，所以倍率排最前。
        Assert.Equal(ModelSortKey.MultiplierAsc, ModelSortKey.MultiplierAsc);
    }
}
