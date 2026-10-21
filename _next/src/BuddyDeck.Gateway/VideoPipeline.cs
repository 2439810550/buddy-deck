using System.Globalization;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 生视频的**完整生命周期**：提交 → 轮询 → 完成落盘 → 回读。
///
/// ## 为什么要有这一层（Core 里不是已经有 PollAsync 了吗）
///
/// `MioraClient.SubmitAsync` 内部确实会轮询到终态，但它是**一次性阻塞**的：
/// 调用方拿不到中间进度，也拿不到「任务 id」去让客户端稍后自己问。
/// 而视频要跑几分钟到十几分钟，这正是 <see cref="MioraClient.SubmitRawAsync"/>
/// 存在的理由（它的注释：把 HTTP 连接挂着不现实）。
///
/// 于是现状是**两半各管一半**：
///   · `SubmitRawAsync` 只提交，**不轮询** —— 调用方拿到 id 就没了下文
///   · `SubmitAsync` 轮询到底，但**不给你 id**
///
/// 这个类把两半接起来：**提交拿到 id → 用同一套 id 轮询 → 完成后落盘**，
/// 并且把「为什么结束」分成可区分的几类（成功 / 上游失败 / 超时 / 取消）。
///
/// ## ⚠ 它是纯逻辑，不碰 HttpContext
///
/// 端点（`MediaEndpoints`）负责解析请求与写响应；这里只管**任务生命周期**。
/// 分开的理由和 `ImageInput` 一样：这套状态机可以在普通 xUnit 里全量测，
/// 不需要起真 Kestrel，也不需要假上游之外的东西。
/// </summary>
public sealed partial class VideoPipeline
{
    private readonly MioraClient _miora;
    private readonly ILogger<VideoPipeline> _log;

    public VideoPipeline(MioraClient miora, ILogger<VideoPipeline> log)
    {
        _miora = miora;
        _log = log;
    }

    /// <summary>
    /// 轮询参数。**可配**，默认值见各字段注释。
    ///
    /// 做成一个可注入的 record 而不是常量：测试要把它压到毫秒级
    /// （否则一个「等 15 分钟看看会不会超时」的用例没人跑得起）。
    /// </summary>
    public sealed record Options
    {
        /// <summary>
        /// 两次查询之间等多久。默认 **3 秒**。
        ///
        /// ## 为什么是 3 秒
        ///
        /// 与 <c>MioraClient.PollInterval</c> 保持一致（那里的注释：
        /// 「旧版图片 2.5 秒、视频 3 秒，这里统一取 3 秒」）。
        /// 不选更短的两个理由：
        ///   · 视频任务本身要几分钟——2 秒一次查询在 10 分钟里就是 300 次请求，
        ///     纯属给上游添堵，而进度感知上没有任何区别
        ///   · 上游的 query-task 是有配额的接口，轮询太密容易被限流
        ///     而**限流会让我们误判任务失败**（查询失败重试，超时了任务其实还在跑）
        /// </summary>
        public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

        /// <summary>
        /// 轮询总上限。默认 **15 分钟** —— 与 <c>MioraClient.VideoPollTimeout</c>
        /// 同一个值、同一个理由：视频要渲染几百帧，用图片那 90 秒必然失败。
        /// </summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);

        /// <summary>默认参数。</summary>
        public static Options Default { get; } = new();
    }

    // ══════════════════════════════════════════════════════════
    // 终态分类
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 任务**为什么结束**。
    ///
    /// 刻意分成这几类而不是「成功/失败」两类 —— 因为它们对用户是**不同的事**：
    /// ```
    /// Completed  成功了，有文件
    /// UpstreamFailed  上游明确说这个任务失败了（带原因）→ 用户该改提示词/换模型
    /// TimedOut   我们等不下去了，任务**可能还在跑** → 用户该稍后重试而不是重写提示词
    /// Cancelled  用户自己取消的 → 什么都别提示
    /// ```
    /// **尤其 `TimedOut` 与 `UpstreamFailed` 必须分开**：前者是「我们放弃了」，
    /// 后者是「它真的坏了」。混在一起的话，用户在一个 12 分钟的视频上
    /// 看到「生成失败」，会把提示词改到面目全非 —— 而那个任务其实快好了。
    /// </summary>
    public enum Outcome
    {
        /// <summary>成功，<see cref="VideoPipelineResult.Files"/> 非空。</summary>
        Completed = 0,

        /// <summary>上游明确报失败（<c>status: failed</c>）。</summary>
        UpstreamFailed = 1,

        /// <summary>我们在 <see cref="Options.Timeout"/> 内没等到终态。**任务未必失败。**</summary>
        TimedOut = 2,

        /// <summary>调用方取消（客户端断开 / 用户点了取消）。</summary>
        Cancelled = 3,

        /// <summary>上游报 cancelled。</summary>
        UpstreamCancelled = 4,

        /// <summary>提交本身就没成功（拿不到任务 id）。</summary>
        SubmitRejected = 5,
    }

    /// <summary>一次视频任务的最终结果。</summary>
    public sealed record VideoPipelineResult
    {
        /// <summary>上游任务 id（提交失败时为空串）。</summary>
        public string TaskId { get; init; } = "";

        /// <summary>为什么结束。</summary>
        public Outcome Outcome { get; init; }

        /// <summary>上游最后报的状态字符串（诊断用，原样保留）。</summary>
        public string UpstreamStatus { get; init; } = "";

        /// <summary>成功时的产物（已落盘；未落盘前只带 URL，见 <see cref="PendingFiles"/>）。</summary>
        public IReadOnlyList<DownloadedMedia> Files { get; init; } = [];

        /// <summary>
        /// 上游给的**待下载**产物（只有轮询阶段用得到，落盘后换成 <see cref="Files"/>）。
        ///
        /// ⚠ 为什么不能塞进 <see cref="Files"/>：`DownloadedMedia` 的第一个字段是
        /// **落盘后的文件名**，而这时候我们手上只有上游的签名 URL ——
        /// 拿 URL 当文件名用会造出一个永远读不出来的路径（`/v1/images/file/<url>`）。
        /// 两者分开，语义才不会串。
        /// </summary>
        public IReadOnlyList<MediaFile> PendingFiles { get; init; } = [];

        /// <summary>失败原因（给人看的一句话；成功时为空串）。</summary>
        public string Message { get; init; } = "";

        /// <summary>轮询了几次（诊断用）。</summary>
        public int PollCount { get; init; }

        /// <summary>从提交到结束花了多久。</summary>
        public TimeSpan Elapsed { get; init; }

        /// <summary>成功了没有（有**落盘后**的产物才算）。</summary>
        public bool Ok => Outcome == Outcome.Completed && Files.Count > 0;

        /// <summary>轮询到终态但还没下载时的判断依据。</summary>
        public bool ReadyToDownload => Outcome == Outcome.Completed && PendingFiles.Count > 0;
    }

    // ══════════════════════════════════════════════════════════
    // 全程：提交 + 轮询 + 落盘
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 跑完整条链路：提交 → 轮询到终态 → 下载落盘。
    ///
    /// ## 为什么「落盘」在这一层而不是端点里
    ///
    /// 因为落盘的**时机**是链路的一环（终态是 completed 才落），
    /// 而且落盘会失败（`DOWNLOAD_FAILED`）—— 那也要作为一种结局反馈上去。
    /// 放在端点里的话，端点就得知道「什么时候该下载」，等于把状态机散落两处。
    ///
    /// ## 上游 URL 只有 1 小时
    ///
    /// `MediaFile.ExpiresAtUnixMs` 是上游给的上限。**这里不做「过期就不下」的判断** ——
    /// 那个时刻是上游报的，它偶尔不给（0 = 没给），给了也可能不准。
    /// 真过期了 `DownloadAsync` 自然失败，报得比我们猜的准。
    /// </summary>
    public async Task<VideoPipelineResult> RunAsync(
        JsonObject upstreamBody,
        Options? options = null,
        Func<JsonObject, CancellationToken, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        var opt = options ?? Options.Default;
        var startedAt = DateTimeOffset.UtcNow;

        MediaTaskResult submitted;
        try
        {
            submitted = await _miora.SubmitRawAsync("video", upstreamBody, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ⚠ 这一条是实测踩出来的：`HttpClient` 在 token 已取消时抛的是
            //   `TaskCanceledException`（继承自 `OperationCanceledException`），
            //   **不是** `MioraException` —— 只 catch MioraException 的话它会
            //   原样冒到调用方，端点层就变成一个 500，而不是「已取消」。
            //
            //   契约要求「取消要有明确分类、不静默挂起」，所以这里必须接住。
            return new VideoPipelineResult
            {
                Outcome = Outcome.Cancelled,
                Message = "已取消（提交阶段就被取消）",
                Elapsed = DateTimeOffset.UtcNow - startedAt,
            };
        }
        catch (MioraException ex)
        {
            return new VideoPipelineResult
            {
                Outcome = Outcome.SubmitRejected,
                Message = ex.Message,
                Elapsed = DateTimeOffset.UtcNow - startedAt,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // 提交时网络就断了 —— 同样不能冒出去
            return new VideoPipelineResult
            {
                Outcome = Outcome.SubmitRejected,
                Message = $"提交视频任务失败：{ex.Message}",
                Elapsed = DateTimeOffset.UtcNow - startedAt,
            };
        }

        if (_log.IsEnabled(LogLevel.Information))
        {
            LogVideoSubmitted(submitted.TaskId, submitted.Model);
        }

        var polled = await PollToTerminalAsync(submitted.TaskId, opt, onProgress, ct).ConfigureAwait(false);

        if (polled.Outcome != Outcome.Completed) return polled with { Elapsed = DateTimeOffset.UtcNow - startedAt };

        return await DownloadAllAsync(polled, ct).ConfigureAwait(false) with
        {
            Elapsed = DateTimeOffset.UtcNow - startedAt,
        };
    }

    /// <summary>
    /// 把 <see cref="VideoPipelineResult.PendingFiles"/> 逐个下载落盘。
    ///
    /// 两条路径（<see cref="RunAsync"/> 与 <see cref="PollTaskAsync"/>）共用这一段 ——
    /// 各写一份的话，「下载失败怎么归类」会在两处慢慢漂移。
    /// </summary>
    private async Task<VideoPipelineResult> DownloadAllAsync(VideoPipelineResult polled, CancellationToken ct)
    {
        var downloaded = new List<DownloadedMedia>();

        foreach (var file in polled.PendingFiles)
        {
            try
            {
                downloaded.Add(await _miora.DownloadAsync(file.Url, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return polled with
                {
                    Outcome = Outcome.Cancelled,
                    Message = "已取消（已下载的部分保留）",
                    Files = downloaded,
                };
            }
            catch (MioraException ex)
            {
                // ⚠ 下载失败**不是**上游生成失败 —— 任务本身成功了。
                //   这里如实说「生成好了但存不下来」，比笼统的失败有用：
                //   用户据此知道重试可能就好了，而不用改提示词。
                return polled with
                {
                    Outcome = Outcome.UpstreamFailed,
                    Message = $"视频生成好了，但保存到本地失败：{ex.Message}",
                    Files = downloaded,
                };
            }
        }

        return polled with { Files = downloaded };
    }

    // ══════════════════════════════════════════════════════════
    // 只轮询（给「提交就走 + 客户端自己问」那条路用）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 轮询一个**已经提交**的任务直到终态（含落盘）。
    ///
    /// 这是 <see cref="MioraClient.SubmitRawAsync"/> 缺的那一半 ——
    /// 客户端先拿到 id，过一会儿再来问「好了没」，走的就是这里。
    /// </summary>
    public async Task<VideoPipelineResult> PollTaskAsync(
        string taskId,
        Options? options = null,
        Func<JsonObject, CancellationToken, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return new VideoPipelineResult
            {
                Outcome = Outcome.SubmitRejected,
                Message = "缺少任务 id",
            };
        }

        var opt = options ?? Options.Default;
        var startedAt = DateTimeOffset.UtcNow;

        var polled = await PollToTerminalAsync(taskId, opt, onProgress, ct).ConfigureAwait(false);
        if (polled.Outcome != Outcome.Completed) return polled with { Elapsed = DateTimeOffset.UtcNow - startedAt };

        return await DownloadAllAsync(polled, ct).ConfigureAwait(false) with
        {
            Elapsed = DateTimeOffset.UtcNow - startedAt,
        };
    }

    // ══════════════════════════════════════════════════════════
    // 轮询状态机
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 轮询到终态。
    ///
    /// ## 三条终止路径，每一条都必须有出口
    ///
    /// ```
    /// ① 上游报终态   completed / failed / cancelled
    /// ② 到点         超过 Options.Timeout → TimedOut
    /// ③ 取消         ct 被触发 → Cancelled
    /// ```
    /// **「不静默挂起」的关键是第 ② 条**：没有超时的话，一个永远停在
    /// `running` 的任务会让调用方一直等下去（客户端那边表现为「卡住了」，
    /// 而服务端连一条日志都没有）。
    ///
    /// ## 单次查询失败不致命
    ///
    /// 网络抖一下、上游 503 一下都很正常，**接着轮**就是。与
    /// `MioraClient.PollAsync` 同样的策略（那里的注释：
    /// 「单次查询失败不致命（网络抖一下很正常）—— 接着轮，别放弃整个任务」）。
    /// ⚠ 但**取消要原样抛** —— 那是客户端走了，不是网络抖动。
    /// </summary>
    private async Task<VideoPipelineResult> PollToTerminalAsync(
        string taskId,
        Options opt,
        Func<JsonObject, CancellationToken, Task>? onProgress,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + opt.Timeout;
        var polls = 0;
        var lastStatus = "pending";
        var pollFailures = 0;

        while (true)
        {
            // ── ③ 取消 ──────────────────────────────────────
            if (ct.IsCancellationRequested)
            {
                return new VideoPipelineResult
                {
                    TaskId = taskId,
                    Outcome = Outcome.Cancelled,
                    UpstreamStatus = lastStatus,
                    Message = "已取消",
                    PollCount = polls,
                };
            }

            // ── ② 超时 ──────────────────────────────────────
            //
            // ⚠ 判在**循环开头**而不是循环尾：末尾判的话，一个
            //   `Timeout = 0`（测试里会这么用）会先跑一轮再报超时 ——
            //   行为就变成「至少查一次」，与「上限」的语义不符。
            //   开头判之后，Timeout=0 表示「一次都不查就超时」，可预期。
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return new VideoPipelineResult
                {
                    TaskId = taskId,
                    Outcome = Outcome.TimedOut,
                    UpstreamStatus = lastStatus,
                    // ⚠ 文案要点：明确说「任务可能还在跑」，否则用户会以为白做了
                    Message = $"等待视频生成超时（{Describe(opt.Timeout)}），任务可能还在上游继续跑",
                    PollCount = polls,
                };
            }

            // ── 查一次 ──────────────────────────────────────
            JsonNode? entry;
            try
            {
                entry = await _miora.QueryAsync("video", taskId, ct).ConfigureAwait(false);
                pollFailures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new VideoPipelineResult
                {
                    TaskId = taskId,
                    Outcome = Outcome.Cancelled,
                    UpstreamStatus = lastStatus,
                    Message = "已取消",
                    PollCount = polls,
                };
            }
            catch (MioraException ex)
            {
                pollFailures++;
                if (_log.IsEnabled(LogLevel.Debug)) LogPollFailed(polls, ex.Message);

                // ⚠ 上游连续失败也要有个上限，否则「每次查询都 401」会一直
                //   转到超时，日志刷屏而结论一样。这里的 10 次是**经验值**：
                //   3 秒一次就是 30 秒的连续失败，足够区分「抖一下」与
                //   「凭证彻底失效」。
                if (pollFailures >= MaxConsecutivePollFailures)
                {
                    return new VideoPipelineResult
                    {
                        TaskId = taskId,
                        Outcome = Outcome.UpstreamFailed,
                        UpstreamStatus = lastStatus,
                        Message = $"连续 {pollFailures} 次查询任务状态都失败：{ex.Message}",
                        PollCount = polls,
                    };
                }

                // 抖一下就接着轮（理由见下面 HttpRequestException 那个 catch 的注释）
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                pollFailures++;
                if (_log.IsEnabled(LogLevel.Debug)) LogPollFailed(polls, ex.Message);

                if (pollFailures >= MaxConsecutivePollFailures)
                {
                    return new VideoPipelineResult
                    {
                        TaskId = taskId,
                        Outcome = Outcome.UpstreamFailed,
                        UpstreamStatus = lastStatus,
                        Message = $"连续 {pollFailures} 次查询任务状态都失败：{ex.Message}",
                        PollCount = polls,
                    };
                }

                // ⚠ 这一句不是多余的：没有它编译器会报 CS0165（entry 可能未赋值）——
                //   因为 catch 块「正常结束」时 entry 确实没值。
                //   而它同时也是**正确的控制流**：抖了一下就接着轮，
                //   不要拿一个空 entry 去判状态（那会被 ClassifyTerminal
                //   当成「不是终态」，逻辑上碰巧对，但读起来是错的）。
                continue;
            }

            polls++;

            // 查到了：先看状态
            var status = await ObserveAsync(entry, taskId, polls, lastStatus, onProgress, ct).ConfigureAwait(false);
            if (status is not null)
            {
                lastStatus = status;

                var terminal = ClassifyTerminal(status, entry);
                if (terminal is not null)
                {
                    return terminal with { TaskId = taskId, PollCount = polls };
                }
            }
            else
            {
                // ⚠ `QueryAsync` 返回 null = 上游**查不到这个 id**。
                //   这与「查询失败」不同：失败是网络/权限问题，这里是上游说
                //   「没有这个任务」。继续轮下去毫无意义 —— 它不会凭空出现。
                return new VideoPipelineResult
                {
                    TaskId = taskId,
                    Outcome = Outcome.SubmitRejected,
                    UpstreamStatus = lastStatus,
                    Message = "上游查不到这个任务（id 可能已过期或从未提交成功）",
                    PollCount = polls,
                };
            }

            // ── 睡一会儿（可取消） ───────────────────────────
            //
            // ⚠ 用 Task.Delay(TimeSpan, CancellationToken) 而不是
            //   `await Task.Delay(x); ct.ThrowIfCancellationRequested();` ——
            //   后者在最长 3 秒的窗口里不响应取消，客户端断开后我们还要
            //   空转一轮。这里让取消**立刻**生效。
            //
            // ⚠ 睡眠时长要夹到「剩余时间」：不夹的话，距超时还剩 1 秒时
            //   仍然会睡满一个 PollInterval，超时判定的误差最大到一个间隔。
            var remaining = deadline - DateTimeOffset.UtcNow;
            var wait = remaining < opt.PollInterval ? remaining : opt.PollInterval;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // ⚠ 取消**不从这里冒出去** —— 契约要求它是一类**分类**，
                    //   不是异常。冒出去的话端点层会把它变成 500（或者
                    //   被某个宽泛的 catch 吞掉变成「没反应」）。
                    return new VideoPipelineResult
                    {
                        TaskId = taskId,
                        Outcome = Outcome.Cancelled,
                        UpstreamStatus = lastStatus,
                        Message = "已取消",
                        PollCount = polls,
                    };
                }
            }
        }
    }

    /// <summary>连续多少次查询失败就放弃。见 <see cref="PollToTerminalAsync"/> 里的注释。</summary>
    private const int MaxConsecutivePollFailures = 10;

    /// <summary>
    /// 把一次查询结果报给调用方，返回状态字符串。
    ///
    /// @returns 状态字符串；**查不到这个任务时返回 null**。
    ///
    /// `onProgress` 是**可选**的：不传就完全不构造进度对象，
    /// 省掉每次轮询的一次分配。
    ///
    /// ## ⚠ 「查不到」的判据是实测出来的，不是猜的
    ///
    /// `MioraClient.PickTaskEntry`（`:687-693`）在按 id 查不到时
    /// **会回退成返回整个 `data`** —— 于是上游回 `{"data":{}}` 时，
    /// 我们拿到的是一个**空的 JsonObject**，而不是 null。
    ///
    /// 所以「查不到」必须**同时**满足：不是 JsonObject（真 null），
    /// **或者**是个连 `status` 字段都没有的空对象。
    /// 只看 `is not JsonObject` 的话，空对象会被当成一个状态为
    /// "unknown" 的**有效响应**，于是我们一直轮到超时 —— 实测表现为
    /// 一条本该 2ms 结束的用例跑了 30 秒。
    /// </summary>
    private static async Task<string?> ObserveAsync(
        JsonNode? entry,
        string taskId,
        int pollCount,
        string previousStatus,
        Func<JsonObject, CancellationToken, Task>? onProgress,
        CancellationToken ct)
    {
        if (entry is not JsonObject obj) return null;

        // 空对象 = 上游没这个任务（见上面的说明）
        if (obj["status"] is not JsonValue) return null;

        var status = obj["status"]!.GetValue<string>();

        if (onProgress is not null)
        {
            var progress = new JsonObject
            {
                ["id"] = taskId,
                ["status"] = status,
                ["poll_count"] = pollCount,
                ["changed"] = !string.Equals(status, previousStatus, StringComparison.Ordinal),
            };

            if (obj["progress"] is JsonValue pv) progress["progress"] = pv.DeepClone();
            if (obj["model"]?.GetValue<string>() is { Length: > 0 } model) progress["model"] = model;

            await onProgress(progress, ct).ConfigureAwait(false);
        }

        return status;
    }

    /// <summary>
    /// 状态字符串 → 终态结果；**不是终态时返回 null**（继续轮）。
    ///
    /// 上游的状态词表比我们写的宽（`pending` / `running` / `processing` /
    /// `queued` / `completed` / `succeeded` / `failed` / `error` / `cancelled` …）。
    /// 这里**只认明确的终态词**，其余一律当「还在跑」—— 反过来（把不认识的
    /// 状态当失败）会让一个正常跑着的任务被误报成失败。
    /// </summary>
    private static VideoPipelineResult? ClassifyTerminal(string status, JsonNode? entry) => status switch
    {
        "completed" or "succeeded" or "success" => Completed(entry, status),

        "failed" or "error" => new VideoPipelineResult
        {
            Outcome = Outcome.UpstreamFailed,
            UpstreamStatus = status,
            Message = (entry?["errorMessage"]?.GetValue<string>()
                       ?? entry?["error_message"]?.GetValue<string>())
                      ?? "上游没有给出失败原因",
        },

        "cancelled" or "canceled" => new VideoPipelineResult
        {
            Outcome = Outcome.UpstreamCancelled,
            UpstreamStatus = status,
            Message = "任务在上游被取消",
        },

        _ => null,
    };

    /// <summary>
    /// 完成态 → 产物列表。
    ///
    /// ⚠ 有一条容易被忽略的分支：**状态是 completed 但一个文件都没有**。
    /// 那不叫成功 —— 端点层的 `VideoGenerationsAsync` 也处理了这种情况
    /// （「上游没有返回视频」）。这里把它归到 <see cref="Outcome.UpstreamFailed"/>，
    /// 而不是让 `Ok` 因为 `Files.Count > 0` 隐式为 false 却报 `Completed` ——
    /// **分类要能自己说清自己**，不能靠调用方再补一个判断。
    /// </summary>
    private static VideoPipelineResult Completed(JsonNode? entry, string status)
    {
        var files = ExtractFiles(entry);

        if (files.Count == 0)
        {
            return new VideoPipelineResult
            {
                Outcome = Outcome.UpstreamFailed,
                UpstreamStatus = status,
                Message = "任务报完成，但上游没有返回视频文件",
            };
        }

        return new VideoPipelineResult
        {
            Outcome = Outcome.Completed,
            UpstreamStatus = status,
            PendingFiles = files,
        };
    }

    /// <summary>
    /// 从查询结果里抽出文件引用。
    ///
    /// 字段名有 **snake_case 和 camelCase 两套**（上游两个接口给的不一样，
    /// 见 `MioraClient.Normalize` 同样两套都认）。
    /// </summary>
    internal static List<MediaFile> ExtractFiles(JsonNode? entry)
    {
        var result = new List<MediaFile>();

        var files = entry?["resultFiles"] as JsonArray ?? entry?["result_files"] as JsonArray;
        if (files is null) return result;

        foreach (var item in files)
        {
            if (item is not JsonObject file) continue;

            var url = file["signedUrl"]?.GetValue<string>() ?? file["url"]?.GetValue<string>() ?? "";
            if (url.Length == 0) continue;

            result.Add(new MediaFile(
                url,
                file["mimeType"]?.GetValue<string>() ?? file["mime_type"]?.GetValue<string>() ?? "video/mp4",
                file["fileKey"]?.GetValue<string>() ?? file["file_key"]?.GetValue<string>() ?? "",
                ReadLong(file["expiresAt"]) ?? ReadLong(file["expires_at"]) ?? 0));
        }

        return result;
    }

    private static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<string>(out var s) && long.TryParse(s, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    /// <summary>把时长说成人话（进错误提示）。</summary>
    internal static string Describe(TimeSpan span)
        => span.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes} 分钟")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalSeconds} 秒");

    [LoggerMessage(Level = LogLevel.Information, Message = "[video] 任务已提交 {TaskId}，模型 {Model}")]
    private partial void LogVideoSubmitted(string taskId, string model);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[video] 第 {Attempt} 次查询失败：{Message}")]
    private partial void LogPollFailed(int attempt, string message);
}
