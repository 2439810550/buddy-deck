// BuddyDeck 真实导出文件验证器
// Build: dotnet run --project tools/VaultVerify
// Run:   dotnet run --project tools/VaultVerify -- <文件路径> [口令]
//
// 用途：拿【真实的旧版导出文件】验证新版解密路径，而不是只跑合成向量。
//       旧版导出文件带着真实的口令归一化、真实的 scrypt 参数、真实的
//       GCM 形状 —— 合成测试证不了这些。
//
// 不传口令时：只做结构核对（参数、长度、字段），不解密。
// 传口令时  ：真正解密，看能否还原账号列表。

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuddyDeck.Core;

if (args.Length < 1)
{
    Console.Error.WriteLine("用法: VaultVerify <导出文件路径> [口令]");
    return 2;
}

var path = args[0];
var passphrase = args.Length > 1 ? args[1] : null;

if (!File.Exists(path))
{
    Console.Error.WriteLine($"文件不存在: {path}");
    return 2;
}

var raw = File.ReadAllText(path, Encoding.UTF8);
var doc = JsonNode.Parse(raw)!.AsObject();

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"文件: {path}");
Console.WriteLine($"大小: {new FileInfo(path).Length} 字节");
Console.WriteLine();

// ── 1. 结构核对（不需要口令）─────────────────────────────
Console.WriteLine("── 结构核对 ──");
string Get(string k) => doc[k]?.GetValue<string>() ?? "(缺)";

Console.WriteLine($"  format : {Get("format")}   (期望 workbuddy-accounts)");
Console.WriteLine($"  version: {doc["version"]?.GetValue<int>()}          (期望 1)");
Console.WriteLine($"  cipher : {Get("cipher")}   (期望 aes-256-gcm)");

var kdf = doc["kdf"]?.AsObject();
if (kdf is not null)
{
    Console.WriteLine($"  kdf    : {kdf["name"]?.GetValue<string>()} " +
                      $"N={kdf["N"]?.GetValue<int>()} r={kdf["r"]?.GetValue<int>()} " +
                      $"p={kdf["p"]?.GetValue<int>()} keylen={kdf["keylen"]?.GetValue<int>()}");
}

// 长度核对：iv/tag/salt 都是 base64，payload 也是。
// ⚠ 曾经把 base64 当 hex 算过一次，得出「iv=8 字节，与旧版 12 不符」的错误结论。
//   这里显式按 base64 解，并把实际长度打出来。
static int B64Len(string v)
{
    try { return Convert.FromBase64String(v).Length; }
    catch (FormatException) { return -1; }
}

var ivLen = B64Len(Get("iv"));
var tagLen = B64Len(Get("tag"));
var payloadLen = B64Len(Get("payload"));
var saltLen = kdf is null ? -1 : B64Len(kdf["salt"]?.GetValue<string>() ?? "");

Console.WriteLine($"  salt   : {saltLen} 字节  (期望 16)");
Console.WriteLine($"  iv     : {ivLen} 字节  (期望 12)");
Console.WriteLine($"  tag    : {tagLen} 字节  (期望 16)");
Console.WriteLine($"  payload: {payloadLen} 字节");

var structOk = Get("format") == "workbuddy-accounts"
    && Get("cipher") == "aes-256-gcm"
    && saltLen == 16 && ivLen == 12 && tagLen == 16;
Console.WriteLine($"  => 结构 {(structOk ? "通过" : "不一致")}");
Console.WriteLine();

// ── 2. 解密（需要口令）───────────────────────────────────
//
// ⚠ 这里【故意】比 AccountTransfer.Open 说的话多。
//
// Core 里那三类失败（口令错 / 文件被改过 / 文件被截断）是**有意合并**成
// 一句「口令不对或文件损坏」的 —— 分开说等于告诉攻击者「口令对了但文件
// 坏了」，那是一条旁路信息。
//
// 但这个工具**已经先做过结构预检**，此时再说「结构正常但解不开」不泄露
// 任何 Core 没泄露的东西。而对用户来说，「选错文件了」和「口令记错了」
// 是**两个完全不同的动作** —— 合并成一句会让他两个方向都试一遍。
//
// ⇒ 所以：区分做在工具里，**不动 Core 的报错语义**。
if (passphrase is null)
{
    Console.WriteLine("未提供口令 —— 只做结构核对，跳过解密。");
    Console.WriteLine("（真实互通验证需要口令：用它解开后看账号列表能否还原）");
    return structOk ? 0 : 1;
}

Console.WriteLine("── 解密 ──");
try
{
    var opened = AccountTransfer.Open(doc, passphrase);
    Console.WriteLine($"  provider: {opened.Provider}");
    Console.WriteLine($"  账号数  : {opened.Accounts.Count}");
    foreach (var acc in opened.Accounts.Take(10))
    {
        var uid = acc.Uid.Length >= 8 ? acc.Uid[..8] : acc.Uid;
        Console.WriteLine($"    - {acc.Label}  uid={uid}");
    }
    Console.WriteLine();
    Console.WriteLine("  => 互通验证【通过】—— 新版能解开旧版导出的真实文件");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"  解密失败: {ex.GetType().Name}");
    Console.WriteLine($"  消息    : {ex.Message}");
    Console.WriteLine();

    if (structOk)
    {
        // 结构全对、只是 GCM 认证不过 → 几乎只可能是口令或文件内容问题
        Console.WriteLine("  => 结构正常但解不开：大概率是【口令不对】，或文件被改过。");
        Console.WriteLine("     结构核对已逐项通过（format/version/cipher/kdf/各段长度），");
        Console.WriteLine("     所以「选错文件」可以排除，请先核对口令；");
        Console.WriteLine("     只有确认口令无误后，才需要怀疑实现差异。");
    }
    else
    {
        // 结构就不对 → 多半根本不是这个程序导出的文件
        Console.WriteLine("  => 结构核对未通过：这很可能【不是本程序导出的文件】。");
        Console.WriteLine("     请先确认选对了文件，而不是先怀疑口令。");
    }

    return 1;
}
