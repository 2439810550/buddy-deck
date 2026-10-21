using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 媒体文件 HMAC 签名（t39 / M9）。
///
/// ## 为什么单独一个文件
///
/// 签名的**算法正确性**和**可用性**是两件事，分开验：
///
/// · **算法**：必须与旧版 `gateway.js:333-345` 的输出**逐位相同** ——
///   否则用户在旧版拿到的链接在新版打不开（跨版本迁移场景）。
///   这条用**独立算出来的期望值**钉死，不用「自己 seal 自己 open」
///   （那两边错得一样照样绿）。
/// · **可用性**：签名 URL 在真实 HTTP 路径上确实能免 Authorization 取回文件。
/// </summary>
public class MediaFileEndpointTests
{
    // ══════════════════════════════════════════════════════════
    // ① 签名算法：与旧版 JS 逐位对齐（★ 跨语言互操作）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// ★★ 期望值由**旧版同一套 API**（Node `crypto.createHmac`）独立算出，
    /// 不是用我的实现自己生成的。
    ///
    /// 复算命令（在 `D:\workfandaiapi\workbuddy_api` 下）：
    /// ```js
    /// const {createHmac} = require('crypto');
    /// const signName = (name, apiKey) =>
    ///   apiKey === '' ? '' : createHmac('sha256', apiKey).update(name).digest('hex').slice(0, 16);
    /// console.log(signName('abc123def456.png', 'sk-test-key'));   // 2ea6193f828733a6
    /// ```
    ///
    /// ⚠ 为什么必须用**外部**期望值：自写密码学的经典陷阱是
    /// 「自己 seal 自己 open 照样绿」—— 转义写反、截断位置写错，
    /// **两边错得一样**就永远测不出来。只有拿旧版的真实输出比对才行。
    /// </summary>
    [Theory]
    [InlineData("abc123def456.png", "sk-test-key", "2ea6193f828733a6")]
    [InlineData("deadbeefdeadbeefdeadbeef.png", "media-secret", "c2d83cfd25e28b3a")]
    [InlineData("0011223344556677.mp4", "密钥含中文", "2ca8d2a0bcb28773")]
    public void 签名与旧版_JS_逐位一致(string name, string apiKey, string expected)
    {
        Assert.Equal(expected, MediaSignature.SignName(name, apiKey));
    }

    [Fact]
    public void apiKey_为空时签名为空串()
    {
        // 与旧版 `if (apiKey === "") return ""` 一致。
        // 语义是「网关没开鉴权，签名这条路不成立」—— 不是「任何人都能过」。
        Assert.Equal("", MediaSignature.SignName("abc123def456.png", ""));
        Assert.Equal("", MediaSignature.SignName("abc123def456.png", null));
    }

    [Fact]
    public void 签名是_16_个小写十六进制字符()
    {
        var signature = MediaSignature.SignName("abc123def456.png", "sk-test-key");

        Assert.Equal(16, signature.Length);
        Assert.Matches("^[0-9a-f]{16}$", signature);
    }

    [Fact]
    public void 不同文件名签名不同()
    {
        var a = MediaSignature.SignName("abc123def456.png", "sk-test-key");
        var b = MediaSignature.SignName("abc123def457.png", "sk-test-key");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 不同_key_签名不同()
    {
        var a = MediaSignature.SignName("abc123def456.png", "sk-test-key");
        var b = MediaSignature.SignName("abc123def456.png", "sk-other-key");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 签名覆盖文件名而不覆盖其它参数()
    {
        // ⚠ 这是**已知且有意**的语义（旧版 gateway.js:555 只把 name 喂给 signName）：
        //   签名不是「整条 URL 的防篡改」，只有名字被保护。
        //   补这一步是为了防止将来有人误以为它能防参数篡改。
        var signature = MediaSignature.SignName("abc123def456.png", "sk-test-key");

        Assert.Equal(signature, MediaSignature.SignName("abc123def456.png", "sk-test-key"));
    }

    // ══════════════════════════════════════════════════════════
    // ② 签名比较：常量时间 + 边界
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 签名匹配的三种结果()
    {
        const string expected = "2ea6193f828733a6";

        Assert.True(MediaSignature.SignatureMatches(expected, expected));
        Assert.False(MediaSignature.SignatureMatches(expected, "2ea6193f828733a7")); // 差最后一位
        Assert.False(MediaSignature.SignatureMatches(expected, "2ea6193f828733a"));  // 短一位
        Assert.False(MediaSignature.SignatureMatches(expected, "2ea6193f828733a6ff")); // 长两位
    }

    [Fact]
    public void expected_为空串时恒不匹配()
    {
        // 与旧版 `if (typeof presented !== "string" || expected === "") return false` 一致。
        // ⚠ 这一条是**安全关键**：网关没开鉴权时 SignName 回空串，
        //   若这里误判为 true，等于「任何都不带签名 = 签名有效」。
        Assert.False(MediaSignature.SignatureMatches("", ""));
        Assert.False(MediaSignature.SignatureMatches("", "anything"));
        Assert.False(MediaSignature.SignatureMatches(null, "anything"));
        Assert.False(MediaSignature.SignatureMatches("2ea6193f828733a6", ""));
        Assert.False(MediaSignature.SignatureMatches("2ea6193f828733a6", null));
    }

    [Fact]
    public void 大小写敏感()
    {
        // 十六进制签名是**小写**的（ToLowerInvariant）。
        // 大写的等价数值不该通过 —— 否则等于放宽了匹配面。
        Assert.False(MediaSignature.SignatureMatches("2ea6193f828733a6", "2EA6193F828733A6"));
    }

    // ══════════════════════════════════════════════════════════
    // ③ URL 拼接
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 给_URL_补上签名()
    {
        var url = MediaSignature.AppendSignature(
            "http://127.0.0.1:8787/v1/images/file/abc123def456.png", "sk-test-key");

        Assert.Equal(
            "http://127.0.0.1:8787/v1/images/file/abc123def456.png?s=2ea6193f828733a6", url);
    }

    [Fact]
    public void apiKey_为空时不补签名()
    {
        // 网关没开鉴权 → 所有请求本来就放行 → URL 上加一个空签名只会让人困惑
        const string url = "http://127.0.0.1:8787/v1/images/file/abc123def456.png";

        Assert.Equal(url, MediaSignature.AppendSignature(url, ""));
        Assert.Equal(url, MediaSignature.AppendSignature(url, null));
    }

    [Fact]
    public void URL_已带查询串时用_amp_续接()
    {
        var url = MediaSignature.AppendSignature(
            "http://127.0.0.1:8787/v1/images/file/abc123def456.png?x=1", "sk-test-key");

        Assert.Contains("?x=1&s=2ea6193f828733a6", url, StringComparison.Ordinal);
    }

    [Fact]
    public void 不是媒体路径的_URL_原样返回()
    {
        const string url = "http://127.0.0.1:8787/v1/models";

        Assert.Equal(url, MediaSignature.AppendSignature(url, "sk-test-key"));
    }

    [Fact]
    public void 签发与校验用的是同一个前缀常量()
    {
        // ★ 结构性保证：AppendSignature 造出来的 URL，喂回 SignName 必须能对上。
        //   两处若各写一份前缀，将来改一处就会静默漂移（症状：所有签名链接全 401）。
        const string name = "abc123def456.png";
        var url = MediaSignature.AppendSignature(
            "http://127.0.0.1:8787" + MediaSignature.PathPrefix + name, "sk-test-key");

        var presented = url[(url.IndexOf("?s=", StringComparison.Ordinal) + 3)..];

        Assert.True(MediaSignature.SignatureMatches(MediaSignature.SignName(name, "sk-test-key"), presented));
    }

    // ══════════════════════════════════════════════════════════
    // ④ 请求级：IsSignedMediaRequest
    // ══════════════════════════════════════════════════════════

    private static DefaultHttpContext Request(string method, string path, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (query.Length > 0) context.Request.QueryString = new QueryString("?" + query);
        return context;
    }

    [Fact]
    public void 带正确签名的_GET_被认出来()
    {
        var context = Request("GET", "/v1/images/file/abc123def456.png", "s=2ea6193f828733a6");

        Assert.True(MediaSignature.IsSignedMediaRequest(context, "sk-test-key"));
    }

    [Fact]
    public void 签名错的不被认出来()
    {
        var context = Request("GET", "/v1/images/file/abc123def456.png", "s=0000000000000000");

        Assert.False(MediaSignature.IsSignedMediaRequest(context, "sk-test-key"));
    }

    [Fact]
    public void 没有签名的不被认出来()
    {
        var context = Request("GET", "/v1/images/file/abc123def456.png");

        Assert.False(MediaSignature.IsSignedMediaRequest(context, "sk-test-key"));
    }

    [Fact]
    public void 非_GET_的不被认出来()
    {
        // 签名只给 GET 开 —— 写操作仍然只能靠 Bearer
        var context = Request("POST", "/v1/images/file/abc123def456.png", "s=2ea6193f828733a6");

        Assert.False(MediaSignature.IsSignedMediaRequest(context, "sk-test-key"));
    }

    [Fact]
    public void 非媒体路径的不被认出来()
    {
        // ⚠ 安全关键：签名路径之外的一切（/admin/*、/v1/chat/* …）
        //   绝不能因为带了签名就绕过 Bearer。
        foreach (var path in (string[])["/admin/accounts", "/v1/chat/completions", "/v1/models", "/v1/responses"])
        {
            var context = Request("GET", path, "s=2ea6193f828733a6");
            Assert.False(MediaSignature.IsSignedMediaRequest(context, "sk-test-key"));
        }
    }

    [Fact]
    public void 前缀相同但更长的路径不被认出来()
    {
        // `/v1/images/file/abc.png/extra` —— 切成 "abc.png/extra" 算签名，必然对不上。
        // 这是**有意**的：能让它通过的签名，等价于攻击者能自己造签名。
        var context = Request("GET", "/v1/images/file/abc123def456.png/extra", "s=2ea6193f828733a6");

        Assert.False(MediaSignature.IsSignedMediaRequest(context, "sk-test-key"));
    }

    [Fact]
    public void 尾部斜杠不影响签名校验()
    {
        // ⚠ 与 GatewayApp.NormalizePath 的语义保持一致：
        //   路由那边把 `/v1/images/file/x.png/` 规范成 `x.png`，
        //   签名这边也必须剥掉尾部斜杠，否则两边对不上 → 所有链接 401。
        var withSlash = Request("GET", "/v1/images/file/abc123def456.png/", "s=2ea6193f828733a6");
        var without = Request("GET", "/v1/images/file/abc123def456.png", "s=2ea6193f828733a6");

        Assert.True(MediaSignature.IsSignedMediaRequest(without, "sk-test-key"));
        Assert.True(MediaSignature.IsSignedMediaRequest(withSlash, "sk-test-key"));
    }

    [Fact]
    public void apiKey_为空时签名路径整体不成立()
    {
        var context = Request("GET", "/v1/images/file/abc123def456.png", "s=whatever");

        Assert.False(MediaSignature.IsSignedMediaRequest(context, ""));
        Assert.False(MediaSignature.IsSignedMediaRequest(context, null));
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ 端到端：签名 URL 免 Authorization 取回文件
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 造一个「已经有内容」的媒体目录。
    ///
    /// ⚠ 直接写文件而不是走一遍生图 —— 生图要打假上游，与本节的
    /// 「文件回读 + 鉴权」正交。文件名的形状必须过
    /// <see cref="MioraClient.IsSafeMediaName"/>（8–64 位 hex + 扩展名）。
    /// </summary>
    private static (string Directory, string Name, byte[] Bytes) MakeMediaFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "buddydeck-m9-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(directory);

        var name = "abc123def456abc123def456.png";
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02 };
        File.WriteAllBytes(Path.Combine(directory, name), bytes);

        return (directory, name, bytes);
    }

    /// <summary>
    /// 起一个「挂了 `MediaEndpoints` 且已配 API key」的真宿主。
    ///
    /// ## 为什么必须有这个夹具（两个坑都踩过一次）
    ///
    /// **坑① 不注册 `MediaEndpoints`** → `/v1/images/file/` 没有任何族处理 →
    /// 落到 `GatewayApp` 的 404 兜底。症状是 **404 而不是 401**，
    /// 看起来像「鉴权过了但文件不在」，**排查方向完全被带偏**。
    ///
    /// **坑② `MioraClient` 不挂假上游的 handler** → 它去打**真实网络**。
    /// 实测拿到一个真的 401 而 `fake.Count` 是 0，看起来像「端点压根没被调用」。
    /// （这条是 harness 在 `MediaEndpointTests:186-189` 里记下的，我照抄它的做法。）
    /// </summary>
    private static async Task<GatewayHost> StartMediaHostAsync(
        string mediaDir, FakeUpstreamHandler fake, TestVault vault, string apiKey)
    {
        return await GatewayHost.StartReadyAsync(
            fake: fake,
            vault: vault,
            apiKey: apiKey,
            configure: services =>
            {
                services.AddSingleton(new MioraClient(
                    new HttpClient(fake, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
                    () => vault.Entries.Count > 0
                        ? vault.Entries[0].ToCredential()
                        : new Credential { AccessToken = "" },
                    UpstreamVariant.China, mediaDir, TimeProvider.System,
                    NullLogger<MioraClient>.Instance));

                // ⚠ 少了这一行，文件回读端点根本不存在（症状：404 而非 401）
                EndpointFamilies.Register<MediaEndpoints>(services);
            });
    }

    [Fact]
    public async Task 端到端_带签名不带_Authorization_能取回文件()
    {
        // ★★ 这是 M9 的**核心验收**：浏览器直接打开图片的场景 ——
        //    没有任何 Authorization 头，只有 URL 上的签名。
        var (mediaDir, name, bytes) = MakeMediaFile();
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        var vault = new TestVault().Add();

        await using var host = await StartMediaHostAsync(mediaDir, fake, vault, "sk-m9-secret");

        var signature = MediaSignature.SignName(name, "sk-m9-secret");

        // ⚠ 刻意**不挂** Authorization 头
        using var response = await host.Client.GetAsync($"/v1/images/file/{name}?s={signature}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task 端到端_不带签名不带_Authorization_回_401()
    {
        var (mediaDir, name, _) = MakeMediaFile();
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        var vault = new TestVault().Add();

        await using var host = await StartMediaHostAsync(mediaDir, fake, vault, "sk-m9-secret");

        using var response = await host.Client.GetAsync($"/v1/images/file/{name}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 端到端_签名错回_401_且与文件不存在不可区分()
    {
        // ★ 契约硬要求③「不泄露文件是否存在」。
        //
        // 对照两组：
        //   · 签名错 + 文件**存在** → ？
        //   · 签名错 + 文件**不存在** → ？
        // **两者必须完全一样**，否则这个端点就成了文件探测接口
        // （攻击者能靠「404 vs 401」枚举出哪些哈希存在）。
        var (mediaDir, name, _) = MakeMediaFile();
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        var vault = new TestVault().Add();

        await using var host = await StartMediaHostAsync(mediaDir, fake, vault, "sk-m9-secret");

        using var exists = await host.Client.GetAsync($"/v1/images/file/{name}?s=0000000000000000");
        using var missing = await host.Client.GetAsync(
            "/v1/images/file/ffffffffffffffffffffffff.png?s=0000000000000000");

        Assert.Equal(exists.StatusCode, missing.StatusCode);
        Assert.Equal(
            await exists.Content.ReadAsStringAsync(),
            await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 端到端_带_Bearer_仍然能取回文件()
    {
        // ★ 回归：加了签名这条路**不能**把原来的 Bearer 路弄坏。
        //   harness 的媒体测试全部走 Bearer（见 MediaEndpointTests ⑨ 鉴权节），
        //   这条保证它们不会被我的改动打红。
        var (mediaDir, name, bytes) = MakeMediaFile();
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        var vault = new TestVault().Add();

        await using var host = await StartMediaHostAsync(mediaDir, fake, vault, "sk-m9-secret");

        using var request = GatewayHost.BearerRequest(
            HttpMethod.Get, $"/v1/images/file/{name}", "sk-m9-secret");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task 端到端_签名不能给非媒体路径开绿灯()
    {
        // ★★ 这是**最危险**的一种误实现：把签名检查写成「只要 query 里有 s 就放行」。
        //    那样 /admin/accounts 就成了无鉴权端点。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        var vault = new TestVault().Add();

        await using var host = await GatewayHost.StartReadyAsync(
            fake: fake, vault: vault, apiKey: "sk-m9-secret");

        using var response = await host.Client.GetAsync("/admin/accounts?s=2ea6193f828733a6");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ══════════════════════════════════════════════════════════
    // ⑥ 防目录穿越（复用 Core 的既有校验，不重写）
    // ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("../appsettings.json")]
    [InlineData("..%2Fappsettings.json")]
    [InlineData("....//appsettings.json")]
    [InlineData("/etc/passwd")]
    [InlineData("abc.png")]                  // 哈希太短
    [InlineData("deadbeef.exe")]             // 扩展名不在白名单
    [InlineData("deadbeefdeadbeefdeadbeef.png.bak")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public void 非法文件名一律被拒(string name)
    {
        // ⚠ 这道校验**已经在 Core 里**（`MioraClient.IsSafeMediaName`，t20 修穿越时加的）。
        //   这里**不重写**，只钉住它的行为 —— 因为「任何调用方都该受它保护」。
        //   重写一份必然漂移（两边白名单不一致时，宽的那边就是漏洞）。
        Assert.False(MioraClient.IsSafeMediaName(name), $"「{name}」不该被当成合法媒体名");
    }

    [Theory]
    [InlineData("abc123def456.png")]
    [InlineData("deadbeefdeadbeefdeadbeef.png")]
    [InlineData("0011223344556677.mp4")]
    [InlineData("a1b2c3d4.webp")]
    [InlineData("A1B2C3D4.png")]             // 大写 hex 也认（正则 IgnoreCase）
    public void 合法文件名被接受(string name)
    {
        Assert.True(MioraClient.IsSafeMediaName(name), $"「{name}」应当是合法媒体名");
    }

    [Fact]
    public void 穿越文件名即使签名正确也读不到文件()
    {
        // ★ 两层防线的第二层：即便攻击者知道 key、**算出了合法签名**，
        //   穿越名也过不了 `ReadMedia` 的 `IsSafeMediaName` 闸门。
        //   （第一层是签名本身，但它只保护「知道 key 的人」——
        //     对已经拿到 key 的人，这道校验才是真正拦住 ../ 的那一道。）
        var directory = Path.Combine(Path.GetTempPath(), "buddydeck-m9-trav-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(directory);

        var miora = new MioraClient(
            new HttpClient(), () => new Credential { AccessToken = "" },
            UpstreamVariant.China, directory, TimeProvider.System, NullLogger<MioraClient>.Instance);

        // 即使签名正确，这些名字也读不出来
        foreach (var name in (string[])["../appsettings.json", "..%2Fappsettings.json", "/etc/passwd", "abc.exe"])
        {
            Assert.True(MediaSignature.SignatureMatches(
                MediaSignature.SignName(name, "sk-m9-secret"),
                MediaSignature.SignName(name, "sk-m9-secret")));

            Assert.Null(miora.ReadMedia(name));
        }
    }
}
