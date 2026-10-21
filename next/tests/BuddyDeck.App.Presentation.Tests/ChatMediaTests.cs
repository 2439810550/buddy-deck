using BuddyDeck.App.Services;
using Xunit;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// 聊天媒体的**纯逻辑**测试（t62）。
///
/// ## 为什么测 Services 而不是 ViewModel
///
/// `ChatViewModel` 用了 `[ObservableProperty]` / `[RelayCommand]`，依赖
/// CommunityToolkit 的**源生成器在编译期展开**。而源生成器**不会为
/// `Compile Include` 链接进测试程序集的文件跑**（那属于另一个程序集的编译），
/// 结果是 CS0246 + CS0759 —— **机制性的，加包无效**。
///
/// 所以按本项目既有约定（同 `AccountPresentation` / `QuotaMath` / `TimeText`）：
/// **纯逻辑进 `Services/`，ViewModel 只留绑定 glue。**
///
/// ## 这组测试守的是什么
///
/// 用户需求是「生图/生视频能在回复框里展示」。实现过程中有两个
/// **会静默出错**的地方，编译期都不报：
///
///   ① **媒体 URL 来自网络** —— 拿它发请求等于让远端决定我们连哪里
///      （与 t52 的 `action_button.Action` 同一条道理）
///   ② **一张巨图能卡死界面** —— 没有字节上限时，500 MB 的响应会打爆内存，
///      而那看起来像"界面卡了"
///
/// 重点不是「媒体能不能显示」（那要看截图），而是**安全边界与尺寸约定的不变量**。
/// </summary>
public class ChatMediaTests
{
    // ===================== MediaUrlGuard：白名单 =====================

    [Fact]
    public void 放行_网关自身的媒体地址()
    {
        // 网关实际生成的形状（MediaEndpoints.cs:292–296）
        Assert.True(MediaUrlGuard.IsAllowedMediaUrl("http://127.0.0.1:8790/v1/images/file/a.png", 8790));
        Assert.True(MediaUrlGuard.IsAllowedMediaUrl("http://localhost:8790/v1/images/file/a.png", 8790));
    }

    [Theory]
    [InlineData("https://evil.example.com/a.png")]        // 外部 host
    [InlineData("http://attacker.test/x.png")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]  // 非 http scheme
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]                                      // 畸形
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/v1/images/file/a.png")]                 // 相对路径
    [InlineData("//evil.com/a.png")]                      // 协议相对
    public void 拒绝_外部或畸形地址(string url)
    {
        Assert.False(MediaUrlGuard.IsAllowedMediaUrl(url, 8790), $"应当拒绝：{url}");
    }

    /// <summary>
    /// ⚠ 这条是本组最重要的安全断言。
    ///
    /// 只判「回环 host」是不够的 —— **本机上别的服务也监听 127.0.0.1**。
    /// 白名单只到 host 一级的话，`http://127.0.0.1:8080/随便什么` 也能过，
    /// 等于允许上游让我们去戳本机任意服务（SSRF 的本地版）。
    ///
    /// 所以必须连**端口**一起卡死为当前网关端口。
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:8080/a.png")]
    [InlineData("http://127.0.0.1:22/x")]
    [InlineData("http://localhost:3000/x.png")]
    public void 拒绝_回环但非网关端口的地址(string url)
    {
        Assert.False(MediaUrlGuard.IsAllowedMediaUrl(url, 8790),
            $"回环但不是网关端口，必须拒绝（否则能戳本机任意服务）：{url}");
    }

    [Fact]
    public void 拒绝说明不回显完整URL()
    {
        // URL 可能带查询串（签名）。host 可以说（定位需要），查询串不回显。
        var desc = MediaUrlGuard.DescribeRejection("https://evil.example.com/x?token=SECRET", 8790);

        Assert.Contains("evil.example.com", desc);
        Assert.DoesNotContain("SECRET", desc);
    }

    // ===================== 上限与超时的常量约定 =====================

    [Fact]
    public void 字节上限是有限值()
    {
        // 万一有人改成 long.MaxValue（等于没上限），这条会红
        Assert.True(MediaUrlGuard.MaxBytes > 0);
        Assert.True(MediaUrlGuard.MaxBytes <= 64L * 1024 * 1024,
            "上限不该超过 64MB —— 再大就有卡死界面的风险");
    }

    [Fact]
    public void 超时是有限值()
    {
        Assert.True(MediaUrlGuard.Timeout > TimeSpan.Zero);
        Assert.True(MediaUrlGuard.Timeout <= TimeSpan.FromMinutes(2),
            "媒体超时不该超过 2 分钟");
    }

    // ===================== 媒体块：占位尺寸固定 =====================

    /// <summary>
    /// 「布局不跳」的实现方式就是这组数值**恒定**。
    /// 如果哪天有人让它按图片实际像素尺寸变，这条会红 —— 那正是会跳的写法。
    /// </summary>
    [Fact]
    public void 占位尺寸是常量且与状态无关()
    {
        var img = new ImageBlock("http://127.0.0.1:8790/v1/images/file/a.png");
        var vid = new VideoBlock("http://127.0.0.1:8790/v1/video/x.mp4", "/tmp/x.mp4");

        // 图片块与视频块共用同一组尺寸 —— 列表里两者宽度一致
        Assert.True(img.PlaceholderWidth > 0);
        Assert.True(img.PlaceholderHeight > 0);
        Assert.Equal(img.PlaceholderWidth, vid.PlaceholderWidth);
        Assert.Equal(img.PlaceholderHeight, vid.PlaceholderHeight);

        // 状态变化不改变尺寸
        img.MarkReady("10 KB");
        Assert.Equal(MediaBlock.PlaceholderWidthValue, img.PlaceholderWidth);
    }

    /// <summary>
    /// ⚠ 这条是回归测试，守的是一个**真实踩过的坑**。
    ///
    /// 最初 `PlaceholderWidth` 是 `const`，XAML 用 `{Binding PlaceholderWidth}` 绑它 ——
    /// **WPF 绑定读不到 `const` 字段**（反射查的是属性，const 在 IL 里是字面量）。
    /// 于是绑定**静默失败**，`MaxWidth` 保持默认的 `PositiveInfinity`，
    /// 图片按原始像素撑满整行（实测 1087px，而常量写的是 360）。
    ///
    /// **表征看起来完全像布局问题**，我为此改了两轮 XAML（换 Button、加 ClipToBounds）
    /// 都没用 —— 根因是绑定没生效。
    ///
    /// 所以：尺寸必须是**可读的属性**（实例属性），不能是 const 字段。
    /// 这条断言用反射确认它确实是属性。
    /// </summary>
    [Fact]
    public void 占位尺寸必须是属性而不是常量_否则XAML绑定会静默失败()
    {
        var t = typeof(MediaBlock);

        var w = t.GetProperty(nameof(MediaBlock.PlaceholderWidth));
        var h = t.GetProperty(nameof(MediaBlock.PlaceholderHeight));

        Assert.NotNull(w);
        Assert.NotNull(h);
        Assert.Equal(typeof(double), w!.PropertyType);
        Assert.Equal(typeof(double), h!.PropertyType);

        // 而且真的能取到值（不是抛异常的那种）
        var block = new ImageBlock("http://127.0.0.1:8790/x.png");
        Assert.Equal(MediaBlock.PlaceholderWidthValue, block.PlaceholderWidth);
    }

    [Fact]
    public void 媒体块初始状态是加载中()
    {
        var img = new ImageBlock("http://127.0.0.1:8790/x.png");

        Assert.True(img.IsLoading);
        Assert.False(img.IsReady);
        Assert.False(img.HasError);
        Assert.Null(img.Error);
    }

    [Fact]
    public void 成功标记_转为就绪态()
    {
        var img = new ImageBlock("http://127.0.0.1:8790/x.png");
        img.MarkReady("123 KB");

        Assert.False(img.IsLoading);
        Assert.True(img.IsReady);
        Assert.False(img.HasError);
        Assert.Equal("123 KB", img.SizeText);
    }

    [Fact]
    public void 失败标记_转为错误态()
    {
        var img = new ImageBlock("http://127.0.0.1:8790/x.png");
        img.MarkFailed("取不到这个媒体。");

        Assert.False(img.IsLoading);
        Assert.False(img.IsReady);
        Assert.True(img.HasError);
        Assert.Equal("取不到这个媒体。", img.Error);
    }

    /// <summary>成功与失败互斥 —— 不该出现"又能显示又有错误"的状态。</summary>
    [Fact]
    public void 成功与失败互斥()
    {
        var ok = new ImageBlock("http://127.0.0.1:8790/a.png");
        ok.MarkReady("1 KB");
        Assert.True(ok.IsReady);
        Assert.False(ok.HasError);

        var bad = new ImageBlock("http://127.0.0.1:8790/b.png");
        bad.MarkFailed("坏了");
        Assert.False(bad.IsReady);
        Assert.True(bad.HasError);
    }

    // ===================== MIME 分类 =====================

    [Fact]
    public void 媒体类型由_MIME_决定()
    {
        var png = new MediaBytes([1, 2, 3], "image/png");
        var mp4 = new MediaBytes([1, 2, 3], "video/mp4");

        Assert.True(png.IsImage);
        Assert.False(png.IsVideo);
        Assert.True(mp4.IsVideo);
        Assert.False(mp4.IsImage);
    }

    [Fact]
    public void MIME_大小写不敏感()
    {
        Assert.True(new MediaBytes([], "IMAGE/PNG").IsImage);
        Assert.True(new MediaBytes([], "Video/MP4").IsVideo);
    }

    /// <summary>
    /// 别把任何东西都当图片塞给解码器 —— 那会抛 NotSupportedException。
    /// 上游出错时回的 JSON 错误体正好会走到这条路上。
    /// </summary>
    [Fact]
    public void 未知_MIME_既不算图片也不算视频()
    {
        var json = new MediaBytes([123, 34], "application/json");

        Assert.False(json.IsImage);
        Assert.False(json.IsVideo);
    }

    // ===================== 视频/图片的判定 =====================

    [Theory]
    [InlineData("http://127.0.0.1:8790/v1/video/x.mp4", true)]
    [InlineData("http://127.0.0.1:8790/v1/videos/abc", true)]
    [InlineData("http://127.0.0.1:8790/x.webm", true)]
    [InlineData("http://127.0.0.1:8790/x.mov", true)]
    [InlineData("http://127.0.0.1:8790/v1/images/file/a.png", false)]
    [InlineData("http://127.0.0.1:8790/v1/images/file/b.jpg", false)]
    public void 按URL粗判视频(string url, bool expected)
    {
        Assert.Equal(expected, ChatMedia.LooksLikeVideo(url));
    }

    [Fact]
    public void 有_MIME_时以_MIME_为准而不是_URL_后缀()
    {
        // URL 像图片，但 MIME 说是视频 —— 要以 MIME 为准。
        // 这正是「URL 后缀只能粗判」的原因。
        var block = ChatMedia.CreateBlock(
            "http://127.0.0.1:8790/v1/images/file/a.png",
            new MediaBytes([], "video/mp4"));

        Assert.IsType<VideoBlock>(block);
    }

    [Fact]
    public void 没有_MIME_时按_URL_粗判()
    {
        Assert.IsType<VideoBlock>(ChatMedia.CreateBlock("http://127.0.0.1:8790/v1/video/x.mp4", null));
        Assert.IsType<ImageBlock>(ChatMedia.CreateBlock("http://127.0.0.1:8790/v1/images/file/x.png", null));
    }

    // ===================== URL 识别：宽松识别 + 严格校验 =====================

    [Fact]
    public void 识别_从文本里捞出网关媒体_URL()
    {
        Assert.True(ChatMedia.TryExtractMediaUrl(
            "好的，这是：http://127.0.0.1:8790/v1/images/file/x.png", 8790, out var url));
        Assert.Equal("http://127.0.0.1:8790/v1/images/file/x.png", url);
    }

    [Fact]
    public void 识别_末尾标点不算_URL_的一部分()
    {
        Assert.True(ChatMedia.TryExtractMediaUrl(
            "看这个 http://127.0.0.1:8790/v1/images/file/x.png。", 8790, out var url));

        Assert.EndsWith(".png", url);
        Assert.DoesNotContain("。", url);
    }

    /// <summary>
    /// **宽松识别 + 严格校验**：识别到 https://evil 也没关系，校验会挡。
    /// 这条同时证明"识别"与"校验"是两层 —— 少一层都会出问题。
    /// </summary>
    [Fact]
    public void 识别_外部地址不认()
    {
        Assert.False(ChatMedia.TryExtractMediaUrl("https://evil.example.com/x.png", 8790, out _));
    }

    [Fact]
    public void 识别_纯文本或无输入不认()
    {
        Assert.False(ChatMedia.TryExtractMediaUrl("这是一段普通回复，没有媒体。", 8790, out _));
        Assert.False(ChatMedia.TryExtractMediaUrl("", 8790, out _));
        Assert.False(ChatMedia.TryExtractMediaUrl(null, 8790, out _));
    }

    // ===================== 内容块流：不是字符串拼接 =====================

    [Fact]
    public void 纯文本投影_忽略媒体块()
    {
        // 媒体块不是文字 —— 拼文本时不该出现它的 URL
        var blocks = new ChatBlock[]
        {
            new TextBlock0("这是"),
            new ImageBlock("http://127.0.0.1:8790/x.png"),
            new TextBlock0("一张图"),
        };

        var text = ChatMedia.ToPlainText(blocks);

        Assert.Equal("这是一张图", text);
        Assert.DoesNotContain("http", text);   // URL 不该混进正文
    }

    [Fact]
    public void 流的顺序有意义_文本与媒体交错()
    {
        // 顺序是「文字 → 图 → 文字」；投影只拿文字，但块本身的顺序保持
        var blocks = new ChatBlock[]
        {
            new TextBlock0("前"),
            new ImageBlock("http://127.0.0.1:8790/x.png"),
            new TextBlock0("后"),
        };

        Assert.Equal(3, blocks.Length);
        Assert.IsType<TextBlock0>(blocks[0]);
        Assert.IsType<ImageBlock>(blocks[1]);
        Assert.IsType<TextBlock0>(blocks[2]);
        Assert.Equal("前后", ChatMedia.ToPlainText(blocks));
    }

    [Fact]
    public void 刷新文本块_空缓冲不产生块()
    {
        var pending = new System.Text.StringBuilder();
        Assert.Null(ChatMedia.FlushText(pending));

        pending.Append("有内容");
        var block = ChatMedia.FlushText(pending);

        Assert.NotNull(block);
        Assert.Equal("有内容", block!.Text);
        // 刷完要清空，否则下次调用会重复产生同一段文字
        Assert.Equal(0, pending.Length);
    }

    // ===================== 大小格式化 =====================

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1024 * 1024 * 3 / 2, "1.5 MB")]
    public void 大小格式化可读(int bytes, string expected)
    {
        Assert.Equal(expected, ChatMedia.FormatSize(bytes));
    }
}
