using System.Diagnostics;

namespace BuddyDeckHub;

/// <summary>
/// 按配置拉起 workbuddy_api 实例。
///
/// ## 为什么每个实例要独立目录
///
/// workbuddy_api 用 WORKBUDDY_API_HOME 定位它自己的状态（账号库、配置、日志）。
/// 给每个渠道指定不同的目录，实例之间就完全隔离 —— 各自管各自的账号池，
/// 互不干扰。这是「一个渠道 = 一个账号池」能成立的基础。
///
/// ## 为什么 Hub 不管账号刷新
///
/// workbuddy_api 自己就有滑动窗口续期 + 签到。Hub 插手只会把事情搞复杂。
/// Hub 只负责：**确保实例在跑**。
/// </summary>
public sealed class ChannelSupervisor : IHostedService, IDisposable
{
    private readonly HubStore _store;
    private readonly ILogger<ChannelSupervisor> _log;
    private readonly Dictionary<string, Process> _spawned = new();
    private Timer? _timer;

    public ChannelSupervisor(HubStore store, ILogger<ChannelSupervisor> log)
    {
        _store = store;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // 启动时不急着拉 —— 先看有没有已经跑着的
        _timer = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    private async Task SweepAsync()
    {
        foreach (var channel in _store.Config.Channels.Where(c => c.Enabled))
        {
            // 只拉起「配了启动信息」的渠道。没配的说明是用户手动跑的，别乱动。
            if (channel.NodePath == "" || channel.ScriptDir == "" || channel.DataHome == "") continue;

            if (await IsAliveAsync(channel)) continue;
            Spawn(channel);
        }
    }

    /// <summary>探一下这个地址后面有没有活着的网关。</summary>
    public static async Task<bool> IsAliveAsync(Channel channel)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync(channel.BaseUrl.TrimEnd('/') + "/healthz");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void Spawn(Channel channel)
    {
        if (_spawned.TryGetValue(channel.Id, out var existing) && !existing.HasExited) return;

        try
        {
            var uri = new Uri(channel.BaseUrl);
            var psi = new ProcessStartInfo
            {
                FileName = channel.NodePath,
                WorkingDirectory = channel.ScriptDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add(Path.Combine(channel.ScriptDir, "src", "bin.js"));
            psi.ArgumentList.Add("serve");
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(uri.Port.ToString());
            psi.ArgumentList.Add("--host");
            psi.ArgumentList.Add("127.0.0.1");
            psi.ArgumentList.Add("--api-key");
            psi.ArgumentList.Add(channel.ApiKey);
            psi.ArgumentList.Add("--quiet");

            // 这一行是整个多账号方案的关键：给它一个独立的状态目录
            psi.Environment["WORKBUDDY_API_HOME"] = channel.DataHome;

            var proc = Process.Start(psi);
            if (proc is null)
            {
                _log.LogError("渠道 {Id} 启动失败：Process.Start 返回 null", channel.Id);
                return;
            }

            _spawned[channel.Id] = proc;
            _log.LogInformation("已拉起渠道 {Id}（{Name}，PID {Pid}，端口 {Port}）",
                channel.Id, channel.Name, proc.Id, uri.Port);

            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                _log.LogWarning("渠道 {Id} 进程退出（code {Code}）", channel.Id, proc.ExitCode);
                _spawned.Remove(channel.Id);
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "渠道 {Id} 拉起异常", channel.Id);
        }
    }

    /// <summary>停止 Hub 时把由它拉起的实例一并带走。</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);

        foreach (var (id, proc) in _spawned)
        {
            try
            {
                if (proc.HasExited) continue;
                proc.Kill(entireProcessTree: true);
                _log.LogInformation("已停止由 Hub 拉起的渠道 {Id}", id);
            }
            catch { /* 关不掉就算了，别在退出路径上抛 */ }
        }
        _spawned.Clear();
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        foreach (var p in _spawned.Values) p.Dispose();
    }
}
