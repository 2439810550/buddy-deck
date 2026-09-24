using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
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
            }
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
        DetectPathsCommand = new AsyncRelayCommand(DetectPathsAsync, null, ReportError);
        CopyBaseUrlCommand = new RelayCommand(() => CopyToClipboard(BaseUrl, "Base URL"));
        CopyApiKeyCommand = new RelayCommand(() => CopyToClipboard(ApiKey, "API Key"));
        OpenStateFolderCommand = new RelayCommand(OpenStateFolder);
        AutoDetectAllCommand = new AsyncRelayCommand(AutoDetectAllAsync, () => CanRunNode, ReportError);

        InitialiseClients();
        InitialiseAccounts();

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
    public string NodePath { get => _nodePath; set { if (Set(ref _nodePath, value)) RaiseDerived(); } }

    private string _apiDirectory;
    public string ApiDirectory { get => _apiDirectory; set { if (Set(ref _apiDirectory, value)) RaiseDerived(); } }

    private string _port;
    public string Port { get => _port; set => Set(ref _port, value); }

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
            }
        }
    }

    private string _apiKey;
    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, value); }

    private string _authFile;
    public string AuthFile { get => _authFile; set => Set(ref _authFile, value); }

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
    private void FlashStatus(string message)
    {
        StatusDetail = message;
        Raise(nameof(StatusIsTransient));
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    /// <summary>常驻状态：不会被自动清掉（错误、当前配置这类）。</summary>
    private void SetStatus(string message)
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
    public RelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand DetectPathsCommand { get; }
    public RelayCommand CopyBaseUrlCommand { get; }
    public RelayCommand CopyApiKeyCommand { get; }
    public RelayCommand OpenStateFolderCommand { get; }
    public AsyncRelayCommand AutoDetectAllCommand { get; }

    // ── 派生状态 ───────────────────────────────────────────────
    public bool CanRunNode => !string.IsNullOrEmpty(NodePath) && !string.IsNullOrEmpty(ApiDirectory)
                              && File.Exists(CliRunner.DescribeApiDirectory(ApiDirectory));

    /// <summary>当前用的 node.exe 是从哪来的。</summary>
    public string NodeOriginText => ProcessLocator.NodeOrigin(NodePath).Describe();

    /// <summary>当前用的 workbuddy_api 是从哪来的。</summary>
    public string ApiOriginText => ProcessLocator.ApiOrigin(ApiDirectory).Describe();

    public string RuntimeSummary => $"Node.js 来源：{NodeOriginText}　·　workbuddy_api 来源：{ApiOriginText}";

    private void RaiseDerived()
    {
        Raise(nameof(CanRunNode));
        Raise(nameof(PathsOk));
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

    public string PathsHint => CanRunNode
        ? RuntimeSummary
        : "没找到可用的 node.exe 或 workbuddy_api。到「设置」标签页点「自动探测」；"
          + "发布包自带的在 runtime\\ 子目录下，别把它删了。";

    private void OnPropertyChanged(string name) => Raise(name);

    // ── 启动初始化 ────────────────────────────────────────────
    /// <summary>
    /// 开窗后跑一次：先保证路径可用，再把账号信息和模型列表拉回来。
    /// 这里失败只写日志，不弹窗——启动瞬间弹窗很讨嫌。
    /// </summary>
    public async Task InitializeAsync()
    {
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
            RefreshAccountsAsync(),
            RefreshStatusAsync(),
            RefreshModelsAsync());
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

        if (resolved.Node is not null) NodePath = resolved.Node;
        if (resolved.Api is not null) ApiDirectory = resolved.Api;

        AppendLog($"node.exe：{NodePath ?? "(没找到)"}　来源：{NodeOriginText}");
        AppendLog($"workbuddy_api：{ApiDirectory ?? "(没找到)"}　来源：{ApiOriginText}");
        RaiseDerived();
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
        AppendLog("设置已保存到 %APPDATA%\\WorkBuddyPanel\\settings.json");
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
    /// 启动网关。失败时抛异常，由调用方决定是弹窗（用户手点）还是只写日志（开机自启）。
    /// </summary>
    private async Task StartGatewayCoreAsync()
    {
        if (!CanRunNode)
        {
            throw new InvalidOperationException(
                "路径没配好。到「设置」标签页点「自动探测」，或手动指定 node.exe 和 workbuddy_api 目录。");
        }

        if (!int.TryParse(Port, out var port) || port is < 0 or > 65535)
            throw new InvalidOperationException($"端口号不对：{Port}");

        if (port != 0 && CliRunner.IsPortInUse(port))
            throw new InvalidOperationException(
                $"端口 {port} 已经被别的程序占用了。\n\n" +
                "可能是之前启动的网关没关干净（比如上次程序被强制结束）。\n" +
                "解决办法：在「设置」里把端口改成别的（比如 8788），或者重启电脑。");

        SaveSettings();
        ClearLogInternal();
        AppendLog($"正在启动网关（{ProviderLabel}，端口 {port}）…");

        IsBusy = true;
        BusyText = "正在启动网关…";
        try
        {
            await _gateway.StartAsync(new GatewayOptions(
                NodePath,
                ApiDirectory,
                port,
                ApiKey,
                Provider,
                string.IsNullOrWhiteSpace(AuthFile) ? null : AuthFile));

            BaseUrl = _gateway.BaseUrl;
            _chat?.Dispose();
            _chat = new ChatStreamClient(BaseUrl, ApiKey);
            IsGatewayRunning = true;
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
    }

    private void OnGatewayLog(string line)
    {
        // 网关的日志线程不是 UI 线程，转发到 UI 线程
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) AppendLog(line);
        else dispatcher.BeginInvoke(() => AppendLog(line));
    }

    private void AppendLog(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        Log.Add($"[{stamp}] {line}");
        while (Log.Count > 2000) Log.RemoveAt(0);
    }

    private void ClearLogInternal() => Log.Clear();

    // ── 一次性 CLI 命令 ────────────────────────────────────────
    private string ProviderArgs()
        => Provider == "workbuddy-ai" ? " --provider workbuddy-ai" : "";

    private async Task RefreshStatusAsync()
    {
        IsBusy = true;
        BusyText = "正在读取账号信息…";
        try
        {
            var result = await CliRunner.RunAsync(NodePath, ApiDirectory, $"src/bin.js status --json{ProviderArgs()}");
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

        StatusDetail = $"账号来源：{dto.Source ?? "—"}　域名：{dto.Domain ?? "—"}　状态目录：{dto.StateHome ?? "—"}";
    }

    private async Task RefreshModelsAsync()
    {
        IsBusy = true;
        BusyText = "正在刷新模型列表…";
        try
        {
            var result = await CliRunner.RunAsync(NodePath, ApiDirectory, $"src/bin.js models --json{ProviderArgs()}");
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
            var result = await CliRunner.RunAsync(NodePath, ApiDirectory, $"src/bin.js doctor{ProviderArgs()}");
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
        if (_chat is null || SelectedModel is null) return;
        var prompt = Input.Trim();
        if (prompt.Length == 0) return;

        var history = new List<ChatTurn>();
        foreach (var message in Messages)
        {
            if (message.IsUser) history.Add(new ChatTurn("user", message.Text));
            else if (message.HasText) history.Add(new ChatTurn("assistant", message.Text));
        }
        history.Add(new ChatTurn("user", prompt));

        Messages.Add(new ChatMessageVm { IsUser = true, Text = prompt });
        Input = "";
        Raise(nameof(HasMessages));

        var answer = new ChatMessageVm { IsUser = false, IsStreaming = true };
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
        _gateway.Dispose();
    }
}
