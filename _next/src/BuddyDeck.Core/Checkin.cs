using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Core;

/// <summary>
/// 每日签到能力的**返回结构**。
///
/// ## 为什么要有状态枚举而不是一个 bool
///
/// 调用方（界面按钮、CLI `checkin` 子命令、批量签到）对四种结果的处理**完全不同**：
///
/// ```
/// Claimed       签到成功，界面上要显示「+N 积分」并放一次小动画
/// AlreadyClaimed 今天早签过了，什么都不用做（不是错误）
/// NoActivity    上游没有进行中的活动，按钮该灰掉
/// Failed        真出错，要显示原因
/// ```
///
/// 用一个 bool 的话，「已经签过」和「凭证失效」会挤在同一个 false 里 ——
/// 界面就分不清该显示一句提示还是弹一个重新登录的引导。
///
/// ## 为什么不抛异常给调用方
///
/// 签到是**批量操作**的一环（旧版 `ensureCheckedIn` 就是被批量签到和界面按钮
/// 共用的）。一个账号凭证失效就抛出去的话，后面那些账号全签不了。
/// 所以这里一律返回结构，`ErrorKind` 里面放分类。
/// </summary>
public enum CheckinOutcome
{
    /// <summary>签到成功，拿到了积分。</summary>
    Claimed,

    /// <summary>今天已经签过了（幂等命中，既可能来自本地记录也可能来自上游）。</summary>
    AlreadyClaimed,

    /// <summary>当前没有进行中的签到活动 —— 上游 `active` 为 false。</summary>
    NoActivity,

    /// <summary>失败。原因见 <see cref="CheckinResult.ErrorKind"/> / <see cref="CheckinResult.Message"/>。</summary>
    Failed,
}

/// <summary>一次签到尝试的结果。</summary>
public sealed record CheckinResult
{
    /// <summary>结论。</summary>
    public required CheckinOutcome Outcome { get; init; }

    /// <summary>
    /// 人类的可读说明。**失败时一定有值** —— 界面直接显示它就行。
    /// </summary>
    public string Message { get; init; } = "";

    /// <summary>
    /// 失败分类。取值来自 <see cref="UpstreamErrorKind"/>，
    /// 加上两个签到特有的：
    ///   · <c>no_activity</c> 没有进行中的活动
    ///   · <c>unexpected</c> 收到了响应但形状认不出来
    ///
    /// 成功/已签到时为 null。
    /// </summary>
    public string? ErrorKind { get; init; }

    /// <summary>HTTP 状态码。0 表示连接层失败（DNS / TLS / 超时）。</summary>
    public int Status { get; init; }

    /// <summary>上游的业务 code（信封里的 <c>code</c>）。没拿到时为 null。</summary>
    public int? Code { get; init; }

    /// <summary>上游的 requestId —— 拿它找腾讯对账用。</summary>
    public string RequestId { get; init; } = "";

    /// <summary>签到后的活动状态。拿不到时为 null。</summary>
    public CheckinStatus? Status2 { get; init; }

    /// <summary>
    /// 这次调用**有没有真的打上游**。
    ///
    /// 本地幂等命中（今天已经签过、本地记着）时为 false ——
    /// 测试和指标都靠它区分「打了一次上游」和「短路返回」。
    /// </summary>
    public bool UpstreamCalled { get; init; }

    /// <summary>单纯的成功判定（成功 + 已签到都算拿到结果了）。</summary>
    public bool Ok => Outcome is CheckinOutcome.Claimed or CheckinOutcome.AlreadyClaimed;
}

/// <summary>
/// 签到活动的归一化状态。
///
/// ## 为什么字段这么多
///
/// 界面的账号页要画一块签到面板：连签天数、本周进度、活动名字、双倍日…… 这些
/// **全是上游一次给的**，我们只是把 snake_case 转成 camelCase。少转一个字段，
/// 界面上就少一块 —— 而且没人会发现（上游不会因为你不读就报错）。
///
/// ## 为什么不用强类型反序列化
///
/// 上游字段是 snake_case + 部分可缺，而且**它是我们扒出来的、不是文档的**。
/// 强类型反序列化遇到上游改字段会抛；手写读法遇到不认识的字段只是留个默认值。
/// 对「扒出来的接口」来说，后者是唯一活得久的选择。
/// </summary>
public sealed record CheckinStatus
{
    /// <summary>有没有进行中的签到活动。false 时其他字段基本都没意义。</summary>
    public bool Active { get; init; }

    /// <summary>今天签过了吗。</summary>
    public bool TodayCheckedIn { get; init; }

    /// <summary>连续签到天数。</summary>
    public int StreakDays { get; init; }

    /// <summary>每日基础积分。</summary>
    public double DailyCredit { get; init; }

    /// <summary>今天拿到的积分。</summary>
    public double TodayCredit { get; init; }

    /// <summary>累计积分。</summary>
    public double TotalCredits { get; init; }

    /// <summary>本周已签天数。</summary>
    public int WeekCheckinDays { get; init; }

    /// <summary>本周的逐日打卡情况（true = 那天签了）。</summary>
    public IReadOnlyList<bool> WeekProgress { get; init; } = [];

    /// <summary>已经签过的日期字符串（上游原样，不做解析 —— 格式没验证过）。</summary>
    public IReadOnlyList<string> CheckinDates { get; init; } = [];

    /// <summary>活动名。</summary>
    public string ActivityName { get; init; } = "";

    /// <summary>主题名。</summary>
    public string ThemeName { get; init; } = "";

    /// <summary>第几赛季。</summary>
    public int Season { get; init; }

    /// <summary>活动开始时间（上游原样字符串，可能是带时区的文案）。</summary>
    public string StartTime { get; init; } = "";

    /// <summary>活动结束时间。</summary>
    public string EndTime { get; init; } = "";

    /// <summary>按钮文案（上游给的，界面直接用 —— 比自己编一套准）。</summary>
    public string ClaimButtonText { get; init; } = "";

    /// <summary>
    /// 今天是不是连签奖赏日。
    ///
    /// ## ⚠ 这个属性**当前不经序列化器**
    ///
    /// 它的值由 <c>BoolOf(obj, "is_streak_day")</c> **手工抽取**（见本类型下方
    /// 那个静态工厂）—— 属性名根本不参与绑定，所以当前是安全的。
    ///
    /// **那为什么还要标注**：前缀布尔（`Is*`/`Has*`）的「写出去」和「读回来」
    /// 键名**天然不对称** —— 序列化时这个属性会写成 `IsStreakDay`，
    /// 而这里读的是 `is_streak_day`。哪天有人把这些 DTO 序列化发出去
    /// （给界面用、或加个调试端点），就会**写得出、读不回来**，而且不报错。
    ///
    /// **代价不对称**：标注 2 行 vs 事后查一天。
    ///
    /// 真实案例（同形状，已发生过）：`IsTerminal` ← JSON 键 `terminal`，
    /// `PropertyNameCaseInsensitive` 不做 `Is` 前缀推理 → 绑不上 →
    /// 留在默认值 `true` → 「所有失败的中间尝试都被当成终局结果」。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("is_streak_day")]
    public bool IsStreakDay { get; init; }

    /// <summary>连签奖赏要求的天数。</summary>
    public int StreakBonusDays { get; init; }

    /// <summary>连签奖赏给的积分。</summary>
    public double StreakBonusCredit { get; init; }

    /// <summary>
    /// 距下一次连签奖赏还有几天。
    ///
    /// ## 真机来源（t48 实测，2026-10-07）
    ///
    /// ```
    /// POST /v2/billing/meter/checkin-activity-status
    /// → {"code":0,"data":{ ..., "next_streak_day":0, "is_streak_day":false, ... }}
    /// ```
    /// 19 个账号全部返回该字段（int），实测值 `0`（当天 `is_streak_day` 也是 false）。
    ///
    /// **缺失时取 0**（不是 null）—— 与同族的 `StreakBonusDays` / `StreakBonusCredit`
    /// 保持同一种降级方式：这一族都是"上游没给就当作 0"，界面按 0 渲染成
    /// 「暂无倒计时」是最自然的表现，不需要额外判空分支。
    ///
    /// ⚠ 语义未验证的部分：**「0」到底是"奖赏日已到"还是"没有倒计时"**，
    /// 真机上两种解释都自洽（当天 `is_streak_day=false`、`streak_bonus_days=0`）。
    /// 这里**只如实搬运数值，不替界面解读**。
    /// </summary>
    public int NextStreakDay { get; init; }

    /// <summary>
    /// 上游给的行动按钮。**整块可能为 null**（上游只在有活动时发）。
    ///
    /// ## 真机来源（t48 实测，2026-10-07）
    ///
    /// ```
    /// "action_button": {
    ///     "show": true,
    ///     "text": "体验Buddy应用",
    ///     "action": "workbuddy://home?openBuddySwitcher=true"
    /// }
    /// ```
    ///
    /// ## ⚠ `action` 是 `workbuddy://` 深链 —— 界面消费时必须当**不可信输入**
    ///
    /// 它会被界面拿去打开（`Process.Start` / `ShellExecute` 之类）。一个
    /// **上游可控的 URL 直接交给系统打开**是典型的注入面：今天的值是
    /// `workbuddy://…`（无害），但它是**服务端数据**，将来可能变成任意 scheme。
    ///
    /// 所以：**解析层只负责如实搬运，不做白名单**（那不是它的职责）；
    /// **界面在用之前必须校验 scheme 是自己认识的那几个**。
    /// 这条写在这里，是因为用到它的地方（界面）未必会想到它的来源是网络。
    /// </summary>
    public CheckinActionButton? ActionButton { get; init; }

    /// <summary>
    /// 从上游的 `data` 归一化。
    ///
    /// @returns 不是对象时返回 null（上游回了 `data: []` 或 `data: 1` 那种）。
    /// </summary>
    public static CheckinStatus? FromData(JsonNode? data)
    {
        if (data is not JsonObject obj) return null;

        return new CheckinStatus
        {
            Active = BoolOf(obj, "active"),
            TodayCheckedIn = BoolOf(obj, "today_checked_in"),
            StreakDays = IntOf(obj, "streak_days"),
            DailyCredit = NumOf(obj, "daily_credit"),
            TodayCredit = NumOf(obj, "today_credit"),
            TotalCredits = NumOf(obj, "total_credits"),
            WeekCheckinDays = IntOf(obj, "week_checkin_days"),
            WeekProgress = BoolArrayOf(obj, "week_progress"),
            CheckinDates = StringArrayOf(obj, "checkin_dates"),
            ActivityName = StringOf(obj, "activity_name"),
            ThemeName = StringOf(obj, "theme_name"),
            Season = IntOf(obj, "season"),
            StartTime = StringOf(obj, "start_time"),
            EndTime = StringOf(obj, "end_time"),
            ClaimButtonText = StringOf(obj, "claim_button_text"),
            IsStreakDay = BoolOf(obj, "is_streak_day"),
            StreakBonusDays = IntOf(obj, "streak_bonus_days"),
            StreakBonusCredit = NumOf(obj, "streak_bonus_credit"),
            // ⚠ 下面两个是 t48 真机实测发现的字段（原先被静默忽略）：
            //   "next_streak_day": 0
            //   "action_button": {"show":true,"text":"体验Buddy应用",
            //                     "action":"workbuddy://home?openBuddySwitcher=true"}
            NextStreakDay = IntOf(obj, "next_streak_day"),
            ActionButton = ButtonOf(obj, "action_button"),
        };
    }

    // ── 宽容读法 ───────────────────────────────────────────────
    //
    // 上游是「扒出来的接口」：字段类型说变就变（数字变字符串、布尔变 0/1）。
    // 读不出来的值一律给默认值，**绝不抛** —— 一个字段读不出不该让整块面板消失。

    private static bool BoolOf(JsonObject obj, string name)
    {
        var node = obj[name];
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<bool>(out var b)) return b;

        // ⚠ 字符串要**独立判**，不能靠下面那条 int 分支：
        // .NET 的 JsonValue.TryGetValue<int> 对字符串 "1" **也会成功**，
        // 而字符串 "true" 在那种写法下会落进 int 分支 → 被读成 false。
        if (value.TryGetValue<string>(out var s))
        {
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n != 0;
        }

        // 上游偶尔用 0/1
        if (value.TryGetValue<int>(out var i)) return i != 0;
        return false;
    }

    private static int IntOf(JsonObject obj, string name)
    {
        var node = obj[name];
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l)) return l is >= int.MinValue and <= int.MaxValue ? (int)l : 0;
        if (value.TryGetValue<double>(out var d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        if (value.TryGetValue<string>(out var s)
            && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return 0;
    }

    /// <summary>
    /// 读一个可选对象（`action_button`）。
    ///
    /// @returns 字段不存在、为 null、或不是对象时返回 **null**（整块缺失）。
    ///
    /// ## 为什么"不是对象"也返回 null 而不是造一个空壳
    ///
    /// 空壳（`new CheckinActionButton()`）和「上游没发」在界面上是**两回事**：
    /// 前者会渲染出一个"有按钮但文案为空"的东西，后者是"没有按钮"。
    /// 上游回了 `"action_button": 123`（类型漂移）时，正确处置是**当作没发**，
    /// 而不是造一个看起来像有按钮的空对象。
    /// </summary>
    private static CheckinActionButton? ButtonOf(JsonObject obj, string name)
    {
        if (obj[name] is not JsonObject button) return null;

        // 三个字段全缺时也当作没发 —— 否则又会造出那个"空壳按钮"
        var hasAny = button.ContainsKey("show") || button.ContainsKey("text") || button.ContainsKey("action");
        if (!hasAny) return null;

        return new CheckinActionButton
        {
            Show = BoolOf(button, "show"),
            Text = StringOf(button, "text"),
            Action = StringOf(button, "action"),
        };
    }

    /// <summary>
    /// 读一个数。
    ///
    /// ⚠ 顺序：**字符串放最后**。JsonValue 的 TryGetValue 会做转换，
    /// 所以数值字段用字符串分支兜底就够了；但反过来（先字符串）会把数字
    /// 也读成字符串，类型判断就失去意义。
    /// </summary>
    private static double NumOf(JsonObject obj, string name)
    {
        var node = obj[name];
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : 0;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<string>(out var s)
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed))
        {
            return parsed;
        }
        return 0;
    }

    private static string StringOf(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";

    /// <summary>读一个布尔数组。逐项用 <see cref="BoolOf"/> 的同一套宽容规则。</summary>
    private static List<bool> BoolArrayOf(JsonObject obj, string name)
    {
        if (obj[name] is not JsonArray array) return [];
        var result = new List<bool>(array.Count);
        foreach (var item in array)
        {
            result.Add(item is JsonValue value && BoolOfValue(value));
        }
        return result;
    }

    /// <summary>单个 <see cref="JsonValue"/> → bool，规则与 <see cref="BoolOf"/> 一致。</summary>
    private static bool BoolOfValue(JsonValue value)
    {
        if (value.TryGetValue<bool>(out var b)) return b;
        if (value.TryGetValue<string>(out var s))
        {
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n != 0;
        }
        if (value.TryGetValue<int>(out var i)) return i != 0;
        return false;
    }

    private static List<string> StringArrayOf(JsonObject obj, string name)
    {
        if (obj[name] is not JsonArray array) return [];
        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var s) && s.Length > 0)
            {
                result.Add(s);
            }
        }
        return result;
    }
}

/// <summary>
/// 上游给的行动按钮（`action_button`）。**整块可能是 null** —— 上游只在有活动时发。
///
/// ## 真机形态（t48 实测，2026-10-07）
///
/// ```jsonc
/// "action_button": {
///     "show": true,
///     "text": "体验Buddy应用",
///     "action": "workbuddy://home?openBuddySwitcher=true"
/// }
/// ```
///
/// ## 三个字段各自的默认
///
/// ```
/// Show   缺 → false（**不显示**，保守方向：不确定就别弹按钮）
/// Text   缺 → ""（空串）
/// Action 缺 → ""（空串）
/// ```
///
/// `Show` 默认 **false** 是刻意的：这是"要不要给用户看一个按钮"的开关，
/// 拿不准时**不显示**比误显示安全（误显示会让用户点到一个没有目标的东西）。
/// </summary>
public sealed record CheckinActionButton
{
    /// <summary>上游说要不要显示这个按钮。**缺字段时为 false**（保守：不确定就不显示）。</summary>
    public bool Show { get; init; }

    /// <summary>按钮文案。</summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// 按钮的动作 —— 真机是 `workbuddy://home?openBuddySwitcher=true`（深链）。
    ///
    /// ## ⚠ 这是**上游可控的数据**，界面消费前必须校验
    ///
    /// 界面大概率会把它交给系统打开（`Process.Start` / `ShellExecute`）。
    /// **一个来自网络、能触发任意 scheme 的 URL 是典型的注入面。**
    ///
    /// 解析层**只如实搬运**（校验不是它的职责）；用它的地方要自己判断
    /// scheme 是不是自己认识的那几个（`workbuddy://` 之类），
    /// 不认识的一律不打开。
    ///
    /// 这条写在类型上而不是只写在界面里 —— 因为**界面作者看不出这个字符串
    /// 的来源是网络**（它长得就像一个常量）。
    /// </summary>
    public string Action { get; init; } = "";
}

/// <summary>
/// 每日签到（对齐旧版 `checkin.js`，151 行）。
///
/// ## 上游端点
///
/// 接口是从桌面客户端产物里扒出来的（`resources/app.asar` 里的
/// `WorkBuddyAgentAdapter`），不是官方文档：
///
/// ```
/// POST {billingBase}/v2/billing/meter/checkin-activity-status   查签到状态
/// POST {billingBase}/v2/billing/meter/daily-checkin             执行签到
/// ```
///
/// 两个都是**空 body `{}`**，用的是和计费接口同一套头
/// （<see cref="UpstreamHeaders.ApplyBilling"/> —— 只有 Authorization / X-Product /
/// X-Enterprise-Id，**不带 `X-IDE-*` 归属族**，带了会把用量归属算乱）。
///
/// 客户端还会带一个 `X-Device-Token`（腾讯 Turing 风控指纹），实测**不带也能通**，
/// 所以这里不发 —— 那个 token 要靠客户端自己的原生模块拿，我们拿不到也不该伪造。
///
/// ## 幂等：三层，从便宜到贵
///
/// ```
/// ① 本地记录   VaultEntry.LastCheckinAt 是今天  → 直接返回，一个字节都不发
/// ② 上游状态   checkin-activity-status 说 today_checked_in → 不打 daily-checkin
/// ③ 上游本身   真打了也没关系，重复签它照样回 code 0
/// ```
///
/// ① 是**跨进程**的（写到保险库文件里），所以网关重启、界面重开都还认。
/// 这是旧版没有的：旧版只做 ②，每次点签到都要先打一次 status 请求。
///
/// ## 为什么「查一下，没签就签」是一个方法而不是两个
///
/// 调用方（界面按钮、批量签到、CLI）想要的语义**从来都是**「确保今天签了」，
/// 而不是「先给我看状态，我再决定签不签」。把它做成一个方法，
/// 调用方就不可能忘掉那个检查。
/// </summary>
public sealed partial class Checkin
{
    /// <summary>查签到状态。</summary>
    public const string StatusPath = "/v2/billing/meter/checkin-activity-status";

    /// <summary>
    /// 执行签到。
    ///
    /// ⚠ 这个字面量是**外部契约**的一部分：e2e 检查器拿它做「签到能力存在」的
    /// 检测特征（`grep checkin` 会被 `Vault.cs` 的 `lastCheckinAt` 字段名假命中，
    /// 所以只有这个路径是可信的）。**不要改。**
    /// </summary>
    public const string ClaimPath = "/v2/billing/meter/daily-checkin";

    /// <summary>没有进行中的活动 —— 签到特有的失败分类。</summary>
    public const string NoActivityKind = "no_activity";

    /// <summary>拿到了响应但形状认不出来。</summary>
    public const string UnexpectedKind = "unexpected";

    private readonly HttpClient _http;
    private readonly UpstreamVariant _variant;
    private readonly ILogger<Checkin> _log;

    /// <summary>
    /// 可注入的时钟。
    ///
    /// 为什么需要：幂等的判定是「LastCheckinAt 是不是**今天**」。
    /// 用 <c>DateTimeOffset.UtcNow</c> 直接读的话，「跨天重签」这条用例
    /// 就只能靠真的等到明天 —— 那等于测不了。
    /// </summary>
    private readonly TimeProvider _time;

    /// <summary>上游超时。20 秒，和旧版 <c>checkin.js:22</c> 一致。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public Checkin(
        HttpClient http,
        UpstreamVariant variant,
        ILogger<Checkin> log,
        TimeProvider? time = null)
    {
        _http = http;
        _variant = variant;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    // ── 对外 ──────────────────────────────────────────────────

    /// <summary>
    /// 查某个账号的签到状态。
    ///
    /// **查不到返回 null，不抛** —— 状态查不出来不该拦住别的账号
    /// （旧版 `fetchCheckinStatus` 就是 catch 成 null 的）。
    /// </summary>
    public async Task<CheckinStatus?> FetchStatusAsync(Credential credential, CancellationToken ct = default)
    {
        var response = await PostAsync(credential, StatusPath, ct).ConfigureAwait(false);
        return response.Ok ? response.Status2 : null;
    }

    /// <summary>
    /// 执行一次签到（**强制**，不看本地记录，也不先查状态）。
    ///
    /// 上游已经签过的话它照样回 code 0 —— 所以看返回里的
    /// <see cref="CheckinStatus.TodayCheckedIn"/> 判断是不是真拿到了积分。
    ///
    /// 想省一次往返、并且要幂等，用 <see cref="EnsureCheckedInAsync"/>。
    /// </summary>
    public async Task<CheckinResult> ClaimAsync(Credential credential, CancellationToken ct = default)
    {
        var response = await PostAsync(credential, ClaimPath, ct).ConfigureAwait(false);

        if (!response.Ok)
        {
            return new CheckinResult
            {
                Outcome = CheckinOutcome.Failed,
                Message = response.Message,
                ErrorKind = response.ErrorKind,
                Status = response.Status,
                Code = response.Code,
                RequestId = response.RequestId,
                UpstreamCalled = true,
            };
        }

        // 签成功但上游没回状态 —— 回查一次拿准确天数（旧版 ensureCheckedIn 的做法）。
        // 回查失败就用签到响应里带的，再不行就是 null。
        var status = response.Status2;
        if (status is null || !status.TodayCheckedIn)
        {
            var after = await FetchStatusAsync(credential, ct).ConfigureAwait(false);
            status = after ?? status;
        }

        if (_log.IsEnabled(LogLevel.Information))
        {
            LogClaimed(credential.AccountTag, status?.StreakDays ?? 0, status?.TodayCredit ?? 0);
        }

        return new CheckinResult
        {
            Outcome = CheckinOutcome.Claimed,
            Message = status is { StreakDays: > 0 }
                ? $"签到成功（连续 {status.StreakDays} 天）"
                : "签到成功",
            Status = response.Status,
            Code = response.Code,
            RequestId = response.RequestId,
            Status2 = status,
            UpstreamCalled = true,
        };
    }

    /// <summary>
    /// 「查一下，没签就签」—— **界面按钮和批量签到都该调这个**。
    ///
    /// ## 幂等的三层（见类注释）
    ///
    /// ## 调用次数取决于上游怎么回（实测过，不是猜的）
    ///
    /// ```
    /// 本地已签            0 次（短路，一个字节都不发）
    /// 没有进行中的活动      1 次（status）
    /// status 已说今天签过    1 次（status）
    /// 需要真签 + 上游回状态   2 次（status → daily-checkin）  ← 正常路径
    /// 需要真签 + 上游不回状态 3 次（status → daily-checkin → status 回查）
    /// ```
    ///
    /// 最后那一种就是旧版 `checkin.js:145` 的「回查一次拿准确天数」——
    /// **只有上游不把最新状态一起下发时才会发生**。别把「3 次」当成 bug。
    ///
    /// ⚠ 这个方法是**无状态**的：它不会写 <see cref="VaultEntry.LastCheckinAt"/>。
    /// 调用方要负责把 <see cref="CheckinResult.Outcome"/> 是 <c>Claimed</c>
    /// 时的 <see cref="NowIso"/> 存进保险库 —— 这是刻意的分工，
    /// Core 不该知道保险库文件在哪。
    ///
    /// @param knownLastCheckinAt 本地记着的上次签到时刻（ISO 8601 字符串，
    ///        就是 <see cref="VaultEntry.LastCheckinAt"/>）。是今天的话直接短路，
    ///        **一个字节都不发**。传 null 表示本地没有记录。
    ///
    /// 返回的 <see cref="CheckinResult.Message"/> 可以直接显示给用户。
    /// </summary>
    public async Task<CheckinResult> EnsureCheckedInAsync(
        Credential credential,
        string? knownLastCheckinAt = null,
        CancellationToken ct = default)
    {
        // ① 本地记录：今天已经签过 → 短路，不打上游
        if (IsToday(knownLastCheckinAt))
        {
            return new CheckinResult
            {
                Outcome = CheckinOutcome.AlreadyClaimed,
                Message = "今天已经签过了",
                UpstreamCalled = false,
            };
        }

        // ② 查上游状态：今天已签 / 没有活动 → 不执行签到
        var before = await FetchStatusAsync(credential, ct).ConfigureAwait(false);
        if (before is null)
        {
            return new CheckinResult
            {
                Outcome = CheckinOutcome.Failed,
                Message = "查不到签到状态（网络或令牌问题）",
                ErrorKind = UpstreamErrorKind.Unknown,
                Status = 0,
                UpstreamCalled = true,
            };
        }

        if (!before.Active)
        {
            return new CheckinResult
            {
                Outcome = CheckinOutcome.NoActivity,
                Message = "当前没有进行中的签到活动",
                ErrorKind = NoActivityKind,
                Status2 = before,
                UpstreamCalled = true,
            };
        }

        if (before.TodayCheckedIn)
        {
            return new CheckinResult
            {
                Outcome = CheckinOutcome.AlreadyClaimed,
                Message = before.StreakDays > 0
                    ? $"今天已经签过了（连续 {before.StreakDays} 天）"
                    : "今天已经签过了",
                Status2 = before,
                UpstreamCalled = true,
            };
        }

        // ③ 真签
        var claim = await ClaimAsync(credential, ct).ConfigureAwait(false);
        return claim with { UpstreamCalled = true };
    }

    /// <summary>
    /// 这个时刻是不是**今天**（UTC 日切）。
    ///
    /// ## 为什么按 UTC 而不是本地时区
    ///
    /// 旧版记录的是 `new Date().toISOString()`（**本身就是 UTC**）。按本地时区
    /// 比较的话，东八区用户在 08:00 之前签的那次会被算成「昨天」—— 于是同一天
    /// 又签一次。而签到的日切是**上游按它的时区**算的，我们这边只做粗过滤，
    /// 真正准确的判断在 ② （上游 status）。
    ///
    /// 所以：UTC 比较 = 不漏判（宁可多打一次上游 status，也不让幂等假阳性）。
    /// </summary>
    public bool IsToday(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return false;
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
        {
            return false;
        }
        return at.UtcDateTime.Date == _time.GetUtcNow().UtcDateTime.Date;
    }

    /// <summary>签到成功时刻的 ISO 8601 字符串 —— 直接存进 <see cref="VaultEntry.LastCheckinAt"/>。</summary>
    public string NowIso()
        => _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    // ── 内部 ──────────────────────────────────────────────────

    /// <summary>POST 一个签到端点并解信封。**不抛** —— 所有失败都编成结果。</summary>
    private async Task<PostOutcome> PostAsync(Credential credential, string path, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_variant.BillingBase}{path}");

            // ⚠ 复用已有的计费请求头，**不另起一套凭据机制**。
            // ApplyBilling 刻意不带 X-IDE-* 归属族 —— 签到属于计费链路。
            UpstreamHeaders.ApplyBilling(request, credential);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            // 超时用 CancellationTokenSource 而不是 HttpClient.Timeout：
            // 后者是**客户端级**的，改了会影响同一个 HttpClient 上的聊天请求。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方主动取消 —— 原样抛，这不是「签到失败」而是「不用签了」
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // 连接层失败：DNS / TLS / 超时 / 连接被重置。**不抛给调用方。**
            LogTransportFailure(ex, credential.AccountTag);
            return new PostOutcome
            {
                Ok = false,
                Status = 0,
                ErrorKind = UpstreamErrorKind.Server,
                Message = $"连不上上游：{ex.Message}",
            };
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return new PostOutcome
                {
                    Ok = false,
                    Status = status,
                    ErrorKind = UpstreamErrorKind.Server,
                    Message = $"读不到上游响应体：{ex.Message}",
                };
            }

            return ParseEnvelope(status, text);
        }
    }

    /// <summary>
    /// 解上游信封 `{code, msg, requestId, data}`。
    ///
    /// 判定规则照抄旧版 `checkin.js:79-83`：**HTTP 成功 + 业务 code 0 才算成功**。
    /// 两条都要看 —— 上游把很多语义塞在 200 里（业务 code），
    /// 只看状态码会把「活动已结束」当成签到成功。
    /// </summary>
    private PostOutcome ParseEnvelope(int status, string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // 不是 JSON（多半是网关配置错误时的 HTML 错误页）
            return new PostOutcome
            {
                Ok = false,
                Status = status,
                ErrorKind = UpstreamErrorKind.Server,
                Message = $"签到接口返回的不是 JSON（http {status}）：{Truncate(text)}",
            };
        }

        if (node is not JsonObject obj)
        {
            return new PostOutcome
            {
                Ok = false,
                Status = status,
                ErrorKind = status >= 400 ? Classify(status) : UnexpectedKind,
                Message = $"签到接口返回的形状认不出来（http {status}）：{Truncate(text)}",
            };
        }

        var code = ReadInt(obj, "code");
        // 上游两种都发过：新的是 msg，旧的是 message
        var message = ReadString(obj, "msg");
        if (message.Length == 0) message = ReadString(obj, "message");
        var requestId = ReadString(obj, "requestId");
        var data = obj["data"];

        var httpOk = status is >= 200 and < 300;
        var businessOk = code == 0;

        if (!httpOk || !businessOk)
        {
            // ⚠ **HTTP 状态码优先**，业务 code 只作兜底。
            //
            // 为什么必须这个顺序：上游在 HTTP 错误时回的 body 里也塞一个 `code`，
            // 而那个 `code` 未必是 HTTP 语义（实测旧版抓到过 401 的 body 里
            // `code` 是别的值）。反过来以 body 为准的话，「凭证失效」会被归成
            // `unknown`，界面就不知道该引导用户重新登录还是提示稍后重试 ——
            // 而这两件事对用户完全不同。
            //
            // 旧版 checkin.js 同样只看 HTTP 状态（`response.ok && code === 0`）。
            var kind = !httpOk
                ? Classify(status)
                : ClassifyBusiness(code);

            if (message.Length == 0) message = $"code {code}";
            if (message.Length == 0) message = $"http {status}";

            LogUpstreamRejected(status, code, message);

            return new PostOutcome
            {
                Ok = false,
                Status = status,
                Code = code,
                RequestId = requestId,
                ErrorKind = kind,
                // 上游的 msg 是给用户看的（「您的使用量已超出频率限制…」），
                // 直接透出比自己编一句准
                Message = message,
            };
        }

        return new PostOutcome
        {
            Ok = true,
            Status = status,
            Code = code,
            RequestId = requestId,
            Status2 = CheckinStatus.FromData(data),
        };
    }

    /// <summary>HTTP 状态码 → 错误分类。和 <c>UpstreamClient.Classify</c> 同一套语义。</summary>
    private static string Classify(int status) => status switch
    {
        401 or 403 => UpstreamErrorKind.Unauthorized,
        >= 500 => UpstreamErrorKind.Server,
        >= 400 => UpstreamErrorKind.BadRequest,
        _ => UpstreamErrorKind.Unknown,
    };

    /// <summary>
    /// 业务 code → 错误分类。
    ///
    /// 只看几个**有明确含义**的码，其余归 <see cref="UpstreamErrorKind.Unknown"/> ——
    /// 编不出来的分类比编错好。
    /// </summary>
    private static string ClassifyBusiness(int code) => code switch
    {
        401 or 403 => UpstreamErrorKind.Unauthorized,
        429 => UpstreamErrorKind.RateLimited,
        6004 => UpstreamErrorKind.ModelRateLimited,
        _ => UpstreamErrorKind.Unknown,
    };

    private static int ReadInt(JsonObject obj, string name)
    {
        var node = obj[name];
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        return 0;
    }

    private static string ReadString(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";

    private static string Truncate(string text)
        => text.Length <= 200 ? text : text[..200] + "…";

    /// <summary>「POST 一个签到端点」的中间结果 —— 比 <see cref="CheckinResult"/> 少一层语义。</summary>
    private sealed record PostOutcome
    {
        public required bool Ok { get; init; }
        public int Status { get; init; }
        public int? Code { get; init; }
        public string RequestId { get; init; } = "";
        public string ErrorKind { get; init; } = "";
        public string Message { get; init; } = "";
        public CheckinStatus? Status2 { get; init; }
    }

    // ── 日志 ──────────────────────────────────────────────────
    //
    // 用源生成的委托，不用 LogInformation(...) 那种带 params object[] 的重载
    // —— 后者每次调用都要装箱参数、分配数组（和 UpstreamClient 的理由一致）。

    [LoggerMessage(Level = LogLevel.Information, Message = "签到成功：{Account} 连续 {Streak} 天，今日 +{Credit}")]
    private partial void LogClaimed(string account, int streak, double credit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "签到被上游拒绝：http {Status} code {Code} —— {Message}")]
    private partial void LogUpstreamRejected(int status, int code, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "签到连不上上游（{Account}）")]
    private partial void LogTransportFailure(Exception ex, string account);
}
