using System.Net;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 每日签到的测试（M11，对齐旧版 `checkin.js`）。
///
/// ## 全部走假上游
///
/// 一次都不打真实上游 —— 理由和别处一样：慢、要网络、会消耗全队共用的配额，
/// 而且**错误路径根本测不了**（想让上游回 401 得先把 token 弄失效）。
///
/// ## 假上游是<see cref="FakeUpstream"/>（Core.Tests 自己那个）
///
/// 不是 `BuddyDeck.TestSupport` 的 `FakeUpstreamHandler` —— Core.Tests 不引用
/// TestSupport（后者反向依赖 Gateway，会把它拖进 Core 的测试依赖）。
/// 这个任务不改任何 csproj，所以用本地这个。两者记录的东西是同构的
/// （方法 / URL / 头 / 体），断言写法一致。
///
/// ## 为什么要断言「次数」
///
/// 幂等的**唯一可信证据就是请求次数**。「结果是对的」证明不了没重复打上游
/// —— 一个每次都真打、但每次都返回 AlreadyClaimed 的实现，行为上完全说得通，
/// 却没有省下任何一次往返。所以每个幂等用例都断言 <c>Requests.Count</c>。
/// </summary>
public class CheckinTests
{
    private static readonly Credential TestCredential = new()
    {
        AccessToken = "ck-token-abc",
        RefreshToken = "ck-refresh",
        Uid = "u1",
        EnterpriseId = "e1",
        Domain = "workbuddy.cn",
        Label = "签到测试号",
    };

    /// <summary>一个可控的时钟 —— 让「今天/昨天」可测，不用真的等一天。</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Anchor = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static (Checkin Checkin, FakeUpstream Fake, FixedTime Clock) Make(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        DateTimeOffset? now = null)
    {
        var fake = new FakeUpstream(handler);
        var clock = new FixedTime(now ?? Anchor);
        var checkin = new Checkin(
            new HttpClient(fake),
            UpstreamVariant.China,
            NullLogger<Checkin>.Instance,
            clock);
        return (checkin, fake, clock);
    }

    /// <summary>造一份上游的签到状态 data（snake_case，照抄真实字段名）。</summary>
    private static JsonObject StatusData(
        bool active = true,
        bool todayCheckedIn = false,
        int streakDays = 0,
        double todayCredit = 0)
        => new()
        {
            ["active"] = active,
            ["today_checked_in"] = todayCheckedIn,
            ["streak_days"] = streakDays,
            ["daily_credit"] = 1,
            ["today_credit"] = todayCredit,
            ["total_credits"] = 42,
            ["week_checkin_days"] = 3,
            ["week_progress"] = new JsonArray(true, true, false, false, false, false, false),
            ["checkin_dates"] = new JsonArray("2026-03-09", "2026-03-10"),
            ["activity_name"] = "每日签到",
            ["theme_name"] = "春季",
            ["season"] = 2,
            ["start_time"] = "2026-03-01T00:00:00+08:00",
            ["end_time"] = "2026-04-01T00:00:00+08:00",
            ["claim_button_text"] = "立即签到",
            ["is_streak_day"] = true,
            ["streak_bonus_days"] = 7,
            ["streak_bonus_credit"] = 5,
        };

    private static HttpResponseMessage StatusOk(JsonObject data)
        => FakeUpstream.Ok(FakeUpstream.Envelope(0, data));

    /// <summary>
    /// 一个**有状态**的假上游 —— 签到之后状态就变了。
    ///
    /// ## 为什么必须是「有状态的」，而不是每条路径回固定响应
    ///
    /// 第一版这里每条路径都回同一个固定响应（`today_checked_in` 永远 false）。
    /// 结果正常路径变成「status 说没签 → 签 → 回查 status **还是说没签** →
    /// 又签一遍」—— 三次请求，测试红了。
    ///
    /// 那不是实现的 bug，是**假上游不真实**：真实上游签过之后
    /// `today_checked_in` 就变 true 了。假上游不建模这个，就等于在
    /// 「一个会说谎的上游」上测幂等 —— 测出来的结论没有意义。
    ///
    /// 所以这里把「签过」这个状态存起来，让假上游和真上游一样**随请求变化**。
    /// </summary>
    private sealed class SigningUpstream(bool active = true, int streakAfterClaim = 6)
    {
        private bool _claimed;
        private int _streak = streakAfterClaim;

        /// <summary>上游收到了几次请求 —— 幂等的唯一可信证据。</summary>
        public int Count { get; private set; }

        public List<string> Paths { get; } = [];

        /// <summary>
        /// 「日切了」—— 上游那边进入新的一天，今天已签的记号清掉。
        ///
        /// 真实上游按自己的日期算 `today_checked_in`；测试里时钟是**我们自己
        /// 往前拨**的，上游不知道 —— 所以这一步是假上游必须补的建模。
        /// 少了它，「跨天重签」会退化成「上游说已签」。
        /// </summary>
        public void NewDay() => _claimed = false;

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Count++;
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);

            if (path.EndsWith("/daily-checkin", StringComparison.Ordinal))
            {
                // 签到成功，但**不回状态** —— 逼调用方回查一次（旧版也是这么做的）
                _claimed = true;
                return FakeUpstream.Ok(FakeUpstream.Envelope(0, null));
            }

            return StatusOk(StatusData(
                active: active,
                todayCheckedIn: _claimed,
                streakDays: _claimed ? _streak : 0));
        }
    }

    // ══ ① 成功路径 ═══════════════════════════════════════════════

    [Fact]
    public async Task 成功签到走_daily_checkin_路径并带计费头()
    {
        // 第一次 status → 没签；第二次 daily-checkin → 签成功
        var (checkin, fake, _) = Make(_ => StatusOk(StatusData()));

        var result = await checkin.ClaimAsync(TestCredential);

        Assert.Equal(CheckinOutcome.Claimed, result.Outcome);
        Assert.True(result.Ok);
        Assert.True(result.UpstreamCalled);

        var claim = fake.Requests.First(r => r.Url.EndsWith("/v2/billing/meter/daily-checkin", StringComparison.Ordinal));
        Assert.Equal("POST", claim.Method);
        Assert.Equal("https://www.codebuddy.cn/v2/billing/meter/daily-checkin", claim.Url);

        // 复用已有计费头，**不另起一套凭据机制**
        Assert.Equal("Bearer ck-token-abc", claim.Headers["Authorization"].Single());
        Assert.Equal("SaaS", claim.Headers["X-Product"].Single());
        Assert.Equal("e1", claim.Headers["X-Enterprise-Id"].Single());

        // ⚠ 不带 X-IDE-* 归属族 —— 那是 chat / 探活专属，签到带上会把用量归属算乱
        Assert.DoesNotContain("X-IDE-Type", claim.Headers.Keys);
        Assert.DoesNotContain("X-IDE-Name", claim.Headers.Keys);
    }

    [Fact]
    public async Task 签到请求体是空_json_对象()
    {
        var upstream = new SigningUpstream();
        var (checkin, fake, _) = Make(upstream.Respond);

        await checkin.ClaimAsync(TestCredential);

        // 旧版 checkin.js 全是空 body `{}`
        Assert.NotEmpty(fake.Requests);
        Assert.All(fake.Requests, r => Assert.Equal("{}", r.Body));
    }

    [Fact]
    public async Task 签到成功后回查状态拿到准确天数()
    {
        // 假上游**签到后不回状态** —— 这时必须回查一次（旧版 checkin.js:145 的做法）
        var upstream = new SigningUpstream(streakAfterClaim: 8);
        var (checkin, _, _) = Make(upstream.Respond);

        var result = await checkin.ClaimAsync(TestCredential);

        Assert.Equal(CheckinOutcome.Claimed, result.Outcome);
        Assert.NotNull(result.Status2);
        Assert.Equal(8, result.Status2!.StreakDays);
        Assert.Contains("连续 8 天", result.Message, StringComparison.Ordinal);

        // 2 次：daily-checkin + 回查 status。
        // ClaimAsync **不预查状态** —— 那是 EnsureCheckedInAsync 的事。
        Assert.Equal(2, upstream.Count);
        Assert.Equal(
            ["/v2/billing/meter/daily-checkin", "/v2/billing/meter/checkin-activity-status"],
            upstream.Paths);
    }

    [Fact]
    public async Task 状态字段_snake_case_全部归一化()
    {
        var (checkin, _, _) = Make(_ => StatusOk(StatusData(todayCheckedIn: true, streakDays: 5, todayCredit: 2)));

        var status = await checkin.FetchStatusAsync(TestCredential);

        Assert.NotNull(status);
        Assert.True(status!.Active);
        Assert.True(status.TodayCheckedIn);
        Assert.Equal(5, status.StreakDays);
        Assert.Equal(2, status.TodayCredit);
        Assert.Equal(42, status.TotalCredits);
        Assert.Equal(3, status.WeekCheckinDays);
        Assert.Equal([true, true, false, false, false, false, false], status.WeekProgress);
        Assert.Equal(["2026-03-09", "2026-03-10"], status.CheckinDates);
        Assert.Equal("每日签到", status.ActivityName);
        Assert.Equal("立即签到", status.ClaimButtonText);
        Assert.True(status.IsStreakDay);
        Assert.Equal(7, status.StreakBonusDays);
        Assert.Equal(5, status.StreakBonusCredit);
        Assert.Equal(2, status.Season);
    }

    // ══ ② 已签到（幂等）═══════════════════════════════════════════

    [Fact]
    public async Task 上游说今天已签则不执行签到()
    {
        var (checkin, fake, _) = Make(_ => StatusOk(StatusData(todayCheckedIn: true, streakDays: 4)));

        var result = await checkin.EnsureCheckedInAsync(TestCredential);

        Assert.Equal(CheckinOutcome.AlreadyClaimed, result.Outcome);
        Assert.True(result.Ok);
        Assert.Contains("连续 4 天", result.Message, StringComparison.Ordinal);

        // 只打了一次 status，**没打 daily-checkin**
        Assert.Equal(1, fake.Requests.Count);
        Assert.Equal("/v2/billing/meter/checkin-activity-status", fake.Requests.Single().Url is var u ? new Uri(u).AbsolutePath : "");
    }

    [Fact]
    public async Task 本地记录说今天已签则一个字节都不发()
    {
        var (checkin, fake, _) = Make(_ => StatusOk(StatusData()));

        // 时钟锚点是 2026-03-10T12:00Z
        var result = await checkin.EnsureCheckedInAsync(TestCredential, "2026-03-10T08:30:00.000Z");

        Assert.Equal(CheckinOutcome.AlreadyClaimed, result.Outcome);
        Assert.False(result.UpstreamCalled);

        // ← 这就是幂等的证据：**零次上游请求**
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task 本地记录是昨天则照常签到()
    {
        var upstream = new SigningUpstream();
        var (checkin, _, _) = Make(upstream.Respond);

        var result = await checkin.EnsureCheckedInAsync(TestCredential, "2026-03-09T23:59:59.000Z");

        Assert.Equal(CheckinOutcome.Claimed, result.Outcome);

        // 3 次：status（没签）→ daily-checkin → status 回查
        Assert.Equal(3, upstream.Count);
        Assert.Contains("/v2/billing/meter/daily-checkin", upstream.Paths);
    }

    [Fact]
    public async Task 本地记录缺失或坏掉时不误判为已签()
    {
        // 这些「读不出来」的输入 —— 都必须是**不短路**，宁可多打一次上游
        foreach (var bogus in new[] { null, "", "   ", "not-a-date", "2026-13-45" })
        {
            var upstream = new SigningUpstream();
            var (checkin, _, _) = Make(upstream.Respond);

            var result = await checkin.EnsureCheckedInAsync(TestCredential, bogus);

            Assert.Equal(CheckinOutcome.Claimed, result.Outcome);

            // 真签了 —— 这是最重要的：坏记录不能让签到被静默跳过
            Assert.Contains("/v2/billing/meter/daily-checkin", upstream.Paths);
        }
    }

    [Fact]
    public void 日期判定按_UTC_日切()
    {
        var (checkin, _, clock) = Make(_ => StatusOk(StatusData()));

        // 同一天的不同时刻
        Assert.True(checkin.IsToday("2026-03-10T00:00:00.000Z"));
        Assert.True(checkin.IsToday("2026-03-10T23:59:59.999Z"));
        Assert.True(checkin.IsToday("2026-03-10T20:00:00+08:00")); // = 12:00Z，同一天

        // 跨了一天的两侧
        Assert.False(checkin.IsToday("2026-03-09T23:59:59.999Z"));
        Assert.False(checkin.IsToday("2026-03-11T00:00:00.000Z"));

        // 时钟走过日切之后，同一条记录变成「昨天」
        clock.Now = Anchor.AddDays(1);
        Assert.False(checkin.IsToday("2026-03-10T12:00:00.000Z"));
    }

    [Fact]
    public async Task 没有进行中的活动时不算失败也不算签到()
    {
        var (checkin, fake, _) = Make(_ => StatusOk(StatusData(active: false)));

        var result = await checkin.EnsureCheckedInAsync(TestCredential);

        Assert.Equal(CheckinOutcome.NoActivity, result.Outcome);
        Assert.Equal(Checkin.NoActivityKind, result.ErrorKind);
        Assert.Equal(1, fake.Requests.Count);
    }

    [Fact]
    public async Task 跨天之后重新签一次()
    {
        var upstream = new SigningUpstream();
        var (checkin, _, clock) = Make(upstream.Respond);

        var first = await checkin.EnsureCheckedInAsync(TestCredential);
        Assert.Equal(CheckinOutcome.Claimed, first.Outcome);
        var stamp = checkin.NowIso();

        // 同一天再问 —— 本地记录命中，短路
        var afterFirst = upstream.Count;
        var sameDay = await checkin.EnsureCheckedInAsync(TestCredential, stamp);
        Assert.Equal(CheckinOutcome.AlreadyClaimed, sameDay.Outcome);
        Assert.False(sameDay.UpstreamCalled);
        Assert.Equal(afterFirst, upstream.Count);   // ← 一次都没多打

        // 到第二天 —— 又该签了。
        //
        // 两件事必须同时发生，缺一个这测试就没在测「跨天」：
        //   ① **我们这边的时钟往前走一天**（否则本地记录仍然命中，直接短路）
        //   ② **假上游也进入新的一天**（否则它还在说 today_checked_in = true，
        //      测出来的是「上游说已签」而不是「跨天后重签」）
        clock.Now = clock.Now.AddDays(1);
        upstream.NewDay();

        var claimsBefore = upstream.Paths.Count(p => p.EndsWith("/daily-checkin", StringComparison.Ordinal));

        var nextDay = await checkin.EnsureCheckedInAsync(TestCredential, stamp);

        Assert.Equal(CheckinOutcome.Claimed, nextDay.Outcome);
        var claimsAfter = upstream.Paths.Count(p => p.EndsWith("/daily-checkin", StringComparison.Ordinal));
        Assert.Equal(claimsBefore + 1, claimsAfter);   // ← 真的又签了一次
    }

    // ══ ③ 上游失败 ═══════════════════════════════════════════════

    [Fact]
    public async Task 上游_500_时返回失败结构而不抛异常()
    {
        var (checkin, _, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.InternalServerError,
            FakeUpstream.Envelope(500, null, "服务开小差了")));

        var result = await checkin.ClaimAsync(TestCredential);

        Assert.Equal(CheckinOutcome.Failed, result.Outcome);
        Assert.False(result.Ok);
        Assert.Equal(UpstreamErrorKind.Server, result.ErrorKind);
        Assert.Equal(500, result.Status);
        Assert.Contains("服务开小差了", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 业务码非零时即使_http_200_也算失败()
    {
        // 上游把语义塞在 200 里 —— 只看状态码会把这种当成签到成功
        var (checkin, _, _) = Make(_ => FakeUpstream.Ok(FakeUpstream.Envelope(6004, null, "活动已结束")));

        var result = await checkin.ClaimAsync(TestCredential);

        Assert.Equal(CheckinOutcome.Failed, result.Outcome);
        Assert.Equal(6004, result.Code);
        Assert.Equal(UpstreamErrorKind.ModelRateLimited, result.ErrorKind);
        Assert.Contains("活动已结束", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 上游返回非_json_时返回失败结构()
    {
        var (checkin, _, _) = Make(_ => FakeUpstream.Ok("<html><body>502 Bad Gateway</body></html>"));

        var result = await checkin.ClaimAsync(TestCredential);

        Assert.Equal(CheckinOutcome.Failed, result.Outcome);
        Assert.Contains("不是 JSON", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 连接层失败_比如上游不可达_时返回失败结构()
    {
        var fake = new FakeUpstream(_ => throw new HttpRequestException("connection refused"));
        var clock = new FixedTime(Anchor);
        var checkin = new Checkin(new HttpClient(fake), UpstreamVariant.China, NullLogger<Checkin>.Instance, clock);

        var result = await checkin.ClaimAsync(TestCredential);

        Assert.Equal(CheckinOutcome.Failed, result.Outcome);
        Assert.Equal(0, result.Status);
        Assert.Equal(UpstreamErrorKind.Server, result.ErrorKind);
        Assert.Contains("连不上上游", result.Message, StringComparison.Ordinal);
    }

    // ══ ★ 真机发现的响应形状（t48 实测，2026-10-07）════════════════
    //
    // 下面三条的输入是**真实上游返回的原文**（真机跑出来的，不是编的）。
    // 它们钉住的是「真机形状被解析路径正确处理」—— 这正是 t48 要验的东西。
    //
    // 原始证据：
    //   POST /v2/billing/meter/daily-checkin  （今天已签的账号）
    //   → HTTP 400 {"code":10001,"msg":"今天已签到，请明天再来",
    //               "requestId":"601a0f5c-0412-48eb-92ca-21cf07bfb06d"}

    [Fact]
    public async Task 真机_重复签到时_上游回_400_code_10001()
    {
        // ⚠⚠ 这条是 t48 真机验证的**核心发现** ⚠⚠
        //
        // 单测原先假设「上游对重复签到回 code 0，靠 today_checked_in 判断」。
        // **真机不是这样**：上游回 HTTP 400 + code 10001 的**明确业务错误**。
        //
        // 影响：Checkin.ClaimAsync（强制签到路径）会把它归成
        //       Failed / BadRequest，**而不是 AlreadyClaimed**。
        //       调用方只看 ClaimAsync 的话，会把这个当成"签到失败"报给用户。
        var (checkin, _, _) = Make(_ => FakeUpstream.Status(
            HttpStatusCode.BadRequest,
            """{"code":10001,"msg":"今天已签到，请明天再来","requestId":"601a0f5c-0412-48eb-92ca-21cf07bfb06d"}"""));

        var result = await checkin.ClaimAsync(TestCredential);

        // 现状：**失败**，不是「已签到」
        Assert.Equal(CheckinOutcome.Failed, result.Outcome);
        Assert.False(result.Ok);
        Assert.Equal(400, result.Status);
        Assert.Equal(10001, result.Code);

        // 上游的中文 msg 被原样透出（这一点是对的 —— 给用户看的话它比我们编的准）
        Assert.Contains("今天已签到", result.Message, StringComparison.Ordinal);

        // requestId 带出来了（对账要用）
        Assert.Equal("601a0f5c-0412-48eb-92ca-21cf07bfb06d", result.RequestId);
    }

    [Fact]
    public async Task 真机_重复签到走_ensure_时_靠_status_短路拿到_AlreadyClaimed()
    {
        // 上一条证明了 ClaimAsync 会误判。但**实际调用方不该走 ClaimAsync** ——
        // EnsureCheckedInAsync 会**先查 status**，看到 today_checked_in 就短路，
        // 根本不会发出 daily-checkin。
        //
        // 真机实测这个 status（2026-10-07）：
        //   {"code":0,"msg":"OK","data":{"active":true,"today_checked_in":true,
        //    "streak_days":8,...}}
        var (checkin, fake, _) = Make(_ => StatusOk(StatusData(
            active: true, todayCheckedIn: true, streakDays: 8, todayCredit: 100)));

        var result = await checkin.EnsureCheckedInAsync(TestCredential);

        Assert.Equal(CheckinOutcome.AlreadyClaimed, result.Outcome);
        Assert.True(result.Ok);
        Assert.Contains("连续 8 天", result.Message, StringComparison.Ordinal);

        // ★ 关键断言：**没有发出 daily-checkin**
        Assert.Single(fake.Requests);
        Assert.DoesNotContain(
            fake.Requests,
            r => r.Url.EndsWith("/daily-checkin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 真机状态响应的全部字段都被正确读取()
    {
        // t48 真机原文的完整字段集（逐字段核对类型与读法）。
        // ⚠ 其中 next_streak_day / action_button **CheckinStatus 没有** ——
        //    见下面单独的用例。
        var real = new JsonObject
        {
            ["active"] = true,
            ["today_checked_in"] = true,
            ["streak_days"] = 8,
            ["daily_credit"] = 100,
            ["today_credit"] = 100,
            ["is_streak_day"] = false,
            ["next_streak_day"] = 0,
            ["streak_bonus_days"] = 0,
            ["streak_bonus_credit"] = 0,
            ["checkin_dates"] = new JsonArray(
                "2026-10-07", "2026-10-06", "2026-10-05", "2026-10-04",
                "2026-10-03", "2026-10-02", "2026-10-01", "2026-09-30"),
            ["week_checkin_days"] = 3,
            ["week_progress"] = new JsonArray(true, true, true, false, false, false, false),
            ["total_credits"] = 800,
            // ⚠ 真机的 start_time / end_time 是**空格分隔、不带时区**的形态，
            //    **不是** ISO 8601。CheckinStatus 把它们当不透明字符串存，
            //    所以不会出错 —— 但界面若要解析时间，得知道这个形状。
            ["start_time"] = "2026-09-30 00:00:00",
            ["end_time"] = "2026-10-15 23:59:59",
            ["theme_name"] = "Buddy见面站",
            ["season"] = 10,
            ["activity_name"] = "Buddy应用",
            ["claim_button_text"] = "立即领取",
            ["action_button"] = new JsonObject
            {
                ["show"] = true,
                ["text"] = "体验Buddy应用",
                ["action"] = "workbuddy://home?openBuddySwitcher=true",
            },
        };

        var (checkin, _, _) = Make(_ => StatusOk(real));

        var status = await checkin.FetchStatusAsync(TestCredential);

        Assert.NotNull(status);
        Assert.True(status!.Active);
        Assert.True(status.TodayCheckedIn);
        Assert.Equal(8, status.StreakDays);
        Assert.Equal(100, status.DailyCredit);
        Assert.Equal(100, status.TodayCredit);
        Assert.Equal(800, status.TotalCredits);
        Assert.Equal(3, status.WeekCheckinDays);
        Assert.Equal([true, true, true, false, false, false, false], status.WeekProgress);
        Assert.Equal(8, status.CheckinDates.Count);
        Assert.Equal("2026-10-07", status.CheckinDates[0]);
        Assert.Equal("Buddy见面站", status.ThemeName);
        Assert.Equal("Buddy应用", status.ActivityName);
        Assert.Equal("立即领取", status.ClaimButtonText);
        Assert.Equal(10, status.Season);
        Assert.False(status.IsStreakDay);
        Assert.Equal(0, status.StreakBonusDays);
        Assert.Equal(0, status.StreakBonusCredit);

        // 非 ISO 的时间串原样保留（不被误解、不抛）
        Assert.Equal("2026-09-30 00:00:00", status.StartTime);
        Assert.Equal("2026-10-15 23:59:59", status.EndTime);
    }

    // ══ ★ t50：补上两个真机字段 ══════════════════════════════════
    //
    // t48 真机验证发现上游在发 `next_streak_day` 与 `action_button`，
    // 而 CheckinStatus 没有这两个属性 —— 于是它们被静默丢弃（信息损失）。
    // 下面这几条是补齐后的回归，输入全部取自**真机原始响应**，不是编的。

    /// <summary>
    /// t48 真机原文里的 `action_button` 整块（2026-10-07 实测，19 个账号一致）。
    /// </summary>
    private static JsonObject RealActionButton() => new()
    {
        ["show"] = true,
        ["text"] = "体验Buddy应用",
        ["action"] = "workbuddy://home?openBuddySwitcher=true",
    };

    [Fact]
    public void 真机_next_streak_day_被正确读出()
    {
        // 真机原文：{"next_streak_day":0,...}
        var data = new JsonObject { ["active"] = true, ["next_streak_day"] = 0 };

        var status = CheckinStatus.FromData(data);

        Assert.NotNull(status);
        Assert.Equal(0, status!.NextStreakDay);
    }

    [Fact]
    public void 真机_next_streak_day_非零值也能读出()
    {
        // 真机当天是 0；**非零值没在真机见过**（19 个账号全是 0），
        // 值是构造的。这里只验证"解析路径能搬任意整数"，
        // 不能算作"真机验证过非零语义"。
        var data = new JsonObject { ["active"] = true, ["next_streak_day"] = 3 };

        Assert.Equal(3, CheckinStatus.FromData(data)!.NextStreakDay);
    }

    [Fact]
    public void 真机_action_button_三字段被完整读出()
    {
        // ⚠ 这条用**真机原文的整块**，含 workbuddy:// 深链
        var data = new JsonObject
        {
            ["active"] = true,
            ["action_button"] = RealActionButton(),
        };

        var status = CheckinStatus.FromData(data);

        Assert.NotNull(status);
        Assert.NotNull(status!.ActionButton);
        Assert.True(status.ActionButton!.Show);
        Assert.Equal("体验Buddy应用", status.ActionButton.Text);
        Assert.Equal("workbuddy://home?openBuddySwitcher=true", status.ActionButton.Action);
    }

    [Fact]
    public void 真机_完整状态原文_两字段都在()
    {
        // 把 t48 那份真机原文（含这两个字段）整块过一遍。
        // 这是"最接近真机"的一条 —— 输入逐字来自实测响应。
        var real = new JsonObject
        {
            ["active"] = true,
            ["today_checked_in"] = true,
            ["streak_days"] = 8,
            ["daily_credit"] = 100,
            ["today_credit"] = 100,
            ["is_streak_day"] = false,
            ["next_streak_day"] = 0,
            ["streak_bonus_days"] = 0,
            ["streak_bonus_credit"] = 0,
            ["checkin_dates"] = new JsonArray("2026-10-07", "2026-10-06"),
            ["week_checkin_days"] = 3,
            ["week_progress"] = new JsonArray(true, true, true, false, false, false, false),
            ["total_credits"] = 800,
            ["start_time"] = "2026-09-30 00:00:00",
            ["end_time"] = "2026-10-15 23:59:59",
            ["theme_name"] = "Buddy见面站",
            ["season"] = 10,
            ["activity_name"] = "Buddy应用",
            ["claim_button_text"] = "立即领取",
            ["action_button"] = RealActionButton(),
        };

        var status = CheckinStatus.FromData(real);

        Assert.NotNull(status);
        Assert.Equal(0, status!.NextStreakDay);
        Assert.NotNull(status.ActionButton);
        Assert.Equal("workbuddy://home?openBuddySwitcher=true", status.ActionButton!.Action);
    }

    // ── 缺字段容错 ─────────────────────────────────────────────

    [Fact]
    public void 两字段缺失时取安全默认_不抛()
    {
        // 只给必填字段 —— 两个新字段完全缺失
        var data = new JsonObject { ["active"] = true, ["today_checked_in"] = true };

        var status = CheckinStatus.FromData(data);

        Assert.NotNull(status);
        Assert.Equal(0, status!.NextStreakDay);      // int → 0
        Assert.Null(status.ActionButton);            // 整块 → null
    }

    [Fact]
    public void action_button_类型漂移时当作没发_不造空壳()
    {
        // ⚠ 这条钉的是 ButtonOf 的设计决定：
        // 上游回非对象时，**返回 null 而不是造一个空壳对象**。
        //
        // 为什么重要：空壳（Show=false, Text="", Action=""）和"上游没发"
        // 在界面上是**两回事** —— 前者会渲染出"有按钮但文案为空"的东西。
        foreach (var bogus in new JsonNode?[]
                 {
                     JsonValue.Create(123),
                     JsonValue.Create("workbuddy://x"),
                     new JsonArray(1, 2),
                     JsonValue.Create(true),
                 })
        {
            var data = new JsonObject { ["active"] = true, ["action_button"] = bogus };

            var status = CheckinStatus.FromData(data);

            Assert.NotNull(status);
            Assert.Null(status!.ActionButton);
        }
    }

    [Fact]
    public void action_button_为空对象时也当作没发()
    {
        // `{}` 三个字段全缺 —— 同样是"没内容"，不该造一个空壳出来
        var data = new JsonObject { ["active"] = true, ["action_button"] = new JsonObject() };

        Assert.Null(CheckinStatus.FromData(data)!.ActionButton);
    }

    [Fact]
    public void action_button_部分字段缺失时给各自默认()
    {
        // 只给 action，不给 show/text
        var data = new JsonObject
        {
            ["active"] = true,
            ["action_button"] = new JsonObject { ["action"] = "workbuddy://home" },
        };

        var button = CheckinStatus.FromData(data)!.ActionButton;

        Assert.NotNull(button);
        // ⚠ Show 默认 **false**（保守方向：拿不准要不要显示 → 不显示）
        Assert.False(button!.Show);
        Assert.Equal("", button.Text);
        Assert.Equal("workbuddy://home", button.Action);
    }

    [Fact]
    public void action_button_的_show_支持字符串与数字形态()
    {
        // 沿用 Checkin 既有的宽容读法（上游偶尔发 "true" / 1）
        var asString = new JsonObject
        {
            ["active"] = true,
            ["action_button"] = new JsonObject { ["show"] = "true" },
        };
        var asNumber = new JsonObject
        {
            ["active"] = true,
            ["action_button"] = new JsonObject { ["show"] = 1 },
        };

        Assert.True(CheckinStatus.FromData(asString)!.ActionButton!.Show);
        Assert.True(CheckinStatus.FromData(asNumber)!.ActionButton!.Show);
    }

    [Fact]
    public void next_streak_day_类型漂移时退化成_0()
    {
        // 沿用 IntOf 的宽容读法：能读的读，读不出给 0
        var asString = new JsonObject { ["active"] = true, ["next_streak_day"] = "5" };
        var asJunk = new JsonObject { ["active"] = true, ["next_streak_day"] = "abc" };
        var asNull = new JsonObject { ["active"] = true, ["next_streak_day"] = null };

        Assert.Equal(5, CheckinStatus.FromData(asString)!.NextStreakDay);
        Assert.Equal(0, CheckinStatus.FromData(asJunk)!.NextStreakDay);
        Assert.Equal(0, CheckinStatus.FromData(asNull)!.NextStreakDay);
    }

    [Fact]
    public void 两个新字段在_FromData_之外也是硬编码读键()
    {
        // 契约要求"显式 JsonPropertyName 或手工读键，与既有风格一致"。
        // Checkin 的风格是**手工读键**（属性名不参与绑定），这里钉住这一点：
        // 若将来有人误以为它们走序列化绑定、把键名改掉，这条会红。
        var props = typeof(CheckinStatus).GetProperties().Select(p => p.Name).ToArray();
        Assert.Contains("NextStreakDay", props);
        Assert.Contains("ActionButton", props);

        // 真机键名（手工读键的依据）
        var data = new JsonObject
        {
            ["next_streak_day"] = 7,                                   // snake_case
            ["action_button"] = new JsonObject { ["show"] = true },
        };
        var status = CheckinStatus.FromData(data)!;
        Assert.Equal(7, status.NextStreakDay);
        Assert.NotNull(status.ActionButton);
    }

    [Fact]
    public async Task 真机两个新字段在_FetchStatusAsync_链路上也能拿到()
    {
        // 端到端：假上游回真机原文 → FetchStatusAsync → 两个字段可读
        var real = new JsonObject
        {
            ["active"] = true,
            ["today_checked_in"] = true,
            ["streak_days"] = 8,
            ["next_streak_day"] = 0,
            ["action_button"] = RealActionButton(),
        };
        var (checkin, _, _) = Make(_ => StatusOk(real));

        var status = await checkin.FetchStatusAsync(TestCredential);

        Assert.NotNull(status);
        Assert.Equal(0, status!.NextStreakDay);
        Assert.Equal("体验Buddy应用", status.ActionButton!.Text);
        Assert.Equal("workbuddy://home?openBuddySwitcher=true", status.ActionButton.Action);
    }

    [Fact]
    public async Task 状态查询失败时返回_null_不抛()
    {
        var (checkin, _, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.Unauthorized,
            FakeUpstream.Envelope(401, null, "token 过期")));

        // 旧版 fetchCheckinStatus 就是 catch 成 null —— 状态查不出来不该拦住别的账号
        var status = await checkin.FetchStatusAsync(TestCredential);

        Assert.Null(status);
    }

    // ══ ④ 凭证失效 ═══════════════════════════════════════════════
    //
    // ⚠ 这里用 **ClaimAsync** 而不是 EnsureCheckedInAsync。
    //
    // 理由：EnsureCheckedInAsync 第一步是查状态，401 会让状态查询返回 null，
    // 于是它统一归成「查不到签到状态」（见下面那条用例）。那条路径**丢掉**了
    // 上游给的具体分类 —— 这是有意的（「查不到状态」对调用方是一个更有用的
    // 说法），但也就意味着分类断言要在 ClaimAsync 上做。
    //
    // 两个方法各测各的语义，不是重复。

    [Fact]
    public async Task 凭证失效_401_时给出_unauthorized_分类()
    {
        var (checkin, _, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.Unauthorized,
            FakeUpstream.Envelope(401, null, "invalid token")));

        var result = await checkin.ClaimAsync(TestCredential);

        Assert.Equal(CheckinOutcome.Failed, result.Outcome);
        Assert.Equal(UpstreamErrorKind.Unauthorized, result.ErrorKind);
        Assert.Equal(401, result.Status);
        Assert.Contains("invalid token", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 凭证失效_403_也归_unauthorized()
    {
        var (checkin, _, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.Forbidden,
            FakeUpstream.Envelope(403, null, "no permission")));

        var result = await checkin.ClaimAsync(TestCredential);

        Assert.Equal(CheckinOutcome.Failed, result.Outcome);
        Assert.Equal(UpstreamErrorKind.Unauthorized, result.ErrorKind);
        Assert.Equal(403, result.Status);
    }

    [Fact]
    public async Task HTTP_状态码优先于业务_code()
    {
        // ⚠ 这条钉的是**分类优先级**：上游在 HTTP 401 的 body 里可能塞一个
        // 语义完全不同的 code（这里是 6004）。以 body 为准的话「凭证失效」
        // 会被归成「模型级限流」—— 界面就会让用户等几分钟重试，
        // 而正确处置是引导他重新登录。
        var (checkin, _, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.Unauthorized,
            FakeUpstream.Envelope(6004, null, "token 过期，请重新登录")));

        var result = await checkin.ClaimAsync(TestCredential);

        Assert.Equal(CheckinOutcome.Failed, result.Outcome);
        Assert.Equal(UpstreamErrorKind.Unauthorized, result.ErrorKind);
        Assert.Equal(401, result.Status);
        Assert.Equal(6004, result.Code);   // code 照样带出去，只是不参与分类
    }

    [Fact]
    public async Task 状态查不到时_ensure_返回失败但不抛()
    {
        // 凭证失效 → status 回 401 → before 是 null
        var (checkin, _, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.Unauthorized,
            FakeUpstream.Envelope(401, null, "expired")));

        var result = await checkin.EnsureCheckedInAsync(TestCredential);

        Assert.Equal(CheckinOutcome.Failed, result.Outcome);
        Assert.Contains("查不到签到状态", result.Message, StringComparison.Ordinal);
    }

    // ══ ⑤ 批量：一个号坏掉不该拖垮别的号 ══════════════════════════

    [Fact]
    public async Task 批量签到中单个账号失败不影响其他账号()
    {
        // 每个账号一份自己的「签没签」状态 —— 真实上游也是按账号记的
        var signed = new HashSet<string>(StringComparer.Ordinal);
        var claims = new List<string>();

        var fake = new FakeUpstream(request =>
        {
            var auth = request.Headers.Authorization?.Parameter ?? "";

            // 坏号一律 401
            if (auth.Contains("bad", StringComparison.Ordinal))
            {
                return FakeUpstream.Status(HttpStatusCode.Unauthorized, FakeUpstream.Envelope(401, null, "expired"));
            }

            // 好号：签到后状态生效（和 SigningUpstream 同一套建模）
            if (request.RequestUri!.AbsolutePath.EndsWith("/daily-checkin", StringComparison.Ordinal))
            {
                signed.Add(auth);
                claims.Add(auth);
                return FakeUpstream.Ok(FakeUpstream.Envelope(0, null));
            }

            return StatusOk(StatusData(todayCheckedIn: signed.Contains(auth), streakDays: 3));
        });

        var clock = new FixedTime(Anchor);
        var checkin = new Checkin(new HttpClient(fake), UpstreamVariant.China, NullLogger<Checkin>.Instance, clock);

        var results = new List<CheckinResult>();
        foreach (var token in new[] { "bad-1", "good-1", "bad-2", "good-2" })
        {
            results.Add(await checkin.EnsureCheckedInAsync(
                TestCredential with { AccessToken = token }));
        }

        Assert.Equal(CheckinOutcome.Failed, results[0].Outcome);
        Assert.Equal(CheckinOutcome.Claimed, results[1].Outcome);
        Assert.Equal(CheckinOutcome.Failed, results[2].Outcome);
        Assert.Equal(CheckinOutcome.Claimed, results[3].Outcome);

        // 坏号一次都没签成，好号各签了一次 —— 失败没拖垮后面的号
        Assert.Equal(["good-1", "good-2"], claims);
    }

    // ══ ⑥ 契约：端点字面量 ═══════════════════════════════════════

    [Fact]
    public void 端点路径字面量与旧版一致()
    {
        // ⚠ 这两个常量是**外部契约**：e2e 检查器拿 ClaimPath 做「签到能力存在」的
        // 检测特征（`grep checkin` 会被 Vault.cs 的 lastCheckinAt 字段名假命中）。
        // 改了它们 = 悄悄让检查器失效，所以这里钉死。
        Assert.Equal("/v2/billing/meter/daily-checkin", Checkin.ClaimPath);
        Assert.Equal("/v2/billing/meter/checkin-activity-status", Checkin.StatusPath);
    }

    [Fact]
    public async Task 端到端_ensure_打的是两个不同的签到端点()
    {
        var upstream = new SigningUpstream();
        var (checkin, _, _) = Make(upstream.Respond);

        await checkin.EnsureCheckedInAsync(TestCredential);

        // 3 次 —— 最后那次是「签完回查拿准确天数」（旧版 checkin.js:145）。
        // 不是重复签到：中间那次 daily-checkin 只发生了一次。
        Assert.Equal(
            [
                "/v2/billing/meter/checkin-activity-status",
                "/v2/billing/meter/daily-checkin",
                "/v2/billing/meter/checkin-activity-status",
            ],
            upstream.Paths);

        // ⚠ 这条才是幂等的真正断言：**签到端点只打了一次**
        Assert.Single(upstream.Paths, p => p.EndsWith("/daily-checkin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 空身份账号也发得出去请求()
    {
        // 复用 ApplyBilling —— 它只在有 enterpriseId 时才发 X-Enterprise-Id，
        // 空身份不会因此崩掉（签到不是身份敏感的接口）
        var upstream = new SigningUpstream();
        var (checkin, fake, _) = Make(upstream.Respond);

        var empty = new Credential { AccessToken = "anon-token" };
        var result = await checkin.ClaimAsync(empty);

        Assert.Equal(CheckinOutcome.Claimed, result.Outcome);

        var first = fake.Requests.First();
        Assert.Equal("Bearer anon-token", first.Headers["Authorization"].Single());
        Assert.DoesNotContain("X-Enterprise-Id", first.Headers.Keys);
    }

    [Fact]
    public void 状态归一化对脏数据宽容()
    {
        // 上游是扒出来的接口：类型说变就变。读不出来的给默认值，**绝不抛**
        var data = new JsonObject
        {
            ["active"] = "true",              // 字符串布尔
            ["today_checked_in"] = 1,          // 数字布尔
            ["streak_days"] = "7",             // 字符串数字
            ["daily_credit"] = "1.5",          // 字符串小数
            ["week_progress"] = new JsonArray(1, 0, "true", false),
            ["checkin_dates"] = new JsonArray("2026-03-10", 42, null),
            ["season"] = "x",                  // 完全读不出来
        };

        var status = CheckinStatus.FromData(data);

        Assert.NotNull(status);
        Assert.True(status!.Active);
        Assert.True(status.TodayCheckedIn);
        Assert.Equal(7, status.StreakDays);
        Assert.Equal(1.5, status.DailyCredit);
        Assert.Equal([true, false, true, false], status.WeekProgress);
        Assert.Equal(["2026-03-10"], status.CheckinDates);
        Assert.Equal(0, status.Season);
    }

    [Fact]
    public void 状态归一化对非对象返回_null()
    {
        Assert.Null(CheckinStatus.FromData(null));
        Assert.Null(CheckinStatus.FromData(new JsonArray(1, 2)));
        Assert.Null(CheckinStatus.FromData(JsonValue.Create(1)));
    }
}
