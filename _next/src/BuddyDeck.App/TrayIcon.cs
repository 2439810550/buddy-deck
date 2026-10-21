using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace BuddyDeck.App;

/// <summary>
/// 系统托盘图标与右键菜单。
///
/// ## 为什么用 WinForms 的 NotifyIcon
///
/// WPF **没有**自己的托盘 API。三条路：
///   A. WinForms.NotifyIcon        ← 选它
///   B. 手写 Shell_NotifyIcon P/Invoke
///   C. 第三方包
///
/// 选 A：旧版 WorkBuddyPanel 在同一技术栈上验证过；B 要自己处理窗口消息循环、
/// 菜单弹出、图标生命周期（工作量和 bug 面都翻倍）；C 多一个依赖。
/// 代价是 csproj 里开了 `UseWindowsForms`，见那里的注释。
///
/// ## ⚠ WPF + WinForms 混用时必须用别名
///
/// `Application` / `Brush` / `Color` 等名字两边都有。csproj 里已经
/// `<Using Remove="System.Windows.Forms" />` + `<Using Remove="System.Drawing" />`
/// 把隐式 using 摘掉了，所以本文件里**显式**用 `WinForms.` 前缀。
/// 直接 `using System.Windows.Forms;` 会让全项目的 `Application` 变成二义（48 条 CS0104）。
///
/// ## 关闭收进托盘，而不是退出
///
/// 这是用户点名要的老版行为：点关闭只是把它收起来，网关还在跑。
/// **但这个行为必须可关** —— 有人就是想要"关闭 = 退出"。
/// 托盘菜单里给了开关（<see cref="MinimizeToTray"/>），默认开。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>
    /// `NotifyIcon.Text` 的**硬上限是 63 个字符**，超了直接抛
    /// （旧版 TrayIcon.cs 的注释里也记着这条）。
    ///
    /// 我们的悬停提示要放"网关状态 + 端口 + 账号数"，很容易超 ——
    /// 所以统一走 <see cref="SetStatus"/> 截断，不指望每个调用方记得。
    /// </summary>
    private const int MaxTooltipLength = 63;

    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _openItem;
    private readonly WinForms.ToolStripMenuItem _trayItem;
    private readonly Action _onOpen;
    private readonly Action _onExit;

    private bool _disposed;

    /// <param name="onOpen">「显示主窗口」的动作。</param>
    /// <param name="onExit">「退出」的动作 —— 由调用方决定要不要顺带停网关。</param>
    public TrayIcon(Action onOpen, Action onExit)
    {
        _onOpen = onOpen;
        _onExit = onExit;

        _openItem = new WinForms.ToolStripMenuItem("显示主窗口", null, (_, _) => _onOpen());
        _trayItem = new WinForms.ToolStripMenuItem("关闭窗口时收进托盘")
        {
            CheckOnClick = true,
            Checked = true,
        };

        var exitItem = new WinForms.ToolStripMenuItem("退出", null, (_, _) => _onExit());

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(_openItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_trayItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            ContextMenuStrip = menu,
            Text = "BuddyDeck",
        };

        // 双击图标 = 打开窗口。这是托盘图标的通用约定，不给反而奇怪。
        _icon.DoubleClick += (_, _) => _onOpen();
    }

    /// <summary>
    /// 点窗口关闭时，是收进托盘还是真退出。
    ///
    /// 用户可以在托盘菜单里改。**默认收进托盘** —— 那是老版的行为，
    /// 也是用户点名要的。
    /// </summary>
    public bool MinimizeToTray => _trayItem.Checked;

    /// <summary>托盘菜单里的「显示主窗口」是否可用（窗口已经开着时可以置灰）。</summary>
    public bool OpenEnabled
    {
        get => _openItem.Enabled;
        set => _openItem.Enabled = value;
    }

    /// <summary>
    /// 更新悬停提示。
    ///
    /// **自动截断到 63 字符**并加省略号 —— 这是 WinForms 的硬限制，
    /// 超了会抛 ArgumentException（不是静默截断）。把截断收在这里，
    /// 而不是指望每个调用方算长度。
    /// </summary>
    public void SetStatus(string text)
    {
        if (_disposed) return;

        if (string.IsNullOrEmpty(text))
        {
            _icon.Text = "BuddyDeck";
            return;
        }

        _icon.Text = text.Length <= MaxTooltipLength
            ? text
            : text[..(MaxTooltipLength - 1)] + "…";
    }

    /// <summary>
    /// 取图标。
    ///
    /// 先试资源（csproj 里 `<Resource Include="Assets\tray.ico" />` 打进去的那份），
    /// **取不到就退到系统图标** —— 不能让"图标资源丢了"变成"托盘整个不出现"，
    /// 那会连带把"关闭收进托盘"也弄坏，用户点关闭就再也找不到窗口了。
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/tray.ico", UriKind.Absolute);
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
            {
                return new Icon(stream);
            }
        }
        catch (IOException)
        {
            // 资源读不到 —— 落到下面的兜底
        }
        catch (UriFormatException)
        {
        }

        // 兜底：用系统自带的"应用程序"图标。丑，但托盘功能是活的。
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 顺序很重要：先隐藏再释放。
        // 直接 Dispose 而不先 Visible=false 时，图标可能残留在通知区域
        // （要等用户把鼠标划过去才消失）—— 那是很显眼的 bug。
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
