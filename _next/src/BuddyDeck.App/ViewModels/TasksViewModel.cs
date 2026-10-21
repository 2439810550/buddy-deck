using System.Collections.ObjectModel;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.ViewModels;

/// <summary>
/// 任务库 —— 右侧可收起的面板，展示后台任务的进度。
///
/// ## 为什么需要（用户需求）
///
/// 批量拉积分 / 签到 / 续期是**费时操作**（拉 20 个号积分要几十秒）。
/// 这些不该阻塞 UI 线程，也不该让用户盯着一个"保存中"的按钮。
/// 正确做法：点按钮 → 触发后台任务 → 任务库显示进度条 + 实时描述。
///
/// 这个 VM 用定时器轮询 `/admin/tasks`，实时刷新进度。
/// </summary>
public sealed partial class TasksViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private System.Windows.Threading.DispatcherTimer? _timer;

    public TasksViewModel(IGatewayClient gateway)
    {
        _gateway = gateway;
    }

    /// <summary>任务列表（最近 50 个，倒序）。</summary>
    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];

    /// <summary>面板是否展开。</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>有没有正在运行的任务（界面据此显示"进行中"的标记）。</summary>
    [ObservableProperty]
    private bool _hasRunning;

    /// <summary>正在运行的任务数。</summary>
    [ObservableProperty]
    private int _runningCount;

    /// <summary>是否在轮询中。</summary>
    [ObservableProperty]
    private bool _isPolling;

    /// <summary>开始轮询（App 启动后调一次）。</summary>
    public void StartPolling()
    {
        if (_timer is not null) return;

        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += async (_, _) => await PollAsync().ConfigureAwait(true);
        _timer.Start();

        _ = PollAsync();
    }

    /// <summary>展开/收起面板。</summary>
    [RelayCommand]
    public void Toggle() => IsOpen = !IsOpen;

    /// <summary>
    /// 拉一次任务列表。
    ///
    /// ⚠ 只拉最近 50 条（数据库最多保留 200 条，见 TaskHost.PruneIfNeeded）。
    ///
    /// ⚠ **增量更新，绝不 Clear 重建**：之前每次轮询都 Tasks.Clear() 再 Add，
    /// 会导致：① 展开详情的行被移除再重建，详情动画（Loaded）反复触发；
    /// ② 每秒一次的全量重建拖垮 UI 线程（任务运行时尤其明显）——
    /// 这正是「打卡后任务卡死、关不掉、详情没了」的根因。
    /// </summary>
    public async Task PollAsync(CancellationToken ct = default)
    {
        if (IsPolling) return;
        IsPolling = true;
        try
        {
            var result = await _gateway.GetTasksAsync(limit: 50, ct).ConfigureAwait(true);

            if (!result.Ok || result.Value is null)
            {
                return;   // 网关没起/端点没实现 —— 保持现状，不打扰
            }

            // ① 按 id 建字典，旧行复用（保留展开状态和事件流）。
            var byId = new Dictionary<string, TaskRowViewModel>(StringComparer.Ordinal);
            foreach (var existing in Tasks) byId[existing.Id] = existing;

            // ② 服务器返回的 id 集合（倒序，与展示顺序一致）。
            var incomingIds = result.Value.Select(t => t.Id).ToList();

            // ③ 移除「服务器已不再返回」的行（比如被 Prune 掉的旧任务）。
            for (var i = Tasks.Count - 1; i >= 0; i--)
            {
                if (!incomingIds.Contains(Tasks[i].Id))
                {
                    Tasks.RemoveAt(i);
                }
            }

            // ④ 按服务器顺序重建索引（复用旧对象，不 Clear）。
            //    只在顺序变化或新任务插入时移动元素，避免无谓的集合变更通知。
            var target = new List<TaskRowViewModel>(incomingIds.Count);
            foreach (var id in incomingIds)
            {
                if (!byId.TryGetValue(id, out var row))
                {
                    // 新任务：从 result 里找到对应 info 建行。
                    var info = result.Value.First(t => t.Id == id);
                    row = new TaskRowViewModel(info);
                }
                else
                {
                    // 旧任务：更新进度/描述。
                    var info = result.Value.First(t => t.Id == id);
                    row.Apply(info);
                }
                target.Add(row);
            }

            // ⑤ 只有在顺序/内容变化时才重建集合（避免每次轮询都触发全套变更通知）。
            if (!SequenceEqual(Tasks, target))
            {
                Tasks.Clear();
                foreach (var row in target) Tasks.Add(row);
            }

            RunningCount = Tasks.Count(t => t.IsRunning);
            HasRunning = RunningCount > 0;

            // ★ 展开中的详情**实时刷新**：任务运行中事件流不断增长。
            foreach (var open in Tasks.Where(t => t.IsOpen && t.IsRunning))
            {
                await RefreshOpenDetailAsync(open, ct).ConfigureAwait(true);
            }
        }
        finally
        {
            IsPolling = false;
        }
    }

    /// <summary>两个集合是否按序相等（id 序列相同 = 无需重建）。</summary>
    private static bool SequenceEqual(
        System.Collections.ObjectModel.ObservableCollection<TaskRowViewModel> a,
        List<TaskRowViewModel> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i], b[i])) return false;
        }
        return true;
    }

    /// <summary>刷新一个展开中任务的详情事件流（不改变展开状态）。</summary>
    private async Task RefreshOpenDetailAsync(TaskRowViewModel row, CancellationToken ct)
    {
        var result = await _gateway.GetTaskDetailAsync(row.Id, ct).ConfigureAwait(true);
        if (result.Ok && result.Value is not null)
        {
            row.FillEvents(result.Value.Events);
        }
    }

    /// <summary>触发一个后台任务（refresh-credits / checkin-all / renew-all）。</summary>
    public async Task<bool> StartAsync(string kind, CancellationToken ct = default)
    {
        var result = await _gateway.StartTaskAsync(kind, ct).ConfigureAwait(true);
        if (result.Ok)
        {
            IsOpen = true;   // 触发任务后自动展开任务库，让用户看到进度
            await PollAsync(ct).ConfigureAwait(true);
            return true;
        }
        return false;
    }

    // ══ 任务详情（事件流）══════════════════════════════════

    /// <summary>点一个任务：展开/收起它的详情。</summary>
    public async Task ToggleDetailAsync(TaskRowViewModel row, CancellationToken ct = default)
    {
        if (row.IsOpen)
        {
            // 收起：清掉这一行的详情状态。
            row.CloseDetail();
            return;
        }

        // 先把其它行都收起（一次只展开一个，列表清爽）。
        foreach (var other in Tasks)
        {
            if (other != row) other.CloseDetail();
        }

        row.OpenDetail();

        var result = await _gateway.GetTaskDetailAsync(row.Id, ct).ConfigureAwait(true);
        if (!result.Ok || result.Value is null)
        {
            row.CloseDetail();
            return;
        }

        row.FillEvents(result.Value.Events);
    }

    /// <summary>收起全部详情。</summary>
    [RelayCommand]
    public void CloseDetail()
    {
        foreach (var row in Tasks) row.CloseDetail();
    }
}

/// <summary>任务库里的一行（进度条 + 实时描述）。</summary>
public sealed partial class TaskRowViewModel : ObservableObject
{
    public TaskRowViewModel(TaskInfo info)
    {
        Id = info.Id;
        Title = info.Title;
        Apply(info);
    }

    public string Id { get; }
    public string Title { get; }
    public string Kind { get; private set; } = "";

    [ObservableProperty]
    private string _status = "pending";

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private string _description = "";

    [ObservableProperty]
    private string _error = "";

    /// <summary>是不是还在跑（pending + running）。</summary>
    public bool IsRunning => Status is "pending" or "running";

    /// <summary>任务状态的中文标签。</summary>
    public string StatusText => Status switch
    {
        "pending" => "排队中",
        "running" => "进行中",
        "done" => "完成",
        "failed" => "失败",
        "cancelled" => "已取消",
        _ => Status,
    };

    /// <summary>状态对应的进度条颜色（界面用）。</summary>
    public string StatusBrushKey => Status switch
    {
        "done" => "OkBrush",
        "failed" => "StopBrush",
        _ => "AccentBrush",
    };

    public void Apply(TaskInfo info)
    {
        Kind = info.Kind;
        Status = info.Status;
        Progress = info.Progress;
        Description = info.Description;
        Error = info.Error ?? "";

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrushKey));
    }

    // ══ 详情（点这行展开，事件流内联在这行下面）════════

    /// <summary>这行的详情是否展开。</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>详情事件流（按时间升序）。</summary>
    public ObservableCollection<TaskEventViewModel> Events { get; } = [];

    /// <summary>展开这行的详情。</summary>
    public void OpenDetail() => IsOpen = true;

    /// <summary>收起这行的详情。</summary>
    public void CloseDetail()
    {
        IsOpen = false;
        Events.Clear();
    }

    /// <summary>
    /// 把事件流填进这行。
    ///
    /// ⚠ 增量更新：按 <c>Seq</c> 判断，只追加**新出现**的事件。
    /// 任务运行中轮询每秒都会刷详情，如果每次 Clear+重填，
    /// 整列表重建会闪烁，滚动位置也会跳。增量更新只加新行。
    /// </summary>
    public void FillEvents(IReadOnlyList<TaskEventInfo> events)
    {
        var existingSeq = Events.Count > 0 ? Events[^1].Seq : 0;
        foreach (var e in events)
        {
            if (e.Seq <= existingSeq) continue;   // 已经展示过
            Events.Add(new TaskEventViewModel(e));
        }
    }
}

/// <summary>任务流里的一条事件（详情面板里的一行）。</summary>
public sealed class TaskEventViewModel
{
    public TaskEventViewModel(TaskEventInfo info)
    {
        Seq = info.Seq;
        Time = info.Ts > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(info.Ts).ToLocalTime().ToString("HH:mm:ss")
            : "—";
        Level = info.Level;
        Message = info.Message;
        ProgressText = info.Progress is { } p ? $"{p}%" : "";
    }

    public int Seq { get; }
    public string Time { get; }
    public string Level { get; }
    public string Message { get; }
    public string ProgressText { get; }

    /// <summary>失败/告警的那条用醒目色（界面里做触发器）。</summary>
    public bool IsError => Level == "error";
    public bool IsWarn => Level == "warn";
}
