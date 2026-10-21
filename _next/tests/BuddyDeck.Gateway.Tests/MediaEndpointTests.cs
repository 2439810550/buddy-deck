using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.Gateway;
using BuddyDeck.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 媒体与联网能力的测试：生图 / 改图 / 生视频 / 图生视频 / 搜索 / 抓取。
///
/// ## 全部走假上游
///
/// 上游调用一律走 <see cref="FakeUpstreamHandler"/> —— **不打真实 Miora**。
/// 这不只是习惯问题：真打一次会**真的生成一张图并扣费**，而且签名 URL
/// 1 小时就失效，测试根本没法稳定重复。
///
/// ## 上游形状从哪来
///
/// 全部对照旧版 `workbuddy_api/src/miora.js` 与 `search.js` 的实测形状
/// （`data.upstreamTaskId` / `data.mode=="sync"` / 轮询的
/// `{taskId: {...}}` 映射 / 搜索的**扁平**返回）。这些形状是移植的依据，
/// 写死在这里也让「上游变了」时能一眼看出该改哪。
/// </summary>
public class MediaEndpointTests
{
    // ══════════════════════════════════════════════════════════
    // 上游响应工厂
    // ══════════════════════════════════════════════════════════

    /// <summary>换 tempKey 的成功响应。</summary>
    private static HttpResponseMessage TempKeyOk(string key = "ck_t_test_key")
        => UpstreamResponses.Text(new JsonObject
        {
            ["code"] = 0,
            ["data"] = new JsonObject
            {
                ["key"] = key,
                ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(15).ToString("O"),
            },
        }.ToJsonString());

    /// <summary>
    /// 生图**同步**返回（旧版实测生图通常是 `mode: "sync"`）。
    /// </summary>
    private static HttpResponseMessage ImageSyncOk(string signedUrl = "https://cos.example.com/signed/abc.png")
        => UpstreamResponses.Text(new JsonObject
        {
            ["code"] = 0,
            ["data"] = new JsonObject
            {
                ["upstreamTaskId"] = "task-img-1",
                ["mode"] = "sync",
                ["status"] = "completed",
                ["model"] = "miora-image-v1",
                ["resultFiles"] = new JsonArray(new JsonObject
                {
                    ["signedUrl"] = signedUrl,
                    ["mimeType"] = "image/png",
                    ["fileKey"] = "bga/xyz",
                }),
            },
        }.ToJsonString());

    /// <summary>生图**异步**提交（只给任务 id）。</summary>
    private static HttpResponseMessage SubmitAsync(string taskId = "task-vid-1", string kind = "video")
        => UpstreamResponses.Text(new JsonObject
        {
            ["code"] = 0,
            ["data"] = new JsonObject
            {
                ["upstreamTaskId"] = taskId,
                ["status"] = "pending",
                ["mode"] = "async",
                ["model"] = kind == "video" ? "miora-video-v1" : "miora-image-v1",
            },
        }.ToJsonString());

    /// <summary>轮询返回（`{taskId: {...}}` 映射形状，这是 query-task 的实测形状）。</summary>
    private static HttpResponseMessage QueryResult(
        string taskId,
        string status,
        string? signedUrl = null,
        string? error = null)
    {
        var task = new JsonObject
        {
            ["status"] = status,
            ["model"] = "miora-video-v1",
        };

        if (signedUrl is not null)
        {
            task["resultFiles"] = new JsonArray(new JsonObject
            {
                ["signedUrl"] = signedUrl,
                ["mimeType"] = "video/mp4",
            });
        }

        if (error is not null) task["errorMessage"] = error;

        return UpstreamResponses.Text(new JsonObject
        {
            ["code"] = 0,
            ["data"] = new JsonObject { [taskId] = task },
        }.ToJsonString());
    }

    /// <summary>一张 1x1 PNG 的字节 —— 用来当「下载回来的图」。</summary>
    private static readonly byte[] TinyPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
    ];

    /// <summary>
    /// 下载产物用的响应（图片/视频二进制）。
    ///
    /// ⚠ 用**纯 ASCII 字节**当内容，不走 UpstreamResponses.Text ——
    /// 那个会把字符串按 UTF-8 编码，非 ASCII 字节（0x89 / 0x8A 等）
    /// 会被扩成两字节，于是「下载回来的字节数」和源串对不上（实测踩到）。
    /// 这里直接给 CSV 风格的 ASCII 内容，长度可预期、断言干净。
    /// </summary>
    private static HttpResponseMessage Binary(string contentType = "image/png", string? asciiContent = null)
    {
        var bytes = Encoding.ASCII.GetBytes(asciiContent ?? "PNG-ASCII-PAYLOAD-0123456789");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        return response;
    }

    // ══════════════════════════════════════════════════════════
    // 宿主夹具
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 一个挂了媒体/联网端点族 + MediaToolInjector 的真网关。
    ///
    /// 用共享的 <see cref="GatewayHost"/>（端口 0、轮询 /healthz，零睡眠）。
    /// </summary>
    private sealed class MediaFixture : IAsyncDisposable
    {
        private readonly GatewayHost _host;

        private MediaFixture(GatewayHost host, MioraClient miora, string mediaDir, System.Collections.Concurrent.ConcurrentQueue<string> injected)
        {
            _host = host;
            Miora = miora;
            MediaDirectory = mediaDir;
            InjectedTools = injected;
        }

        public MioraClient Miora { get; }
        public string MediaDirectory { get; }
        public HttpClient Client => _host.Client;

        /// <summary>每次聊天请求注入的工具名（用来断言「注入真的发生了」）。</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> InjectedTools { get; }

        public static async Task<MediaFixture> StartAsync(
            FakeUpstreamHandler fake,
            TestVault? vault = null,
            bool mediaToolsEnabled = true,
            CancellationToken ct = default)
        {
            var vaultValue = vault ?? new TestVault().Add();
            var injected = new System.Collections.Concurrent.ConcurrentQueue<string>();

            // 媒体目录用临时目录 —— 不污染仓库，也不会和别的测试互相看到文件
            var mediaDir = Path.Combine(Path.GetTempPath(), "buddydeck-media-test-" + Guid.NewGuid().ToString("N")[..8]);

            var host = await GatewayHost.StartReadyAsync(
                fake: fake,
                vault: vaultValue,
                configure: services =>
                {
                    var miora = new MioraClient(
                        // ⚠⚠ 必须挂假上游的 handler —— 不挂的话 HttpClient 会去
                        //   **打真实网络**。实测表现极具误导性：拿到一个真的 401，
                        //   而 `fake.Count` 是 0，看起来像「端点压根没被调用」，
                        //   排查方向完全被带偏。这个坑值得留在这里。
                        new HttpClient(fake, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
                        () => vaultValue.Entries.Count > 0
                            ? vaultValue.Entries[0].ToCredential()
                            : new Credential { AccessToken = "" },
                        UpstreamVariant.China,
                        mediaDir,
                        TimeProvider.System,
                        NullLogger<MioraClient>.Instance);

                    var search = new SearchClient(
                        new HttpClient(fake, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
                        () => vaultValue.Entries.Count > 0
                            ? vaultValue.Entries[0].ToCredential()
                            : new Credential { AccessToken = "" },
                        UpstreamVariant.China,
                        NullLogger<SearchClient>.Instance);

                    services.AddSingleton(miora);
                    services.AddSingleton(search);

                    // 记录注入的工具名 —— 用一个包装过的 injector（子类不方便，
                    // 因为 InjectInto 不是 virtual；所以这里自己数一遍）
                    services.AddSingleton(sp => new MediaToolInjector(
                        NullLogger<MediaToolInjector>.Instance, enabled: mediaToolsEnabled));

                    services.AddSingleton(sp => new MediaToolExecutor(
                        miora, NullLogger<MediaToolExecutor>.Instance));

                    EndpointFamilies.Register<MediaEndpoints>(services);
                    EndpointFamilies.Register<SearchEndpoints>(services);

                    // ⚠ 用工厂替换 GatewayHost 里那个不带媒体注入器的 GatewayApp。
                    //   宿主先注册、这里后注册 —— DI 解析单例时取最后一条。
                    services.AddSingleton(sp => new GatewayApp(
                        sp.GetRequiredService<GatewayOptions>(),
                        sp.GetRequiredService<UpstreamVariant>(),
                        sp.GetRequiredService<ModelCatalog>(),
                        sp.GetRequiredService<BuildInfo>(),
                        sp.GetRequiredService<UpstreamClient>(),
                        sp.GetRequiredService<AccountPool>(),
                        sp.GetRequiredService<ICredentialSource>(),
                        sp.GetRequiredService<ChatObservers>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GatewayApp>>(),
                        mediaTools: sp.GetRequiredService<MediaToolInjector>(),
                        families: EndpointFamilies.Resolve(sp)));
                },
                ct: ct).ConfigureAwait(false);

            var resolved = host.Application.Services.GetRequiredService<MioraClient>();
            return new MediaFixture(host, resolved, mediaDir, injected);
        }

        public async Task<HttpResponseMessage> PostJsonAsync(string path, JsonObject body)
            => await Client.PostAsync(path,
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")).ConfigureAwait(false);

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(MediaDirectory)) Directory.Delete(MediaDirectory, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不该让测试红
            }

            return _host.DisposeAsync();
        }
    }

    // ══════════════════════════════════════════════════════════
    // ① 生图：tempKey → 提交 → 拿到结果 → 落盘
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 生图_同步模式_换密钥后拿结果并落盘()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            TempKeyOk(),                 // ① 换 tempKey
            ImageSyncOk(),               // ② 提交（sync 直接带结果）
            Binary("image/png"));        // ③ 下载产物

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/images/generations",
            new JsonObject { ["prompt"] = "一只戴墨镜的橘猫" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var data = body["data"]!.AsArray();

        Assert.Single(data);

        // ★ 默认交付：网关自己的 URL（不是上游那个 1 小时失效的签名 URL）
        var url = data[0]!["url"]!.GetValue<string>();
        Assert.StartsWith("http://127.0.0.1:", url, StringComparison.Ordinal);
        Assert.Contains("/v1/images/file/", url, StringComparison.Ordinal);
        Assert.DoesNotContain("cos.example.com", url, StringComparison.Ordinal);

        // ★ 文件真的落盘了，而且名字是内容哈希
        var fileName = url[(url.LastIndexOf('/') + 1)..];
        Assert.True(MioraClient.IsSafeMediaName(fileName), $"文件名应是内容哈希：{fileName}");
        Assert.True(File.Exists(Path.Combine(fx.MediaDirectory, fileName)), "产物应该落盘");

        // ★ 上游被打了三次：换 key、提交、下载
        Assert.Equal(3, fake.Count);
        Assert.Equal("/v2/api-keys", fake.Requests[0].Path);
        Assert.Contains("/api/ai/workbuddy-proxy/image/generate/submit", fake.Requests[1].Url);
    }

    [Fact]
    public async Task 生图_deliver_url_直接转发上游签名_URL()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), ImageSyncOk("https://cos.example.com/signed/abc.png"));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/images/generations",
            new JsonObject { ["prompt"] = "风景", ["deliver"] = "url" });

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var url = body["data"]!.AsArray()[0]!["url"]!.GetValue<string>();

        // ★ 显式要求直转时给的是上游 URL（省一次下载，但 1 小时会过期）
        Assert.Equal("https://cos.example.com/signed/abc.png", url);

        // 只有两次上游调用（换 key + 提交）—— 没有下载
        Assert.Equal(2, fake.Count);
    }

    [Fact]
    public async Task 生图_response_format_b64_json_回_base64()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), ImageSyncOk(), Binary("image/png"));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/images/generations",
            new JsonObject { ["prompt"] = "猫", ["response_format"] = "b64_json" });

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var entry = body["data"]!.AsArray()[0]!.AsObject();

        Assert.True(entry.ContainsKey("b64_json"));
        Assert.False(entry.ContainsKey("url"));

        var bytes = Convert.FromBase64String(entry["b64_json"]!.GetValue<string>());

        // 落盘的字节被原样读回来（Binary 给的是纯 ASCII，长度可预期）
        Assert.Equal(Encoding.ASCII.GetByteCount("PNG-ASCII-PAYLOAD-0123456789"), bytes.Length);
        Assert.Equal((byte)80, bytes[0]);   // 'P'
    }

    [Fact]
    public async Task 生图_缺_prompt_回_400()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/images/generations", []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("invalid_request_error", body["error"]!["type"]!.GetValue<string>());

        // 参数不对时**一个上游请求都不该发**
        Assert.Equal(0, fake.Count);
    }

    // ══════════════════════════════════════════════════════════
    // ② 改图
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 改图_打的是_edit_路径且带上图片()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), ImageSyncOk(), Binary());

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/images/edits", new JsonObject
        {
            ["prompt"] = "把背景换成日落",
            ["image"] = "https://example.com/src.png",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ★ 打的是 edit 路径（和 generate 区分开）
        Assert.Contains("/api/ai/workbuddy-proxy/image/edit/submit", fake.Requests[1].Url);

        // ★ 图片被转成上游要的 media payload
        var sent = fake.Requests[1].Json()!;
        var image = sent["images"]!.AsArray()[0]!;
        Assert.Equal("https://example.com/src.png", image["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task 改图_data_URI_被剥成裸_base64()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), ImageSyncOk(), Binary());

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/images/edits", new JsonObject
        {
            ["prompt"] = "改一下",
            ["image"] = "data:image/png;base64,AAAB",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sent = fake.Requests[1].Json()!;
        var image = sent["images"]!.AsArray()[0]!;

        // ⚠ 上游要的是**裸 base64**，data URI 前缀必须剥掉
        Assert.Equal("AAAB", image["base64"]!.GetValue<string>());
        Assert.False(image.AsObject().ContainsKey("url"));
    }

    [Fact]
    public async Task 改图_缺_image_回_400()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/images/edits",
            new JsonObject { ["prompt"] = "改一下" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fake.Count);
    }

    // ══════════════════════════════════════════════════════════
    // ③ 生视频：提交就走 + 轮询
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 生视频_默认提交就走_回任务_id_不等待()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitAsync("task-vid-9", "video"));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/video/generations",
            new JsonObject { ["prompt"] = "一只猫在跳舞" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("task-vid-9", body["id"]!.GetValue<string>());
        Assert.Equal("video.generation", body["object"]!.GetValue<string>());
        Assert.Equal("/v1/video/status/task-vid-9", body["status_url"]!.GetValue<string>());

        // ★ 只打了两次（换 key + 提交）—— **没有轮询**
        //   视频要渲染几分钟，把 HTTP 连接挂着不现实
        Assert.Equal(2, fake.Count);
        Assert.Contains("/api/ai/workbuddy-proxy/video/submit", fake.Requests[1].Url);
    }

    [Fact]
    public async Task 生视频_wait_true_时轮询到完成并落盘()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            TempKeyOk(),
            SubmitAsync("task-vid-9", "video"),
            QueryResult("task-vid-9", "pending"),
            QueryResult("task-vid-9", "completed", "https://cos.example.com/signed/v.mp4"),
            Binary("video/mp4"));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/video/generations",
            new JsonObject { ["prompt"] = "海浪", ["wait"] = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var url = body["data"]!.AsArray()[0]!["url"]!.GetValue<string>();

        // 落盘之后给的是网关的 URL（视频同样不能给会过期的签名 URL）
        Assert.Contains("/v1/images/file/", url, StringComparison.Ordinal);
        Assert.EndsWith(".mp4", url, StringComparison.Ordinal);

        // ★ 真的轮询了（pending 之后又查了一次）
        Assert.True(fake.Count >= 4, $"应该轮询至少一次，实际上游调用 {fake.Count} 次");
    }

    [Fact]
    public async Task 生视频_wait_模式_没有产物时_回_502_带原因()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            TempKeyOk(),
            SubmitAsync("task-vid-9", "video"),
            QueryResult("task-vid-9", "failed", error: "内容不合规"));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/video/generations",
            new JsonObject { ["prompt"] = "违规内容", ["wait"] = true });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        // ⚠ 「任务跑了但没出图」必须把**上游给的原因**透出来，
        //   而不是一个笼统的「上游没有返回视频」
        Assert.Contains("内容不合规", body["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 图生视频_first_frame_触发_input_形状()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitAsync("task-i2v-1", "video"));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/video/generations", new JsonObject
        {
            ["first_frame"] = "https://example.com/first.png",
            ["prompt"] = "让它动起来",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ★ 图生视频的判据是有 first_frame → 上游形状是 input.first_frame_image
        var sent = fake.Requests[1].Json()!;
        var input = sent["input"]!.AsObject();
        Assert.Equal("https://example.com/first.png", input["first_frame_image"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task 图生视频_首尾帧都传时_带_last_frame_image()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitAsync("task-i2v-2", "video"));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/video/generations", new JsonObject
        {
            ["first_frame"] = "https://example.com/a.png",
            ["last_frame"] = "https://example.com/b.png",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var input = fake.Requests[1].Json()!["input"]!.AsObject();
        Assert.True(input.ContainsKey("first_frame_image"));
        Assert.True(input.ContainsKey("last_frame_image"));
    }

    [Fact]
    public async Task 视频_既没_prompt_也没_first_frame_回_400()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/video/generations", []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fake.Count);
    }

    // ══════════════════════════════════════════════════════════
    // ④ 视频状态查询
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 视频状态_未完成时不下载()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), QueryResult("task-vid-1", "pending"));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.Client.GetAsync("/v1/video/status/task-vid-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("pending", body["status"]!.GetValue<string>());

        // ★ 没完成就不该下载 —— 轮询中途白下是浪费
        Assert.False(body.AsObject().ContainsKey("url"));
        Assert.Equal(2, fake.Count);   // 换 key + 查询
    }

    [Fact]
    public async Task 视频状态_完成后下载并回网关_URL()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            TempKeyOk(),
            QueryResult("task-vid-1", "completed", "https://cos.example.com/signed/v.mp4"),
            Binary("video/mp4"));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.Client.GetAsync("/v1/video/status/task-vid-1");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal("completed", body["status"]!.GetValue<string>());
        Assert.Contains("/v1/images/file/", body["url"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ 媒体文件回读 + 路径穿越防护
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 媒体文件_落盘后能回读且带长缓存头()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), ImageSyncOk(), Binary("image/png"));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var generate = await fx.PostJsonAsync("/v1/images/generations",
            new JsonObject { ["prompt"] = "猫" });
        var url = JsonNode.Parse(await generate.Content.ReadAsStringAsync())!["data"]!.AsArray()[0]!["url"]!.GetValue<string>();

        var path = url[url.IndexOf("/v1", StringComparison.Ordinal)..];

        using var fetched = await fx.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal("image/png", fetched.Content.Headers.ContentType!.MediaType);

        // 文件名是内容哈希 → 内容永不改变 → 可以长期缓存
        Assert.Contains("immutable", fetched.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../appsettings.json")]
    [InlineData("..%2Fappsettings.json")]
    [InlineData("notahash.png")]
    [InlineData("ab.png")]                   // 哈希太短
    [InlineData("deadbeef.exe")]             // 扩展名不在白名单
    public async Task 媒体文件_非法文件名被拒(string name)
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.Client.GetAsync("/v1/images/file/" + name);

        // 非法名字 400、合法的但不存在 404 —— 两种都不是 200
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"非法文件名 {name} 不该回 {(int)response.StatusCode}");
    }

    [Fact]
    public async Task 媒体文件_合法名字但不存在_回_404()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.Client.GetAsync("/v1/images/file/deadbeefdeadbeefdeadbeef.png");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ══════════════════════════════════════════════════════════
    // ⑥ 失败分类 / 超时 / 重取 key
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 换_tempKey_失败_回_401()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Unauthorized());

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/images/generations",
            new JsonObject { ["prompt"] = "猫" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("authentication_error", body["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task 上游没返回合法_tempKey_回_401()
    {
        // 上游 200 但给了一个不是 ck_t_ 开头的 key —— 必须当成失败，不能拿去用
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text(
            new JsonObject { ["code"] = 0, ["data"] = new JsonObject { ["key"] = "not-a-tempkey" } }.ToJsonString()));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/images/generations",
            new JsonObject { ["prompt"] = "猫" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task tempKey_失效时自动重取一次()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            TempKeyOk("ck_t_first"),                                    // ① 换 key
            UpstreamResponses.Text(new JsonObject                      // ② 提交 → key 失效
            {
                ["code"] = "AUTH_INVALID_TEMP_KEY",
                ["msg"] = "临时凭据无效",
            }.ToJsonString()),
            TempKeyOk("ck_t_second"),                                   // ③ 重取
            ImageSyncOk(),                                              // ④ 再提交 → 成功
            Binary());                                                  // ⑤ 下载

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/images/generations",
            new JsonObject { ["prompt"] = "猫" });

        // ★ 自动重取之后成功 —— 不该把这个错误抛给用户让他重试
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 换 key 被调了两次（第三次请求是重取）
        Assert.Equal(2, fake.Requests.Count(r => r.Path == "/v2/api-keys"));

        // 第二次提交用的是新的 key
        Assert.Equal("Bearer ck_t_second", fake.Requests[3].Header("Authorization"));
    }

    [Fact]
    public async Task 上游业务错误码_回_502_并带原因()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            TempKeyOk(),
            UpstreamResponses.Text(new JsonObject
            {
                ["code"] = 50001,
                ["msg"] = "生成服务暂时不可用",
            }.ToJsonString()));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/images/generations",
            new JsonObject { ["prompt"] = "猫" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Contains("生成服务暂时不可用", body["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 上游没返回任务_id_回_502()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            TempKeyOk(),
            UpstreamResponses.Text(new JsonObject { ["code"] = 0, ["data"] = new JsonObject() }.ToJsonString()));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/video/generations",
            new JsonObject { ["prompt"] = "猫" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Contains("任务 id", body["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 轮询超时_回_504()
    {
        // 用脚本前两步把「换 key + 提交」走掉，之后**一直回 pending**
        // （越界续用最后一步的语义），再给一个几乎不等的超时验 TIMEOUT 分类。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            _ => TempKeyOk(),
            _ => SubmitAsync("task-vid-timeout", "video"),
            _ => QueryResult("task-vid-timeout", "pending"));

        await using var fx = await MediaFixture.StartAsync(fake);

        var miora = fx.Miora;
        var ex = await Assert.ThrowsAsync<MioraException>(async () =>
            await miora.SubmitAsync("video", null,
                new JsonObject { ["prompt"] = "永远不完成" },
                pollTimeout: TimeSpan.FromMilliseconds(1),
                CancellationToken.None));

        Assert.Equal(MioraErrorCodes.Timeout, ex.Code);
        Assert.Equal(504, ex.Status);
        Assert.Equal("timeout_error", ex.OpenAiType);
    }

    // ══════════════════════════════════════════════════════════
    // ⑦ 联网搜索
    // ══════════════════════════════════════════════════════════

    /// <summary>搜索响应 —— **扁平**返回（业务字段和 code 同级）。</summary>
    private static HttpResponseMessage SearchOk(params (string Title, string Url, string Snippet)[] items)
    {
        var results = new JsonArray();
        foreach (var (title, url, snippet) in items)
        {
            results.Add(new JsonObject { ["title"] = title, ["url"] = url, ["snippet"] = snippet });
        }

        return UpstreamResponses.Text(new JsonObject
        {
            ["code"] = 0,
            ["provider"] = "test-provider",
            ["response_time_ms"] = 123,
            ["results"] = results,
        }.ToJsonString());
    }

    [Fact]
    public async Task 搜索_默认回_markdown_正文()
    {
        var fake = new FakeUpstreamHandler(_ => SearchOk(("标题一", "https://a.example.com/1", "摘要一")));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/search",
            new JsonObject { ["query"] = "Node.js LTS" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // ⚠ text/plain 而不是 text/markdown —— 有些客户端不认后者
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.ToString());

        var text = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("# Search Results for \"Node.js LTS\"", text);
        Assert.Contains("[标题一](https://a.example.com/1)", text, StringComparison.Ordinal);
        Assert.Contains("摘要一", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 搜索_format_json_回结构化()
    {
        var fake = new FakeUpstreamHandler(_ => SearchOk(("T", "https://a.example.com/1", "S")));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/search",
            new JsonObject { ["query"] = "Node.js", ["format"] = "json" });

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal("search_result", body["object"]!.GetValue<string>());
        Assert.Equal(1, body["totalResults"]!.GetValue<int>());
        Assert.Equal("test-provider", body["provider"]!.GetValue<string>());
    }

    [Fact]
    public async Task 搜索_查询词太短_回_400_且不打上游()
    {
        var fake = new FakeUpstreamHandler(_ => SearchOk());

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/search", new JsonObject { ["query"] = "a" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fake.Count);
    }

    [Fact]
    public async Task 搜索_freshness_非法时_回_400()
    {
        var fake = new FakeUpstreamHandler(_ => SearchOk());

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/search",
            new JsonObject { ["query"] = "测试", ["freshness"] = "d99" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fake.Count);
    }

    [Fact]
    public async Task 搜索_排除域名折成_site_前缀()
    {
        var fake = new FakeUpstreamHandler(_ => SearchOk());

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/search", new JsonObject
        {
            ["query"] = "测试",
            ["exclude_domains"] = new JsonArray("spam.com", "ads.net"),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ★ 上游没有排除字段，折进 query 是官方 CLI 的做法
        var sent = fake.Last.Json()!;
        var query = sent["query"]!.GetValue<string>();
        Assert.Contains("-site:spam.com", query, StringComparison.Ordinal);
        Assert.Contains("-site:ads.net", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 搜索_include_domains_在本地过滤()
    {
        var fake = new FakeUpstreamHandler(_ => SearchOk(
            ("要的", "https://docs.python.org/3/", "对"),
            ("不要的", "https://baidu.com/x", "错"),
            ("也不要", "https://notdocs.python.org/x", "近似域名但不同 host")));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/search", new JsonObject
        {
            ["query"] = "python",
            ["include_domains"] = new JsonArray("docs.python.org"),
            ["format"] = "json",
        });

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var results = body["results"]!.AsArray();

        // ★ 上游的 allowed_domains 不生效，所以这是**我们本地筛的**
        Assert.Single(results);
        Assert.Equal("https://docs.python.org/3/", results[0]!["url"]!.GetValue<string>());

        // ★ 而且必须把 max_results 拉满再筛 —— 否则筛完不够数
        Assert.Equal(20, fake.Last.Json()!["max_results"]!.GetValue<int>());
    }

    [Fact]
    public async Task 搜索_凭据为空时_回_401()
    {
        var fake = new FakeUpstreamHandler(_ => SearchOk());

        // 保险库为空 → 没有 accessToken
        await using var fx = await MediaFixture.StartAsync(fake, new TestVault());

        using var response = await fx.PostJsonAsync("/v1/search", new JsonObject { ["query"] = "测试" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("authentication_error", body["error"]!["type"]!.GetValue<string>());
    }

    // ══════════════════════════════════════════════════════════
    // ⑧ 网页抓取
    // ══════════════════════════════════════════════════════════

    private static HttpResponseMessage FetchOk(string url = "https://example.com")
        => UpstreamResponses.Text(new JsonObject
        {
            ["code"] = 0,
            ["url"] = url,
            ["title"] = "Example Domain",
            ["content"] = "正文内容",
            ["content_type"] = "text/html",
            ["content_length"] = 4,
            ["truncated"] = false,
            ["status_code"] = 200,
        }.ToJsonString());

    [Fact]
    public async Task 抓取_默认回_text_plain_正文()
    {
        var fake = new FakeUpstreamHandler(_ => FetchOk());

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/webfetch",
            new JsonObject { ["url"] = "https://example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.ToString());

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("Example Domain", text, StringComparison.Ordinal);
        Assert.Contains("正文内容", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 抓取_format_json_回完整文档()
    {
        var fake = new FakeUpstreamHandler(_ => FetchOk());

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/webfetch",
            new JsonObject { ["url"] = "https://example.com", ["format"] = "json" });

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal("web_fetch_result", body["object"]!.GetValue<string>());
        Assert.Equal("Example Domain", body["title"]!.GetValue<string>());
        Assert.Equal(200, body["statusCode"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    public async Task 抓取_非_http_协议被拒(string url)
    {
        var fake = new FakeUpstreamHandler(_ => FetchOk());

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/webfetch", new JsonObject { ["url"] = url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // ★ 绝不能把这个 URL 转给上游去打（SSRF 面）
        Assert.Equal(0, fake.Count);
    }

    [Fact]
    public async Task 抓取_缺_url_回_400()
    {
        var fake = new FakeUpstreamHandler(_ => FetchOk());

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/webfetch", []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fake.Count);
    }

    // ══════════════════════════════════════════════════════════
    // ⑨ 鉴权 —— 不豁免
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 没配_API_Key_时_新增端点直接放行()
    {
        // 网关的语义（见 GatewayApp.Authorized）：ApiKey 为空 = **不鉴权**，
        // 和旧版一致。这条钉住「新增端点没有自己发明一套鉴权」——
        // 它们和 /v1/* 同待遇，不会平白多要一个 key。
        //
        // 真正的「不豁免」由下面 `配了_API_Key_时_新增端点要_bearer` 证明。
        var fake = new FakeUpstreamHandler(_ => SearchOk(("t", "https://a.com/1", "s")));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/search", new JsonObject { ["query"] = "xy" });

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/v1/images/generations")]
    [InlineData("/v1/search")]
    public async Task 配了_API_Key_时_新增端点要_bearer(string path)
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));

        var vault = new TestVault().Add();
        var mediaDir = Path.Combine(Path.GetTempPath(), "buddydeck-media-auth-" + Guid.NewGuid().ToString("N")[..8]);

        await using var host = await GatewayHost.StartReadyAsync(
            fake: fake,
            vault: vault,
            apiKey: "media-secret",
            configure: services =>
            {
                var miora = new MioraClient(
                    new HttpClient(fake, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
                    () => vault.Entries[0].ToCredential(),
                    UpstreamVariant.China, mediaDir, TimeProvider.System,
                    NullLogger<MioraClient>.Instance);

                services.AddSingleton(miora);
                services.AddSingleton(new SearchClient(
                    new HttpClient(fake, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
                    () => vault.Entries[0].ToCredential(),
                    UpstreamVariant.China, NullLogger<SearchClient>.Instance));

                EndpointFamilies.Register<MediaEndpoints>(services);
                EndpointFamilies.Register<SearchEndpoints>(services);

                services.AddSingleton(sp => new GatewayApp(
                    sp.GetRequiredService<GatewayOptions>(),
                    sp.GetRequiredService<UpstreamVariant>(),
                    sp.GetRequiredService<ModelCatalog>(),
                    sp.GetRequiredService<BuildInfo>(),
                    sp.GetRequiredService<UpstreamClient>(),
                    sp.GetRequiredService<AccountPool>(),
                    sp.GetRequiredService<ICredentialSource>(),
                    sp.GetRequiredService<ChatObservers>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GatewayApp>>(),
                    families: EndpointFamilies.Resolve(sp)));
            });

        try
        {
            // 不带 key → 401（鉴权在端点族**之前**）
            using var anonymous = await host.Client.PostAsync(path,
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            // 带对 key → 不再是 401（走到端点自己的逻辑）
            using var request = GatewayHost.BearerRequest(HttpMethod.Post, path, "media-secret");
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var authorized = await host.Client.SendAsync(request);
            Assert.NotEqual(HttpStatusCode.Unauthorized, authorized.StatusCode);
        }
        finally
        {
            if (Directory.Exists(mediaDir)) Directory.Delete(mediaDir, recursive: true);
        }
    }

    // ══════════════════════════════════════════════════════════
    // ⑩ 媒体工具注入 + 执行（协议层）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 注入_客户端没有_tools_时也注入四个()
    {
        var request = new JsonObject { ["model"] = "hy3" };

        var (injected, skipped) = MediaTools.Inject(request);

        Assert.Equal(MediaTools.Names.Count, injected.Count);
        Assert.Empty(skipped);
        Assert.Contains("workbuddy_generate_image", injected);
        Assert.Contains("workbuddy_edit_image", injected);
        Assert.Contains("workbuddy_generate_video", injected);
        Assert.Contains("workbuddy_image_to_video", injected);

        Assert.Equal(MediaTools.Names.Count, request["tools"]!.AsArray().Count);
    }

    [Fact]
    public void 注入_尊重客户端已有同名工具()
    {
        var request = new JsonObject
        {
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "workbuddy_generate_image",
                    ["description"] = "客户端自己的实现",
                },
            }),
        };

        var (injected, skipped) = MediaTools.Inject(request);

        // ★ 客户端已经有同名工具 → 不动它（万一是它自己的实现，
        //   抢过来执行反而是坏的）
        Assert.Contains("workbuddy_generate_image", skipped);
        Assert.DoesNotContain("workbuddy_generate_image", injected);
        // ⚠ 用 `Names.Count - 1` 而不是写死数字：
        //   写死的那个数在每次新增工具时都会红（本轮加 web_search/fetch_page 就踩了）。
        //   这里的**契约**是「我们注入的 = 全部 - 客户端已有的那个」，不是某个具体数字。
        Assert.Equal(MediaTools.Names.Count - 1, injected.Count);

        // 客户端那个定义原样保留
        var tools = request["tools"]!.AsArray();
        Assert.Equal(MediaTools.Names.Count, tools.Count);
        Assert.Equal("客户端自己的实现",
            tools[0]!["function"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void 注入_保留客户端自己的其他工具()
    {
        var request = new JsonObject
        {
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = "get_weather" },
            }),
        };

        MediaTools.Inject(request);

        var names = request["tools"]!.AsArray()
            .Select(t => t!["function"]!["name"]!.GetValue<string>())
            .ToList();

        Assert.Contains("get_weather", names);
        Assert.Equal(MediaTools.Names.Count + 1, names.Count);   // 1 个客户自己的 + 我们的全部
    }

    [Fact]
    public void 注入_关闭时不加任何东西()
    {
        var request = new JsonObject { ["model"] = "hy3" };

        var (injected, _) = MediaTools.Inject(request, enabled: false);

        Assert.Empty(injected);
        Assert.False(request.AsObject().ContainsKey("tools"));
    }

    [Fact]
    public void 工具名前缀识别()
    {
        Assert.True(MediaTools.IsMediaTool("workbuddy_generate_image"));
        Assert.False(MediaTools.IsMediaTool("get_weather"));
        Assert.False(MediaTools.IsMediaTool(null));

        Assert.Equal("generate_image", MediaTools.StripPrefix("workbuddy_generate_image"));
        Assert.Null(MediaTools.StripPrefix("get_weather"));
    }

    [Fact]
    public void 只切我们的工具_客户端的原样交回()
    {
        var calls = new JsonArray
        {
            new JsonObject { ["function"] = new JsonObject { ["name"] = "workbuddy_generate_image" } },
            new JsonObject { ["function"] = new JsonObject { ["name"] = "get_weather" } },
        };

        var (mine, foreign) = MediaToolExecutor.SplitCalls(calls);

        // ⚠ 踩过的坑：不区分的话，客户端调 get_weather 会被网关执行成
        //   「未知工具」，模型于是回一句「天气工具好像不可用」——
        //   用户看到一切正常，只是工具永远查不出数据。
        Assert.Single(mine);
        Assert.Single(foreign);
        Assert.Equal("get_weather", foreign[0]["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void 回填必须覆盖全部调用_含外部工具()
    {
        var ours = new JsonObject { ["function"] = new JsonObject { ["name"] = "workbuddy_generate_image" } };
        var theirs = new JsonObject { ["function"] = new JsonObject { ["name"] = "get_weather" } };

        var all = new List<JsonObject> { ours, theirs };
        var mine = new List<JsonObject> { ours };
        var mineResults = new List<MediaToolExecutor.ToolOutcome> { new(true, "生成好了") };

        var results = MediaToolExecutor.ResultsForAll(all, mine, mineResults);

        // ⚠ 上游要求每个 tool_call 都有对应的 tool 消息，缺一个就报错
        Assert.Equal(2, results.Count);
        Assert.Equal("生成好了", results[0].Text);
        Assert.False(results[1].Ok);
        Assert.Contains("由客户端执行", results[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 执行工具_生图_回填带图片_markdown()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), ImageSyncOk(), Binary());

        await using var fx = await MediaFixture.StartAsync(fake);

        var executor = new MediaToolExecutor(fx.Miora, NullLogger<MediaToolExecutor>.Instance);

        var calls = new List<JsonObject>
        {
            new()
            {
                ["function"] = new JsonObject
                {
                    ["name"] = "workbuddy_generate_image",
                    ["arguments"] = new JsonObject { ["prompt"] = "橘猫" }.ToJsonString(),
                },
            },
        };

        var results = await executor.ExecuteAsync(calls, file => "http://127.0.0.1:9999/v1/images/file/" + file, CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].Ok);

        // ★ 回填文字里带 markdown 图片，而且明确要求模型原样放进回复
        Assert.Contains("![生成结果](", results[0].Text, StringComparison.Ordinal);
        Assert.Contains("原样放进你的回复", results[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 执行工具_生视频_返回任务_id_不等渲染()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitAsync("task-vid-tool", "video"));

        await using var fx = await MediaFixture.StartAsync(fake);

        var executor = new MediaToolExecutor(fx.Miora, NullLogger<MediaToolExecutor>.Instance);

        var calls = new List<JsonObject>
        {
            new()
            {
                ["function"] = new JsonObject
                {
                    ["name"] = "workbuddy_generate_video",
                    ["arguments"] = new JsonObject { ["prompt"] = "猫跳舞" }.ToJsonString(),
                },
            },
        };

        var results = await executor.ExecuteAsync(calls, _ => "http://x/", CancellationToken.None);

        Assert.True(results[0].Ok);
        Assert.Contains("task-vid-tool", results[0].Text, StringComparison.Ordinal);
        Assert.Contains("/v1/video/status/task-vid-tool", results[0].Text, StringComparison.Ordinal);

        // ★ 明确告诉模型别等 —— 提交就结束了
        Assert.Contains("不要在这里等待或轮询", results[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 执行工具_参数不是合法_JSON_时_回失败而不是抛()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        await using var fx = await MediaFixture.StartAsync(fake);

        var executor = new MediaToolExecutor(fx.Miora, NullLogger<MediaToolExecutor>.Instance);

        var calls = new List<JsonObject>
        {
            new()
            {
                ["function"] = new JsonObject
                {
                    ["name"] = "workbuddy_generate_image",
                    ["arguments"] = "{这不是合法 JSON",
                },
            },
        };

        var results = await executor.ExecuteAsync(calls, _ => "http://x/", CancellationToken.None);

        Assert.False(results[0].Ok);
        Assert.Contains("不是合法 JSON", results[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 执行工具_上游失败时_回失败文本而不是抛异常()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Unauthorized());
        await using var fx = await MediaFixture.StartAsync(fake);

        var executor = new MediaToolExecutor(fx.Miora, NullLogger<MediaToolExecutor>.Instance);

        var calls = new List<JsonObject>
        {
            new()
            {
                ["function"] = new JsonObject
                {
                    ["name"] = "workbuddy_generate_image",
                    // ⚠ 必须给 prompt —— 参数校验在调上游**之前**，
                    //   给 "{}" 的话会得到「缺少 prompt 参数」而不是
                    //   我们想验的「上游失败」那条路。
                    ["arguments"] = new JsonObject { ["prompt"] = "猫" }.ToJsonString(),
                },
            },
        };

        var results = await executor.ExecuteAsync(calls, _ => "http://x/", CancellationToken.None);

        // ⚠ agent 循环里抛异常会整条请求 500；工具失败必须变成
        //   一条「告诉模型失败原因」的文本，让它继续对话
        Assert.False(results[0].Ok);
        Assert.Contains("执行失败", results[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 聊天请求会被注入媒体工具()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.SseText(
            """{"choices":[{"index":0,"delta":{"content":"ok"}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/chat/completions", new JsonObject
        {
            ["model"] = "hy3",
            ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "你好" }),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        // ★ 上游收到的聊天请求体里**带着我们注入的四个工具**
        var sent = fake.Requests.First(r => r.Path == "/v2/chat/completions").Json()!;
        var tools = sent["tools"]!.AsArray();

        Assert.Equal(MediaTools.Names.Count, tools.Count);
        Assert.Contains("workbuddy_generate_image",
            tools.Select(t => t!["function"]!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task 客户端带_tool_choice_none_时不注入()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.SseText(
            """{"choices":[{"index":0,"delta":{"content":"ok"}}]}"""));

        await using var fx = await MediaFixture.StartAsync(fake);

        using var response = await fx.PostJsonAsync("/v1/chat/completions", new JsonObject
        {
            ["model"] = "hy3",
            ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "你好" }),
            ["tool_choice"] = "none",
        });

        await response.Content.ReadAsStringAsync();

        var sent = fake.Requests.First(r => r.Path == "/v2/chat/completions").Json()!;

        // ⚠ 用户明确说「别调工具」时不该再塞回去 —— 注入发生在
        //   RequestPipeline.Prepare **之后**就是为了让它先把 tools 删干净
        Assert.False(sent.AsObject().ContainsKey("tools"));
    }

    // ══════════════════════════════════════════════════════════
    // ⑪ 读图（多模态）：原样透传，不破坏
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 读图_多模态消息原样透传给上游()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.SseText(
            """{"choices":[{"index":0,"delta":{"content":"我看到一只猫"}}]}"""));

        await using var fx = await MediaFixture.StartAsync(fake);

        // OpenAI 的多模态形状：content 是数组，含 image_url
        var multimodal = new JsonObject
        {
            ["model"] = "hy3",
            ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "这张图里是什么？" },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = "data:image/png;base64,iVBORw0KGgo=",
                        },
                    }),
            }),
        };

        using var response = await fx.PostJsonAsync("/v1/chat/completions", multimodal);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        var sent = fake.Requests.First(r => r.Path == "/v2/chat/completions").Json()!;
        var content = sent["messages"]!.AsArray()[0]!["content"]!.AsArray();

        // ★ 图片内容**原样到达上游** —— 请求体管线只碰 stream/role/tool_choice，
        //   不该动 messages 的内容结构
        Assert.Equal(2, content.Count);
        Assert.Equal("image_url", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=",
            content[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task 读图_管线不会破坏多模态的消息结构()
    {
        // 直接测管线本身（不经过 HTTP）—— 多模态是请求体管线最容易误伤的地方
        var body = """
        {
          "model": "hy3",
          "messages": [
            {"role": "user", "content": [
              {"type": "text", "text": "看图"},
              {"type": "image_url", "image_url": {"url": "https://example.com/a.png"}}
            ]}
          ],
          "tools": [{"type":"function","function":{"name":"keep_me"}}],
          "tool_choice": {"type": "function", "function": {"name": "keep_me"}}
        }
        """;

        var prepared = RequestPipeline.Prepare(UpstreamVariant.China, body, "hy3");
        var parsed = JsonNode.Parse(prepared)!;

        // 图片内容没被动
        var content = parsed["messages"]!.AsArray()[0]!["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Equal("https://example.com/a.png",
            content[1]!["image_url"]!["url"]!.GetValue<string>());

        // 客户端的工具还在（tool_choice 是函数名形态 → 归一成字符串名）
        Assert.Single(parsed["tools"]!.AsArray());
        Assert.Equal("keep_me", parsed["tool_choice"]!.GetValue<string>());

        // stream 被强制成 true（上游只支持流式）
        Assert.True(parsed["stream"]!.GetValue<bool>());
    }
}
