using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace WorkBuddyPanel.Services;

/// <summary>
/// 机器码 —— 一台机器一个稳定标识。
///
/// ## 取什么
///
///     主板 UUID（Win32_ComputerSystemProduct.UUID）   ← 主要依据
///   + CPU ID（Win32_Processor.ProcessorId）           ← 辅助
///
/// 两个一起哈希。为什么不是单独一个：
///
///   主板 UUID 在部分主板/虚拟机里是空的或全 F —— 单独用它会有机器取不到值
///   CPU ID 在换 CPU 时会变 —— 单独用它用户升个级就要重新授权
///
/// **不用网卡 MAC**：虚拟网卡、Wi-Fi 开关、装 VPN 都会让它变，
/// 用户会莫名其妙被判定「换机器」。
///
/// ## 稳定性
///
/// 换主板 → 机器码变（合理，等于换了台机器）
/// 换 CPU  → 可能变（主板 UUID 还在的话不会变，因为我们做了兜底）
/// 重装系统 → 不变
/// 换硬盘  → 不变
/// </summary>
public static class MachineId
{
    private static string? _cached;

    /// <summary>
    /// 本机机器码。16 位大写十六进制，比如 `A1B2C3D4E5F60718`。
    ///
    /// 结果会缓存 —— 首次调用走 WMI（约 100~300ms），之后就是内存读取。
    /// 授权检查在启动路径上，不能每次都查 WMI。
    /// </summary>
    public static string Current => _cached ??= Compute();

    /// <summary>强制重新计算（诊断用）。</summary>
    public static string Recompute() => _cached = Compute();

    /// <summary>拿机器码的各个组成部分，界面上「为什么是这个码」要展示。</summary>
    public static IReadOnlyDictionary<string, string> Components()
    {
        var dict = new Dictionary<string, string>();
        dict["主板 UUID"] = Query("Win32_ComputerSystemProduct", "UUID");
        dict["CPU ID"] = Query("Win32_Processor", "ProcessorId");
        dict["机器名"] = Environment.MachineName;
        return dict;
    }

    private static string Compute()
    {
        var board = Query("Win32_ComputerSystemProduct", "UUID");
        var cpu = Query("Win32_Processor", "ProcessorId");

        // 主板 UUID 在虚拟机里常常是 "00000000-0000-0000-0000-000000000000"
        // 或者全 "F"。这种值当无效处理，只用 CPU。
        if (IsPlaceholder(board)) board = "";

        var material = $"{board}|{cpu}";

        // material 全是空的话（极端情况：WMI 不可用），退回机器名 ——
        // 至少能出个值，虽然稳定性差。总比抛异常让程序起不来强。
        if (material == "|") material = Environment.MachineName;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..16];
    }

    private static bool IsPlaceholder(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;

        var v = value.Replace("-", "").Replace(" ", "").ToUpperInvariant();

        // 全 0、全 F 都是「没有值」的常见写法
        if (v.All(c => c == '0')) return true;
        if (v.All(c => c == 'F')) return true;

        return false;
    }

    /// <summary>
    /// 查一个 WMI 属性。
    ///
    /// 单项失败返回空串而不是抛 —— 机器码是启动路径，
    /// 一个 WMI 查询挂掉不该让整个程序起不来。
    /// </summary>
    private static string Query(string wmiClass, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var value = item[property]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(value)) return value;
                }
            }
        }
        catch
        {
            // WMI 不可用（极少见，但虚拟机里偶发）
        }
        return "";
    }
}
