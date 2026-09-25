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

    /// <summary>授权文件名。放在 exe 同目录。</summary>
    public const string LicenseFileName = "license.lic";

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

    /// <summary>授权文件的完整路径（exe 同目录）。</summary>
    public static string LicensePath =>
        Path.Combine(AppContext.BaseDirectory, LicenseFileName);

    /// <summary>
    /// 校验。
    ///
    /// 这个方法**不抛异常**，所有失败都通过返回值表达 ——
    /// 调用方（启动流程）永远能拿到一句能显示给用户的话。
    /// </summary>
    public static Result Verify()
    {
        var path = LicensePath;

        if (!File.Exists(path))
        {
            return new Result(false, Reason.NoLicenseFile,
                $"没有找到授权文件。\n\n应该放在：\n{path}\n\n" +
                $"你的机器码是：\n{MachineId.Current}\n\n" +
                "把这个机器码发给作者，会收到一个 license.lic。",
                null);
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
