using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 生视频全链路（t73）：提交 → 轮询 → 完成落盘 → 回读。
///
/// ## 为什么直接驱动 VideoPipeline 而不走 HTTP 端点
///
/// 这一层是**任务生命周期状态机**，它的难点全在「什么时候停」：
/// 上游报终态 / 到点 / 被取消 —— 三条出口都要有，且要能分开报。
/// 端点那层（`MediaEndpoints`）只负责解析请求、写响应，它自己的用例
/// 在 `MediaEndpointTests` 里。
///
/// 直接构造 `MioraClient` + `VideoPipeline`，就能在**同一进程内**精确控制
/// 「轮询返回什么、轮询几次、什么时候取消」，不需要起 Kestrel。
///
/// ## ⚠ 时间被压到毫秒级
///
/// `Options.PollInterval` / `Timeout` 都是可注入的。默认值（3 秒 / 15 分钟）
/// 是给生产的；测试里如果不压，一个「超时」用例要跑 15 分钟 —— **没人跑得起，
/// 等于没有这条测试**。
/// </summary>
public class VideoPipelineTests
{
    // ══════════════════════════════════════════════════════════
    // 假上游的响应工厂（与 MediaEndpointTests 同形状）
    // ══════════════════════════════════════════════════════════

    /// <summary>tempKey 换取成功。</summary>
    private static HttpResponseMessage TempKeyOk()
        => UpstreamResponses.Text(new JsonObject
        {
            ["code"] = 0,
            ["data"] = new JsonObject
            {
                ["key"] = "ck_t_test_key",
                ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(15).ToString("O"),
            },
        }.ToJsonString());

    /// <summary>视频异步提交（只给任务 id）。</summary>
    private static HttpResponseMessage SubmitOk(string taskId = "task-vid-1")
        => UpstreamResponses.Text(new JsonObject
        {
            ["code"] = 0,
            ["data"] = new JsonObject
            {
                ["upstreamTaskId"] = taskId,
                ["status"] = "pending",
                ["mode"] = "async",
                ["model"] = "miora-video-v1",
            },
        }.ToJsonString());

    /// <summary>提交失败：上游没给任务 id。</summary>
    private static HttpResponseMessage SubmitNoTaskId()
        => UpstreamResponses.Text(new JsonObject
        {
            ["code"] = 0,
            ["data"] = new JsonObject { ["status"] = "pending" },
        }.ToJsonString());

    /// <summary>
    /// 轮询返回。形状是 `{data: {taskId: {...}}}` —— query-task 的实测形状。
    /// </summary>
    private static HttpResponseMessage Query(
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
                ["fileKey"] = "bga/video-1",
            });
        }

        if (error is not null) task["errorMessage"] = error;

        return UpstreamResponses.Text(new JsonObject
        {
            ["code"] = 0,
            ["data"] = new JsonObject { [taskId] = task },
        }.ToJsonString());
    }

    /// <summary>查询返回一个**空**的 data —— 上游「查不到这个任务」。</summary>
    private static HttpResponseMessage QueryUnknownTask()
        => UpstreamResponses.Text(new JsonObject
        {
            ["code"] = 0,
            ["data"] = new JsonObject(),
        }.ToJsonString());

    /// <summary>下载产物（纯 ASCII 字节，长度可预期）。</summary>
    private static HttpResponseMessage VideoBinary()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes("MP4-ASCII-PAYLOAD-0123456789")),
        };
        response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("video/mp4");
        return response;
    }

    // ══════════════════════════════════════════════════════════
    // 夹具
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 造一个 VideoPipeline + 一个假上游 + 一个临时媒体目录。
    ///
    /// ⚠ `MioraClient` 必须挂**假上游的 handler** ——
    /// 不挂的话它去打真实网络（`MediaEndpointTests:186-189` 记过这个坑：
    /// 实测表现是「拿到一个真的 401 而 fake.Count 是 0」，排查方向被带偏）。
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _mediaDir;

        public FakeUpstreamHandler Fake { get; }
        public VideoPipeline Pipeline { get; }

        /// <summary>
        /// 轮询参数压到毫秒级 —— 否则超时用例要跑 15 分钟。
        ///
        /// ⚠ Timeout 给 5 秒而不是几百毫秒：全量跑测试时机器负载高，
        /// 几百毫秒会让「提交_轮询到完成_落盘」这种**成功路径**的用例
        /// 在负载下偶发超时（实测：并发跑 6 个测试程序集时稳定复现）。
        /// 成功路径根本不该撞到 Timeout，给宽一点不影响断言语义 ——
        /// 真正测超时的用例会显式传更短的 timeout。
        /// </summary>
        public static VideoPipeline.Options FastOptions(TimeSpan? timeout = null) => new()
        {
            PollInterval = TimeSpan.FromMilliseconds(5),
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
        };

        private Fixture(FakeUpstreamHandler fake, VideoPipeline pipeline, string mediaDir)
        {
            Fake = fake;
            Pipeline = pipeline;
            _mediaDir = mediaDir;
        }

        public static Fixture Create(FakeUpstreamHandler fake)
        {
            var mediaDir = Path.Combine(Path.GetTempPath(), "buddydeck-video-" + Guid.NewGuid().ToString("N")[..8]);

            var miora = new MioraClient(
                new HttpClient(fake, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
                () => new Credential { AccessToken = "test-token", Label = "测试号" },
                UpstreamVariant.China,
                mediaDir,
                TimeProvider.System,
                NullLogger<MioraClient>.Instance);

            return new Fixture(fake, new VideoPipeline(miora, NullLogger<VideoPipeline>.Instance), mediaDir);
        }

        /// <summary>媒体目录里现在有哪些文件。</summary>
        public string[] MediaFiles()
            => Directory.Exists(_mediaDir) ? Directory.GetFiles(_mediaDir) : [];

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(_mediaDir)) Directory.Delete(_mediaDir, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不该让测试红
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>一个空的上游 body（视频提交用）。</summary>
    private static JsonObject VideoBody() => new() { ["prompt"] = "一只在跳舞的猫" };

    // ══════════════════════════════════════════════════════════
    // ① 成功路径：提交 → 轮询 → 落盘
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 提交_轮询到完成_落盘()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            TempKeyOk(),                                        // ① 换 tempKey
            SubmitOk(),                                         // ② 提交
            Query("task-vid-1", "pending"),                     // ③ 轮询：还没好
            Query("task-vid-1", "running"),                     // ④ 轮询：在跑
            Query("task-vid-1", "completed", "https://cos.example.com/v1.mp4"),  // ⑤ 好了
            VideoBinary());                                     // ⑥ 下载

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());

        Assert.True(result.Ok, result.Message);
        Assert.Equal(VideoPipeline.Outcome.Completed, result.Outcome);
        Assert.Equal("task-vid-1", result.TaskId);
        Assert.Single(result.Files);
        Assert.Equal("video/mp4", result.Files[0].MimeType);
    }

    [Fact]
    public async Task 成功后文件真的落到了媒体目录()
    {
        // ★ 「落盘」是契约明写的验收项，所以要验**文件真的在磁盘上** ——
        //   只断言 Files 非空的话，一个「记了个文件名但没写盘」的实现照样绿。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitOk(),
            Query("task-vid-1", "completed", "https://cos.example.com/v1.mp4"),
            VideoBinary());

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());

        Assert.True(result.Ok, result.Message);

        var files = fx.MediaFiles();
        Assert.Single(files);
        Assert.EndsWith(".mp4", files[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(result.Files[0].FileName, Path.GetFileName(files[0]));
    }

    [Fact]
    public async Task 文件名是内容哈希_同一内容重复下载不堆积()
    {
        // 与 MioraClient.DownloadAsync 的既有约定一致：
        // 文件名用内容哈希 → 同一段视频重复下载会覆盖，不越积越多。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitOk(),
                Query("task-vid-1", "completed", "https://cos.example.com/a.mp4"), VideoBinary())
            .Script(TempKeyOk(), SubmitOk(),
                Query("task-vid-1", "completed", "https://cos.example.com/a.mp4"), VideoBinary());

        await using var fx = Fixture.Create(fake);

        await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());
        await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());

        // 内容一样 → 同一个文件名 → 目录里只有一个文件
        Assert.Single(fx.MediaFiles());
    }

    [Fact]
    public async Task 轮询到终态前不会白下载()
    {
        // 「执行了 6 个脚本」本身就证明：pending / running 那两次**没有**触发下载。
        // 多下一次的话脚本会提前对不上（fake 是先进先出）。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            TempKeyOk(),
            SubmitOk(),
            Query("task-vid-1", "pending"),
            Query("task-vid-1", "running"),
            Query("task-vid-1", "completed", "https://cos.example.com/v1.mp4"),
            VideoBinary());

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());

        Assert.True(result.Ok, result.Message);
        // 6：tempKey + 提交 + 3 次轮询 + 1 次下载。多一次少一次都会红。
        Assert.Equal(6, fake.Count);
    }

    [Fact]
    public async Task 轮询次数被记录下来()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitOk(),
            Query("task-vid-1", "pending"),
            Query("task-vid-1", "completed", "https://cos.example.com/v1.mp4"),
            VideoBinary());

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());

        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.PollCount);
    }

    // ══════════════════════════════════════════════════════════
    // ② 进度回调
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 每次轮询都回调进度()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitOk(),
            Query("task-vid-1", "pending"),
            Query("task-vid-1", "running"),
            Query("task-vid-1", "completed", "https://cos.example.com/v1.mp4"),
            VideoBinary());

        await using var fx = Fixture.Create(fake);

        var seen = new List<string>();
        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions(),
            onProgress: (p, _) =>
            {
                seen.Add(p["status"]!.GetValue<string>());
                return Task.CompletedTask;
            });

        Assert.True(result.Ok, result.Message);
        Assert.Equal(["pending", "running", "completed"], seen);
    }

    [Fact]
    public async Task 进度里带轮询序号与状态变化标记()
    {
        // `changed` 让界面不用自己记住上一次是什么 —— 只在该刷新时才刷新。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitOk(),
            Query("task-vid-1", "running"),
            Query("task-vid-1", "running"),
            Query("task-vid-1", "completed", "https://cos.example.com/v1.mp4"),
            VideoBinary());

        await using var fx = Fixture.Create(fake);

        var changes = new List<bool>();
        await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions(),
            onProgress: (p, _) =>
            {
                changes.Add(p["changed"]!.GetValue<bool>());
                return Task.CompletedTask;
            });

        // 第 1 次是「从 pending 变成 running」→ 变了
        // 第 2 次还是 running            → 没变
        // 第 3 次变成 completed          → 变了
        Assert.Equal([true, false, true], changes);
    }

    [Fact]
    public async Task 不传进度回调也能跑()
    {
        // onProgress 是可选的：不传就不构造进度对象（省每次轮询的一次分配）
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitOk(),
            Query("task-vid-1", "completed", "https://cos.example.com/v1.mp4"),
            VideoBinary());

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());

        Assert.True(result.Ok, result.Message);
    }

    // ══════════════════════════════════════════════════════════
    // ③ 终态分类：上游失败
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 上游报失败_分类为_UpstreamFailed_且带原因()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitOk(),
            Query("task-vid-1", "failed", error: "内容审核不通过"));

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());

        Assert.False(result.Ok);
        Assert.Equal(VideoPipeline.Outcome.UpstreamFailed, result.Outcome);
        Assert.Contains("内容审核不通过", result.Message, StringComparison.Ordinal);
        Assert.Empty(fx.MediaFiles());   // 失败不该落盘
    }

    [Fact]
    public async Task 上游报失败但没给原因_也要有话说()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitOk(), Query("task-vid-1", "failed"));

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());

        Assert.Equal(VideoPipeline.Outcome.UpstreamFailed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task 上游报_cancelled_分类为_UpstreamCancelled_而不是失败()
    {
        // 「上游取消了」和「上游失败了」对用户是两件事：
        // 前者通常是我们自己要求的（或者超时后上游自己收摊），
        // 后者是真的出了问题。混在一起会让用户白白改提示词。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitOk(), Query("task-vid-1", "cancelled"));

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());

        Assert.Equal(VideoPipeline.Outcome.UpstreamCancelled, result.Outcome);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task 报完成但一个文件都没有_不算成功()
    {
        // ★ 这条是分类自洽性的关键：`Completed` 必须自己说清自己。
        //   如果这里回 Completed 而 Files 空，调用方就得再补一句
        //   `if (result.Files.Count == 0)` —— 那说明分类漏了一档。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitOk(), Query("task-vid-1", "completed"));

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());

        Assert.False(result.Ok);
        Assert.NotEqual(VideoPipeline.Outcome.Completed, result.Outcome);
        Assert.Contains("没有返回视频", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 提交时拿不到任务id_分类为_SubmitRejected()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitNoTaskId());

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());

        Assert.Equal(VideoPipeline.Outcome.SubmitRejected, result.Outcome);
        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task 上游查不到这个任务_立刻结束而不是轮到底()
    {
        // ★ 判据：`QueryAsync` 返回 null = 上游说「没有这个 id」。
        //   继续轮下去毫无意义 —— 它不会凭空出现。
        //   这条同时验证「没有静默挂起」。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(TempKeyOk(), SubmitOk(), QueryUnknownTask());

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions(
            timeout: TimeSpan.FromSeconds(30)));

        Assert.Equal(VideoPipeline.Outcome.SubmitRejected, result.Outcome);
        Assert.Contains("查不到", result.Message, StringComparison.Ordinal);
        // 没有一直轮到 30 秒的超时 —— 只查了一两次就收手了
        Assert.True(result.PollCount <= 3, $"应当在发现查不到后立刻结束，实际轮了 {result.PollCount} 次");
    }

    // ══════════════════════════════════════════════════════════
    // ④ 终态分类：超时（与 UpstreamFailed 必须可分）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 装一个「提交成功 + 之后一直回同一个状态」的脚本。
    ///
    /// ⚠⚠ 为什么不能用 `fake.Script(a, b)` 然后再 `foreach (...) fake.Script(c)`
    ///    —— **`Script()` 是替换而不是追加**（`FakeUpstreamHandler.cs:168`：
    ///    `_script = new UpstreamScript(steps);`）。连着调两次的话，第二次
    ///    把第一次整个覆盖掉，于是序列从「tempKey → 提交 → 轮询…」变成
    ///    「轮询…」—— 换 tempKey 那一步拿到的是轮询响应，报
    ///    「上游没返回合法的临时凭据」。
    ///
    /// 所以这里**一次性拼成一个数组**再交给 Script。
    /// </summary>
    private static void ScriptSubmitThenAlwaysRespond(
        FakeUpstreamHandler fake,
        string status,
        int tailSteps = 200,
        string? signedUrl = null,
        HttpResponseMessage? tail = null)
    {
        var steps = new List<HttpResponseMessage> { TempKeyOk(), SubmitOk() };
        for (var i = 0; i < tailSteps; i++)
        {
            steps.Add(tail ?? Query("task-vid-1", status, signedUrl));
        }

        fake.Script(steps.ToArray());
    }

    [Fact]
    public async Task 一直不报终态_到点判超时()
    {
        // 上游永远回 running —— 没有超时机制的话这里会**永远转下去**
        // （用户看到「卡住了」，服务端连一条日志都没有）。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        ScriptSubmitThenAlwaysRespond(fake, "running");

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions(
            timeout: TimeSpan.FromMilliseconds(120)));

        Assert.Equal(VideoPipeline.Outcome.TimedOut, result.Outcome);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task 超时文案明说_任务可能还在跑()
    {
        // ★ 这条文案是**有用性**的关键：
        //   用户在一个 12 分钟的视频上看到「生成失败」，会把提示词改到面目全非 ——
        //   而那个任务其实快好了。必须告诉他「上游可能还在跑」。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        ScriptSubmitThenAlwaysRespond(fake, "running");

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions(
            timeout: TimeSpan.FromMilliseconds(120)));

        Assert.Contains("可能还在上游", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 超时与上游失败是两种分类()
    {
        // ★★ 契约明写「超时与失败有明确分类」。这条把两者放一起对比 ——
        //    如果实现把超时也归成 UpstreamFailed，这条会红。
        var timeoutFake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        ScriptSubmitThenAlwaysRespond(timeoutFake, "running");

        await using var a = Fixture.Create(timeoutFake);
        var timedOut = await a.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions(
            timeout: TimeSpan.FromMilliseconds(120)));

        var failFake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        failFake.Script(TempKeyOk(), SubmitOk(), Query("task-vid-1", "failed", error: "坏了"));

        await using var b = Fixture.Create(failFake);
        var failed = await b.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());

        Assert.Equal(VideoPipeline.Outcome.TimedOut, timedOut.Outcome);
        Assert.Equal(VideoPipeline.Outcome.UpstreamFailed, failed.Outcome);
        Assert.NotEqual(timedOut.Outcome, failed.Outcome);
        // 文案也要不同 —— 分对类但说同一句话等于没分
        Assert.NotEqual(timedOut.Message, failed.Message);
    }

    [Fact]
    public async Task 超时后不再继续轮询()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        ScriptSubmitThenAlwaysRespond(fake, "running");

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions(
            timeout: TimeSpan.FromMilliseconds(100)));

        Assert.Equal(VideoPipeline.Outcome.TimedOut, result.Outcome);
        // 100ms / 5ms 间隔 ≈ 20 次上下；给它放宽到 60，但绝不能是 100
        Assert.True(result.PollCount < 60, $"超时后应当停手，实际轮了 {result.PollCount} 次");
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ 终态分类：取消
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 取消_判为_Cancelled_而不是失败()
    {
        // 「用户自己取消的」不该被报成错误 —— 他会莫名其妙。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        ScriptSubmitThenAlwaysRespond(fake, "running");

        await using var fx = Fixture.Create(fake);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(60));

        var result = await fx.Pipeline.RunAsync(
            VideoBody(), Fixture.FastOptions(timeout: TimeSpan.FromSeconds(30)), ct: cts.Token);

        Assert.Equal(VideoPipeline.Outcome.Cancelled, result.Outcome);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task 一开始就取消_立刻返回()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        ScriptSubmitThenAlwaysRespond(fake, "running");

        await using var fx = Fixture.Create(fake);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // ⚠ 提交那一步就会因为取消而抛 —— 实现应当把它兜成 SubmitRejected
        //   或 Cancelled，**绝不能冒到调用方**（那会让端点层 500）。
        var result = await fx.Pipeline.RunAsync(
            VideoBody(), Fixture.FastOptions(timeout: TimeSpan.FromSeconds(30)), ct: cts.Token);

        Assert.False(result.Ok);
        Assert.True(
            result.Outcome is VideoPipeline.Outcome.Cancelled or VideoPipeline.Outcome.SubmitRejected,
            $"取消应当有明确分类，实际 {result.Outcome}");
    }

    [Fact]
    public async Task 三种终止互不混淆()
    {
        // ★★ 契约原文：「超时与失败有明确分类（上游失败 / 超时 / 任务被取消），
        //    不静默挂起」。这条把三类一次性摆出来断言它们两两不同。
        var outcomes = new List<VideoPipeline.Outcome>();

        // ① 上游失败
        var f1 = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        f1.Script(TempKeyOk(), SubmitOk(), Query("task-vid-1", "failed", error: "x"));
        await using (var a = Fixture.Create(f1))
            outcomes.Add((await a.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions())).Outcome);

        // ② 超时
        var f2 = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        ScriptSubmitThenAlwaysRespond(f2, "running", tailSteps: 50);
        await using (var b = Fixture.Create(f2))
            outcomes.Add((await b.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions(
                timeout: TimeSpan.FromMilliseconds(80)))).Outcome);

        // ③ 取消
        var f3 = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        ScriptSubmitThenAlwaysRespond(f3, "running", tailSteps: 50);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(40));
        await using (var c = Fixture.Create(f3))
            outcomes.Add((await c.Pipeline.RunAsync(VideoBody(),
                Fixture.FastOptions(timeout: TimeSpan.FromSeconds(30)), ct: cts.Token)).Outcome);

        Assert.Equal(3, outcomes.Distinct().Count());
        Assert.Contains(VideoPipeline.Outcome.UpstreamFailed, outcomes);
        Assert.Contains(VideoPipeline.Outcome.TimedOut, outcomes);
        Assert.Contains(VideoPipeline.Outcome.Cancelled, outcomes);
    }

    // ══════════════════════════════════════════════════════════
    // ⑥ 查询抖动：单次失败不该放弃整个任务
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 单次查询失败_接着轮而不是放弃()
    {
        // 网络抖一下很正常。放弃的话用户会看到一个本来能成功的任务报失败。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            TempKeyOk(),
            SubmitOk(),
            Query("task-vid-1", "running"),
            UpstreamResponses.TooManyRequests(),                 // ← 抖一下
            Query("task-vid-1", "completed", "https://cos.example.com/v1.mp4"),
            VideoBinary());

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions());

        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public async Task 连续查询失败到上限_判失败而不是转到底()
    {
        // 「每次都 401」会一直转到超时，日志刷屏而结论一样。
        // 连续失败要有上限，且**分类要说清是查询失败**而不是生成失败。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        ScriptSubmitThenAlwaysRespond(fake, "running", tail: UpstreamResponses.Unauthorized("凭证失效"));

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(), Fixture.FastOptions(
            timeout: TimeSpan.FromMinutes(5)));

        Assert.False(result.Ok);
        Assert.Equal(VideoPipeline.Outcome.UpstreamFailed, result.Outcome);
        Assert.Contains("查询", result.Message, StringComparison.Ordinal);
        Assert.True(result.PollCount < 20, $"连续失败应当尽早收手，实际轮了 {result.PollCount} 次");
    }

    // ══════════════════════════════════════════════════════════
    // ⑦ 只轮询（给「提交就走 + 客户端自己问」那条路）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 只轮询一个已提交的任务_能拿到成品()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        fake.Script(
            TempKeyOk(),
            Query("task-vid-9", "pending"),
            Query("task-vid-9", "completed", "https://cos.example.com/v9.mp4"),
            VideoBinary());

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.PollTaskAsync("task-vid-9", Fixture.FastOptions());

        Assert.True(result.Ok, result.Message);
        Assert.Equal("task-vid-9", result.TaskId);
        Assert.Single(result.Files);
    }

    [Fact]
    public async Task 只轮询时空任务id被拒()
    {
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.PollTaskAsync("", Fixture.FastOptions());

        Assert.Equal(VideoPipeline.Outcome.SubmitRejected, result.Outcome);
        Assert.Equal(0, fake.Count);   // 一个请求都没发
    }

    // ══════════════════════════════════════════════════════════
    // ⑧ 可配参数与默认值
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 默认轮询间隔与超时是生产值()
    {
        // ★ 契约要求「轮询间隔与上限可配，默认值有注释说明理由」。
        //   这条把默认值钉死：改它们必须同时改这条测试，于是改动会进入 review。
        var defaults = VideoPipeline.Options.Default;

        Assert.Equal(TimeSpan.FromSeconds(3), defaults.PollInterval);
        Assert.Equal(TimeSpan.FromMinutes(15), defaults.Timeout);

        // 与 Core 的既有常量一致（MioraClient.VideoPollTimeout）
        Assert.Equal(MioraClient.VideoPollTimeout, defaults.Timeout);
    }

    [Fact]
    public async Task 轮询间隔真的被用上了()
    {
        // 用 5ms 间隔 + 200ms 超时，轮询次数应当远多于「1 次」；
        // 若间隔参数没被用上（比如硬编码 3 秒），这里只会查一两次就超时。
        var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Text("{}"));
        ScriptSubmitThenAlwaysRespond(fake, "running", tailSteps: 200);

        await using var fx = Fixture.Create(fake);

        var result = await fx.Pipeline.RunAsync(VideoBody(),
            new VideoPipeline.Options
            {
                PollInterval = TimeSpan.FromMilliseconds(5),
                Timeout = TimeSpan.FromMilliseconds(200),
            });

        Assert.Equal(VideoPipeline.Outcome.TimedOut, result.Outcome);
        Assert.True(result.PollCount >= 10, $"5ms 间隔下 200ms 应当轮很多次，实际 {result.PollCount}");
    }

    [Fact]
    public void 时长的中文描述()
    {
        Assert.Equal("15 分钟", VideoPipeline.Describe(TimeSpan.FromMinutes(15)));
        Assert.Equal("2 分钟", VideoPipeline.Describe(TimeSpan.FromMinutes(2)));
        Assert.Equal("30 秒", VideoPipeline.Describe(TimeSpan.FromSeconds(30)));
    }

    // ══════════════════════════════════════════════════════════
    // ⑨ 字段名两套都认（上游两个接口给的不一样）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 文件字段_snake_case_与_camelCase_都认()
    {
        var camel = JsonNode.Parse("""
        {"resultFiles":[{"signedUrl":"https://a/1.mp4","mimeType":"video/mp4","fileKey":"k1"}]}
        """);

        var snake = JsonNode.Parse("""
        {"result_files":[{"url":"https://a/2.mp4","mime_type":"video/mp4","file_key":"k2"}]}
        """);

        var a = VideoPipeline.ExtractFiles(camel);
        var b = VideoPipeline.ExtractFiles(snake);

        Assert.Single(a);
        Assert.Equal("https://a/1.mp4", a[0].Url);
        Assert.Equal("k1", a[0].FileKey);

        Assert.Single(b);
        Assert.Equal("https://a/2.mp4", b[0].Url);
        Assert.Equal("k2", b[0].FileKey);
    }

    [Fact]
    public void 没有URL的文件项被跳过()
    {
        var node = JsonNode.Parse("""
        {"resultFiles":[{"mimeType":"video/mp4"},{"signedUrl":"https://a/1.mp4"}]}
        """);

        var files = VideoPipeline.ExtractFiles(node);

        Assert.Single(files);
        Assert.Equal("https://a/1.mp4", files[0].Url);
    }

    [Fact]
    public void 没有文件字段时返回空表()
    {
        Assert.Empty(VideoPipeline.ExtractFiles(JsonNode.Parse("""{"status":"completed"}""")));
        Assert.Empty(VideoPipeline.ExtractFiles(null));
    }

    [Fact]
    public void 结果记录的_Ok_与_ReadyToDownload_语义不同()
    {
        // Ok           = 落盘完成（Files 有东西）
        // ReadyToDownload = 轮询到终态、还没下载（PendingFiles 有东西）
        // 混用会让调用方拿一个「只有 URL」的结果当成品去读盘。
        var pending = new VideoPipeline.VideoPipelineResult
        {
            Outcome = VideoPipeline.Outcome.Completed,
            PendingFiles = [new MediaFile("https://a/1.mp4", "video/mp4", "", 0)],
        };

        Assert.True(pending.ReadyToDownload);
        Assert.False(pending.Ok);          // 还没落盘

        var done = pending with
        {
            Files = [new DownloadedMedia("abc123.mp4", 100, "video/mp4")],
        };

        Assert.True(done.ReadyToDownload);
        Assert.True(done.Ok);
    }
}
