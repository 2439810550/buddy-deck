using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// 多号池注册表的测试（t58）。
///
/// ## 这一批测的是什么
///
/// t58 之前「分池」是**硬编码**：`GET /admin/pools` 固定回一个长度为 1 的数组
/// （就是本实例），`POST /admin/pools`（新增）回 501。
/// 用户原话「可以启用多个号池像中转站一样」—— 那是**能用的功能**，不是缺个界面。
///
/// 本文件分两层：
///   · **注册表层**（<see cref="PoolRegistry"/> 纯函数）—— 路径安全、端口分配、去重
///   · **端到端层**（真 Kestrel + 真 HTTP）—— 端点真能读、能写、能增删
///
/// ## 为什么路径安全那几条要单独测
///
/// `DirectoryName` 来自 `pools.json` —— **用户可手改**。而删池会
/// `Directory.Delete(recursive: true)`。旧版在注释里专门写了那个攻击形态
/// （`"..\\..\\..\\重要文件"`），**它能让删池变成删用户文件**。
/// 这类「不越界」的不变量值得用测试钉死。
/// </summary>
public class PoolRegistryTests
{
    // ══════════════════════════════════════════════════════════
    // ① 注册表层：路径安全（删库级防御）
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 目录名_中文保留_不退化成一串端口号()
    {
        // 旧版早期只留 ASCII，结果 pools/ 下全是 p-port8788 ——
        // 用户想手工备份某个池都认不出哪个是哪个。
        var name = PoolRegistry.MakeDirectoryName("志强", 8788);

        Assert.Equal("p-志强", name);
    }

    [Fact]
    public void 目录名_去掉路径分隔符与盘符()
    {
        // 这三个会造出路径穿越或跳到别处 —— 最大的危险
        var name = PoolRegistry.MakeDirectoryName(@"a/b\c:d", 8788);

        Assert.DoesNotContain("/", name, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", name, StringComparison.Ordinal);
        Assert.DoesNotContain(":", name, StringComparison.Ordinal);
    }

    [Fact]
    public void 目录名_去掉_Windows_保留字符()
    {
        var name = PoolRegistry.MakeDirectoryName("""a*b?c"d<e>f|g""", 8788);

        foreach (var ch in @"*?""<>|")
        {
            Assert.DoesNotContain(ch.ToString(), name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 目录名_不留下连续的点()
    {
        // 旧版实测：`a/../../b` 丢掉斜杠后剩 `a....b`，里头带着 ".."
        var name = PoolRegistry.MakeDirectoryName("a/../../b", 8788);

        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
    }

    [Fact]
    public void 目录名_首尾不能是点或空格()
    {
        // Windows 文件名不能以点或空格结尾（会默默被替掉）
        var name = PoolRegistry.MakeDirectoryName("  ..测试..  ", 8788);

        Assert.False(name.EndsWith('.') || name.EndsWith(' '));
        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
    }

    [Fact]
    public void 目录名_全空时退回端口号()
    {
        var name = PoolRegistry.MakeDirectoryName("   ", 8788);

        Assert.StartsWith("p-", name, StringComparison.Ordinal);
        Assert.Contains("8788", name, StringComparison.Ordinal);
    }

    [Fact]
    public void 目录名_有长度上限()
    {
        var name = PoolRegistry.MakeDirectoryName(new string('长', 100), 8788);

        Assert.True(name.Length <= 30, $"实际 {name.Length}");
    }

    // ── IsSafeDirectoryName：允许清单 ────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("p-志强")]
    [InlineData("p-port8788")]
    public void 目录名校验_合法的放行(string name)
    {
        Assert.True(PoolRegistry.IsSafeDirectoryName(name));
    }

    [Theory]
    [InlineData(@"..\..\..\重要文件")]
    [InlineData("../../etc")]
    [InlineData("p-a..b")]            // 归一化后可能是上级
    [InlineData(@"p-a\b")]            // 路径分隔符
    [InlineData("p-a/b")]
    [InlineData("p-a:b")]
    [InlineData("p-a*b")]
    [InlineData("没有前缀")]
    [InlineData("p-a\u0001b")]        // 控制字符
    public void 目录名校验_危险的一律拒(string name)
    {
        // ★ 这是「删库级」防御：不校验的话，删池会删到用户别的文件。
        Assert.False(PoolRegistry.IsSafeDirectoryName(name));
    }

    // ── ResolveVaultHome：归一化后的容器检查 ────────────────

    [Fact]
    public void 保险库目录_默认池用默认位置()
    {
        var poolsRoot = Path.Combine(Path.GetTempPath(), "bd-pools");
        var defaultHome = Path.Combine(Path.GetTempPath(), "bd-default");

        var home = PoolRegistry.ResolveVaultHome(poolsRoot, defaultHome,
            new PoolConfig { Name = "默认池", DirectoryName = "" });

        Assert.Equal(Path.GetFullPath(defaultHome), home);
    }

    [Fact]
    public void 保险库目录_普通池落在_pools_子目录下()
    {
        var poolsRoot = Path.Combine(Path.GetTempPath(), "bd-pools");

        var home = PoolRegistry.ResolveVaultHome(poolsRoot, Path.GetTempPath(),
            new PoolConfig { Name = "志强", DirectoryName = "p-志强" });

        Assert.StartsWith(Path.GetFullPath(poolsRoot) + Path.DirectorySeparatorChar, home, StringComparison.Ordinal);
        Assert.EndsWith("p-志强", home, StringComparison.Ordinal);
    }

    [Fact]
    public void 保险库目录_不安全的目录名要抛而不是拼出来()
    {
        // ★ 归一化防御：不抛的话就会返回一个「跑出去」的路径，
        //   而调用方（删池）会拿它去 Directory.Delete(recursive: true)。
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PoolRegistry.ResolveVaultHome(
                Path.Combine(Path.GetTempPath(), "bd-pools"), Path.GetTempPath(),
                new PoolConfig { Name = "恶意", DirectoryName = @"..\..\..\重要文件" }));

        Assert.Contains("不合法", ex.Message, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════
    // ② 端口分配
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 端口分配_避开已分配给别的池的端口()
    {
        // ★ 旧版实测踩过的坑：只看「此刻有没人监听」会漏掉
        //   「已分配但那个池现在没跑」的端口 → 两个池分到同一端口。
        var existing = new List<PoolConfig>
        {
            new() { Name = "默认池", DirectoryName = "", Port = 8787 },
            new() { Name = "A", DirectoryName = "p-a", Port = 8788 },
        };

        var port = PoolRegistry.PickPort(existing);

        Assert.NotEqual(8787, port);
        Assert.NotEqual(8788, port);
        Assert.True(port >= PoolRegistry.FirstAutoPort);
    }

    [Fact]
    public void 端口分配_用户指定的空闲端口就用它()
    {
        var existing = new List<PoolConfig> { new() { Name = "默认池", DirectoryName = "", Port = 8787 } };

        // 挑一个几乎不可能被占的高位端口
        var port = PoolRegistry.PickPort(existing, preferred: 45678);

        Assert.Equal(45678, port);
    }

    [Fact]
    public void 端口分配_用户指定的端口已被别的池占_则另挑()
    {
        var existing = new List<PoolConfig>
        {
            new() { Name = "默认池", DirectoryName = "", Port = 8787 },
            new() { Name = "A", DirectoryName = "p-a", Port = 45678 },
        };

        var port = PoolRegistry.PickPort(existing, preferred: 45678);

        Assert.NotEqual(45678, port);
    }

    // ══════════════════════════════════════════════════════════
    // ③ 去重
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 目录名去重_重名时加序号()
    {
        var existing = new List<PoolConfig> { new() { Name = "测试", DirectoryName = "p-测试" } };

        Assert.Equal("p-测试-2", PoolRegistry.UniqueDirectoryName("p-测试", existing));
    }

    [Fact]
    public void 目录名去重_不重名时原样()
    {
        Assert.Equal("p-新池", PoolRegistry.UniqueDirectoryName("p-新池", []));
    }

    // ══════════════════════════════════════════════════════════
    // ④ 读写 pools.json
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 读不存在的文件_回默认池一个()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("bd-poolreg-").FullName, "pools.json");

        var pools = PoolRegistry.Load(path, 8787, out var error);

        Assert.Null(error);
        Assert.Single(pools);
        Assert.True(pools[0].IsDefault);
        Assert.Equal(8787, pools[0].Port);
        Assert.True(pools[0].AutoStart);
    }

    [Fact]
    public void 坏文件_不拦启动且备份成_broken()
    {
        // 分池是增强功能，不能因为它的配置文件坏了就拦住整个网关。
        var dir = Directory.CreateTempSubdirectory("bd-poolreg-").FullName;
        var path = Path.Combine(dir, "pools.json");
        File.WriteAllText(path, "{ 这不是合法 JSON");

        var pools = PoolRegistry.Load(path, 8787, out var error);

        Assert.NotNull(error);                       // 原因要报出来，不静默吞
        Assert.Single(pools);                        // 但退回默认池
        Assert.True(File.Exists(path + ".broken"));  // 且备份了（用户的数据不能丢）
    }

    [Fact]
    public void 保存后能读回来()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("bd-poolreg-").FullName, "pools.json");

        PoolRegistry.Save(path, [
            new PoolConfig { Name = "默认池", DirectoryName = "", Port = 8787, AutoStart = true },
            new PoolConfig { Name = "志强", DirectoryName = "p-志强", Port = 8788, ApiKey = "sk-x" },
        ]);

        var pools = PoolRegistry.Load(path, 8787, out _);

        Assert.Equal(2, pools.Count);
        Assert.True(pools[0].IsDefault);            // 默认池永远排第一
        Assert.Equal("志强", pools[1].Name);
        Assert.Equal(8788, pools[1].Port);
        Assert.Equal("sk-x", pools[1].ApiKey);
    }

    [Fact]
    public void 默认池永远排第一_即使文件里它在后面()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("bd-poolreg-").FullName, "pools.json");
        File.WriteAllText(path, """
        {"version":1,"pools":[
          {"name":"A","directoryName":"p-a","port":8788},
          {"name":"默认池","directoryName":"","port":8787}]}
        """);

        var pools = PoolRegistry.Load(path, 8787, out _);

        Assert.True(pools[0].IsDefault);
    }

    [Fact]
    public void 池之间默认不共用_key()
    {
        // 共用 key 会让用户以为分池没生效
        var path = Path.Combine(Directory.CreateTempSubdirectory("bd-poolreg-").FullName, "pools.json");
        File.WriteAllText(path, """
        {"version":1,"pools":[
          {"name":"默认池","directoryName":"","port":8787},
          {"name":"A","directoryName":"p-a","port":8788},
          {"name":"B","directoryName":"p-b","port":8789}]}
        """);

        var pools = PoolRegistry.Load(path, 8787, out _);
        var keys = pools.Where(p => !p.IsDefault).Select(p => p.ApiKey).ToList();

        Assert.Equal(2, keys.Count);
        Assert.All(keys, k => Assert.False(string.IsNullOrWhiteSpace(k)));
        Assert.NotEqual(keys[0], keys[1]);
    }

    // ══════════════════════════════════════════════════════════
    // ⑤ 端到端：真 Kestrel + 真 HTTP
    // ══════════════════════════════════════════════════════════

    /// <summary>起一个只挂管理面的真网关（省掉上游依赖）。</summary>
    private sealed class AdminHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public HttpClient Client { get; }
        public string LogDir { get; }

        private AdminHost(WebApplication app, string baseAddress, string logDir)
        {
            _app = app;
            LogDir = logDir;
            Client = new HttpClient(new SocketsHttpHandler())
            {
                BaseAddress = new Uri(baseAddress),
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        public Task<HttpResponseMessage> GetAsync(string path)
            => Client.GetAsync(path);

        public Task<HttpResponseMessage> PostAsync(string path, string json)
            => Client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

        public static async Task<AdminHost> StartAsync(string? vaultPath = null)
        {
            var logDir = Directory.CreateTempSubdirectory("bd-pools-e2e-").FullName;

            var options = new GatewayOptions
            {
                Host = "127.0.0.1",
                Port = 47901,                     // 一个高位端口，仅用于本实例的默认池标识
                ApiKey = "",
                Variant = UpstreamVariant.China.Id,
                LogDirectory = logDir,
                // ⚠ 用**非默认文件名**（不是 auth.cn.json）——
                //   这正是 e2e 抓到的那个缺陷的触发条件：默认池的号来自
                //   `--vault`，而旧代码按「池目录约定」把文件名重推成变体默认名。
                VaultPath = vaultPath ?? "",
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
            services.AddSingleton<AccountPool>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<ICredentialSource>(new EmptyVault());
            services.AddSingleton(sp => new RequestLog(sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton(sp => new AdminEndpoints(
                sp.GetRequiredService<GatewayOptions>(),
                sp.GetRequiredService<UpstreamVariant>(),
                sp.GetRequiredService<ICredentialSource>(),
                sp.GetRequiredService<RequestLog>(),
                sp.GetRequiredService<ILogger<AdminEndpoints>>()));

            var app = builder.Build();

            // 只挂管理面（不需要 GatewayApp 那一整套）
            var admin = app.Services.GetRequiredService<AdminEndpoints>();
            app.Run(async ctx =>
            {
                if (!await admin.TryHandleAsync(ctx))
                {
                    ctx.Response.StatusCode = 404;
                }
            });

            await app.StartAsync().ConfigureAwait(false);
            return new AdminHost(app, app.Urls.First(), logDir);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        private sealed class EmptyVault : ICredentialSource
        {
            public IReadOnlyList<VaultEntry> Load() => [];
        }
    }

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task 端到端_初始只有一个默认池()
    {
        await using var host = await AdminHost.StartAsync();

        using var response = await host.GetAsync("/admin/pools");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var array = (await JsonAsync(response)).AsArray();
        Assert.Single(array);

        var pool = array[0]!.AsObject();
        Assert.Equal("default", pool["id"]!.GetValue<string>());
        Assert.True(pool["isDefault"]!.GetValue<bool>());
        Assert.Equal(47901, pool["port"]!.GetValue<int>());
        Assert.NotNull(pool["running"]);
        Assert.NotNull(pool["accountCount"]);
    }

    [Fact]
    public async Task 端到端_新增池_不再回_501()
    {
        // ★ 这条是 t58 的核心：旧代码这里回 501「未实现」。
        await using var host = await AdminHost.StartAsync();

        using var response = await host.PostAsync("/admin/pools", """{"name":"志强"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(json["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task 端到端_新增后列表里有两个池_各有独立端口()
    {
        await using var host = await AdminHost.StartAsync();

        using (var created = await host.PostAsync("/admin/pools", """{"name":"志强"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        using var list = await host.GetAsync("/admin/pools");
        var array = (await JsonAsync(list)).AsArray();

        Assert.Equal(2, array.Count);

        var ports = array.Select(p => p!["port"]!.GetValue<int>()).ToList();
        Assert.Equal(2, ports.Distinct().Count());      // ★ 端口必须不同
        Assert.Contains(47901, ports);

        // 每个池都有完整字段
        foreach (var p in array)
        {
            var o = p!.AsObject();
            Assert.False(string.IsNullOrWhiteSpace(o["id"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(o["displayName"]!.GetValue<string>()));
            Assert.True(o["port"]!.GetValue<int>() > 0);
            Assert.NotNull(o["running"]);
            Assert.NotNull(o["accountCount"]);
        }
    }

    [Fact]
    public async Task 端到端_新池有独立的保险库路径()
    {
        await using var host = await AdminHost.StartAsync();

        using var created = await host.PostAsync("/admin/pools", """{"name":"志强"}""");
        var json = await JsonAsync(created);

        var vaultPath = json["vaultPath"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(vaultPath));
        // 落在 pools 子目录下 —— 这就是账号隔离的结构性保证
        Assert.Contains("pools", vaultPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 端到端_停掉新池_不影响默认池()
    {
        // ★ 契约第 ③ 条：独立启停。
        await using var host = await AdminHost.StartAsync();

        using var created = await host.PostAsync("/admin/pools", """{"name":"志强"}""");
        var id = (await JsonAsync(created))["id"]!.GetValue<string>();

        using (var stop = await host.PostAsync($"/admin/pools/{Uri.EscapeDataString(id)}/state",
                   """{"running":false}"""))
        {
            Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        }

        using var list = await host.GetAsync("/admin/pools");
        var array = (await JsonAsync(list)).AsArray();

        var def = array.First(p => p!["id"]!.GetValue<string>() == "default")!;
        var other = array.First(p => p!["id"]!.GetValue<string>() == id)!;

        Assert.False(other["running"]!.GetValue<bool>());   // 被停的那个
        Assert.True(def["running"]!.GetValue<bool>());      // 默认池不受影响
    }

    [Fact]
    public async Task 端到端_默认池不能被删()
    {
        await using var host = await AdminHost.StartAsync();

        using var response = await host.PostAsync("/admin/pools/default/remove", "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 端到端_删新池_返回保险库保留位置()
    {
        // ★ 删注册表条目**不删保险库文件** —— 那里面是明文 token，
        //   是用户最贵的东西。返回位置让用户知道去哪捞。
        await using var host = await AdminHost.StartAsync();

        using var created = await host.PostAsync("/admin/pools", """{"name":"临时池"}""");
        var id = (await JsonAsync(created))["id"]!.GetValue<string>();

        using var removed = await host.PostAsync($"/admin/pools/{Uri.EscapeDataString(id)}/remove", "{}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var json = await JsonAsync(removed);
        Assert.False(string.IsNullOrWhiteSpace(json["vaultKeptAt"]!.GetValue<string>()));

        // 列表里只剩默认池
        using var list = await host.GetAsync("/admin/pools");
        Assert.Single((await JsonAsync(list)).AsArray());
    }

    [Fact]
    public async Task 端到端_改不存在的池的启停_404()
    {
        await using var host = await AdminHost.StartAsync();

        using var response = await host.PostAsync("/admin/pools/不存在/state", """{"running":true}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 端到端_新增缺_name_400()
    {
        await using var host = await AdminHost.StartAsync();

        using var response = await host.PostAsync("/admin/pools", "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 端到端_重复新增同名池_目录名自动去重()
    {
        await using var host = await AdminHost.StartAsync();

        using (var first = await host.PostAsync("/admin/pools", """{"name":"测试"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        using (var second = await host.PostAsync("/admin/pools", """{"name":"测试"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        using var list = await host.GetAsync("/admin/pools");
        var ids = (await JsonAsync(list)).AsArray().Select(p => p!["id"]!.GetValue<string>()).ToList();

        Assert.Equal(3, ids.Count);                          // 默认池 + 两个
        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task 端到端_池列表落盘_重启后还在()
    {
        // ★ 注册表必须**跨进程存活** —— 池的本质是多进程，
        //   内存对象重启就没了。
        await using var host = await AdminHost.StartAsync();

        using (var created = await host.PostAsync("/admin/pools", """{"name":"持久池"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        var poolsFile = Path.Combine(host.LogDir, "pools.json");
        Assert.True(File.Exists(poolsFile), $"pools.json 应该被落盘：{poolsFile}");

        var reloaded = PoolRegistry.Load(poolsFile, 47901, out _);
        Assert.Equal(2, reloaded.Count);
    }

    // ══════════════════════════════════════════════════════════
    // ⑥ 回归：accountCount 与 /admin/accounts 必须一致
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task 端到端_默认池的_accountCount_等于_账号列表的长度()
    {
        // ★★ 这条是 e2e 在 t61 真机验证时抓到的缺陷的回归测试。
        //
        // ## 缺陷形态（「三个端点讲三个故事」）
        // ```
        // /admin/accounts  → 1 个号
        // /metrics.recent  → accountTag 证明正在用那个号
        // /admin/pools     → accountCount: 0     ← 只有它说 0
        // ```
        //
        // ## 根因
        // 默认池的号来自 **`--vault`**，而 `VaultPathOf` 对**所有**池都按
        // 「池目录约定」重推：`<vault 所在目录>/<变体文件名>`。
        // 于是 `--vault .local/vault-test.json` 会被推成 `.local/auth.cn.json`
        // —— **那个文件不存在** → `Vault.Read` 返回 null → `?? 0` → 报 0。
        //
        // 这条与 t53（UA 缺失被静默吞掉）同源：
        // **「读不到」与「本来就没有」长得一样，是最容易藏住缺陷的形状**。
        //
        // ## 这条测试的意义
        // 它**跨两个端点做交叉断言** —— 单看 `/admin/pools` 永远看不出 0 是错的
        // （「池里没号」是个合理状态）。必须与「另一个端点说有 N 个号」对照才能暴露。
        var vaultPath = Path.Combine(
            Directory.CreateTempSubdirectory("bd-pools-count-").FullName,
            "vault-test.json");   // ← 故意用非默认文件名（auth.cn.json 之外）

        var vault = new VaultDocument
        {
            Version = 1,
            ActiveId = "c1:",
            Accounts =
            [
                new VaultEntry { Id = "c1:", Uid = "u1", AccessToken = "t1", Label = "一号" },
                new VaultEntry { Id = "c2:", Uid = "u2", AccessToken = "t2", Label = "二号" },
                new VaultEntry { Id = "c3:", Uid = "u3", AccessToken = "t3", Label = "三号" },
            ],
        };
        Vault.Write(vaultPath, vault);

        await using var host = await AdminHost.StartAsync(vaultPath: vaultPath);

        // ① /admin/accounts 说有 3 个
        using var accountsResponse = await host.GetAsync("/admin/accounts");
        var accounts = (await JsonAsync(accountsResponse)).AsArray();
        Assert.Equal(3, accounts.Count);

        // ② /admin/pools 的 accountCount 必须**与它一致**（修复前这里是 0）
        using var poolsResponse = await host.GetAsync("/admin/pools");
        var defaultPool = (await JsonAsync(poolsResponse)).AsArray()
            .First(p => p!["id"]!.GetValue<string>() == "default")!;

        Assert.Equal(3, defaultPool["accountCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task 端到端_默认池的_vaultPath_指向实际的_vault_而不是变体默认名()
    {
        // 上一条的「路径来源」版本：`VaultPathOf` 对默认池必须复用
        // `--vault` 解析出来的路径，不能按池目录约定重推成 `auth.cn.json`。
        var vaultPath = Path.Combine(
            Directory.CreateTempSubdirectory("bd-pools-vp-").FullName,
            "some-other-name.json");

        Vault.Write(vaultPath, new VaultDocument
        {
            Version = 1,
            ActiveId = "c1:",
            Accounts = [new VaultEntry { Id = "c1:", Uid = "u1", AccessToken = "t1" }],
        });

        await using var host = await AdminHost.StartAsync(vaultPath: vaultPath);

        // 通过 POST /admin/pools 返回的 vaultPath 观察（它走同一个函数）
        using var created = await host.PostAsync("/admin/pools", """{"name":"新池"}""");
        var json = await JsonAsync(created);

        // 新池（非默认池）走池目录约定 —— 它的路径里应有 pools 子目录
        Assert.Contains("pools", json["vaultPath"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        // 而默认池的 count 能读出来 = 路径对了（上一条已断言 1 个）
        using var poolsResponse = await host.GetAsync("/admin/pools");
        var def = (await JsonAsync(poolsResponse)).AsArray()
            .First(p => p!["id"]!.GetValue<string>() == "default")!;
        Assert.Equal(1, def["accountCount"]!.GetValue<int>());
    }
}
