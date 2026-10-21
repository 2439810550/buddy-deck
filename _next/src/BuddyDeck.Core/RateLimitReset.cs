using System.Globalization;
using System.Text.RegularExpressions;

namespace BuddyDeck.Core;

/// <summary>
/// 从上游的限流文案里解析**重置时刻**。
///
/// ## 上游原文长什么样
///
/// 实测抓到的（旧版 `ratelimit.js:8` 记录）：
///
/// ```
/// HTTP 429  {"code":6004,
///   "msg":"您的使用量已超出频率限制，将在 2026-09-25 17:07:48 UTC+8 重置，
///          您也可以切换其他模型继续使用。"}
/// ```
///
/// 这一句话把三件事都说清楚了，所以它值得单独解析：
///
///   1. **限制是按「账号 + 模型」算的** —— 上游自己建议「切换其他模型继续使用」。
///      这就是「一个模型被限不要把整个号拉黑」的原始依据（HANDOVER 坑 4）。
///   2. **有明确的恢复时间** —— 解出来就能知道「什么时候恢复」，而不是盲等 5 分钟。
///   3. **这是周期性配额，不是封号** —— 所以「换个模型继续」是对的应对。
///
/// ## 为什么必须解析而不是固定冷却
///
/// 固定 5 分钟有两个方向都错：
///   · 上游说 30 秒后恢复 → 白等 4 分半（可用容量平白少一截）
///   · 上游说 2 小时后恢复 → 5 分钟后又来撞，白撞一次（还污染指标）
///
/// 解析出来后按上游给的真实时刻记冷却，这两个方向都对上。
///
/// ## 与旧版的对应关系
///
/// 逐条对齐 `ratelimit.js:51` 的 `parseRateLimitReset`（含正则与偏移换算），
/// 因为那是**唯一被真实账号验证过**的实现。
/// </summary>
public static class RateLimitReset
{
    /// <summary>
    /// 第一种格式：`2026-09-25 17:07:48 UTC+8`（也认无偏移的 `UTC`）。
    ///
    /// 注意 `UTC` 后面的偏移**小时位可以只有一位**（`+8` 而不是 `+08`）——
    /// 上游实际就这么发的。
    /// </summary>
    private static readonly Regex UtcStyle = new(
        @"(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2}):(\d{2})\s*UTC([+-]\d{1,2})?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 第二种格式：`2026-09-25T17:07:48+08:00`（也认 `+0800`，冒号可选）。
    /// </summary>
    private static readonly Regex IsoStyle = new(
        @"(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2}):(\d{2})\s*([+-]\d{2}):?(\d{2})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>上游的限流专用业务码。同一账号不同模型可能给不同的码。</summary>
    private static readonly HashSet<int> RateLimitCodes = [6004, 6005, 6006];

    /// <summary>
    /// 从一段文案里挖出重置时刻。
    ///
    /// @param text 上游返回的原文（**可以是整个 JSON 字符串** —— 我们直接在原文里搜，
    ///             不做 JSON 解析。因为文案可能包在 `msg` / `message` / `displayMsg`
    ///             等不同字段里，还可能是非 JSON 的纯文本）。
    /// @returns 重置时刻（Unix 毫秒）；**解析不出来时返回 null**（不是抛异常，也不是 0）。
    ///          返回 null 的语义是「上游没说」，调用方该退回默认冷却 ——
    ///          而不是「立刻可以重试」。
    /// </summary>
    public static long? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        // ⚠ 先试 UTC 式（更具体），再试 ISO 式。
        // 反过来的话：ISO 式的前 6 组在 `... UTC+8` 上也匹配得上，
        // 但它会把 `UTC` 当成别的东西、偏移算错。
        var match = UtcStyle.Match(text);
        if (match.Success) return FromMatch(match, offsetGroup: 7, minuteGroup: 0);

        match = IsoStyle.Match(text);
        if (match.Success) return FromMatch(match, offsetGroup: 7, minuteGroup: 8);

        return null;
    }

    /// <summary>
    /// 把正则的各组换算成 Unix 毫秒。
    ///
    /// ## 时区换算为什么这么绕
    ///
    /// 两种格式的偏移表达不同：
    /// ```
    /// "17:07:48 UTC+8"      → 小时位 = "+8"，没有分钟位
    /// "17:07:48+08:00"      → 小时位 = "+08"，分钟位 = "00"
    /// "17:07:48 UTC"        → 整个偏移组都没匹配上 → 视为 UTC+0
    /// ```
    /// 所以：偏移量 = 小时×60 + 分钟×60（分钟要带上小时的符号，
    /// 否则 `-08:30` 会算成 `-8h + 30min` 而不是 `-(8h30m)`）。
    ///
    /// 而文案里的时间**是本地时间**（带偏移的那个），
    /// 所以先当成 UTC 算出毫秒，再**减去**偏移量才是真正的 UTC 时刻。
    /// </summary>
    private static long? FromMatch(Match match, int offsetGroup, int minuteGroup)
    {
        if (!int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out var day) ||
            !int.TryParse(match.Groups[4].Value, CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(match.Groups[5].Value, CultureInfo.InvariantCulture, out var minute) ||
            !int.TryParse(match.Groups[6].Value, CultureInfo.InvariantCulture, out var second))
        {
            return null;
        }

        var offsetHours = 0;
        if (offsetGroup > 0 && offsetGroup < match.Groups.Count && match.Groups[offsetGroup].Success)
        {
            int.TryParse(match.Groups[offsetGroup].Value, CultureInfo.InvariantCulture, out offsetHours);
        }

        var offsetMinutes = 0;
        if (minuteGroup > 0 && minuteGroup < match.Groups.Count && match.Groups[minuteGroup].Success)
        {
            int.TryParse(match.Groups[minuteGroup].Value, CultureInfo.InvariantCulture, out offsetMinutes);
            // ⚠ 分钟要带上小时的符号：`-08:30` 是 -(8h30m)，不是 -8h+30m。
            // 旧版这里是 `offsetMinutes * Math.sign(offsetHours || 1)`，
            // 我改写成显式判断 —— `Math.sign` 在 offsetHours 为 0 时给 1，
            // 而 0 只可能出现在 `+00:30` 这种（那也该是正号），语义一致但更好读。
            offsetMinutes *= offsetHours < 0 ? -1 : 1;
        }

        // 范围校验：明显不合理的日期直接当解析失败，别把冷却记到几百年后。
        if (month is < 1 or > 12 || day is < 1 or > 31 ||
            hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 60 ||
            year is < 2000 or > 2200)
        {
            return null;
        }

        DateTimeOffset local;
        try
        {
            // 按「文案里的字面时刻 + 零偏移」构造 —— 它其实是个本地时间，
            // 下一步再减掉偏移得到 UTC。
            local = new DateTimeOffset(year, month, day, hour, minute, Math.Min(second, 59), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        var totalOffsetMinutes = offsetHours * 60 + offsetMinutes;
        return local.ToUnixTimeMilliseconds() - (long)totalOffsetMs(totalOffsetMinutes);
    }

    private static int totalOffsetMs(int minutes) => minutes * 60_000;

    /// <summary>
    /// 这条上游结果算不算「受限」。**不能只看状态码** ——
    /// 服务端也会把限流包在 400 里发过来（旧版 `ratelimit.js:76` 的实测结论）。
    ///
    /// 判据（依次）：
    ///   1. 分类已经是 rate_limit / model_rate_limit（`UpstreamClient` 已经分好了）
    ///   2. 状态码 429
    ///   3. 正文里出现 `code: 6004/6005/6006`
    ///   4. 正文里出现「超出频率限制」这类中文文案
    /// </summary>
    public static bool IsRateLimited(bool isRateLimitKind, int status, string? message)
    {
        if (isRateLimitKind) return true;
        if (status == 429) return true;
        if (string.IsNullOrEmpty(message)) return false;

        // 业务码：允许 `"code": 6004` / `code:6004` / 裸 `6004` 前后有引号各种写法
        foreach (var code in RateLimitCodes)
        {
            if (ContainsCode(message, code)) return true;
        }

        return message.Contains("超出频率限制", StringComparison.Ordinal)
               || message.Contains("使用量已超出", StringComparison.Ordinal);
    }

    /// <summary>`"code": 6004` 这类写法都认（冒号两侧可有空白、键可有引号）。</summary>
    private static bool ContainsCode(string message, int code)
    {
        var text = code.ToString(CultureInfo.InvariantCulture);

        // 找 `code` 关键字，再确认它后面跟着的就是这个码
        var index = 0;
        while ((index = message.IndexOf("code", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var after = message[(index + 4)..];
            var trimmed = after.TrimStart('"', '\'', ' ', '\t', ':', '"', '\'');

            if (trimmed.StartsWith(text, StringComparison.Ordinal))
            {
                return true;
            }

            index += 4;
        }

        return false;
    }

    /// <summary>
    /// 从上游结果里抽出「能给人看的一句」。优先 `displayMsg` / `message` / `msg`，
    /// 都取不到就截断原文。
    ///
    /// 为什么要有这个：业务错误（安全审核、非法请求、限流）的真实原因在
    /// `displayMsg` 里，贴整个信封会把那句话埋掉。
    /// </summary>
    public static string? ExtractDisplayMessage(string? body, int max = 400)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var text = body.Trim();
        string? found = null;

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(text);
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                foreach (var key in (string[])["displayMsg", "display_msg", "message", "msg"])
                {
                    var value = obj[key] switch
                    {
                        System.Text.Json.Nodes.JsonValue v when v.TryGetValue<string>(out var s) => s,
                        System.Text.Json.Nodes.JsonObject o => o["message"]?.GetValue<string>(),
                        _ => null,
                    };

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        found = value.Trim();
                        break;
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 正文不是 JSON —— 直接用原文
        }

        var result = found ?? text;
        return result.Length <= max ? result : result[..max] + "…";
    }
}
