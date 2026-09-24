using System.IO;

namespace WorkBuddyPanel.Services;

/// <summary>某个路径是从哪来的，界面上要如实告诉用户。</summary>
public enum PathOrigin
{
    Missing,
    Manual,
    Bundled,
    System,
}

public static class PathOriginText
{
    public static string Describe(this PathOrigin origin) => origin switch
    {
        PathOrigin.Manual => "手动指定",
        PathOrigin.Bundled => "程序自带",
        PathOrigin.System => "系统安装",
        _ => "未找到",
    };
}

/// <summary>
/// 找 node.exe 和 workbuddy_api 目录。
///
/// 优先级：手动指定 → 程序自带的便携版 → 系统安装的。
/// 「程序自带」这一层是为了让用户**什么都不用装**：发布包里带了 node.exe
/// 和一份 workbuddy_api 源码，解压双击就能跑。
/// </summary>
public static class ProcessLocator
{
    private static readonly string[] NodeCandidateNames = ["node.exe", "node"];

    /// <summary>发布包里附带的运行时目录。</summary>
    public static string RuntimeRoot => Path.Combine(AppContext.BaseDirectory, "runtime");

    public static string BundledNodePath => Path.Combine(RuntimeRoot, "node.exe");

    public static string BundledApiDirectory => Path.Combine(RuntimeRoot, "workbuddy_api");

    /// <summary>
    /// 把相对路径按 **exe 所在目录** 展开成绝对路径。
    ///
    /// 为什么不能靠进程的当前目录：--maintain 是计划任务拉起来的，当前目录可能是
    /// System32；快捷方式也可以把「起始位置」改成别的。而这些路径是相对发布包说的，
    /// 唯一正确的基准就是 exe 自己待的那个目录。
    /// 绝对路径原样返回，空值返回 null。
    /// </summary>
    public static string? MakeAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var trimmed = path.Trim();
        try
        {
            // 已经是绝对路径（含 "C:\..." 和 UNC）就原样用，别再规范化 ——
            // 用户自己填的路径显示成什么样就保持什么样
            return Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, trimmed));
        }
        catch
        {
            // 路径里有非法字符：交给下游按「找不到」处理，别在这里抛
            return trimmed;
        }
    }

    /// <summary>这个路径是不是「相对 exe 目录」写的（界面上要提示一句）。</summary>
    public static bool IsRelative(string? path)
        => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path.Trim());

    /// <summary>在 PATH 里逐个目录找可执行文件。</summary>
    public static string? Which(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;
            try
            {
                var full = Path.Combine(dir, exeName);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // 非法路径段直接跳过
            }
        }

        return null;
    }

    /// <summary>
    /// 定位 node.exe。hint 是用户手填的路径，只有真的存在才会采用。
    /// hint 允许是相对路径（发布版的默认值就是 .\runtime\node.exe），按 exe 目录展开。
    /// </summary>
    public static string? FindNode(string? hint = null)
    {
        // 1) 用户手填的（也可能是发布版的默认值）
        var hinted = MakeAbsolute(hint);
        if (hinted is not null && File.Exists(hinted)) return hinted;

        // 2) 发布包自带的便携版 —— 用户没装 Node 也能跑，靠的就是这一条
        var bundled = BundledNodePath;
        if (File.Exists(bundled)) return bundled;

        // 3) 系统装的
        foreach (var name in NodeCandidateNames)
        {
            var found = Which(name);
            if (found is not null) return found;
        }

        foreach (var guess in new[]
                 {
                     @"C:\Program Files\nodejs\node.exe",
                     @"C:\Program Files (x86)\nodejs\node.exe",
                     @"D:\nodejs\node.exe",
                     @"D:\Program Files\nodejs\node.exe",
                 })
        {
            if (File.Exists(guess)) return guess;
        }

        return null;
    }

    /// <summary>判断某个目录是不是 workbuddy_api 的根目录。</summary>
    private static bool LooksLikeApiDir(string? dir)
        => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "src", "bin.js"));

    /// <summary>
    /// 定位 workbuddy_api 目录。
    /// </summary>
    public static string? FindApiDirectory(string? hint = null)
    {
        // 1) 用户手填的（允许填仓库根、bin.js 所在目录、或 src 的上层；
        //    也允许相对路径 —— 发布版的默认值就是 .\runtime\workbuddy_api）
        var hinted = MakeAbsolute(hint);
        if (hinted is not null)
        {
            var trimmed = hinted.TrimEnd('\\', '/');
            if (LooksLikeApiDir(trimmed)) return trimmed;
            var parent = Path.GetDirectoryName(trimmed);
            if (LooksLikeApiDir(parent)) return parent;
        }

        // 2) 发布包自带的那一份
        if (LooksLikeApiDir(BundledApiDirectory)) return BundledApiDirectory;

        // 3) 开发布局：从 exe 所在目录向上逐级找
        var roots = new List<string?>
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var dir = new DirectoryInfo(root);
            for (var depth = 0; depth < 6 && dir is not null; depth++)
            {
                if (LooksLikeApiDir(dir.FullName)) return dir.FullName;

                foreach (var name in new[] { "workbuddy_api", "workbuddy-api" })
                {
                    var candidate = Path.Combine(dir.FullName, name);
                    if (LooksLikeApiDir(candidate)) return candidate;
                }

                dir = dir.Parent;
            }
        }

        foreach (var guess in new[]
                 {
                     @"D:\workfandaiapi\workbuddy_api",
                     @"C:\workfandaiapi\workbuddy_api",
                 })
        {
            if (LooksLikeApiDir(guess)) return guess;
        }

        return null;
    }

    /// <summary>这个 node 路径是哪来的。</summary>
    public static PathOrigin NodeOrigin(string? nodePath)
    {
        // 先展开：发布版的默认值是 .\runtime\node.exe，不展开就跟自带的绝对路径比不上，
        // 会被误报成「系统安装」
        var resolved = MakeAbsolute(nodePath);
        if (resolved is null || !File.Exists(resolved)) return PathOrigin.Missing;

        if (IsSameFile(resolved, BundledNodePath)) return PathOrigin.Bundled;
        if (!string.IsNullOrWhiteSpace(_manualNodeHint) && IsSameFile(resolved, MakeAbsolute(_manualNodeHint)))
            return PathOrigin.Manual;
        return PathOrigin.System;
    }

    public static PathOrigin ApiOrigin(string? apiDirectory)
    {
        var resolved = MakeAbsolute(apiDirectory);
        if (resolved is null || !LooksLikeApiDir(resolved)) return PathOrigin.Missing;

        if (IsSameFile(resolved, BundledApiDirectory)) return PathOrigin.Bundled;
        if (!string.IsNullOrWhiteSpace(_manualApiHint) && IsSameFile(resolved, MakeAbsolute(_manualApiHint)))
            return PathOrigin.Manual;
        return PathOrigin.System;
    }

    /// <summary>本次探测时用户手填的路径，用来判断来源是「手动指定」还是别的。</summary>
    private static string? _manualNodeHint;
    private static string? _manualApiHint;

    /// <summary>一次性解析出 node + api 目录，并记住用户手填的提示路径。</summary>
    public static (string? Node, string? Api) Resolve(string? nodeHint, string? apiHint)
    {
        _manualNodeHint = nodeHint;
        _manualApiHint = apiHint;
        return (FindNode(nodeHint), FindApiDirectory(apiHint));
    }

    private static bool IsSameFile(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

}
