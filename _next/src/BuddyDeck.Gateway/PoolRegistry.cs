using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuddyDeck.Gateway;

/// <summary>
/// 一个账号池的配置。
///
/// ## 池是什么（概念抄自旧版 `WorkBuddyPanel/Services/PoolStore.cs`，不另发明）
///
/// ```
/// 池 = 一组账号 + 独立保险库 + 独立端口 + 独立 API Key
/// ```
///
/// **为什么必须这样拆**：保险库里只有一个 `activeId`（「此刻烧哪个号」），
/// 所以**一个网关实例同一时刻只能用一个号**。要让几组号互不干扰地各自用，
/// 只能给每组配一套（账户集 + 端口 + key）。
///
/// ## `DirectoryName` 才是唯一标识，`Name` 只是给人看的
///
/// - 默认池 → `DirectoryName` 为**空字符串**，表示直接用工作目录下的保险库
///   （单池用户的账号文件位置和以前完全一样，没有迁移问题）
/// - 其他池 → 形如 `p-志强` 的目录名，保险库在 `pools/<目录名>/` 下
///
/// **为什么不用 `Name` 当标识**：中文名可以重复（用户可能建两个都叫「测试」的池），
/// 而且它要进文件路径 —— 用 `DirectoryName` 做键，重名由
/// <see cref="PoolRegistry.MakeDirectoryName"/> + 去重来处理。
/// </summary>
public sealed class PoolConfig
{
    /// <summary>界面上显示的名字（可重复，不影响隔离）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// 磁盘上的目录名 —— **真正的唯一标识**。空字符串 = 默认池。
    /// </summary>
    [JsonPropertyName("directoryName")]
    public string DirectoryName { get; set; } = "";

    /// <summary>这个池的网关监听端口。</summary>
    [JsonPropertyName("port")]
    public int Port { get; set; } = PoolRegistry.DefaultPort;

    /// <summary>
    /// 这个池的 API Key（`ApiKey` 是**服务端配置**：它要求来访者出示什么）。
    /// 空 = 不鉴权。
    /// </summary>
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";

    /// <summary>这个池专用的保险库文件路径。空 = 按 <see cref="DirectoryName"/> 推导。</summary>
    [JsonPropertyName("vaultPath")]
    public string VaultPath { get; set; } = "";

    /// <summary>界面备注（给谁用的）。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>程序启动时自动拉起。默认池恒为 true。</summary>
    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; set; }

    /// <summary>是不是默认池（`DirectoryName` 为空）。</summary>
    [JsonIgnore]
    public bool IsDefault => string.IsNullOrEmpty(DirectoryName);

    /// <summary>界面上显示的名字。</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? (IsDefault ? "默认池" : DirectoryName)
        : Name;
}

/// <summary>
/// `pools.json` 文档。
/// </summary>
public sealed class PoolDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("pools")]
    public List<PoolConfig> Pools { get; set; } = [];
}

/// <summary>
/// 池注册表 —— `pools.json` 的读写与校验。
///
/// ## 为什么是「文件里的注册表」而不是「进程内动态对象」
///
/// 池的本质是「一套账号 + 一个监听端口」，而**一个进程只能绑一个端口**。
/// 所以真正的多池 = **多进程**，池注册表要跨进程存活 —— 它必须是**文件**，
/// 不是内存对象。
///
/// 本实例的角色是：**读这张表、回答有哪些池、按表去起/停其他实例**。
///
/// 这与旧版 `PoolStore` 完全同形（`%APPDATA%\WorkBuddyPanel\pools.json`）。
/// </summary>
public static class PoolRegistry
{
    /// <summary>默认端口（与旧版一致：客户端里写死的接入地址就是这个）。</summary>
    public const int DefaultPort = 8787;

    /// <summary>自动分配端口时的起点（默认池占着 8787，所以从这里开始）。</summary>
    public const int FirstAutoPort = 8788;

    /// <summary>目录名前缀（旧版同款 `p-` 前缀）。</summary>
    public const string DirectoryPrefix = "p-";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文池名不转义 —— 用 cat 看的时候要能读
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // ══ 读取 ══════════════════════════════════════════════════

    /// <summary>
    /// 读池列表。
    ///
    /// **文件不存在或内容坏掉都返回「只有一个默认池」** —— 分池是增强功能，
    /// 不能因为它的配置文件出问题就拦住整个网关启动。
    ///
    /// ⚠ 但**坏文件会备份成 `pools.json.broken`**：不能拦启动，也不能
    /// 默默吞掉用户的数据（他可能手改坏了，备份还在就有救）。
    /// </summary>
    /// <param name="path">`pools.json` 的绝对路径。</param>
    /// <param name="defaultPoolPort">默认池用哪个端口（通常就是本实例的端口）。</param>
    /// <param name="loadError">读了但坏了时，这里给出原因（正常时 null）。</param>
    public static List<PoolConfig> Load(string path, int defaultPoolPort, out string? loadError)
    {
        loadError = null;
        var result = new List<PoolConfig>();
        var existed = File.Exists(path);

        if (existed)
        {
            try
            {
                var json = File.ReadAllText(path);
                var doc = JsonSerializer.Deserialize<PoolDocument>(json, ReadOptions);
                if (doc?.Pools is { Count: > 0 })
                {
                    result.AddRange(doc.Pools.Where(p => p is not null));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                loadError = ex.Message;
                TryBackupBroken(path);
            }
        }

        // 默认池必须存在、永远排第一 —— 它保证「只有一个池」的用户察觉不到分池功能
        var def = result.FirstOrDefault(p => p.IsDefault);
        if (def is null)
        {
            def = new PoolConfig
            {
                Name = "默认池",
                DirectoryName = "",
                Port = defaultPoolPort,
                AutoStart = true,
            };
            result.Insert(0, def);
        }
        else
        {
            result.Remove(def);
            result.Insert(0, def);
            def.AutoStart = true;   // 默认池永远跟着程序起
        }

        Normalize(result, defaultPoolPort);
        return result;
    }

    private static void TryBackupBroken(string path)
    {
        try { File.Copy(path, path + ".broken", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 备份不上也不该拦启动
        }
    }

    /// <summary>把端口/key 补齐，并把端口冲突挪开。</summary>
    private static void Normalize(List<PoolConfig> pools, int defaultPoolPort)
    {
        foreach (var p in pools)
        {
            if (p.Port is < 1 or > 65535) p.Port = 0;
        }

        var used = new HashSet<int>();

        // ⚠ 默认池**先占位** —— 保证它的端口不会被别的池抢走
        var def = pools.First(p => p.IsDefault);
        def.Port = defaultPoolPort;
        used.Add(def.Port);

        foreach (var p in pools.Where(p => !p.IsDefault))
        {
            if (p.Port == 0 || used.Contains(p.Port)) p.Port = PickPortCore(used);
            used.Add(p.Port);

            // 每个池自己的 key（空的话补一个）—— 池之间**不该共用 key**，
            // 否则在界面上「A 池的 key」和「B 池的 key」是同一个，用户会以为没生效。
            if (string.IsNullOrWhiteSpace(p.ApiKey))
            {
                p.ApiKey = "sk-pool-" + Guid.NewGuid().ToString("N")[..12];
            }
        }
    }

    /// <summary>
    /// 从一个界面名字造出磁盘目录名（抄旧版 `MakeDirectoryName` 的规则）。
    ///
    /// ## 规则（只挡**真正会出问题**的）
    ///
    /// | 丢掉 | 为什么 |
    /// |---|---|
    /// | `/` `\` `:` | 路径分隔符/盘符 —— 会造出路径穿越或跳到别处 |
    /// | `*` `?` `"` `&lt;` `&gt;` `|` | Windows 保留字符，文件系统直接拒 |
    /// | 控制字符 | 把终端和日志搞乱 |
    /// | 开头的 `.` | 避免造出 `.` / `..` / 隐藏文件 |
    /// | 连续的点 | 旧版实测：`a/../../b` 丢掉斜杠后剩 `a....b`，看着脏且易被别的工具误解 |
    ///
    /// **中文保留** —— 旧版早期只留 ASCII，结果 `~/.workbuddy-api/pools/` 下
    /// 全是 `p-port8788` 这种，用户想手工备份某个池都认不出哪个是哪个。
    /// </summary>
    public static string MakeDirectoryName(string displayName, int port)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var ch in displayName ?? "")
        {
            if (ch is '/' or '\\' or ':') continue;
            if (ch is '*' or '?' or '"' or '<' or '>' or '|') continue;
            if (char.IsControl(ch)) continue;
            if (ch == '.' && sb.Length == 0) continue;
            if (ch == '.' && sb.Length > 0 && sb[^1] == '.') continue;

            sb.Append(ch);
        }

        // 首尾空白和点都要去掉 —— Windows 文件名不能以点或空格结尾
        var slug = sb.ToString().Trim().Trim('.', ' ');

        // 长度上限：算上 "p-" 前缀不超过 30 字符。
        // 中文一个字符占 3 字节，30 字符在各种路径长度限制下比较安全。
        const int maxSlug = 26;
        if (slug.Length > maxSlug) slug = slug[..maxSlug].TrimEnd('.', ' ');

        // 清完什么都不剩（全中文标点、全空格）—— 退回端口号
        if (slug.Length == 0) slug = "池" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return DirectoryPrefix + slug;
    }

    /// <summary>确保目录名在已有池里唯一（重名就加 -2 / -3）。</summary>
    public static string UniqueDirectoryName(string candidate, IEnumerable<PoolConfig> existing)
    {
        var taken = existing.Select(p => p.DirectoryName)
            .Where(d => !string.IsNullOrEmpty(d))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(candidate)) return candidate;

        for (var i = 2; i < 1000; i++)
        {
            var next = $"{candidate}-{i}";
            if (!taken.Contains(next)) return next;
        }

        return candidate + "-" + Guid.NewGuid().ToString("N")[..6];
    }

    /// <summary>
    /// 给新池挑一个端口。
    ///
    /// ## ⚠ 不能只看「现在有没有人监听」
    ///
    /// 端口被占用有**两种**情况：
    ///   ① 此刻真的有进程在听 —— `IsPortInUse` 查得到
    ///   ② **已被别的池分配了，但那个池现在没跑** —— 查不到
    ///
    /// 只看 ① 的话，旧版实测出现过：
    /// ```
    /// pools.json:  默认池 = 8789，朋友的池 = 8789     ← 两个池同一个端口
    /// ```
    /// 启第二个必然失败。所以「已分配给别的池的端口」也要算作占用 ——
    /// 这就是为什么本方法的签名要收 `existing`。
    /// </summary>
    /// <param name="existing">现有的池（它们的 Port 都算占用）。</param>
    /// <param name="preferred">用户指定的端口；为 null 或已被占用则自动挑。</param>
    public static int PickPort(IEnumerable<PoolConfig> existing, int? preferred = null)
    {
        var taken = existing.Select(p => p.Port)
            .Where(p => p is > 0 and <= 65535)
            .ToHashSet();

        if (preferred is > 0 and <= 65535
            && !taken.Contains(preferred.Value)
            && !IsPortInUse(preferred.Value))
        {
            return preferred.Value;
        }

        return PickPortCore(taken);
    }

    private static int PickPortCore(HashSet<int> taken)
    {
        for (var port = FirstAutoPort; port < 65535; port++)
        {
            if (!taken.Contains(port) && !IsPortInUse(port)) return port;
        }

        // 理论上到不了 —— 端口空间不可能全占满
        return FirstAutoPort;
    }

    /// <summary>本机有没有人在监听这个端口。</summary>
    public static bool IsPortInUse(int port)
    {
        try
        {
            var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            foreach (var ep in props.GetActiveTcpListeners())
            {
                if (ep.Port == port) return true;
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // 查不到就当空闲 —— 真正 bind 的时候还会报错，那时错误更准确
        }

        return false;
    }

    // ══ `DirectoryName` 的安全性 ══════════════════════════════

    /// <summary>
    /// 目录名是不是安全的（能安全地拼进路径）。
    ///
    /// ## 为什么必须有这道校验（这是**删库级**的防御）
    ///
    /// `DirectoryName` 来自 `pools.json` —— **用户可手改，也可能被别的东西篡改**。
    /// 而删池会 `Directory.Delete(recursive: true)`。不校验的话：
    ///
    /// ```
    /// "directoryName": "..\\..\\..\\重要文件"
    ///     ↓ Path.Combine 保留 ".."
    /// <vaultRoot>/pools/..\..\..\重要文件
    ///     ↓ Windows 解析
    /// <上三层>/重要文件
    ///     ↓ 删池
    /// Directory.Delete(recursive: true)   ← 用户的文件没了
    /// ```
    ///
    /// ## 规则（与 <see cref="Core.MioraClient.IsSafeMediaName"/> 同族思路）
    ///
    /// 用**允许清单**而不是黑名单：黑名单永远漏（`..` 的变体、UNC 路径、
    /// 短名、ADS…），允许清单只放我们真的会生成的东西。
    /// `MakeDirectoryName` 生成的就是 `p-<可见字符>`，所以这里也只认它。
    /// </summary>
    public static bool IsSafeDirectoryName(string? directoryName)
    {
        // 空 = 默认池，合法（它不套 pools 子目录）
        if (string.IsNullOrEmpty(directoryName)) return true;

        if (!directoryName.StartsWith(DirectoryPrefix, StringComparison.Ordinal)) return false;
        if (directoryName.Length > 64) return false;

        // 只允许前缀之后的可见字符，且禁止一切路径相关字符
        foreach (var ch in directoryName)
        {
            if (char.IsControl(ch)) return false;
            if (ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') return false;
        }

        // 再好用的字符也不能让归一化跑出前缀
        if (directoryName.Contains("..", StringComparison.Ordinal)) return false;

        return true;
    }

    /// <summary>
    /// 把池的目录名解析成**绝对保险库目录**。
    ///
    /// ⚠ 这里做**归一化后的容器检查**：即使 <see cref="IsSafeDirectoryName"/>
    /// 放过了，也再确认一次「归一化结果真的在 pools 目录里」。
    ///
    /// **前缀比对必须带上分隔符** —— 只比 `pools` 的话，
    /// 名字叫 `pools恶意` 的目录会被误判成「在内部」。
    ///
    /// @throws InvalidOperationException 目录名不合法或跑出了 pools 目录。
    /// </summary>
    /// <param name="poolsRoot">`pools` 子目录的绝对路径。</param>
    /// <param name="defaultVaultHome">默认池的保险库目录（`DirectoryName` 为空时用它）。</param>
    public static string ResolveVaultHome(string poolsRoot, string defaultVaultHome, PoolConfig pool)
    {
        if (pool.IsDefault) return Path.GetFullPath(defaultVaultHome);

        if (!IsSafeDirectoryName(pool.DirectoryName))
        {
            throw new InvalidOperationException(
                $"池「{pool.DisplayName}」的目录名不合法（{pool.DirectoryName}）。" +
                "它可能指向账号目录之外的位置 —— 多半是 pools.json 被改过。删掉重建即可。");
        }

        var root = Path.GetFullPath(poolsRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, pool.DirectoryName));

        // 前缀要比对 "pools" + 分隔符，不能只比 "pools"
        var prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"池「{pool.DisplayName}」的目录归一化后跑出了 pools 目录（{candidate}）。");
        }

        return candidate;
    }

    // ══ 写回 ══════════════════════════════════════════════════

    /// <summary>
    /// 原子写回（临时文件 + 替换）。
    ///
    /// 与保险库同款理由：写到一半崩掉就是个坏 JSON，
    /// 而坏文件会让用户丢掉全部分池配置。
    /// </summary>
    public static void Save(string path, IEnumerable<PoolConfig> pools)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var doc = new PoolDocument { Version = 1, Pools = [.. pools] };
        var json = JsonSerializer.Serialize(doc, Options);

        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}
