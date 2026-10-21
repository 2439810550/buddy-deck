using System.Globalization;

namespace BuddyDeck.App.Services;

/// <summary>
/// 账号在界面上的状态。
///
/// 顺序有意义：越靠上越"要人管"。列表按这个排序，仪表盘也按这个数数。
/// </summary>
public enum AccountState
{
    /// <summary>已经过期，token 不能用了。</summary>
    Expired = 0,

    /// <summary>离过期不到 24 小时 —— 该续期了，但还能用。</summary>
    ExpiringSoon = 1,

    /// <summary>账号本身没问题，但网关报了错（限流、上游拒绝）。</summary>
    Degraded = 2,

    /// <summary>正常可用。</summary>
    Healthy = 3,

    /// <summary>没有过期时间 —— 上游没给，判断不了。</summary>
    Unknown = 4,
}

/// <summary>
/// 界面上要显示的、由账号数据推导出来的一切。
///
/// 为什么单独放一个文件而不是写在 ViewModel 里：这些是纯函数，
/// 不依赖 WPF，可以脱离界面直接跑测试。ViewModel 只负责调它们。
/// </summary>
public static class AccountPresentation
{
    /// <summary>「快过期了」的阈值。</summary>
    public static readonly TimeSpan ExpiringSoonWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// 从过期时刻推导状态。
    ///
    /// expiresAtMs &lt;= 0 表示上游没给过期时间（Core 里就是这么约定的），
    /// 这时不要瞎猜「已过期」—— 猜错会让用户以为号废了。返回 Unknown。
    /// </summary>
    public static AccountState DeriveState(
        long expiresAtMs,
        DateTimeOffset now,
        string? gatewayError = null)
    {
        if (!string.IsNullOrWhiteSpace(gatewayError))
            return AccountState.Degraded;

        if (expiresAtMs <= 0)
            return AccountState.Unknown;

        var remaining = DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs) - now;

        if (remaining <= TimeSpan.Zero) return AccountState.Expired;
        if (remaining <= ExpiringSoonWindow) return AccountState.ExpiringSoon;
        return AccountState.Healthy;
    }

    /// <summary>状态的中文名，直接进界面。</summary>
    public static string StateLabel(AccountState state) => state switch
    {
        AccountState.Expired => "已过期",
        AccountState.ExpiringSoon => "即将过期",
        AccountState.Degraded => "异常",
        AccountState.Healthy => "可用",
        _ => "未知",
    };

    /// <summary>
    /// 剩余时间的说法。
    ///
    /// 刻意不做"3 天 4 小时 12 分"这种精确到分的写法 ——
    /// 一个 token 的倒计时精确到分钟没有信息量，反而显得吵。
    /// 粒度跟着量级走：日子级只说到天，小时级只说到小时。
    /// </summary>
    public static string FormatRemaining(long expiresAtMs, DateTimeOffset now)
    {
        if (expiresAtMs <= 0) return "无过期时间";

        var remaining = DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs) - now;
        if (remaining <= TimeSpan.Zero)
        {
            var ago = now - DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs);
            return ago < TimeSpan.FromDays(1)
                ? "刚刚过期"
                : $"过期 {FormatDays(ago)}";
        }

        if (remaining < TimeSpan.FromMinutes(1)) return "不到 1 分钟";
        if (remaining < TimeSpan.FromHours(1)) return $"{remaining.TotalMinutes:F0} 分钟";
        if (remaining < TimeSpan.FromDays(1)) return $"{remaining.TotalHours:F0} 小时";
        return FormatDays(remaining);
    }

    private static string FormatDays(TimeSpan span)
    {
        var days = (int)span.TotalDays;
        if (days < 30) return $"{days} 天";

        var months = days / 30;
        if (months < 12) return $"{months} 个月";
        return $"{months / 12} 年";
    }

    /// <summary>账号的显示名。标签为空时退回 uid 的前 8 位，再不行退回 id。</summary>
    public static string DisplayName(AccountSummary account)
    {
        if (!string.IsNullOrWhiteSpace(account.Label)) return account.Label;
        if (!string.IsNullOrWhiteSpace(account.Uid)) return Shorten(account.Uid, 12);
        return Shorten(account.Id, 12);
    }

    /// <summary>uid 的短形式 —— 界面上够用就行，完整的在详情页里给。</summary>
    public static string Shorten(string value, int keep)
        => string.IsNullOrEmpty(value) || value.Length <= keep ? value : value[..keep] + "…";

    /// <summary>
    /// 企业号还是个人号。界面上要区分：企业号的归属和计费都跟个人号不一样。
    /// </summary>
    public static string TenantLabel(AccountSummary account)
        => string.IsNullOrWhiteSpace(account.EnterpriseId) ? "个人" : "企业";

    /// <summary>
    /// 仪表盘上那句总结。
    ///
    /// 这条文案是界面的声音，不是日志 —— 说清楚现在该做什么，不说"共 N 个账号"。
    /// </summary>
    public static string Summarize(int total, int usable)
        => total switch
        {
            0 => "还没有账号。导入一份登录文件就能开始。",
            _ when usable == 0 => $"{total} 个账号都不可用，先续期或者导入新号。",
            _ when usable < total => $"{total} 个账号里 {usable} 个可用。",
            _ => $"{total} 个账号全部可用。",
        };

    /// <summary>把 Unix 毫秒转成本地时间字符串；0 或负数返回「—」。</summary>
    public static string FormatTimestamp(long unixMs)
        => unixMs <= 0
            ? "—"
            : DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>百分比显示。0–1 的输入，返回「93.4%」这种。</summary>
    public static string FormatPercent(double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio)) return "—";
        return (ratio * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
    }

    // ========================================================================
    // 换号重试的体检指标（t16 的新口径）
    //
    // 背景：网关的 /metrics 现在有两套口径 ——
    //   · TotalRequests  客户端请求数（一次请求算一个，内部换号重试 3 次还是 1）
    //   · AttemptCount   上游尝试次数（每次真正出网算一个）
    //
    // 为什么要单独把它做出来：**这是唯一能看出"池子在恶化"的信号**。
    // 重试成功会让成功率保持 100% —— 一个请求试了 3 次才成功，
    // 指标上完全看不出来，但用户体验已经在变差（等待变长）。
    // ========================================================================

    /// <summary>
    /// 「请求 N 次 / 上游尝试 M 次」这个比值的显示串。
    ///
    /// 三个边界都在这里处理（它们都是会**静默出错**的地方，所以放在纯逻辑里配测试）：
    ///   · M = 0（新装的网关，一次请求都没有）→ 返回空串，界面走空态。
    ///     不返回 "0/0" —— 那既没信息量又诱人去做除法。
    ///   · N = 0 但 M &gt; 0 → 理论上不该出现，但真出现了也要能显示（不除零）
    ///   · 两者相等（从未重试）→ 仍然显示，因为「没重试过」本身是健康信号，
    ///     而且用户需要先看到这个数字，才知道它变大的含义
    /// </summary>
    public static string FormatAttemptRatio(long totalRequests, long attemptCount)
    {
        if (totalRequests <= 0 && attemptCount <= 0) return "";
        return $"{totalRequests} 次 / 上游 {attemptCount} 次";
    }

    /// <summary>
    /// 这个比值该怎么说。
    ///
    /// **注意措辞取向**：这里刻意**不说"异常""错误""告警"**。
    /// 换号重试是系统在**正常工作** —— 一个号被限流，池子换了一个号顶上，
    /// 用户照样拿到了答案。把它显示成故障是错的（用户会以为坏了）。
    /// 但也不能藏起来：它是池子承压的**早期信号**，等到成功率都掉了才看就晚了。
    ///
    /// 所以语气是「客观陈述 + 轻微提示」，不是「报警」。
    /// </summary>
    public static string DescribeAttemptOverhead(long totalRequests, long attemptCount)
    {
        if (totalRequests <= 0 && attemptCount <= 0) return "还没有请求。";

        var extra = Math.Max(0, attemptCount - totalRequests);

        if (extra == 0)
            return "每个请求都一次打中，没有发生过换号重试。";

        // 多打了多少的比例 —— 说"约 1.4 倍"比"多 37 次"更容易感知严重程度
        var factor = totalRequests > 0 ? (double)attemptCount / totalRequests : 0;

        return extra == 1
            ? "有 1 次请求换了号才成功（上游多收了 1 次尝试）。"
            : $"上游多收了 {extra} 次尝试，平均每个请求打 {factor:F1} 次。";
    }

    /// <summary>
    /// 是否发生过换号重试。界面用它决定要不要给这个数字加提示色。
    ///
    /// 单独抽出来是为了让"什么时候算发生过"只有一处定义 ——
    /// 免得 ViewModel 和 XAML 各判一套（`>` 还是 `≥` 很容易写歪）。
    /// </summary>
    public static bool HasRetryOverhead(long totalRequests, long attemptCount)
        => attemptCount > totalRequests && attemptCount > 0;

    /// <summary>
    /// 「平均耗时」那个数字的标签。
    ///
    /// **为什么不是「平均耗时」四个字**：t16 之后这个数的语义是
    /// 「用户从发请求到拿到回答的等待时长」——**含全部换号重试**，
    /// 不是单次上游往返。原来那个标签会被读成"上游响应时间"，
    /// 于是用户看到 3 秒时会去怪上游，其实那 3 秒里有一次换号重试。
    ///
    /// 之所以不拆成两套口径（"用户等待"/"上游往返"各显示一个）：
    /// 用户真正关心的只有"我要等多久"。多一个数字不增加信息，
    /// 只增加一个需要他理解的概念。
    /// </summary>
    public const string AverageLatencyLabel = "平均等待";

    /// <summary>耗时显示：短的说毫秒，长的说秒。</summary>
    public static string FormatLatency(double ms)
    {
        if (double.IsNaN(ms) || ms < 0) return "—";
        return ms < 1000
            ? $"{ms:F0} ms"
            : $"{ms / 1000:F1} s";
    }

    /// <summary>
    /// HTTP 状态码 → 人话。
    ///
    /// 「HTTP 成功率」这种词用户看不懂，但「上游拒绝（429）」看得懂。
    /// </summary>
    public static string DescribeStatus(int status) => status switch
    {
        200 or 201 => "正常",
        401 => "凭证失效",
        403 => "上游拒绝",
        404 => "模型不存在",
        429 => "被限流",
        >= 500 => "上游出错",
        _ => $"HTTP {status}",
    };

    // ========================================================================
    // URL scheme 白名单守卫（安全边界）
    //
    // ## 它防的是什么
    //
    // `CheckinActionButton.Action` 里是一个**来自上游网络的 URL**
    // （t50 实测真机值：`workbuddy://home?openBuddySwitcher=true`）。
    //
    // 界面若把它直接交给 `Process.Start` / `ShellExecute`，等于：
    // **上游只要改一个字符串，就能让用户机器上的任意协议处理器被触发。**
    // 上游被入侵、或被中间人改写响应时，`ms-settings:`
    // `file:` `smb:` `mailto:` 之类都可能是攻击面 —— 它们能拉起系统组件、
    // 访问本地路径、发起网络连接。
    //
    // **「当前值是 workbuddy:// 所以无害」不是安全论证。** 那个值由上游决定，
    // 明天就可以变。安全属性必须由**使用侧**保证，不能依赖对端行为。
    //
    // ## 为什么校验放在这里（而不是 Core 的解析层）
    //
    // 解析层的职责是**如实搬运**：它把上游发的东西读出来、保持形状。
    // 在那里做白名单会混淆两件事 —— 「上游发了什么」和「我们愿意做什么」，
    // 而且会让 Core 无法忠实表达上游的实际内容（诊断时看不到真值）。
    // 白名单只应该出现在**真正要产生副作用的那一点**，也就是这里，
    // 以及将来调用 <see cref="IsSafeActionUrl"/> 的那个 Process.Start。
    //
    // ## 用法（给下一个接线的人）
    //
    // ```csharp
    // if (!AccountPresentation.IsSafeActionUrl(button.Action)) {
    //     // 不要打开，也不要静默吞掉 —— 记一条日志说明收到了什么 scheme
    //     return;
    // }
    // Process.Start(new ProcessStartInfo(button.Action) { UseShellExecute = true });
    // ```
    //
    // ---
    //
    // ⚠ **当前接线状态（2026-10-07）：界面尚未消费 `ActionButton`。**
    //    全 App 层搜 `ActionButton` / `Process.Start` / `ShellExecute` 均无命中。
    //    也就是说下面这个守卫**目前还没有调用方** —— 它现在就存在，
    //    是为了让接线那天有一个「拿起来就用、不用现想」的安全路径，
    //    而不是让人在实现功能的同时顺手决定安全边界（那种时刻最容易出错）。
    // ========================================================================

    /// <summary>
    /// 允许被打开的 URL scheme 白名单。**只有这一个。**
    ///
    /// 为什么是白名单而不是黑名单：黑名单要穷举 `file:` `ms-settings:` `smb:`
    /// `mailto:` `javascript:` … 永远列不全，而且新协议还在出现。
    /// 白名单的失败方向是「该开没开」（可见、可修），黑名单的失败方向是
    /// 「不该开的开了」（静默、可能灾难）。安全控制要选前者。
    /// </summary>
    private static readonly string[] AllowedActionSchemes = ["workbuddy"];

    /// <summary>
    /// 这个 URL 能不能安全地交给系统去打开。
    ///
    /// 返回 false 的所有情形**都必须不打开**，且**不得**因为「反正解析出来了」
    /// 就退化成打开别的什么东西。
    ///
    /// 拒绝清单（都有对应测试）：
    ///   · 空 / 空白 —— 没有可执行的动作
    ///   · 无法解析成绝对 URI —— 相对路径、乱码
    ///   · scheme 不在白名单 —— `https:` `file:` `ms-settings:` `smb:` …
    ///   · 连 scheme 都没有（形如 `foo/bar`）—— 会被当相对路径处理，危险
    ///
    /// **大小写**：scheme 按 RFC 3986 是大小写不敏感的，所以比较用
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>。
    /// （`WORKBUDDY://` 和 `workbuddy://` 是同一个 scheme。）
    /// </summary>
    public static bool IsSafeActionUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // 必须是绝对 URI —— TryCreate 对相对路径也返回 true，
        // 所以光判 TryCreate 不够，还要 IsAbsoluteUri。两个都要。
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (!uri.IsAbsoluteUri) return false;

        // 没有 scheme 的绝对 URI 在 .NET 里基本不会出现，但显式挡一下 ——
        // 这里的每个 return false 都是「宁可不开」。
        if (string.IsNullOrEmpty(uri.Scheme)) return false;

        foreach (var allowed in AllowedActionSchemes)
        {
            if (string.Equals(uri.Scheme, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 给日志/诊断用：把一个被拒绝的 URL 说清楚。
    ///
    /// 为什么需要它：守卫返回 false 之后如果什么都不说，排查时根本不知道
    /// 「上游到底发了个什么被挡了」。但**又不能把整个 URL 原样打进日志** ——
    /// 它可能带查询参数（`?token=…`）。所以这里只回 scheme，不回完整 URL。
    /// 这是「够定位 + 不泄露」的折中。
    /// </summary>
    public static string DescribeRejectedAction(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "上游给了一个空的行动链接。";

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
            return "上游给的行动链接不是一个合法的绝对 URL，已忽略。";

        return string.IsNullOrEmpty(uri.Scheme)
            ? "上游给的行动链接没有 scheme，已忽略。"
            : $"上游给的行动链接用了不被允许的 scheme「{uri.Scheme}:」，已忽略。";
    }

    // ========================================================================
    // 积分展示（t69，对标 workbuddy-switch 的账号卡）
    //
    // 数据来自 Core 的 CreditsSnapshot（t57）。这里只做**展示层推导**：
    // 怎么排序、什么时候算"临期"、怎么把它说成人话。
    //
    // ⚠ 谁负责什么：
    //   · Core（CreditsSnapshot）—— 解析上游的 JSON，给出结构化数据
    //   · 这里 —— 「界面上看起来该是什么样」（阈值、措辞、格式）
    //   · ViewModel/ XAML —— 只负责摆位置
    // 阈值放这里而不是 Core：**它是产品决策，不是协议事实**。
    // ========================================================================

    /// <summary>
    /// 「快要过期了」的天数阈值。**3 天。**
    ///
    /// 为什么是 3 天而不是 7 天：积分是**先到期先用**的（上游按批次扣减），
    /// 而用户不会天天开这个面板。3 天是「再不来处理就真没了」的量级；
    /// 7 天的话告警会长期亮着 —— 长期亮着的告警等于没有告警。
    /// </summary>
    public const int CreditExpiringSoonDays = 3;

    /// <summary>
    /// 积分包的到期状态。
    ///
    /// 与 <see cref="AccountState"/> 分开：那个说的是**账号（token）**，
    /// 这个说的是**积分包**。两者会不同 —— 账号好好的但积分快过期很常见。
    /// </summary>
    public enum CreditExpiryState
    {
        /// <summary>解析不出到期时间 —— 不当成"最早到期"（那会制造假紧迫感）。</summary>
        Unknown = 0,

        /// <summary>已经过期。</summary>
        Expired = 1,

        /// <summary>N 天内到期（<see cref="CreditExpiringSoonDays"/>）。</summary>
        ExpiringSoon = 2,

        /// <summary>还早。</summary>
        Fine = 3,
    }

    /// <summary>
    /// 从一个到期时刻推导积分包状态。
    ///
    /// <paramref name="expireAtMs"/> 为 null 表示**解析不出时间** ——
    /// 返回 Unknown，而不是当成"已过期"或"最早到期"。
    /// 宁可不说，也不要制造假的紧迫感（t57 的 ByExpiryAscending 也是这个取向：
    /// 解析不出的排最后）。
    /// </summary>
    public static CreditExpiryState DeriveCreditExpiry(long? expireAtMs, DateTimeOffset now)
    {
        if (expireAtMs is null) return CreditExpiryState.Unknown;

        var end = DateTimeOffset.FromUnixTimeMilliseconds(expireAtMs.Value);
        if (end <= now) return CreditExpiryState.Expired;
        if (end <= now.AddDays(CreditExpiringSoonDays)) return CreditExpiryState.ExpiringSoon;
        return CreditExpiryState.Fine;
    }

    /// <summary>积分包状态的中文名。</summary>
    public static string CreditStateLabel(CreditExpiryState state) => state switch
    {
        CreditExpiryState.Expired => "已过期",
        CreditExpiryState.ExpiringSoon => $"{CreditExpiringSoonDays} 天内到期",
        CreditExpiryState.Fine => "有效",
        _ => "无到期时间",
    };

    /// <summary>
    /// 剩余积分的大字显示。
    ///
    /// 与 <see cref="FormatPercent"/> 不同：这里是**绝对量**，
    /// 而且小数位跟着量级走 —— 几百点显示一位小数有意义，
    /// 几万点还显示小数就是噪音。
    /// </summary>
    public static string FormatCredits(double remaining)
    {
        if (double.IsNaN(remaining) || double.IsInfinity(remaining)) return "—";

        // ⚠ 负数归零，但**仍走同一条格式路径** —— 直接 `return "0"` 会漏掉小数位，
        // 界面上一列里出现「0」和「50.3」两种精度，看着像 bug。
        // （原实现就返回 "0"，测试期望 "0.0"，captain 按"格式一致"统一为后者。）
        if (remaining < 0) remaining = 0;

        // ⚠ 舍入方向是**四舍五入**（AwayFromZero），不是 .NET 默认的银行家舍入。
        // 50.25 用 "F1" 默认格式会输出 "50.2"（.25 走 ToEven），
        // 而用户看到余额 50.25 期望的是 "50.3" —— 银行家舍入在余额显示上反直觉。
        return remaining switch
        {
            < 100 => Round(remaining, 1).ToString("F1", CultureInfo.InvariantCulture),
            < 10000 => Round(remaining, 0).ToString("F0", CultureInfo.InvariantCulture),
            _ => Round(remaining / 1000, 1).ToString("F1", CultureInfo.InvariantCulture) + "k",
        };
    }

    /// <summary>四舍五入（远离零方向）。</summary>
    private static double Round(double value, int digits)
        => Math.Round(value, digits, MidpointRounding.AwayFromZero);

    // ══════════════════════════════════════════════════════════
    //  模型冷却（用户实测反馈「每一个账号的模型冷却情况也没有」）
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 冷却状态的严重度。用来决定界面上的颜色 —— **不是所有冷却都值得报警**。
    /// </summary>
    public enum CooldownLevel
    {
        /// <summary>没有冷却。界面不显示任何东西。</summary>
        None,

        /// <summary>有冷却，但很快就好（≤ 5 分钟）。安静的次级文字色。</summary>
        Brief,

        /// <summary>冷却较久（&gt; 5 分钟）。醒目一点 —— 用户该考虑换号了。</summary>
        Long,
    }

    /// <summary>
    /// 一个号的冷却状态。
    ///
    /// @param count 正在冷却的模型数
    /// @param shortestRemainSeconds 最短的那个还剩多少秒（没有冷却时为 0）
    /// </summary>
    public readonly record struct CooldownSummary(int Count, long ShortestRemainSeconds, CooldownLevel Level);

    /// <summary>
    /// 把冷却列表归纳成「界面要显示什么」。
    ///
    /// ## 判据
    ///
    /// · 空 ⇒ <see cref="CooldownLevel.None"/>（**界面什么都不显示**，
    ///   而不是显示"0 个冷却"—— 那是噪音，每个号都有这一行就没人看了）
    /// · ≤ 5 分钟 ⇒ <see cref="CooldownLevel.Brief"/>
    ///   （限流通常几分钟就恢复，报得太重会让用户白白换号）
    /// · \&gt; 5 分钟 ⇒ <see cref="CooldownLevel.Long"/>
    ///
    /// ## 为什么用「最短的那个」而不是总数判断
    ///
    /// 用户关心的是**「我什么时候能再用上」**，不是"有几个模型在冷却"。
    /// 一个号有 5 个模型冷却但最快 30 秒恢复，比只冷 1 个但要等 20 分钟好得多。
    /// </summary>
    public static CooldownSummary SummarizeCooldowns(IReadOnlyList<ModelCooldown>? cooldowns)
    {
        if (cooldowns is null || cooldowns.Count == 0)
        {
            return new CooldownSummary(0, 0, CooldownLevel.None);
        }

        var shortest = cooldowns.Min(c => c.RemainSeconds);
        var level = shortest <= BriefCooldownSeconds ? CooldownLevel.Brief : CooldownLevel.Long;
        return new CooldownSummary(cooldowns.Count, shortest, level);
    }

    /// <summary>「很快就好」的上界（秒）。超过它才值得醒目。</summary>
    public const long BriefCooldownSeconds = 5 * 60;

    /// <summary>
    /// 冷却的显示文本。
    ///
    /// 说人话：说「再过 3 分钟就好」，而不是「冷却中」——
    /// 前者告诉用户**要等多久**，后者只告诉他"现在不行"。
    /// </summary>
    public static string FormatCooldowns(CooldownSummary summary)
    {
        if (summary.Count == 0) return "";

        var wait = FormatWait(summary.ShortestRemainSeconds);
        return summary.Count == 1
            ? $"{wait}后可再用"
            : $"{summary.Count} 个模型冷却中，最快 {wait} 恢复";
    }

    /// <summary>把剩余秒数说成人话。</summary>
    public static string FormatWait(long seconds)
    {
        if (seconds <= 0) return "马上";
        if (seconds < 60) return $"{seconds} 秒";

        var minutes = seconds / 60.0;
        if (minutes < 60) return $"{Math.Ceiling(minutes):F0} 分钟";

        var hours = minutes / 60.0;
        return $"{Math.Ceiling(hours):F0} 小时";
    }

    /// <summary>
    /// 冷冻的解释文本（连续失败到阈值）。
    ///
    /// **必须说清"为什么"** —— 只说「已冻结」用户不知道是自己账号坏了、
    /// 还是网络问题、还是网关 bug。带上次数他至少知道"它一直在失败"。
    /// </summary>
    public static string FormatFrozen(int failureCount)
        => failureCount <= 0
            ? "已暂停使用"
            : $"连续失败 {failureCount} 次，已暂停使用";

    /// <summary>
    /// 积分包的到期显示。
    ///
    /// 说成人话：临界的时候说"还剩 X 小时"（比"10/12 23:59"更有行动感），
    /// 还早的时候给日期（精确到分钟没意义）。
    /// </summary>
    public static string FormatCreditExpiry(long? expireAtMs, DateTimeOffset now)
    {
        if (expireAtMs is null) return "无到期时间";

        var end = DateTimeOffset.FromUnixTimeMilliseconds(expireAtMs.Value);
        var remaining = end - now;

        if (remaining <= TimeSpan.Zero) return "已过期";
        if (remaining < TimeSpan.FromHours(1)) return $"还剩 {remaining.TotalMinutes:F0} 分钟";
        if (remaining < TimeSpan.FromDays(1)) return $"还剩 {remaining.TotalHours:F0} 小时";
        if (remaining < TimeSpan.FromDays(CreditExpiringSoonDays))
            return $"还剩 {remaining.TotalDays:F0} 天";

        return end.ToLocalTime().ToString("MM-dd 到期", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 积分风险的一句话总结（总览页用）。
    ///
    /// 取向与 <see cref="Summarize"/> 一致：**说清楚现在该做什么**，
    /// 而不是报一个数。
    /// </summary>
    public static string SummarizeCreditRisk(int accountsAtRisk, int packagesExpiring)
    {
        if (accountsAtRisk <= 0)
            return $"没有账号在 {CreditExpiringSoonDays} 天内积分到期。";

        return packagesExpiring > 0
            ? $"{accountsAtRisk} 个账号有积分即将到期（共 {packagesExpiring} 个包）。"
            : $"{accountsAtRisk} 个账号的积分即将到期。";
    }

    /// <summary>
    /// 状态 chip 的一句话（账号卡上那个小标签）。
    ///
    /// 为什么单独一个方法而不是直接用 <see cref="StateLabel"/>：
    /// chip 上要**同时**表达账号状态和积分状态，而它们是两个维度。
    /// 只显示其中一个都会漏掉一半信息。
    /// </summary>
    public static string FormatAccountChip(AccountState account, CreditExpiryState credit)
        => credit switch
        {
            CreditExpiryState.ExpiringSoon => "积分临期",
            CreditExpiryState.Expired => "积分已过期",
            _ => StateLabel(account),
        };
}
