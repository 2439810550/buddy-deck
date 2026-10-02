using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace WorkBuddyPanel;

/// <summary>
/// 系统托盘图标。
///
/// 存在的意义：这个程序要在后台长期挂着网关，让用户一直盯着一个大窗口是不可接受的。
/// 有了托盘，关窗口 = 收进托盘（网关继续跑），托盘菜单里能开关网关、设置开机自启、真正退出。
///
/// 图标优先读嵌入资源 Assets/app.ico（鲸鱼娘）—— 跟 exe 图标同一个文件，
/// 由 tools/make-icon/make_whale_icon.py 生成。
///
/// 读不到时退回运行时画的字母图标（见 CreateFallbackGlyph）。留兜底的理由：
/// 托盘图标画不出来 = 用户在托盘里找不到程序，那比「图标不好看」严重得多。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly Icon _glyph;
    private readonly WinForms.ToolStripMenuItem _openItem;
    private readonly WinForms.ToolStripMenuItem _startItem;
    private readonly WinForms.ToolStripMenuItem _stopItem;
    private readonly WinForms.ToolStripMenuItem _autoStartItem;
    private readonly WinForms.ToolStripMenuItem _trayItem;
    private readonly System.Action _onExit;
    private readonly Func<bool> _isGatewayRunning;
    private readonly Func<bool> _canStartGateway;

    /// <summary>每次关闭窗口只提示一次「还在后台跑」，别每回都弹气泡。</summary>
    private bool _hintedAboutTray;

    public TrayIcon(
        System.Action onOpenPanel,
        System.Action onStartGateway,
        System.Action onStopGateway,
        System.Action onExit,
        Func<bool> isGatewayRunning,
        Func<bool> canStartGateway,
        Func<bool> isAutoStartEnabled,
        Action<bool> setAutoStart,
        Func<bool> isMinimizeToTray,
        Action<bool> setMinimizeToTray)
    {
        _onExit = onExit;
        _isGatewayRunning = isGatewayRunning;
        _canStartGateway = canStartGateway;

        _glyph = CreateTrayIcon();

        _openItem = new WinForms.ToolStripMenuItem("打开控制面板", null, (_, _) => onOpenPanel())
        {
            Font = new Font(WinForms.SystemInformation.MenuFont, FontStyle.Bold),
        };

        _startItem = new WinForms.ToolStripMenuItem("启动网关", null, (_, _) => onStartGateway());
        _stopItem = new WinForms.ToolStripMenuItem("停止网关", null, (_, _) => onStopGateway());

        _autoStartItem = new WinForms.ToolStripMenuItem("开机自动启动（静默进托盘）")
        {
            CheckOnClick = true,
        };
        _autoStartItem.Click += (_, _) => setAutoStart(_autoStartItem.Checked);

        _trayItem = new WinForms.ToolStripMenuItem("关闭窗口时收进托盘")
        {
            CheckOnClick = true,
        };
        _trayItem.Click += (_, _) => setMinimizeToTray(_trayItem.Checked);

        var exitItem = new WinForms.ToolStripMenuItem("退出（停止网关）", null, (_, _) => Exit());

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange(
        [
            _openItem,
            new WinForms.ToolStripSeparator(),
            _startItem,
            _stopItem,
            new WinForms.ToolStripSeparator(),
            _autoStartItem,
            _trayItem,
            new WinForms.ToolStripSeparator(),
            exitItem,
        ]);
        menu.Opening += (_, _) =>
        {
            var running = _isGatewayRunning();
            _startItem.Enabled = !running && _canStartGateway();
            _stopItem.Enabled = running;
            _autoStartItem.Checked = isAutoStartEnabled();
            _trayItem.Checked = isMinimizeToTray();
            _openItem.Text = running ? "打开控制面板（网关运行中）" : "打开控制面板";
        };

        _icon = new WinForms.NotifyIcon
        {
            Icon = _glyph,
            Text = "WorkBuddy 控制台",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => onOpenPanel();
        _icon.BalloonTipClicked += (_, _) => onOpenPanel();
    }

    /// <summary>网关状态变了就刷新提示文字。</summary>
    public void UpdateState(bool running)
    {
        try
        {
            _icon.Text = running
                ? "WorkBuddy 控制台 · 网关运行中"
                : "WorkBuddy 控制台 · 网关未启动";
        }
        catch
        {
            // NotifyIcon.Text 有 63 字符上限，超了会抛；这里不该发生，兜一下
        }
    }

    /// <summary>第一次收进托盘时提示一句，让用户知道程序没退。</summary>
    public void HintHiddenToTray()
    {
        if (_hintedAboutTray) return;
        _hintedAboutTray = true;
        Notify("WorkBuddy 控制台", "程序已收进托盘，网关仍在后台运行。双击托盘图标可以重新打开。");
    }

    public void Notify(string title, string message)
    {
        try
        {
            _icon.Visible = true;
            _icon.ShowBalloonTip(4000, title, message, WinForms.ToolTipIcon.Info);
        }
        catch
        {
            // 气泡失败不影响主流程
        }
    }

    private void Exit() => _onExit();

    /// <summary>运行时画一个圆底白字的图标，避免往仓库里放 .ico。</summary>
    /// <summary>
    /// 托盘图标：优先用打包进来的 Assets/app.ico。
    ///
    /// 为什么专门读资源而不是直接用 CreateFallbackGlyph：
    /// 托盘图标是用户看得最久的（程序常驻在托盘），那里用鲸鱼娘
    /// 比一个字母 W 有辨识度。
    ///
    /// 为什么必须有兜底：托盘图标画不出来 = 用户在托盘里找不到程序。
    /// 那比「图标不好看」严重得多，所以任何异常都退回画图。
    /// </summary>
    private static Icon CreateTrayIcon()
    {
        try
        {
            // pack://application:,,,/ —— 三个逗号是 WPF 的固定写法，
            // 不能省（一个或两个都解析失败）。
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var res = System.Windows.Application.GetResourceStream(uri);
            if (res is not null)
            {
                using var stream = res.Stream;
                // Icon 构造器会自己从多尺寸 ico 里挑合适的那档 ——
                // 这正是 app.ico 里塞 16/20/24/32/40/48/64/128/256 的意义。
                return new Icon(stream);
            }
        }
        catch (Exception ex)
        {
            // 资源丢了 / WPF 资源系统还没起来 / ico 坏了 —— 都不该让程序挂
            System.Diagnostics.Debug.WriteLine($"[托盘] 读图标资源失败，退回画的图标：{ex.Message}");
        }

        return CreateFallbackGlyph();
    }

    /// <summary>
    /// 兜底图标：运行时用 GDI+ 画一个蓝色圆圈 + 字母 W。
    ///
    /// 这是加 Assets/app.ico 之前的老实现。留着两个用处：
    ///   · 图标资源读不到时不至于没有托盘图标
    ///   · 也是「纯代码生成图标」的一个可参考实现
    /// </summary>
    private static Icon CreateFallbackGlyph()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var fill = new SolidBrush(Color.FromArgb(0x2F, 0x6F, 0xEB));
            g.FillEllipse(fill, 0, 0, size - 1, size - 1);

            using var font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("W", font, ink, new RectangleF(0f, 0f, size, size), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            // FromHandle 不接管句柄的生命周期，Clone 出真正拥有资源的副本再放掉原句柄
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
            // 关不掉就算了
        }

        _glyph.Dispose();
    }
}
