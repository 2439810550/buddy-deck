using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkBuddyPanel.Services;

/// <summary>
/// 一个账号池。
///
/// 池是「一组账号 + 一个独立网关实例」的组合：
///
///     池 → 独立保险库目录（~/.workbuddy-api/pools/&lt;目录名&gt;/accounts.cn.json）
///        → 独立网关进程（自己的端口）
///        → 独立 API Key
///
/// 为什么要这样拆：workbuddy_api 的保险库里只有一个 activeId ——
/// 也就是「此刻烧哪个号」。一个实例同一时刻只能用一个号，
/// 所以要让几组号互不干扰地各自用，只能起多个实例。
/// </summary>
public sealed class PoolConfig
{
    /// <summary>界面上的名字（可以重复，不影响隔离）。</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 磁盘上的目录名 —— 这是**真正的唯一标识**。
    ///
    /// 取值规则（见 <see cref="PoolStore.MakeDirectoryName"/>）：
    ///   - 默认池 → 空字符串，表示直接用 ~/.workbuddy-api（不套 pools 子目录），
    ///     这样单池用户的账号文件位置和以前完全一样，不会有迁移问题。
    ///   - 其他池 → 形如 "p-friend" 的 ASCII 短名。
    ///
    /// 用 ASCII 是因为这个值会进文件路径，也会进子进程的环境变量。
    /// 中文目录名在 Windows 上能用，但一旦哪层编码出问题就很难查。
    /// </summary>
    public string DirectoryName { get; set; } = "";

    /// <summary>网关监听端口。</summary>
    public int Port { get; set; } = 8787;

    /// <summary>这个池的 API Key（客户端要填的那个）。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>界面显示的备注（给谁用的）。</summary>
    public string? Note { get; set; }

    /// <summary>程序启动时自动拉起这个池。默认池恒为 true。</summary>
    public bool AutoStart { get; set; }

    /// <summary>
    /// 是不是默认池。
    ///
    /// 默认池的特点：不套 pools 子目录（DirectoryName 为空）、不能删、端口跟
    /// settings.json 里那个 Port 同步。它的存在保证了「只有一个池」的用户
    /// 完全察觉不到分池功能的存在。
    /// </summary>
    [JsonIgnore]
    public bool IsDefault => string.IsNullOrEmpty(DirectoryName);

    /// <summary>界面上显示的状态文案用这个。</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? (IsDefault ? "默认池" : DirectoryName)
        : Name!;

    /// <summary>
    /// 这个池的保险库目录（绝对路径）。
    /// 不保证存在 —— 建目录是 <see cref="PoolStore"/> 的事。
    /// </summary>
    public string VaultHome()
    {
        var root = PoolStore.RootHome();
        return IsDefault ? root : Path.Combine(root, "pools", DirectoryName);
    }

    /// <summary>保险库目录里的账号文件。</summary>
    public string VaultPath() => Path.Combine(VaultHome(), "accounts.cn.json");
}

/// <summary>
/// 池配置的持久化，存在 %APPDATA%\WorkBuddyPanel\pools.json。
///
/// 单独一个文件、不复用 settings.json，理由：
///   settings.json 是「环境怎么跑」（node 在哪、端口多少），
///   pools.json 是「有几套号、各归谁」—— 后者的结构会长，混在一起
///   以后加字段容易和用户手改的配置打架。
/// </summary>
public static class PoolStore
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkBuddyPanel");

    public static string FilePath => Path.Combine(Dir, "pools.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>workbuddy_api 的状态根目录（默认池用的那个）。</summary>
    public static string RootHome()
    {
        var env = Environment.GetEnvironmentVariable("WORKBUDDY_API_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".workbuddy-api");
    }

    /// <summary>上次读取失败的原因（正常时 null）。</summary>
    public static string? LastLoadError { get; private set; }

    /// <summary>
    /// 从一个界面名字造出磁盘目录名。
    ///
    /// 只保留 ASCII 字母数字，转小写，加 "p-" 前缀并截断 ——
    /// 这个名字会进路径和子进程环境变量，越保守越好。中文名会变成空串，
    /// 这时退回用端口号，保证仍然唯一。
    /// </summary>
    public static string MakeDirectoryName(string displayName, int port)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in displayName ?? "")
        {
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
                sb.Append(char.ToLowerInvariant(ch));
            else if (ch is '-' or '_' && sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 24) slug = slug[..24].TrimEnd('-');
        if (slug.Length == 0) slug = "port" + port.ToString();
        return "p-" + slug;
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
    /// 读配置。文件不存在或坏了都返回「只有一个默认池」。
    ///
    /// 坏文件会先备份成 pools.json.broken 再走默认值 ——
    /// 和 AppSettings 一样的策略：不能拦启动，也不能默默吞掉用户数据。
    /// </summary>
    public static List<PoolConfig> Load()
    {
        LastLoadError = null;
        var result = new List<PoolConfig>();
        var existed = File.Exists(FilePath);

        try
        {
            if (existed)
            {
                var json = File.ReadAllText(FilePath);
                var doc = JsonSerializer.Deserialize<PoolDocument>(json, Options);
                if (doc?.Pools is { Count: > 0 })
                {
                    result.AddRange(doc.Pools.Where(p => p is not null));
                }
            }
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            try { File.Copy(FilePath, FilePath + ".broken", overwrite: true); } catch { }
        }

        // 默认池必须存在，而且永远排第一
        var def = result.FirstOrDefault(p => p!.IsDefault, null);
        if (def is null)
        {
            def = new PoolConfig { Name = "默认池", DirectoryName = "", Port = 8787, AutoStart = true };
            result.Insert(0, def);
        }
        else
        {
            result.Remove(def);
            result.Insert(0, def);
            def.AutoStart = true;          // 默认池永远跟着程序起
        }

        Normalize(result);

        // 默认池没设过 key 时补一个，和 settings.json 的老默认值保持一致
        if (string.IsNullOrWhiteSpace(def.ApiKey)) def.ApiKey = "sk-workbuddy-local";

        // 文件不存在（或读坏了）就把补好的默认配置落盘。
        //
        // 不落盘的话，用户下次打开还是「文件不存在」，永远靠内存里的默认值活着 ——
        // 而且分池页上改了东西也看不出到底存哪了。
        if (!existed || LastLoadError is not null)
        {
            try { Save(result); } catch { /* 存不下也不该拦启动 */ }
        }

        return result;
    }

    /// <summary>把各池的端口 / key 补齐，并把端口冲突挪开。</summary>
    private static void Normalize(List<PoolConfig> pools)
    {
        var used = new HashSet<int>();

        foreach (var p in pools)
        {
            if (p.Port is < 1 or > 65535) p.Port = 0;
        }

        // 默认池先占位，保证它的端口不会被别的池抢走
        var def = pools.First(p => p.IsDefault);
        if (def.Port == 0) def.Port = 8787;
        used.Add(def.Port);

        foreach (var p in pools.Where(p => !p.IsDefault))
        {
            if (p.Port == 0 || used.Contains(p.Port))
            {
                // 从 8788 起找第一个空位。默认池占着 8787，所以这里天然避开。
                var candidate = 8788;
                while (used.Contains(candidate) && candidate < 65535) candidate++;
                p.Port = candidate;
            }
            used.Add(p.Port);

            if (string.IsNullOrWhiteSpace(p.ApiKey))
                p.ApiKey = "sk-pool-" + Guid.NewGuid().ToString("N")[..12];
        }
    }

    /// <summary>原子写回。</summary>
    public static void Save(IEnumerable<PoolConfig> pools)
    {
        Directory.CreateDirectory(Dir);
        var doc = new PoolDocument { Version = 1, Pools = pools.ToList() };
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>
    /// 找一个当前没被占用的端口，从 from 开始往上找。
    /// 只查「有没有人监听」，不查 pools.json —— 用户可能自己手工跑了个别的服务。
    /// </summary>
    public static int FindFreePort(int from = 8788)
    {
        for (var port = from; port < from + 200 && port < 65535; port++)
        {
            if (!IsPortInUse(port)) return port;
        }
        return from;
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
        catch
        {
            // 查不到就当空闲，反正真正 bind 的时候还会报错
        }
        return false;
    }

    private sealed class PoolDocument
    {
        public int Version { get; set; } = 1;
        public List<PoolConfig> Pools { get; set; } = new();
    }
}
