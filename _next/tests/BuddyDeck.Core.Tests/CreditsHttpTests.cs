using System.Net;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuddyDeck.Core.Tests;

/// <summary>
/// 积分查询 **HTTP 调用层**的测试（t65）。
///
/// t57 已覆盖 `Credits.Parse` 的纯解析（43 条）。这里只测**网络那一层**：
/// 打到哪个域名/路径、带什么头、请求体形状、HTTP 与业务 code 的失败怎么分类。
///
/// 全部走假上游（<see cref="FakeUpstream"/>），**不打真实上游**。
/// 回归夹具用 t57 从真机抄回来的响应片段（含 `AccountAttributes` 里的 `grantSource`）。
/// </summary>
public class CreditsHttpTests
{
    private static readonly Credential TestCredential = new()
    {
        AccessToken = "credits-token-abc",
        Uid = "u1",
        EnterpriseId = "e1",
        Domain = "workbuddy.cn",
        Label = "积分测试号",
    };

    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private const string Ua = "WorkBuddy/5.6.2";

    private static (UpstreamClient Client, FakeUpstream Fake) Make(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        UpstreamVariant? variant = null)
    {
        var fake = new FakeUpstream(handler);
        var client = new UpstreamClient(
            new HttpClient(fake),
            variant ?? UpstreamVariant.China,
            NullLogger<UpstreamClient>.Instance);
        return (client, fake);
    }

    /// <summary>t57 从真机抄回来的响应片段（含 AccountAttributes 里的 grantSource）。</summary>
    private static string RealCreditsBody() => new JsonObject
    {
        ["code"] = 0,
        ["msg"] = "OK",
        ["requestId"] = "d6d2ed36-a9de-414a-947b-9afe989100c6",
        ["data"] = new JsonObject
        {
            ["Response"] = new JsonObject
            {
                ["Data"] = new JsonObject
                {
                    ["TotalCount"] = 2,
                    ["TotalDosage"] = 600,
                    ["Accounts"] = new JsonArray(
                        // 有 grantSource 的（真机 Accounts[20] 的形状）
                        new JsonObject
                        {
                            ["PackageName"] = "CodeBuddy个人版国内运营裂变包",
                            ["CycleEndTime"] = "2026-10-31 08:26:15",
                            ["CapacityRemainPrecise"] = "100",
                            ["CapacitySizePrecise"] = "100",
                            ["CapacityUsedPrecise"] = "0",
                            ["AccountAttributes"] = new JsonArray(
                                new JsonObject { ["Key"] = "grantSource", ["Value"] = "daily_checkin", ["Type"] = 5 }),
                        },
                        // 没有 grantSource 的（真机 Accounts[0] 的形状：
                        // 前两项是 payerType / payerUin）
                        new JsonObject
                        {
                            ["PackageName"] = "CodeBuddy个人版包",
                            ["CycleEndTime"] = "2026-10-31 23:59:59",
                            ["CapacityRemainPrecise"] = "500",
                            ["CapacitySizePrecise"] = "500",
                            ["CapacityUsedPrecise"] = "0",
                            ["AccountAttributes"] = new JsonArray(
                                new JsonObject { ["Key"] = "payerType", ["Value"] = "0", ["Type"] = 5 },
                                new JsonObject { ["Key"] = "payerUin", ["Value"] = "330111699997", ["Type"] = 5 }),
                        }),
                },
                ["RequestId"] = "inner",
            },
        },
    }.ToJsonString();

    private static HttpResponseMessage Ok(string body = "", HttpStatusCode status = HttpStatusCode.OK)
        => FakeUpstream.Status(status, body.Length > 0 ? body : RealCreditsBody());

    private static JsonNode BodyOf(FakeUpstream.CapturedRequest request)
        => JsonNode.Parse(request.Body ?? "{}")!;

    // ══ ① 端点、方法、头 ═════════════════════════════════════════

    [Fact]
    public async Task 打到计费域名的积分端点_且是_POST()
    {
        var (client, fake) = Make(_ => Ok());

        await client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None);

        var request = fake.Requests.Single();
        Assert.Equal("POST", request.Method);
        Assert.Equal(
            "https://www.codebuddy.cn/v2/billing/meter/get-user-resource",
            request.Url);
    }

    [Fact]
    public async Task 带计费头_不带_IDE_归属族()
    {
        var (client, fake) = Make(_ => Ok());

        await client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None);

        var headers = fake.Requests.Single().Headers;
        Assert.Equal("Bearer credits-token-abc", headers["Authorization"].Single());
        Assert.Equal("SaaS", headers["X-Product"].Single());
        Assert.Equal("e1", headers["X-Enterprise-Id"].Single());

        // ⚠ 归属族是 chat / 探活专属的 —— 混进计费请求会把用量归属算乱
        Assert.DoesNotContain("X-IDE-Type", headers.Keys);
        Assert.DoesNotContain("X-IDE-Name", headers.Keys);
    }

    [Fact]
    public async Task 带_User_Agent()
    {
        // ⚠ t48 实测 /v3/config 缺 UA 会回 code 12403。
        //    积分接口的 UA 敏感性**未单独实测**（推断），所以按保守做法要求传。
        var (client, fake) = Make(_ => Ok());

        await client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None);

        Assert.Equal("WorkBuddy/5.6.2", fake.Requests.Single().Headers["User-Agent"].Single());
    }

    [Fact]
    public async Task 不传_UA_时不自己编一个()
    {
        var (client, fake) = Make(_ => Ok());

        await client.FetchCreditsAsync(TestCredential, null, Now, CancellationToken.None);

        Assert.DoesNotContain("User-Agent", fake.Requests.Single().Headers.Keys);
    }

    // ══ ② 分变体 billingBase ═════════════════════════════════════

    [Fact]
    public async Task 国内版走_codebuddy_域名()
    {
        var (client, fake) = Make(_ => Ok(), UpstreamVariant.China);

        await client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None);

        Assert.StartsWith("https://www.codebuddy.cn/", fake.Requests.Single().Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 国际版走_workbuddy_ai_域名()
    {
        // ⚠ 两个变体的 BillingBase 不同（国内 codebuddy.cn /
        //    国际 workbuddy.ai）。用 ChatBase 会打错域名。
        var (client, fake) = Make(_ => Ok(), UpstreamVariant.Global);

        await client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None);

        Assert.Equal(
            "https://www.workbuddy.ai/v2/billing/meter/get-user-resource",
            fake.Requests.Single().Url);
    }

    // ══ ③ 请求体 ═════════════════════════════════════════════════

    [Fact]
    public async Task 请求体用_BuildQueryBody_的形状()
    {
        var (client, fake) = Make(_ => Ok());

        await client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None);

        var body = BodyOf(fake.Requests.Single());

        Assert.Equal(1, body["PageNumber"]!.GetValue<int>());
        Assert.Equal(100, body["PageSize"]!.GetValue<int>());
        Assert.Equal("p_tcaca", body["ProductCode"]!.GetValue<string>());
        Assert.Equal([0, 3], body["Status"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal("2026-10-07 12:00:00", body["PackageEndTimeRangeBegin"]!.GetValue<string>());
        // now + 3185136e6 ms = 2026-10-07 12:00:00 + 101 年
        Assert.Equal("2127-09-13 12:00:00", body["PackageEndTimeRangeEnd"]!.GetValue<string>());
    }

    [Fact]
    public async Task 请求体能被_JSON_解析出来()
    {
        // ⚠ 这里**不**断言 `Content-Type` 头 ——
        //    Core.Tests 用的 FakeUpstream 只记录 `request.Headers`，
        //    而 Content-Type 属于 **content headers**（挂在 `Content.Headers` 上），
        //    所以取不到。改夹具不在本任务 inScope，且为一个断言给公共夹具加属性
        //    代价大于收益。
        //
        //    替代判据同样有效：**请求体本身能解析成 JSON** ——
        //    这已经证明发出去的是合法 JSON（而它的 Content-Type 是
        //    `StringContent(..., "application/json")`，由 StringContent 保证）。
        var (client, fake) = Make(_ => Ok());

        await client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None);

        var raw = fake.Requests.Single().Body;
        Assert.False(string.IsNullOrWhiteSpace(raw), "请求体不该为空");

        // 解析成功即证明是合法 JSON（解析失败会抛 JsonException）
        var parsed = JsonNode.Parse(raw!);
        Assert.NotNull(parsed);
        Assert.Equal("p_tcaca", parsed!["ProductCode"]!.GetValue<string>());
    }

    // ══ ④ 成功：真机片段被解析出来 ═══════════════════════════════

    [Fact]
    public async Task 真机片段_成功时返回两个包_含签到标记()
    {
        var (client, _) = Make(_ => Ok());

        var snapshot = await client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None);

        Assert.Equal(2, snapshot.Packages.Count);
        Assert.Equal(600, snapshot.TotalRemaining);

        // ★ 含 AccountAttributes 里的 grantSource
        Assert.Equal(1, snapshot.DailyCheckinPackageCount);
        Assert.Equal(100, snapshot.DailyCheckinRemaining);

        var checkinPackage = Assert.Single(snapshot.Packages, p => p.IsDailyCheckin);
        Assert.Equal("daily_checkin", checkinPackage.GrantSource);
        Assert.Equal("CodeBuddy个人版国内运营裂变包", checkinPackage.PackageName);

        // 没有 grantSource 的那个不该被误报（真机前两项是 payerType/payerUin）
        var other = Assert.Single(snapshot.Packages, p => !p.IsDailyCheckin);
        Assert.Equal("", other.GrantSource);
    }

    // ══ ⑤ HTTP 失败分类 ══════════════════════════════════════════

    [Fact]
    public async Task _401_分类成_session_dead()
    {
        // ⚠ 契约要求 401 → session_dead（不是 unauthorized）。
        //    理由：旧版语义是"会话在服务端不存在了"，处置是**重新登录**，
        //    刷新 token 没用 —— 与"凭证本身无效"是两种提示。
        var (client, _) = Make(_ => FakeUpstream.Status(HttpStatusCode.Unauthorized, """{"code":401}"""));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        Assert.Equal(401, ex.Status);
        Assert.Equal(UpstreamErrorKind.SessionDead, ex.Kind);
    }

    [Fact]
    public async Task _429_分类成_soft_rate()
    {
        var (client, _) = Make(_ => FakeUpstream.Status(
            HttpStatusCode.TooManyRequests, """{"code":429,"msg":"too many requests"}"""));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        Assert.Equal(429, ex.Status);
        Assert.Equal(UpstreamErrorKind.RateLimited, ex.Kind);
    }

    [Fact]
    public async Task _402_分类成_hard_credit()
    {
        // 402 Payment Required = 额度耗尽。**与限流不是一回事**：
        // 限流"等一会儿有用"，额度耗尽等多久都没用。
        var (client, _) = Make(_ => FakeUpstream.Status(
            (HttpStatusCode)402, """{"code":402,"msg":"payment required"}"""));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        Assert.Equal(402, ex.Status);
        Assert.Equal(UpstreamErrorKind.HardCredit, ex.Kind);
    }

    [Theory]
    [InlineData("insufficient credit")]
    [InlineData("NO CREDIT")]
    [InlineData("credit exhausted")]
    [InlineData("额度不足")]
    [InlineData("积分不足")]
    public async Task 正文含额度标记时分类成_hard_credit(string marker)
    {
        var (client, _) = Make(_ => FakeUpstream.Status(
            HttpStatusCode.BadRequest, $$"""{"code":100,"msg":"{{marker}}"}"""));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        // ⚠ 中文标记必须靠**原文**匹配（不是转小写后）——
        //    这个 Theory 同时覆盖英文（大小写不敏感）与中文（原文）两条路径。
        Assert.Equal(UpstreamErrorKind.HardCredit, ex.Kind);
    }

    [Fact]
    public async Task 正文含会话失效标记时分类成_session_dead()
    {
        var (client, _) = Make(_ => FakeUpstream.Status(
            HttpStatusCode.Forbidden, """{"code":403,"msg":"Offline user session not found"}"""));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        Assert.Equal(UpstreamErrorKind.SessionDead, ex.Kind);
    }

    [Fact]
    public async Task 业务码_12153_也是_session_dead()
    {
        var (client, _) = Make(_ => FakeUpstream.Status(
            HttpStatusCode.BadRequest, """{"code":12153,"msg":"bad"}"""));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        Assert.Equal(UpstreamErrorKind.SessionDead, ex.Kind);
    }

    // ══ ⑥ ⚠ 分类顺序：401 优先于正文标记 ═════════════════════════

    [Fact]
    public async Task _401_优先于正文里的额度标记()
    {
        // ⚠⚠ 这条钉的是**判定顺序**（旧版注释专门讲了理由）：
        //
        //   "a bare 401 whose body happens to name credits still reports
        //    session_dead and keeps its (http 401)/AUTH meaning"
        //
        // 一个错误页顺口提到 "credit"，不该把**凭证失效**变成**没额度** ——
        // 两者处置完全相反（重新登录 vs 什么都做不了）。
        var (client, _) = Make(_ => FakeUpstream.Status(
            HttpStatusCode.Unauthorized,
            """{"code":401,"msg":"insufficient credit for your session"}"""));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        // 401 赢，不看正文
        Assert.Equal(UpstreamErrorKind.SessionDead, ex.Kind);
    }

    [Fact]
    public async Task _402_优先于_right_正文里的会话标记()
    {
        // 402 在最前面判（旧版顺序），所以正文里的会话标记盖不过它
        var (client, _) = Make(_ => FakeUpstream.Status(
            (HttpStatusCode)402, """{"msg":"Offline user session not found"}"""));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        Assert.Equal(UpstreamErrorKind.HardCredit, ex.Kind);
    }

    [Fact]
    public async Task _404_分类成_not_found()
    {
        var (client, _) = Make(_ => FakeUpstream.Status(
            HttpStatusCode.NotFound, """{"code":404,"msg":"not found"}"""));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        Assert.Equal(UpstreamErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task _500_分类成_server()
    {
        var (client, _) = Make(_ => FakeUpstream.Status(
            HttpStatusCode.InternalServerError, """{"code":500,"msg":"boom"}"""));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        Assert.Equal(UpstreamErrorKind.Server, ex.Kind);
    }

    [Fact]
    public async Task 其余_4xx_分类成_bad_request()
    {
        var (client, _) = Make(_ => FakeUpstream.Status(
            HttpStatusCode.BadRequest, """{"code":1,"msg":"bad param"}"""));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        Assert.Equal(UpstreamErrorKind.BadRequest, ex.Kind);
    }

    [Fact]
    public async Task 连接层失败_状态码_0_分类成_server()
    {
        var (client, _) = Make(_ => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        Assert.Equal(0, ex.Status);
        Assert.Equal(UpstreamErrorKind.Server, ex.Kind);
    }

    // ══ ⑦ ⚠ HTTP 200 + 业务 code != 0 ════════════════════════════

    [Fact]
    public async Task _HTTP_200_但业务码非零时也要报错()
    {
        // ⚠⚠ 这是这个接口最容易静默出错的地方 ⚠⚠
        //
        // 上游会用 **HTTP 200 + code!=0** 表达业务错误（与签到同款）。
        // 只看 HTTP 状态就会把它当成功，然后 `Credits.Parse` 解出一个
        // **空快照** ⇒ 界面显示「余额 0」——
        // 而实际是凭证或额度出了问题。**用户看到的是"你没积分了"。**
        var (client, _) = Make(_ => FakeUpstream.Ok("""{"code":12153,"msg":"Offline user session not found"}"""));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        Assert.Equal(UpstreamErrorKind.SessionDead, ex.Kind);
        Assert.Contains("Offline user session not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task _HTTP_200_业务码为零时正常返回()
    {
        var (client, _) = Make(_ => FakeUpstream.Ok(RealCreditsBody()));

        var snapshot = await client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None);

        Assert.Equal(2, snapshot.Packages.Count);
    }

    [Fact]
    public async Task _HTTP_200_无_code_字段时当作成功()
    {
        // 上游有的接口直接给数据、不包信封。没有 code 就当成功，
        // 由 Credits.Parse 自己去取（它读不到会返回空快照，不抛）。
        var (client, _) = Make(_ => FakeUpstream.Ok("""{"data":{"Response":{"Data":{"Accounts":[]}}}}"""));

        var snapshot = await client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None);

        Assert.Empty(snapshot.Packages);
    }

    // ══ ⑧ 形状不对：不抛，返回空快照 ═════════════════════════════

    [Fact]
    public async Task 非_JSON_正文时报错()
    {
        var (client, _) = Make(_ => FakeUpstream.Ok("<html>502 Bad Gateway</html>"));

        var ex = await Assert.ThrowsAsync<UpstreamException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None));

        Assert.Contains("不是合法 JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 形状对但没有包时返回空快照而不是抛()
    {
        // 形状不对**不该抛** —— "余额显示为 0"比"面板炸掉"好。
        // （只有 HTTP 失败与业务码非零才抛。）
        var (client, _) = Make(_ => FakeUpstream.Ok("""{"code":0,"msg":"OK","data":{}}"""));

        var snapshot = await client.FetchCreditsAsync(TestCredential, Ua, Now, CancellationToken.None);

        Assert.Empty(snapshot.Packages);
        Assert.Equal(0, snapshot.TotalRemaining);
    }

    // ══ ⑨ 取消 ═══════════════════════════════════════════════════

    [Fact]
    public async Task 调用方取消时原样抛出_不当成上游错误()
    {
        using var cts = new CancellationTokenSource();
        var (client, _) = Make(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        // 客户端主动断开不是"上游失败" —— 原样抛让上层知道是它自己取消的
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, cts.Token));
    }

    [Fact]
    public async Task 已取消的令牌立即抛出()
    {
        var (client, fake) = Make(_ => Ok());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.FetchCreditsAsync(TestCredential, Ua, Now, cts.Token));

        Assert.Empty(fake.Requests);   // 一个请求都不该发出去
    }

    // ══ ⑩ 分类函数的直接用例 ═════════════════════════════════════

    [Fact]
    public void 分类函数覆盖全部分支()
    {
        Assert.Equal(UpstreamErrorKind.HardCredit, UpstreamClient.ClassifyCreditFailure(402, ""));
        Assert.Equal(UpstreamErrorKind.SessionDead, UpstreamClient.ClassifyCreditFailure(401, ""));
        Assert.Equal(UpstreamErrorKind.HardCredit, UpstreamClient.ClassifyCreditFailure(400, "no credit"));
        Assert.Equal(UpstreamErrorKind.SessionDead, UpstreamClient.ClassifyCreditFailure(200, "12153"));
        Assert.Equal(UpstreamErrorKind.RateLimited, UpstreamClient.ClassifyCreditFailure(429, ""));
        Assert.Equal(UpstreamErrorKind.NotFound, UpstreamClient.ClassifyCreditFailure(404, ""));
        Assert.Equal(UpstreamErrorKind.Server, UpstreamClient.ClassifyCreditFailure(503, ""));
        Assert.Equal(UpstreamErrorKind.BadRequest, UpstreamClient.ClassifyCreditFailure(400, "whatever"));

        // ⚠ status 0 = 连接层失败，走 server（可重试），**不是** bad_request。
        //    漏了这一条，"连不上上游"会被当成"请求有问题"⇒ 上游不重试。
        Assert.Equal(UpstreamErrorKind.Server, UpstreamClient.ClassifyCreditFailure(0, "transport error"));
    }
}
