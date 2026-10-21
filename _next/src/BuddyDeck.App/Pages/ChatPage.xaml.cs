using System.Windows;
using BuddyDeck.App.ViewModels;

namespace BuddyDeck.App.Pages;

/// <summary>聊天测试页的代码后置。只做加载与「点击放大」，没有业务逻辑。</summary>
public partial class ChatPage : System.Windows.Controls.UserControl
{
    private readonly ChatViewModel _vm;

    public ChatPage()
    {
        _vm = App.Services.GetRequiredService<ChatViewModel>();
        InitializeComponent();
        DataContext = _vm;

        Loaded += async (_, _) => await _vm.LoadModelsAsync();

        // ⚠ **新消息到了要自动滚到底** —— 否则回复超出可视区时，
        //   用户看到的是"发完没反应"（新内容在下面、看不见）。
        //   这与"没有滚动条"是两个独立的问题，都会表现为"看不到消息"。
        //
        // 用 Dispatcher 异步排一帧再滚：集合刚变化时，ItemsControl 还没完成
        // 新项的测量与布局，此时 `ScrollToEnd()` 会滚到一个**旧的**范围尽头。
        _vm.Blocks.CollectionChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => BlockScroller.ScrollToEnd()));
        };
    }

    /// <summary>
    /// 点图片放大。
    ///
    /// 为什么用 `ContentDialog` 而不是新开窗口：它自带遮罩、ESC 关闭、
    /// 焦点陷阱，而且点空白处就能关。自造一个 Window 这些全要自己写，
    /// 还容易留下「关不掉」的 bug。
    ///
    /// 图仍然用 `Uniform` —— 放大只是给它更大的框，**不裁切、不拉伸变形**。
    /// </summary>
    private async void OnZoomImage(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ImageBlock block }) return;
        if (block.Image is null) return;

        var window = Window.GetWindow(this);
        var maxW = (window?.ActualWidth ?? 900) - 160;
        var maxH = (window?.ActualHeight ?? 700) - 200;

        var dialog = new Wpf.Ui.Controls.ContentDialog
        {
            Title = string.IsNullOrEmpty(block.SizeText) ? "查看大图" : $"查看大图（{block.SizeText}）",
            Content = new System.Windows.Controls.Image
            {
                Source = block.Image,
                Stretch = System.Windows.Media.Stretch.Uniform,
                MaxWidth = Math.Max(320, maxW),
                MaxHeight = Math.Max(240, maxH),
                Margin = new Thickness(0, 8, 0, 8),
            },
            CloseButtonText = "关闭",
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (InvalidOperationException)
        {
            // 极端情况下（同一时刻又点了一次）ContentDialog 会拒绝再次打开。
            // 这不是错误，静默忽略 —— 不要因为"多点了一下"弹异常给用户。
        }
    }

    /// <summary>
    /// 点「播放」开始播视频。
    ///
    /// ⚠ MediaElement 的 LoadedBehavior=Manual 时**不会自动播**，
    /// 而且它根本没有内置播放条 —— 不给一个明确的「播」动作，
    /// 用户看到的就是一块永远不动的黑框。
    ///
    /// 覆盖层在第一次点击后藏掉（播起来了它就没用了）；
    /// 视频未就绪时它也不该出现 —— 由它在视觉上叠在加载圈上，
    /// 但 DataTemplate 里它没有绑 IsLoading，
    /// 所以在处理器里再看一眼状态，未就绪就不播。
    /// </summary>
    private void OnPlayVideo(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: VideoBlock block } button) return;
        if (!block.IsReady) return;

        // MediaElement 与按钮同在一个 DataTemplate 的可视化树里，
        // 从按钮的父级往上找同模板里的 MediaElement。
        if (button.Parent is System.Windows.Controls.Grid grid)
        {
            foreach (var child in grid.Children)
            {
                if (child is System.Windows.Controls.MediaElement player)
                {
                    player.Play();
                    button.Visibility = Visibility.Collapsed;
                    return;
                }
            }
        }
    }
}
