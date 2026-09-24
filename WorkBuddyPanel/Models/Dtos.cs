using System.Text.Json.Serialization;

namespace WorkBuddyPanel.Models;

/// <summary>`node src/bin.js status --json` 的返回结构（缺字段时全部可空）。</summary>
public sealed class StatusDto
{
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("domain")] public string? Domain { get; set; }
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("accessTokenExpires")] public string? AccessTokenExpires { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("credits")] public double? Credits { get; set; }
    [JsonPropertyName("catalog")] public CatalogInfoDto? Catalog { get; set; }
    [JsonPropertyName("stateHome")] public string? StateHome { get; set; }
}

public sealed class CatalogInfoDto
{
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("fetchedAt")] public string? FetchedAt { get; set; }
    [JsonPropertyName("count")] public int? Count { get; set; }
}

/// <summary>`node src/bin.js models --json` 的返回结构。</summary>
public sealed class ModelsDto
{
    [JsonPropertyName("provider")] public string? Provider { get; set; }
    [JsonPropertyName("catalog")] public CatalogInfoDto? Catalog { get; set; }
    [JsonPropertyName("data")] public List<ModelDto>? Data { get; set; }
}

public sealed class ModelDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("contextWindow")] public long? ContextWindow { get; set; }
    [JsonPropertyName("maxTokens")] public long? MaxTokens { get; set; }
    [JsonPropertyName("supportsImages")] public bool? SupportsImages { get; set; }
    [JsonPropertyName("reasoning")] public bool? Reasoning { get; set; }
    [JsonPropertyName("supportedEfforts")] public List<string>? SupportedEfforts { get; set; }
    [JsonPropertyName("credits")] public string? Credits { get; set; }
    [JsonPropertyName("badges")] public List<string>? Badges { get; set; }
}

/// <summary>OpenAI 兼容的流式分片。</summary>
public sealed class StreamChunk
{
    [JsonPropertyName("choices")] public List<StreamChoice>? Choices { get; set; }
    [JsonPropertyName("error")] public StreamError? Error { get; set; }
}

public sealed class StreamChoice
{
    [JsonPropertyName("delta")] public StreamDelta? Delta { get; set; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

public sealed class StreamDelta
{
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }
}

public sealed class StreamError
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

/// <summary>错误响应的标准外壳。</summary>
public sealed class ErrorEnvelope
{
    [JsonPropertyName("error")] public StreamError? Error { get; set; }
}

/// <summary>`node src/bin.js accounts --json` 的返回结构。</summary>
public sealed class AccountListDto
{
    [JsonPropertyName("activeId")] public string? ActiveId { get; set; }
    [JsonPropertyName("vaultPath")] public string? VaultPath { get; set; }
    [JsonPropertyName("desktopAuthFile")] public string? DesktopAuthFile { get; set; }
    [JsonPropertyName("data")] public List<AccountDto>? Data { get; set; }
}

public sealed class AccountDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("uid")] public string? Uid { get; set; }
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
    [JsonPropertyName("domain")] public string? Domain { get; set; }
    [JsonPropertyName("enterpriseId")] public string? EnterpriseId { get; set; }
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("expiresAt")] public string? ExpiresAt { get; set; }
    [JsonPropertyName("refreshExpiresAt")] public string? RefreshExpiresAt { get; set; }
    [JsonPropertyName("refreshDaysLeft")] public int? RefreshDaysLeft { get; set; }
    [JsonPropertyName("addedAt")] public string? AddedAt { get; set; }
    [JsonPropertyName("lastCheckinAt")] public string? LastCheckinAt { get; set; }
    [JsonPropertyName("hasRefreshToken")] public bool HasRefreshToken { get; set; }
    [JsonPropertyName("credits")] public double? Credits { get; set; }
    [JsonPropertyName("creditsUnlimited")] public bool CreditsUnlimited { get; set; }
    [JsonPropertyName("checkin")] public CheckinDto? Checkin { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class CheckinDto
{
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("todayCheckedIn")] public bool TodayCheckedIn { get; set; }
    [JsonPropertyName("streakDays")] public int StreakDays { get; set; }
    [JsonPropertyName("dailyCredit")] public int DailyCredit { get; set; }
    [JsonPropertyName("totalCredits")] public int TotalCredits { get; set; }
    [JsonPropertyName("weekCheckinDays")] public int WeekCheckinDays { get; set; }
    [JsonPropertyName("weekProgress")] public List<bool>? WeekProgress { get; set; }
    [JsonPropertyName("activityName")] public string? ActivityName { get; set; }
    [JsonPropertyName("themeName")] public string? ThemeName { get; set; }
    [JsonPropertyName("season")] public int Season { get; set; }
    [JsonPropertyName("endTime")] public string? EndTime { get; set; }
}

/// <summary>`node src/bin.js checkin --json` 的返回结构。</summary>
public sealed class CheckinListDto
{
    [JsonPropertyName("data")] public List<CheckinResultDto>? Data { get; set; }
}

public sealed class CheckinResultDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("claimed")] public bool Claimed { get; set; }
    [JsonPropertyName("renewed")] public bool Renewed { get; set; }
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
}

/// <summary>`node src/bin.js accounts renew --json` 的返回结构。</summary>
public sealed class RenewListDto
{
    [JsonPropertyName("data")] public List<RenewResultDto>? Data { get; set; }
}

public sealed class RenewResultDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("renewed")] public bool Renewed { get; set; }
    [JsonPropertyName("daysLeft")] public int? DaysLeft { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
}

/// <summary>`node src/bin.js accounts import --json` 的返回结构。</summary>
public sealed class ImportListDto
{
    [JsonPropertyName("data")] public List<ImportResultDto>? Data { get; set; }
}

public sealed class ImportResultDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("uid")] public string? Uid { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("daysLeft")] public int? DaysLeft { get; set; }
    [JsonPropertyName("msg")] public string? Msg { get; set; }
}

/// <summary>`node src/bin.js ccswitch --json` 的返回结构。</summary>
public sealed class CcSwitchResultDto
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("written")] public List<string>? Written { get; set; }
    [JsonPropertyName("created")] public List<string>? Created { get; set; }
    [JsonPropertyName("adopted")] public List<CcSwitchAdoptedDto>? Adopted { get; set; }
    [JsonPropertyName("skipped")] public List<string>? Skipped { get; set; }
    [JsonPropertyName("backupPath")] public string? BackupPath { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class CcSwitchAdoptedDto
{
    [JsonPropertyName("appType")] public string? AppType { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

/// <summary>`ccswitch --status --json`。</summary>
public sealed class CcSwitchStatusDto
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("dbPath")] public string? DbPath { get; set; }
    [JsonPropertyName("entries")] public List<CcSwitchEntryDto>? Entries { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class CcSwitchEntryDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("appType")] public string? AppType { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("isCurrent")] public bool IsCurrent { get; set; }
    [JsonPropertyName("isOurs")] public bool IsOurs { get; set; }
}

/// <summary>`ccswitch --apply --json`。</summary>
public sealed class CcSwitchApplyDto
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("applied")] public List<string>? Applied { get; set; }
    [JsonPropertyName("skipped")] public List<string>? Skipped { get; set; }
    [JsonPropertyName("backups")] public List<string>? Backups { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}
