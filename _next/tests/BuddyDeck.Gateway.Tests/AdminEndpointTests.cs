using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using BuddyDeck.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// `/admin/*` 端点族的测试（t17）。
///
/// ## 为什么这里自带一个宿主而不复用 ObservabilityFixture
///
/// `ObservabilityFixture` 没有挂 `AdminEndpoints`，而且 `GatewayHost`
/// 内部的 `GatewayOptions.VaultPath` 是**空的** —— 它会退化成
/// `AppContext.BaseDirectory` 下的文件。管理面几乎全是**对保险库的写操作**，
/// 必须指向一个临时文件，否则测试会去写被测程序目录（而且互相污染）。
///
/// 所以这里按 `GatewayHost` 的同一套形状起宿主，只是：
///   · 指定一个临时保险库路径
///   · 注册 `AdminEndpoints`
/// 其余（假上游、真 Kestrel、端口 0）都沿用共享夹具与同一套约定。
///
/// ## 脱敏
///
/// `/admin/accounts` 绝不能回 token 明文 —— 这里用**独特标记值**做断言
/// （做法与 t2 的脱敏测试同源）：只要那个字符串出现在任何响应里就会被抓到。
/// </summary>
public class AdminEndpointTests
{
    /// <summary>独一无二的 token 标记 —— 出现即说明泄露。</summary>
    private const string SecretAccessToken = "sk-ADMIN-LEAK-CANARY-0123456789abcdef";
    private const string SecretRefreshToken = "rt-ADMIN-LEAK-CANARY-fedcba9876543210";

    /// <summary>一个管理面测试宿主。</summary>
    private sealed class AdminHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public HttpClient Client { get; }
        public string VaultPath { get; }
        public FakeUpstreamHandler Fake { get; }
        public AdminEndpoints Admin { get; }

        private AdminHost(WebApplication app, string baseAddress, string vaultPath,
            FakeUpstreamHandler fake, AdminEndpoints admin)
        {
            _app = app;
            VaultPath = vaultPath;
            Fake = fake;
            Admin = admin;
            Client = new HttpClient(new SocketsHttpHandler())
            {
                BaseAddress = new Uri(baseAddress),
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        /// <summary>工厂 —— 构造函数留 private，起宿主的逻辑都在外面。</summary>
        public static AdminHost Create(WebApplication app, string baseAddress, string vaultPath,
            FakeUpstreamHandler fake, AdminEndpoints admin)
            => new(app, baseAddress, vaultPath, fake, admin);

        /// <summary>带 Bearer key 的 JSON POST。</summary>
        public Task<HttpResponseMessage> PostAsync(string path, string json, string? apiKey = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            }
            return Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        public Task<HttpResponseMessage> GetAsync(string path, string? apiKey = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            }
            return Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 起一个挂了管理面的宿主。
    ///
    /// @param accounts 保险库里的账号（null = 一个都没有）
    /// @param apiKey   要求的 Bearer key（null = 不鉴权）
    /// @param seedVault 是否把 accounts 真的落盘成保险库文件。
    ///                  false 用于测「保险库还没建」。
    /// </summary>
    private static async Task<AdminHost> StartAdminAsync(
        IReadOnlyList<VaultEntry>? accounts = null,
        string? apiKey = null,
        bool seedVault = true)
    {
        var vaultPath = Path.Combine(
            Directory.CreateTempSubdirectory("buddydeck-admin-vault-").FullName,
            "auth.cn.json");

        if (seedVault)
        {
            Vault.Write(vaultPath, new VaultDocument
            {
                Version = 1,
                ActiveId = accounts is { Count: > 0 } ? accounts[0].Id : "",
                Accounts = [.. accounts ?? []],
            });
        }

        var fake = new FakeUpstreamHandler(UpstreamResponses.SseText("{}"));
        var options = new GatewayOptions
        {
            Host = "127.0.0.1",
            Port = 0,
            ApiKey = apiKey ?? "",
            Variant = UpstreamVariant.China.Id,
            DefaultModel = "hy3",
            VaultPath = vaultPath,
            // ⚠ 每个测试宿主**自己的**日志目录（t58 修）。
            //
            // 原来这里写死 `Path.Combine(Path.GetTempPath(), "buddydeck-admin-logs")`.
            // 在 t58 之前没问题 —— 那时池列表是硬编码的，没有状态。
            // t58 之后池注册表**落盘**在 `<LogDirectory>/pools.json`,
            // 于是写死路径会让**所有测试共用同一个 pools.json**:
            // 上一个测试建的池会留在那里,下一个测试看到的是累计结果。
            //
            // 实测症状:`新增池_端口不与已有池冲突` 期望 4 个池,实际 16 个
            // —— 前几轮测试攒下来的池全在里面。
            //
            // 这**不是产品缺陷**(生产里 pools.json 本来就该持久),
            // 是**测试隔离**问题:测试之间不该通过磁盘共享状态。
            LogDirectory = Directory.CreateTempSubdirectory("buddydeck-admin-logs-").FullName,
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton(UpstreamVariant.China);
        services.AddSingleton(new ModelCatalog(UpstreamVariant.China.Id, "hy3"));
        services.AddSingleton(new BuildInfo(AppContext.BaseDirectory));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AccountPool>();
        services.AddSingleton<ICredentialSource>(new StaticSource(accounts ?? []));
        services.AddSingleton(new HttpClient(fake, disposeHandler: false));
        services.AddSingleton(sp => new UpstreamClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<UpstreamVariant>(),
            sp.GetRequiredService<ILogger<UpstreamClient>>()));
        services.AddSingleton<ChatObservers>();
        services.AddSingleton(sp => new RequestLog(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new AdminEndpoints(
            sp.GetRequiredService<GatewayOptions>(),
            sp.GetRequiredService<UpstreamVariant>(),
            sp.GetRequiredService<ICredentialSource>(),
            sp.GetRequiredService<RequestLog>(),
            sp.GetRequiredService<ILogger<AdminEndpoints>>()));
        services.AddSingleton<GatewayApp>();
        services.AddSingleton<IChatObserver, NullChatObserver>();

        var app = builder.Build();
        app.Services.GetRequiredService<GatewayApp>().Map(app);
        await app.StartAsync().ConfigureAwait(false);

        var address = app.Urls.First();
        return AdminHost.Create(
            app, address, vaultPath, fake,
            app.Services.GetRequiredService<AdminEndpoints>());
    }

    private sealed class StaticSource(IReadOnlyList<VaultEntry> entries) : ICredentialSource
    {
        public IReadOnlyList<VaultEntry> Load() => entries;
    }

    /// <summary>
    /// 造一个带独特 token 标记的账号 —— 用来验「响应里不含 token 明文」。
    /// </summary>
    private static VaultEntry CanaryAccount(string id = "canary-uuid:ent-1") => new()
    {
        Id = id,
        Uid = "canary-user",
        EnterpriseId = "ent-1",
        Domain = "workbuddy.cn",
        AccessToken = SecretAccessToken,
        RefreshToken = SecretRefreshToken,
        ExpiresAtMs = 1793731164678,
        RefreshExpiresAtMs = 1793903964678,
        Label = "金丝雀号",
        AddedAt = "2026-09-24T09:35:12.642Z",
        LastSeenAt = "2026-10-06T18:39:24.680Z",
    };

    private static VaultEntry SimpleAccount(string id, string label) => new()
    {
        Id = id,
        Uid = "u-" + id,
        EnterpriseId = "ent-1",
        Domain = "workbuddy.cn",
        AccessToken = "token-" + id,
        RefreshToken = "refresh-" + id,
        ExpiresAtMs = 1793731164678,
        Label = label,
    };

    private static async Task<JsonNode> ParseAsync(HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    // ══ 鉴权 ══════════════════════════════════════════════════

    [Fact]
    public async Task 管理面_无_key_返回401()
    {
        // ⚠ /admin/* 与 /healthz 不同：它暴露账号标签与配置，必须要求鉴权。
        await using var host = await StartAdminAsync([CanaryAccount()], apiKey: "secret-key");

        using var response = await host.GetAsync("/admin/accounts");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 管理面_错_key_返回401()
    {
        await using var host = await StartAdminAsync([CanaryAccount()], apiKey: "secret-key");

        using var response = await host.GetAsync("/admin/accounts", apiKey: "wrong-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 管理面_对_key_放行()
    {
        await using var host = await StartAdminAsync([CanaryAccount()], apiKey: "secret-key");

        using var response = await host.GetAsync("/admin/accounts", apiKey: "secret-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task 管理面鉴权在路由之前_无key打不存在的路径也是401不是404()
    {
        // 与 GatewayApp 的顺序一致：鉴权（:161）在路由兜底（:188）之前。
        // 这条钉住「未授权者连『有哪些端点』都不该探到」。
        await using var host = await StartAdminAsync([CanaryAccount()], apiKey: "secret-key");

        using var response = await host.GetAsync("/admin/不存在的区域");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ══ 账号：列表 ════════════════════════════════════════════

    [Fact]
    public async Task 账号列表_含_App_DTO_要求的全部字段()
    {
        // 字段名以 BuddyDeck.App 的 AccountSummary 为准（我读了那个文件）。
        await using var host = await StartAdminAsync([CanaryAccount()]);

        using var response = await host.GetAsync("/admin/accounts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var array = (await ParseAsync(response)).AsArray();
        var item = Assert.IsType<JsonObject>(array[0]);

        Assert.Equal("canary-uuid:ent-1", item["id"]!.GetValue<string>());
        Assert.Equal("金丝雀号", item["label"]!.GetValue<string>());
        Assert.Equal("canary-user", item["uid"]!.GetValue<string>());
        Assert.Equal("ent-1", item["enterpriseId"]!.GetValue<string>());
        Assert.Equal("workbuddy.cn", item["domain"]!.GetValue<string>());
        Assert.Equal(1793731164678, item["expiresAtMs"]!.GetValue<long>());
        Assert.Equal(1793903964678, item["refreshExpiresAtMs"]!.GetValue<long>());
        Assert.True(item["active"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 账号列表_绝不回_token_明文()
    {
        // ★ 硬要求。用独特标记值：只要它出现在响应体里就会被抓到。
        await using var host = await StartAdminAsync([CanaryAccount()]);

        using var response = await host.GetAsync("/admin/accounts");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(SecretAccessToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretRefreshToken, body, StringComparison.Ordinal);
        // 连「accessToken」这个键都不该存在
        Assert.DoesNotContain("accessToken", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 账号列表_空保险库回空数组而不是报错()
    {
        await using var host = await StartAdminAsync(accounts: [], seedVault: true);

        using var response = await host.GetAsync("/admin/accounts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await ParseAsync(response)).AsArray());
    }

    [Fact]
    public async Task 账号列表_保险库文件不存在时也不报500()
    {
        await using var host = await StartAdminAsync(seedVault: false);

        using var response = await host.GetAsync("/admin/accounts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await ParseAsync(response)).AsArray());
    }

    // ══ 账号：写操作 ══════════════════════════════════════════

    [Fact]
    public async Task 激活_切换_activeId_并原子写回()
    {
        var a = SimpleAccount("id-a:", "号 A");
        var b = SimpleAccount("id-b:", "号 B");
        await using var host = await StartAdminAsync([a, b]);

        // 初始 activeId 是第一条
        Assert.Equal("id-a:", Vault.Read(host.VaultPath)!.ActiveId);

        using var response = await host.PostAsync("/admin/accounts/id-b%3A/activate", "{}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ⚠ 写回后 activeId 必须是 id-b:，**末尾冒号不能被 trim**
        //（那是格式的一部分，见 HANDOVER 第三节第 5 条）
        var doc = Vault.Read(host.VaultPath)!;
        Assert.Equal("id-b:", doc.ActiveId);
        Assert.Equal(2, doc.Accounts.Count);
    }

    [Fact]
    public async Task 激活_不存在的账号_404()
    {
        await using var host = await StartAdminAsync([SimpleAccount("id-a:", "号 A")]);

        using var response = await host.PostAsync("/admin/accounts/不存在/activate", "{}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 重命名_只改标签_不动_token()
    {
        await using var host = await StartAdminAsync([CanaryAccount("id-a:")]);

        using var response = await host.PostAsync("/admin/accounts/id-a%3A/rename", """{"label":"新名字"}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = Vault.Read(host.VaultPath)!;
        Assert.Equal("新名字", doc.Accounts[0].Label);
        // token 原样保留（重命名不该碰凭证）
        Assert.Equal(SecretAccessToken, doc.Accounts[0].AccessToken);
        Assert.Equal(SecretRefreshToken, doc.Accounts[0].RefreshToken);
    }

    [Fact]
    public async Task 删除_移除账号_并把_activeId_挪给剩下的第一条()
    {
        var a = SimpleAccount("id-a:", "号 A");
        var b = SimpleAccount("id-b:", "号 B");
        await using var host = await StartAdminAsync([a, b]);

        // 删掉当前 active 的那条
        using var response = await host.PostAsync("/admin/accounts/id-a%3A/remove", "{}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = Vault.Read(host.VaultPath)!;
        Assert.Single(doc.Accounts);
        Assert.Equal("id-b:", doc.Accounts[0].Id);
        // ⚠ activeId 不能还指着已删的那条 —— 否则界面上「当前号」是空的
        Assert.Equal("id-b:", doc.ActiveId);
    }

    [Fact]
    public async Task 删除最后一个号_activeId_清空而不是留着悬空id()
    {
        await using var host = await StartAdminAsync([SimpleAccount("id-a:", "号 A")]);

        using var response = await host.PostAsync("/admin/accounts/id-a%3A/remove", "{}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = Vault.Read(host.VaultPath)!;
        Assert.Empty(doc.Accounts);
        Assert.Equal("", doc.ActiveId);
    }

    [Fact]
    public async Task 删除不存在的账号_404_且保险库不被改动()
    {
        await using var host = await StartAdminAsync([SimpleAccount("id-a:", "号 A")]);

        using var response = await host.PostAsync("/admin/accounts/没有这个/remove", "{}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(Vault.Read(host.VaultPath)!.Accounts);
    }

    // ══ 账号：导入 ════════════════════════════════════════════

    [Fact]
    public async Task 导入_从桌面登录文件形态()
    {
        await using var host = await StartAdminAsync(accounts: [], seedVault: true);

        // 形态 A：桌面 App 的登录文件（嵌套 auth + account）
        var payload = """
        {"auth":{"accessToken":"imported-token","refreshToken":"imported-refresh","expiresAt":1793731164678},
         "account":{"uid":"imported-uid","enterpriseId":"imported-ent","domain":"workbuddy.cn","nickname":"导入的号"}}
        """;

        var body = new JsonObject { ["payload"] = payload }.ToJsonString();
        using var response = await host.PostAsync("/admin/accounts/import", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var doc = Vault.Read(host.VaultPath)!;
        var entry = Assert.Single(doc.Accounts);
        Assert.Equal("imported-token", entry.AccessToken);
        Assert.Equal("imported-uid", entry.Uid);
        Assert.Equal("imported-ent", entry.EnterpriseId);
        Assert.Equal("导入的号", entry.Label);
        // ⚠ id 格式 `uuid:enterpriseId`
        Assert.EndsWith(":imported-ent", entry.Id, StringComparison.Ordinal);
        // 空库导入第一条要自动成为 active
        Assert.Equal(entry.Id, doc.ActiveId);
    }

    [Fact]
    public async Task 导入_同样可以吃裸凭证形态()
    {
        await using var host = await StartAdminAsync(accounts: [], seedVault: true);

        var payload = """
        {"accessToken":"bare-token","refreshToken":"bare-refresh","expiresAtMs":1793731164678,
         "uid":"bare-uid","enterpriseId":"bare-ent","domain":"workbuddy.cn"}
        """;

        using var response = await host.PostAsync("/admin/accounts/import",
            new JsonObject { ["payload"] = payload }.ToJsonString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("bare-token", Vault.Read(host.VaultPath)!.Accounts[0].AccessToken);
    }

    [Fact]
    public async Task 导入_同一个_uid_重复导入是更新不是新增()
    {
        // 用户重新登录后重复导入是常态，每次堆一条会让列表越来越乱。
        await using var host = await StartAdminAsync(accounts: [], seedVault: true);

        var first = """
        {"auth":{"accessToken":"v1-token"},"account":{"uid":"same-uid","enterpriseId":"same-ent"}}
        """;
        var second = """
        {"auth":{"accessToken":"v2-token"},"account":{"uid":"same-uid","enterpriseId":"same-ent"}}
        """;

        await host.PostAsync("/admin/accounts/import", new JsonObject { ["payload"] = first }.ToJsonString());
        await host.PostAsync("/admin/accounts/import", new JsonObject { ["payload"] = second }.ToJsonString());

        var doc = Vault.Read(host.VaultPath)!;
        Assert.Single(doc.Accounts);
        Assert.Equal("v2-token", doc.Accounts[0].AccessToken);
    }

    [Fact]
    public async Task 导入_畸形_JSON_返回400()
    {
        await using var host = await StartAdminAsync(accounts: [], seedVault: true);

        using var response = await host.PostAsync("/admin/accounts/import",
            new JsonObject { ["payload"] = "{这不是 JSON" }.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 导入_没有_accessToken_返回400()
    {
        await using var host = await StartAdminAsync(accounts: [], seedVault: true);

        using var response = await host.PostAsync("/admin/accounts/import",
            new JsonObject { ["payload"] = """{"uid":"x"}""" }.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 导入_缺少_payload_字段_返回400()
    {
        await using var host = await StartAdminAsync(accounts: [], seedVault: true);

        using var response = await host.PostAsync("/admin/accounts/import", "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ══ 账号：续期（如实回 501，不假装成功）═══════════════════

    [Fact]
    public async Task 续期_未实现时返回501_而不是假装成功()
    {
        // ★ 宁可 501 也不要「假装成功」—— 后者会让界面显示续期成功而 token 没变。
        await using var host = await StartAdminAsync([SimpleAccount("id-a:", "号 A")]);

        using var response = await host.PostAsync("/admin/accounts/id-a%3A/refresh", "{}");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        var json = await ParseAsync(response);
        var message = json["error"]!["message"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public async Task 续期_不存在的账号仍是404而不是501()
    {
        // 顺序很重要：先判存在性，再报「没实现」—— 否则用户会以为
        // 「这个功能没做」，其实是「你给的 id 不对」。
        await using var host = await StartAdminAsync([SimpleAccount("id-a:", "号 A")]);

        using var response = await host.PostAsync("/admin/accounts/没有这个/refresh", "{}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ══ 分池 ══════════════════════════════════════════════════

    [Fact]
    public async Task 池列表_回一个池且字段齐()
    {
        await using var host = await StartAdminAsync([SimpleAccount("id-a:", "号 A"), SimpleAccount("id-b:", "号 B")]);

        using var response = await host.GetAsync("/admin/pools");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var item = Assert.IsType<JsonObject>((await ParseAsync(response)).AsArray()[0]);
        Assert.Equal("default", item["id"]!.GetValue<string>());
        Assert.True(item["isDefault"]!.GetValue<bool>());
        Assert.True(item["running"]!.GetValue<bool>());
        // 号数要与保险库实际一致
        Assert.Equal(2, item["accountCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task 池启停_真的会生效()
    {
        await using var host = await StartAdminAsync([SimpleAccount("id-a:", "号 A")]);

        using var response = await host.PostAsync("/admin/pools/default/state", """{"running":false}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 落到进程级开关 —— 断言的是**实现状态**，不是响应体
        Assert.False(host.Admin.PoolRunning);
    }

    [Fact]
    public async Task 池启停_缺_running_字段_400()
    {
        await using var host = await StartAdminAsync([SimpleAccount("id-a:", "号 A")]);

        using var response = await host.PostAsync("/admin/pools/default/state", "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 池启停_未知池_404()
    {
        await using var host = await StartAdminAsync([SimpleAccount("id-a:", "号 A")]);

        using var response = await host.PostAsync("/admin/pools/不存在的池/state", """{"running":true}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 新增池_真的能被新增并出现在列表里()
    {
        // ## ⚠ t58 之后的语义变更（必读）
        //
        // 这条原来叫「新增池_501_并给出理由」，断言的是 **501 Not Implemented**。
        // 原意（t17 写的）是：
        //   > 不假装支持：进程内多池需要每池独立的账号集与端口，那是多实例架构。
        //   > 假装支持会让界面显示一堆点了没反应的假池子。
        //
        // **那个原意没有被推翻，只是被实现了。** 「池 = 一套账号 + 一个独立端口」
        // 依然成立 —— 正因为如此，池注册表才必须**跨进程存活**（落成 pools.json），
        // 而不是内存对象。t58 做的就是这件事：读那张表、回答有哪些池、按表改配置。
        //
        // 所以这里断言新语义（真能新增），而「不假装支持」那条约束改由
        // 下面两条守着：**端口必须不与已有池冲突**、**目录名安全校验生效**。
        await using var host = await StartAdminAsync();

        using var response = await host.PostAsync("/admin/pools", """{"name":"新池"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await ParseAsync(response);
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(json["id"]!.GetValue<string>()));

        // ★ 新池必须真的出现在列表里（不是只在响应里说"成功"）
        using var list = await host.GetAsync("/admin/pools");
        var array = (await ParseAsync(list)).AsArray();
        Assert.Contains(array, p => p!["id"]!.GetValue<string>() == json["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task 删除池_默认池被拒_新池能删且从列表消失()
    {
        // ## ⚠ t58 之后的语义变更（必读）
        //
        // 这条原来叫「删除池_501_且说明应由进程管理做」，断言 **501**。
        // 原意（t17）：
        //   > 删掉本实例那个池等于关掉网关自己，应该由进程管理（面板启停按钮）做。
        //
        // **那条原意对「默认池」仍然正确** —— 所以默认池依然被拒（见下）。
        // 但**普通池**是注册表里的一个条目，删条目 ≠ 关进程，端点完全可以做。
        //
        // t58 的取舍：删注册表条目，**不删保险库文件**（那里面是明文 token，
        // 是用户最贵的东西；文件留着，用户后悔了还能手工捞回来）。
        await using var host = await StartAdminAsync();

        // ① 默认池不能删 —— 原意保留（它等于本进程自己 + 主保险库）
        using (var def = await host.PostAsync("/admin/pools/default/remove", "{}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, def.StatusCode);
        }

        // ② 新池能删，且从列表消失
        string id;
        using (var created = await host.PostAsync("/admin/pools", """{"name":"临时池"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            id = (await ParseAsync(created))["id"]!.GetValue<string>();
        }

        using (var removed = await host.PostAsync($"/admin/pools/{Uri.EscapeDataString(id)}/remove", "{}"))
        {
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            // 保险库文件没删 —— 告诉调用方它在哪
            var json = await ParseAsync(removed);
            Assert.False(string.IsNullOrWhiteSpace(json["vaultKeptAt"]!.GetValue<string>()));
        }

        using var list = await host.GetAsync("/admin/pools");
        var array = (await ParseAsync(list)).AsArray();
        Assert.DoesNotContain(array, p => p!["id"]!.GetValue<string>() == id);
    }

    [Fact]
    public async Task 池的目录名安全校验生效_拒绝路径穿越()
    {
        // ★ t58 自己提出的那条防御，值得单独一条用例。
        //
        // `DirectoryName` 来自 pools.json（用户可手改），而删池会
        // `Directory.Delete(recursive: true)`。不校验的话：
        //     "..\\..\\..\\重要文件"  →  删池时把用户别的文件删了
        //
        // 这条在**纯函数层**测（IsSafeDirectoryName），
        // 因为端点层不允许用户直接指定 DirectoryName（由 MakeDirectoryName 生成）。
        Assert.True(PoolRegistry.IsSafeDirectoryName(""));
        Assert.True(PoolRegistry.IsSafeDirectoryName("p-志强"));

        Assert.False(PoolRegistry.IsSafeDirectoryName(@"..\..\..\重要文件"));
        Assert.False(PoolRegistry.IsSafeDirectoryName("../../etc"));
        Assert.False(PoolRegistry.IsSafeDirectoryName(@"p-a\b"));
        Assert.False(PoolRegistry.IsSafeDirectoryName("没有前缀"));

        // 归一化防御：即使名字看着合法，也要确认它真落在 pools 目录里
        Assert.Throws<InvalidOperationException>(() =>
            PoolRegistry.ResolveVaultHome(
                Path.Combine(Path.GetTempPath(), "bd-pools-x"),
                Path.GetTempPath(),
                new PoolConfig { Name = "恶意", DirectoryName = @"..\..\..\重要文件" }));
    }

    [Fact]
    public async Task 新增池_端口不与已有池冲突()
    {
        // ★ 「不假装支持」的另一半：新池必须拿到**真的能用**的独立端口。
        await using var host = await StartAdminAsync();

        for (var i = 0; i < 3; i++)
        {
            using var created = await host.PostAsync("/admin/pools", $$"""{"name":"池{{i}}"}""");
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        using var list = await host.GetAsync("/admin/pools");
        var ports = (await ParseAsync(list)).AsArray()
            .Select(p => p!["port"]!.GetValue<int>())
            .ToList();

        Assert.Equal(4, ports.Count);                              // 默认池 + 3 个
        Assert.Equal(4, ports.Distinct().Count());                 // ★ 端口互不相同
    }

    // ══ 日志 ══════════════════════════════════════════════════

    [Fact]
    public async Task 日志_回_App_要的_lines_形状()
    {
        // App 的 LogListDto 是 { lines: [{at, level, message}] }，不是 LogEntry 原形。
        await using var host = await StartAdminAsync();

        using var response = await host.GetAsync("/admin/logs?lines=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await ParseAsync(response);
        Assert.NotNull(json["lines"]);
    }

    [Fact]
    public async Task 日志_发生过请求后能读到脱敏行()
    {
        var host = await StartAdminAsync([SimpleAccount("id-a:", "号 A")]);
        await using (host)
        {
            // 发一次真聊天，让日志里有东西 —— 用独特标记的提示词，
            // 检查它**不会**出现在日志里。
            const string secretPrompt = "ZZTOP-ADMIN-LOG-CANARY-7f3a";
            var chatBody = """
            {"model":"hy3","messages":[{"role":"user","content":"ZZTOP-ADMIN-LOG-CANARY-7f3a"}],"stream":false}
            """;
            using (var chat = await host.PostAsync("/v1/chat/completions", chatBody))
            {
                await chat.Content.ReadAsStringAsync();
            }

            using var response = await host.GetAsync("/admin/logs?lines=50");
            var body = await response.Content.ReadAsStringAsync();

            // ★ 脱敏硬要求：日志里绝不能出现提示词
            Assert.DoesNotContain(secretPrompt, body, StringComparison.Ordinal);

            var json = JsonNode.Parse(body)!;
            var lines = json["lines"]!.AsArray();
            Assert.NotEmpty(lines);

            // 每行都要有 App 要的三个字段
            foreach (var line in lines)
            {
                Assert.NotNull(line!["at"]);
                Assert.False(string.IsNullOrWhiteSpace(line["level"]!.GetValue<string>()));
                Assert.NotNull(line["message"]);
            }
        }
    }

    [Fact]
    public async Task 日志_lines_参数非法时用默认值不报错()
    {
        await using var host = await StartAdminAsync();

        using var response = await host.GetAsync("/admin/logs?lines=不是数字");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task 日志_lines_超大值被夹住不会去分配大数组()
    {
        await using var host = await StartAdminAsync();

        using var response = await host.GetAsync("/admin/logs?lines=2000000000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ══ 设置 ══════════════════════════════════════════════════

    [Fact]
    public async Task 设置_读回_App_DTO_要求的全部字段()
    {
        await using var host = await StartAdminAsync();

        using var response = await host.GetAsync("/admin/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await ParseAsync(response);
        // ⚠ 这里断言的是「port 字段存在且是整数」，**不是 > 0** ——
        // 测试夹具用 Port=0（让内核挑空闲端口），而 0 是个合法取值
        // （与 GatewayOptions 的注释一致：0 = 系统分配）。
        // 写成 > 0 会把夹具的正常配置误判成缺陷。
        Assert.True(json["port"] is JsonValue);
        Assert.True(json["port"]!.GetValue<int>() >= 0);
        Assert.Equal("workbuddy", json["variantId"]!.GetValue<string>());
        Assert.NotNull(json["apiKey"]);
        Assert.False(string.IsNullOrWhiteSpace(json["vaultPath"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(json["logPath"]!.GetValue<string>()));
    }

    [Fact]
    public async Task 设置_写入后能读回()
    {
        await using var host = await StartAdminAsync();

        var newSettings = """
        {"port":9123,"apiKey":"new-key","variantId":"workbuddy","vaultPath":"C:\\vault.json","logPath":"C:\\logs"}
        """;

        using (var save = await host.PostAsync("/admin/settings", newSettings))
        {
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        }

        // ★ 保存了 apiKey 后鉴权**立即生效**（这是本轮修的根因），
        //   所以读回要带新 key，否则 401。
        using var response = await host.GetAsync("/admin/settings", apiKey: "new-key");
        var json = await ParseAsync(response);

        Assert.Equal(9123, json["port"]!.GetValue<int>());
        Assert.Equal("new-key", json["apiKey"]!.GetValue<string>());
        Assert.Equal("C:\\vault.json", json["vaultPath"]!.GetValue<string>());
    }

    [Fact]
    public async Task 设置_保存新key后_鉴权立即生效()
    {
        // ★ 用户实测：保存 API key 后「报错网关要求 apikey 而当前没有或不对」。
        // 根因：/admin/settings 保存只改了 _settings 内存副本，鉴权读的是
        // _options.ApiKey（构造时注入、永不更新）→ 保存后立即 401。
        // 这条钉住「保存后 _options.ApiKey 同步」。
        //
        // 起一个**没配 key**的宿主（不鉴权），保存新 key 后，
        // 不带 key 的访问必须变成 401，带新 key 的必须 200。
        await using var host = await StartAdminAsync([CanaryAccount()], apiKey: null);

        // 保存前：无 key 也能读（不鉴权）
        using (var before = await host.GetAsync("/admin/settings"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        // 保存新 key（⚠ key 里不能有非 ASCII —— HTTP 头只收 ASCII）
        using (var save = await host.PostAsync("/admin/settings", """{"apiKey":"sk-new-secret-key"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        }

        // ★ 保存后立即生效：不带 key → 401
        using (var after = await host.GetAsync("/admin/settings"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        }

        // 带新 key → 200
        using (var withKey = await host.GetAsync("/admin/settings", apiKey: "sk-new-secret-key"))
        {
            Assert.Equal(HttpStatusCode.OK, withKey.StatusCode);
        }

        // 带旧 key（任意非新值）→ 401
        using (var wrongKey = await host.GetAsync("/admin/settings", apiKey: "some-other-key"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        }
    }

    [Fact]
    public async Task 设置_非法端口_400()
    {
        await using var host = await StartAdminAsync();

        using var response = await host.PostAsync("/admin/settings", """{"port":99999}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 设置_未知变体_400_而不是存进去()
    {
        // 存一个不认识的变体进去，下次启动会直接抛 —— 在入口拦掉。
        await using var host = await StartAdminAsync();

        using var response = await host.PostAsync("/admin/settings", """{"variantId":"不存在的变体"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 设置_只改一个字段时其余保持不变()
    {
        await using var host = await StartAdminAsync();

        var before = await ParseAsync(await host.GetAsync("/admin/settings"));

        using (var save = await host.PostAsync("/admin/settings", """{"port":9200}"""))
        {
            Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        }

        var after = await ParseAsync(await host.GetAsync("/admin/settings"));

        Assert.Equal(9200, after["port"]!.GetValue<int>());
        // 其它字段不该被这次局部更新清空
        Assert.Equal(before["variantId"]!.GetValue<string>(), after["variantId"]!.GetValue<string>());
        Assert.Equal(before["vaultPath"]!.GetValue<string>(), after["vaultPath"]!.GetValue<string>());
    }

    // ══ 未知 admin 路由 ═══════════════════════════════════════

    [Fact]
    public async Task 未知_admin_子路径_404_并说明是_admin_路由()
    {
        await using var host = await StartAdminAsync();

        using var response = await host.GetAsync("/admin/这不存在");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var message = (await ParseAsync(response))["error"]!["message"]!.GetValue<string>();
        Assert.Contains("admin", message, StringComparison.OrdinalIgnoreCase);
    }

    // ══ 保险库格式不被破坏 ════════════════════════════════════

    [Fact]
    public async Task 写操作后保险库的时间字段类型不变_数值_ms_加_ISO_字符串()
    {
        // ★ HANDOVER 坑 2：expiresAtMs 是**数字**、addedAt 是 **ISO 字符串**。
        // 类型写反的话读真实保险库会抛 JsonException。
        await using var host = await StartAdminAsync([CanaryAccount("id-a:")]);

        using (var response = await host.PostAsync("/admin/accounts/id-a%3A/rename", """{"label":"改个名"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var raw = File.ReadAllText(host.VaultPath);
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var account = doc.RootElement.GetProperty("accounts")[0];

        // 数字
        Assert.Equal(System.Text.Json.JsonValueKind.Number, account.GetProperty("expiresAtMs").ValueKind);
        // ISO 字符串
        Assert.Equal(System.Text.Json.JsonValueKind.String, account.GetProperty("addedAt").ValueKind);
        Assert.Contains("T", account.GetProperty("addedAt").GetString()!, StringComparison.Ordinal);

        // activeId 末尾冒号保住
        Assert.EndsWith(":", doc.RootElement.GetProperty("activeId").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 写操作不产生临时文件残留()
    {
        // Vault.Write 是「临时文件 + 原子替换」，正常路径下临时文件必须已被移走。
        await using var host = await StartAdminAsync([SimpleAccount("id-a:", "号 A")]);

        using (var response = await host.PostAsync("/admin/accounts/id-a%3A/rename", """{"label":"x"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.False(File.Exists(host.VaultPath + ".tmp"), "原子写的临时文件残留了");
    }
}
