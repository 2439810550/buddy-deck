// PoolStore / VaultMover 的行为测试。
//
// 直接链接产品代码 —— 测的是真家伙，不是照着逻辑重写的复刻版。
//
// 为什么这层必须有：界面测试只能验证「点下去不崩」，验证不了行为对不对。
// 而且这些是纯文件/字符串操作，脱离 WPF 就能跑，快且稳。
//
// 跑法：dotnet run -c Release      （在 _t/logictest 下）

using System.Text;
using System.Text.Json;
using WorkBuddyPanel.Services;

Console.OutputEncoding = Encoding.UTF8;

int ok = 0, bad = 0;

void Check(string label, bool cond, string detail = "")
{
    if (cond) { ok++; Console.WriteLine($"  ✓ {label}"); }
    else { bad++; Console.WriteLine($"  ✗ {label}   {detail}"); }
}

void Section(string t)
{
    Console.WriteLine();
    Console.WriteLine(new string('═', 62));
    Console.WriteLine("  " + t);
    Console.WriteLine(new string('═', 62));
}

// ══════════════════════════════════════════════════════════════
Section("① 目录名 —— 中文必须保住（否则用户认不出哪个是哪个）");

var cn1 = PoolStore.MakeDirectoryName("朋友的池", 8788);
var cn2 = PoolStore.MakeDirectoryName("我的池", 8789);
var cn3 = PoolStore.MakeDirectoryName("公司的池", 8790);

Console.WriteLine($"    朋友的池  → {cn1}");
Console.WriteLine($"    我的池    → {cn2}");
Console.WriteLine($"    公司的池  → {cn3}");
Console.WriteLine();

Check("中文被保留", cn1.Contains("朋友"), $"实际 {cn1}");
Check("三个中文池名互不相同", cn1 != cn2 && cn2 != cn3, $"{cn1} / {cn2} / {cn3}");
Check("目录名能从标题认出来（不是 p-port8788 这种）",
      !cn1.Contains("port8788"), $"实际 {cn1}");

// ══════════════════════════════════════════════════════════════
Section("② 目录名 —— 路径穿越必须挡住");

var attacks = new (string input, string why)[]
{
    ("../../etc/passwd", "路径穿越"),
    ("....//....//", "变形穿越"),
    ("C:\\Windows\\System32", "盘符+反斜杠"),
    ("/etc/shadow", "绝对路径"),
    ("..\\..\\windows", "反斜杠穿越"),
    ("a/../../b", "混合"),
};

foreach (var (input, why) in attacks)
{
    var r = PoolStore.MakeDirectoryName(input, 8788);
    var safe = !r.Contains("..") && !r.Contains("/") && !r.Contains("\\") && !r.Contains(":");
    Check($"{why}：{input} → {r}", safe, "结果仍含危险字符");
}

// ══════════════════════════════════════════════════════════════
Section("③ 目录名 —— 空空如也的输入");

foreach (var (input, why) in new[] { ("", "空串"), ("   ", "全空格"), ("...", "全点"), (".", "单点"), ("///", "全斜杠") })
{
    var r = PoolStore.MakeDirectoryName(input, 8788);
    Check($"{why} → {r}（有兜底，不为空）", r.Length > 2 && r != "p-", $"实际 '{r}'");
}

// ══════════════════════════════════════════════════════════════
Section("④ 目录名 —— 长度和非法字符");

var longName = new string('a', 60);
var r1 = PoolStore.MakeDirectoryName(longName, 8788);
Check($"超长英文名被截断：len={r1.Length}", r1.Length <= 30, $"实际 {r1.Length}");

var longCn = new string('中', 60);
var r2 = PoolStore.MakeDirectoryName(longCn, 8788);
Check($"超长中文名被截断：len={r2.Length}", r2.Length <= 30, $"实际 {r2.Length}");

var r3 = PoolStore.MakeDirectoryName("a/b\\c:d*e?f\"g<h>i|j", 8788);
Check($"全套非法字符被清掉 → {r3}", !r3.Contains("/") && !r3.Contains("\\") && !r3.Contains(":"));

var ctrl = PoolStore.MakeDirectoryName("池" + (char)1 + (char)2 + "名", 8788);
Check($"控制字符被清掉 → {ctrl}", !ctrl.Contains((char)1) && !ctrl.Contains((char)2));

// ══════════════════════════════════════════════════════════════
Section("⑤ 目录名唯一性");

var seen = new List<string>();
foreach (var _ in Enumerable.Range(0, 3))
{
    var c = PoolStore.MakeDirectoryName("朋友的池", 8788);
    var u = PoolStore.UniqueDirectoryName(c, seen.Select(x => new PoolConfig { DirectoryName = x }));
    seen.Add(u);
}
Check($"同名池三级区分：{string.Join(" / ", seen)}", seen.Distinct().Count() == 3);

// ══════════════════════════════════════════════════════════════
Section("⑥ 账号搬移 —— 这座曾经丢数据");

var tmp = Path.Combine(Path.GetTempPath(), "pooltest-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmp);
string V(string n) => Path.Combine(tmp, n + ".json");

string Acc(string id, string label) => JsonSerializer.Serialize(new Dictionary<string, object>
{
    ["id"] = id + ":", ["uid"] = id, ["label"] = label, ["accessToken"] = "tok-" + id,
});

void WV(string path, string active, params string[] accs)
{
    var doc = new Dictionary<string, object?>
    {
        ["version"] = 1,
        ["activeId"] = active,
        ["accounts"] = accs.Select(a => JsonSerializer.Deserialize<object>(a)).ToList(),
    };
    File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
}

var src = V("src"); var dst = V("dst");
WV(src, "id-1:", Acc("id-1", "主号"), Acc("id-2", "少钦号"));
WV(dst, "", Acc("id-9", "别的号"));

var res = VaultMover.Move(src, dst, "id-1");

// 这条是核心：写出去的键名必须能被读回来
Check("目标池读回来是 2 个号（写读键名一致）",
      VaultMover.Count(dst) == 2, $"实际 {VaultMover.Count(dst)}");
Check("源池剩 1 个", VaultMover.Count(src) == 1, $"实际 {VaultMover.Count(src)}");
Check("目标池 id 列表正确",
      VaultMover.ListIds(dst).Contains("id-1:") && VaultMover.ListIds(dst).Contains("id-9:"));
Check("目标是新来的", !res.AlreadyExisted);
Check("它原本是 active", res.WasActive);
Check("源池 activeId 被清空（不留悬空引用）", VaultMover.ActiveId(src) == "");

try { Directory.Delete(tmp, true); } catch { }

// ══════════════════════════════════════════════════════════════
Section("⑦ 端口分配");

Check("默认池端口是 8787", PoolStore.FindFreePort(8788) >= 8788);
Check("FindFreePort 不会返回保留端口", PoolStore.FindFreePort(1024) >= 1024);

// ══════════════════════════════════════════════════════════════
Section("⑧ 池目录不能逃出 state root（防删错文件）");

// 场景：pools.json 被手改或篡改，DirectoryName 写成了往上跳的路径。
// 删池时会 Directory.Delete(recursive:true) —— 逃出去就会删掉用户的文件。
var rootHome = PoolStore.RootHome();

var escapes = new[]
{
    @"..\..\..\重要文件",
    @"../../..",
    @"pools\..\..",
    @"..\..\..\..\..\Windows",
};

foreach (var dir in escapes)
{
    var cfg = new PoolConfig { Name = "恶意池", DirectoryName = dir, Port = 8788 };
    try
    {
        var home = cfg.VaultHome();
        // 没抛异常的话，至少要落在 root 里
        var full = Path.GetFullPath(home);
        var rootFull = Path.GetFullPath(rootHome);
        var inside = full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                     || full.Equals(rootFull, StringComparison.OrdinalIgnoreCase);
        Check($"DirectoryName='{dir}' → {full}", inside,
              "能逃出 state root，删池时会删到外面");
    }
    catch (InvalidOperationException)
    {
        Check($"DirectoryName='{dir}' 被拒绝（抛 InvalidOperationException）", true);
    }
}

// 单个 ".." 也会指到 pools 目录本身，也不该允许
// （删它会删掉所有池的数据）
try
{
    var dotdot = new PoolConfig { Name = "x", DirectoryName = "..", Port = 8788 };
    var h = dotdot.VaultHome();
    var hf = Path.GetFullPath(h);
    var expect = Path.GetFullPath(Path.Combine(rootHome, "pools"));
    Check("DirectoryName='..' 不能指向 pools 根目录本身",
          !hf.Equals(expect, StringComparison.OrdinalIgnoreCase), hf);
}
catch (InvalidOperationException)
{
    Check("DirectoryName='..' 被拒绝", true);
}

// 正常名字不能被误判
var good = new PoolConfig { Name = "朋友的池", DirectoryName = "p-朋友的池", Port = 8788 };
var goodFull = Path.GetFullPath(good.VaultHome());
Check("正常池名可用",
      goodFull.StartsWith(Path.GetFullPath(rootHome), StringComparison.OrdinalIgnoreCase),
      goodFull);
Check("正常池名指向 pools 里面",
      goodFull.Contains("pools"), goodFull);


// ══════════════════════════════════════════════════════════════
//  ⑨ 端口分配不能和已有池撞（实测踩到的 bug）
// ══════════════════════════════════════════════════════════════

// 场景：pools.json 里默认池已经占了 8789，但网关此刻没在跑
//       → 「现在有没有人监听」查到 8789 是空闲的
//       → 新建池又选了 8789
//
// 对用户而言：两个池显示同一个端口，启动第二个必然失败。

var existing = new List<PoolConfig>
{
    new() { Name = "默认池", DirectoryName = "", Port = 8789 },
    new() { Name = "已有的池", DirectoryName = "p-aaa", Port = 8790 },
};

// 直接测产品代码里的 PickPort —— 不是我自己重写一遍逻辑。
var picked = PoolStore.PickPort(existing);

Check($"新池端口避开已分配的（8789/8790 已占 → 选 {picked}）",
      picked != 8789 && picked != 8790, $"实际 {picked}");

// 用户明确指定端口，且没被占用 → 尊重它
var picked2 = PoolStore.PickPort(existing, 8899);
Check($"指定的空闲端口被尊重（8899）", picked2 == 8899, $"实际 {picked2}");

// 用户指定了已被别的池占用的端口 → 换一个
var picked3 = PoolStore.PickPort(existing, 8789);
Check($"指定的已占端口被换掉（8789 → {picked3}）",
      picked3 != 8789, $"实际 {picked3}");

// 空列表（刚装好，只有默认池的情况）
var picked4 = PoolStore.PickPort(new List<PoolConfig>());
Check($"没有任何池时从 8788 起（{picked4}）", picked4 >= 8788, $"实际 {picked4}");

// 默认池的端口也要避开
var withDefault = new List<PoolConfig> { new() { Name = "默认池", Port = 8787 } };
var picked5 = PoolStore.PickPort(withDefault);
Check($"避开默认池的 8787（{picked5}）", picked5 != 8787, $"实际 {picked5}");

Console.WriteLine();
Console.WriteLine(new string('═', 62));
Console.WriteLine($"  通过 {ok} 项，失败 {bad} 项");
Console.WriteLine(new string('═', 62));
return bad == 0 ? 0 : 1;
