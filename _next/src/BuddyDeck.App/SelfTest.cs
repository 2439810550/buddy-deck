using System.Text;
using System.Windows;
using BuddyDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BuddyDeck.App;

/// <summary>
/// 诊断模式：`BuddyDeck.App.exe --selftest &lt;输出文件&gt;`
///
/// 用来回答「网关没起时界面到底拿到了什么」—— 把离线实现的每个调用结果
/// 和 ViewModel 的关键属性都写出来。这是给验收用的证据，不是产品功能。
/// 正常启动完全不碰。
/// </summary>
public static class SelfTest
{
    public static void Run(string outFile)
    {
        var sb = new StringBuilder();

        try
        {
            var gateway = App.Services.GetRequiredService<IGatewayClient>();
            sb.AppendLine($"IGatewayClient 实现 = {gateway.GetType().Name}");
            sb.AppendLine($"端口 = {gateway.Port}");
            sb.AppendLine();

            // ① 契约本身：这些调用在网关没起时必须返回结果，不能抛
            var health = gateway.GetHealthAsync().GetAwaiter().GetResult();
            sb.AppendLine($"GetHealthAsync -> Online={health.Online} Port={health.Port} Error={health.Error}");

            var accounts = gateway.GetAccountsAsync().GetAwaiter().GetResult();
            sb.AppendLine($"GetAccountsAsync -> Ok={accounts.Ok} Error={accounts.Error}");

            var metrics = gateway.GetMetricsAsync().GetAwaiter().GetResult();
            sb.AppendLine($"GetMetricsAsync -> Ok={metrics.Ok} Error={metrics.Error}");

            var pools = gateway.GetPoolsAsync().GetAwaiter().GetResult();
            sb.AppendLine($"GetPoolsAsync -> Ok={pools.Ok} Error={pools.Error}");

            var models = gateway.GetModelsAsync().GetAwaiter().GetResult();
            sb.AppendLine($"GetModelsAsync -> Ok={models.Ok} Error={models.Error}");

            var logs = gateway.ReadLogAsync(100).GetAwaiter().GetResult();
            sb.AppendLine($"ReadLogAsync -> Ok={logs.Ok} Error={logs.Error}");

            var settings = gateway.GetSettingsAsync().GetAwaiter().GetResult();
            sb.AppendLine($"GetSettingsAsync -> Ok={settings.Ok} Error={settings.Error}");
            sb.AppendLine();

            // ② 流式：网关没起时必须给一句人话，不抛
            sb.Append("ChatStreamAsync -> ");
            var chunks = 0;
            foreach (var _ in gateway.ChatStreamAsync("m", "p").ToBlockingEnumerable())
            {
                chunks++;
            }
            sb.AppendLine($"收到 {chunks} 段文本");
            sb.AppendLine();

            // ③ 六个 ViewModel：跑一遍刷新，看它们有没有炸
            var dashboard = App.Services.GetRequiredService<DashboardViewModel>();
            dashboard.RefreshAsync().GetAwaiter().GetResult();
            sb.AppendLine($"DashboardViewModel: Online={dashboard.Online} OfflineReason=\"{dashboard.OfflineReason}\" " +
                          $"AccountCount={dashboard.AccountCount} ConnectionHeadline=\"{dashboard.ConnectionHeadline}\"");

            var accountsVm = App.Services.GetRequiredService<AccountsViewModel>();
            accountsVm.RefreshAsync().GetAwaiter().GetResult();
            sb.AppendLine($"AccountsViewModel: Rows={accountsVm.Rows.Count} IsEmpty={accountsVm.IsEmpty} Notice=\"{accountsVm.Notice}\"");

            var poolsVm = App.Services.GetRequiredService<PoolsViewModel>();
            poolsVm.RefreshAsync().GetAwaiter().GetResult();
            sb.AppendLine($"PoolsViewModel: Pools={poolsVm.Pools.Count} IsEmpty={poolsVm.IsEmpty} Notice=\"{poolsVm.Notice}\"");

            var chatVm = App.Services.GetRequiredService<ChatViewModel>();
            chatVm.LoadModelsAsync().GetAwaiter().GetResult();
            sb.AppendLine($"ChatViewModel: Models={chatVm.Models.Count} ModelsNotice=\"{chatVm.ModelsNotice}\"");

            var settingsVm = App.Services.GetRequiredService<SettingsViewModel>();
            settingsVm.LoadAsync().GetAwaiter().GetResult();
            sb.AppendLine($"SettingsViewModel: Notice=\"{settingsVm.Notice}\" Port={settingsVm.Port}");

            var logsVm = App.Services.GetRequiredService<LogsViewModel>();
            logsVm.RefreshAsync().GetAwaiter().GetResult();
            sb.AppendLine($"LogsViewModel: Lines={logsVm.Lines.Count} Notice=\"{logsVm.Notice}\"");

            sb.AppendLine();
            sb.AppendLine("结论：所有调用都返回了结果，没有抛出异常。");
        }
        catch (Exception ex)
        {
            // 只要有任何一个调用抛了，这里就是验收失败的直接证据。
            sb.AppendLine();
            sb.AppendLine("!!! 有调用抛异常了 —— 这违反了 IGatewayClient 的「永不抛」契约：");
            sb.AppendLine(ex.ToString());
            Environment.ExitCode = 1;
        }

        var dir = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
    }
}
