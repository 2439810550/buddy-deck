namespace BuddyDeck.Core;

/// <summary>
/// 上游服务的变体（国内版 / 国际版）。
///
/// 两个变体的协议完全一样，只是域名和配置文件不同。所以这里用数据描述差异，
/// 而不是写两套代码 —— 国际版目前没实测过（开发机只装了国内版），
/// 但把差异集中在这个文件里，将来验证时只需要改这里。
/// </summary>
public sealed record UpstreamVariant
{
    /// <summary>内部标识，用于配置和日志。</summary>
    public required string Id { get; init; }

    /// <summary>界面上显示的名字。</summary>
    public required string DisplayName { get; init; }

    /// <summary>聊天接口的基地址。</summary>
    public required string ChatBase { get; init; }

    /// <summary>计费/账号接口的基地址。</summary>
    public required string BillingBase { get; init; }

    /// <summary>模型目录的路径（相对 BillingBase）。</summary>
    public string CatalogPath { get; init; } = "/v3/config";

    /// <summary>桌面 App 登录态文件名。</summary>
    public required string DesktopFilename { get; init; }

    /// <summary>
    /// 桌面 App 的产品名 —— 注册表 DisplayName 的前缀（`WorkBuddy 5.8.3`）。
    ///
    /// 探测真实版本号时要用它去查注册表。写错了就查不到，会掉到兜底版本。
    /// </summary>
    public required string AppDisplayName { get; init; }

    /// <summary>保险库里存凭证的文件名。</summary>
    public required string VaultFilename { get; init; }

    /// <summary>国内版还是国际版。</summary>
    public bool IsInternational { get; init; }

    /// <summary>国内版。</summary>
    public static readonly UpstreamVariant China = new()
    {
        Id = "workbuddy",
        DisplayName = "WorkBuddy",
        ChatBase = "https://copilot.tencent.com",
        BillingBase = "https://www.codebuddy.cn",
        DesktopFilename = "workbuddy-desktop.info",
        AppDisplayName = "WorkBuddy",
        VaultFilename = "auth.cn.json",
    };

    /// <summary>国际版。⚠ 未实测。</summary>
    public static readonly UpstreamVariant Global = new()
    {
        Id = "workbuddy-ai",
        DisplayName = "WorkBuddy AI",
        ChatBase = "https://www.workbuddy.ai",
        BillingBase = "https://www.workbuddy.ai",
        DesktopFilename = "workbuddy-desktop-ai.info",
        AppDisplayName = "WorkBuddy AI",
        VaultFilename = "auth.ai.json",
        IsInternational = true,
    };

    public static readonly IReadOnlyList<UpstreamVariant> All = [China, Global];

    public static UpstreamVariant? Find(string id)
        => All.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
}
