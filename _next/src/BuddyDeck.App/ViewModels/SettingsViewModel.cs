using System.Collections.ObjectModel;

namespace BuddyDeck.App.ViewModels;

/// <summary>
/// 设置页。
///
/// 只放「改完会影响别的东西」的项 —— 那些纯粹是看一眼的信息放在总览页，
/// 不塞进设置里凑数。
///
/// ## 保存语义（t88 架构修正）
///
/// 改端口/key/变体后，**面板会重启自己的网关子进程**让新值生效 ——
/// 而不是只改网关的内存（那样端口/变体根本不会生效，Kestrel 绑端口在启动时）。
/// 所以 Save 的流程是：落盘 AppSettings → 重启网关 → 同步客户端。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private readonly IGatewayController _controller;
    private readonly AppSettings _settings;

    public SettingsViewModel(
        IGatewayClient gateway,
        IGatewayController controller,
        AppSettings settings)
    {
        _gateway = gateway;
        _controller = controller;
        _settings = settings;

        // 从**面板自己的设置**初始化，而不是等 LoadAsync ——
        // 这样离线时页面也是完整的（旧行为）。
        Port = settings.Port;
        ApiKey = settings.ApiKey;
        VaultPath = settings.VaultPath;
        _selectedVariant = Variants.FirstOrDefault(v => v.Id == settings.VariantId) ?? Variants[0];
    }

    [ObservableProperty]
    private int _port = 8791;

    [ObservableProperty]
    private string _apiKey = "";

    /// <summary>保险库文件的完整路径。</summary>
    [ObservableProperty]
    private string _vaultPath = "";

    /// <summary>网关日志文件的完整路径。</summary>
    [ObservableProperty]
    private string _logPath = "";

    /// <summary>选中的变体 —— 国内版 / 国际版。</summary>
    [ObservableProperty]
    private VariantOption? _selectedVariant;

    public ObservableCollection<VariantOption> Variants { get; } =
    [
        new("workbuddy", "国内版", "copilot.tencent.com · codebuddy.cn"),
        new("workbuddy-ai", "国际版", "www.workbuddy.ai（未实测）"),
    ];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _notice = "";

    [ObservableProperty]
    private bool _noticeIsError;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            // ★ 设置页**始终显示面板自己的设置**（数据库里的 AppSettings），
            //   不再从网关读 —— 网关只是被启动/停止的进程，不是设置的真相源。
            //   之前从网关读 settings 的路径已废弃：网关那边可能存着旧值（空 key），
            //   会把面板设置覆盖成空。
            Port = _settings.Port;
            ApiKey = _settings.ApiKey;
            VaultPath = _settings.VaultPath;
            SelectedVariant = Variants.FirstOrDefault(v => v.Id == _settings.VariantId) ?? Variants[0];
            Notice = "";
            NoticeIsError = false;
            await Task.CompletedTask.ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            // ★ 端口范围校验（网关侧也会拦，但这里先给用户明确的反馈）。
            if (Port is <= 0 or > 65535)
            {
                Notice = $"端口必须在 1-65535 之间，收到 {Port}";
                NoticeIsError = true;
                return;
            }

            // ① 落盘面板设置（重启网关时从它读新值）。
            _settings.Port = Port;
            _settings.ApiKey = ApiKey.Trim();
            _settings.VariantId = SelectedVariant?.Id ?? "workbuddy";
            _settings.VaultPath = VaultPath.Trim();
            _settings.Save();

            // ② 重启网关子进程（让新端口/key/变体生效）。
            //    端口冲突时这里会抛，把原因给用户看。
            try
            {
                await _controller.RestartAsync(ct).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Notice = $"保存失败，网关没起来：{ex.Message}";
                NoticeIsError = true;
                return;
            }

            // ③ 同步客户端自己的凭据（新端口/key）。
            _gateway.ApiKey = _settings.ApiKey;
            _gateway.Port = _settings.Port;

            Notice = "设置已保存，网关已重启。";
            NoticeIsError = false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>变体选项。</summary>
/// <param name="Id">内部标识（和 Core 的 Variants.cs 一致）。</param>
/// <param name="Name">界面上的名字。</param>
/// <param name="Detail">两个域名，给用户确认连的是哪个区。</param>
public sealed record VariantOption(string Id, string Name, string Detail);
