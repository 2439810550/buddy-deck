// 完整场景测试：模拟面板启动时的「检测旧网关 → 自动重启」流程。
//
// ## 场景（真实发生过的）
//
//   8787 上跑着几小时前起的网关（孤儿进程，父面板已退出）。
//   代码改了好几轮 —— 视频从「当成生图处理」改成了真正的视频生成。
//   但那个网关不会自动重载，它还在跑旧代码。
//
//   用户重启面板 → 面板看到「端口上有人在监听」→ 采用它
//   → 功能还是旧的。用户看到的是「视频还是生成不了」，
//   完全不知道为什么。
//
// ## 测试自己搭场景
//
// 不依赖「跑之前恰好有个旧网关」—— 那样第一次和第二次跑结果不一样，
// 等于没测。这里把产品源码拷一份到临时目录、加个标记让指纹不同，
// 然后从那儿起一个网关。它**就是**旧代码。
//
// ## 命脉
//
// 8787 上那个网关同时是 AI 助手（pi）的模型来源。这个测试只对它做
// **只读探测**（确认认得出、没被误碰），绝不停它。

using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

int pass = 0;
int fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {name}"); }
    else { fail++; Console.WriteLine($"  FAIL  {name}  {detail}"); }
}

void Section(string t) => Console.WriteLine($"\n  ── {t} ──");

int Port = 18798;
string apiDir = ResolveApiDirectory();
string oldDir = Path.Combine(Path.GetTempPath(), "reaper-test-oldgw");

Console.WriteLine($"  产品源码目录：{apiDir}");

// ── 前置：自己搭出「一个旧网关在跑」这个状态 ──
if (!PrepareOldGateway(apiDir, oldDir, Port))
{
    Console.WriteLine("  搭不出测试场景，退出");
    Cleanup(oldDir);
    return 1;
}

// ── ① 探测 ──
Section("① 探测：端口上是不是我们的网关");
Check($"{Port} 有人监听", HasListener(Port));
var found = FindGateways(Port);
Check($"{Port} 认得出是我们的网关", found.Count == 1, $"found {found.Count}");
foreach (var f in found) Console.WriteLine($"        PID {f.Pid}");

// ── ② 判断 ──
Section("② 判断：是不是旧代码");
var (noId, remoteId, started) = await ProbeHealth(Port);
Check("healthz 报出了 buildId", remoteId.Length > 0);
Console.WriteLine($"        它自报的 buildId : {remoteId}");
Console.WriteLine($"        启动时间         : {started:yyyy-MM-dd HH:mm:ss}");

var onDisk = ComputeFingerprint(apiDir);
Console.WriteLine($"        磁盘上的代码指纹 : {onDisk}");
Check("两个指纹不一样（它确实是旧代码）", remoteId != onDisk && onDisk.Length > 0);
Check("面板会判为「旧」", remoteId != onDisk || noId);

// ── ③ 重启：停掉旧的 ──
Section("③ 重启：停掉旧的");
if (found.Count == 1)
{
    int pid = found[0].Pid;
    try
    {
        using var proc = Process.GetProcessById(pid);
        proc.Kill(entireProcessTree: true);
        for (int i = 0; i < 50 && !proc.HasExited; i++) Thread.Sleep(100);
        Check($"PID {pid} 已停掉", proc.HasExited);
    }
    catch (ArgumentException) { Check($"PID {pid} 已停掉", true); }
    catch (Exception ex) { Check($"PID {pid} 已停掉", false, ex.Message); }
}
for (int i = 0; i < 30 && HasListener(Port); i++) Thread.Sleep(100);
Check("端口已释放（新的能起来了）", !HasListener(Port));

// ── ④ 起新代码的网关 ──
Section("④ 起新代码的网关，确认指纹变新");
var psi = new ProcessStartInfo
{
    FileName = ResolveNode(),
    Arguments = $"src/bin.js serve --port {Port} --api-key tk",
    WorkingDirectory = apiDir,
    UseShellExecute = false,
    CreateNoWindow = true,
};
Process.Start(psi);
for (int i = 0; i < 60; i++)
{
    Thread.Sleep(500);
    if (HasListener(Port)) break;
}
Check("新网关起来了", HasListener(Port));

var (_, newId, _) = await ProbeHealth(Port);
Console.WriteLine($"        新网关自报 buildId: {newId}");
Check("新网关的指纹 = 磁盘上的", newId == onDisk, $"期望 {onDisk}，实际 {newId}");

// ── ⑤ 命脉 ──
Section("⑤ 命脉必须完好（8787，只读探测）");
Check("8787 还在跑", HasListener(8787));
var main = FindGateways(8787);
Check("8787 上的网关没被误碰", main.Count >= 1, $"found {main.Count}");
foreach (var m in main) Console.WriteLine($"        PID {m.Pid}（未触碰）");

// ── 收尾 ──
foreach (var (pid, _) in FindGateways(Port))
{
    try { Process.GetProcessById(pid).Kill(true); } catch { }
}
Cleanup(oldDir);

Console.WriteLine();
Console.WriteLine($"  {(fail == 0 ? "ALL GREEN" : "FAILED")} — {pass} 通过, {fail} 失败");
return fail == 0 ? 0 : 1;

// ══════════════════════════════════════════════════════════════
// 本地辅助（产品的 ProcessReaper 引 WPF，这里复制一份判据来验证思路）
// ══════════════════════════════════════════════════════════════

static bool HasListener(int port)
{
    try
    {
        return IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Any(e => e.Port == port);
    }
    catch { return false; }
}

static string QueryCommandLine(int pid)
{
    try
    {
        using var s = new ManagementObjectSearcher(
            $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
        foreach (ManagementBaseObject item in s.Get())
        {
            using (item) { return item["CommandLine"]?.ToString() ?? ""; }
        }
    }
    catch { }
    return "";
}

/// <summary>三重匹配：node + bin.js + serve + --port N。产品的 ProcessReaper 用的是同一套。</summary>
static List<(int Pid, string Cmd)> FindGateways(int port)
{
    var r = new List<(int, string)>();
    var tokens = new[] { $"--port {port}", $"--port={port}" };

    foreach (var p in Process.GetProcessesByName("node"))
    {
        int pid = p.Id;
        p.Dispose();

        var cmd = QueryCommandLine(pid);
        if (cmd.Length == 0) continue;
        if (!cmd.Contains("bin.js", StringComparison.OrdinalIgnoreCase)) continue;
        if (!cmd.Contains("serve", StringComparison.OrdinalIgnoreCase)) continue;
        if (!tokens.Any(t => cmd.Contains(t, StringComparison.Ordinal))) continue;

        r.Add((pid, cmd));
    }
    return r;
}

/// <summary>和产品、和网关 src/build-id.js 三方一致的指纹算法。</summary>
static string ComputeFingerprint(string apiDirectory)
{
    try
    {
        var dir = Path.Combine(apiDirectory, "src");
        if (!Directory.Exists(dir)) return "";

        var files = Directory.GetFiles(dir, "*.js")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var parts = new List<string>();
        foreach (var name in files)
        {
            var info = new FileInfo(Path.Combine(dir, name));
            var ms = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            parts.Add($"{name}:{info.Length}:{ms}");
        }
        if (parts.Count == 0) return "";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return Convert.ToHexString(hash).ToLowerInvariant()[..12];
    }
    catch { return ""; }
}

static async Task<(bool NoId, string BuildId, DateTime? Started)> ProbeHealth(int port)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
        var body = await http.GetStringAsync($"http://127.0.0.1:{port}/healthz");
        if (!body.Contains("\"ok\"") || !body.Contains("workbuddy", StringComparison.OrdinalIgnoreCase))
            return (false, "", null);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var id = root.TryGetProperty("buildId", out var b) ? b.GetString() ?? "" : "";
        DateTime? started = root.TryGetProperty("startedAt", out var s) && s.TryGetDateTime(out var t)
            ? t.ToLocalTime()
            : null;
        return (id.Length == 0, id, started);
    }
    catch { return (false, "", null); }
}

/// <summary>产品源码目录 —— 从测试程序往上找到仓库根。</summary>
static string ResolveApiDirectory()
{
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 10 && dir is not null; i++)
    {
        var candidate = Path.Combine(dir, "workbuddy_api");
        if (Directory.Exists(Path.Combine(candidate, "src"))) return candidate;
        dir = Path.GetDirectoryName(dir);
    }
    return Path.Combine(@"D:\workfandaiapi", "workbuddy_api");
}

/// <summary>
/// 把产品源码拷一份、加标记文件让指纹不同，然后起一个网关。
/// 这就是「旧代码在跑」的状态。
/// </summary>
static bool PrepareOldGateway(string apiDir, string oldDir, int port)
{
    try
    {
        if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true);
        Directory.CreateDirectory(oldDir);

        CopyDir(Path.Combine(apiDir, "src"), Path.Combine(oldDir, "src"));

        var pkg = Path.Combine(apiDir, "package.json");
        if (File.Exists(pkg)) File.Copy(pkg, Path.Combine(oldDir, "package.json"));

        // 加个文件改变指纹 —— 让它成为「和磁盘上的产品代码不一样」的版本
        File.WriteAllText(
            Path.Combine(oldDir, "src", "zz-reaper-test-old.js"),
            "// old build marker\n");

        var psi = new ProcessStartInfo
        {
            FileName = ResolveNode(),
            Arguments = $"src/bin.js serve --port {port} --api-key tk",
            WorkingDirectory = oldDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi);

        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(500);
            if (HasListener(port)) return true;
        }
        return false;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"        prepare failed: {ex.Message}");
        return false;
    }
}

/// <summary>优先用产品设置里那个 node，找不到退回 PATH。</summary>
static string ResolveNode()
{
    var configured = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkBuddyPanel", "settings.json");

    if (File.Exists(configured))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configured));
            if (doc.RootElement.TryGetProperty("NodePath", out var n)
                && n.GetString() is { Length: > 0 } path
                && File.Exists(path))
            {
                return path;
            }
        }
        catch { }
    }
    return "node";
}

static void CopyDir(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var f in Directory.GetFiles(from))
        File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    foreach (var d in Directory.GetDirectories(from))
        CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
}

static void Cleanup(string oldDir)
{
    try { if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true); } catch { }
}
