using System.Security.Cryptography;
using System.Text;

namespace BuddyDeck.Gateway;

/// <summary>
/// 代码指纹 —— 让面板能判断「端口上那个网关是不是最新代码」。
///
/// 算法**逐字对齐旧版 `build-id.js`**：把源码目录下所有 `.cs` 文件的
/// `name:size:floor(mtimeMs)` 排序后拼起来，取 sha256 十六进制的前 12 位。
///
/// ## 为什么不用 git commit
///
/// · 用户机器上可能没有 git
/// · 也可能不是 git 仓库（解压安装的场景）
/// · 「工作区有未提交改动」时 git 无法反映 —— 但代码确实变了
///
/// ## 为什么不用文件内容哈希
///
/// 每次启动都要读全部源码，没必要。mtime + size 已经足够区分
/// 「代码变了没有」。指纹一样 = 大概率没变。
///
/// ## 为什么要报 stale
///
/// 真实踩过的事故：8787 上跑着几小时前的孤儿进程，面板点「启动网关」
/// 时只看到「有人在监听」就采用了它 —— 于是改过的代码不生效，
/// 表现成「功能还是旧的」，排查方向完全跑偏。
/// 所以 `/healthz` 除了「我这份代码的指纹」，还要报「磁盘现在是什么指纹」。
/// </summary>
public static class BuildId
{
    /// <summary>指纹长度。旧版取 12 个十六进制字符。</summary>
    public const int Length = 12;

    /// <summary>读不到源码目录时的哨兵值（和旧版一致）。</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// 算一次指纹。
    ///
    /// @param sourceDirectory 源码目录（旧版是 `src/`）。
    /// @returns 12 位小写十六进制；目录读不到或没有 .cs 文件时返回 "unknown"。
    /// </summary>
    public static string Compute(string? sourceDirectory)
    {
        if (string.IsNullOrEmpty(sourceDirectory) || !Directory.Exists(sourceDirectory)) return Unknown;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unknown;
        }

        var parts = new List<string>();
        foreach (var file in files)
        {
            // ⚠ 跳过 bin/obj。
            //
            // 为什么必须跳：编译产物里有生成出来的 .cs（AssemblyInfo、
            // GlobalUsings……），它们的 mtime 每次 `dotnet build` 都变。
            // 不跳的话：
            //   · 每次重新编译都会让指纹变 → 重启一次网关指纹就换一个
            //     （实测踩到：同一份源码两次启动算出 d48f5c… 和 65fe9f…）
            //   · 于是指纹彻底失去意义 —— 它本来是用来回答
            //     「端口上那个网关跑的是不是最新的源码」的
            // 旧版没有这个问题，因为它只读 src/ 那一层（没有嵌套的 bin/obj）。
            var directory = Path.GetDirectoryName(file) ?? "";
            if (IsBuildOutput(directory)) continue;

            try
            {
                var info = new FileInfo(file);
                // floor(mtimeMs)：Node 的 Math.floor(st.mtimeMs)。
                // 这里用 UTC 换算，避免时区影响。
                var mtimeMs = (long)Math.Floor((info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalMilliseconds);

                // 键里带**相对路径**（不只是文件名）：
                // 两个子目录里的同名文件（比如两个 Program.cs）不能算成同一个。
                var relative = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
                parts.Add($"{relative}:{info.Length}:{mtimeMs}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到就跳过 —— 指纹少算一个文件也比启动失败强
            }
        }

        if (parts.Count == 0) return Unknown;

        // ⚠ 必须排序。
        //
        // 为什么：`Directory.EnumerateFiles` 不保证顺序 —— 文件系统给的
        // 顺序可能随目录项增删而变。不排序的话，同一份源码两次算出来的
        // 指纹可能不一样（实测踩到），那这个值就没法用来判断
        // 「代码变了没有」。
        //
        // 排序键带目录：不同子目录里的同名文件（比如两个 Program.cs）
        // 不能算成同一个。
        parts.Sort(StringComparer.Ordinal);

        var joined = string.Join("|", parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash).ToLowerInvariant()[..Length];
    }

    /// <summary>这个目录是不是编译产物（bin / obj）。</summary>
    private static bool IsBuildOutput(string directory)
    {
        foreach (var segment in directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// 进程级的指纹快照。
///
/// ## 为什么是「快照」
///
/// `Loaded` 代表「这个进程启动时加载的代码」。**只在启动时算一次**，
/// 之后是常量 —— 如果每次请求都重算，代码改了之后指纹会跟着变，
/// 那反而失去了「这个进程加载的是哪份代码」的语义。
/// </summary>
public sealed class BuildInfo
{
    public BuildInfo(string sourceDirectory, DateTimeOffset? startedAt = null)
    {
        SourceDirectory = sourceDirectory;
        Loaded = BuildId.Compute(sourceDirectory);
        StartedAt = startedAt ?? DateTimeOffset.UtcNow;
    }

    /// <summary>被指纹的源码目录。</summary>
    public string SourceDirectory { get; }

    /// <summary>这个进程启动时加载的代码指纹。</summary>
    public string Loaded { get; }

    /// <summary>进程启动时刻。只算一次。</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>当前磁盘上的指纹 —— 和 <see cref="Loaded"/> 可能不一样。</summary>
    public string Current => BuildId.Compute(SourceDirectory);

    /// <summary>
    /// 磁盘上的代码和正在跑的是不是同一份。
    ///
    /// 任一侧是 "unknown" 时返回 false —— 不知道的事不能报成「不一致」，
    /// 那会让用户以为自己的改动没生效。
    /// </summary>
    public bool IsStale
    {
        get
        {
            var now = Current;
            return now != BuildId.Unknown && Loaded != BuildId.Unknown && now != Loaded;
        }
    }

    /// <summary>ISO 8601 毫秒精度，和旧版 `Date.toISOString()` 同形。</summary>
    public string StartedAtIso => StartedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
}
