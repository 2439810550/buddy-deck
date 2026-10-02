using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace WorkBuddyPanel.Services;

/// <summary>
/// 一个正在运行的池实例。
///
/// 每个池一个 <see cref="GatewayRunner"/>，各自占一个端口、读各自那份账号。
/// 界面上的「运行中 / 已停止」就是看它。
/// </summary>
public sealed class PoolRuntime : IDisposable
{
    public required PoolConfig Config { get; init; }

    public GatewayRunner? Runner { get; private set; }

    /// <summary>
    /// 这个实例由主界面管（默认池），还是由分池页管。
    ///
    /// 默认池复用主界面那个 <see cref="GatewayRunner"/> ——
    /// 因为聊天页、状态、模型列表全都挂在它上面，
    /// 让分池页另起一个会多出一套账号、多占一个端口，而且那个
    /// 类里的 BaseUrl 会指向一个没人用的实例。
    /// </summary>
    public bool IsAdopted { get; init; }

    public bool IsRunning => Runner is { IsRunning: true } || _externalRunning;

    /// <summary>
    /// 网关在跑，但不是这个进程起的。
    ///
    /// 真实场景：用户的正式版（D:\BuddyDeck）已经拉起网关占着 8787，
    /// 然后他又开了这个新版想试试。新版检测到端口上是我们自己的网关就
    /// 「直接采用」—— 但它手里并没有那个进程，Runner 是空的。
    /// 这时如果只看 Runner，分池页会显示「已停止」并给一个「启动」按钮，
    /// 点下去只会因为端口被占而报错。
    /// </summary>
    private bool _externalRunning;

    /// <summary>外部实例的地址（采用时填上）。</summary>
    private string _externalUrl = "";

    /// <summary>在跑，但不是本进程起的。</summary>
    public bool IsRunningExternally => _externalRunning;

    public void MarkExternalRunning(string baseUrl)
    {
        _externalRunning = true;
        _externalUrl = baseUrl;
        LastError = null;
    }

    public void ClearExternalRunning()
    {
        _externalRunning = false;
        _externalUrl = "";
    }

    /// <summary>最近一次启动失败的原因（成功时 null）。</summary>
    public string? LastError { get; private set; }

    /// <summary>从 node 输出里解析出来的真实地址（或外部实例的地址）。</summary>
    public string BaseUrl => Runner?.BaseUrl is { Length: > 0 } u ? u : _externalUrl;

    public event Action<string>? Log;

    /// <summary>接管一个已经在跑的实例（默认池用）。</summary>
    public void Adopt(GatewayRunner runner)
    {
        Runner = runner;
        LastError = null;
    }

    /// <summary>
    /// 实例退出了（意外退出也算）。
    ///
    /// 为什么需要它：不通知界面的话，池已经退了，但分池页上那个卡片
    /// 还显示「运行中」—— 「启动」按钮于是点不动，用户看到的就是
    /// 「点了没反应 / 启动不了」。实测踩到。
    ///
    /// PoolRuntime 抛，PoolManager 转发给界面。
    /// </summary>
    public event Action<PoolConfig>? Exited;

    public async Task StartAsync(string nodePath, string apiDir, string provider, string? authFile)
    {
        if (IsRunning) return;

        Runner?.Dispose();
        LastError = null;

        var runner = new GatewayRunner { PoolName = Config.DisplayName };
        runner.LogLine += line => Log?.Invoke($"[{Config.DisplayName}] {line}");
        runner.Exited += _ =>
        {
            Log?.Invoke($"[{Config.DisplayName}] 实例已退出");
            Exited?.Invoke(Config);
        };

        try
        {
            await runner.StartAsync(new GatewayOptions(
                nodePath,
                apiDir,
                Config.Port,
                Config.ApiKey,
                provider,
                string.IsNullOrWhiteSpace(authFile) ? null : authFile)
            {
                VaultHome = Config.IsDefault ? null : Config.VaultHome(),
            });

            Runner = runner;
            Log?.Invoke($"[{Config.DisplayName}] 就绪 → {runner.BaseUrl}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            runner.Dispose();
            Log?.Invoke($"[{Config.DisplayName}] 启动失败：{ex.Message}");
            throw;
        }
    }

    public void Stop()
    {
        var runner = Runner;
        if (runner is null) return;

        // 默认池的实例归主界面管 —— 分池页上点「停止」只是不碰它，
        // 真要停得去主页点「停止网关」，否则两边会各说各话。
        if (IsAdopted)
        {
            Runner = null;
            return;
        }

        Runner = null;
        try
        {
            runner.Stop();
            Log?.Invoke($"[{Config.DisplayName}] 已停止");
        }
        catch { }
        finally { runner.Dispose(); }
    }

    public void Dispose()
    {
        if (IsAdopted) { Runner = null; return; }
        Stop();
    }
}

/// <summary>
/// 池的调度中心：把「池配置」变成「真的在跑的实例」。
///
/// 它同时是账号操作（accounts list / add / use / remove）的路由器 ——
/// 界面点哪个池，命令就带着那个池的 WORKBUDDY_API_HOME 去跑，
/// 于是同一个命令天然作用于不同的账号集合。
/// </summary>
public sealed class PoolManager : IDisposable
{
    private readonly Dictionary<string, PoolRuntime> _runtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dispatcher _dispatcher;

    public PoolManager(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>池配置（和 pools.json 同步）。</summary>
    public ObservableCollection<PoolConfig> Pools { get; } = new();

    /// <summary>默认池配置的快捷方式。</summary>
    public PoolConfig Default => Pools.First(p => p.IsDefault);

    /// <summary>任何池打印的日志。</summary>
    public event Action<string>? Log;

    /// <summary>
    /// 某个池的实例退出了。
    ///
    /// 界面收到之后要刷新分池页 —— 否则那个卡片一直显示「运行中」，
    /// 「启动」按钮点不动（实测踩到）。
    /// </summary>
    public event Action<PoolConfig>? PoolExited;

    public void Load()
    {
        var loaded = PoolStore.Load();
        Pools.Clear();
        foreach (var p in loaded) Pools.Add(p);
    }

    public void Save() => PoolStore.Save(Pools);

    /// <summary>取某个池的运行态（没有就建一个空壳）。</summary>
    public PoolRuntime RuntimeOf(PoolConfig pool)
    {
        var key = pool.IsDefault ? "<default>" : pool.DirectoryName;
        if (!_runtimes.TryGetValue(key, out var rt))
        {
            rt = new PoolRuntime
            {
                Config = pool,
                // 默认池的实例由主界面持有，分池页不自己起
                IsAdopted = pool.IsDefault,
            };
            rt.Log += line => Log?.Invoke(line);
            rt.Exited += cfg => PoolExited?.Invoke(cfg);
            _runtimes[key] = rt;
        }
        return rt;
    }

    /// <summary>
    /// 把主界面那个网关交给默认池「代管」。
    ///
    /// 主界面本来就有自己的 GatewayRunner（聊天、状态、模型都在用它），
    /// 让分池页把这个实例登记过来，两个页面才能对同一个进程说同一句话。
    /// </summary>
    public PoolRuntime AdoptDefault(GatewayRunner runner)
    {
        var rt = RuntimeOf(Default);
        rt.Adopt(runner);
        return rt;
    }

    /// <summary>
    /// 建一个池：分配目录名、端口、API Key，并**立刻把目录建出来**。
    ///
    /// 目录先建好是有意的 —— 用户下一步就是「往这个池里加账号」，
    /// 如果那时候目录还不存在，node 那边会因为找不到状态目录而报一些
    /// 让人摸不着头脑的错。空目录是合法状态（就是个空账号库）。
    /// </summary>
    public PoolConfig Create(string name, string? note, int? port = null)
    {
        var display = string.IsNullOrWhiteSpace(name) ? "新池" : name.Trim();

        // 注意：把 Pools 一起传进去。
        // 只看「现在有没有人监听」是不够的 —— 已经分配给别的池、但那个池
        // 现在没在跑的端口，也是占用的。实测就是这么撞的：
        // 默认池和新建的池都被分到了 8789。
        var chosenPort = PoolStore.PickPort(Pools, port);

        var dir = PoolStore.UniqueDirectoryName(
            PoolStore.MakeDirectoryName(display, chosenPort), Pools);

        var pool = new PoolConfig
        {
            Name = display,
            DirectoryName = dir,
            Port = chosenPort,
            ApiKey = "sk-pool-" + Guid.NewGuid().ToString("N")[..12],
            Note = string.IsNullOrWhiteSpace(note) ? null : note!.Trim(),
            AutoStart = false,
        };

        Directory.CreateDirectory(pool.VaultHome());
        Pools.Add(pool);
        Save();
        Log?.Invoke($"[分池] 已新建「{pool.DisplayName}」，端口 {pool.Port}，目录 {pool.VaultHome()}");
        return pool;
    }

    /// <summary>删池：先停实例，再删账号目录（可选）。</summary>
    public void Remove(PoolConfig pool, bool deleteAccounts)
    {
        if (pool.IsDefault)
            throw new InvalidOperationException("默认池不能删除。");

        Stop(pool);

        if (deleteAccounts)
        {
            try
            {
                var home = pool.VaultHome();
                if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
                Log?.Invoke($"[分池] 已删除目录 {home}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[分池] 目录删不掉（可能被占用）：{ex.Message}");
            }
        }

        Pools.Remove(pool);
        _runtimes.Remove(pool.DirectoryName);
        Save();
        Log?.Invoke($"[分池] 已移除「{pool.DisplayName}」");
    }

    /// <summary>改池的名字 / 备注 / 端口。改端口需要重启实例才生效。</summary>
    public void Update(PoolConfig pool, string name, string? note, int port)
    {
        var portChanged = pool.Port != port;

        pool.Name = string.IsNullOrWhiteSpace(name) ? pool.DisplayName : name.Trim();
        pool.Note = string.IsNullOrWhiteSpace(note) ? null : note!.Trim();

        if (port is > 0 and <= 65535 && port != pool.Port)
        {
            if (Pools.Any(p => !ReferenceEquals(p, pool) && p.Port == port))
                throw new InvalidOperationException($"端口 {port} 已经被另一个池用了。");
            pool.Port = port;
        }

        Save();

        // 端口改了但实例还在跑 = 图上的地址是错的，直接停掉让它重来
        if (portChanged && RuntimeOf(pool).IsRunning) Stop(pool);
    }

    public async Task<bool> StartAsync(PoolConfig pool, string nodePath, string apiDir, string provider, string? authFile)
    {
        var rt = RuntimeOf(pool);
        if (rt.IsRunning) return true;
        await rt.StartAsync(nodePath, apiDir, provider, authFile);
        return true;
    }

    public void Stop(PoolConfig pool) => RuntimeOf(pool).Stop();

    public void StopAll()
    {
        foreach (var rt in _runtimes.Values) rt.Stop();
    }

    /// <summary>某个池此刻有几个账号（读它的保险库文件，不打扰 node）。</summary>
    public int CountAccounts(PoolConfig pool)
    {
        try
        {
            var path = pool.VaultPath();
            if (!File.Exists(path)) return 0;
            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<VaultDocument>(json);
            return doc?.Accounts?.Count ?? 0;
        }
        catch
        {
            return -1;   // -1 = 读不出来，界面上显示「?」
        }
    }

    /// <summary>
    /// 一个池的账号概况。
    ///
    /// 为什么要这个方法：用户问「多池子的话每个池子用哪个账号在哪看」。
    /// 以前卡片上只显示「N 个账号」，要知道用哪个号得点「管理账号」跳过去看 ——
    /// 池一多就得一个个点，很绕。
    ///
    /// 直接读保险库文件（不走 node）：
    ///   · node 启动一次要好几秒，而这个信息只需要本地文件
    ///   · 这里不关心「登录还有效吗」这种活信息，只看「存了哪些号、选的哪个」
    /// </summary>
    public PoolAccountSummary ReadAccounts(PoolConfig pool)
    {
        try
        {
            var path = pool.VaultPath();
            if (!File.Exists(path)) return PoolAccountSummary.Empty;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var labels = new List<string>();
            var ids = new List<string>();

            if (root.TryGetProperty("accounts", out var accs) && accs.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in accs.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;

                    var label = a.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String
                        ? l.GetString() ?? ""
                        : "";
                    var uid = a.TryGetProperty("uid", out var u) && u.ValueKind == JsonValueKind.String
                        ? u.GetString() ?? ""
                        : "";

                    // 没名字就用 uid 前 8 位 —— 和账号页的规矩保持一致
                    if (string.IsNullOrWhiteSpace(label))
                        label = uid.Length > 8 ? uid[..8] : uid;

                    labels.Add(label);
                    ids.Add(uid);
                }
            }

            // 当前选中的是哪个
            var activeId = root.TryGetProperty("activeId", out var act) && act.ValueKind == JsonValueKind.String
                ? (act.GetString() ?? "").TrimEnd(':')
                : "";

            string? activeLabel = null;
            if (activeId.Length > 0)
            {
                var idx = ids.FindIndex(x => x.Equals(activeId, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) activeLabel = labels[idx];
            }

            return new PoolAccountSummary(labels, activeLabel);
        }
        catch
        {
            // 读坏了不报错，界面显示「读不出来」
            return new PoolAccountSummary(null, null);
        }
    }

    /// <summary>
    /// 列出某个池的账号（走 node，因为这里要的是「登录还有效吗」这类活信息）。
    /// 命令本身和以前一模一样 —— 区别只在多带了一个 WORKBUDDY_API_HOME。
    /// </summary>
    public Task<CliResult> RunAccountsAsync(
        PoolConfig pool, string nodePath, string apiDir, string extraArgs = "", CancellationToken ct = default)
    {
        var home = pool.IsDefault ? null : pool.VaultHome();
        return CliRunner.RunAsync(
            nodePath, apiDir, $"src/bin.js accounts --json --no-checkin {extraArgs}".Trim(),
            stdin: null, ct: ct, vaultHome: home);
    }

    /// <summary>
    /// 从桌面 App **当前登录的那个号**导入到这个池。
    ///
    /// 注意这是唯一一种加号方式：workbuddy_api 读的是桌面 App 的登录文件，
    /// 所以「换号」必须在 App 里换，然后对目标池再跑一次这个命令。
    /// </summary>
    public Task<CliResult> AddAccountAsync(
        PoolConfig pool, string nodePath, string apiDir, string label, CancellationToken ct = default)
    {
        var home = pool.IsDefault ? null : pool.VaultHome();
        var safeLabel = label.Replace("\"", "");
        return CliRunner.RunAsync(
            nodePath, apiDir, $"src/bin.js accounts add --label \"{safeLabel}\" --json",
            stdin: null, ct: ct, vaultHome: home);
    }

    /// <summary>把某个池里的某个号设为「用这个」。</summary>
    public Task<CliResult> UseAccountAsync(
        PoolConfig pool, string nodePath, string apiDir, string id, CancellationToken ct = default)
    {
        var home = pool.IsDefault ? null : pool.VaultHome();
        return CliRunner.RunAsync(
            nodePath, apiDir, $"src/bin.js accounts use \"{id}\" --json",
            stdin: null, ct: ct, vaultHome: home);
    }

    /// <summary>把一个号从池里删掉。</summary>
    public Task<CliResult> RemoveAccountAsync(
        PoolConfig pool, string nodePath, string apiDir, string id, CancellationToken ct = default)
    {
        var home = pool.IsDefault ? null : pool.VaultHome();
        return CliRunner.RunAsync(
            nodePath, apiDir, $"src/bin.js accounts remove \"{id}\" --json",
            stdin: null, ct: ct, vaultHome: home);
    }

    /// <summary>
    /// 把一个号从源池搬到目标池。
    ///
    /// 具体逻辑在 <see cref="VaultMover"/> —— 那里是纯文件操作，
    /// 可以脱离 WPF 直接跑单元测试。这里只负责记一条日志。
    ///
    /// 为什么这么做：这段逻辑原本写在这里，而 PoolManager 要 WPF 的
    /// Dispatcher 才能构造 —— 于是「搬号」一次都没被测过。
    /// 抽出去之后立刻测出了两个问题：
    ///   1. 写出去的 JSON 是 PascalCase，读的时候找 camelCase，永远读不回来
    ///   2. 而且它压根没接到界面上，是死代码
    /// </summary>
    public string MoveAccount(PoolConfig from, PoolConfig to, string accountId)
    {
        var r = VaultMover.Move(from.VaultPath(), to.VaultPath(), accountId);
        var shortId = r.AccountId.Length > 8 ? r.AccountId[..8] : r.AccountId;
        Log?.Invoke($"[分池] 账号 {shortId}：{from.DisplayName} → {to.DisplayName}"
                    + (r.AlreadyExisted ? "（目标池里已有，已更新）" : ""));
        return $"已搬到「{to.DisplayName}」";
    }


    public void Dispose()
    {
        StopAll();
        foreach (var rt in _runtimes.Values) rt.Dispose();
        _runtimes.Clear();
    }

    // ── 和 node 的 JSON 打交道的两个最小结构 ────────────────────────

    private sealed class VaultDocument
    {
        [System.Text.Json.Serialization.JsonPropertyName("accounts")]
        public List<JsonElement>? Accounts { get; set; }
    }
}

/// <summary>
/// 一个池的账号概况 —— 给分池页的卡片用。
///
/// 用户问「多池子的话每个池子用哪个账号在哪看」，以前得点「管理账号」
/// 跳过去才知道。现在卡片上直接写出来。
///
/// @param Labels       所有账号的名字（按保险库里的顺序）。null = 读不出来。
/// @param ActiveLabel  当前选中的那个号的名字。null = 没选（或选了个不存在的、读不出来）。
/// </summary>
public sealed record PoolAccountSummary(List<string>? Labels, string? ActiveLabel)
{
    public static readonly PoolAccountSummary Empty = new(new List<string>(), null);

    /// <summary>读不出来（文件坏了 / 权限问题）。</summary>
    public bool IsBroken => Labels is null;

    public int Count => Labels?.Count ?? 0;

    /// <summary>卡片上那行「当前使用：XXX」。</summary>
    public string ActiveText => IsBroken
        ? "账号读不出来"
        : Count == 0
            ? "还没有账号"
            : ActiveLabel is { Length: > 0 } name
                ? $"当前使用：{name}"
                : "当前使用：桌面 App 登录的那个号";

    /// <summary>账号名单（太多了就截断）。</summary>
    public string NamesText
    {
        get
        {
            if (IsBroken) return "";
            if (Labels is null || Labels.Count == 0) return "";

            // 最多列 6 个，多了省略 —— 卡片宽度有限
            const int max = 6;
            var shown = Labels.Take(max).ToList();
            var text = string.Join("、", shown);
            if (Labels.Count > max) text += $" 等 {Labels.Count} 个";
            return text;
        }
    }

    public bool HasNames => NamesText.Length > 0;
}
