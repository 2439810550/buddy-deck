using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuddyDeck.Core;

/// <summary>
/// 一份上游凭证。
///
/// 这是整个系统里最敏感的东西 —— 拿到它就等于拿到了那个 WorkBuddy 账号。
/// 所以：
///   · 它的 ToString 不输出 token（见下面的重写）
///   · 日志里一律用它算出来的 <see cref="AccountTag"/>，不打印原值
/// </summary>
public sealed record Credential
{
    [JsonPropertyName("accessToken")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; init; } = "";

    /// <summary>
    /// 过期时刻（Unix 毫秒）。0 表示上游没给或给了个读不出来的值。
    ///
    /// 为什么用毫秒而不是 DateTime：上游两种单位都用过（秒和毫秒），
    /// 归一成 UTC 毫秒是唯一不会误解的形式。转换在 <see cref="Expiry.Normalize"/> 里做。
    /// </summary>
    [JsonPropertyName("expiresAtMs")]
    public long ExpiresAtMs { get; init; }

    [JsonPropertyName("uid")]
    public string Uid { get; init; } = "";

    /// <summary>企业账号才有的租户标识。个人账号是 null。</summary>
    [JsonPropertyName("enterpriseId")]
    public string? EnterpriseId { get; init; }

    /// <summary>界面上显示的名字（从凭证里读到的昵称/域名）。</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    /// <summary>
    /// 登录域（如 workbuddy.cn）。
    ///
    /// 出站请求要带成 `X-Domain`，为空时改成 `X-No-Department-Info: 1`。
    /// 上游靠这个判断走国内还是国际链路 —— 少了它会打错区域。
    /// </summary>
    [JsonPropertyName("domain")]
    public string Domain { get; init; } = "";

    /// <summary>
    /// 账号标识，用于日志和限流追踪。
    ///
    /// 格式和旧版网关保持一致（`uid:enterpriseId`），因为限流冷却的键
    /// 就是按它算的 —— 换了格式会让旧账本对不上。
    /// </summary>
    [JsonIgnore]
    public string AccountTag => $"{Uid}:{EnterpriseId ?? ""}";

    /// <summary>这个凭证还能用多久。没给过期时间时返回 null。</summary>
    [JsonIgnore]
    public TimeSpan? Remaining =>
        ExpiresAtMs <= 0 ? null : TimeSpan.FromMilliseconds(ExpiresAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>
    /// 故意不输出 token。
    ///
    /// 为什么重写：这个 record 会出现在异常消息、调试输出、日志里。
    /// 默认的 ToString 会把 accessToken 原样打出来 —— 那就是凭证泄露。
    /// </summary>
    public override string ToString() => $"Credential({AccountTag}, {Label})";
}

/// <summary>过期时间的归一化。上游在秒和毫秒之间摇摆，这里统一成毫秒。</summary>
public static class Expiry
{
    /// <summary>
    /// 这个阈值以上当作毫秒，以下当作秒。
    ///
    /// 怎么定出来的：约公元 120000 年的毫秒数。任何合理的「秒」时间戳
    /// （就算是 2099 年也才 4e9）都远小于它，而毫秒时间戳（2026 年约 1.7e12）
    /// 远大于它。所以量级判断不会错。
    /// </summary>
    private const long MillisecondThreshold = 0xe8d4a51000;

    public static long Normalize(long value)
        => value <= 0 ? 0 : value > MillisecondThreshold ? value : value * 1000;
}
