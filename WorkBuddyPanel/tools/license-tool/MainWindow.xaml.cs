using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Standard.Licensing;
using Standard.Licensing.Security.Cryptography;

namespace LicenseTool;

/// <summary>
/// 发卡工具（图形界面）。
///
/// 和命令行版（tools/make-license）共用同一套密钥和签名逻辑，
/// 签出来的授权文件完全一样 —— 这个只是把常用操作做成点几下就完。
///
/// 私钥口令不落盘：每次签的时候现输。窗口里那个框是用来**清空**的，
/// 不是用来记住的。
/// </summary>
public partial class MainWindow : Window
{
    private readonly string _keyDir;
    private string PrivateKeyPath => Path.Combine(_keyDir, "private.key");
    private string PublicKeyPath => Path.Combine(_keyDir, "public.key");

    /// <summary>本次会话里攒的待签清单。</summary>
    private readonly List<PendingItem> _pending = [];

    private sealed record PendingItem(string MachineId, string Name, string Note);

    public MainWindow()
    {
        InitializeComponent();

        // 密钥目录：优先找 exe 旁边的 keys/，再往上找 WorkBuddyPanel/keys/
        _keyDir = FindKeyDir();
        RefreshKeyStatus();
        RefreshPending();
    }

    /// <summary>
    /// 密钥目录。
    ///
    /// ## 为什么不放在 exe 旁边
    ///
    /// 之前是从 exe 目录往上找 `keys/` —— 那是绑在**目录结构**上的：
    ///
    ///     开发时  WorkBuddyPanel/tools/license-tool/bin/Release/ ← 往上找 5 层
    ///     发给朋友 WorkBuddy 发卡工具.exe 单独放一个文件夹 ← 找不到，或者找到错的
    ///     换台机器 路径全变了 ← 又要重新配
    ///
    /// 而且密钥是**你个人的东西**，不是程序的一部分 —— 它不该跟 exe 绑在一起。
    ///
    /// ## 放在哪
    ///
    ///     %USERPROFILE%\.workbuddy-license\
    ///         private.key
    ///         public.key
    ///
    /// 跟 exe 在哪无关。把 exe 搬到任何地方、换台机器，只要这两个文件还在，
    /// 就能继续发卡。换机器时拷这个目录过去就行。
    ///
    /// ## 兼容旧的
    ///
    /// 已经放在仓库 `keys/` 里的，还能找到（迁移用）。
    /// 找到后会提示一句，建议搬去新位置。
    /// </summary>
    private static string PreferredKeyDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".workbuddy-license");

    private static string? _legacyKeyDir;

    private static string FindKeyDir()
    {
        var preferred = PreferredKeyDir;

        // 1) 首选位置有密钥就用它
        if (File.Exists(Path.Combine(preferred, "private.key"))) return preferred;

        // 2) 从 exe 往上找（开发时的仓库 keys/，或者用户自己放在上级目录的）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "keys");
            if (File.Exists(Path.Combine(candidate, "private.key")))
            {
                _legacyKeyDir = candidate;
                return candidate;
            }
        }

        // 3) 都没有：用首选位置（首次运行时在那里生成）
        return preferred;
    }

    /// <summary>是不是在用旧位置的密钥（界面上要提示搬走）。</summary>
    private bool UsingLegacyDir =>
        _legacyKeyDir is not null &&
        string.Equals(_legacyKeyDir, _keyDir, StringComparison.OrdinalIgnoreCase);

    // ══════════════════════════════════════════════════════════
    //  密钥状态
    // ══════════════════════════════════════════════════════════

    private void RefreshKeyStatus()
    {
        var hasPrivate = File.Exists(PrivateKeyPath);
        var hasPublic = File.Exists(PublicKeyPath);

        if (hasPrivate && hasPublic)
        {
            KeyStatusText.Text = "密钥已就绪";
            KeyStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
            KeyPathText.Text = _keyDir;
            GenerateKeyButton.Visibility = Visibility.Collapsed;
            IssueButton.IsEnabled = true;

            // 还在用旧位置的密钥：提醒搬走。
            // 不搬的话，换个目录跑 exe 就又找不到了。
            if (UsingLegacyDir)
            {
                KeyStatusText.Text = "密钥已就绪（但位置不对，建议搬走）";
                KeyStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
                MigrateButton.Visibility = Visibility.Visible;
            }
            else
            {
                MigrateButton.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            KeyStatusText.Text = "还没有密钥，先点上面的「生成密钥对」";
            KeyStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
            KeyPathText.Text = _keyDir;
            GenerateKeyButton.Visibility = Visibility.Visible;
            MigrateButton.Visibility = Visibility.Collapsed;
            IssueButton.IsEnabled = false;
        }
    }

    /// <summary>
    /// 把密钥从旧位置搬到 %USERPROFILE%\.workbuddy-license\。
    ///
    /// 干嘛要搬：旧位置（仓库里的 keys/）跟目录结构绑着，
    /// 换个地方跑 exe 就找不到了。搬到用户目录之后，
    /// 只要这个目录在，exe 放哪都行。
    /// </summary>
    private void Migrate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = PreferredKeyDir;
            Directory.CreateDirectory(target);

            foreach (var name in new[] { "private.key", "public.key" })
            {
                var src = Path.Combine(_keyDir, name);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(target, name), overwrite: true);
            }

            // 删掉旧的。删不掉就算了（可能被占用），至少新的已经存好了。
            try
            {
                Directory.Delete(_keyDir, recursive: true);
            }
            catch { }

            MessageBox.Show(this,
                $"密钥已搬到：\n{target}\n\n" +
                "以后 exe 放哪都能找到它。备份的时候拷这个目录就行。",
                "搬迁完成", MessageBoxButton.OK, MessageBoxImage.Information);

            // 重新定位（_keyDir 是 readonly，重新开窗口最简单）
            MessageBox.Show(this,
                "请关掉重开这个工具，让新的密钥位置生效。",
                "需要重启", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"搬迁失败：{ex.Message}\n\n可以手工把 keys 目录拷到：\n{PreferredKeyDir}",
                "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void GenerateKey_Click(object sender, RoutedEventArgs e)
    {
        // 已有私钥的话先确认 —— 覆盖它会作废所有已发出的授权
        if (File.Exists(PrivateKeyPath))
        {
            var ok = MessageBox.Show(this,
                "已经有一对密钥了。\n\n" +
                "重新生成会作废**所有已经发出去的授权** —— " +
                "用户手里的 license.lic 全部失效，得重新发一遍。\n\n" +
                "确定要重新生成吗？",
                "重新生成密钥", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (ok != MessageBoxResult.Yes) return;
        }

        var pass = AskPassphrase("给私钥设个口令（发卡时要输，忘了就签不了）");
        if (pass is null) return;

        if (pass.Length < 6)
        {
            MessageBox.Show(this, "口令至少 6 位。这个口令保护的是整套授权的签发权。",
                "口令太短", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(_keyDir);
            var pair = KeyGenerator.Create().GenerateKeyPair();

            File.WriteAllText(PrivateKeyPath, pair.ToEncryptedPrivateKeyString(pass));
            File.WriteAllText(PublicKeyPath, pair.ToPublicKeyString());

            RefreshKeyStatus();

            // 把公钥显示出来 —— 要贴进主程序的 LicenseGuard.cs
            var pub = pair.ToPublicKeyString();
            PublicKeyBox.Text = pub;

            MessageBox.Show(this,
                $"密钥对已生成。\n\n" +
                $"私钥  {PrivateKeyPath}\n" +
                $"      → 备份好，别进 git，别发给任何人\n\n" +
                $"公钥  {PublicKeyPath}\n" +
                $"      → 已经填在下面的框里，把它贴进主程序的 LicenseGuard.cs\n\n" +
                $"（主程序里嵌的是公钥；如果换过密钥，要重新编译主程序）",
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"生成失败：{ex.Message}", "出错了",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyPublicKey_Click(object sender, RoutedEventArgs e)
    {
        var text = PublicKeyBox.Text?.Trim() ?? "";

        // 没有就现读文件
        if (text == "" && File.Exists(PublicKeyPath))
        {
            text = File.ReadAllText(PublicKeyPath).Trim();
            PublicKeyBox.Text = text;
        }

        if (text == "")
        {
            MessageBox.Show(this, "还没有公钥。先点「生成密钥对」。", "没有公钥",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 剪贴板被别的程序占着时会抛 COMException，重试几次基本都能成功
        if (ClipboardHelper.TrySetText(text))
        {
            MessageBox.Show(this, "公钥已复制。", "复制成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            // 全试完还失败：退到手动选中
            PublicKeyBox.Focus();
            PublicKeyBox.SelectAll();
            MessageBox.Show(this, "剪贴板被占住了。公钥已经选中，按 Ctrl+C 复制。",
                "请手动复制", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ══════════════════════════════════════════════════════════
    //  签发
    // ══════════════════════════════════════════════════════════

    private void Issue_Click(object sender, RoutedEventArgs e)
    {
        var machineId = MachineBox.Text.Trim().ToUpperInvariant();

        if (!IsPlausibleMachineId(machineId))
        {
            MessageBox.Show(this,
                "机器码看起来不对。\n\n" +
                "应该是 16 位十六进制，比如 E387198897C29D6E。\n" +
                "（用户在激活窗口上点「复制」就能拿到）",
                "机器码不对", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = NameBox.Text.Trim();
        if (name == "") name = "用户";

        var (expiresAt, periodText) = ResolveExpiry();
        if (expiresAt is null && PerpetualCheck.IsChecked != true)
        {
            MessageBox.Show(this, "天数填得不对。", "参数有误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pass = AskPassphrase($"口令（签给「{name}」）");
        if (pass is null) return;

        try
        {
            var xml = BuildLicense(machineId, name, expiresAt, pass);

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存授权文件",
                //
                // 文件名带用户信息，方便发卡方区分（一次发多个人的时候尤其有用）。
                //
                // 主程序不挑名字 —— 它会扫 exe 目录下所有 .lic，逐个验签。
                // 所以叫什么都行，这里只负责起个能看懂的名字。
                FileName = $"license-{name}-{machineId[..8]}.lic",
                DefaultExt = ".lic",
                Filter = "授权文件 (*.lic)|*.lic|所有文件 (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            };

            if (dialog.ShowDialog(this) != true) return;

            File.WriteAllText(dialog.FileName, xml, new UTF8Encoding(false));

            // 如果后缀不是 .lic，提醒一句 —— 程序只扫 .lic
            var finalExt = Path.GetExtension(dialog.FileName);
            var extWarn = finalExt.Equals(".lic", StringComparison.OrdinalIgnoreCase)
                ? ""
                : $"\n\n⚠ 后缀是 {finalExt}，程序只认 .lic 文件。\n" +
                  "   发给用户前改成 .lic。";

            // 记进待签清单（方便一次发多个）
            _pending.Add(new PendingItem(machineId, name, periodText));
            RefreshPending();

            var next = MessageBox.Show(this,
                $"✓ 已生成：\n{dialog.FileName}\n\n" +
                $"    机器码  {machineId}\n" +
                $"    授权给  {name}\n" +
                $"    有效期  {periodText}\n" +
                "\n" +
                "把这个文件发给用户，让他直接放进程序目录（文件名不用改）。" +
                extWarn +
                "\n\n继续签下一个吗？（会清空输入框）",
                "签发成功", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (next == MessageBoxResult.Yes)
            {
                MachineBox.Text = "";
                MachineBox.Focus();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"签发失败：{ex.Message}\n\n" +
                "如果提示口令不对，就是私钥口令输错了。",
                "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string BuildLicense(string machineId, string name, DateTime? expiresAt, string passphrase)
    {
        if (!File.Exists(PrivateKeyPath))
            throw new InvalidOperationException("找不到私钥，先点「生成密钥对」。");

        var builder = License.New()
            .WithUniqueIdentifier(Guid.NewGuid())
            .As(LicenseType.Standard)
            .WithMaximumUtilization(1)
            .WithProductFeatures(new Dictionary<string, string>
            {
                ["MachineId"] = machineId,
                ["Edition"] = "pro",
                ["IssuedAt"] = DateTime.UtcNow.ToString("O"),
            })
            .LicensedTo(name, "");

        if (expiresAt.HasValue) builder = builder.ExpiresAt(expiresAt.Value);

        var privateKey = File.ReadAllText(PrivateKeyPath);
        return builder.CreateAndSignWithPrivateKey(privateKey, passphrase).ToString();
    }

    /// <summary>读有效期。返回 (到期时间, 人话描述)。永久授权返回 (null, "永久")。</summary>
    private (DateTime? ExpiresAt, string Text) ResolveExpiry()
    {
        if (PerpetualCheck.IsChecked == true) return (null, "永久");

        var daysText = DaysBox.Text.Trim();
        if (!int.TryParse(daysText, out var days) || days <= 0)
            return (null, "");

        var until = DateTime.Now.AddDays(days);
        return (until, $"{days} 天（到 {until:yyyy-MM-dd}）");
    }

    private void Perpetual_Changed(object sender, RoutedEventArgs e)
    {
        // 勾上永久就不需要天数了
        if (DaysBox is null || PerpetualCheck is null) return;
        DaysBox.IsEnabled = PerpetualCheck.IsChecked != true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        MachineBox.Text = "";
        NameBox.Text = "";
        MachineBox.Focus();
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        var text = ClipboardHelper.TryGetText();
        if (text is not null) MachineBox.Text = text.Trim().ToUpperInvariant();
    }

    // ══════════════════════════════════════════════════════════
    //  待签清单
    // ══════════════════════════════════════════════════════════

    private void RefreshPending()
    {
        PendingList.ItemsSource = null;
        PendingList.ItemsSource = _pending
            .Select((x, i) => new
            {
                序号 = i + 1,
                机器码 = x.MachineId,
                授权给 = x.Name,
                有效期 = x.Note,
            })
            .ToList();

        PendingHint.Text = _pending.Count == 0
            ? "这个会话里签过的授权会列在这里，方便对账。关掉窗口就清空。"
            : $"本次会话已签 {_pending.Count} 个。";
    }

    private void ClearPending_Click(object sender, RoutedEventArgs e)
    {
        _pending.Clear();
        RefreshPending();
    }

    // ══════════════════════════════════════════════════════════
    //  口令输入
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 弹个小窗读口令。用 PasswordBox（不回显），而且口令不落盘。
    ///
    /// 不用主窗口上的输入框：那样口令会留在界面上，截屏/录屏/旁边有人都会泄露。
    /// </summary>
    private string? AskPassphrase(string prompt)
    {
        var win = new Window
        {
            Title = "私钥口令",
            Width = 420,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9)),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
        };

        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = prompt,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28)),
        };
        Grid.SetRow(label, 0);

        var box = new PasswordBox
        {
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 14,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEC)),
            Background = Brushes.White,
        };
        Grid.SetRow(box, 1);

        var foot = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        Grid.SetRow(foot, 3);

        string? result = null;

        var okBtn = new Button
        {
            Content = "确定",
            MinWidth = 86,
            MinHeight = 36,
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        okBtn.Click += (_, _) =>
        {
            result = box.Password;
            win.DialogResult = true;
        };

        var cancelBtn = new Button
        {
            Content = "取消",
            MinWidth = 76,
            MinHeight = 36,
            Background = Brushes.White,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xEC)),
            BorderThickness = new Thickness(1),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        cancelBtn.Click += (_, _) => { win.DialogResult = false; };

        foot.Children.Add(okBtn);
        foot.Children.Add(cancelBtn);

        grid.Children.Add(label);
        grid.Children.Add(box);
        grid.Children.Add(foot);
        win.Content = grid;

        win.Loaded += (_, _) => box.Focus();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                result = box.Password;
                win.DialogResult = true;
            }
        };

        return win.ShowDialog() == true ? result : null;
    }

    private static bool IsPlausibleMachineId(string id) =>
        id.Length is >= 8 and <= 64 && id.All(c => char.IsAsciiHexDigit(c) || c == '-');
}
