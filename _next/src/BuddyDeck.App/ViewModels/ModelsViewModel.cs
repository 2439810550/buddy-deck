using System.Collections.ObjectModel;
using System.Linq;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.ViewModels;

/// <summary>
/// 模型页 —— **只留绑定 glue**。
///
/// ## 这一页回答什么问题
///
/// 「我这个号能用的模型里，哪个便宜、哪个能推理、哪个快没了。」
///
/// ## ⚠ 纯逻辑不在本文件
///
/// 排序、行模型、排序枚举都在 <c>Services/ModelsPresentation.cs</c>。
/// 原因：测试项目用 <c>&lt;Compile Include&gt;</c> 链接纯逻辑文件，
/// 而 <c>[ObservableProperty]</c> / <c>[RelayCommand]</c> 依赖的 MVVM
/// **源生成器不会为链接进来的文件跑**（属于另一个程序集的编译）
/// ⇒ CS0246 + CS0759，机制性的、加包也解决不了。
///
/// 所以本文件保持"薄"：只有可观察属性 + 调 Services 的排序/过滤。
/// （同样的切分见 <c>Services/ChatMedia.cs</c>，t62 走过同一条路。）
/// </summary>
public sealed partial class ModelsViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;

    public ModelsViewModel(IGatewayClient gateway) => _gateway = gateway;

    /// <summary>全部读到的模型（未过滤、未排序）。</summary>
    private readonly List<ModelRow> _all = [];

    /// <summary>显示的（过滤 + 排序后）。</summary>
    public ObservableCollection<ModelRow> Models { get; } = [];

    /// <summary>按名称/id 筛选。</summary>
    [ObservableProperty]
    private string _search = "";

    /// <summary>排序方式。</summary>
    [ObservableProperty]
    private ModelSort? _sort;

    /// <summary>
    /// 排序选项。
    ///
    /// 顺序是刻意的：**按倍率**排在前面，因为「哪个便宜」是这个页面
    /// 最高频的问题（对标 manager 的 models 页也是先给倍率）。
    /// </summary>
    public ObservableCollection<ModelSort> Sorts { get; } =
    [
        new(ModelSortKey.MultiplierAsc, "倍率从低到高"),
        new(ModelSortKey.MultiplierDesc, "倍率从高到低"),
        new(ModelSortKey.NameAsc, "名称"),
        new(ModelSortKey.IdAsc, "ID"),
        new(ModelSortKey.ReasoningFirst, "能推理的排前面"),
    ];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _notice = "";

    [ObservableProperty]
    private bool _noticeIsError;

    /// <summary>一行都没有时的说明。</summary>
    [ObservableProperty]
    private string _emptyNotice = "还没有读到模型。";

    /// <summary>模型总数（未过滤）。</summary>
    [ObservableProperty]
    private int _totalCount;

    /// <summary>当前过滤后显示的条数。</summary>
    [ObservableProperty]
    private int _shownCount;

    /// <summary>
    /// 计费倍率是否可用。
    ///
    /// 网关的 /v1/models 只回 id/name 时这个为 false ——
    /// 界面据此把「倍率」那一列显示成「—」并在页头说明原因，
    /// **而不是显示一列看起来合理的假数据**。
    /// </summary>
    [ObservableProperty]
    private bool _billingAvailable;

    /// <summary>倍率不可用时的解释文案。</summary>
    [ObservableProperty]
    private string _billingNotice = "";

    partial void OnSearchChanged(string value) => ApplyView();
    partial void OnSortChanged(ModelSort? value) => ApplyView();

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _gateway.GetModelsAsync(ct).ConfigureAwait(true);
            _all.Clear();

            if (!result.Ok || result.Value is null)
            {
                Notice = result.Error ?? "读不到模型列表";
                NoticeIsError = true;
                TotalCount = 0;
                Models.Clear();
                ShownCount = 0;
                EmptyNotice = Notice;
                return;
            }

            Notice = "";
            NoticeIsError = false;

            foreach (var m in result.Value)
            {
                _all.Add(ModelRow.From(m));
            }

            TotalCount = _all.Count;

            // 倍率数据面的探测：只要有一个模型带上了倍率，就认为可用。
            BillingAvailable = _all.Any(r => r.HasMultiplier);
            BillingNotice = BillingAvailable
                ? ""
                : "网关的 /v1/models 只回 ID 和名称，倍率要等目录带上计费信息才能显示。";

            if (Sort is null) Sort = Sorts[0];

            ApplyView();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 过滤 + 排序。**纯函数式**，不碰网络 —— 所以可以脱离界面测。
    /// </summary>
    private void ApplyView()
    {
        // 过滤与排序都委托给 Services/ModelsPresentation（纯逻辑，可脱离界面测）
        var view = ModelOrdering.Filter(_all, Search);
        view = ModelOrdering.Sort(view, Sort?.Key ?? ModelSortKey.MultiplierAsc);

        Models.Clear();
        foreach (var r in view) Models.Add(r);

        ShownCount = Models.Count;

        EmptyNotice = _all.Count == 0
            ? "还没有读到模型。确认网关在跑，并且账号能拉到目录。"
            : "当前的筛选条件下没有模型 —— 换个关键词试试。";
    }
}
