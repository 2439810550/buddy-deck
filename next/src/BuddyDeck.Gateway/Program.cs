using System.Net;
using System.Text;
using BuddyDeck.Core;
using BuddyDeck.Gateway;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Events;

// ★ 中文乱码修复：Windows 控制台默认 GBK，而 App 读子进程 stdout 用的是 UTF-8。
//   网关写中文日志时若不显式设 UTF-8，stdout 会按系统编码（GBK）输出，
//   面板日志页就全乱码。必须在任何 Console 输出之前设置。
try
{
    Console.OutputEncoding = new UTF8Encoding(false);
}
catch (IOException)
{
    // stdout 被重定向到非控制台（比如无头环境）时设置编码会抛 —— 忽略，
    // 那时编码由重定向目标决定。
}

// 网关入口点。
//
// 旧版是 Node 的 `bin.js serve`。这版是 ASP.NET Core 的最小宿主：
// 解析参数 → 装配 DI → 起 Kestrel → 交给 GatewayApp 处理请求。
//
// ## 装配顺序为什么是现在这样
//
// Serilog 必须**最先**初始化（在 Host 建起来之前），否则启动早期
// （配置解析、DI 构建）的失败信息压根没地方去 —— 恰恰那是最容易出错的地方。
//
// ## 关于端口
//
// 默认 8787（和旧版一致，客户端里写死的接入地址）。要临时起测试实例
// 用 `--port 8791`。**不要**在没有确认的情况下占 8787 ——
// 那是用户正在跑的网关，撞端口会让他以为程序坏了。
var options = GatewayOptions.Parse(args);

var logDirectory = options.ResolveLogDirectory();
Directory.CreateDirectory(logDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    // formatProvider: InvariantCulture —— 日志时间戳不能跟着机器区域设置变，
    // 不然 zh-CN 机器上的日志和 en-US 机器上的对不上（CA1305）。
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
    // 滚动文件：一天一个、单文件 32 MB、保留 7 天。
    // 为什么不写死一个文件：网关是长跑的，日志长起来能把磁盘吃光。
    .WriteTo.File(
        Path.Combine(logDirectory, "gateway-.log"),
        formatProvider: System.Globalization.CultureInfo.InvariantCulture,
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 32 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 7,
        shared: true)
    .CreateLogger();

try
{
    var variant = options.ResolveVariant();

    // 调试/端到端验证时可以把上游整个换掉（用真的 HTTP 假上游）。
    // 只在显式给了 --upstream-base 时生效；生产不设就是真实地址。
    if (!string.IsNullOrEmpty(options.UpstreamBaseOverride))
    {
        variant = variant with
        {
            ChatBase = options.UpstreamBaseOverride,
            BillingBase = options.UpstreamBaseOverride,
        };
        Log.Warning("⚠ 上游已被 --upstream-base 覆盖为 {Base}（仅用于调试/验证）", options.UpstreamBaseOverride);
    }

    var vaultPath = options.ResolveVaultPath();

    // ── 保险库路径的**响亮失败**（t47）────────────────────────
    //
    // ## 为什么这里必须校验（这是真实发生过的静默降级）
    //
    // 用户给相对路径 `.local\vault.json` 启动时，旧行为会去读
    // `src\BuddyDeck.Gateway\.local\vault.json`（进程 cwd 下的），
    // 那个文件不存在 —— 但**没有任何提示**：
    // ```
    // 启动日志正常打印路径     ✓（打印的是用户给的字符串，看着对）
    // GET  /healthz           ✓ 200
    // GET  /v1/models         ✓ 200（不读保险库）
    // POST /v1/chat/...       ✗ 401 not_signed_in「保险库里没有账号」
    // ```
    // **配置看起来被接受了、启动看起来正常，只有聊天时才降级成「没账号」**，
    // 用户会以为是自己账号坏了。这正是「装配层缺陷家族」最阴的一种。
    //
    // 相对路径的解析已在 `ResolveVaultPath` 里按「用户启动时所在目录」修掉了；
    // 这里补的是**第二道防线**：如果解析出来的文件**仍然不存在**，就必须
    // **立刻退出**，而不是带着一个空池子跑起来 —— 后者要等到第一次聊天
    // 才暴露，而且暴露成「你没导入账号」这个**指向用户**的错误。
    //
    // ## 为什么是「退出」而不是「只警告」
    //
    // 网关存在的意义就是用它保险库里的账号去调上游。**没有账号的网关是纯废物**：
    // 它只会对所有请求回 401。带着这种状态继续跑，用户看到的是
    // 「网关起来了、但什么都干不了」—— 比启动失败更难查。
    //
    // 启动失败至少能立刻告诉用户「你给的路径是 X，那儿没有文件」。
    if (!File.Exists(vaultPath))
    {
        Log.Fatal(
            "保险库文件不存在，拒绝启动：{VaultPath}\n" +
            "  · 如果这是相对路径，它是相对**启动时所在目录**解析的\n" +
            "  · 当前解析结果：{Full}\n" +
            "  · 修法：给绝对路径，或先确认那个文件真的在",
            options.VaultPath.Length > 0 ? options.VaultPath : "(未指定，按变体推导)",
            vaultPath);

        await Log.CloseAndFlushAsync();
        return 2;
    }

    // 源码目录：用来算 buildId 和 stale。
    //
    // 为什么从程序集位置往上找而不是写死：运行时（bin/Debug/net8.0/）
    // 和源码（src/）的相对位置在开发/发布两种布局下不一样。
    // 所以往上爬到第一个同时含 src/ 和 .slnx 的目录。
    var sourceDirectory = FindSourceRoot(AppContext.BaseDirectory);

    // ── 读落盘设置并应用（t88：保存后重启不丢 + 保存后立即生效的前提）──
    //
    // ## 为什么必须在这里做（这是「保存没反馈、不会刷新」的一部分根因）
    //
    // `/admin/settings` 的 POST 会把 apiKey 写进 settings.json，
    // 但**启动时从不读它** —— 于是用户改了 key、重启网关后又回到旧 key
    // （或空 key），客户端再访问又是 401。
    //
    // 优先级：命令行 > 环境变量 > 配置文件（与 OptionSources 的语义一致）。
    // 所以这里只在**命令行/环境变量都没给**某个字段时，才用文件里的值补上。
    //
    // 为什么直接改 `options` 而不是建一套 Sourced 合并：
    // 这里已经能拿到「命令行/环境变量到底给没给」的事实（见 ApplyEnvironment
    // 与 Parse 里的 null 检查），再套一层合并只是重复同一件事。
    // 保持最小改动：文件层只做「填空白」。
    ApplyPersistedSettings(options, vaultPath);

    var builder = WebApplication.CreateSlimBuilder(args);

    // 宿主自己那套日志让位给 Serilog
    builder.Logging.ClearProviders();
    builder.Host.UseSerilog(Log.Logger);

    // 只绑回环。这是本机信任边界的核心 —— 绑 0.0.0.0 等于把账号池
    // 暴露给整个局域网。
    builder.WebHost.UseUrls($"http://{options.Host}:{options.Port}");

    var services = builder.Services;

    services.AddSingleton(options);
    services.AddSingleton(variant);
    services.AddSingleton(new ModelCatalog(variant.Id, options.DefaultModel));
    services.AddSingleton(new BuildInfo(sourceDirectory));
    services.AddSingleton(TimeProvider.System);

    // 目录刷新器（t53）—— 带**正确的 UA 形态**去拉 `/v3/config`。
    //
    // ⚠ 在 t53 之前，刷新是 GatewayApp 自己内联做的、**且传 null UA**：
    //   而不带 UA 访问 `/v3/config` 实测返回 400 / code 12403 ⇒ **刷新从来没成功过**，
    //   失败还压在 Debug 级 ⇒ 永久回落到 16 个内置模型、界面毫无异常。
    //
    // 现在抽成独立类型：UA 形态写在 CatalogRefresh 里（含来源注释），
    // 失败以 **Warning** 记（含端点 / UA / 状态码 / 原始正文）。
    services.AddSingleton(sp => new CatalogRefresh(
        sp.GetRequiredService<UpstreamClient>(),
        sp.GetRequiredService<ModelCatalog>(),
        sp.GetRequiredService<UpstreamVariant>(),
        sp.GetRequiredService<ILogger<CatalogRefresh>>()));

    // ── 限流历史落盘（t86 接线）──────────────────────────────
    //
    // 用户原话：「20个号的 deepseek 基本都限流了但显示全绿，应该告诉用户
    // 哪些模型限流了、啥时候解除限流」。内存里的 `RateLimits` 重启即丢 ——
    // 所以把它落到保险库同目录（和凭据放一起，备份时不会漏）。
    //
    // 文件位置：与保险库同目录的 rate-limits.json（和 settings.json / api-keys.json 并列）。
    // 惰性路径（Func<string>）：保险库目录在启动时已确定。
    var rateLimitStore = new RateLimitStore(
        () => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(vaultPath) ?? ".",
            "rate-limits.json"),
        null); // 日志用 NullLogger 兜底，由 AccountPool 记落盘失败

    services.AddSingleton(rateLimitStore);

    // ⚠ AccountPool 必须用工厂注册：它现在带一个可选的 RateLimitStore，
    //   而 DI 不会为可选参数注入服务。不显式传的话 pool 退化成纯内存，
    //   落盘形同虚设（t86 的核心诉求）。
    services.AddSingleton<AccountPool>(sp => new AccountPool(
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<AccountPool>>(),
        rateLimitStore));
    services.AddSingleton<ICredentialSource>(sp => new VaultCredentialSource(
        () => vaultPath,
        sp.GetRequiredService<ILogger<VaultCredentialSource>>()));

    // ★ 多池路由（new_api 渠道模型）：扫描 pools/ 目录下的子池，
    //   为每个池建独立的 AccountPool + CredentialSource，按 API key 路由。
    //   单池用户（pools 目录为空）只有一个默认池，行为与旧版完全一致。
    services.AddSingleton<PoolRouter>(sp =>
    {
        var instances = new List<PoolInstance>();
        var logger = sp.GetRequiredService<ILogger<AccountPool>>();
        var timeProvider = sp.GetRequiredService<TimeProvider>();

        // ① 默认池
        instances.Add(new PoolInstance(
            "default", "默认池", options.ApiKey, vaultPath,
            new AccountPool(timeProvider, logger, rateLimitStore),
            new VaultCredentialSource(() => vaultPath, sp.GetRequiredService<ILogger<VaultCredentialSource>>())));

        // ② 子池目录（pools/<目录名>/accounts.cn.json）
        var poolsDir = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(vaultPath) ?? ".", "pools");
        if (Directory.Exists(poolsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(poolsDir))
            {
                var poolVault = System.IO.Path.Combine(dir, variant.VaultFilename);
                if (!File.Exists(poolVault))
                {
                    // 兼容旧版命名：accounts.cn.json
                    poolVault = System.IO.Path.Combine(dir, "accounts.cn.json");
                }
                if (!File.Exists(poolVault)) continue;

                var dirName = System.IO.Path.GetFileName(dir);
                instances.Add(new PoolInstance(
                    dirName,
                    dirName,
                    "",   // 子池默认不设独立 key —— 面板建池时再配
                    poolVault,
                    new AccountPool(timeProvider, logger, null),
                    new VaultCredentialSource(() => poolVault, sp.GetRequiredService<ILogger<VaultCredentialSource>>())));
            }
        }

        return new PoolRouter(instances, sp.GetRequiredService<ILogger<PoolRouter>>());
    });

    // ── 客户端身份（UA 里的版本号）────────────────────────────
    //
    // ⚠ 这块是「网关为什么突然不能用了」的头号嫌疑。
    //
    // 上游会校验 UA 里的版本号：真实的 5.8.3 通过，旧版本 5.5.6 被拦。
    // 所以必须从本机装的 WorkBuddy App 里读真实版本 —— 不能写死。
    //
    // Node 版在这件事上有个 bug：CLI 版本的路径写成了 macOS 的
    // `Contents/Resources/...`，Windows 上永远读不到，UA 一直缺第三段。
    // Core 的 ClientIdentity 两个平台的路径都试。
    services.AddSingleton<IProcessRunner, SystemProcessRunner>();
    services.AddSingleton<IFileProbe, SystemFileProbe>();
    services.AddSingleton(sp => new ClientIdentity(
        sp.GetRequiredService<IProcessRunner>(),
        sp.GetRequiredService<IFileProbe>()));

    // 解析出的身份也单例缓存 —— 解析要起子进程查注册表，
    // 每条消息都跑一次不可接受。
    services.AddSingleton(sp =>
    {
        var identity = sp.GetRequiredService<ClientIdentity>();
        var variant = sp.GetRequiredService<UpstreamVariant>();
        // 同步等一次：这一步在启动时做，几十毫秒，不阻塞请求路径
        return identity.ResolveAsync(variant, CancellationToken.None).GetAwaiter().GetResult();
    });

    // HttpClient 走 Core 的 UpstreamClient。超时留长 ——
    // 一个长回答生成几分钟是正常的，短超时会把正常请求砍掉。
    services
        .AddHttpClient<UpstreamClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        })
        // ⚠ 必须显式允许系统代理 —— 这是实测踩出来的坑，见 UseSystemProxy 的注释。
        .ConfigurePrimaryHttpMessageHandler(CreateUpstreamHandler)
        // 标准弹性策略：重试 3 次（指数退避 + 抖动）、熔断、超时。
        // 注意 UpstreamClient 自己已经把连接层失败分类了，
        // 这里只是在更外层加一层网络抖动容忍。
        .AddStandardResilienceHandler();

        // ── 媒体与联网客户端 ─────────────────────────────────────
    //
    // 两者都用**惰性凭证**：每次要用时现取 `ICredentialSource.Load()` 的第一条 ——
    // 不在构造时把 token 抓下来存着。这样切号/续期能自动跟上，
    // 而且**不另起一套凭据机制**（和 UpstreamClient 用的是同一个来源）。
    //
    // ⚠ 媒体下载是「无账号概念」的操作（下载一个签名 URL 不需要凭证），
    //   但**换 tempKey 需要**。所以它仍然走当前账号的 JWT ——
    //   换句话说：**额度归属当前选中的第一个账号**。
    //   这里刻意不做账号池轮询：生图是重操作，多账号并发提交只会更快撞限流，
    //   而「用哪个号生图」对用户没有可见差异。
    services.AddSingleton(sp => new MioraClient(
        // ⚠ 不能复用 AddHttpClient<UpstreamClient> 那个 HttpClient：
        //   那条管道挂了 ResilienceHandler（重试 3 次 + 熔断），
        //   而生图**绝不能自动重试** —— 重试会导致重复扣费、生成两张图。
        new HttpClient(CreateUpstreamHandler()) { Timeout = Timeout.InfiniteTimeSpan },
        () => FirstCredential(sp),
        sp.GetRequiredService<UpstreamVariant>(),
        Path.Combine(options.ResolveLogDirectory(), "media"),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<MioraClient>>()));

    services.AddSingleton(sp => new SearchClient(
        new HttpClient(CreateUpstreamHandler()) { Timeout = Timeout.InfiniteTimeSpan },
        () => FirstCredential(sp),
        sp.GetRequiredService<UpstreamVariant>(),
        sp.GetRequiredService<ILogger<SearchClient>>()));

    // 媒体工具的注入器（往聊天请求体里塞四个工具定义）与执行器
    // （模型调它们时由网关自己执行）。
    services.AddSingleton(sp => new MediaToolInjector(
        sp.GetRequiredService<ILogger<MediaToolInjector>>(),
        enabled: !string.Equals(
            Environment.GetEnvironmentVariable("BUDDYDECK_MEDIA_TOOLS"), "0", StringComparison.Ordinal)));

    services.AddSingleton(sp => new MediaToolExecutor(
        sp.GetRequiredService<MioraClient>(),
        sp.GetRequiredService<ILogger<MediaToolExecutor>>(),
        // ⚠ **必须传 SearchClient** —— 否则 `web_search` / `fetch_page`
        //   两个工具会回"没配置"。
        //   用户实测反馈「搜索拓展没有」：那对工具原来**根本没注册进工具清单**，
        //   所以模型不知道自己能联网，只会回答"我无法联网"。
        sp.GetRequiredService<SearchClient>()));

    // ── observability 的挂载点 ──────────────────────────────
    //
    // t1 留的挂载点：谁要接就在注册一个 IChatObserver 实现。
    // t2 已经把 P1/P2 三个模块接上（会话粘性 / 请求指标 / 脱敏日志），
    // 但**它们不走观察者那条路** —— 原因见下。
    //
    // 为什么不把这三个做成 IChatObserver：
    //   ① 会话粘性要在**选号之前**生效（查表命中就不用走池），
    //      而 OnChatCompleted 是终局事件，那时候号已经选完了
    //   ② 指标要覆盖「没选到号」「没账号」这些**还没进入聊天流程**的失败，
    //      观察者钩子挂在聊天流程的终局点上，够不到它们
    // 所以它们由 GatewayApp 直接持有（见它的构造函数末尾三个可选参数），
    // ChatObservers 这条通用扩展点保持原样，留给以后别的需求。
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatObserver, NullChatObserver>());
    services.AddSingleton<ChatObservers>();

    // ── P1 会话粘性 ──────────────────────────────────────────
    //
    // 有界亲和表（键 → 账号 id，带 TTL）。表的大小和存活时间见
    // AffinityOptions —— 不设界的话，长跑起来就是一条稳定的内存泄漏。
    services.AddSingleton(sp => new ConversationAffinity(
        sp.GetRequiredService<AccountPool>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<ConversationAffinity>>()));

    // ── P2 请求指标 + 脱敏日志 ───────────────────────────────
    //
    // 指标注册成「同一个实例同时满足两个类型」——
    // IMetricsSource 给界面/别的组件读，RequestMetrics 给内部按容量配置。
    // 分成两个实例的话，读到的会是空的。
    services.AddSingleton<RequestMetrics>(sp => new RequestMetrics(
        sp.GetRequiredService<TimeProvider>(),
        new MetricsOptions { RecentCapacity = options.MetricsRecentCapacity }));

    services.AddSingleton<IMetricsSource>(sp => sp.GetRequiredService<RequestMetrics>());

    services.AddSingleton(sp => new RequestLog(sp.GetRequiredService<TimeProvider>()));

    // ── M10 用量账本（t29 接线）───────────────────────────────
    //
    // ⚠ 这一段是 t29 补的 —— 在此之前 `UsageLedger` / `UsageEndpoints` 是**死代码**：
    //   类型写好了、单测能过，但**没注册、没挂载、请求路径上没有记账钩子**。
    //   实测后果：跑了几十次 chat 请求，`usage.jsonl` 一个字节都没落盘，
    //   `/usage/*` 四个端点全部 404。
    //
    //   这正是「写完了」的第 ② 层（被装配）断了 —— 而**单测永远验不出第 ② 层**
    //   （它测的是第 ① 层「类型存在」）。t17 之前 `/admin/*` 卡的是同一层。
    //
    // 账本文件放日志目录下：和日志一样是「长跑累积的运维数据」，
    // 放一起便于用户一次性找到/清理/备份。文件名固定，便于文档与排障引用。
    var usageLedgerPath = Path.Combine(options.ResolveLogDirectory(), "usage.jsonl");

    services.AddSingleton(sp => new UsageLedger(
        usageLedgerPath,
        sp.GetRequiredService<ILogger<UsageLedger>>()));

    // ── SQLite 数据库（数据中枢，用户需求的架构升级）────────────
    //
    // 数据库放**保险库同目录**（与 settings.json / rate-limits.json 并列）——
    // 它是账号数据、用量、积分、限流历史的统一存储，和凭据一起备份。
    //
    // 为什么不用 UsageLedger 那个日志目录：账本 jsonl 是可移植的文本，
    // 数据库是查询加速层，两者职责不同但都要能被用户找到。
    // 与保险库同目录 = 用户备份保险库时自动带上数据库。
    var dbPath = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(vaultPath) ?? ".",
        "buddydeck.db");

    var database = new Database(dbPath, null);
    database.Initialize();
    services.AddSingleton(database);

    // ★ 启动时把 JSONL 历史账本导入数据库（幂等，按 ts 去重）。
    //   否则「升级之前的积分消耗明细」在数据库里是空的，界面读不到
    //   （用户反馈「积分消耗详情搞没了」的根因）。
    try
    {
        var ledgerPath = usageLedgerPath;
        if (File.Exists(ledgerPath))
        {
            var ledger = new UsageLedger(ledgerPath, null);
            var historical = ledger.Read();
            if (historical.Entries.Count > 0)
            {
                database.ImportUsage(historical.Entries);
                Log.Information("已从账本导入 {Count} 条历史用量到数据库", historical.Entries.Count);
            }
        }
    }
    catch (Exception ex)
    {
        Log.Warning("账本历史导入数据库失败（不影响运行）：{Message}", ex.Message);
    }

    // 后台任务执行器（费时操作：批量拉积分/签到/续期，不阻塞 UI）。
    services.AddSingleton(sp => new TaskHost(
        sp.GetRequiredService<Database>(),
        sp.GetRequiredService<ILogger<TaskHost>>()));

    // 记账钩子挂在 IChatObserver 上 —— 那是**唯一一个「终局点」统一出口**：
    // GatewayApp 在成功/失败/取消三条终局路径各调一次 Notify，参数里已经带齐
    // 记账需要的全部字段（模型、账号、usage、outcome、尝试次数、耗时）。
    //
    // 挂在别处都有坑：挂 StreamAsync 只能覆盖流式成功路径；自己 try/catch
    // 要和主流程那几处 return 对齐，漏一个分支就漏记。
    //
    // ★ 双写：UsageLedger（可移植 JSONL）+ Database（查询加速）。
    services.AddSingleton<IChatObserver>(sp => new UsageLedgerObserver(
        sp.GetRequiredService<UsageLedger>(),
        sp.GetRequiredService<ILogger<UsageLedgerObserver>>(),
        sp.GetRequiredService<Database>()));

    // 读端点（`/usage/summary` 等四个）。它**只读**账本，不写。
    // ★ recent 优先从数据库读（秒级），聚合视图仍走 JSONL（保证全量）。
    services.AddSingleton(sp => new UsageEndpoints(
        sp.GetRequiredService<UsageLedger>(),
        sp.GetRequiredService<ILogger<UsageEndpoints>>(),
        sp.GetRequiredService<Database>()));

    // ── 管理面（/admin/*）：界面五个功能面的后端 ─────────────
    //
    // 单独一个类型而不是塞进 GatewayApp：两者的**信任模型不同** ——
    // /v1/* 是给第三方模型客户端用的，/admin/* 会暴露账号标签与配置，
    // 所以全部要求鉴权。分开放边界清楚（详见 AdminEndpoints 的类注释）。
    //
    // ⚠⚠ **只注册具体类型，不在这里注册成 IEndpointFamily** ——
    //   否则会产出**两个实例**（一个给这个 AddSingleton，一个给 AddAll 里的
    //   `AddSingleton<IEndpointFamily, AdminEndpoints>`），
    //   而 `AdminEndpoints` 里持有**可变的设置状态**（`_settings`）——
    //   两个实例意味着「POST /admin/settings 改了一个、GET 读到另一个」。
    //
    //   正确做法：这里注册具体类型一次，`AddAll` 里的
    //   `Register<AdminEndpoints>` 用 DI 的**转发**写法复用同一个实例。
    //
    // ── API Key 存储（/admin/keys）────────────────────────────
    //
    // 与保险库**同目录**：两者都是"这台机器上的凭据"，
    // 放在一起用户备份/迁移时不会漏掉一个。
    // （文件权限由 KeyStore 自己按 Vault 同款处理。）
    services.AddSingleton(sp => new KeyStore(
        System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(vaultPath) ?? ".",
            "api-keys.json"),
        sp.GetService<TimeProvider>(),
        sp.GetService<ILogger<KeyStore>>()));

    // ── 积分端点（/admin/credits）────────────────────────────
    //
    // ⚠ **必须显式注册**，理由与 `GatewayApp` 那条一样：
    //   `CreditsEndpoints` 的构造函数尾部有可选参数（`TimeProvider? clock = null`），
    //   而 DI 的自动解析**不会为可选参数注入服务**。
    //
    // 实测踩过（用户反馈「余量那一块你再研究一下」）：
    //   `CreditsEndpoints.cs` 存在、单测 23/23 全绿，
    //   但**既没进 DI、也没进 `EndpointFamilies.AddAll`** ——
    //   于是真网关上 `GET /admin/credits` 是 404，界面静默显示空。
    services.AddSingleton(sp => new CreditsEndpoints(
        sp.GetRequiredService<UpstreamClient>(),
        sp.GetRequiredService<ICredentialSource>(),
        sp.GetRequiredService<UpstreamVariant>(),
        sp.GetRequiredService<ILogger<CreditsEndpoints>>(),
        sp.GetService<TimeProvider>(),
        sp.GetService<Database>()));

    // 任务库端点（后台任务：拉积分/签到/续期）。
    services.AddSingleton(sp => new TaskEndpoints(
        sp.GetRequiredService<Database>(),
        sp.GetRequiredService<TaskHost>(),
        sp.GetRequiredService<ICredentialSource>(),
        sp.GetRequiredService<UpstreamClient>(),
        sp.GetRequiredService<ILogger<TaskEndpoints>>(),
        sp.GetService<Checkin>()));

    // ── 设置落盘（M14 收口）──────────────────────────────────
    // ⚠ 没有它的话 `/admin/settings` 的改动**只存在内存里，重启即丢** ——
    //   而用户改的时候是成功的、界面上看不出异常。
    //   这是"看起来成功了、结果没保存"的典型静默失败。
    services.AddSingleton(sp => new SettingsStore(
        // ⚠ 第一个参数是 `Func<string>`（**惰性**路径），不是 string ——
        //   捕获 `vaultPath` 这个局部变量即可。
        () => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(vaultPath) ?? ".",
            SettingsStore.FileName),
        sp.GetService<ILogger<SettingsStore>>()));

    // ── 签到客户端（一键签到）────────────────────────────
    //
    // 与 UpstreamClient 一样用惰性凭证：签到也要用当前账号的 JWT。
    // 单独一个 HttpClient（不带 ResilienceHandler）—— 签到不该自动重试
    // （重试可能重复签到，虽然上游幂等，但没必要多打）。
    services.AddSingleton(sp => new Checkin(
        new HttpClient(CreateUpstreamHandler()) { Timeout = Timeout.InfiniteTimeSpan },
        sp.GetRequiredService<UpstreamVariant>(),
        sp.GetRequiredService<ILogger<Checkin>>()));

    services.AddSingleton(sp => new AdminEndpoints(
        options,
        variant,
        sp.GetRequiredService<ICredentialSource>(),
        sp.GetRequiredService<RequestLog>(),
        sp.GetRequiredService<ILogger<AdminEndpoints>>(),
        // 账号池 + 时钟：只为「每个号的模型冷却情况」这一列可观测性 ——
        // 用户实测反馈「每一个账号的模型冷却情况也没有」。
        // 限流是按「号+模型」记的，界面看不到这层时只会觉得"这个号时好时坏"。
        sp.GetRequiredService<AccountPool>(),
        sp.GetService<TimeProvider>(),
        // API Key 存储：/admin/keys 三个动作要用。
        // ⚠ 不注册它的话那条路由回 503（可见的错误），而不是崩。
        sp.GetService<KeyStore>(),
        // 设置落盘：让 /admin/settings 的改动活过重启。
        sp.GetService<SettingsStore>(),
        // 模型目录：/admin/pool-health 要枚举模型。
        sp.GetService<ModelCatalog>(),
        // 请求指标：/admin/pool-health 用它算「降级次数」——
        // 那是唯一能看出"平时老换模型"的数据源（冷却表看不到它）。
        sp.GetService<IMetricsSource>(),
        // 上游客户端：真续期。
        sp.GetService<UpstreamClient>(),
        // 签到客户端：一键签到。
        sp.GetService<Checkin>(),
        // 数据库：池快照 / 池↔账号关联 / 跨池积分汇总 / 冷却入库。
        sp.GetService<Database>(),
        // 多池路由：/admin/pools 返回扫描到的全部池。
        sp.GetService<PoolRouter>()));

    // ── 端点族：**注册顺序 = 询问顺序** ──────────────────────
    //
    // ⚠⚠ 顺序的**唯一真源**在 `EndpointFamilies.AddAll()` ——
    //   这里**刻意不再逐个 Register**，理由是一个真踩过的坑（e2e 报的）：
    //
    //   生产装配（这里）和测试装配（`tests/BuddyDeck.TestSupport/GatewayHost.cs`）
    //   都要挂端点族。**两边各写一份清单必然漂移** —— 加一族时改了一边
    //   忘另一边，症状是「真进程一切正常、测试里全是 404」。
    //
    //   这个坑真的发生了：`GatewayHost` 起初没注册任何族，
    //   于是 `GatewayApp._families` 是空的 —— 端点族在**所有测试宿主里
    //   完全不可见**，而 `dotnet run` 起的真进程完全正常。
    //   那种「手工验是真的 + 测试全红」的组合最容易让人误判成
    //   「测试环境问题」然后把测试跳过 —— 而那正是要防的。
    //
    //   所以两处**调同一个方法**，顺序只有一个真源。
    //   加新族请改 `EndpointFamilies.AddAll()`，并在那里的注释里写明顺序理由。
    EndpointFamilies.AddAll(services);

    // ⚠ **显式构造**，不能用 `services.AddSingleton<GatewayApp>()`。
    //
    // 原因：`GatewayApp` 的构造函数尾部有一串**可选参数**（`= null`），
    // 而 DI 的自动解析**不会为可选参数注入服务** —— 它会用默认值 null。
    // 于是 `_mediaToolExecutor` 永远是 null，**模型请求调生图工具时网关不执行**。
    //
    // 这正是用户实测反馈「聊天测试生成图片和视频没法在回复框看见」的根因：
    // 收集器把 tool_calls 合出来了、注入器也把工具定义发出去了，
    // 但**没有人执行它们** —— 而客户端（我们的界面）执行不了。
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
        sp.GetService<ConversationAffinity>(),
        sp.GetService<IMetricsSource>(),
        sp.GetService<RequestLog>(),
        sp.GetService<AdminEndpoints>(),
        sp.GetServices<IEndpointFamily>(),
        sp.GetService<MediaToolInjector>(),
        sp.GetService<MediaToolExecutor>(),   // ← 执行器：必须显式传，否则默认 null
        sp.GetService<CatalogRefresh>(),
        sp.GetService<PoolRouter>()));

    var app = builder.Build();

    app.Services.GetRequiredService<GatewayApp>().Map(app);

    if (Log.IsEnabled(LogEventLevel.Information))
    {
        var buildInfo = app.Services.GetRequiredService<BuildInfo>();
        Log.Information(
            "BuddyDeck.Gateway 启动：{Host}:{Port}  变体={Variant}  模型={Models}  buildId={BuildId}  鉴权={Auth}  保险库={Vault}",
            options.Host, options.Port, variant.Id,
            app.Services.GetRequiredService<ModelCatalog>().Count,
            buildInfo.Loaded,
            string.IsNullOrEmpty(options.ApiKey) ? "关闭" : "开启",
            vaultPath);
    }

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "网关启动失败");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>
/// 找到源码根目录（用来算 buildId / stale）。
///
/// 从程序集所在目录往上爬，找第一个同时含 `src/` 和 `BuddyDeck.slnx`
/// 的目录。找不到就返回程序集目录 —— 那种情况下指纹会是 "unknown"，
/// `/healthz` 照常回，不会因此启动失败。
/// </summary>
static string FindSourceRoot(string startDirectory)
{
    var directory = new DirectoryInfo(startDirectory);

    while (directory is not null)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, "src")) &&
            File.Exists(Path.Combine(directory.FullName, "BuddyDeck.slnx")))
        {
            return Path.Combine(directory.FullName, "src");
        }

        directory = directory.Parent;
    }

    return startDirectory;
}

/// <summary>
/// 把 settings.json（与保险库同目录）里的持久设置补进启动参数。
///
/// ## 为什么是「填空白」而不是「覆盖」
///
/// 优先级：命令行 > 环境变量 > 配置文件 > 默认值（OptionSources 语义）。
/// 命令行/环境变量已经给过的字段**不动**，只有它们没给时，
/// 才用文件里用户上次保存的值。这样：
///   · `--port 8791` 起测试实例 → 不会被文件里的 8787 盖掉
///   · 面板保存过 key 后重启 → key 从文件读回，不再变回空
///
/// ## 为什么读不到就静默跳过
///
/// settings.json 不存在是**第一次运行**的常态；它坏了也不该让网关起不来
/// （设置是锦上添花，不是性命攸关 —— 见 SettingsStore.Load 的注释）。
/// </summary>
static void ApplyPersistedSettings(GatewayOptions options, string vaultPath)
{
    // 判断「命令行/环境变量到底给没给」的唯一依据是 ApiKeySpecified ——
    // 而不是「当前值是否为空」。显式传了空串（App 拉网关时的常态）也是「给过」。
    var commandLineOrEnvApiKey = options.ApiKeySpecified;

    SettingsDocument? doc;
    try
    {
        var path = SettingsStore.PathBesideVault(vaultPath);
        var store = new SettingsStore(() => path);
        doc = store.Load();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // 读设置失败不致命 —— 当没有持久设置继续启动。
        Log.Warning("读持久设置失败（用默认值启动）：{Message}", ex.Message);
        return;
    }

    // ★ 只有命令行/环境变量没给 apiKey 时，才用文件里的值。
    if (!commandLineOrEnvApiKey && !string.IsNullOrEmpty(doc.ApiKey))
    {
        options.ApiKey = doc.ApiKey;
        Log.Information("已从设置文件恢复 API key（长度 {Length}）", doc.ApiKey.Length);
    }
}

/// <summary>
/// 取「当前账号」的凭证 —— 媒体与联网客户端用。
///
/// ## 为什么是「第一条」而不是走账号池
///
/// 生图/搜索是**重操作**：一次生图可能占用上游几分钟。多账号并发提交
/// 只会更快撞限流，而「用哪个号生图」对用户没有可见差异。
///
/// ## 为什么要惰性取
///
/// 每次要用时现取，不在构造时存 token —— 用户在面板里换号、token 续期
/// 之后立刻跟上。**不另起一套凭据机制**：和 UpstreamClient 共用
/// <c>ICredentialSource</c>。
///
/// 保险库为空时返回一个空 token 的凭证，让下游报明确的
/// 「没有可用凭据」而不是 NullReferenceException。
/// </summary>
static BuddyDeck.Core.Credential FirstCredential(IServiceProvider sp)
{
    var entries = sp.GetRequiredService<BuddyDeck.Gateway.ICredentialSource>().Load();

    return entries.Count > 0
        ? entries[0].ToCredential()
        : new BuddyDeck.Core.Credential { AccessToken = "", Label = "（保险库为空）" };
}

/// <summary>
/// 建上游 HttpClient 的 handler —— **关键点是「显式尊重系统代理」**。
///
/// ## ⚠ 这是实测踩出来的坑（2026-10-07，真机全 502）
///
/// 现象：旧面板（node）一切正常，新网关所有请求 `上游 http 0`（连接层失败）
///       → 连续失败把 8 个号全推进冷冻区 → 全部请求 429「没有可用账号」。
///
/// 定位过程：
/// <code>
/// 同一时刻、同一个域名 copilot.tencent.com：
///   node 直连   → HTTP 200  ✅
///   curl 直连   → HTTP 000  ❌   ← 只有 curl/.NET 不通
/// 本机系统代理：ProxyEnable=1  ProxyServer=127.0.0.1:7897
/// </code>
///
/// **⇒ `AddHttpClient` 用的 `SocketsHttpHandler` 默认 <c>UseProxy = true</c>，
///   但它只认「进程环境变量」里的 HTTP(S)_PROXY，不读 Windows 的
///   「Internet 选项」注册表设置。** 而这台机器的代理恰好只配在注册表里。
///
/// 所以这里显式把系统代理交给 handler —— 有代理就用，没有就直连，
/// 两种情况都不会因为"该走代理没走"而静默连接失败。
///
/// ⚠ 注意与「不许重试」的区分：这里是**传输层配置**，与重试策略无关。
///   生图那条管道仍然挂的是无重试的裸 HttpClient（只是 handler 换成本方法）。
/// </summary>
static SocketsHttpHandler CreateUpstreamHandler()
{
    var handler = new SocketsHttpHandler
    {
        // 走系统代理（含 Windows「Internet 选项」里的设置，不只是环境变量）
        UseProxy = true,
        Proxy = ResolveProxy(),
        // 连接池：上游偶发长尾，多留一点并发连接
        MaxConnectionsPerServer = 32,
        // 自动解压：上游有 gzip 响应，不解压会拿到二进制
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    };

    return handler;
}

/// <summary>
/// 解析出该用的代理。
///
/// ## 为什么要自己解析，不直接 <c>HttpClient.DefaultProxy</c>
///
/// 实测：`DefaultProxy` 在这台机器上解析出的代理**连不上**
/// （报「目标计算机积极拒绝 (127.0.0.1:7897)」），
/// 而**同一个代理地址用 curl 走却完全正常**（两个上游域名都 200）。
///
/// ⇒ 不是代理本身的问题，是 `DefaultProxy` 拿到的**格式/协议**不对。
///
/// 所以按优先级自己取，并在每一步都显式补上 `http://` scheme：
/// <list type="number">
///   <item>环境变量 <c>BUDDYDECK_PROXY</c>（用户显式指定，最高优先）</item>
///   <item>环境变量 <c>HTTPS_PROXY</c> / <c>HTTP_PROXY</c></item>
///   <item>Windows「Internet 选项」里的 ProxyServer（这台机器就配在这里）</item>
///   <item>都没有 ⇒ 直连（返回 null）</item>
/// </list>
/// </summary>
static IWebProxy? ResolveProxy()
{
    static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        // 注册表里的值可能是 "127.0.0.1:7897"（无 scheme）或
        // "http=127.0.0.1:7897;https=127.0.0.1:7897"（分协议）。两种都要能处理。
        if (v.Contains('='))
        {
            var part = v.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Split('=', 2))
                .FirstOrDefault(p => p.Length == 2 &&
                    (p[0].Trim().Equals("https", StringComparison.OrdinalIgnoreCase) ||
                     p[0].Trim().Equals("http", StringComparison.OrdinalIgnoreCase)));
            if (part is null) return null;
            v = part[1].Trim();
        }
        if (v.Length == 0) return null;
        if (!v.Contains("://", StringComparison.Ordinal)) v = "http://" + v;
        return v;
    }

    var explicitProxy =
        Clean(Environment.GetEnvironmentVariable("BUDDYDECK_PROXY"))
        ?? Clean(Environment.GetEnvironmentVariable("HTTPS_PROXY"))
        ?? Clean(Environment.GetEnvironmentVariable("HTTP_PROXY"))
        ?? Clean(Environment.GetEnvironmentVariable("https_proxy"))
        ?? Clean(Environment.GetEnvironmentVariable("http_proxy"));

    // ⚠ OperatingSystem.IsWindows() 守卫是必需的：Microsoft.Win32.Registry 只在
    //   Windows 上可用，而本项目开了「警告即错误」，CA1416 会因此拦下编译。
    if (explicitProxy is null && OperatingSystem.IsWindows())
    {
        // 退到 Windows「Internet 选项」
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key?.GetValue("ProxyEnable") is int enabled && enabled == 1)
            {
                explicitProxy = Clean(key.GetValue("ProxyServer") as string);
            }
        }
        catch
        {
            // 读注册表失败就当没配代理 —— 不因为"读不到代理设置"而让整个网关起不来
        }
    }

    return explicitProxy is null ? null : new WebProxy(explicitProxy);
}
