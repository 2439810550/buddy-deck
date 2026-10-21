using BuddyDeck.App.Controls;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.Converters;

/// <summary>
/// 状态 → 外观的映射。
///
/// ## 为什么是「token 名」而不是 Brush
///
/// 这个类不依赖 WPF（可测），所以它返回的是**资源 key 的名字**（字符串），
/// 由 <see cref="AccountStateBrushConverter"/> 拿去 ResourceDictionary 里查。
/// 这样：
///   · 映射表本身能用普通 xUnit 测
///   · 换配色只动 Colors.xaml，不动代码
///
/// ## 状态色永远说真话
///
/// 一张状态 → 颜色的表里最容易出的错是「把两种处境映成同一个颜色」。
/// BuddyDeck 里最要区分的两对：
///
///   Expired（号废了，要你动手）  vs  Degraded（上游的问题，等一等）
///   → Stop 暗红                    → Fault 锈橙
///
///   Degraded（上游拒绝）         vs  冷却（正常调度，池子自己会换号）
///   → Fault 锈橙                   → Cool 蓝紫（**不是红**）
///
/// 第二对尤其重要：冷却是家常便饭，给它红色会让用户以为天天出事故。
///
/// 详见 docs/ui-design-plan.md 第 1.2 节。
/// </summary>
public static class StatusAppearance
{
    /// <summary>资源 key：账号状态徽章的文字色。</summary>
    public const string OkKey = "OkBrush";
    public const string WarnKey = "WarnBrush";
    public const string StopKey = "StopBrush";
    public const string FaultKey = "FaultBrush";
    public const string IdleKey = "IdleBrush";
    public const string CoolKey = "CoolBrush";

    /// <summary>
    /// 账号状态 → 文字/图标色 key。
    ///
    /// 注意 <see cref="AccountState"/> 的顺序有意义（越靠上越要人管），
    /// 但这里**不按顺序**映射 —— 映射按语义，不是按枚举序号。
    /// </summary>
    public static string BrushKey(AccountState state) => state switch
    {
        AccountState.Healthy => OkKey,
        AccountState.ExpiringSoon => WarnKey,   // 琥珀：该看一眼，但还能用
        AccountState.Expired => StopKey,        // 暗红：号废了
        AccountState.Degraded => FaultKey,      // 锈橙：上游的问题
        _ => IdleKey,                           // Unknown：中性，不报警
    };

    /// <summary>
    /// 账号状态 → 左侧竖条色 key。
    ///
    /// 和 <see cref="BrushKey"/> 分开，是因为竖条压过不透明度（0.85），
    /// 用的是另一组 key。看起来可以合并，但合并之后「想单独调竖条亮度」
    /// 就要动代码；分开的话只动 Colors.xaml。
    /// </summary>
    public static string RailKey(AccountState state) => state switch
    {
        AccountState.Healthy => "OkRailBrush",
        AccountState.ExpiringSoon => "WarnRailBrush",
        AccountState.Expired => "StopRailBrush",
        AccountState.Degraded => "FaultRailBrush",
        _ => "IdleRailBrush",
    };

    /// <summary>账号状态 → 徽章底色 key（状态色压到 0.16 的薄底）。</summary>
    public static string BadgeBackgroundKey(AccountState state) => state switch
    {
        AccountState.Healthy => "OkBadgeBackgroundBrush",
        AccountState.ExpiringSoon => "WarnBadgeBackgroundBrush",
        AccountState.Expired => "StopBadgeBackgroundBrush",
        AccountState.Degraded => "FaultBadgeBackgroundBrush",
        _ => "IdleBadgeBackgroundBrush",
    };

    /// <summary>
    /// 账号状态 → 徽章里的词。
    ///
    /// 转发给 ui 已经定好的 <see cref="AccountPresentation.StateLabel"/>，
    /// **不在这里重新写一套** —— 同一个状态在两个地方叫不同名字，
    /// 是界面开始失控的第一个信号。
    /// </summary>
    public static string Label(AccountState state) => AccountPresentation.StateLabel(state);

    /// <summary>用量条状态 → 条的填充色 key。</summary>
    public static string QuotaBrushKey(QuotaState state) => state switch
    {
        QuotaState.Normal => OkKey,       // 充裕 → 绿
        QuotaState.Low => CoolKey,        // 开始消耗 → 蓝紫（不是黄，还不需要提醒）
        QuotaState.Warning => WarnKey,    // 快用尽 → 琥珀
        QuotaState.Critical => StopKey,   // 临界 → 暗红
        QuotaState.Cooldown => CoolKey,   // 冷却 → 蓝紫（正常调度，不是故障）
        _ => IdleKey,
    };

    /// <summary>用量条状态 → 条尾那个说明文字的色 key。</summary>
    public static string QuotaTextKey(QuotaState state) => state switch
    {
        QuotaState.Normal => "TextSecondaryBrush",
        QuotaState.Low => "TextSecondaryBrush",
        QuotaState.Warning => WarnKey,
        QuotaState.Critical => StopKey,
        QuotaState.Cooldown => CoolKey,
        _ => "TextTertiaryBrush",
    };

    /// <summary>网关在线状态 → 状态点的色 key。不在线时用中性灰，不用红。</summary>
    public static string GatewayDotKey(bool online) => online ? OkKey : IdleKey;
}
