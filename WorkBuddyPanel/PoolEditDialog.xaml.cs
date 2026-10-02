using System.Globalization;
using System.Windows;
using WorkBuddyPanel.Services;

namespace WorkBuddyPanel;

/// <summary>
/// 新建 / 编辑一个账号池。
///
/// 只有三个字段是有意为之：名字、备注、端口。
/// 目录名和 API Key 由程序自己生成 —— 那两个东西用户既不该关心，
/// 手填也只会出错（目录名会进环境变量，Key 要够随机）。
/// </summary>
public partial class PoolEditDialog : Window
{
    private readonly bool _isEdit;
    private readonly string _initialPort;

    public PoolEditDialog(string name, string? note, int port)
    {
        InitializeComponent();

        _isEdit = !string.IsNullOrWhiteSpace(name);
        TitleText.Text = _isEdit ? "编辑池" : "新建池";

        NameBox.Text = name;
        NoteBox.Text = note ?? "";
        _initialPort = port.ToString(CultureInfo.InvariantCulture);
        PortBox.Text = _initialPort;

        PortHint.Text = "每个池要占一个端口。默认池用 8787，其余的从 8788 开始排。";

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string PoolName { get; private set; } = "";

    public string? PoolNote { get; private set; }

    public int PoolPort { get; private set; }

    public bool PortChanged { get; private set; }

    private void OnPickFreePort(object sender, RoutedEventArgs e)
    {
        PortBox.Text = PoolStore.FindFreePort().ToString(CultureInfo.InvariantCulture);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "给这个池起个名字吧 —— 比如「朋友的池」。", "WorkBuddy 控制台",
                MessageBoxButton.OK, MessageBoxImage.Information);
            NameBox.Focus();
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "端口要填 1 到 65535 之间的数字。", "WorkBuddy 控制台",
                MessageBoxButton.OK, MessageBoxImage.Information);
            PortBox.Focus();
            PortBox.SelectAll();
            return;
        }

        // 端口被别人占了的话，node 启动时会报错。这里提前拦一下，
        // 但只在「这个端口现在真的有人监听」时才拦 —— 用户自己手工跑的
        // 东西也算占用，这一点和启动网关时的判断保持一致。
        if (PoolStore.IsPortInUse(port))
        {
            var answer = MessageBox.Show(this,
                $"端口 {port} 现在有程序在监听（可能是别的池、也可能是别的软件）。\n\n" +
                "继续用这个端口的话，这个池可能起不来。\n\n" +
                "要换成别的端口吗？",
                "WorkBuddy 控制台", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Yes)
            {
                PortBox.Text = PoolStore.FindFreePort().ToString(CultureInfo.InvariantCulture);
                PortBox.Focus();
                PortBox.SelectAll();
                return;
            }
        }

        PoolName = name;
        PoolNote = NoteBox.Text.Trim();
        PoolPort = port;
        PortChanged = _isEdit && PortBox.Text.Trim() != _initialPort;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
