using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace BuddyDeck.App.Services;

/// <summary>
/// <see cref="IClipboardImageSource"/> 的**生产实现** —— 真的去读系统剪贴板。
///
/// ## 为什么单独一个文件
///
/// 因为它是整个发图功能里**唯一**依赖 WPF 的地方。
/// <see cref="ImageInput"/>（编码与校验，即 90% 的逻辑）刻意不碰 WPF，
/// 这样它能在 xUnit 的 MTA 线程上被全量测试。
///
/// 这里的每一行都需要 **STA 线程 + WPF 消息泵**，
/// 所以它不参与单元测试 —— 只能靠界面里真跑一次来验。
/// 这个证据等级差异在交付回报里如实标注。
///
/// ## ⚠ 为什么先把 BitmapSource 编码成 PNG 字节
///
/// 剪贴板里的图**不一定是文件**：可能是别的程序（截图工具、浏览器）
/// 直接放在剪贴板上的位图对象。所以不能按「路径 → 读文件」处理，
/// 必须**重新编码**成一种确定的格式。
///
/// 选 PNG 而不是 JPEG：
///   · PNG 无损 —— 截图/UI 截图里的文字用 JPEG 会有可见的方块噪声
///   · 不涉及质量参数（JPEG 要选一个 quality，选了就要解释为什么）
///   · 代价是体积大一些，但 <see cref="ImageInput.MaxBytes"/> 那道闸门会兜住
/// </summary>
public sealed class WpfClipboardImageSource : IClipboardImageSource
{
    /// <inheritdoc />
    public byte[]? TryGetPngBytes()
    {
        // ⚠ `Clipboard.GetImage()` 在剪贴板里没有图时**返回 null**（不抛）。
        //   而剪贴板被别的进程锁住时会抛 COM 异常 —— 那一层由
        //   `ImageInput.FromClipboard` 接住并转成「读剪贴板失败」，
        //   不让它冒到界面上。
        var image = Clipboard.GetImage();
        if (image is null) return null;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }
}
