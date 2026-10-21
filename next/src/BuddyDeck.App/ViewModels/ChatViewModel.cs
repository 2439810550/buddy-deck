using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Media.Imaging;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.ViewModels;

/// <summary>
/// 聊天测试页。
///
/// 它不是聊天客户端 —— 它是「网关通不通」的探针。
/// 所以界面重点在模型名、耗时、失败原因，而不是对话历史的美观。
///
/// ## 这个文件只做「绑定 glue」
///
/// 纯逻辑（块模型、媒体 URL 识别、尺寸约定、安全边界）全在
/// <see cref="ChatMedia"/> / <see cref="MediaUrlGuard"/> 里 —— 那样才可测。
/// 这里只负责：把逻辑的结果搬到可观察属性上、驱动流式循环、落盘视频。
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;

    public ChatViewModel(IGatewayClient gateway)
    {
        _gateway = gateway;

        // ⚠ **必须把「集合变化」转成「属性变化」**。
        //
        // `IsEmpty` / `HasContent` / `Output` 都是 `Blocks` 的**派生属性**，
        // 但 `ObservableCollection` 变化时只会通知 `Blocks` 自己 ——
        // 派生属性**不会**自动收到通知，永远停在初始值。
        //
        // 后果（用户实测反馈「消息框该加的滚动条也没加」）：
        //   XAML 里 `ScrollViewer.Visibility` 绑在 `IsEmpty` 上，
        //   而 `IsEmpty` 一直是初值 `true`（隐藏）——
        //   **消息来了、`Blocks` 有内容了，滚动区却仍然是隐藏的**，
        //   用户看到的就是"消息显示不出来、也没有滚动条"。
        //
        // ⚠ 这类 bug 编译期完全看不出来（绑定名对、类型对、就是不发通知），
        //   只能靠真跑一次发现 —— 与 `const` 绑定失败、`StaticResource` 找不到同族。
        Blocks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasContent));
            OnPropertyChanged(nameof(Output));
        };
    }

    public ObservableCollection<ModelInfo> Models { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private ModelInfo? _selectedModel;

    [ObservableProperty]
    private string _input = "";

    /// <summary>
    /// 回复的**内容块流**（有序：文本块 / 图片块 / 视频块）。
    ///
    /// 从「一个 Output 字符串」改成块集合 —— 这是本任务的核心改动。
    /// </summary>
    public ObservableCollection<ChatBlock> Blocks { get; } = [];

    /// <summary>
    /// 纯文本投影。
    ///
    /// 保留它有两个用处：① `--selftest` 等诊断工具仍能拿到可打印文本；
    /// ② 判断「有没有收到文字」时不用遍历块。
    /// </summary>
    public string Output => ChatMedia.ToPlainText(Blocks);

    /// <summary>
    /// 有没有任何内容（含媒体）。
    /// 空态判断用它而不是 <see cref="Output"/> —— **只有图没有字也是"有内容"**。
    /// </summary>
    public bool HasContent => Blocks.Count > 0;

    /// <summary>没内容时显示空态。绑在 XAML 上。</summary>
    public bool IsEmpty => Blocks.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(SendButtonText))]
    private bool _isStreaming;

    public bool CanSend => !IsStreaming && SelectedModel is not null && Input.Trim().Length > 0;

    public string SendButtonText => IsStreaming ? "停止" : "发送";

    /// <summary>模型目录读不到时的那句话 —— 不说「暂无数据」。</summary>
    [ObservableProperty]
    private string _modelsNotice = "";

    [ObservableProperty]
    private string _elapsed = "";

    [ObservableProperty]
    private bool _lastRunFailed;

    private CancellationTokenSource? _streamCts;

    /// <summary>本轮落盘的媒体文件 —— 下次发送前清掉，避免临时目录堆积。</summary>
    private readonly List<string> _tempMediaFiles = [];

    partial void OnInputChanged(string value) => OnPropertyChanged(nameof(CanSend));

    [RelayCommand]
    public async Task LoadModelsAsync(CancellationToken ct = default)
    {
        var result = await _gateway.GetModelsAsync(ct).ConfigureAwait(true);
        Models.Clear();

        if (!result.Ok || result.Value is null)
        {
            ModelsNotice = result.Error ?? "网关没有给出模型列表。";
            OnPropertyChanged(nameof(CanSend));
            return;
        }

        foreach (var m in result.Value) Models.Add(m);

        ModelsNotice = Models.Count == 0 ? "网关回了空模型列表。" : "";

        if (SelectedModel is null && Models.Count > 0) SelectedModel = Models[0];
        OnPropertyChanged(nameof(CanSend));
    }

    /// <summary>
    /// 发送 / 停止。同一颗按钮两种职责，因为流式的时候用户要的就是「别说了」。
    /// </summary>
    [RelayCommand]
    public async Task SendOrStopAsync()
    {
        if (IsStreaming)
        {
            _streamCts?.Cancel();
            return;
        }

        if (!CanSend) return;

        var model = SelectedModel?.Id ?? "";
        var prompt = Input.Trim();

        ResetForNewRun();
        IsStreaming = true;

        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var pendingText = new StringBuilder();

        try
        {
            await foreach (var chunk in _gateway.ChatStreamAsync(model, prompt, ct).ConfigureAwait(true))
            {
                if (ct.IsCancellationRequested) break;

                if (ChatMedia.TryExtractMediaUrl(chunk, _gateway.Port, out var mediaUrl))
                {
                    // 文本先落地，再插媒体块 —— 保持「文字在前、图在后」的自然顺序。
                    FlushPendingText(pendingText);
                    AppendMediaBlock(mediaUrl);
                }
                else
                {
                    pendingText.Append(chunk);
                    AppendTextChunk(pendingText);
                }
            }
        }
        finally
        {
            sw.Stop();
            FlushPendingText(pendingText);
            IsStreaming = false;
            Elapsed = AccountPresentation.FormatLatency(sw.Elapsed.TotalMilliseconds);

            LastRunFailed = Blocks.Count == 0;
            if (LastRunFailed)
                AppendText("没有收到任何内容。看看「日志」页网关说了什么。");

            NotifyContentChanged();
            _streamCts?.Dispose();
            _streamCts = null;
        }
    }

    /// <summary>开始新一轮之前清干净 —— 包括上一轮落盘的临时媒体文件。</summary>
    private void ResetForNewRun()
    {
        Blocks.Clear();
        LastRunFailed = false;
        Elapsed = "";

        foreach (var f in _tempMediaFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); }
            catch (IOException) { /* 删不掉就算了，系统会清临时目录 */ }
            catch (UnauthorizedAccessException) { }
        }
        _tempMediaFiles.Clear();

        NotifyContentChanged();
    }

    private void FlushPendingText(StringBuilder pending)
    {
        if (ChatMedia.FlushText(pending) is { } block)
        {
            Blocks.Add(block);
            NotifyContentChanged();
        }
    }

    /// <summary>
    /// 流式追加。
    ///
    /// 文本块不可变（`Text` 只读），所以追加是**替换最后一个块**。
    /// 为什么不做成可变属性：那样每来一个增量就发一次属性变更，
    /// WPF 要重排整个块流；替换最后一个元素的开销小得多。
    /// </summary>
    private void AppendTextChunk(StringBuilder pending)
    {
        if (pending.Length == 0) return;

        if (Blocks.LastOrDefault() is TextBlock0 last)
            Blocks[Blocks.Count - 1] = new TextBlock0(last.Text + pending);
        else
            Blocks.Add(new TextBlock0(pending.ToString()));

        pending.Clear();
        NotifyContentChanged();
    }

    private void AppendText(string text)
    {
        if (Blocks.LastOrDefault() is TextBlock0 last)
            Blocks[Blocks.Count - 1] = new TextBlock0(last.Text + text);
        else
            Blocks.Add(new TextBlock0(text));

        NotifyContentChanged();
    }

    /// <summary>
    /// 插入一个媒体块并**异步**把它取回来。
    ///
    /// 「异步」是关键：不阻塞流式接收。取媒体期间那块显示固定尺寸的占位，
    /// 拿到了再填进去 —— 布局不跳（占位与最终同尺寸）。
    /// </summary>
    private void AppendMediaBlock(string url)
    {
        Blocks.Add(ChatMedia.CreateBlock(url, media: null));
        NotifyContentChanged();

        _ = FillMediaAsync(Blocks[^1]);
    }

    /// <summary>把媒体取回来填进块里。失败就标错误，**不抛**。</summary>
    private async Task FillMediaAsync(ChatBlock block)
    {
        if (block is not MediaBlock media) return;

        var result = await _gateway.FetchMediaAsync(media.Url).ConfigureAwait(true);

        if (!result.Ok || result.Value is null)
        {
            media.MarkFailed(result.Error ?? "取不到这个媒体。");
            NotifyContentChanged();
            return;
        }

        var bytes = result.Value;

        // 下载后用 MIME 复核类型（URL 后缀那次只是粗判）
        if (bytes.IsVideo && media is not VideoBlock)
        {
            ReplaceBlock(media, new VideoBlock(media.Url, ""));
            return;   // 替换后由下面新块的路径继续填
        }

        try
        {
            if (bytes.IsVideo)
            {
                // 视频要先落盘 —— MediaElement 需要一个可定位的源
                var path = Path.Combine(Path.GetTempPath(), $"buddydeck-media-{Guid.NewGuid():N}.mp4");
                await File.WriteAllBytesAsync(path, bytes.Data).ConfigureAwait(true);
                _tempMediaFiles.Add(path);

                ReplaceBlock(media, new VideoBlock(media.Url, path));
            }
            else
            {
                // 图片：**从内存解码**，不落盘。
                // 字节本来就已经在手上（FetchMediaAsync 返回 byte[]），
                // 再写盘读回来是白绕一圈，还留下要清理的临时文件。
                // SetImage 内部会 Freeze() —— 见 ImageBlock 的注释，
                // 不冻结的话跨线程绑定会抛。
                if (media is ImageBlock img)
                {
                    img.SetImage(DecodeImage(bytes.Data));
                }

                media.MarkReady(ChatMedia.FormatSize(bytes.Data.Length));
            }

            NotifyContentChanged();
        }
        catch (IOException ex)
        {
            media.MarkFailed($"媒体写盘失败：{ex.Message}");
            NotifyContentChanged();
        }
        catch (NotSupportedException)
        {
            // 解码失败 —— 最常见的是「这其实不是图片」（比如返回了 JSON 错误体）
            media.MarkFailed("这个文件不是能显示的图片或视频。");
            NotifyContentChanged();
        }
    }

    /// <summary>把一个块换成另一个（用于「粗判是图片、实际是视频」的纠正）。</summary>
    private void ReplaceBlock(ChatBlock old, ChatBlock replacement)
    {
        var index = Blocks.IndexOf(old);
        if (index < 0) return;

        Blocks[index] = replacement;
        NotifyContentChanged();

        // 新块还要继续填数据
        if (replacement is MediaBlock mb && mb.IsLoading) _ = FillMediaAsync(mb);
    }

    /// <summary>
    /// 把字节解码成位图。
    ///
    /// `BitmapCacheOption.OnLoad` + `Freeze()` 是必须的：
    ///   · `OnLoad` —— 立刻读完，不持有原流（否则 MemoryStream 释放后图就空）
    ///   · `Freeze()` —— 冻结后可跨线程用，也少一层 WPF 跟踪开销
    /// 少了这两个会出现「有图但显示不出来」这种很难查的现象。
    /// </summary>
    internal static BitmapImage DecodeImage(byte[] data)
    {
        using var ms = new MemoryStream(data);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void NotifyContentChanged()
    {
        OnPropertyChanged(nameof(Output));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
