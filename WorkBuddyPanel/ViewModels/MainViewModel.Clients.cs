using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
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
        RestoreCommand = new RelayCommand(Restore, () => CanRestore);
    }

    public ClientKind Kind { get; }
    public string Name { get; }
    public string ConfigPath { get; }
    public string Hint { get; }

    public AsyncRelayCommand ConfigureCommand { get; }
    public RelayCommand RestoreCommand { get; }

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

    private void Restore()
    {
        _owner.RestoreClient(this);
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
    public RelayCommand RestoreAllCommand { get; private set; } = null!;
    public RelayCommand RefreshClientsCommand { get; private set; } = null!;
    public RelayCommand OpenCcSwitchCommand { get; private set; } = null!;

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
        RestoreAllCommand = new RelayCommand(RestoreAll, () => ClientIntegrator.HasAnyBackup());
        RefreshClientsCommand = new RelayCommand(RefreshClients);
        OpenCcSwitchCommand = new RelayCommand(OpenCcSwitch);
        RefreshClients();
    }

    private void RefreshClients()
    {
        var states = ClientIntegrator.Inspect(CurrentEndpoint());

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

        Raise(nameof(HasIntegrateResult));
        RestoreAllCommand.RaiseCanExecuteChanged();
    }

    /// <summary>当前该让客户端指向哪里。网关没起时用配置里的端口拼一个，至少不会拼出空地址。</summary>
    public GatewayEndpoint CurrentEndpoint()
    {
        var baseUrl = !string.IsNullOrWhiteSpace(BaseUrl)
            ? BaseUrl
            : $"http://127.0.0.1:{(int.TryParse(Port, out var p) && p > 0 ? p : 8787)}/v1";

        var model = IntegrateModel?.Id;
        if (string.IsNullOrWhiteSpace(model)) model = SelectedModel?.Id;
        if (string.IsNullOrWhiteSpace(model))
        {
            // 没手动选过就按网关自己的规矩来：优先免费的
            model = Models.FirstOrDefault(m => m.Rate == "x0.00")?.Id ?? Models.FirstOrDefault()?.Id ?? "";
        }
        return GatewayEndpoint.From(baseUrl, ApiKey, model);
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

        if (!IsGatewayRunning)
        {
            var started = await TryStartGatewayAsync();
            if (!started)
            {
                IntegrateResult = "网关没起来，先修好网关再配客户端（看「日志」标签页）。";
                RefreshClients();
                return;
            }
        }

        var endpoint = CurrentEndpoint();
        var specs = Models.Select(model => model.ToSpec()).ToList();
        var lines = new List<string> { $"网关地址：{endpoint.BaseUrl}　　模型：{endpoint.Model}" };

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

        lines.Add("");
        lines.Add("备份在 " + ClientIntegrator.BackupRoot);
        IntegrateResult = string.Join("\n", lines);

        foreach (var line in lines) AppendLog("[接入] " + line);
        RefreshClients();
        StatusDetail = $"已配置 {targets.Count} 个客户端";
    }

    internal void RestoreClient(ClientRowVm row)
    {
        var message = ClientIntegrator.Restore(row.Kind);
        IntegrateResult = message;
        AppendLog("[接入] " + message);
        RefreshClients();
        StatusDetail = message;
    }

    private void RestoreAll()
    {
        var messages = new List<string>();
        foreach (var kind in new[] { ClientKind.ClaudeCode, ClientKind.Codex, ClientKind.Pi })
            messages.Add(ClientIntegrator.Restore(kind));

        IntegrateResult = string.Join("\n", messages);
        foreach (var message in messages) AppendLog("[接入] " + message);
        RefreshClients();
        StatusDetail = "已还原所有客户端配置";
    }

    private void OpenCcSwitch()
    {
        var exe = ClientIntegrator.FindCcSwitch();
        if (exe is null)
        {
            MessageBox.Show(
                "没找到 CC Switch 的安装位置。\n\n" +
                "如果你没装它，直接点上面的「全部配置」就行 —— 本程序是直接改各客户端的配置文件，" +
                "不依赖 CC Switch。",
                "WorkBuddy 控制台", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            StatusDetail = "已启动 CC Switch";
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }
}
