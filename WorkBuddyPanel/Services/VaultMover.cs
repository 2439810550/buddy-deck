using System.IO;
using System.Text.Json;

namespace WorkBuddyPanel.Services;

/// <summary>
/// 账号在池之间搬移的纯逻辑。
///
/// 为什么单独拎出来：编译进 WPF 程序的话，测它就得起一个 GUI 宿主 ——
/// 于是「搬号」这个功能一直没被测过。抽成纯文件操作之后可以直接跑单元测试。
///
/// 保险库是明文 JSON（0600 权限，里面是 accessToken / refreshToken），
/// 所以搬一个号不需要走 export/import 那套口令加密 —— 那两个命令是拿
/// **整个库**打包的（一个 AES 块，没法按账号拆）。
/// </summary>
public static class VaultMover
{
    /// <summary>搬完之后的结果描述，给界面显示用。</summary>
    public sealed record MoveResult(string AccountId, string DisplayName, bool WasActive, bool AlreadyExisted);

    /// <summary>
    /// 把一个账号从 <paramref name="srcPath"/> 搬到 <paramref name="dstPath"/>。
    ///
    /// 语义（都是刻意选的，测过）：
    ///   · 源库里删掉那条记录；如果它正是 activeId，activeId 清空
    ///     （源池退回「用桌面 App 当前登录的号」，而不是留一个指向不存在账号的悬空 id）
    ///   · 目标库里同 id 已存在 → 覆盖那一条，不叠第二份
    ///   · 目标库原本没有 activeId → 把这个号设为 active
    ///   · 目标库已经有 activeId → 不动它（搬进来的号只是备用）
    ///
    /// @throws FileNotFoundException 源库不存在
    /// @throws InvalidOperationException 源库里没有这个账号 / 源目标同一个
    /// </summary>
    public static MoveResult Move(string srcPath, string dstPath, string accountId)
    {
        if (string.Equals(
                Path.GetFullPath(srcPath), Path.GetFullPath(dstPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("源池和目标池是同一个。");

        if (!File.Exists(srcPath))
            throw new FileNotFoundException("源池里还没有账号文件。", srcPath);

        var wanted = accountId.TrimEnd(':');
        var src = ReadVault(srcPath);

        var index = src.Accounts.FindIndex(a => SameId(a, wanted));
        if (index < 0)
            throw new InvalidOperationException(
                $"源池里找不到 id 以 {wanted[..Math.Min(8, wanted.Length)]} 开头的账号。");

        var record = src.Accounts[index];
        var movedId = IdOf(record);
        var wasActive = src.ActiveId.TrimEnd(':')
            .Equals(wanted, StringComparison.OrdinalIgnoreCase);

        src.Accounts.RemoveAt(index);
        if (wasActive) src.ActiveId = "";

        Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
        var dst = File.Exists(dstPath) ? ReadVault(dstPath) : new Vault();
        var dstHadActive = !string.IsNullOrEmpty(dst.ActiveId);

        var existing = dst.Accounts.FindIndex(a => SameId(a, movedId));
        var alreadyExisted = existing >= 0;
        if (alreadyExisted) dst.Accounts[existing] = record;
        else dst.Accounts.Add(record);

        if (!dstHadActive) dst.ActiveId = movedId;

        // 先写目标再删源 —— 反过来的话，写目标失败就会把号弄丢
        WriteVault(dstPath, dst);
        WriteVault(srcPath, src);

        return new MoveResult(movedId, "", wasActive, alreadyExisted);
    }

    /// <summary>库存里的账号（只读用途）。</summary>
    public static List<string> ListIds(string path)
    {
        if (!File.Exists(path)) return new List<string>();
        return ReadVault(path).Accounts.Select(IdOf).ToList();
    }

    /// <summary>当前选中的账号 id（空串 = 没选）。</summary>
    public static string ActiveId(string path)
        => File.Exists(path) ? ReadVault(path).ActiveId : "";

    /// <summary>账号条数。</summary>
    public static int Count(string path)
        => File.Exists(path) ? ReadVault(path).Accounts.Count : 0;

    // ── 内部 ────────────────────────────────────────────────────

    private static bool SameId(JsonElement account, string id)
    {
        if (account.ValueKind != JsonValueKind.Object) return false;
        if (!account.TryGetProperty("id", out var prop)) return false;
        if (prop.ValueKind != JsonValueKind.String) return false;
        return (prop.GetString() ?? "").TrimEnd(':')
            .Equals(id.TrimEnd(':'), StringComparison.OrdinalIgnoreCase);
    }

    private static string IdOf(JsonElement account)
        => account.ValueKind == JsonValueKind.Object &&
           account.TryGetProperty("id", out var p) &&
           p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : "";

    internal sealed class Vault
    {
        // 属性名必须和 ReadVault 里手工找的键名一致 —— 都是小写 camelCase。
        //
        // 这曾经是个真 bug：Vault 是 C# 类，JsonSerializer 默认写成 PascalCase
        // （Version / ActiveId / Accounts），而 ReadVault 手工 TryGetProperty("version")
        // 找的是小写 —— 于是写出去的文件永远读不回来。
        //
        // 症状极危险：搬号不报错、文件也写出来了、内容看着完全正常，
        // 但下次读回来「一个账号都没有」—— 对用户而言就是号凭空消失。
        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [System.Text.Json.Serialization.JsonPropertyName("activeId")]
        public string ActiveId { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("accounts")]
        public List<JsonElement> Accounts { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static Vault ReadVault(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var v = new Vault();
        if (root.TryGetProperty("version", out var ver) && ver.TryGetInt32(out var vi))
            v.Version = vi;
        if (root.TryGetProperty("activeId", out var act) && act.ValueKind == JsonValueKind.String)
            v.ActiveId = act.GetString() ?? "";
        if (root.TryGetProperty("accounts", out var accs) && accs.ValueKind == JsonValueKind.Array)
        {
            // Clone：JsonDocument 释放后 JsonElement 就失效
            foreach (var a in accs.EnumerateArray()) v.Accounts.Add(a.Clone());
        }
        return v;
    }

    internal static void WriteVault(string path, Vault vault)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(vault, Json));
        File.Move(tmp, path, overwrite: true);

        try
        {
            var fi = new FileInfo(path);
            var sec = fi.GetAccessControl();
            sec.SetAccessRuleProtection(true, false);
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                me, System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            fi.SetAccessControl(sec);
        }
        catch
        {
            // 设不了 ACL 不影响功能
        }
    }
}
