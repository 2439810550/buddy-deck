using System.Collections.Generic;
using System.Linq;

namespace BuddyDeck.App.Services;

/// <summary>
/// 模型页的**纯逻辑**（排序、行模型）。
///
/// ## 为什么它不在 ViewModels/ 下
///
/// 测试项目刻意不 <c>ProjectReference</c> BuddyDeck.App（它是 WinExe +
/// net8.0-windows + WPF，引用它会和 xunit 的 STA/MTA 打架），只用
/// <c>&lt;Compile Include&gt;</c> 逐个链接**纯逻辑**文件。
///
/// ⚠ **链接 ViewModel 走不通**：<c>[ObservableProperty]</c> /
/// <c>[RelayCommand]</c> 依赖 CommunityToolkit 的**源生成器在编译期展开**，
/// 而源生成器**不会为 Compile Include 进来的文件跑**（那属于另一个程序集的编译），
/// 结果是 CS0246 + CS0759（分部方法没有实现声明）—— **机制性的，加包也解决不了**。
///
/// 所以 ViewModel 只留绑定 glue，能测的东西放这里。
/// （同样的切分见 <c>Services/ChatMedia.cs</c>，那是 t62 走过同一条路的结果。）
/// </summary>

/// <summary>排序依据。</summary>
public enum ModelSortKey
{
    /// <summary>倍率升序（便宜的在前）。未知倍率的排最后。</summary>
    MultiplierAsc,

    /// <summary>倍率降序。未知倍率仍排最后（不掺进排序里）。</summary>
    MultiplierDesc,

    NameAsc,
    IdAsc,

    /// <summary>能推理的排前面，同组内按名称。</summary>
    ReasoningFirst,
}

/// <summary>排序选项的一项。</summary>
/// <param name="Key">排序依据。</param>
/// <param name="Name">界面上的说法。</param>
public sealed record ModelSort(ModelSortKey Key, string Name);

/// <summary>
/// 排序逻辑 —— **抽成静态类，脱离界面可测**。
///
/// ## 「未知值排最后」是刻意的，两条排序都遵守
///
/// 倍率未知的模型**不能**当成 0 参与排序 —— 那会让它在「从低到高」
/// 里排到最前面，**看起来像「最便宜」，而实际是「不知道」**。
/// 所以未知值一律沉到最后，且**两个方向都沉底**（不随升降序翻转）。
/// </summary>
public static class ModelOrdering
{
    /// <summary>
    /// 按名称/id 筛选。**纯函数**，不碰网络 —— 所以可以脱离界面测。
    ///
    /// 空串/纯空白 = 不过滤（返回全部）。
    /// </summary>
    public static IEnumerable<ModelRow> Filter(IEnumerable<ModelRow> rows, string? search)
    {
        var q = search?.Trim();
        if (string.IsNullOrEmpty(q)) return rows;

        return rows.Where(r =>
            r.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<ModelRow> Sort(IEnumerable<ModelRow> rows, ModelSortKey key)
        => key switch
        {
            ModelSortKey.MultiplierAsc => rows
                .OrderBy(r => r.HasMultiplier ? 0 : 1)
                .ThenBy(r => r.Multiplier ?? double.MaxValue)
                .ThenBy(r => r.SortName, StringComparer.OrdinalIgnoreCase),

            ModelSortKey.MultiplierDesc => rows
                .OrderBy(r => r.HasMultiplier ? 0 : 1)
                .ThenByDescending(r => r.Multiplier ?? double.MinValue)
                .ThenBy(r => r.SortName, StringComparer.OrdinalIgnoreCase),

            ModelSortKey.NameAsc => rows
                .OrderBy(r => r.SortName, StringComparer.OrdinalIgnoreCase),

            ModelSortKey.IdAsc => rows
                .OrderBy(r => r.Id, StringComparer.Ordinal),

            ModelSortKey.ReasoningFirst => rows
                .OrderBy(r => r.ReasoningRank)
                .ThenBy(r => r.SortName, StringComparer.OrdinalIgnoreCase),

            _ => rows,
        };
}

/// <summary>
/// 一行的展示模型。
///
/// **刻意不直接绑 <see cref="ModelInfo"/>** —— 界面上要显示的倍率/档位不在
/// 那个 record 里，包一层比改公共契约安全。
///
/// ## 为什么是 record 而不是 class
///
/// 将来网关把计费信息透出来时，调用方要能这样补字段：
/// <code>ModelRow.From(info) with { Multiplier = 1.5 }</code>
/// 用 class + 只读属性做不到（测试与将来的接线都会很别扭）。
/// record 还自带值相等 —— 列表刷新时便于比较。
/// </summary>
public sealed record ModelRow
{
    private ModelRow(ModelInfo source)
    {
        Id = source.Id;
        DisplayName = string.IsNullOrWhiteSpace(source.DisplayName) ? source.Id : source.DisplayName;
    }

    public static ModelRow From(ModelInfo source) => new(source);

    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>排序用的名字：优先显示名，回退 id。</summary>
    public string SortName => DisplayName;

    /// <summary>
    /// 计费倍率。**null = 不知道**（不等于 0）。
    /// </summary>
    public double? Multiplier { get; init; }

    /// <summary>倍率是不是真的拿到了。</summary>
    public bool HasMultiplier => Multiplier is not null;

    /// <summary>倍率的显示文本。拿不到时说「—」而不是编一个数。</summary>
    public string MultiplierText => Multiplier is { } m
        ? m.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "×"
        : "—";

    /// <summary>
    /// 是否支持推理。null = 不知道（目录没给 / 还没探测）。
    /// **不用 false 兜底** —— false 是「明确不支持」，和「不知道」是两回事。
    /// </summary>
    public bool? SupportsReasoning { get; init; }

    /// <summary>推理支持的显示文本。</summary>
    public string ReasoningText => SupportsReasoning switch
    {
        true => "支持",
        false => "不支持",
        _ => "未知",
    };

    /// <summary>推理档位（如 low / medium / high）。空 = 没拿到。</summary>
    public IReadOnlyList<string> Efforts { get; init; } = [];

    /// <summary>档位的显示文本。</summary>
    public string EffortsText => Efforts.Count == 0
        ? "—"
        : string.Join(" / ", Efforts);

    /// <summary>「能推理的排前面」用的序：已知支持的 0，未知 1，明确不支持 2。</summary>
    public int ReasoningRank => SupportsReasoning switch
    {
        true => 0,
        null => 1,
        false => 2,
    };
}
