using System.Collections.ObjectModel;

namespace BuddyDeck.App.ViewModels;

/// <summary>
/// 日志页。
///
/// 网关的日志**只有元数据**（谁、哪个模型、什么结果、多久）——
/// 提示词和响应正文不进日志。这是硬要求，界面上也照着这个前提写文案。
/// </summary>
public sealed partial class LogsViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;

    public LogsViewModel(IGatewayClient gateway) => _gateway = gateway;

    /// <summary>全部读到的行（未过滤）。</summary>
    private readonly List<LogLine> _all = [];

    /// <summary>显示的（过滤后）。</summary>
    public ObservableCollection<LogLine> Lines { get; } = [];

    /// <summary>级别过滤。空 = 全都要。</summary>
    [ObservableProperty]
    private LogLevelFilter? _level;

    public ObservableCollection<LogLevelFilter> Levels { get; } =
    [
        new("", "全部级别"),
        new("Warning", "警告及以上"),
        new("Error", "只看错误"),
        new("Information", "只看信息"),
        new("Debug", "包含调试"),
    ];

    /// <summary>时间过滤：只看最近多久。</summary>
    [ObservableProperty]
    private TimeWindow? _window;

    public ObservableCollection<TimeWindow> Windows { get; } =
    [
        new(TimeSpan.Zero, "不限时间"),
        new(TimeSpan.FromMinutes(5), "最近 5 分钟"),
        new(TimeSpan.FromMinutes(30), "最近 30 分钟"),
        new(TimeSpan.FromHours(2), "最近 2 小时"),
        new(TimeSpan.FromHours(24), "最近一天"),
    ];

    /// <summary>关键字过滤（在消息里找）。</summary>
    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private int _maxLines = 500;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _notice = "";

    [ObservableProperty]
    private bool _noticeIsError;

    /// <summary>过滤后一行都没有时的说明。</summary>
    [ObservableProperty]
    private string _emptyNotice = "还没有读到日志。";

    partial void OnLevelChanged(LogLevelFilter? value) => ApplyFilter();
    partial void OnWindowChanged(TimeWindow? value) => ApplyFilter();
    partial void OnSearchChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _gateway.ReadLogAsync(MaxLines, ct).ConfigureAwait(true);

            if (!result.Ok || result.Value is null)
            {
                Notice = result.Error ?? "读不到日志";
                NoticeIsError = true;
                EmptyNotice = Notice;
                ApplyFilter();
                return;
            }

            Notice = "";
            NoticeIsError = false;

            // ⚠ 不再 `_all.Clear()` —— 那是「打开日志页闪一下全没」的根因：
            //   进程输出（AppendProcessLine）先累积了网关启动日志（端口被占等错误），
            //   打开日志页时 RefreshAsync 一 Clear 就把它们全删了，
            //   然后 HTTP 端点拉回的又是旧/空数据 → 用户看到「有错误信息 → 闪没了」。
            //
            //   正确做法：HTTP 日志**合并**进已有集合，去重后排序，不删进程输出。
            var merged = new List<LogLine>(_all);
            merged.AddRange(result.Value);

            // 去重：按时间戳 + 消息内容（同一条日志 HTTP 和进程输出可能各给一份）。
            var seen = new HashSet<(long, string)>();
            var deduped = new List<LogLine>();
            foreach (var line in merged.OrderBy(l => l.At))
            {
                var key = (line.At.ToUnixTimeMilliseconds(), line.Message);
                if (seen.Add(key)) deduped.Add(line);
            }

            _all.Clear();
            _all.AddRange(deduped);

            // 上限保护
            const int max = 2000;
            if (_all.Count > max)
            {
                _all.RemoveRange(0, _all.Count - max);
            }

            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 网关**子进程**的输出直接进日志页（App 自己拉起的那条）。
    ///
    /// 与 <see cref="RefreshAsync"/>（从网关 HTTP 端点拉）互补：
    /// 这条是子进程的 stdout/stderr —— 网关启动早期、还没能响应 HTTP 时，
    /// 也只有这条能看到它说了什么（端口被占、保险库缺、起不来）。
    ///
    /// ⚠ 线程安全：网关子进程的输出在**线程池线程**上到，
    /// 而 Lines 绑在 UI 上 —— 直接 Add 会跨线程。用 Dispatcher 派回 UI 线程。
    /// </summary>
    public void AppendProcessLine(string line)
    {
        var record = new LogLine
        {
            At = DateTimeOffset.Now,
            Level = "Information",
            Message = line,
        };

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            AppendLocal(record);
        }
        else
        {
            dispatcher.BeginInvoke(() => AppendLocal(record));
        }
    }

    private void AppendLocal(LogLine record)
    {
        _all.Add(record);

        // 上限保护：子进程输出可能很密，不能无界增长。
        const int max = 2000;
        if (_all.Count > max)
        {
            _all.RemoveRange(0, _all.Count - max);
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var levelRank = Rank(Level?.Id ?? "");
        var cutoff = Window is null || Window.Span == TimeSpan.Zero
            ? DateTimeOffset.MinValue
            : DateTimeOffset.Now - Window.Span;
        var q = Search.Trim();

        Lines.Clear();
        foreach (var line in _all)
        {
            if (Rank(line.Level) < levelRank) continue;
            if (line.At != default && line.At.ToLocalTime() < cutoff) continue;
            if (!string.IsNullOrEmpty(q) &&
                !line.Message.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Lines.Add(line);
        }

        EmptyNotice = _all.Count == 0
            ? "网关还没有写日志，或者日志路径不对（去「设置」看看）。"
            : "当前的过滤条件下一行都没有 —— 放宽级别或者时间范围试试。";
    }

    /// <summary>
    /// 日志级别的高低序。数值越大越严重，「警告及以上」就是取 ≥ Warning。
    /// 认不出的级别当成 Information（网关加了新级别时不至于全被滤掉）。
    /// </summary>
    private static int Rank(string level) => level switch
    {
        "Trace" => 0,
        "Debug" => 1,
        "Information" or "Info" => 2,
        "Warning" or "Warn" => 3,
        "Error" => 4,
        "Critical" or "Fatal" => 5,
        _ => 2,
    };
}

/// <summary>级别过滤器的一项。</summary>
/// <param name="Id">Syslog 级别的名字，空 = 不过滤。</param>
/// <param name="Name">界面上的说法。</param>
public sealed record LogLevelFilter(string Id, string Name);

/// <summary>时间窗口过滤器的一项。</summary>
/// <param name="Span">往前看多久；<see cref="TimeSpan.Zero"/> = 不限。</param>
/// <param name="Name">界面上的说法。</param>
public sealed record TimeWindow(TimeSpan Span, string Name);
