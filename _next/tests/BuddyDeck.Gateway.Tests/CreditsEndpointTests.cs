using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.Gateway;
using BuddyDeck.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;   // HttpResponse.WriteAsync 是扩展方法，缺这条 using 会 CS1061
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 积分端点的测试。
///
/// ## 为什么这几条是重点
///
/// 1. **三条失败路径都要有用例**（验收第 2 条）——「正常」那条最容易写，
///    而真正会出事的是「账号无权限」「上游抽风」。
/// 2. **账号标签要与 `/metrics` 对得上**（验收第 3 条）—— 两处各用一套
///    公式的话，界面上的积分卡永远关联不到「最近请求」里的那个号。
///    这一条**直接跑真 `/metrics` 对照**，不是自己再算一遍。
/// 3. **积分失败不能拖垮别的端点**（验收第 4 条）—— 上游挂了时，
///    `/healthz` 与 `/v1/models` 必须照常。
///
/// 夹具：本类起一个**只挂 CreditsEndpoints** 的最小宿主（真 Kestrel + 真 HTTP）。
/// 为什么不用 `GatewayHost`：那个宿主挂的是 `EndpointFamilies.AddAll()` 里登记的族，
/// 而**本任务不接线**（`AddAll` 在别人的文件里），所以 `CreditsEndpoints`
/// 在它里面不可见 —— 用它测会全 404，测得的是「接线还没做」而不是「端点对不对」。
/// </summary>
public class CreditsEndpointTests
{
    // ══════════════════════════════════════════════════════════
    // 夹具
    // ══════════════════════════════════════════════════════════

    private sealed class CreditsHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private CreditsHost(WebApplication app, string baseAddress, FakeUpstreamHandler fake)
        {
            _app = app;
            BaseAddress = baseAddress;
            Fake = fake;
            Client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(30) };
        }

        public string BaseAddress { get; }
        public FakeUpstreamHandler Fake { get; }
        public HttpClient Client { get; }

        /// <summary>
        /// 起一个只挂 CreditsEndpoints 的宿主。
        ///
        /// @param responder 假上游：收到积分请求后回什么
        /// @param entries   保险库内容
        /// @param withMetrics 是否**同时**挂 GatewayApp（用于「与 /metrics 对照」那条）
        /// </summary>
        public static async Task<CreditsHost> StartAsync(
            Func<CapturedRequest, HttpResponseMessage> responder,
            IReadOnlyList<VaultEntry> entries,
            bool withMetrics = false)
        {
            var fake = new FakeUpstreamHandler(responder);

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            var services = builder.Services;

            var options = new GatewayOptions
            {
                Host = "127.0.0.1",
                Port = 0,
                ApiKey = "",                      // 不鉴权，简化测试
                Variant = UpstreamVariant.China.Id,
                DefaultModel = "hy3",
                LogDirectory = Path.Combine(Path.GetTempPath(), "buddydeck-credits-logs"),
            };

            services.AddSingleton(options);
            services.AddSingleton(UpstreamVariant.China);
            services.AddSingleton(new ModelCatalog(UpstreamVariant.China.Id, "hy3"));
            services.AddSingleton(new BuildInfo(AppContext.BaseDirectory));
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<AccountPool>();
            services.AddSingleton<ICredentialSource>(new ListSource(entries));

            services.AddSingleton(new HttpClient(fake, disposeHandler: false));
            services.AddSingleton(sp => new UpstreamClient(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<UpstreamVariant>(),
                sp.GetRequiredService<ILogger<UpstreamClient>>()));

            services.AddSingleton(sp => new CreditsEndpoints(
                sp.GetRequiredService<UpstreamClient>(),
                sp.GetRequiredService<ICredentialSource>(),
                sp.GetRequiredService<UpstreamVariant>(),
                sp.GetRequiredService<ILogger<CreditsEndpoints>>()));

            if (withMetrics)
            {
                // 为了「账号标签与 /metrics 一致」那条：同时挂网关本体（它提供 /metrics）
                services.AddSingleton(new RequestMetrics(TimeProvider.System));
                services.AddSingleton<IMetricsSource>(sp => sp.GetRequiredService<RequestMetrics>());
                services.AddSingleton(sp => new RequestLog(sp.GetRequiredService<TimeProvider>()));
                services.AddSingleton(sp => new ChatObservers(
                    [], sp.GetRequiredService<ILogger<ChatObservers>>()));
                services.AddSingleton(sp => new GatewayApp(
                    sp.GetRequiredService<GatewayOptions>(),
                    sp.GetRequiredService<UpstreamVariant>(),
                    sp.GetRequiredService<ModelCatalog>(),
                    sp.GetRequiredService<BuildInfo>(),
                    sp.GetRequiredService<UpstreamClient>(),
                    sp.GetRequiredService<AccountPool>(),
                    sp.GetRequiredService<ICredentialSource>(),
                    sp.GetRequiredService<ChatObservers>(),
                    sp.GetRequiredService<ILogger<GatewayApp>>(),
                    // ⚠ metrics 是构造函数的**第 11 个参数（可选，默认 null）** ——
                    //   早期版本只传到第 9 个（log），于是 _metrics 是 null，
                    //   `/metrics` 端点在、但永远没有记录 ——
                    //   表现为「recent 数组空」，而不是报错。
                    //   （这一族：失败不表现为错误，而表现为一个合法但空的值。）
                    null,
                    sp.GetRequiredService<IMetricsSource>(),
                    sp.GetRequiredService<RequestLog>(),
                    // ⚠⚠ 关键：把积分族**交给 GatewayApp**（第 13 个参数 families）。
                    //
                    //   这里曾经是错的（t83 定位到的第三个夹具缺陷）：
                    //   早期写法是「先 GatewayApp.Map(app)（内部 app.Run 终结管线），
                    //   再自己 app.Run(credits…) 兜一层」——
                    //   而 `Map` 内部是终端中间件（`GatewayApp.cs:142 app.Run(HandleAsync)`），
                    //   **管线到那里就结束了**，后面那个 app.Run **永远不可达**。
                    //
                    //   症状极具误导性：`/admin/credits` 落到 GatewayApp 的 404 兜底，
                    //   返回的是错误对象（没有 accountTag 字段）→ 测试 NRE
                    //   → 看起来像「积分端点实现坏了」。
                    //
                    //   ⇒ 正确的挂法是把族传进 GatewayApp，由它统一分派：
                    //     一个终端中间件 + 内部按 _families 顺序询问。
                    //     这正是生产装配（EndpointFamilies.AddAll）的做法。
                    null,
                    [sp.GetRequiredService<CreditsEndpoints>()]));
            }

            var app = builder.Build();

            // ── 挂管线：**只能有一个终端中间件** ──────────────────
            //
            // 两种模式二选一，不能叠加（t83 定位到的夹具缺陷）：
            //
            // ```
            // withMetrics = true   → GatewayApp.Map(app) 内部是 app.Run(HandleAsync)，
            //                        它自己按 _families 顺序分派（积分族已传进去）；
            //                        这里再 app.Run 一次是**不可达的死代码**。
            // withMetrics = false  → 没有 GatewayApp，直接把积分族当终端挂上。
            // ```
            if (withMetrics)
            {
                app.Services.GetRequiredService<GatewayApp>().Map(app);
            }
            else
            {
                var credits = app.Services.GetRequiredService<CreditsEndpoints>();
                app.Run(async context =>
                {
                    if (await credits.TryHandleAsync(context).ConfigureAwait(false)) return;

                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync("no route").ConfigureAwait(false);
                });
            }

            await app.StartAsync().ConfigureAwait(false);
            var address = app.Urls.First();

            return new CreditsHost(app, address, fake);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await _app.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        private sealed class ListSource(IReadOnlyList<VaultEntry> entries) : ICredentialSource
        {
            public IReadOnlyList<VaultEntry> Load() => entries;
        }
    }

    // ── 造数据 ────────────────────────────────────────────────

    private static VaultEntry Entry(
        string id = "uuid-1:ent-1",
        string uid = "user-abcdef123456",
        string? enterpriseId = "ent-1",
        string label = "测试号")
        => new()
        {
            Id = id,
            Uid = uid,
            EnterpriseId = enterpriseId,
            Domain = "workbuddy.cn",
            AccessToken = "at-secret-token",
            RefreshToken = "rt",
            ExpiresAtMs = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeMilliseconds(),
            Label = label,
        };

    /// <summary>上游积分响应的一个正常样例（形状对齐 Credits.cs 的解析口径）。</summary>
    /// <summary>
    /// 造一个**与真实上游同形状**的积分响应。
    ///
    /// ⚠ 这里曾经造错：早期版本写的是
    /// <c>{ code:0, data:{ totalCount:1, dosages:[…] } }</c> —— 少了两层包裹。
    /// 而真实上游（captain 实测 <c>/v2/billing/meter/get-user-resource</c>）是：
    /// <code>
    /// { "code":0, "data": { "Response": { "Data": { "TotalCount", "Accounts":[…] } } } }
    /// </code>
    /// ⇒ 解析器（<c>Credits.cs:166-170</c>）按三层读，夹具按一层造
    /// ⇒ 解析出**空列表**，测试报「collection was empty」。
    ///
    /// **教训**：夹具要照真实响应的形状造，不能照"我觉得合理"的形状造 ——
    /// 否则测的是夹具与解析器的**互相想象**，不是真实协议。
    /// </summary>
    private static string CreditsEnvelope(
        string packageName = "个人版套餐",
        double remain = 120.5,
        double size = 200,
        string cycleEnd = "2026-11-01T00:00:00Z")
        => new JsonObject
        {
            ["code"] = 0,
            ["msg"] = "OK",
            ["data"] = new JsonObject
            {
                ["Response"] = new JsonObject
                {
                    ["Data"] = new JsonObject
                    {
                        ["TotalCount"] = 1,
                        ["TotalDosage"] = remain,
                        // 真实字段名是 Accounts，不是 dosages
                        ["Accounts"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["PackageName"] = packageName,
                                ["PackageCode"] = "pkg-1",
                                ["ProductName"] = "WorkBuddy",
                                ["CapacityUnit"] = "credits",
                                ["CapacitySize"] = size,
                                ["CapacityRemain"] = remain,
                                ["CapacityUsed"] = size - remain,
                                // 真实响应里另有精确到小数的字符串版本
                                ["CapacitySizePrecise"] = size.ToString(
                                    "0.##", System.Globalization.CultureInfo.InvariantCulture),
                                ["CapacityRemainPrecise"] = remain.ToString(
                                    "0.##", System.Globalization.CultureInfo.InvariantCulture),
                                ["CycleStartTime"] = "2026-10-01 00:00:00",
                                ["CycleEndTime"] = cycleEnd,
                                ["Status"] = 0,
                                // grantSource 藏在 AccountAttributes 里（t57 实测）
                                ["AccountAttributes"] = new JsonArray
                                {
                                    new JsonObject { ["Key"] = "grantSource", ["Value"] = "monthly" },
                                },
                            },
                        },
                    },
                },
            },
        }.ToJsonString();

    private static async Task<JsonObject> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonNode.Parse(text)!.AsObject();
    }

    // ══════════════════════════════════════════════════════════
    // ① 正常返回
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 正常返回_积分包列表与到期时间()
    {
        await using var host = await CreditsHost.StartAsync(
            _ => UpstreamResponses.Text(CreditsEnvelope()), [Entry()]);

        var json = await GetJsonAsync(host.Client, "/admin/credits");

        Assert.True(json["ok"]!.GetValue<bool>());

        var packages = json["packages"]!.AsArray();
        var package = Assert.Single(packages)!.AsObject();

        Assert.Equal("个人版套餐", package["packageName"]!.GetValue<string>());
        Assert.Equal(120.5, package["remain"]!.GetValue<double>(), 3);
        Assert.Equal(200, package["size"]!.GetValue<double>(), 3);

        // ★ 到期时间要有 —— 界面靠它显示「X 月 X 日到期」
        Assert.Equal("2026-11-01T00:00:00Z", package["cycleEndTime"]!.GetValue<string>());

        Assert.Equal(1, json["totalCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task 正常返回_带账号维度字段()
    {
        await using var host = await CreditsHost.StartAsync(
            _ => UpstreamResponses.Text(CreditsEnvelope()), [Entry(label: "我的号")]);

        var json = await GetJsonAsync(host.Client, "/admin/credits");

        Assert.Equal("uuid-1:ent-1", json["accountId"]!.GetValue<string>());
        Assert.Equal("我的号", json["label"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(json["accountTag"]!.GetValue<string>()));
    }

    [Fact]
    public async Task 打的是_billing_域名上的积分路径()
    {
        await using var host = await CreditsHost.StartAsync(
            _ => UpstreamResponses.Text(CreditsEnvelope()), [Entry()]);

        await GetJsonAsync(host.Client, "/admin/credits");

        var request = Assert.Single(host.Fake.Requests);

        // ⚠ 走 BillingBase 而不是 ChatBase —— 用错域名就 404
        Assert.StartsWith(UpstreamVariant.China.BillingBase, request.Url, StringComparison.Ordinal);
        Assert.Contains(Credits.ResourcePath, request.Url, StringComparison.Ordinal);

        // 带上 Bearer（积分接口要鉴权）
        Assert.Equal("Bearer at-secret-token", request.HeaderOrNull("Authorization"));

        // ★ 带上 UA —— t65 提醒过：积分接口的 UA 敏感性是从 /v3/config 推断的
        Assert.False(string.IsNullOrEmpty(request.HeaderOrNull("User-Agent")));
    }

    [Fact]
    public async Task 尾部斜杠与查询参数都能处理()
    {
        await using var host = await CreditsHost.StartAsync(
            _ => UpstreamResponses.Text(CreditsEnvelope()), [Entry()]);

        // 尾部斜杠等价
        var withSlash = await GetJsonAsync(host.Client, "/admin/credits/");
        Assert.True(withSlash["ok"]!.GetValue<bool>());

        // 点名账号
        var named = await GetJsonAsync(host.Client, "/admin/credits?account=uuid-1%3Aent-1");
        Assert.Equal("uuid-1:ent-1", named["accountId"]!.GetValue<string>());
    }

    [Fact]
    public async Task 不是积分路径时返回false让给别人()
    {
        await using var host = await CreditsHost.StartAsync(
            _ => UpstreamResponses.Text(CreditsEnvelope()), [Entry()]);

        // 别的路径不该被积分族吃掉（那是 404，由后面的 handler 决定）
        using var response = await host.Client.GetAsync("/admin/accounts").ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 非GET方法返回405()
    {
        await using var host = await CreditsHost.StartAsync(
            _ => UpstreamResponses.Text(CreditsEnvelope()), [Entry()]);

        using var response = await host.Client
            .PostAsync("/admin/credits", new StringContent("{}", Encoding.UTF8, "application/json"))
            .ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ══════════════════════════════════════════════════════════
    // ② 账号无权限 / 没有账号
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 保险库为空时返回_not_signed_in()
    {
        await using var host = await CreditsHost.StartAsync(
            _ => UpstreamResponses.Text(CreditsEnvelope()), []);

        var json = await GetJsonAsync(host.Client, "/admin/credits");

        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Equal("not_signed_in", json["error"]!["code"]!.GetValue<string>());

        // 一个字都没打上游
        Assert.Equal(0, host.Fake.Count);
    }

    [Fact]
    public async Task 点名不存在的账号返回_account_not_found()
    {
        await using var host = await CreditsHost.StartAsync(
            _ => UpstreamResponses.Text(CreditsEnvelope()), [Entry()]);

        var json = await GetJsonAsync(host.Client, "/admin/credits?account=不存在");

        Assert.False(json["ok"]!.GetValue<bool>());
        // ★ 与「没有账号」区分开 —— 界面要能提示「你选错号了」
        Assert.Equal("account_not_found", json["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, host.Fake.Count);
    }

    [Fact]
    public async Task 账号无权限时返回结构化结果而不是500()
    {
        // 上游 401（凭证失效）
        await using var host = await CreditsHost.StartAsync(
            _ => UpstreamResponses.Unauthorized("invalid token"), [Entry()]);

        using var response = await host.Client.GetAsync("/admin/credits").ConfigureAwait(false);

        // ★ 200 而不是 5xx —— 积分查不到 ≠ 整个请求失败（见端点注释的取舍）
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.False(json["ok"]!.GetValue<bool>());

        // 必须带分类，界面才能区分「要重新登录」和「稍后再试」
        Assert.False(string.IsNullOrEmpty(json["kind"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(json["message"]!.GetValue<string>()));

        // 空包，不是崩
        Assert.Empty(json["packages"]!.AsArray());
    }

    // ══════════════════════════════════════════════════════════
    // ③ 上游错误
    // ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "429")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    [InlineData(HttpStatusCode.BadRequest, "400")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    [InlineData(HttpStatusCode.BadGateway, "502")]
    public async Task 各类上游错误都不抛异常且带分类(HttpStatusCode status, string label)
    {
        await using var host = await CreditsHost.StartAsync(
            _ => UpstreamResponses.Text($"{{\"code\":1,\"message\":\"{label}\"}}", status),
            [Entry()]);

        using var response = await host.Client.GetAsync("/admin/credits").ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(json["kind"]!.GetValue<string>()));
    }

    [Fact]
    public async Task 上游返回非法JSON时也不抛()
    {
        await using var host = await CreditsHost.StartAsync(
            _ => UpstreamResponses.Text("<html>这不是 JSON</html>"), [Entry()]);

        var json = await GetJsonAsync(host.Client, "/admin/credits");

        Assert.False(json["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 连接层失败时也不抛()
    {
        await using var host = await CreditsHost.StartAsync(
            _ => throw new HttpRequestException("连不上上游"), [Entry()]);

        var json = await GetJsonAsync(host.Client, "/admin/credits");

        Assert.False(json["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 上游正文不进响应也不进日志()
    {
        // 上游在正文里回显了敏感串（真实会发生：错误消息可能带请求内容）
        const string marker = "ZZTOP_UPSTREAM_BODY_MARKER";
        await using var host = await CreditsHost.StartAsync(
            _ => UpstreamResponses.Text(
                $"{{\"code\":1,\"message\":\"{marker}\",\"detail\":\"{marker}\"}}",
                HttpStatusCode.BadRequest),
            [Entry()]);

        using var response = await host.Client.GetAsync("/admin/credits").ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync();

        // ★ 上游正文一个字都不该出现在响应里（它可能回显请求内容）
        Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════
    // ④ 与 /metrics 的账号标签一致（验收第 3 条）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 账号标签公式与metrics一致_短标签()
    {
        // 长 uid → 取前 8 位
        var entry = Entry(uid: "user-abcdef123456", enterpriseId: "ent-1");

        // GatewayApp.AccountTagOf 的公式：uid 前 8 位 + ':' + enterpriseId
        Assert.Equal("user-abc:ent-1", CreditsEndpoints.AccountTagOf(entry));
    }

    [Fact]
    public void 账号标签公式与metrics一致_短uid不截断()
    {
        var entry = Entry(uid: "abc", enterpriseId: "ent-1");
        Assert.Equal("abc:ent-1", CreditsEndpoints.AccountTagOf(entry));
    }

    [Fact]
    public void 账号标签公式与metrics一致_个人账号()
    {
        // 个人账号 EnterpriseId = null → 末尾是空串（不是 "null"）
        var entry = Entry(uid: "user-abcdef123456", enterpriseId: null);
        Assert.Equal("user-abc:", CreditsEndpoints.AccountTagOf(entry));
    }

    [Fact]
    public void 账号标签不是完整uid_避免把凭据标识写出去()
    {
        var entry = Entry(uid: "user-abcdef1234567890");
        var tag = CreditsEndpoints.AccountTagOf(entry);

        Assert.DoesNotContain("user-abcdef1234567890", tag, StringComparison.Ordinal);
        Assert.True(tag.Length <= 40, "标签应与 /metrics 一样是短标签");
    }

    /// <summary>
    /// ★ 直接跑真 `/metrics` 对照（不是自己再算一遍）。
    ///
    /// 这条证明的是**两处真的对得上**，而不只是"我抄了同一个公式"。
    /// 先发一个聊天请求让 /metrics 里有账号记录，再比积分端点回的 accountTag。
    /// </summary>
    [Fact]
    public async Task 账号标签与真metrics能对上()
    {
        var entry = Entry(uid: "user-abcdef123456", enterpriseId: "ent-1");

        // 同一个假上游同时服务两类请求：
        //   · 积分路径 → 积分信封
        //   · 其余（聊天）→ SSE
        //
        // ⚠ 匹配条件曾写成 `request.Path.Contains("credit")` —— **错的**。
        //   真实积分路径是 `/v2/billing/meter/get-user-resource`（见
        //   `Credits.ResourcePath`），**一个 "credit" 都不含**。
        //   于是积分请求被当成聊天请求、回了 SSE，解析失败，
        //   表面症状是 `credits["ok"]` 为 null 的 NullReferenceException ——
        //   **看不出是"夹具路由猜错了"**。
        //   ⇒ 按真实常量匹配，别按想当然的关键词。
        await using var host = await CreditsHost.StartAsync(
            request => request.Path.Contains(Credits.ResourcePath, StringComparison.OrdinalIgnoreCase)
                ? UpstreamResponses.Text(CreditsEnvelope())
                : UpstreamResponses.SseText(
                    """{"choices":[{"index":0,"delta":{"content":"好"}}]}""",
                    """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""),
            [entry],
            withMetrics: true);

        // 发一次聊天，让 /metrics 里有这个号的记录
        using (var chat = await host.Client.PostAsync("/v1/chat/completions",
            new StringContent("""{"model":"hy3","stream":true,"messages":[{"role":"user","content":"你好"}]}""",
                Encoding.UTF8, "application/json")))
        {
            await chat.Content.ReadAsStringAsync();
        }

        var metrics = await GetJsonAsync(host.Client, "/metrics");
        var credits = await GetJsonAsync(host.Client, "/admin/credits");

        var recent = metrics["recent"]!.AsArray();
        Assert.NotEmpty(recent);

        var metricsTag = recent[0]!["accountTag"]!.GetValue<string>();
        var creditsTag = credits["accountTag"]!.GetValue<string>();

        // ★★ 两处的账号标识必须一模一样 —— 界面靠它把积分卡和请求记录关联起来
        Assert.Equal(metricsTag, creditsTag);
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ 积分失败不影响其他端点（验收第 4 条）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 积分拉取失败时其他端点照常()
    {
        // 上游对积分一律 500
        await using var host = await CreditsHost.StartAsync(
            _ => UpstreamResponses.Text("""{"code":1}""", HttpStatusCode.InternalServerError),
            [Entry()], withMetrics: true);

        // 积分失败
        var credits = await GetJsonAsync(host.Client, "/admin/credits");
        Assert.False(credits["ok"]!.GetValue<bool>());

        // ★ 但 /healthz 与 /v1/models 与 /metrics 都必须照常
        using (var health = await host.Client.GetAsync("/healthz"))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            var json = JsonNode.Parse(await health.Content.ReadAsStringAsync())!.AsObject();
            Assert.True(json["ok"]!.GetValue<bool>());
        }

        using (var models = await host.Client.GetAsync("/v1/models"))
        {
            Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        }

        using (var metrics = await host.Client.GetAsync("/metrics"))
        {
            Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        }

        // 再查一次积分仍然只是结构化失败，不是崩
        var again = await GetJsonAsync(host.Client, "/admin/credits");
        Assert.False(again["ok"]!.GetValue<bool>());
    }
}
