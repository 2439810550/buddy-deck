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
    /// </summary>
    public static string? FindNode(string? hint = null)
    {
        // 1) 用户手填的
        if (!string.IsNullOrWhiteSpace(hint) && File.Exists(hint)) return hint;

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
        // 1) 用户手填的（允许填仓库根、bin.js 所在目录、或 src 的上层）
        if (!string.IsNullOrWhiteSpace(hint))
        {
            var trimmed = hint.TrimEnd('\\', '/');
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
        if (string.IsNullOrWhiteSpace(nodePath) || !File.Exists(nodePath)) return PathOrigin.Missing;

        if (IsSameFile(nodePath, BundledNodePath)) return PathOrigin.Bundled;
        if (!string.IsNullOrWhiteSpace(_manualNodeHint) && IsSameFile(nodePath, _manualNodeHint)) return PathOrigin.Manual;
        return PathOrigin.System;
    }

    public static PathOrigin ApiOrigin(string? apiDirectory)
    {
        if (string.IsNullOrWhiteSpace(apiDirectory) || !LooksLikeApiDir(apiDirectory)) return PathOrigin.Missing;

        if (IsSameFile(apiDirectory, BundledApiDirectory)) return PathOrigin.Bundled;
        if (!string.IsNullOrWhiteSpace(_manualApiHint) && IsSameFile(apiDirectory, _manualApiHint)) return PathOrigin.Manual;
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

    private static bool IsSameFile(string a, string b)
    {
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
