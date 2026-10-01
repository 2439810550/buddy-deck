using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WorkBuddyPanel.Models;
using WorkBuddyPanel.Services;

namespace WorkBuddyPanel.ViewModels;

/// <summary>模型列表里的一行。</summary>
public sealed class ModelRowVm
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Context { get; init; } = "";
    public string MaxOutput { get; init; } = "";
    public string Images { get; init; } = "";
    public string Rate { get; init; } = "";
    public string Badges { get; init; } = "";
    public string Efforts { get; init; } = "";

    // 原始数值：pi 的供应商配置要写数字，不能用上面那些给人看的 "1M" / "64K"
    public long? ContextWindowRaw { get; init; }
    public long? MaxTokensRaw { get; init; }
    public bool SupportsImagesRaw { get; init; }
    public bool ReasoningRaw { get; init; }
    public IReadOnlyList<string> EffortsRaw { get; init; } = [];

    /// <summary>转成写进 pi 配置的形状。</summary>
    public ModelSpec ToSpec() => new(
        Id, Name, ContextWindowRaw, MaxTokensRaw, SupportsImagesRaw, ReasoningRaw, EffortsRaw);

    /// <summary>下拉框里显示的文本。</summary>
    public string Display => string.IsNullOrEmpty(Badges) ? $"{Name} · {Rate}" : $"{Name} · {Rate} · {Badges}";

    public static ModelRowVm FromDto(ModelDto dto)
    {
        var badges = dto.Badges is { Count: > 0 } ? string.Join(" / ", dto.Badges) : "";
        var efforts = dto.SupportedEfforts is { Count: > 0 } ? string.Join(" / ", dto.SupportedEfforts) : "";

        return new ModelRowVm
        {
            Id = dto.Id ?? "",
            Name = dto.Name ?? dto.Id ?? "",
            Context = FormatTokens(dto.ContextWindow),
            MaxOutput = FormatTokens(dto.MaxTokens),
            Images = dto.SupportsImages == true ? "支持" : "不支持",
            Rate = NormalizeRate(dto.Credits),
            Badges = badges,
            Efforts = efforts,
            ContextWindowRaw = dto.ContextWindow,
            MaxTokensRaw = dto.MaxTokens,
            SupportsImagesRaw = dto.SupportsImages == true,
            ReasoningRaw = dto.Reasoning == true,
            EffortsRaw = dto.SupportedEfforts ?? [],
        };
    }

    /// <summary>把上游的 "x0.79 credits" 收拾成 "x0.79"。</summary>
    public static string NormalizeRate(string? credits)
    {
        if (string.IsNullOrWhiteSpace(credits)) return "—";
        var text = credits.Trim();
        var lowered = text.ToLowerInvariant();
        if (lowered.EndsWith(" credits", StringComparison.Ordinal))
            text = text[..^" credits".Length].Trim();
        else if (lowered == "credits")
            text = "";
        return text.Length == 0 ? "—" : text;
    }

    public static string FormatTokens(long? value)
    {
        if (value is null or <= 0) return "—";
        var v = value.Value;
        if (v >= 1_000_000) return $"{v / 1_000_000.0:0.#}M";
        if (v >= 1_000) return $"{v / 1_000.0:0.#}K";
        return v.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>聊天记录里的一条（用户问 / 助手答）。都会被界面实时更新。</summary>
public sealed class ChatMessageVm : ObservableObject
{
    private string _text = "";
    private string _reasoning = "";
    private bool _isStreaming;

    /// <summary>
    /// 拿来做下载、弹对话框、写状态栏。
    ///
    /// 为什么要有：图片的三个按钮（复制/保存/打开）都要下载那个地址、
    /// 都要能弹文件对话框、都要能把结果写进状态栏 —— 这些能力都在
    /// 主视图模型上。参照 AccountRowVm 的做法，把 owner 传进来。
    /// </summary>
    private readonly MainViewModel? _owner;

    public ChatMessageVm() { }

    public ChatMessageVm(MainViewModel owner) => _owner = owner;

    /// <summary>把图片字节抓下来（带内存缓存，复制/保存/打开共用一次下载）。</summary>
    private byte[]? _imageBytes;

    /// <summary>
    /// 图片操作命令。
    ///
    /// 刻意**不给 CanExecute 判定** —— 这三个按钮的可见性已经绑在 HasImage 上了
    /// （没图时整个操作条不显示），再叠一层 CanExecute 只会引入一个经典毛病：
    /// WPF 只在绑定时问一次 CanExecute，之后属性变了不重新问，
    /// 结果就是「按钮显示了但一直是灰的」。实测踩过这个坑。
    ///
    /// 用 AsyncRelayCommand 是因为它自带「执行期间禁用自己」和错误回调，
    /// 不用在各处手写 try/catch。
    /// </summary>
    public ICommand CopyImageCommand => _copyImage ??= new AsyncRelayCommand(CopyImageAsync, null, ex => _owner?.ReportError(ex));
    public ICommand SaveImageCommand => _saveImage ??= new AsyncRelayCommand(SaveImageAsync, null, ex => _owner?.ReportError(ex));
    public ICommand OpenImageCommand => _openImage ??= new AsyncRelayCommand(OpenImageAsync, null, ex => _owner?.ReportError(ex));

    private ICommand? _copyImage;
    private ICommand? _saveImage;
    private ICommand? _openImage;

    /// <summary>
    /// 下载图片。三个操作共用 —— 点「复制」再点「保存」不会下载两遍。
    ///
    /// 地址来自网关，是带签名的本地地址（不依赖上游那个 1 小时失效的链接）。
    /// </summary>
    private async Task<byte[]?> FetchImageAsync()
    {
        if (_imageBytes is not null) return _imageBytes;
        if (!HasImage) return null;

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var bytes = await http.GetByteArrayAsync(ImageUrl).ConfigureAwait(true);
            _imageBytes = bytes;
            return bytes;
        }
        catch (Exception ex)
        {
            _owner?.ReportError(ex);
            return null;
        }
    }

    /// <summary>
    /// 复制图片到剪贴板。
    ///
    /// 为什么要单独做：WPF 的 Image 控件**不响应 Ctrl+C**，也没有默认右键菜单。
    /// 以前界面上写着「右键可保存到本地」是假的 —— 用户发现图根本复制不了。
    ///
    /// 剪贴板里同时放 PNG 和 Bitmap 两种格式：
    ///   · PNG  → 粘进 Word / 微信 / 浏览器 都能用
    ///   · Bitmap → 粘进画图 / 一些老程序才认
    /// </summary>
    private async Task CopyImageAsync()
    {
        var bytes = await FetchImageAsync();
        if (bytes is null) return;

        try
        {
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;  // 拷完就断开流，别锁文件
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();                                 // 冻结后才能跨线程用
            }

            var data = new DataObject();
            data.SetImage(bitmap);
            // 再塞一份原始 PNG 字节 —— 有些程序只认这个格式
            data.SetData("PNG", new MemoryStream(bytes));

            Clipboard.SetDataObject(data, copy: true);
            _owner?.SetStatus("图片已复制到剪贴板");
        }
        catch (Exception ex)
        {
            _owner?.ReportError(ex);
        }
    }

    private async Task SaveImageAsync()
    {
        var bytes = await FetchImageAsync();
        if (bytes is null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存图片",
            FileName = $"workbuddy-{Time:yyyyMMdd-HHmmss}.png",
            Filter = "PNG 图片|*.png|所有文件|*.*",
            DefaultExt = ".png",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            _owner?.SetStatus($"图片已保存到 {dialog.FileName}");
        }
        catch (Exception ex)
        {
            _owner?.ReportError(ex);
        }
    }

    /// <summary>用系统默认看图程序打开。临时文件放独立目录，避免每次落一地。</summary>
    private async Task OpenImageAsync()
    {
        var bytes = await FetchImageAsync();
        if (bytes is null) return;

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "WorkBuddyPanel");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"image-{Time:yyyyMMdd-HHmmss}.png");
            await File.WriteAllBytesAsync(file, bytes);

            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            _owner?.SetStatus("已用系统看图程序打开");
        }
        catch (Exception ex)
        {
            _owner?.ReportError(ex);
        }
    }

    public bool IsUser { get; init; }
    public string RoleLabel => IsUser ? "我" : "WorkBuddy";
    public DateTime Time { get; } = DateTime.Now;
    public string TimeLabel => Time.ToString("HH:mm:ss");

    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value))
            {
                Raise(nameof(HasText));
                Raise(nameof(ImageUrl));
                Raise(nameof(EffectiveImageUrl));
                Raise(nameof(HasImage));
                Raise(nameof(TextWithoutImage));
                Raise(nameof(VideoStatusPath));
                Raise(nameof(HasVideoTask));
            }
        }
    }

    /// <summary>
    /// 从回复里找出生成图片的地址。
    ///
    /// 为什么需要：画图那条路返回的是 markdown
    ///
    ///     已生成图片：![生成结果](http://127.0.0.1:8787/v1/images/file/xxx.png?s=...)
    ///
    /// 而对话气泡里的正文是个 TextBox（纯文本），markdown 不会渲染 ——
    /// 用户看到的就是那串原始字符，得手动复制地址才能在浏览器里打开。
    /// 实测就有人问「这生成的图片去哪了」。
    ///
    /// 所以把地址抠出来，界面上直接显示图片。
    /// </summary>
    public string ImageUrl
    {
        get
        {
            if (IsUser) return "";
            var m = System.Text.RegularExpressions.Regex.Match(
                _text, @"!\[[^\]]*\]\((http[^\s\)]+)");
            return m.Success ? m.Groups[1].Value : "";
        }
    }

    /// <summary>
    /// 图片地址**只在真的能用**时才有值。
    ///
    /// 为什么要多一层判断（踩过一个很贵的坑）：
    ///
    /// 一开始写的是 `ImageUrl` 抓到什么就返回什么。视频回复里没有图片，
    /// 返回空串 —— 看着没问题，`Visibility` 也该把它藏起来。
    ///
    /// 但 WPF 的绑定**同时求值**：`Source=""` 照样会喂给
    /// `ImageSourceConverter`，转换器对空串抛异常，异常又触发 `Measure`
    /// 重排，重排又走到那个绑定…… **栈溢出，进程直接死**
    /// （事件日志 `0xc00000fd`，dump 里能看到
    /// ImageSourceConverter.ConvertFrom ↔ BindingExpression.TransferValue
    /// 来回递归）。
    ///
    /// 所以：不能给 Image 控件任何它转换不了的值。空串也不行。
    /// </summary>
    public string EffectiveImageUrl
    {
        get
        {
            var url = ImageUrl;
            if (url.Length == 0) return "";
            // Image.Source 认 http/https/file 绝对路径；相对路径也能认，
            // 但网关给的是完整 URL，这里只放行明确能解析的
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
                ? url
                : "";
        }
    }

    public bool HasImage => EffectiveImageUrl.Length > 0;

    /// <summary>
    /// 这条回复里有没有**视频任务**（网关提交后就返回，不是成品）。
    ///
    /// 为什么单独处理：视频要几分钟渲染，网关提交完就返回一个任务 id
    /// 和查询地址，让模型转告用户。用户看到的就是一段文字 —— 没有
    /// 任何可点的东西，只能自己拼地址去浏览器查。
    ///
    /// 这里把它抠出来，界面上给个「查询进度」按钮。
    /// 地址形如 /v1/video/status/wb-xxxx
    /// </summary>
    public string VideoStatusPath
    {
        get
        {
            if (IsUser) return "";
            var m = System.Text.RegularExpressions.Regex.Match(_text, @"/v1/video/status/([A-Za-z0-9_\-]+)");
            return m.Success ? m.Value : "";
        }
    }

    public bool HasVideoTask => VideoStatusPath.Length > 0;

    public ICommand CheckVideoCommand => _checkVideo ??= new AsyncRelayCommand(CheckVideoAsync, null, ex => _owner?.ReportError(ex));
    private ICommand? _checkVideo;

    /// <summary>
    /// 查一次视频任务。
    ///
    /// 为什么不自动轮询：视频要几分钟，自动轮询要么让界面等半天，
    /// 要么写个后台定时器一直开火。让用户点一下更简单也更可控 ——
    /// 他知道自己在等什么。
    /// </summary>
    private async Task CheckVideoAsync()
    {
        if (!HasVideoTask || _owner is null) return;

        try
        {
            _owner.SetStatus("正在查询视频进度…");
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            var url = $"{_owner.BaseUrl.TrimEnd('/')}{VideoStatusPath.Replace("/v1", "")}";
            // BaseUrl 已经是 http://host:port/v1，所以路径要去掉 /v1 前缀
            url = _owner.BaseUrl.TrimEnd('/') + VideoStatusPath.Substring("/v1".Length);

            using var req = new System.Net.Http.HttpRequestMessage(HttpMethod.Get, url);
            if (_owner.ApiKey.Length > 0)
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_owner.ApiKey}");

            using var resp = await http.SendAsync(req).ConfigureAwait(true);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "?" : "?";

            if (status is "completed" && root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            {
                var videoUrl = data[0].TryGetProperty("url", out var u) ? u.GetString() : null;
                if (videoUrl is { Length: > 0 })
                {
                    VideoUrl = videoUrl;
                    Raise(nameof(VideoUrl));
                    Raise(nameof(HasVideo));
                    _owner.SetStatus("视频生成完成");
                    return;
                }
            }

            if (status is "failed" or "error")
            {
                var msg = root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var em)
                    ? em.GetString() : null;
                VideoNote = $"视频生成失败：{msg ?? "上游没有说明原因"}";
                Raise(nameof(VideoNote));
                _owner.SetStatus("视频生成失败");
                return;
            }

            VideoNote = status switch
            {
                "pending" or "submitted" => "还在排队…",
                "processing" or "running" => "正在渲染…",
                _ => $"状态：{status}",
            };
            Raise(nameof(VideoNote));
            _owner.SetStatus($"视频{VideoNote}");
        }
        catch (Exception ex)
        {
            _owner.ReportError(ex);
        }
    }

    public ICommand OpenVideoCommand => _openVideo ??= new AsyncRelayCommand(OpenVideoAsync, null, ex => _owner?.ReportError(ex));
    private ICommand? _openVideo;

    /// <summary>
    /// 打开生成的视频。
    ///
    /// 和图片一样先下载到本地再开 —— 网关的地址是带签名的完整 URL，
    /// 但直接丢给系统播放器有时会因为没带请求头发起失败。下载到
    /// 临时文件最稳。
    /// </summary>
    private async Task OpenVideoAsync()
    {
        if (!HasVideo || _owner is null) return;

        try
        {
            _owner.SetStatus("正在下载视频…");
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var bytes = await http.GetByteArrayAsync(VideoUrl).ConfigureAwait(true);

            var dir = Path.Combine(Path.GetTempPath(), "WorkBuddyPanel");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"video-{Time:yyyyMMdd-HHmmss}.mp4");
            await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(true);

            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            _owner.SetStatus("已用系统播放器打开视频");
        }
        catch (Exception ex)
        {
            _owner.ReportError(ex);
        }
    }

    /// <summary>视频好了之后的地址。</summary>
    private string _videoUrl = "";
    public string VideoUrl
    {
        get => _videoUrl;
        set { if (Set(ref _videoUrl, value)) { Raise(nameof(HasVideo)); } }
    }
    public bool HasVideo => VideoUrl.Length > 0;

    /// <summary>查询过程中的提示（「还在排队…」之类）。</summary>
    private string _videoNote = "";
    public string VideoNote
    {
        get => _videoNote;
        set { if (Set(ref _videoNote, value)) Raise(nameof(HasVideoNote)); }
    }
    public bool HasVideoNote => VideoNote.Length > 0;

    /// <summary>
    /// 去掉图片那两行之后的文字。
    ///
    /// 图片已经用真的 Image 控件显示了，再把这串 markdown 和裸地址
    /// 原样摆一遍是重复信息。
    /// </summary>
    public string TextWithoutImage
    {
        get
        {
            if (!HasImage) return _text;
            var lines = _text.Split('\n')
                .Where(l => !l.Contains("!["))
                .Where(l => !l.TrimStart().StartsWith("地址："))
                .ToList();
            return string.Join("\n", lines).Trim();
        }
    }

    public string Reasoning
    {
        get => _reasoning;
        set
        {
            if (Set(ref _reasoning, value))
            {
                Raise(nameof(HasReasoning));
            }
        }
    }

    /// <summary>正在接收增量（界面上转圈）。</summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        set => Set(ref _isStreaming, value);
    }

    public bool HasText => !string.IsNullOrEmpty(_text);
    public bool HasReasoning => !string.IsNullOrEmpty(_reasoning);

    /// <summary>出错时在气泡下方显示一行红字。</summary>
    private string _error = "";
    public string Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    /// <summary>用户在气泡里点"复制"用。</summary>
    public void Append(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        Text += chunk;
    }

    public void AppendReasoning(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        Reasoning += chunk;
    }
}

/// <summary>主界面全部状态与命令。</summary>
/// <summary>环境体检里的一行。绑到界面上要 Brush 和字形，所以单独包一层。</summary>
public sealed class EnvironmentCheckVm
{
    public EnvironmentCheckVm(string label, bool ok, string detail)
    {
        Label = label;
        Ok = ok;
        Detail = detail;
    }

    public string Label { get; }
    public bool Ok { get; }
    public string Detail { get; }
    public string Glyph => Ok ? "✓" : "✕";
    public System.Windows.Media.Brush Brush =>
        MainViewModel.BrushFor(Ok ? "Ok" : "Bad");
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly GatewayRunner _gateway = new();
    private ChatStreamClient? _chat;
    private CancellationTokenSource? _generation;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;

        _nodePath = settings.NodePath ?? "";
        _apiDirectory = settings.ApiDirectory ?? "";
        _port = settings.Port.ToString(CultureInfo.InvariantCulture);
        _provider = settings.Provider;
        _apiKey = settings.ApiKey;
        _authFile = settings.AuthFile ?? "";
        _minimizeToTray = settings.MinimizeToTray;
        _autoStartGateway = settings.AutoStartGateway;
        _accountsAutoRefresh = settings.AccountsAutoRefresh;
        // 开机自启的真实状态要读注册表（约 12ms）。放这里会让窗口晚一拍出来，
        // 先按配置显示，真实值由 InitializeAsync 在窗口显示后校正。
        _autoStartWithWindows = settings.AutoStartWithWindows;

        _gateway.LogLine += OnGatewayLog;
        _gateway.Exited += _ => OnGatewayExited();

        StartGatewayCommand = new AsyncRelayCommand(StartGatewayCoreAsync, () => !IsGatewayRunning, ReportError);
        StopGatewayCommand = new RelayCommand(StopGateway, () => IsGatewayRunning);
        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync, () => CanRunNode, ReportError);
        RefreshModelsCommand = new AsyncRelayCommand(RefreshModelsAsync, () => CanRunNode, ReportError);
        RunDoctorCommand = new AsyncRelayCommand(RunDoctorAsync, () => CanRunNode, ReportError);
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend, ReportError);
        StopGenerationCommand = new RelayCommand(StopGeneration, () => IsGenerating);
        ClearChatCommand = new RelayCommand(() => { Messages.Clear(); OnPropertyChanged(nameof(HasMessages)); });
        ClearLogCommand = new RelayCommand(() => Log.Clear());
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ResetSettingsCommand = new RelayCommand(ResetSettings);
        // 体检要碰文件系统（几毫秒），丢后台跑，别卡 UI
        RefreshEnvironmentCommand = new AsyncRelayCommand(
            async () => await Task.Run(RefreshEnvironment), null, ReportError);
        DetectPathsCommand = new AsyncRelayCommand(DetectPathsAsync, null, ReportError);
        CopyBaseUrlCommand = new RelayCommand(() => CopyToClipboard(BaseUrl, "Base URL"));
        CopyApiKeyCommand = new RelayCommand(() => CopyToClipboard(ApiKey, "API Key"));
        OpenStateFolderCommand = new RelayCommand(OpenStateFolder);
        AutoDetectAllCommand = new AsyncRelayCommand(AutoDetectAllAsync, () => CanRunNode, ReportError);

        InitialiseClients();
        InitialiseAccounts();
        InitialisePools();

        // 一次性提示的自动清除
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusIsTransient = false;
            StatusDetail = "";
        };
    }

    // ── 可绑定的集合 ────────────────────────────────────────────
    public ObservableCollection<ModelRowVm> Models { get; } = [];
    public ObservableCollection<ChatMessageVm> Messages { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    public bool HasMessages => Messages.Count > 0;

    // ── 路径 / 设置 ────────────────────────────────────────────
    private string _nodePath;
    public string NodePath
    {
        get => _nodePath;
        set
        {
            if (Set(ref _nodePath, value)) { RaiseDerived(); MarkDirty(); }
        }
    }

    private string _apiDirectory;
    public string ApiDirectory
    {
        get => _apiDirectory;
        set
        {
            if (Set(ref _apiDirectory, value)) { RaiseDerived(); MarkDirty(); }
        }
    }

    private string _port;
    public string Port
    {
        get => _port;
        set
        {
            if (Set(ref _port, value)) SettingsDirty = true;
        }
    }

    private string _provider;
    public string Provider
    {
        get => _provider;
        set
        {
            if (Set(ref _provider, value))
            {
                Raise(nameof(ProviderLabel));
                Raise(nameof(IsInternational));
                SettingsDirty = true;
            }
        }
    }

    private string _apiKey;
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (Set(ref _apiKey, value)) SettingsDirty = true;
        }
    }

    private string _authFile;
    public string AuthFile
    {
        get => _authFile;
        set
        {
            if (Set(ref _authFile, value)) SettingsDirty = true;
        }
    }

    // ── 设置有没有改过没保存 ───────────────────────────────
    //
    // 设置页里有两种改动类型，混在一起很容易让人迷惑：
    //   即时生效：托盘常驻、开机自启、自动刷新   → 勾了就落盘
    //   需要保存：端口、API Key、路径           → 点「保存设置」才落盘
    //
    // 所以这里把“需要保存的那几个”标出来：改了就提示，存了就清。
    // 另外「启动网关」会用这些值，没保存的话用的是旧值 —— 不提示就会很难查。

    /// <summary>自动探测期间不要标脏 —— 那不是“用户的改动”。</summary>
    private bool _suppressDirty;

    private void MarkDirty()
    {
        if (!_suppressDirty) SettingsDirty = true;
    }

    private bool _settingsDirty;
    public bool SettingsDirty
    {
        get => _settingsDirty;
        private set
        {
            if (!Set(ref _settingsDirty, value)) return;
            Raise(nameof(SettingsDirtyHint));
        }
    }

    public string SettingsDirtyHint => SettingsDirty
        ? "⚠ 有改动还没保存。点上面的「保存设置」，否则「启动网关」用的还是旧参数。"
        : "上面这几个（端口 / 版本 / 密钥 / 路径）改完后要点「保存设置」。";

    /// <summary>把当前编辑中的值写进配置对象并落盘。</summary>

    // ── 后台驻留相关 ─────────────────────────────────────────
    /// <summary>关窗口时是否收进托盘（false 则关窗口就是退出）。</summary>
    private bool _minimizeToTray;
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (!Set(ref _minimizeToTray, value)) return;
            _settings.MinimizeToTray = value;
            _settings.Save();
            Raise(nameof(TrayHint));
        }
    }

    /// <summary>启动时是否自动拉起网关。</summary>
    private bool _autoStartGateway;
    public bool AutoStartGateway
    {
        get => _autoStartGateway;
        set
        {
            if (!Set(ref _autoStartGateway, value)) return;
            _settings.AutoStartGateway = value;
            _settings.Save();
            Raise(nameof(TrayHint));
        }
    }

    /// <summary>开机自启（镜像到注册表 Run 键，以它为唯一真相）。</summary>
    private bool _autoStartWithWindows;
    public bool AutoStartWithWindows
    {
        get => _autoStartWithWindows;
        set
        {
            if (!Set(ref _autoStartWithWindows, value)) return;

            // 缓存值先落盘：下次启动就用这个值把开关摆上，
            // 不用为了一个勾去读注册表、拖慢窗口出现。
            _settings.AutoStartWithWindows = value;
            _settings.Save();

            try
            {
                if (value) AutoStart.Enable();
                else AutoStart.Disable();
                AppendLog(value
                    ? $"已开启开机自启：{AutoStart.Describe()}"
                    : "已关闭开机自启。");
            }
            catch (Exception ex)
            {
                // 写不进去就回滚界面，别显示一个假的勾
                var actual = AutoStart.IsEnabled();
                if (actual != value)
                {
                    _autoStartWithWindows = actual;
                    Raise(nameof(AutoStartWithWindows));
                    _settings.AutoStartWithWindows = actual;
                    _settings.Save();
                }
                ReportError(ex);
            }
        }
    }

    /// <summary>托盘行为的一句话说明。</summary>
    public string TrayHint
    {
        get
        {
            if (!MinimizeToTray) return "关窗口就是退出程序，网关也会一起停。";
            return AutoStartGateway
                ? "关窗口只收进托盘，网关继续在后台跑；开机也会在后台自动拉起。"
                : "关窗口只收进托盘，网关继续在后台跑；要真正退出请用托盘菜单的「退出」。";
        }
    }

    /// <summary>App 写完注册表后回写界面状态。</summary>
    public void NotifyAutoStartChanged(bool enabled)
    {
        if (_autoStartWithWindows == enabled) return;
        _autoStartWithWindows = enabled;
        Raise(nameof(AutoStartWithWindows));
    }

    private string _lastModel = "";
    public string LastModel { get => _lastModel; set => Set(ref _lastModel, value); }

    public string[] ProviderOptions { get; } = ["workbuddy", "workbuddy-ai"];
    public string ProviderLabel => Provider == "workbuddy-ai" ? "国际版 WorkBuddy AI" : "国内版 WorkBuddy";
    public bool IsInternational => Provider == "workbuddy-ai";

    // ── 运行状态 ───────────────────────────────────────────────
    private bool _isGatewayRunning;
    public bool IsGatewayRunning
    {
        get => _isGatewayRunning;
        private set
        {
            if (Set(ref _isGatewayRunning, value))
            {
                Raise(nameof(GatewayStateText));
                Raise(nameof(GatewayStateBrush));
                RaiseDerived();
                StartGatewayCommand.RaiseCanExecuteChanged();
                StopGatewayCommand.RaiseCanExecuteChanged();
                Raise(nameof(CanSend));
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _baseUrl = "";
    public string BaseUrl { get => _baseUrl; private set => Set(ref _baseUrl, value); }

    public string GatewayStateText => IsGatewayRunning ? "运行中" : "未启动";
    public Brush GatewayStateBrush => BrushFor(IsGatewayRunning ? "Ok" : "TextSub");

    /// <summary>把 App.xaml 里定义的画刷按 key 取出来（定义在 App.xaml，先于窗口加载）。</summary>
    internal static Brush BrushFor(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    // ── 账号 / 额度 ────────────────────────────────────────────
    private string _signInText = "还没检查";
    public string SignInText { get => _signInText; private set => Set(ref _signInText, value); }

    private string _signInBrushKey = "TextSub";
    public Brush SignInBrush => BrushFor(_signInBrushKey);

    private void SetSignInState(string text, string brushKey)
    {
        _signInBrushKey = brushKey;
        SignInText = text;
        Raise(nameof(SignInBrush));
    }

    private string _creditsText = "—";
    public string CreditsText { get => _creditsText; private set => Set(ref _creditsText, value); }

    private string _expiresText = "—";
    public string ExpiresText { get => _expiresText; private set => Set(ref _expiresText, value); }

    private string _catalogSourceText = "—";
    public string CatalogSourceText { get => _catalogSourceText; private set => Set(ref _catalogSourceText, value); }

    private string _modelCountText = "—";
    public string ModelCountText { get => _modelCountText; private set => Set(ref _modelCountText, value); }

    private string _statusDetail = "";
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }

    /// <summary>底部提示是不是「一次性」的（会自己消失）。</summary>
    private bool _statusIsTransient;
    public bool StatusIsTransient { get => _statusIsTransient; private set => Set(ref _statusIsTransient, value); }

    private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new()
    {
        Interval = TimeSpan.FromSeconds(12),
    };

    /// <summary>
    /// 底部那行一次性提示。
    ///
    /// 以前的实现是 `StatusDetail = "续期完成"` 然后就**永久停在那儿** ——
    /// 过半小时你还看到“续期完成”，根本分不清那是刚发生的还是很久以前的。
    /// 现在这类“动作已做完”的回执 12 秒后自己消失，
    /// 真正的状态（路径来源、错误）还是用常驻的 StatusDetail。
    /// </summary>
    internal void FlashStatus(string message)
    {
        StatusDetail = message;
        Raise(nameof(StatusIsTransient));
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    /// <summary>常驻状态：不会被自动清掉（错误、当前配置这类）。</summary>
    internal void SetStatus(string message)
    {
        _statusTimer.Stop();
        StatusIsTransient = false;
        StatusDetail = message;
    }

    /// <summary>配置读坏时的警告文案（空 = 没事）。</summary>
    private string _configWarning = "";
    public string ConfigWarning
    {
        get => _configWarning;
        private set
        {
            if (Set(ref _configWarning, value)) Raise(nameof(HasConfigWarning));
        }
    }

    public bool HasConfigWarning => _configWarning.Length > 0;

    // ── 对话 ───────────────────────────────────────────────────
    private ModelRowVm? _selectedModel;
    public ModelRowVm? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (Set(ref _selectedModel, value))
            {
                LastModel = value?.Id ?? "";
                Raise(nameof(CanSend));
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _input = "";
    public string Input
    {
        get => _input;
        set
        {
            if (Set(ref _input, value))
            {
                Raise(nameof(CanSend));
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private bool _showReasoning = true;
    public bool ShowReasoning { get => _showReasoning; set => Set(ref _showReasoning, value); }

    private bool _isGenerating;
    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (Set(ref _isGenerating, value))
            {
                Raise(nameof(CanSend));
                SendCommand.RaiseCanExecuteChanged();
                StopGenerationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanSend => IsGatewayRunning && !IsGenerating && !string.IsNullOrWhiteSpace(Input) && SelectedModel is not null;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    private string _busyText = "";
    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    // ── 命令 ───────────────────────────────────────────────────
    public AsyncRelayCommand StartGatewayCommand { get; }
    public RelayCommand StopGatewayCommand { get; }
    public AsyncRelayCommand RefreshStatusCommand { get; }
    public AsyncRelayCommand RefreshModelsCommand { get; }
    public AsyncRelayCommand RunDoctorCommand { get; }
    public AsyncRelayCommand SendCommand { get; }
    public RelayCommand StopGenerationCommand { get; }
    public RelayCommand ClearChatCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand SaveSettingsCommand { get; private set; } = null!;
    public RelayCommand ResetSettingsCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshEnvironmentCommand { get; private set; } = null!;
    public AsyncRelayCommand DetectPathsCommand { get; }
    public RelayCommand CopyBaseUrlCommand { get; }
    public RelayCommand CopyApiKeyCommand { get; }
    public RelayCommand OpenStateFolderCommand { get; }
    public AsyncRelayCommand AutoDetectAllCommand { get; }

    // ── 派生状态 ───────────────────────────────────────────────
    //
    // 设置页里那两格允许填相对路径（发布版的默认值就是 .\runtime\node.exe），
    // 但**真正去用**的时候必须是绝对路径：
    //   · GatewayRunner 会把工作目录设成 workbuddy_api 目录，此时相对的
    //     ".\runtime\node.exe" 会被解析到 workbuddy_api\runtime\node.exe —— 找不到；
    //   · CliRunner 的 FileName 同理。
    // 所以统一在这里展开一次，界面上仍然显示用户填的那个（相对）样子。

    /// <summary>实际启动进程用的 node.exe 绝对路径。</summary>
    internal string? ResolvedNodePath => ProcessLocator.MakeAbsolute(NodePath);

    /// <summary>实际启动进程用的 workbuddy_api 绝对目录。</summary>
    internal string? ResolvedApiDirectory => ProcessLocator.MakeAbsolute(ApiDirectory);

    public bool CanRunNode =>
        ResolvedNodePath is { } node && File.Exists(node) &&
        ResolvedApiDirectory is { } api && File.Exists(CliRunner.DescribeApiDirectory(api));

    /// <summary>当前用的 node.exe 是从哪来的。</summary>
    public string NodeOriginText => ProcessLocator.NodeOrigin(NodePath).Describe();

    /// <summary>当前用的 workbuddy_api 是从哪来的。</summary>
    public string ApiOriginText => ProcessLocator.ApiOrigin(ApiDirectory).Describe();

    public string RuntimeSummary => $"运行环境：{NodeOriginText}　·　服务程序：{ApiOriginText}";

    /// <summary>
    /// 界面上显示一个路径：填的是相对路径就补上「→ 实际指向哪里」，
    /// 否则光看 .\runtime\node.exe 判断不出它落在哪个盘。
    /// </summary>
    private static string DescribePath(string configured)
    {
        if (!ProcessLocator.IsRelative(configured)) return configured;
        var resolved = ProcessLocator.MakeAbsolute(configured);
        return resolved is null ? configured : $"{configured} → {resolved}";
    }

    /// <summary>设置页里那两格路径的说明（发布版给的是默认值）。</summary>
    public string RuntimeDefaultsHint
    {
        get
        {
            if (!AppSettings.IsReleaseBuild)
            {
                return "开发版：路径留空即可，程序会自己去找运行环境和服务程序。";
            }

            return "默认使用程序自带的环境，一般不用改。";
        }
    }

    private void RaiseDerived()
    {
        Raise(nameof(CanRunNode));
        Raise(nameof(PathsOk));
        Raise(nameof(HasEnvironmentProblem));
        Raise(nameof(PathsHint));
        Raise(nameof(NodeOriginText));
        Raise(nameof(ApiOriginText));
        Raise(nameof(RuntimeSummary));
        RefreshStatusCommand.RaiseCanExecuteChanged();
        RefreshModelsCommand.RaiseCanExecuteChanged();
        RunDoctorCommand.RaiseCanExecuteChanged();
        AutoDetectAllCommand.RaiseCanExecuteChanged();
        StartGatewayCommand.RaiseCanExecuteChanged();
    }

    public bool PathsOk => CanRunNode;

    /// <summary>
    /// 环境体检：把「能不能跑」拆成几条独立的检查，逐条说清楚。
    ///
    /// 为什么要拆：以前只有一句“没找到 node.exe 或 workbuddy_api”，
    /// 用户根本判断不了是「没装」还是「被删了」还是「路径填错了」。
    /// 而发布包本该是自带运行时的 —— 所以“自带那份不见了”和“系统也没装”
    /// 是两种完全不同的情况，提示也应该不一样。
    /// </summary>
    /// <summary>
    /// 环境体检结果。**是属性不是方法** —— WPF 的绑定只能绑属性，
    /// 绑到方法上不会报错，只是永远拿不到值（列表永远是空的）。
    /// </summary>
    public ObservableCollection<EnvironmentCheckVm> EnvironmentChecks { get; } = [];

    /// <summary>重新跑一遍体检，把结果填进 EnvironmentChecks。</summary>
    /// <summary>
    /// 重新跑一遍体检，把结果填进 EnvironmentChecks。
    ///
    /// ⚠ 这个方法会在**后台线程**上被调用（`await Task.Run(RefreshEnvironment)`），
    /// 但它改的 EnvironmentChecks 是绑定到界面的集合。
    ///
    /// WPF 的规则：绑定了界面的 ObservableCollection 只能在 UI 线程上改。
    /// 从后台线程改会抛：
    ///
    ///     该类型的 CollectionView 不支持从调度线程以外的线程
    ///     对其 SourceCollection 进行的更改。
    ///
    /// 而且这个异常会被启动路径上的 fire-and-forget 吞掉，
    /// 表现是“界面出来了但什么都不加载”—— 实测踩过。
    ///
    /// 所以这里分段：计算部分留在后台（不碰界面属性），
    /// 写集合的时候切回 UI 线程。
    /// </summary>
    public void RefreshEnvironment()
    {
        var checks = new List<EnvironmentCheckVm>();

        // ① .NET：能跑到这里就说明运行时没问题（自包含发布）
        checks.Add(new EnvironmentCheckVm(".NET 运行时", true,
            $"已加载 {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}"));

        // ② 自带的运行环境
        var bundledNode = ProcessLocator.BundledNodePath;
        checks.Add(new EnvironmentCheckVm("自带的运行环境", File.Exists(bundledNode),
            File.Exists(bundledNode)
                ? "就绪"
                : $"不在 {bundledNode}（如果这个包本该自带，说明文件被删了）"));

        // ③ 自带的服务程序
        var bundledApi = ProcessLocator.BundledApiDirectory;
        var bundledApiOk = File.Exists(Path.Combine(bundledApi, "src", "bin.js"));
        checks.Add(new EnvironmentCheckVm("自带的服务程序", bundledApiOk,
            bundledApiOk ? "就绪" : $"不在 {bundledApi}"));

        // ④ 实际会用的运行环境
        //    这里报**展开后**的绝对路径：发布版默认填的是 .\runtime\node.exe，
        //    只显示相对路径的话，用户没法确认它到底指向哪里。
        checks.Add(new EnvironmentCheckVm("实际使用的运行环境", CanRunNode && !string.IsNullOrEmpty(NodePath),
            string.IsNullOrEmpty(NodePath) ? "没找到（自带的和系统装的都没有）" : $"{DescribePath(NodePath)}（{NodeOriginText}）"));

        // ⑤ 实际会用的服务程序
        checks.Add(new EnvironmentCheckVm("实际使用的服务程序", CanRunNode && !string.IsNullOrEmpty(ApiDirectory),
            string.IsNullOrEmpty(ApiDirectory) ? "没找到" : $"{DescribePath(ApiDirectory)}（{ApiOriginText}）"));

        // 计算好了，写集合要切回 UI 线程
        RunOnUiThread(() =>
        {
            EnvironmentChecks.Clear();
            foreach (var check in checks) EnvironmentChecks.Add(check);
        });

        Raise(nameof(HasEnvironmentProblem));
        Raise(nameof(PathsHint));
    }

    /// <summary>
    /// 在 UI 线程上执行一段代码。
    ///
    /// 已经在 UI 线程上就直接跑（避免无谓的调度开销）；
    /// 不在就丢到 Dispatcher 里并等它做完。
    ///
    /// 为什么要"等"：调用方（比如 RefreshEnvironment）后面还要用集合的结果，
    /// 不等的话会先返回、状态显示成旧的。
    /// </summary>
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        // 没有 Application（单元测试、纯命令行调用）—— 直接跑
        if (dispatcher is null)
        {
            action();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    /// <summary>体检结果里“缺了什么”，给首页那张警告卡用。</summary>
    public bool HasEnvironmentProblem => !CanRunNode;

    public string PathsHint
    {
        get
        {
            if (CanRunNode) return RuntimeSummary;

            var missing = new List<string>();
            if (string.IsNullOrEmpty(NodePath) || ResolvedNodePath is not { } node || !File.Exists(node))
                missing.Add("运行环境");
            if (string.IsNullOrEmpty(ApiDirectory) || ResolvedApiDirectory is not { } api ||
                !File.Exists(CliRunner.DescribeApiDirectory(api)))
                missing.Add("服务程序");

            var text = $"缺 {string.Join(" 和 ", missing)}。";

            // 自带的那份在不在，决定该怎么办，得说清楚
            var bundledNodeOk = File.Exists(ProcessLocator.BundledNodePath);
            var bundledApiOk = File.Exists(Path.Combine(ProcessLocator.BundledApiDirectory, "src", "bin.js"));

            if (bundledNodeOk || bundledApiOk)
            {
                text += "自带的还在，点「自动探测」应该能找回来。";
            }
            else if (Directory.Exists(ProcessLocator.RuntimeRoot))
            {
                text += "程序目录下的环境文件不完整 —— 可能是被删了或被杀软拦了。"
                     + "重新解压一次，或者在「设置」里手动指定。";
            }
            else
            {
                text += "这个包没有自带运行环境，需要自己装一个，"
                     + "或者在「设置」里指定已有的。";
            }

            return text;
        }
    }

    private void OnPropertyChanged(string name) => Raise(name);

    // ── 启动初始化 ────────────────────────────────────────────
    /// <summary>
    /// 开窗后跑一次：先保证路径可用，再把账号信息和模型列表拉回来。
    /// 这里失败只写日志，不弹窗——启动瞬间弹窗很讨嫌。
    /// </summary>
    public async Task InitializeAsync()
    {
        // 第一件事就是体检：这样即使后面因为路径没找到提前 return，
        // 用户也能在首页看到到底是缺 Node 还是缺 workbuddy_api。
        await Task.Run(RefreshEnvironment);

        AppendLog($"配置文件：{AppSettings.Location}");
        if (AppSettings.LastLoadError is { Length: > 0 } loadError)
        {
            ConfigWarning = $"配置文件读不了（{loadError}）。已回退到默认设置，原文件备份在：{AppSettings.LastBackupPath}";
            AppendLog($"[警告] {ConfigWarning}");
            StatusDetail = "配置文件读不了，已回退到默认设置（看「日志」标签页）。";
        }
        AppendLog($"读到的设置：托盘常驻={MinimizeToTray}，启动时自动开网关={AutoStartGateway}，开机自启={AutoStartWithWindows}");

        if (!CanRunNode) await DetectPathsAsync();
        if (!CanRunNode)
        {
            StatusDetail = PathsHint;
            return;
        }

        // 路径探测、客户端探测、账号列表、状态、模型 —— 全部丢到一起并行跑。
        //
        // 以前是串行的：路径（50-250ms）→ 客户端探测 → 账号列表（1.9s）
        // → status → models，加起来两秒多，而且每一步都把 UI 线程占着。
        // 现在并行，总耗时看最慢的那一个（账号列表 1.9s），而且全程不卡界面。
        await Task.WhenAll(
            RefreshClientsAsync(),
            // 账号用「先快后补」：先 --no-checkin（5 秒出列表），
            // 签到状态后台再查（完整查询要 10 秒，等它启动会很慢）
            RefreshAccountsAtStartupAsync(),
            RefreshStatusAsync(),
            RefreshModelsAsync());

        // 下面两个都是“读注册表 / 跑 schtasks / 读 SQLite”的慢活，
        // 放在并行组之后补正，不挡启动。
        await RefreshAutoStartStateAsync();

        if (CcSwitch.IsAvailable) await RefreshCcSwitchStateAsync();

        // 分池：把标了「自动启动」的池拉起来。
        //
        // 放在最后，因为它会启 node 子进程（每个几百毫秒）。
        // 默认池那一个实际上已经被上面那条主流程起过了，这里会跳过它。
        await AutoStartPoolsAsync();
    }

    // ── 路径探测 ───────────────────────────────────────────────
    /// <summary>
    /// 定位 node.exe 和 workbuddy_api 目录。
    ///
    /// 这里会扫 PATH、查几个常见安装位置、向上逐级找目录（实测总共 50～250ms）。
    /// 所以是 async 的：调度到线程池跑，
    /// 调用方拿到的不是“结果”，而是“已经算完的结果”——
    /// 不这样做的话，窗口会出现之前会先卡一下。
    /// </summary>
    public async Task DetectPathsAsync()
    {
        // 先拿走 hint，因为下面要上后台线程，不能读实例字段之外的东西
        var nodeHint = NodePath;
        var apiHint = ApiDirectory;

        var resolved = await Task.Run(() => ProcessLocator.Resolve(nodeHint, apiHint));

        // 自动探测的结果不是“用户的改动”，不该让设置页显示“有未保存的改动”。
        _suppressDirty = true;
        try
        {
            if (resolved.Node is not null) NodePath = resolved.Node;
            if (resolved.Api is not null) ApiDirectory = resolved.Api;
        }
        finally
        {
            _suppressDirty = false;
        }

        AppendLog($"运行环境：{NodePath ?? "(没找到)"}　来源：{NodeOriginText}");
        AppendLog($"服务程序：{ApiDirectory ?? "(没找到)"}　来源：{ApiOriginText}");
        RaiseDerived();

        // 探测结果直接落盘，下次启动不用再找一遍
        SaveSettings();

        RefreshEnvironment();
    }

    private void SaveSettings()
    {
        _settings.NodePath = NodePath;
        _settings.ApiDirectory = ApiDirectory;
        _settings.Provider = Provider;
        _settings.ApiKey = ApiKey;
        _settings.AuthFile = string.IsNullOrWhiteSpace(AuthFile) ? null : AuthFile;
        _settings.LastModel = LastModel;
        if (int.TryParse(Port, out var p) && p is > 0 and <= 65535) _settings.Port = p;
        _settings.Save();
        SettingsDirty = false;
        AppendLog("设置已保存。");
    }

    /// <summary>把需要保存的那几个恢复成默认值（并落盘）。</summary>
    private void ResetSettings()
    {
        Port = "8787";
        ApiKey = "sk-workbuddy-local";
        AuthFile = "";

        // 路径：发布版恢复到自带的 runtime（那才是"默认值"），调试版保持不动 ——
        // 调试时那份路径是自动探测出来的，清掉反而让程序找不到 node
        if (AppSettings.IsReleaseBuild)
        {
            NodePath = AppSettings.DefaultNodePathRelative;
            ApiDirectory = AppSettings.DefaultApiDirectoryRelative;
        }

        SaveSettings();
        FlashStatus(AppSettings.IsReleaseBuild
            ? "已恢复默认（端口 / 密钥 / 登录文件路径 / 运行环境路径）"
            : "已恢复默认（端口 / 密钥 / 登录文件路径）");
    }

    private void CopyToClipboard(string value, string what)
    {
        try
        {
            Clipboard.SetText(value);
            FlashStatus($"已复制 {what} 到剪贴板");
        }
        catch (Exception ex)
        {
            FlashStatus($"复制失败：{ex.Message}");
        }
    }

    private void OpenStateFolder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".workbuddy-api");
        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    // ── 网关启停 ───────────────────────────────────────────────

    /// <summary>
    /// 这个端口上跑的是不是 WorkBuddy 网关。
    ///
    /// 判断依据：我们的 `/healthz` 会回 `{"ok":true,"provider":"workbuddy",…}`。
    /// 别的程序不会碰巧返回这个结构，所以这个判据足够。
    ///
    /// 超时给得很短（800ms）—— 如果对方不是 HTTP 服务，连接会很快被拒；
    /// 如果是 HTTP 但不响应，也不该让启动流程卡住。
    /// </summary>
    private static async Task<bool> IsOurGatewayOnPortAsync(int port)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            var body = await http.GetStringAsync($"http://127.0.0.1:{port}/healthz");
            return body.Contains("\"ok\"", StringComparison.Ordinal)
                && body.Contains("workbuddy", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 启动网关。失败时抛异常，由调用方决定是弹窗（用户手点）还是只写日志（开机自启）。
    /// </summary>
    private async Task StartGatewayCoreAsync()
    {
        if (!CanRunNode)
        {
            throw new InvalidOperationException(
                "路径没配好。到「设置」标签页点「自动探测」，或手动指定运行环境和服务程序。");
        }

        if (!int.TryParse(Port, out var port) || port is < 0 or > 65535)
            throw new InvalidOperationException($"端口号不对：{Port}");

        //
        // 端口被占 —— 但要先分清"谁在占"：
        //
        //   ① 是本程序启动的网关（或另一个实例启的）
        //      → 正常。直接当"已在运行"，不报错
        //
        //   ② 是别的程序（或者上次异常退出留下的脏进程）
        //      → 才是真问题，要告诉用户怎么办
        //
        // 之前不看是①还是②，只要端口被占就报"没关干净、请重启电脑"。
        // 实际常见的情况是①：比如用户开了两个面板实例、或者另一个工具在用同一端口。
        // 实测就误报过 —— 看到"端口被占"但网关其实在健康运行。
        //
        if (port != 0 && CliRunner.IsPortInUse(port))
        {
            if (await IsOurGatewayOnPortAsync(port))
            {
                // 自己人，当它已经在跑
                AppendLog($"端口 {port} 上已经有网关在运行，直接采用它。");
                IsGatewayRunning = true;

                // 这条路径以前没登记到分池 —— 于是分池页上默认池显示「已停止」，
                // 还多个「启动」按钮（点了会因为端口被占而报错）。
                // 网关是真的在跑，两边得说同一句话。
                BaseUrl = $"http://127.0.0.1:{port}/v1";
                AdoptGatewayIntoDefaultPool(externalRunning: true, externalUrl: BaseUrl);

                // ⚠ 必须重建聊天客户端。
                //
                // 以前漏了这一步，后果很隐蔽：
                //   ① 点「停止网关」→ StopGateway() 把 _chat 清成 null
                //   ② 再点「启动网关」→ 端口上那个 node 其实没死（成了孤儿），
                //      于是走这条「采用」路径
                //   ③ 采用只设了 IsGatewayRunning = true，_chat 还是 null
                //   ④ 界面：发送按钮是亮的（CanSend 只看 IsGatewayRunning）
                //      实际：SendAsync 第一行 `if (_chat is null) return` 静默返回
                //   → 用户看到的就是「发送按钮失效了，点了没反应」
                _chat?.Dispose();
                _chat = new ChatStreamClient(BaseUrl, ApiKey);

                return;
            }

            throw new InvalidOperationException(
                $"端口 {port} 已经被别的程序占用了。\n\n" +
                "可能是之前启动的网关没关干净（比如上次程序被强制结束）。\n" +
                "解决办法：在「设置」里把端口改成别的（比如 8788），或者重启电脑。");
        }

        SaveSettings();
        ClearLogInternal();
        AppendLog($"正在启动网关（{ProviderLabel}，端口 {port}）…");

        // 传绝对路径给进程：GatewayRunner 会把工作目录切到 workbuddy_api 目录，
        // 相对的 ".\runtime\node.exe" 在那种工作目录下是找不到的
        var nodeExe = ResolvedNodePath ?? NodePath;
        var apiDir = ResolvedApiDirectory ?? ApiDirectory;

        IsBusy = true;
        BusyText = "正在启动网关…";
        try
        {
            await _gateway.StartAsync(new GatewayOptions(
                nodeExe,
                apiDir,
                port,
                ApiKey,
                Provider,
                string.IsNullOrWhiteSpace(AuthFile) ? null : AuthFile));

            BaseUrl = _gateway.BaseUrl;
            _chat?.Dispose();
            _chat = new ChatStreamClient(BaseUrl, ApiKey);
            IsGatewayRunning = true;

            // 把主编这个实例登记给默认池，分池页才能对同一个进程说同一句话。
            // （否则分池页会以为默认池是「已停止」，或者自己另起一个占端口）
            AdoptGatewayIntoDefaultPool();

            FlashStatus("网关已启动");
            AppendLog($"网关就绪：{BaseUrl}");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }

        await RefreshStatusAsync();
        await RefreshModelsAsync();
    }

    /// <summary>
    /// 确保**当前看的那一个池**在跑。默认池就走原来的路径。
    ///
    /// 「一键接入」用它 —— 以前那里查的是 IsGatewayRunning（主编的状态），
    /// 切到别的池时会误以为网关已经在跑。
    /// </summary>
    public async Task<bool> TryStartCurrentPoolAsync()
    {
        if (CurrentPoolRunning) return true;
        if (!CanRunNode) return false;

        if (SelectedPool is null)
        {
            // 默认池：原来的路径
            return await TryStartGatewayAsync();
        }

        try
        {
            await ToggleCurrentGatewayAsync();
            return CurrentPoolRunning;
        }
        catch (Exception ex)
        {
            AppendLog($"[静默启动失败] 「{CurrentPoolLabel}」：{ex.Message}");
            StatusDetail = $"「{CurrentPoolLabel}」启动失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 给开机自启用的静默启动：失败只写日志，不弹窗（用户还没看着屏幕呢）。
    /// </summary>
    public async Task<bool> TryStartGatewayAsync()
    {
        if (IsGatewayRunning) return true;
        if (!CanRunNode) return false;

        try
        {
            await StartGatewayCoreAsync();
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"[静默启动失败] {ex.Message}");
            StatusDetail = $"网关启动失败：{ex.Message}";
            return false;
        }
    }

    private void StopGateway()
    {
        _generation?.Cancel();
        _gateway.Stop();
        OnGatewayExited();
        AppendLog("网关已停止。");
    }

    /// <summary>子进程退出可能发生在任意线程，统一切回 UI 线程再改状态。</summary>
    private void OnGatewayExited()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(OnGatewayExited);
            return;
        }

        IsGatewayRunning = false;
        BaseUrl = "";
        _chat?.Dispose();
        _chat = null;
        // 实例已经退了，分池页上默认池也该显示「已停止」
        RefreshPools();
    }

    private void OnGatewayLog(string line)
    {
        // 网关的日志线程不是 UI 线程，转发到 UI 线程。
        //
        // 注：AppendLog 自己也会做这个检查，这里保留只是为了语义清楚。
        AppendLog(line);
    }

    private void AppendLog(string line)
    {
        // 必须在 UI 线程上改 Log —— 它绑到界面，ObservableCollection 的
        // CollectionChanged 会直接通知 WPF 的 CollectionView，
        // 从别的线程改会抛：
        //
        //     System.NotSupportedException:
        //     该类型的 CollectionView 不支持从调度程序线程以外的线程
        //     对其 SourceCollection 进行的更改。
        //
        // 这个坑踩过两次：
        //   1. RefreshEnvironment 在 Task.Run 里改 EnvironmentChecks
        //   2. 分池的 PoolManager.Log += AppendLog —— 池的网关
        //      输出事件在**线程池线程**上触发（GatewayRunner.HandleLine），
        //      于是启动池时直接闪退
        //
        // 所以保护做在这里（而不是每个调用入口）—— AppendLog 是个
        // 天然会被后台线程调用的地方，只堵入口迟早还会漏。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }

        var stamp = DateTime.Now.ToString("HH:mm:ss");
        Log.Add($"[{stamp}] {line}");
        while (Log.Count > 2000) Log.RemoveAt(0);

        // 同时落盘。
        //
        // 以前日志只在内存里，用户报问题、或者我自己排查时都看不到。
        // 只写追加一个文件，开销很小。
        // 单独一个文件，不和维护日志混在一起 —— 那个是给计划任务用的。
        WriteUiLog($"[{stamp}] {line}");
    }

    /// <summary>
    /// 从外部（比如 App 的启动异常处理）往界面日志里写一行。
    ///
    /// 为什么需要：启动流程抛异常时，ViewModel 可能还没建好，
    /// 但用户需要看到“到底怎么了”——而且得先落盘再说话。
    /// </summary>
    public void AppendExternalLog(string line) => AppendLog(line);

    private static readonly object UiLogGate = new();

    private static void WriteUiLog(string line)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WorkBuddyPanel");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "ui.log");

            // 超过 1MB 就截掉一半 —— 长时间开着不至于无限增长
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 1_000_000)
            {
                var lines = File.ReadAllLines(path);
                File.WriteAllLines(path, lines.Skip(lines.Length / 2));
            }

            lock (UiLogGate)
            {
                File.AppendAllText(path, line + Environment.NewLine, System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不了不能影响主流程
        }
    }

    private void ClearLogInternal() => Log.Clear();

    // ── 一次性 CLI 命令 ────────────────────────────────────────
    private string ProviderArgs()
        => Provider == "workbuddy-ai" ? " --provider workbuddy-ai" : "";

    /// <summary>
    /// 跑一条 `node src/bin.js ...` 命令。
    ///
    /// 所有 CLI 调用都走这里，为的是统一把相对路径（.\runtime\...）展开成绝对路径 ——
    /// 漏掉任何一处，发布版默认路径就会在那边悄悄失效。
    /// </summary>
    internal Task<CliResult> RunCliAsync(string arguments, string? stdin = null, CancellationToken ct = default)
        => CliRunner.RunAsync(
            ResolvedNodePath ?? NodePath,
            ResolvedApiDirectory ?? ApiDirectory,
            arguments,
            stdin,
            ct);

    private async Task RefreshStatusAsync()
    {
        IsBusy = true;
        BusyText = "正在读取账号信息…";
        try
        {
            var result = await RunCliAsync($"src/bin.js status --json{ProviderArgs()}");
            var json = CliRunner.ExtractJson(result.StdOut);
            if (json is null)
            {
                SetSignInState("读取失败", "Bad");
                StatusDetail = result.Combined.Length > 0 ? result.Combined : "status 没有返回可解析的内容";
                AppendLog($"[status] {StatusDetail}");
                return;
            }

            var dto = JsonSerializer.Deserialize<StatusDto>(json) ?? new StatusDto();
            ApplyStatus(dto);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private void ApplyStatus(StatusDto dto)
    {
        var signedIn = string.Equals(dto.Status, "signed-in", StringComparison.OrdinalIgnoreCase);
        SetSignInState(signedIn ? "已登录" : (dto.Status ?? "未登录"), signedIn ? "Ok" : "Bad");

        CreditsText = dto.Credits is { } credits ? credits.ToString("0.##", CultureInfo.InvariantCulture) : "—";
        Raise(nameof(StatusCreditsText));

        if (DateTimeOffset.TryParse(dto.AccessTokenExpires, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var expires))
        {
            var remaining = expires - DateTimeOffset.UtcNow;
            ExpiresText = remaining.TotalHours > 0
                ? $"{expires.ToLocalTime():yyyy-MM-dd HH:mm}（还有 {remaining.TotalHours:0.#} 小时）"
                : $"{expires.ToLocalTime():yyyy-MM-dd HH:mm}（已过期，会自动刷新）";
        }
        else
        {
            ExpiresText = "—";
        }

        CatalogSourceText = dto.Catalog?.Source switch
        {
            "live" => "上游实时",
            "cached" => "上次成功缓存",
            "builtin" => "内置兜底",
            null => "—",
            var other => other,
        };

        ModelCountText = dto.Catalog?.Count?.ToString(CultureInfo.InvariantCulture) ?? "—";

        // 状态文字 —— 只说用户看得懂的东西。
        //
        // 以前写的是：账号来源：vault　域名：www.workbuddy.cn　状态目录：C:\Users\...\.workbuddy-api
        // 其中 vault / 状态目录 / 那个内部路径都不该暴露 ——
        // 用户不需要知道底层怎么存的。
        var account = dto.Nickname ?? dto.Email;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(account)) parts.Add(account);
        if (!string.IsNullOrWhiteSpace(dto.Domain)) parts.Add(dto.Domain);
        if (dto.Credits is { } remainingCredits) parts.Add($"{remainingCredits:0.##} 积分");

        StatusDetail = parts.Count > 0
            ? $"已登录：{string.Join("　·　", parts)}"
            : "已登录";
    }

    private async Task RefreshModelsAsync()
    {
        IsBusy = true;
        BusyText = "正在刷新模型列表…";
        try
        {
            var result = await RunCliAsync($"src/bin.js models --json{ProviderArgs()}");
            var json = CliRunner.ExtractJson(result.StdOut);
            if (json is null)
            {
                AppendLog($"[models] 解析失败：{(result.Combined.Length > 0 ? result.Combined : "无输出")}");
                StatusDetail = "模型列表刷新失败，看「日志」标签页。";
                return;
            }

            var dto = JsonSerializer.Deserialize<ModelsDto>(json);
            var previous = SelectedModel?.Id ?? LastModel;

            Models.Clear();
            foreach (var model in dto?.Data ?? [])
            {
                if (string.IsNullOrWhiteSpace(model.Id)) continue;
                Models.Add(ModelRowVm.FromDto(model));
            }

            SelectedModel = Models.FirstOrDefault(m => m.Id == previous) ?? Models.FirstOrDefault();
            ModelCountText = Models.Count.ToString(CultureInfo.InvariantCulture);
            if (dto?.Catalog?.Source is { } source)
            {
                CatalogSourceText = source switch
                {
                    "live" => "上游实时",
                    "cached" => "上次成功缓存",
                    "builtin" => "内置兜底",
                    _ => source,
                };
            }

            AppendLog($"[models] 拿到 {Models.Count} 个模型（来源 {CatalogSourceText}）");
            await RefreshClientsAsync();   // 接入页要用模型名和上下文窗口，刷新后同步过去
            await RefreshAccountsAsync();   // 账号页要用 uid / 昵称去重，也刷一下
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private async Task RunDoctorAsync()
    {
        IsBusy = true;
        BusyText = "正在体检…";
        try
        {
            var result = await RunCliAsync($"src/bin.js doctor{ProviderArgs()}");
            AppendLog("===== doctor =====");
            foreach (var line in result.Combined.Split('\n'))
                AppendLog(line.TrimEnd());
            AppendLog("==================");
            FlashStatus("体检完成，结果在「日志」标签页");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private async Task AutoDetectAllAsync()
    {
        await DetectPathsAsync();
        SaveSettings();
        await RefreshStatusAsync();
        await RefreshModelsAsync();
    }

    // ── 对话 ───────────────────────────────────────────────────
    private void StopGeneration()
    {
        _generation?.Cancel();
        IsGenerating = false;
    }

    private async Task SendAsync()
    {
        // 前置条件不满足时**要说出来**，不能静默返回。
        //
        // 以前是 `if (_chat is null) return;` —— 什么也不做也不报错。
        // 配合另一个 bug（采用已有网关时没重建 _chat），表现就成了
        // 「发送按钮亮着但点了没反应」，而且日志里一个字都没有，
        // 根本无从下手查。
        //
        // 现在：告诉用户为什么没发出去。
        if (_chat is null)
        {
            SetStatus(IsGatewayRunning
                ? "聊天连接没建好 —— 请点一下「停止网关」再「启动网关」"
                : "网关没在跑，先在首页启动它");
            AppendLog("[发送] 被拦下：聊天客户端未建立（_chat 为空）");
            return;
        }

        if (SelectedModel is null)
        {
            SetStatus("还没选模型，在下面那个下拉框里选一个");
            AppendLog("[发送] 被拦下：没选模型");
            return;
        }

        var prompt = Input.Trim();
        if (prompt.Length == 0) return;

        var history = new List<ChatTurn>();
        foreach (var message in Messages)
        {
            if (message.IsUser) history.Add(new ChatTurn("user", message.Text));
            else if (message.HasText) history.Add(new ChatTurn("assistant", message.Text));
        }
        history.Add(new ChatTurn("user", prompt));

        Messages.Add(new ChatMessageVm(this) { IsUser = true, Text = prompt });
        Input = "";
        Raise(nameof(HasMessages));

        var answer = new ChatMessageVm(this) { IsUser = false, IsStreaming = true };
        Messages.Add(answer);
        Raise(nameof(HasMessages));

        IsGenerating = true;
        _generation = new CancellationTokenSource();

        try
        {
            await foreach (var delta in _chat.StreamAsync(SelectedModel.Id, history, _generation.Token))
            {
                if (delta.Error is { Length: > 0 } error)
                {
                    answer.Error = error;
                    break;
                }
                if (delta.Reasoning is { Length: > 0 } reasoning)
                    answer.AppendReasoning(reasoning);
                if (delta.Content is { Length: > 0 } content)
                    answer.Append(content);
                if (delta.FinishReason is { Length: > 0 })
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            answer.Error = "（已停止生成）";
        }
        catch (HttpRequestException ex)
        {
            answer.Error = $"连不上本机网关：{ex.Message}";
        }
        catch (Exception ex)
        {
            answer.Error = ex.Message;
        }
        finally
        {
            answer.IsStreaming = false;
            IsGenerating = false;
            _generation?.Dispose();
            _generation = null;
        }
    }

    /// <summary>开机自启的真实状态（读注册表）。后台跑，回来补正界面。</summary>
    private async Task RefreshAutoStartStateAsync()
    {
        try
        {
            // Describe() 也要读注册表，一并丢后台
            var (enabled, description) = await Task.Run(() =>
                (AutoStart.IsEnabled(), AutoStart.Describe()));

            NotifyAutoStartChanged(enabled);
            if (_settings.AutoStartWithWindows != enabled)
            {
                _settings.AutoStartWithWindows = enabled;
                _settings.Save();
            }
            AppendLog(enabled ? $"开机自启：已开启（{description}）" : "开机自启：未开启");
        }
        catch (Exception ex)
        {
            AppendLog($"[设置] 读开机自启状态失败：{ex.Message}");
        }
    }

    internal void ReportError(Exception ex)
    {
        StatusDetail = ex.Message;
        AppendLog($"[错误] {ex.Message}");
        MessageBox.Show(ex.Message, "WorkBuddy 控制台", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void Dispose()
    {
        _generation?.Cancel();
        _generation?.Dispose();
        _chat?.Dispose();

        // 先收掉所有非默认池的实例，再收主编那个 ——
        // 默认池是「代管」主编的 GatewayRunner 的，两边都 Dispose 会重复 Kill。
        try { _pools?.Dispose(); } catch { }
        _gateway.Dispose();
    }
}
