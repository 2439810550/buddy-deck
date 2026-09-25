using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using WorkBuddyPanel.Services;

namespace WorkBuddyPanel;

/// <summary>
/// 激活窗口 —— 授权校验没过时显示。
///
/// 设计取向：**告诉用户怎么办**，不是「授权失败」四个字然后关掉。
/// 所以这个窗口一定给三样东西：
///   1. 机器码（复制按钮）
///   2. 授权文件该放哪（可一键打开那个目录）
///   3. 「我已放好，重新检查」按钮（不用重启程序）
/// </summary>
public partial class ActivationWindow : Window
{
    private readonly LicenseGuard.Result _result;

    public ActivationWindow(LicenseGuard.Result result)
    {
        InitializeComponent();
        _result = result;

        MachineCodeText.Text = MachineId.Current;

        // 按失败类型给不同的话术
        TitleText.Text = result.FailReason switch
        {
            LicenseGuard.Reason.NoLicenseFile => "需要授权",
            LicenseGuard.Reason.Expired => "授权已过期",
            LicenseGuard.Reason.MachineMismatch => "授权不属于这台机器",
            LicenseGuard.Reason.BadSignature => "授权文件无效",
            LicenseGuard.Reason.Unreadable => "读不到授权文件",
            LicenseGuard.Reason.BadFile => "授权文件格式不对",
            _ => "需要授权",
        };

        SubtitleText.Text = result.Message;

        // 机器信息（排查用）
        var comps = MachineId.Components();
        var lines = comps.Select(kv => $"{kv.Key,-12}{kv.Value}");
        ComponentsText.Text = string.Join(Environment.NewLine, lines);

        // 复制按钮的反馈直接在 Copy_Click 里处理，这里不再单独挂事件

        // 回车 = 重新检查（用户放好文件后不用找按钮）
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Retry_Click(this, new RoutedEventArgs());
            }
        };
    }

    /// <summary>校验通过后由外面读这个值。</summary>
    public LicenseGuard.Result? Passed { get; private set; }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(MachineId.Current);
            CopyButton.Content = "已复制";
            // 1.6 秒后恢复，给用户一个可见的回执
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1600),
            };
            timer.Tick += (_, _) =>
            {
                CopyButton.Content = "复制";
                timer.Stop();
            };
            timer.Start();
        }
        catch
        {
            MessageBox.Show(this, "复制失败，请手动选中那串机器码。", "复制",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 打开 exe 所在目录并选中授权文件（不存在就只打开目录）
            var dir = AppContext.BaseDirectory;
            var file = LicenseGuard.LicensePath;

            if (File.Exists(file))
                Process.Start("explorer.exe", $"/select,\"{file}\"");
            else
                Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开文件夹失败：{ex.Message}", "出错了",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        var again = LicenseGuard.Verify();

        if (again.Ok)
        {
            Passed = again;
            DialogResult = true;
            Close();
            return;
        }

        // 还是不行：更新提示，让用户看到是哪一步没过
        SubtitleText.Text = again.Message;
        TitleText.Text = again.FailReason switch
        {
            LicenseGuard.Reason.NoLicenseFile => "还是没找到授权文件",
            LicenseGuard.Reason.Expired => "授权已过期",
            LicenseGuard.Reason.MachineMismatch => "授权不属于这台机器",
            LicenseGuard.Reason.BadSignature => "授权文件无效",
            LicenseGuard.Reason.Unreadable => "读不到授权文件",
            LicenseGuard.Reason.BadFile => "授权文件格式不对",
            _ => "需要授权",
        };

        RetryButton.Content = "重新检查";
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
