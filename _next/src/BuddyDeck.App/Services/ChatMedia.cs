using System.Text;
using System.Text.RegularExpressions;

namespace BuddyDeck.App.Services;

/// <summary>
/// 聊天气泡里的一个内容块。
///
/// ## 为什么是「块流」而不是一个字符串
///
/// 生图/生视频的结果**不是文字** —— 它是一个 URL，要渲染成图或播放器。
/// 如果继续用「一个 Output 字符串」来表达，就只能把它当成一串地址显示，
/// 用户看到的是一行 URL 而不是图（这正是用户抱怨的「现在没法展示」）。
///
/// 所以消息由**有序的块**组成：文本块 + 图片块 + 视频块。
/// 顺序有意义 —— 回复常常是「好的，这是：」+ 图 + 「还想要别的吗？」。
///
/// ## 为什么放在 Services 而不是 ViewModels
///
/// 这个文件是**纯逻辑**（可脱离界面测），而 ViewModel 依赖
/// CommunityToolkit 的**源生成器**。源生成器不会为被 `Compile Include`
/// 链接进测试程序集的文件跑 —— 所以逻辑必须在能被链接的地方。
/// 这是本项目既有约定（同 `AccountPresentation` / `QuotaMath` / `TimeText`）。
/// </summary>
public abstract class ChatBlock
{
}

/// <summary>一段文字。</summary>
public sealed class TextBlock0 : ChatBlock
{
    public TextBlock0(string text) => Text = text;

    public string Text { get; }
}

/// <summary>
/// 一块媒体（图或视频）的**纯数据**。
///
/// ## 为什么这里不做通知
///
/// 这个文件要被 `Compile Include` 链接进测试项目，而测试项目刻意
/// 只链接纯逻辑（见 csproj 注释）。用 `ObservableObject` 会把
/// CommunityToolkit 的**源生成器**依赖带进来 —— 而源生成器不会为
/// 链接进来的文件跑，结果是 CS0246 + CS0759。**机制性的，加包无效。**
///
/// 所以分工是：
///   · **这个类**：纯数据 + 状态转换（可测）
///   · **`MediaBlockVm`**（ViewModels 下）：包一层，加通知（不可测，但也没逻辑）
///
/// 这与 `AccountPresentation`（纯逻辑）/ `AccountRowViewModel`（绑定层）
/// 是同一个分法 —— 本项目既有约定。
/// </summary>
public class MediaBlock : ChatBlock
{
    public MediaBlock(string url) => Url = url;

    /// <summary>媒体 URL（已过白名单的那些才有这个值）。</summary>
    public string Url { get; }

    /// <summary>占位与最终显示共用的宽度。**固定，不随加载状态变。**</summary>
    public const double PlaceholderWidthValue = 360;

    /// <summary>
    /// 占位与最终显示共用的高度。**固定。**
    ///
    /// 取 4:3 的保守值（图片常更宽、视频常 16:9）。关键是加载前后同值，
    /// 真实尺寸只在 `Stretch=Uniform` 的等比缩放里体现，不改外框。
    /// </summary>
    public const double PlaceholderHeightValue = 270;

    /// <summary>
    /// 占位的宽度（**实例属性**，不是常量）。
    ///
    /// ⚠ 为什么要包一层：**WPF 绑定读不到 `const` 字段** ——
    /// 绑定走的是反射查属性，而 `const` 在 IL 里是个字面量、没有属性描述符。
    /// 于是 `{Binding PlaceholderWidth}` 会**静默失败**，`MaxWidth` 保持默认的
    /// `PositiveInfinity`，图片就按原始像素撑满整行（实测撑到 1087px，
    /// 而这里写的是 360）。
    ///
    /// **这个坑的特征是「看起来像布局问题、其实是绑定没生效」** ——
    /// 和 t56 那次 `RelativeSource=Window` 是同一类。所以宁可多一个属性，
    /// 也不要让 XAML 去绑常量。
    /// </summary>
    public double PlaceholderWidth => PlaceholderWidthValue;

    /// <summary>占位的高度（实例属性，理由同上）。</summary>
    public double PlaceholderHeight => PlaceholderHeightValue;

    /// <summary>下载中。</summary>
    public bool IsLoading { get; private set; } = true;

    /// <summary>失败原因。非空时显示错误态而不是空白。</summary>
    public string? Error { get; private set; }

    /// <summary>可以显示了（成功且不在加载）。</summary>
    public bool IsReady => !IsLoading && Error is null;

    /// <summary>显示错误。</summary>
    public bool HasError => !IsLoading && !string.IsNullOrEmpty(Error);

    /// <summary>给界面的尺寸文字（如「123 KB」）。</summary>
    public string SizeText { get; private set; } = "";

    /// <summary>标记为下载完成。</summary>
    public void MarkReady(string sizeText = "")
    {
        IsLoading = false;
        Error = null;
        SizeText = sizeText;
    }

    /// <summary>标记为失败。</summary>
    public void MarkFailed(string error)
    {
        IsLoading = false;
        Error = error;
    }

    /// <summary>下载完成后回填大小（不改变加载状态）。</summary>
    public void SetSize(string sizeText) => SizeText = sizeText;
}

/// <summary>
/// 一张图片。界面里可点击放大。
///
/// ## 为什么图片走「内存解码」而不是落盘
///
/// 与 <see cref="VideoBlock"/> 形成对比，两者走不同路径是**故意的**：
///
///   · **视频必须落盘** —— `MediaElement` 要能 seek（拖进度条），
///     它需要一个可定位的源，内存流给不了。
///   · **图片不需要落盘** —— `BitmapImage` 可以直接从内存解码。
///     而且我们已经把字节取回来了（`FetchMediaAsync` 返回 `byte[]`），
///     再写一次盘再读回来是**白绕一圈**（还留下要清理的临时文件）。
///
/// ## ⚠ 装了图之后必须 `Freeze()`
///
/// `Image` 是 `BitmapSource`（`Freezable`）。如果不冻结：
///   · 它绑定到 UI 后属于创建它的那个线程
///   · 我们是在 `await` 之后的**线程池线程**上解码的（`ConfigureAwait(true)`
///     会回 UI 线程，但解码那一步本身在 await 之后、可能不在 UI 线程）
///   · 结论：不 Freeze 就可能抛「另一个线程拥有此对象」
///
/// 所以 <see cref="SetImage"/> 强制 `Freeze()`，把这一步收进一个方法里 ——
/// 而不是指望每个调用方都记得。
/// </summary>
public sealed class ImageBlock : MediaBlock
{
    public ImageBlock(string url) : base(url) { }

    /// <summary>解码后的位图。没解码前是 null。</summary>
    public System.Windows.Media.Imaging.BitmapSource? Image { get; private set; }

    /// <summary>
    /// 装解码好的图，**并且冻结它**。
    ///
    /// 冻结是一次性的：冻结后不能改像素。对我们的用途（显示一次）正合适，
    /// 而且换来跨线程可用 + 更少的 WPF 跟踪开销。
    /// </summary>
    public void SetImage(System.Windows.Media.Imaging.BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.CanFreeze) image.Freeze();
        Image = image;
    }
}

/// <summary>一段视频。界面用原生播放条。</summary>
public sealed class VideoBlock : MediaBlock
{
    public VideoBlock(string url, string localPath) : base(url) => LocalPath = localPath;

    /// <summary>
    /// 落盘后的本地路径。
    ///
    /// 为什么视频要先落盘：WPF 的 `MediaElement` **需要一个可定位的源**
    /// （要能 seek），给内存流是行不通的 —— 播放条拖不动。
    /// 图片则不需要（可从内存解码，见 <see cref="ImageBlock"/>）。
    /// </summary>
    public string LocalPath { get; }
}

/// <summary>
/// 聊天媒体的纯逻辑：识别、判定、解码后的分类。
///
/// 与 <see cref="MediaUrlGuard"/> 的分工：
///   · `MediaUrlGuard` 判「这个地址能不能去取」（安全边界）
///   · 这个类判「取回来的是什么、该怎么安排界面」（表现逻辑）
/// </summary>
public static class ChatMedia
{
    /// <summary>
    /// 从一段流式文本里认出媒体 URL。
    ///
    /// **判据是「文本里出现一个指向网关媒体路径的绝对 URL」**，
    /// 而不是「上游说这是图片」—— 聊天流里没有那种结构化标记：
    /// 生图的结果就是被拼成文本吐出来的。
    ///
    /// 策略是**宽松识别 + 严格校验**：
    ///   · 识别：正则找 `http(s)://…`
    ///   · 校验：交给 <see cref="MediaUrlGuard.IsAllowedMediaUrl"/>
    /// 识别过宽没关系（校验会挡），**漏识别才是问题**（用户看不到图）。
    /// </summary>
    public static bool TryExtractMediaUrl(string? chunk, int gatewayPort, out string url)
    {
        url = "";
        if (string.IsNullOrEmpty(chunk)) return false;

        var match = Regex.Match(
            chunk,
            @"https?://[^\s""'<>\)\]]+",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;

        // 句子末尾的标点不算 URL 的一部分
        var candidate = match.Value.TrimEnd('.', ',', ';', '。', '，', '…');

        if (!MediaUrlGuard.IsAllowedMediaUrl(candidate, gatewayPort)) return false;

        url = candidate;
        return true;
    }

    /// <summary>
    /// 按 URL 粗判是视频还是图片。
    ///
    /// **这只是第一遍猜测**（此时还没下载、拿不到 MIME）——
    /// 真判断在下载后用 <see cref="MediaBytes.IsVideo"/> 复核。
    /// 猜错不致命：视频猜成图片会走解码失败分支并显示错误，而不是静默。
    /// </summary>
    public static bool LooksLikeVideo(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        return url.Contains("/video", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按下载结果的形状造块（图片用扩展名猜的那类直接造 ImageBlock）。</summary>
    public static MediaBlock CreateBlock(string url, MediaBytes? media)
    {
        // 有 MIME 就以它为准（比 URL 后缀可靠）
        if (media is not null && media.IsVideo)
            return new VideoBlock(url, "");

        if (media is not null && media.IsImage)
            return new ImageBlock(url);

        // 没下载结果时按 URL 粗判
        return LooksLikeVideo(url) ? new VideoBlock(url, "") : new ImageBlock(url);
    }

    /// <summary>人类可读的字节数。</summary>
    public static string FormatSize(int bytes)
        => bytes < 1024
            ? $"{bytes} B"
            : bytes < 1024 * 1024
                ? $"{bytes / 1024.0:F0} KB"
                : $"{bytes / 1024.0 / 1024.0:F1} MB";

    /// <summary>
    /// 把消息块拼成纯文本（诊断用，也用于「有没有文字」的判断）。
    ///
    /// 媒体块**不参与** —— 它们不是文字。
    /// </summary>
    public static string ToPlainText(IEnumerable<ChatBlock> blocks)
        => string.Concat(blocks.OfType<TextBlock0>().Select(b => b.Text));

    /// <summary>把累积的文本刷成一个文本块（空则不产生块）。</summary>
    public static TextBlock0? FlushText(StringBuilder pending)
    {
        if (pending.Length == 0) return null;
        var t = new TextBlock0(pending.ToString());
        pending.Clear();
        return t;
    }
}
