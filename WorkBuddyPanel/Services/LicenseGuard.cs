using System.IO;
using System.Security.Cryptography;
using System.Text;
using Standard.Licensing;
using Standard.Licensing.Validation;

namespace WorkBuddyPanel.Services;

/// <summary>
/// 授权校验。
///
/// ## 方案
///
/// 用 `Standard.Licensing`（现成库，240 万下载量，专为这个场景设计）：
///
///     发卡方持**私钥** → 签出 license.lic（XML + ECDSA 签名）
///     程序里嵌**公钥** → 只能验签，提取不出私钥
///
/// 授权文件长这样（500 多字节，能直接发微信）：
///
///     &lt;License&gt;
///       &lt;Expiration&gt;Sat, 25 Sep 2027 00:00:00 GMT&lt;/Expiration&gt;
///       &lt;ProductFeatures&gt;
///         &lt;Feature name="MachineId"&gt;A1B2C3D4E5F60718&lt;/Feature&gt;
///       &lt;/ProductFeatures&gt;
///       &lt;Customer&gt;&lt;Name&gt;张三&lt;/Name&gt;&lt;/Customer&gt;
///       &lt;Signature&gt;MEUCID...&lt;/Signature&gt;
///     &lt;/License&gt;
///
/// ## 能防住什么
///
/// ✓ 改机器码 → 签名失效
/// ✓ 改到期时间 → 签名失效
/// ✓ 用别人的授权文件 → 机器码对不上
/// ✓ 自己伪造一个授权 → 没有私钥签不出来
/// ✗ 反编译 exe 把校验改成 return true —— **防不住**
///
/// 最后那条是 .NET 的固有限制（IL 可反编译），要靠混淆才能提高门槛。
/// 对"想用 AI 工具的人"这个用户群，签名足够。
///
/// @see https://github.com/junian/Standard.Licensing
/// </summary>
public static class LicenseGuard
{
    /// <summary>
    /// 公钥 —— 嵌在程序里，用来验签。
    ///
    /// 这是**公钥**，泄露无所谓（它的作用就是公开验证）。
    /// 对应的私钥在发卡工具里（tools/make-license），那个绝不能进仓库 ——
    /// 拿到了就能自己签授权，这个方案就废了。
    ///
    /// 生成方式：KeyGenerator.Create().GenerateKeyPair().ToPublicKeyString()
    /// （参 tools/make-license/Program.cs 里的 --gen 命令）
    /// </summary>
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEeTwhcrLx0JJrjh6GjExVsat43PR1H/pe9Y2pMmS4R3Cs+/" +
        "JXaBmtYALPgVJ9zxcFrTPsHcuviF+T0OEnl/Za3g==";

    /// <summary>
    /// 推荐的授权文件名。
    ///
    /// **不强制** —— 校验时会扫目录里所有 .lic 文件，逐个验签。
    /// 这个名字只是发卡工具的建议默认值，用户改成什么都能用。
    ///
    /// 为什么不写死：用户拿到 license-张三-A1B2C3D4.lic，
    /// 他凭什么知道要改名？改完名还要提心吊胆是不是改坏了。
    /// 而且他要是有多个授权（比如又给同事买了一个），只有一个名字反而麻烦。
    /// </summary>
    public const string LicenseFileName = "license.lic";

    /// <summary>
    /// 扫描 exe 同目录下的授权文件，返回**第一个验签通过的**。
    ///
    /// ## 匹配规则
    ///
    ///   1. 优先看 license.lic（如果有）
    ///   2. 然后看其它 *.lic，**按文件名排序**（保证结果稳定，不会这次认这个下次
    ///      认那个）
    ///   3. 逐个验签，第一个通过的就用它
    ///
    /// 非 .lic 后缀的文件不扫 —— 避免把用户的文档（恰好是 XML）读进来。
    /// 程序目录里一般不会有别的 .lic，但万一有，验签不过就跳过。
    ///
    /// ## 为什么按「验签通过」而不是「文件名对」
    ///
    /// 验签通过 = 这个文件确实是我们签发的、且没被改过。
    /// 这比文件名可靠得多 —— 文件名可以随便改，签名改不了。
    /// </summary>
    public static IEnumerable<string> CandidatePaths()
    {
        var dir = AppContext.BaseDirectory;

        // 1) 首选名字
        var preferred = Path.Combine(dir, LicenseFileName);
        if (File.Exists(preferred)) yield return preferred;

        // 2) 其它 .lic
        string[] others;
        try
        {
            others = Directory.GetFiles(dir, "*.lic", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        Array.Sort(others, StringComparer.OrdinalIgnoreCase);
        foreach (var f in others)
        {
            if (string.Equals(f, preferred, StringComparison.OrdinalIgnoreCase)) continue;
            yield return f;
        }
    }

    /// <summary>
    /// 推荐位置（界面上告知用户放哪、以及方便发卡工具知道丢哪）。
    ///
    /// ⚠ 校验**不依赖**这个路径 —— 它只是个建议。
    /// 真正用的是 {@link CandidatePaths} 扫出来的结果。
    /// </summary>
    public static string LicensePath =>
        Path.Combine(AppContext.BaseDirectory, LicenseFileName);

    /// <summary>机器码在授权文件里的字段名。</summary>
    public const string MachineIdFeature = "MachineId";

    /// <summary>校验结果。</summary>
    public sealed record Result(bool Ok, Reason FailReason, string Message, License? License)
    {
        public string CustomerName => License?.Customer?.Name ?? "";
        public DateTime? ExpiresAt => License?.Expiration;
        public string Edition => License?.ProductFeatures?.Get("Edition") ?? "standard";
    }

    public enum Reason
    {
        Ok,
        NoLicenseFile,
        Unreadable,
        BadSignature,
        Expired,
        MachineMismatch,
        BadFile,
    }

    /// <summary>
    /// 校验。
    ///
    /// 这个方法**不抛异常**，所有失败都通过返回值表达 ——
    /// 调用方（启动流程）永远能拿到一句能显示给用户的话。
    ///
    /// **会扫目录下所有 .lic 文件逐个验签**，不要求特定文件名。
    /// </summary>
    public static Result Verify()
    {
        var candidates = CandidatePaths().ToList();

        if (candidates.Count == 0)
        {
            return new Result(false, Reason.NoLicenseFile,
                "没有找到授权文件。\n\n" +
                "把收到的授权文件放到本程序所在目录" +
                "（也就是和 WorkBuddyPanel.exe 放一起）。\n" +
                "文件名不用改，什么名字都行。\n\n" +
                "你的机器码是：\n" + MachineId.Current + "\n\n" +
                "把这个机器码发给作者，会收到一个授权文件。",
                null);
        }

        // 记住验签失败的第一个文件的错误 —— 全都不过的话，
        // 拿它当提示比笼统说"没有授权文件"有用。
        Result? firstFailure = null;

        foreach (var path in candidates)
        {
            var result = VerifyFile(path);
            if (result.Ok) return result;
            firstFailure ??= result;
        }

        // 一个都没过：返回第一个失败的原因（通常是机器码不匹配或签名无效）
        return firstFailure!;
    }

    /// <summary>验一个具体的文件。</summary>
    private static Result VerifyFile(string path)
    {

        if (!File.Exists(path))
        {
            // 理论上不会走到这里（CandidatePaths 已经过滤了），
            // 但并发删除之类的边界情况留个兼容。
            return new Result(false, Reason.NoLicenseFile,
                "授权文件不见了：" + Path.GetFileName(path), null);
        }

        string xml;
        try
        {
            xml = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return new Result(false, Reason.Unreadable,
                $"授权文件读不出来：{ex.Message}", null);
        }

        License license;
        try
        {
            license = License.Load(xml);
        }
        catch (Exception ex)
        {
            return new Result(false, Reason.BadFile,
                $"授权文件格式不对：{ex.Message}\n\n" +
                "可能复制的时候不完整，重新拿一份试试。", null);
        }

        // 1) 签名 —— 先验这个，后面的检查才有意义
        //    签名过了说明文件是你签发的、且没被改过
        try
        {
            var failures = license.Validate()
                .Signature(PublicKey)
                .AssertValidLicense()
                .ToList();

            if (failures.Count > 0)
            {
                return new Result(false, Reason.BadSignature,
                    "授权文件的签名不对。\n\n" +
                    "这个文件要么不是本程序签发的，要么被改过。", null);
            }
        }
        catch (Exception ex)
        {
            return new Result(false, Reason.BadSignature,
                $"签名校验失败：{ex.Message}", null);
        }

        // 2) 机器码 —— 一台机器一个授权
        var bound = license.ProductFeatures?.Get(MachineIdFeature) ?? "";
        var current = MachineId.Current;

        if (bound == "")
        {
            return new Result(false, Reason.BadFile,
                "授权文件里没有机器码字段，可能不是给这个程序用的。", null);
        }

        if (!string.Equals(bound, current, StringComparison.OrdinalIgnoreCase))
        {
            return new Result(false, Reason.MachineMismatch,
                "这个授权是给另一台机器签发的。\n\n" +
                $"授权里的机器码：{bound}\n" +
                $"本机机器码：　　{current}\n\n" +
                "把本机机器码发给作者，换一份授权。", null);
        }

        // 3) 到期时间
        if (license.Expiration != default && license.Expiration < DateTime.Now)
        {
            return new Result(false, Reason.Expired,
                $"授权已于 {license.Expiration:yyyy-MM-dd} 到期。\n\n" +
                "续期请联系作者。", null);
        }

        return new Result(true, Reason.Ok, "", license);
    }
}
