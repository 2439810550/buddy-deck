using System.Text;
using Standard.Licensing;
using Standard.Licensing.Security.Cryptography;

namespace MakeLicense;

/// <summary>
/// 发卡工具 —— 你自己用的，**不要发出去**。
///
/// 它持有私钥，能签出任何授权。私钥泄露 = 整套授权失效。
///
/// ## 用法
///
///     生成密钥对（只做一次）：
///         make-license --gen
///         → 输出 private.key / public.key
///         → public.key 贴进 Services/LicenseGuard.cs 的 PublicKey
///         → private.key 保存好（别进 git）
///
///     签一个授权：
///         make-license --issue <机器码> --to 张三 --days 365
///         make-license --issue <机器码> --to 李四 --until 2027-12-31
///         make-license --issue <机器码> --to 王五 --perpetual   永久
///
///     批量签（机器码一行一个的文本文件）：
///         make-license --batch machines.txt --days 365
///
/// 产出 license.lic，发给用户，让他放到 exe 同目录。
/// </summary>
public static class Program
{
    /// <summary>私钥文件的默认位置。</summary>
    private static readonly string KeyDir =
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "keys");

    private static string PrivateKeyPath => Path.Combine(Path.GetFullPath(KeyDir), "private.key");
    private static string PublicKeyPath => Path.Combine(Path.GetFullPath(KeyDir), "public.key");

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }

            return args[0] switch
            {
                "--gen" => GenerateKeys(),
                "--issue" => Issue(args),
                "--batch" => Batch(args),
                "--info" => Info(args),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"出错了：{ex.Message}");
            return 1;
        }
    }

    // ──────────────────────────────────────────────────────────
    //  生成密钥对
    // ──────────────────────────────────────────────────────────

    private static int GenerateKeys()
    {
        var dir = Path.GetDirectoryName(PrivateKeyPath)!;
        Directory.CreateDirectory(dir);

        if (File.Exists(PrivateKeyPath))
        {
            Console.WriteLine($"私钥已经存在：{PrivateKeyPath}");
            Console.WriteLine();
            Console.Write("覆盖它会作废所有已发出的授权。确定要重新生成吗？(输入 yes 继续) ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() != "yes")
            {
                Console.WriteLine("已取消。");
                return 0;
            }
        }

        Console.Write("给私钥设个口令（发卡时要输，忘了就签不了）：");
        var passphrase = ReadPassword();
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            Console.Error.WriteLine("口令不能为空。");
            return 1;
        }

        var pair = KeyGenerator.Create().GenerateKeyPair();

        File.WriteAllText(PrivateKeyPath, pair.ToEncryptedPrivateKeyString(passphrase));
        File.WriteAllText(PublicKeyPath, pair.ToPublicKeyString());

        Console.WriteLine();
        Console.WriteLine("生成完成。");
        Console.WriteLine($"  私钥  {PrivateKeyPath}    ← 保存好，别进 git，别发给任何人");
        Console.WriteLine($"  公钥  {PublicKeyPath}");
        Console.WriteLine();
        Console.WriteLine("下一步：把下面这段贴进 Services/LicenseGuard.cs 的 PublicKey 常量：");
        Console.WriteLine();
        Console.WriteLine($"    \"{pair.ToPublicKeyString()}\"");
        Console.WriteLine();

        return 0;
    }

    // ──────────────────────────────────────────────────────────
    //  签单个授权
    // ──────────────────────────────────────────────────────────

    private static int Issue(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：make-license --issue <机器码> [--to 名字] [--days 365 | --until 2027-12-31 | --perpetual] [--out 文件]");
            return 1;
        }

        var machineId = args[1].Trim().ToUpperInvariant();
        if (!IsPlausibleMachineId(machineId))
        {
            Console.Error.WriteLine($"机器码看起来不对：'{machineId}'");
            Console.Error.WriteLine("应该是 16 位十六进制（比如 A1B2C3D4E5F60718）。");
            return 1;
        }

        var to = GetOption(args, "--to") ?? "用户";
        var edition = GetOption(args, "--edition") ?? "pro";
        var outPath = GetOption(args, "--out") ?? $"license-{machineId}.lic";

        var (expiresAt, ok) = ResolveExpiry(args);
        // ⚠ expiresAt 为 null 是**合法**的（永久授权）。
        // 不能写成 `if (expiresAt is null) return 1` —— 那会让 --perpetual
        // 静默退出（退出码 1，零输出）。踩过。
        if (!ok) return 1;

        var passphrase = ReadPassphrase();
        if (passphrase is null) return 1;

        var xml = BuildLicense(machineId, to, edition, expiresAt, passphrase);
        if (xml is null) return 1;

        File.WriteAllText(outPath, xml, new UTF8Encoding(false));

        var periodText = expiresAt is null ? "永久" : $"{expiresAt:yyyy-MM-dd}";

        Console.WriteLine();
        Console.WriteLine($"✓ 已生成 {outPath}");
        Console.WriteLine($"    机器码  {machineId}");
        Console.WriteLine($"    授权给  {to}");
        Console.WriteLine($"    有效期  {periodText}");
        Console.WriteLine($"    版本    {edition}");
        Console.WriteLine();
        Console.WriteLine("把这个文件发给用户，让他放到 WorkBuddyPanel.exe 同目录，改名成 license.lic。");

        return 0;
    }

    // ──────────────────────────────────────────────────────────
    //  批量签
    // ──────────────────────────────────────────────────────────

    private static int Batch(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：make-license --batch <机器码列表.txt> [--days 365] [--outdir 目录]");
            return 1;
        }

        var listPath = args[1];
        if (!File.Exists(listPath))
        {
            Console.Error.WriteLine($"找不到文件：{listPath}");
            return 1;
        }

        var lines = File.ReadAllLines(listPath)
            .Select(l => l.Trim())
            .Where(l => l != "" && !l.StartsWith('#'))
            .ToList();

        if (lines.Count == 0)
        {
            Console.Error.WriteLine("列表是空的。");
            return 1;
        }

        var (expiresAt, ok) = ResolveExpiry(args);
        if (!ok) return 1;

        var outDir = GetOption(args, "--outdir") ?? "licenses";
        Directory.CreateDirectory(outDir);

        var periodText = expiresAt is null ? "永久" : $"{expiresAt:yyyy-MM-dd}";

        Console.WriteLine($"要签 {lines.Count} 个授权，有效期 {periodText}。");
        Console.Write("继续？(输入 yes) ");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() != "yes") return 0;

        var passphrase = ReadPassphrase();
        if (passphrase is null) return 1;

        var succeeded = 0;
        var failed = 0;

        foreach (var machineId in lines)
        {
            // 支持 "机器码 名字" 的形式
            var parts = machineId.Split([' ', '\t', ','], 2, StringSplitOptions.RemoveEmptyEntries);
            var id = parts[0].ToUpperInvariant();
            var name = parts.Length > 1 ? parts[1].Trim() : "用户";

            if (!IsPlausibleMachineId(id))
            {
                Console.WriteLine($"  跳过（机器码不对）：{machineId}");
                failed++;
                continue;
            }

            var xml = BuildLicense(id, name, "pro", expiresAt, passphrase);
            if (xml is null) { failed++; continue; }

            var path = Path.Combine(outDir, $"license-{id}.lic");
            File.WriteAllText(path, xml, new UTF8Encoding(false));
            Console.WriteLine($"  ✓ {id}  {name}");
            succeeded++;
        }

        Console.WriteLine();
        Console.WriteLine($"完成：{succeeded} 个成功，{failed} 个失败。文件在 {Path.GetFullPath(outDir)}");

        return failed > 0 ? 1 : 0;
    }

    // ──────────────────────────────────────────────────────────
    //  看一个授权文件的内容
    // ──────────────────────────────────────────────────────────

    private static int Info(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：make-license --info <license.lic>");
            return 1;
        }

        var path = args[1];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"找不到文件：{path}");
            return 1;
        }

        var license = License.Load(File.ReadAllText(path));

        Console.WriteLine($"  编号    {license.Id}");
        Console.WriteLine($"  授权给  {license.Customer?.Name}");
        Console.WriteLine($"  邮箱    {license.Customer?.Email}");
        Console.WriteLine($"  机器码  {license.ProductFeatures?.Get("MachineId")}");
        Console.WriteLine($"  版本    {license.ProductFeatures?.Get("Edition")}");
        Console.WriteLine($"  到期    {(license.Expiration == default ? "永久" : license.Expiration.ToString("yyyy-MM-dd"))}");

        if (license.Expiration != default && license.Expiration < DateTime.Now)
        {
            Console.WriteLine();
            Console.WriteLine("  ⚠ 已经过期");
        }

        return 0;
    }

    // ──────────────────────────────────────────────────────────
    //  内部
    // ──────────────────────────────────────────────────────────

    private static string? BuildLicense(
        string machineId, string to, string edition, DateTime? expiresAt, string passphrase)
    {
        if (!File.Exists(PrivateKeyPath))
        {
            Console.Error.WriteLine($"找不到私钥：{PrivateKeyPath}");
            Console.Error.WriteLine("先跑 make-license --gen 生成一对。");
            return null;
        }

        var builder = License.New()
            .WithUniqueIdentifier(Guid.NewGuid())
            .As(LicenseType.Standard)
            .WithMaximumUtilization(1)
            .WithProductFeatures(new Dictionary<string, string>
            {
                ["MachineId"] = machineId,
                ["Edition"] = edition,
                ["IssuedAt"] = DateTime.UtcNow.ToString("O"),
            })
            .LicensedTo(to, "");

        // 永久授权就不设 Expiration —— 库的默认 DateTime.MinValue 表示不过期
        if (expiresAt.HasValue) builder = builder.ExpiresAt(expiresAt.Value);

        try
        {
            var privateKey = File.ReadAllText(PrivateKeyPath);
            return builder.CreateAndSignWithPrivateKey(privateKey, passphrase).ToString();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"签名失败：{ex.Message}");
            Console.Error.WriteLine("多半是口令不对。");
            return null;
        }
    }

    /// <summary>
    /// 解析有效期参数。
    ///
    /// 返回 (到期时间, 参数是否合法)。
    ///
    /// **到期时间为 null 表示永久授权** —— 那是合法值，所以用第二个返回值表达
    /// 「参数有没有错」，不能拿 expiresAt 是不是 null 来判。
    /// 踩过：`--perpetual` 静默退出，退出码 1，什么也不输出。
    /// </summary>
    private static (DateTime? ExpiresAt, bool Ok) ResolveExpiry(string[] args)
    {
        if (args.Contains("--perpetual"))
            return (null, true);

        var until = GetOption(args, "--until");
        if (until is not null)
        {
            if (!DateTime.TryParse(until, out var d))
            {
                Console.Error.WriteLine($"日期格式不对：'{until}'，用 2027-12-31 这样的形式。");
                return (null, false);
            }
            return (d.Date.AddDays(1).AddSeconds(-1), true);
        }

        var daysText = GetOption(args, "--days") ?? "365";
        if (!int.TryParse(daysText, out var days) || days <= 0)
        {
            Console.Error.WriteLine($"天数不对：'{daysText}'");
            return (null, false);
        }

        return (DateTime.Now.AddDays(days), true);
    }

    private static bool IsPlausibleMachineId(string id) =>
        id.Length is >= 8 and <= 64 && id.All(c => char.IsAsciiHexDigit(c) || c == '-');

    private static string? GetOption(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>读私钥口令（不回显）。</summary>
    private static string? ReadPassphrase()
    {
        Console.Write("私钥口令：");
        var pass = ReadPassword();
        return string.IsNullOrEmpty(pass) ? null : pass;
    }

    /// <summary>
    /// 不回显地读一行。
    ///
    /// 口令不能显示在屏幕上 —— 录屏、截屏、旁边有人都会泄露。
    ///
    /// 但输入被重定向时（管道、脚本调用）拿不到 ReadKey，
    /// 那就退回普通 ReadLine —— 否则自动化发卡根本跑不了。
    /// </summary>
    private static string ReadPassword()
    {
        // Console.IsInputRedirected 为 true 时（管道 / 文件重定向），
        // ReadKey 会直接抛异常，必须换 ReadLine。
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? "";
        }

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        return sb.ToString();
    }

    private static int Unknown(string arg)
    {
        Console.Error.WriteLine($"不认识的参数：{arg}");
        Console.Error.WriteLine("试试 make-license --help");
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            make-license —— 授权发卡工具（自己用，不要发出去）

            生成密钥对（只做一次）：
                make-license --gen

            签一个授权：
                make-license --issue <机器码> [选项]

            选项：
                --to <名字>          授权给谁（会写进授权文件）
                --days <天数>        有效天数，默认 365
                --until <日期>       指定到期日，如 2027-12-31
                --perpetual          永久授权
                --edition <版本>     写进授权文件的版本标记，默认 pro
                --out <文件>         输出路径，默认 license-<机器码>.lic

            批量签（文本文件，一行一个机器码，可带名字）：
                make-license --batch machines.txt --days 365 --outdir licenses

            看一个授权文件：
                make-license --info license-xxx.lic

            私钥位置：
                keys/private.key （口令加密，绝不提交到 git）
            """);
    }
}
