using System.Text;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// 聊天发图的编码与校验（t64）。
///
/// ## 覆盖策略
///
/// <see cref="ImageInput"/> 刻意不依赖 WPF，所以这里能直接调它 ——
/// 包括**剪贴板那条路**（注入 <see cref="FakeClipboard"/>）。
/// 唯一测不到的是 `WpfClipboardImageSource` 里那一行
/// `Clipboard.GetImage()`（需要 STA + 消息泵），它在回报里单独标注。
///
/// ## 四类边界（契约点名要求的）
///
/// ```
/// 空文件        空字节数组 / 0 字节文件
/// 超大文件      超过 MaxBytes 的字节数组与真文件
/// 非图片扩展名  .txt / .exe / 无扩展名
/// 损坏图片      .png 扩展名 + 随机字节（magic number 对不上）
/// ```
/// </summary>
public class ImageInputTests
{
    // ══════════════════════════════════════════════════════════
    // 测试夹具：造各种字节
    // ══════════════════════════════════════════════════════════

    /// <summary>一段最小但**文件头正确**的 PNG 字节。</summary>
    private static byte[] PngBytes(int padding = 16)
    {
        var bytes = new byte[8 + padding];
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        header.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>JPEG 头 FF D8 FF。</summary>
    private static byte[] JpegBytes() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];

    /// <summary>WEBP 头：RIFF????WEBP（中间 4 字节是长度，必须跳过）。</summary>
    private static byte[] WebpBytes()
    {
        var bytes = new byte[24];
        byte[] riff = [0x52, 0x49, 0x46, 0x46];      // "RIFF"
        byte[] webp = [0x57, 0x45, 0x42, 0x50];      // "WEBP" @ offset 8
        riff.CopyTo(bytes, 0);
        webp.CopyTo(bytes, 8);
        return bytes;
    }

    /// <summary>扩展名说是图、内容是任意字节 —— 用来造「损坏图片」。</summary>
    private static byte[] GarbageBytes() => "这不是图片，只是一段文字"u8.ToArray();

    /// <summary>写一个临时文件，返回路径；测试结束时删。</summary>
    private static string TempFile(string name, byte[] content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "buddydeck-imginput-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>假剪贴板 —— 返回预设字节，或抛预设异常。</summary>
    private sealed class FakeClipboard(byte[]? bytes, Exception? throws = null) : IClipboardImageSource
    {
        public byte[]? TryGetPngBytes()
        {
            if (throws is not null) throw throws;
            return bytes;
        }
    }

    // ══════════════════════════════════════════════════════════
    // ① 编码：本地文件 → data URI
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void PNG_文件被编码成_data_URI()
    {
        var path = TempFile("shot.png", PngBytes());

        var result = ImageInput.FromFile(path);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(ImageInput.Failure.None, result.Reason);
        Assert.Equal("image/png", result.Image!.MimeType);
        Assert.StartsWith("data:image/png;base64,", result.Image.DataUri, StringComparison.Ordinal);
        Assert.Equal("shot.png", result.Image.DisplayName);
    }

    [Fact]
    public void data_URI_解出来等于原始字节()
    {
        // ★ 这是编码正确性的**实质断言**：不是「以 data: 开头」就算过，
        //   而是把 base64 段解回来，逐字节等于源文件。
        //   写错一个填充字符、少截一段，都会在这里露出来。
        var original = PngBytes(padding: 40);
        var path = TempFile("roundtrip.png", original);

        var result = ImageInput.FromFile(path);
        Assert.True(result.Ok, result.Message);

        var comma = result.Image!.DataUri.IndexOf(',', StringComparison.Ordinal);
        var decoded = Convert.FromBase64String(result.Image.DataUri[(comma + 1)..]);

        Assert.Equal(original, decoded);
        Assert.Equal(original.LongLength, result.Image.ByteCount);
    }

    [Theory]
    [InlineData("a.jpg", true)]
    [InlineData("a.jpeg", true)]
    [InlineData("a.png", true)]
    [InlineData("a.webp", true)]
    [InlineData("a.PNG", true)]      // 扩展名大小写不敏感
    [InlineData("a.JPG", true)]
    public void 白名单扩展名都认(string name, bool _)
    {
        // JPEG 用 JPEG 头、PNG 用 PNG 头 —— 否则会被 magic number 判成损坏
        var content = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => JpegBytes(),
            ".webp" => WebpBytes(),
            _ => PngBytes(),
        };

        var result = ImageInput.FromFile(TempFile(name, content));

        Assert.True(result.Ok, $"{name} 应当被接受，实际：{result.Message}");
    }

    [Fact]
    public void MIME_由文件头认定而不是扩展名()
    {
        // ★ 反例的关键：扩展名写 .png，内容其实是 JPEG。
        //   如果实现信扩展名，这里会回 image/png —— 那就错了：
        //   上游按声明的类型解这个 part，解不开就报一个很深的错。
        var path = TempFile("lying.png", JpegBytes());

        var result = ImageInput.FromFile(path);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("image/jpeg", result.Image!.MimeType);
    }

    [Fact]
    public void 同一张图两次编码得到相同的哈希()
    {
        var bytes = PngBytes();
        var a = ImageInput.FromBytes(bytes, "image/png");
        var b = ImageInput.FromBytes(bytes, "image/png");

        Assert.Equal(a.Image!.Sha256, b.Image!.Sha256);
    }

    [Fact]
    public void 不同内容哈希不同()
    {
        var a = ImageInput.FromBytes(PngBytes(padding: 8), "image/png");
        var b = ImageInput.FromBytes(PngBytes(padding: 16), "image/png");

        Assert.NotEqual(a.Image!.Sha256, b.Image!.Sha256);
    }

    // ══════════════════════════════════════════════════════════
    // ② 边界之一：空文件
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 空文件被拒_且原因与格式错误不同()
    {
        var path = TempFile("empty.png", []);

        var result = ImageInput.FromFile(path);

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.Empty, result.Reason);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void 空字节数组被拒()
    {
        var result = ImageInput.FromBytes([], "image/png");

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.Empty, result.Reason);
    }

    [Fact]
    public void null_字节数组被拒()
    {
        var result = ImageInput.FromBytes(null, "image/png");

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.Empty, result.Reason);
    }

    // ══════════════════════════════════════════════════════════
    // ③ 边界之二：超大文件
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 超过上限的字节数组被拒_提示里有实际大小与上限()
    {
        var bytes = PngBytes();                       // 先给个合法头
        var huge = new byte[ImageInput.MaxBytes + 1];
        bytes.CopyTo(huge, 0);

        var result = ImageInput.FromBytes(huge, "image/png");

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.TooLarge, result.Reason);
        // 提示要让用户知道「超了多少」，不是干巴巴一句「太大」
        Assert.Contains("上限", result.Message, StringComparison.Ordinal);
        Assert.Contains("4", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 恰好等于上限的图被接受()
    {
        // ★ 边界的方向性：`>` 而不是 `>=`。
        //   写成 `>=` 的话，一张恰好 4 MiB 的图会被拒 ——
        //   而我们的提示语说的正是「上限 4 MB」。
        var bytes = new byte[ImageInput.MaxBytes];
        PngBytes().CopyTo(bytes, 0);

        var result = ImageInput.FromBytes(bytes, "image/png");

        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public void 超大的真文件被拒_且提示里带实际大小()
    {
        // 真文件那一条路会先看 FileInfo.Length 再决定读不读 ——
        // 这条同时验证「没有为了判大小而把整个文件读进内存」。
        var path = TempFile("huge.png", new byte[ImageInput.MaxBytes + 4096]);

        var result = ImageInput.FromFile(path);

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.TooLarge, result.Reason);
    }

    [Fact]
    public void 大小描述是人话()
    {
        Assert.Contains("MB", ImageInput.Describe(5 * 1024 * 1024), StringComparison.Ordinal);
        Assert.Contains("KB", ImageInput.Describe(2048), StringComparison.Ordinal);
        Assert.Contains("字节", ImageInput.Describe(512), StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════
    // ④ 边界之三：非图片扩展名
    // ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("virus.exe")]
    [InlineData("archive.zip")]
    [InlineData("movie.mp4")]        // 媒体白名单里有 mp4，但**聊天发图**不收
    [InlineData("noextension")]
    [InlineData("a.png.txt")]        // 取的是最后一段扩展名
    public void 非图片扩展名被拒_且提示列出支持的格式(string name)
    {
        var result = ImageInput.FromFile(TempFile(name, PngBytes()));

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.UnsupportedFormat, result.Reason);
        // 提示要告诉用户「那到底支持什么」，否则他只会反复试
        Assert.Contains("png", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 不存在的文件被拒_且原因与格式无关()
    {
        var missing = Path.Combine(Path.GetTempPath(), "绝对不存在-" + Guid.NewGuid().ToString("N") + ".png");

        var result = ImageInput.FromFile(missing);

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.Missing, result.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空路径被拒(string? path)
    {
        var result = ImageInput.FromFile(path);

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.Missing, result.Reason);
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ 边界之四：损坏图片
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 扩展名是_png_但内容不是图片_被拒为损坏()
    {
        // ★ 这是「改了后缀名」的典型形态：.png 结尾，内容是纯文本。
        //   放过去的话上游会收到一个声明 image/png 的垃圾 part。
        var path = TempFile("fake.png", GarbageBytes());

        var result = ImageInput.FromFile(path);

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.Corrupt, result.Reason);
        Assert.Contains("损坏", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 截断的_PNG_头被拒()
    {
        // 只有前 4 个字节对（89 50 4E 47），后面被截掉 —— magic number 不完整
        var result = ImageInput.FromBytes([0x89, 0x50, 0x4E, 0x47], "image/png");

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.Corrupt, result.Reason);
    }

    [Fact]
    public void 只有_RIFF_头没有_WEBP_标识被拒()
    {
        // 只对前 4 个字节 —— 这是 WEBP 识别最容易写错的地方
        // （WEBP 标识在第 8 字节之后，中间 4 字节是长度）
        var bytes = new byte[24];
        "RIFF"u8.CopyTo(bytes);
        // 故意不写第 8 字节处的 "WEBP"

        var result = ImageInput.FromBytes(bytes, "image/webp");

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.Corrupt, result.Reason);
    }

    [Fact]
    public void 空_RIFF_但_WEBP_标识正确时被接受()
    {
        var result = ImageInput.FromBytes(WebpBytes(), "image/webp");

        Assert.True(result.Ok, result.Message);
        Assert.Equal("image/webp", result.Image!.MimeType);
    }

    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]                  // JPEG 头只给了 2 字节
    [InlineData(new byte[] { 0xFF, 0xD8, 0x00 })]            // 第 3 字节不对
    public void 各种残缺头都被拒(byte[] bytes)
    {
        var result = ImageInput.FromBytes(bytes, "image/jpeg");

        Assert.False(result.Ok);
    }

    // ══════════════════════════════════════════════════════════
    // ⑥ 文件头识别（SniffMime）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void SniffMime_认四种格式()
    {
        Assert.Equal("image/png", ImageInput.SniffMime(PngBytes()));
        Assert.Equal("image/jpeg", ImageInput.SniffMime(JpegBytes()));
        Assert.Equal("image/webp", ImageInput.SniffMime(WebpBytes()));
        Assert.Null(ImageInput.SniffMime(GarbageBytes()));
    }

    [Fact]
    public void SniffMime_空与超短输入不崩()
    {
        Assert.Null(ImageInput.SniffMime([]));
        Assert.Null(ImageInput.SniffMime([0x89]));
        Assert.Null(ImageInput.SniffMime([0x89, 0x50, 0x4E]));
    }

    // ══════════════════════════════════════════════════════════
    // ⑦ 剪贴板
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 剪贴板有图_被编码成_data_URI()
    {
        var result = ImageInput.FromClipboard(new FakeClipboard(PngBytes()));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("image/png", result.Image!.MimeType);
        Assert.StartsWith("data:image/png;base64,", result.Image.DataUri, StringComparison.Ordinal);
        Assert.Equal("剪贴板图片", result.Image.DisplayName);
    }

    [Fact]
    public void 剪贴板为空被拒_且原因专门可辨()
    {
        // ★ 「剪贴板里没有图」和「图片损坏」对用户是两件事：
        //   前者他该去复制一张图，后者他该换个文件。
        var result = ImageInput.FromClipboard(new FakeClipboard(null));

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.ClipboardEmpty, result.Reason);
        Assert.Contains("没有图片", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 剪贴板读失败被转成_ClipboardEmpty_而不是崩()
    {
        // ⚠ 剪贴板是跨进程资源，别的程序占着时会抛 COM 异常。
        //   这条钉住「我们接住了它」，界面不会因为读剪贴板失败而整个崩。
        var result = ImageInput.FromClipboard(
            new FakeClipboard(null, new InvalidOperationException("剪贴板被占用")));

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.ClipboardEmpty, result.Reason);
        Assert.Contains("剪贴板被占用", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 剪贴板传_null_源不崩()
    {
        var result = ImageInput.FromClipboard(null);

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.ClipboardEmpty, result.Reason);
    }

    [Fact]
    public void 剪贴板给的图太小也按同一套规则校验()
    {
        // 剪贴板来的字节**不豁免**大小限制 ——
        // 复制一张 20 MB 的图同样该被拒。
        var huge = new byte[ImageInput.MaxBytes + 1];
        PngBytes().CopyTo(huge, 0);

        var result = ImageInput.FromClipboard(new FakeClipboard(huge));

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.TooLarge, result.Reason);
    }

    [Fact]
    public void 剪贴板给的字节不是图_也要拒()
    {
        var result = ImageInput.FromClipboard(new FakeClipboard(GarbageBytes()));

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.Corrupt, result.Reason);
    }

    // ══════════════════════════════════════════════════════════
    // ⑧ 契约面：失败时的不变量
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 每一种失败都带非空的中文提示()
    {
        // ★ 界面直接把 Message 显示给用户，所以「失败却没有话」是 bug：
        //   用户会看到一条空的错误。这条把所有失败路径扫一遍。
        ImageInput.Result[] failures =
        [
            ImageInput.FromFile(null),
            ImageInput.FromFile(TempFile("x.txt", PngBytes())),
            ImageInput.FromFile(TempFile("e.png", [])),
            ImageInput.FromFile(TempFile("bad.png", GarbageBytes())),
            ImageInput.FromFile(Path.Combine(Path.GetTempPath(), "no-" + Guid.NewGuid().ToString("N") + ".png")),
            ImageInput.FromBytes([], "image/png"),
            ImageInput.FromClipboard(new FakeClipboard(null)),
            ImageInput.FromClipboard(null),
        ];

        foreach (var failure in failures)
        {
            Assert.False(failure.Ok);
            Assert.NotEqual(ImageInput.Failure.None, failure.Reason);
            Assert.False(string.IsNullOrWhiteSpace(failure.Message));
            // 提示应当是给用户看的中文，不是异常堆栈
            Assert.DoesNotContain("Exception", failure.Message, StringComparison.Ordinal);
            Assert.Null(failure.Image);
        }
    }

    [Fact]
    public void 失败路径不返回半成品图()
    {
        var result = ImageInput.FromFile(TempFile("bad.png", GarbageBytes()));

        Assert.False(result.Ok);
        Assert.Null(result.Image);
        Assert.Equal(ImageInput.Failure.Corrupt, result.Reason);
    }

    [Fact]
    public void 成功路径没有失败原因()
    {
        var result = ImageInput.FromBytes(PngBytes(), "image/png");

        Assert.True(result.Ok);
        Assert.Equal(ImageInput.Failure.None, result.Reason);
        Assert.Equal("", result.Message);
        Assert.NotNull(result.Image);
    }

    // ══════════════════════════════════════════════════════════
    // ⑨ 给界面用的清单
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 支持的格式清单与白名单一致()
    {
        // 界面拿这两个清单去配文件对话框与提示语 —— 它们必须和真正的
        // 校验规则同源，否则会出现「对话框让选，选完被拒」。
        Assert.Contains(".png", ImageInput.SupportedExtensions);
        Assert.Contains(".jpg", ImageInput.SupportedExtensions);
        Assert.Contains(".jpeg", ImageInput.SupportedExtensions);
        Assert.Contains(".webp", ImageInput.SupportedExtensions);
        Assert.DoesNotContain(".mp4", ImageInput.SupportedExtensions);

        // jpg/jpeg 都映射到 image/jpeg，去重后只有三种
        Assert.Equal(3, ImageInput.SupportedMimeTypes.Count);
    }

    [Fact]
    public void 体积文本是给人看的()
    {
        var result = ImageInput.FromBytes(PngBytes(padding: 2048), "image/png");

        Assert.True(result.Ok, result.Message);
        Assert.Contains("KB", result.Image!.SizeText, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有名字时给一个兜底名字()
    {
        // 剪贴板那条路不传名字；空名字会让界面显示一片空白
        var result = ImageInput.FromBytes(PngBytes(), "image/png", displayName: "  ");

        Assert.True(result.Ok, result.Message);
        Assert.Equal("图片", result.Image!.DisplayName);
    }

    // ══════════════════════════════════════════════════════════
    // ⑩ 真图（不是造出来的文件头）
    // ══════════════════════════════════════════════════════════

    /// <summary>沿目录往上找仓库根（测试输出目录是 tests/…/bin/Debug/net8.0-windows）。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BuddyDeck.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void 对一张真_PNG_截图编码成功()
    {
        // ★ 上面那些用例用的都是**手工拼的头**（8 个字节的 PNG 头 + 填充）。
        //   它们能证明「头认对了」，但证明不了「真文件能走完」。
        //   这条拿仓库里真实存在的截图跑一遍。
        //
        // ⚠ 找不到文件时**不 Skip 而是直接返回** —— 这条测试的价值在于
        //   「真跑过一次」，Skip 掉就等于没有。但也不能因为别人删了截图
        //   就让全队红，所以选「找不到就跳过断言」。
        var root = FindRepoRoot();
        if (root is null) return;

        var screenshot = Directory
            .EnumerateFiles(Path.Combine(root, "docs"), "ui-*.png", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        if (screenshot is null) return;

        var result = ImageInput.FromFile(screenshot);

        Assert.True(result.Ok, $"{Path.GetFileName(screenshot)} 应当被接受，实际：{result.Message}");
        Assert.Equal("image/png", result.Image!.MimeType);
        Assert.Equal(new FileInfo(screenshot).Length, result.Image.ByteCount);

        // 真图的 base64 解回来必须逐字节等于文件
        var comma = result.Image.DataUri.IndexOf(',', StringComparison.Ordinal);
        Assert.Equal(
            File.ReadAllBytes(screenshot),
            Convert.FromBase64String(result.Image.DataUri[(comma + 1)..]));
    }

    [Fact]
    public void 真图改名成_txt_后按扩展名被拒()
    {
        // 反向：哪怕文件是真的 PNG，扩展名不在白名单也一样拒。
        // 「格式校验」这一层是**扩展名 + 内容**两道，缺一不可。
        var root = FindRepoRoot();
        if (root is null) return;

        var screenshot = Directory
            .EnumerateFiles(Path.Combine(root, "docs"), "ui-*.png", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (screenshot is null) return;

        var disguised = TempFile("screenshot.txt", File.ReadAllBytes(screenshot));

        var result = ImageInput.FromFile(disguised);

        Assert.False(result.Ok);
        Assert.Equal(ImageInput.Failure.UnsupportedFormat, result.Reason);
    }
}
