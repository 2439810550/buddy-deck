using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BuddyDeck.App.Services;

/// <summary>
/// 聊天发图的**输入核心类型**：把本地图片 / 剪贴板图片变成「能发出去的东西」，
/// 并在发出去之前把不该发的拒掉。
///
/// ## 为什么单独一个类型，而不是写在 ViewModel 里
///
/// 和 <see cref="AccountPresentation"/>、`TimeText` 同一个理由：
/// **逻辑放在这里，就能脱离 WPF 用普通 xUnit 跑**。
/// 校验规则（多大算超标、什么格式不认）是**业务规则**，
/// 不该只有「点开界面拖一张图」才能验。
///
/// ViewModel / 页面只做两件事：调这里的方法、把结果显示出来。
///
/// ## 「能发出去的形式」是哪一个
///
/// 走 **base64 data URI**，不是 multipart。依据是本网关自己的
/// `MediaEndpoints.cs:169-171`：
///
/// > OpenAI 规范里是 multipart/form-data（带 image 文件）。这里**同时接受
/// > JSON 形式** —— 国内很多客户端更愿意直接传 base64：
/// > `{ image: "&lt;base64 或 data URI 或 https URL&gt;", prompt, size? }`
///
/// 而聊天消息里的图片走 OpenAI 的 `image_url` part
/// （见 `AnthropicEndpoints.ToChatPart` 把 Anthropic 的 base64 image 翻成
/// `{type:"image_url",image_url:{url:"data:...;base64,..."}}`）——
/// **同样是 data URI**。所以 data URI 是这条链路上唯一贯通的形态。
///
/// ## ⚠ 这个类**不碰 WPF**
///
/// 剪贴板那一块通过 <see cref="IClipboardImageSource"/> 注入
/// （WPF 的实现见 <see cref="WpfClipboardImageSource"/>）。
/// 原因不是洁癖：`Clipboard.GetImage()` 要求 **STA 线程 + 消息泵**，
/// 在 xUnit 默认的 MTA 线程上直接调会抛 `ThreadStateException`。
/// 把「取图」和「编码校验」拆开后，**编码校验 100% 可测**，
/// 只有「调 WPF API 那一行」落在生产实现里。
/// </summary>
public static class ImageInput
{
    /// <summary>
    /// 单张图的大小上限：4 MiB。
    ///
    /// ## 为什么是这个数
    ///
    /// 上限不是拍脑袋来的，它要同时满足三件事：
    ///
    /// ① **base64 膨胀 4/3** —— 4 MiB 原始 → 约 5.33 MiB 的 base64 字符串。
    ///   所以真正发出去的是 5.33 MiB，不是 4 MiB。
    /// ② **上游请求体上限**。网关自己读请求体是有大小限制的
    ///   （`GatewayApp` 的 `BodyTooLargeException` 那条路），
    ///   聊天请求里除了这张图还有整段对话历史 —— 图不能把配额吃光。
    /// ③ **一张手机截图通常 1–3 MB**，4 MiB 覆盖绝大多数「拍一下发过去」的场景，
    ///   而 DSLR 原图（20 MB+）本来也不该直接塞进聊天。
    ///
    /// 超限时**拒绝并给出明确提示**，不静默压缩 —— 静默压缩会让用户
    /// 以为发的是原图（对齐本项目的既有取向：宁可报错，不要静默降级）。
    /// </summary>
    public const long MaxBytes = 4L * 1024 * 1024;

    /// <summary>
    /// 认识的扩展名 → MIME。
    ///
    /// 只收这四种，和网关那边一致（`MioraClient` 的媒体白名单同样是
    /// png / jpg / jpeg / webp / mp4；聊天发图不需要 mp4）。
    /// **不认识的一律拒**，不做「猜格式」——猜错会让上游收到一个
    /// 声明了 A 类型、实际是 B 类型的 part。
    /// </summary>
    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
    };

    /// <summary>认得的 MIME 集合（给界面显示「支持哪些格式」用）。</summary>
    public static IReadOnlyCollection<string> SupportedMimeTypes =>
        MimeByExtension.Values.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>支持的扩展名（给文件对话框的 filter 用）。</summary>
    public static IReadOnlyCollection<string> SupportedExtensions =>
        MimeByExtension.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // ══════════════════════════════════════════════════════════
    // 结果类型
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 一次取图尝试的结果。
    ///
    /// ## 为什么不用「抛异常」表达失败
    ///
    /// 用户选错文件是**预料之中**的操作（挑了个 .txt、图太大），
    /// 不是程序异常。用返回值表达，调用方就必须处理每一类原因 ——
    /// 抛异常的话很容易被一个宽泛的 catch 吞掉，变成「点了没反应」。
    ///
    /// <see cref="Message"/> 是**直接给用户看的**中文，调用方不需要再翻译一遍
    /// （否则同一个原因会在若干处被写成不同措辞）。
    /// </summary>
    public sealed record Result
    {
        /// <summary>成功时是图；失败时为 null。</summary>
        public ImageAttachment? Image { get; init; }

        /// <summary>失败原因（成功时为 <see cref="Failure.None"/>）。</summary>
        public Failure Reason { get; init; }

        /// <summary>失败时给用户看的一句话（成功时为空串）。</summary>
        public string Message { get; init; } = "";

        /// <summary>成不成功。比 `Reason == None` 更直白，调用方用这个。</summary>
        public bool Ok => Reason == Failure.None && Image is not null;

        internal static Result Success(ImageAttachment image) => new() { Image = image };

        internal static Result Fail(Failure reason, string message)
            => new() { Reason = reason, Message = message };
    }

    /// <summary>失败的原因分类。界面可以按它决定要不要提示「换个格式」。</summary>
    public enum Failure
    {
        /// <summary>没有失败。</summary>
        None = 0,

        /// <summary>路径为空、不存在、或不是文件。</summary>
        Missing = 1,

        /// <summary>文件是空的（0 字节）。</summary>
        Empty = 2,

        /// <summary>扩展名不在白名单里。</summary>
        UnsupportedFormat = 3,

        /// <summary>超过 <see cref="MaxBytes"/>。</summary>
        TooLarge = 4,

        /// <summary>读出来了，但字节内容不像这个格式（magic number 对不上）。</summary>
        Corrupt = 5,

        /// <summary>剪贴板里没有图。</summary>
        ClipboardEmpty = 6,

        /// <summary>读文件本身出错（权限、被占用）。</summary>
        IoError = 7,
    }

    // ══════════════════════════════════════════════════════════
    // ① 本地文件
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 从一个本地文件读出图片并按 data URI 编码。
    ///
    /// 校验顺序是**刻意排的**（每一步都比下一步便宜）：
    /// ```
    /// 扩展名 → 存在性 → 文件大小（先看 metadata，不读内容）→ 真的读 → magic number
    /// ```
    /// 「先看大小再读内容」很重要：一个 2 GB 的文件如果先 `ReadAllBytes`
    /// 再判大小，进程当场 OOM —— 拒绝得太晚等于没拒绝。
    /// </summary>
    public static Result FromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result.Fail(Failure.Missing, "没有选择文件");
        }

        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            // 路径里有非法字符（比如裸的 '\0'）—— GetExtension 会抛
            return Result.Fail(Failure.Missing, "文件路径不合法");
        }

        if (!MimeByExtension.TryGetValue(extension, out var mimeType))
        {
            return Result.Fail(Failure.UnsupportedFormat,
                $"不支持的图片格式「{extension}」——只支持 {FormatList()}");
        }

        long length;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Result.Fail(Failure.Missing, "找不到这个文件（可能已被移动或删除）");
            }
            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Result.Fail(Failure.IoError, $"读不了这个文件：{ex.Message}");
        }

        // ⚠ 空文件单独报 —— 「0 字节」和「格式不对」对用户是两件事：
        //   一个是他选错了文件，一个是文件被截断了。分开报能少一轮来回。
        if (length == 0)
        {
            return Result.Fail(Failure.Empty, "这个文件是空的（0 字节）");
        }

        // ⚠ 在读内容**之前**判大小 —— 先读再判会让大文件把内存吃爆
        if (length > MaxBytes)
        {
            return Result.Fail(Failure.TooLarge, TooLargeMessage(length));
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Result.Fail(Failure.IoError, $"读不了这个文件：{ex.Message}");
        }

        // ⚠ 再判一次：文件可能在 `FileInfo` 之后、`ReadAllBytes` 之前被写大
        //   （TOCTOU）。多这一次判断的成本可以忽略，而少了它上面那次
        //   大小检查就只是装饰。
        if (bytes.LongLength > MaxBytes)
        {
            return Result.Fail(Failure.TooLarge, TooLargeMessage(bytes.LongLength));
        }

        return Encode(bytes, mimeType, Path.GetFileName(path), Failure.Corrupt);
    }

    // ══════════════════════════════════════════════════════════
    // ② 原始字节（剪贴板、粘贴、测试都走这条）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 从一堆字节编码。剪贴板取到的图走这里
    /// （<see cref="WpfClipboardImageSource"/> 把 `BitmapSource` 编码成 PNG 字节后调用它）。
    ///
    /// @param declaredMime 调用方声明的类型。**仍会用 magic number 复核** ——
    ///                     剪贴板给的字节不保证和声明一致。
    /// @param displayName 给界面显示的名字（剪贴板图给「剪贴板图片」）。
    /// </summary>
    public static Result FromBytes(byte[]? bytes, string declaredMime, string displayName = "")
    {
        if (bytes is null || bytes.Length == 0)
        {
            return Result.Fail(Failure.Empty, "图片是空的（0 字节）");
        }

        if (bytes.LongLength > MaxBytes)
        {
            return Result.Fail(Failure.TooLarge, TooLargeMessage(bytes.LongLength));
        }

        return Encode(bytes, declaredMime, displayName, Failure.Corrupt);
    }

    // ══════════════════════════════════════════════════════════
    // ③ 剪贴板
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 从剪贴板取一张图并编码。
    ///
    /// **真实取图动作在 <see cref="IClipboardImageSource"/> 里**（生产实现
    /// 内部调 `Clipboard.GetImage()`）。这里只负责「拿到字节之后的编码与校验」，
    /// 所以整个方法可以在没有 STA 线程的测试里跑。
    /// </summary>
    public static Result FromClipboard(IClipboardImageSource? source)
    {
        if (source is null)
        {
            return Result.Fail(Failure.ClipboardEmpty, "读不了剪贴板");
        }

        byte[]? bytes;
        try
        {
            bytes = source.TryGetPngBytes();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // ⚠ 剪贴板是**跨进程**资源，别的程序正占着它的时候读会抛
            //   （ExternalException 之类）。这不该让整个界面崩 ——
            //   用户重试一次通常就好了。
            return Result.Fail(Failure.ClipboardEmpty, $"读剪贴板失败：{ex.Message}");
        }

        if (bytes is null || bytes.Length == 0)
        {
            return Result.Fail(Failure.ClipboardEmpty, "剪贴板里没有图片");
        }

        return FromBytes(bytes, "image/png", "剪贴板图片");
    }

    // ══════════════════════════════════════════════════════════
    // ④ 编码（校验通过之后）
    // ══════════════════════════════════════════════════════════

    private static Result Encode(byte[] bytes, string declaredMime, string displayName, Failure mismatchReason)
    {
        var actualMime = SniffMime(bytes);

        if (actualMime is null)
        {
            // ★ 这是「损坏图片」那一类：扩展名说是 PNG，字节却不是任何一个
            //   我们认识的图片头。**必须拒** —— 放过去的话上游会收到一个
            //   声明 image/png 却不是图的东西，报错信息在很深的地方，极难查。
            return Result.Fail(mismatchReason,
                $"这个文件的扩展名说是图片，但内容不是（可能是改了后缀名，或者文件损坏了）");
        }

        // 声明与实际不符：**以实际为准**，但记下来。
        // 为什么不拒：剪贴板那条路声明的都是 image/png，而 WPF 在某些
        // 情况下会给出别的编码；按实际类型发才是对的。
        var mimeType = actualMime;

        _ = declaredMime;

        var base64 = Convert.ToBase64String(bytes);

        return Result.Success(new ImageAttachment
        {
            DataUri = $"data:{mimeType};base64,{base64}",
            MimeType = mimeType,
            ByteCount = bytes.LongLength,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "图片" : displayName,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        });
    }

    /// <summary>
    /// 按**文件头**认类型，不信任扩展名。
    ///
    /// 为什么必须做这一步：扩展名是用户能随手改的，而
    /// 「改后缀名的 .txt 被当成 PNG 发出去」是这类功能最常见的一类脏数据。
    /// 识别不出来时返回 null，调用方按「损坏」拒掉。
    ///
    /// 认这四种（与 <see cref="MimeByExtension"/> 白名单一一对应）：
    /// ```
    /// PNG  89 50 4E 47 0D 0A 1A 0A
    /// JPEG FF D8 FF
    /// GIF  47 49 46 38            ← 认得出来但白名单不收，见下
    /// WEBP 52 49 46 46 … 57 45 42 50   （RIFF....WEBP）
    /// ```
    /// </summary>
    public static string? SniffMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        // ⚠ WEBP 的头在**第 8 字节之后**：RIFF????WEBP
        //   中间那 4 字节是文件长度，不能拿来做判据（会随文件变）。
        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        return null;
    }

    private static string TooLargeMessage(long actualBytes)
        => $"图片太大了：{Describe(actualBytes)}，上限是 {Describe(MaxBytes)}";

    /// <summary>把字节数说成人话（给错误提示用）。</summary>
    internal static string Describe(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.#} MB");
        }
        if (bytes >= 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB");
        }
        return $"{bytes} 字节";
    }

    private static string FormatList()
        => string.Join(" / ", SupportedExtensions.Select(e => e.TrimStart('.')));
}

/// <summary>
/// 一张已经编码好、可以直接塞进消息的图。
///
/// 字段就这几个：界面要显示名字和大小（<see cref="DisplayName"/> /
/// <see cref="ByteCount"/>），发请求要用 <see cref="DataUri"/>，
/// 去重用 <see cref="Sha256"/>（同一张图连发两次没必要各存一份）。
/// </summary>
public sealed record ImageAttachment
{
    /// <summary>`data:image/png;base64,……` —— 直接进 `image_url.url`。</summary>
    public required string DataUri { get; init; }

    /// <summary>实际类型（**由文件头认定**，不是扩展名）。</summary>
    public required string MimeType { get; init; }

    /// <summary>原始字节数（不是 base64 之后的大小）。</summary>
    public required long ByteCount { get; init; }

    /// <summary>界面显示用。</summary>
    public required string DisplayName { get; init; }

    /// <summary>内容哈希 —— 去重与日志用（**不要**把 DataUri 写进日志）。</summary>
    public required string Sha256 { get; init; }

    /// <summary>给人看的体积描述。</summary>
    public string SizeText => ImageInput.Describe(ByteCount);
}

/// <summary>
/// 「从剪贴板取一张图的 PNG 字节」这个动作的抽象。
///
/// ## 为什么要有这个接口
///
/// `System.Windows.Clipboard.GetImage()` 有两个硬约束：
///   · **必须在 STA 线程上**（xUnit 默认 MTA —— 直接调抛 `ThreadStateException`）
///   · **需要 WPF 消息泵**（无头环境下本来就取不到）
///
/// 把「取图」抽出来之后，编码与校验（这个类型里 90% 的逻辑）
/// 就能在普通测试里全量覆盖，而 WPF 那一行只出现在
/// <see cref="WpfClipboardImageSource"/> 里。
///
/// @returns 剪贴板里图的 PNG 字节；没有图时返回 null
///          （**不要**在这里抛「没有图」的异常 —— 那是正常情况，不是错误）。
/// </summary>
public interface IClipboardImageSource
{
    /// <summary>取剪贴板图片的 PNG 字节；没有图返回 null。</summary>
    byte[]? TryGetPngBytes();
}
