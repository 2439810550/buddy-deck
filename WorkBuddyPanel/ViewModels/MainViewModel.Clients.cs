using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Media;
using WorkBuddyPanel.Models;
using WorkBuddyPanel.Services;

namespace WorkBuddyPanel.ViewModels;

/// <summary>「一键接入」列表里的一行。</summary>
public sealed class ClientRowVm : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _detected;
    private bool _configured;
    private string _detail = "";

    public ClientRowVm(MainViewModel owner, ClientState state)
    {
        _owner = owner;
        Kind = state.Kind;
        Name = state.Name;
        ConfigPath = state.ConfigPath;
        Hint = state.Hint;
        _detected = state.Detected;
        _configured = state.Configured;
        _detail = state.Detail;

        ConfigureCommand = new AsyncRelayCommand(ConfigureAsync, () => _detected, owner.ReportError);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => CanRestore, owner.ReportError);
    }

    public ClientKind Kind { get; }
    public string Name { get; }
    public string ConfigPath { get; }
    public string Hint { get; }

    public AsyncRelayCommand ConfigureCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }

    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public bool Detected
    {
        get => _detected;
        private set
        {
            if (!Set(ref _detected, value)) return;
            Raise(nameof(Badge));
            Raise(nameof(BadgeBrush));
            ConfigureCommand.RaiseCanExecuteChanged();
        }
    }

    public bool Configured
    {
        get => _configured;
        private set
        {
            if (!Set(ref _configured, value)) return;
            Raise(nameof(Badge));
            Raise(nameof(BadgeBrush));
            Raise(nameof(CanRestore));
            RestoreCommand.RaiseCanExecuteChanged();
        }
    }

    public string Badge => !Detected ? "没装" : Configured ? "已接入" : "可接入";

    public Brush BadgeBrush => MainViewModel.BrushFor(
        !Detected ? "TextSub" : Configured ? "Ok" : "Accent");

    /// <summary>备份存在才给还原。</summary>
    public bool CanRestore => Kind != ClientKind.Dsh && Kind switch
    {
        ClientKind.ClaudeCode => File.Exists(Path.Combine(ClientIntegrator.BackupRoot, "claude", "settings.json")),
        ClientKind.Codex => File.Exists(Path.Combine(ClientIntegrator.BackupRoot, "codex", "config.toml"))
                            || File.Exists(Path.Combine(ClientIntegrator.BackupRoot, "codex", "auth.json")),
        ClientKind.Pi => File.Exists(Path.Combine(ClientIntegrator.BackupRoot, "pi", "models.json")),
        _ => false,
    };

    private async Task ConfigureAsync()
    {
        await _owner.ConfigureClientsAsync([this]);
    }

    private async Task RestoreAsync()
    {
        await _owner.RestoreClientAsync(this);
    }

    /// <summary>外部改完状态后刷新这一行。</summary>
    public void Refresh(ClientState state)
    {
        Detected = state.Detected;
        Configured = state.Configured;
        Detail = state.Detail;
        Raise(nameof(CanRestore));
        RestoreCommand.RaiseCanExecuteChanged();
    }
}

public sealed partial class MainViewModel
{
    /// <summary>「一键接入」页的四个客户端。</summary>
    public ObservableCollection<ClientRowVm> Clients { get; } = [];

    public AsyncRelayCommand ConfigureAllCommand { get; private set; } = null!;
    public AsyncRelayCommand RestoreAllCommand { get; private set; } = null!;
    public RelayCommand RefreshClientsCommand { get; private set; } = null!;
    public RelayCommand OpenCcSwitchCommand { get; private set; } = null!;
    public AsyncRelayCommand SyncCcSwitchCommand { get; private set; } = null!;
    public AsyncRelayCommand ApplyFromCcSwitchCommand { get; private set; } = null!;

    private string _clientsSummary = "";
    public string ClientsSummary { get => _clientsSummary; private set => Set(ref _clientsSummary, value); }

    /// <summary>
    /// 接入客户端时写进配置里的模型。
    ///
    /// 特意和「对话」页的 SelectedModel 分开：那边是你手动试的东西，
    /// 这边是给 Claude Code / Codex / pi 长期跑的东西，默认要挑便宜的。
    /// </summary>
    private ModelRowVm? _integrateModel;
    public ModelRowVm? IntegrateModel
    {
        get => _integrateModel;
        set
        {
            if (Set(ref _integrateModel, value)) Raise(nameof(IntegrateModelHint));
        }
    }

    public string IntegrateModelHint
    {
        get
        {
            if (_integrateModel is null) return "还没读到模型列表";
            return _integrateModel.Rate == "x0.00"
                ? $"{_integrateModel.Name} 现在不计费（x0.00），适合大量跑"
                : $"{_integrateModel.Name} 会按 {_integrateModel.Rate} 扣积分";
        }
    }

    private string _integrateResult = "";
    public string IntegrateResult
    {
        get => _integrateResult;
        private set
        {
            if (Set(ref _integrateResult, value)) Raise(nameof(HasIntegrateResult));
        }
    }

    public bool HasIntegrateResult => _integrateResult.Length > 0;

    private void InitialiseClients()
    {
        ConfigureAllCommand = new AsyncRelayCommand(() => ConfigureClientsAsync(Clients.ToList()), null, ReportError);
        RestoreAllCommand = new AsyncRelayCommand(RestoreAllAsync, () => HasAnyBackup, ReportError);
        RefreshClientsCommand = new RelayCommand(() => _ = RefreshClientsAsync(), () => CanRunNode);
        OpenCcSwitchCommand = new RelayCommand(OpenCcSwitch);
        SyncCcSwitchCommand = new AsyncRelayCommand(SyncCcSwitchAsync, () => CanRunNode, ReportError);
        ApplyFromCcSwitchCommand = new AsyncRelayCommand(ApplyFromCcSwitchAsync, () => CanRunNode, ReportError);
        // 不在构造函数里探测：探测要读 4 个客户端的配置文件 + 查注册表，
        // 放在构造里会让窗口晚一拍才出来。改由 InitializeAsync 在窗口显示后做。
    }

    /// <summary>
    /// 探测四个客户端。
    ///
    /// 探测过程是纯同步的文件 I/O + 注册表查询（实测几十毫秒），
    /// 所以丢到后台线程跑，别卡住界面。
    /// </summary>
    private async Task RefreshClientsAsync()
    {
        if (!CanRunNode) return;

        var endpoint = CurrentEndpoint();
        List<ClientState> states;
        try
        {
            states = await Task.Run(() => ClientIntegrator.Inspect(endpoint).ToList());
        }
        catch (Exception ex)
        {
            AppendLog($"[接入] 探测客户端失败：{ex.Message}");
            return;
        }

        // 第一次进来直接建表，之后按 Kind 原地更新，避免整页闪一下
        if (Clients.Count != states.Count)
        {
            Clients.Clear();
            foreach (var state in states) Clients.Add(new ClientRowVm(this, state));
        }
        else
        {
            for (var i = 0; i < states.Count; i++) Clients[i].Refresh(states[i]);
        }

        var ready = Clients.Count(row => row.Detected);
        ClientsSummary = $"检测到 {ready} 个客户端（共 4 个）";

        // 默认挑不要钱的：这个页面的典型用途就是“不想烧额度”
        if (IntegrateModel is null && Models.Count > 0)
        {
            IntegrateModel = Models.FirstOrDefault(model => model.Rate == "x0.00") ?? Models[0];
        }

        // 备份扫描的结果缓存起来：WPF 会频繁调 CanExecute，
        // 而扫目录是不该反复做的事。
        RefreshBackupFlag();

        Raise(nameof(HasIntegrateResult));
        RestoreAllCommand.RaiseCanExecuteChanged();
    }

    /// <summary>扫一遍备份目录，把结果存进字段。只在真正做完动作后重算。</summary>
    private void RefreshBackupFlag()
    {
        try
        {
            HasAnyBackup = ClientIntegrator.HasAnyBackup();
        }
        catch
        {
            HasAnyBackup = false;
        }
    }

    private bool _hasAnyBackup;

    /// <summary>有没有可还原的备份。缓存的，不在 CanExecute 里扫盘。</summary>
    public bool HasAnyBackup
    {
        get => _hasAnyBackup;
        private set
        {
            if (Set(ref _hasAnyBackup, value)) RestoreAllCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 当前该让客户端指向哪里。
    ///
    /// ── 为什么必须跟池走 ────────────────────────────────────
    ///
    /// 以前这里直接读主编的 BaseUrl / ApiKey —— 那两个永远是默认池的。
    /// 于是切到志强池后点「一键接入」，写进 Claude Code / Codex / pi
    /// 的配置是**默认池**的地址和 key：用户以为配的是志强池，实际配了默认池。
    /// 一个界面里两处说法不一致，实测就是这么撞的。
    /// </summary>
    public GatewayEndpoint CurrentEndpoint()
    {
        // 当前池的地址（默认池就是主编那个，行为不变）
        string baseUrl;
        if (SelectedPool is { IsDefault: false } pool)
        {
            baseUrl = $"http://127.0.0.1:{pool.Port}/v1";
        }
        else
        {
            baseUrl = !string.IsNullOrWhiteSpace(BaseUrl)
                ? BaseUrl
                : $"http://127.0.0.1:{(int.TryParse(Port, out var p) && p > 0 ? p : 8787)}/v1";
        }

        var model = IntegrateModel?.Id;
        if (string.IsNullOrWhiteSpace(model)) model = SelectedModel?.Id;
        if (string.IsNullOrWhiteSpace(model))
        {
            // 没手动选过就按网关自己的规矩来：优先免费的
            model = Models.FirstOrDefault(m => m.Rate == "x0.00")?.Id ?? Models.FirstOrDefault()?.Id ?? "";
        }

        return GatewayEndpoint.From(baseUrl, CurrentApiKey, model);
    }

    /// <summary>
    /// 把选中的客户端配好。动手之前先确保网关在跑——否则配完也是连不通。
    /// </summary>
    public async Task ConfigureClientsAsync(IReadOnlyList<ClientRowVm> rows)
    {
        var targets = rows.Where(row => row.Detected).ToList();
        if (targets.Count == 0)
        {
            IntegrateResult = "这四个客户端一个都没检测到。先安装并至少运行一次，让它们把配置文件建出来。";
            return;
        }

        // 确保**当前看的那一个池**在跑（不是默认池）。
        //
        // 以前这里查的是 IsGatewayRunning —— 那是主编（默认池）的状态。
        // 切到志强池时志强池没跑，但它会以为跑着，于是直接往下配，
        // 配出一个连不通的地址。
        if (!CurrentPoolRunning)
        {
            var started = await TryStartCurrentPoolAsync();
            if (!started)
            {
                IntegrateResult = $"「{CurrentPoolLabel}」的网关没起来，先把它启动再配客户端（看「日志」标签页）。";
                await RefreshClientsAsync();
                return;
            }
        }

        var endpoint = CurrentEndpoint();
        var specs = Models.Select(model => model.ToSpec()).ToList();
        var lines = new List<string> { $"网关地址：{endpoint.BaseUrl}　　模型：{endpoint.Model}" };

        // 写配置是同步的文件 I/O（还要备份、读注册表），丢后台线程做，
        // 别让界面在“正在配置 4 个客户端”的几百毫秒里僵住。
        await Task.Run(() =>
        {
            foreach (var row in targets)
            {
                try
                {
                    lines.Add("✓ " + ClientIntegrator.Configure(row.Kind, endpoint, specs));
                }
                catch (Exception ex)
                {
                    lines.Add($"✗ {row.Name} 配置失败：{ex.Message}");
                    AppendLog($"[接入] {row.Name} 失败：{ex}");
                }
            }
        });

        lines.Add("");
        lines.Add("备份在 " + ClientIntegrator.BackupRoot);
        IntegrateResult = string.Join("\n", lines);

        foreach (var line in lines) AppendLog("[接入] " + line);
        await RefreshClientsAsync();

        // 写完客户端配置，再把**同一份内容**镜像给 CC Switch。
        //
        // 顺序很重要：镜像读的就是刚写好的那些文件。
        // 为什么一定要写：CC Switch 切换供应商时是**整体覆盖**配置文件的，
        // 它列表里没我们这一条的话，用户切一下就把刚写的东西冲掉了。
        if (CcSwitch.IsAvailable) await SyncCcSwitchAsync();

        FlashStatus(CcSwitch.IsAvailable
            ? $"已配置 {targets.Count} 个客户端，并同步到 CC Switch"
            : $"已配置 {targets.Count} 个客户端");
    }

    internal async Task RestoreClientAsync(ClientRowVm row)
    {
        var message = await Task.Run(() => ClientIntegrator.Restore(row.Kind));
        IntegrateResult = message;
        AppendLog("[接入] " + message);
        await RefreshClientsAsync();
        FlashStatus(message);
    }

    private async Task RestoreAllAsync()
    {
        var messages = new List<string>();

        // 还原是同步的文件拷贝，丢后台线程做
        await Task.Run(() =>
        {
            foreach (var kind in new[] { ClientKind.ClaudeCode, ClientKind.Codex, ClientKind.Pi })
                messages.Add(ClientIntegrator.Restore(kind));
        });

        IntegrateResult = string.Join("\n", messages);
        foreach (var message in messages) AppendLog("[接入] " + message);
        await RefreshClientsAsync();

        // 还原后 CC Switch 里那份就过期了，顺手对齐一下状态显示
        if (CcSwitch.IsAvailable) await RefreshCcSwitchStateAsync();

        FlashStatus("已还原所有客户端配置");
    }

    private string _ccSwitchSummary = "";
    public string CcSwitchSummary { get => _ccSwitchSummary; private set => Set(ref _ccSwitchSummary, value); }

    private bool _ccSwitchAvailable;
    public bool CcSwitchAvailable
    {
        get => _ccSwitchAvailable;
        private set => Set(ref _ccSwitchAvailable, value);
    }

    /// <summary>
    /// 把网关镜像进 CC Switch 的供应商列表。
    ///
    /// 内容不重算，而是把刚写进各 agent 配置文件的那份**原样搬过去** ——
    /// 两边永远一致，也不会出现“JS 和 C# 各算一遍算不一样”。
    ///
    /// 不动的：`is_current`（哪一个是“用户现在用的”应该由用户决定）。
    /// </summary>
    private async Task SyncCcSwitchAsync()
    {
        if (!CcSwitch.IsAvailable)
        {
            CcSwitchSummary = $"没找到 CC Switch 的库（{CcSwitch.DbPath}）—— 可能没装或从没启动过。" +
                              "这一步可以跳过，不影响网关本身。";
            AppendLog("[CC Switch] " + CcSwitchSummary);
            return;
        }

        IsBusy = true;
        BusyText = "正在写入 CC Switch…";
        try
        {
            var result = await RunCliAsync($"src/bin.js ccswitch --json{ProviderArgs()}");
            var json = CliRunner.ExtractJson(result.StdOut);

            if (json is null)
            {
                CcSwitchSummary = "写 CC Switch 失败，看「日志」标签页。";
                AppendLog("[CC Switch] " + result.Combined);
                return;
            }

            var dto = JsonSerializer.Deserialize<CcSwitchResultDto>(json);
            if (dto?.Ok != true)
            {
                CcSwitchSummary = dto?.Reason ?? "写 CC Switch 失败";
                AppendLog("[CC Switch] " + CcSwitchSummary);
                return;
            }

            var lines = new List<string>();
            if (dto.Written is { Count: > 0 })
                lines.Add($"已镜像到 CC Switch：{string.Join("、", dto.Written)}");
            if (dto.Adopted is { Count: > 0 })
            {
                var names = dto.Adopted
                    .Where(item => item is not null)
                    .Select(item => $"{item!.AppType}（你原来建的供应商「{item.Name}」）");
                lines.Add("复用了你已有的条目，没有重复新建：" + string.Join("、", names));
            }
            if (dto.Skipped is { Count: > 0 })
                lines.Add("跳过：" + string.Join("、", dto.Skipped));
            lines.Add("没有改动「当前使用」标记。要切过去在 CC Switch 里选中它即可。");

            CcSwitchSummary = string.Join("\n", lines);
            AppendLog("[CC Switch] " + CcSwitchSummary.Replace("\n", " / "));
            FlashStatus("已同步到 CC Switch");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    /// <summary>只读看一眼 CC Switch 里现在有什么。只在窗口显示后调。</summary>
    private async Task RefreshCcSwitchStateAsync()
    {
        CcSwitchAvailable = CcSwitch.IsAvailable;
        if (!CcSwitchAvailable) return;

        try
        {
            var result = await RunCliAsync($"src/bin.js ccswitch --status --json{ProviderArgs()}");
            var json = CliRunner.ExtractJson(result.StdOut);
            if (json is null) return;

            var dto = JsonSerializer.Deserialize<CcSwitchStatusDto>(json);
            var ours = dto?.Entries?.Where(entry => entry.IsOurs).ToList() ?? [];
            CcSwitchSummary = ours.Count == 0
                ? "CC Switch 里还没有 WorkBuddy 供应商。点「全部配置」会一并写进去。"
                : "CC Switch 里的 WorkBuddy 供应商：" + string.Join("、",
                    ours.Select(entry => $"{entry.AppType}/{entry.Id}{(entry.IsCurrent ? "（当前使用）" : "")}"));
        }
        catch (Exception ex)
        {
            AppendLog($"[CC Switch] 查询失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 从 CC Switch 的库反向写回客户端配置文件。
    ///
    /// 两个用途：修“库里有、文件里没有”的不一致；
    /// 或者在 CC Switch 界面里把供应商内容改好了，不用再去点它的“启用”。
    /// </summary>
    private async Task ApplyFromCcSwitchAsync()
    {
        IsBusy = true;
        BusyText = "正在写回客户端配置…";
        try
        {
            var result = await RunCliAsync($"src/bin.js ccswitch --apply --json{ProviderArgs()}");
            var json = CliRunner.ExtractJson(result.StdOut);

            if (json is null)
            {
                IntegrateResult = result.Combined;
                return;
            }

            var dto = JsonSerializer.Deserialize<CcSwitchApplyDto>(json);
            var lines = new List<string>();
            if (dto?.Applied is { Count: > 0 })
                lines.Add($"✓ 已写回客户端配置：{string.Join("、", dto.Applied)}");
            foreach (var item in dto?.Skipped ?? [])
                lines.Add($"· 跳过 {item}");
            if (dto?.Backups is { Count: > 0 })
            {
                lines.Add("");
                lines.Add("改前的文件都留了备份：");
                foreach (var path in dto.Backups) lines.Add("  " + path);
            }

            IntegrateResult = lines.Count > 0 ? string.Join("\n", lines) : "没有任何文件被修改。";
            AppendLog("[CC Switch] " + IntegrateResult.Replace("\n", " / "));
            FlashStatus(dto?.Applied is { Count: > 0 } ? "已从 CC Switch 写回客户端配置" : "没有可写回的内容");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }

        await RefreshClientsAsync();
    }

    /// <summary>打开 CC Switch 本体。</summary>
    private void OpenCcSwitch()
    {
        var exe = CcSwitch.FindExecutable();
        if (exe is null)
        {
            MessageBox.Show(
                "没找到 CC Switch 的安装位置。\n\n" +
                "如果你没装它，本程序不依赖它 —— 直接用「全部配置」就行，\n" +
                "本程序是直接改各客户端的配置文件。",
                "WorkBuddy 控制台", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            FlashStatus("已启动 CC Switch");
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }
}
