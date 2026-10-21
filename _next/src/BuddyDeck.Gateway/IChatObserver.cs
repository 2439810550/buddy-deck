using System.Text.Json.Nodes;
using BuddyDeck.Core;

namespace BuddyDeck.Gateway;

/// <summary>
/// 一次聊天请求的终局事件。
///
/// 这里**只放元数据位**，不放请求体/响应正文 —— 脱敏是硬要求，
/// 见 HANDOVER 第九节的用户偏好。
/// </summary>
public sealed class ChatObservation
{
    public required string RequestedModel { get; init; }

    /// <summary>实际发出去的模型（换过模型时和请求的不一样）。</summary>
    public required string UsedModel { get; init; }

    /// <summary>账本标识（`uid前8位:enterpriseId`）。**不是完整 uid**。</summary>
    public required string AccountTag { get; init; }

    /// <summary>池成员 id —— 会话粘性/调度统计要用。</summary>
    public required string MemberId { get; init; }

    /// <summary>最终 outcome：ok / failed / cancelled。</summary>
    public required string Outcome { get; init; }

    /// <summary>客户端上游失败分类（<see cref="UpstreamErrorKind"/>）。成功时为 null。</summary>
    public string? Kind { get; init; }

    /// <summary>上游 HTTP 状态。0 表示连接层失败。</summary>
    public int UpstreamStatus { get; init; }

    /// <summary>
    /// 这次客户端请求内部已经发生的上游尝试次数（含本次）。
    ///
    /// ⚠ t16 起它承载**真实值**（之前恒为默认值 1，等于这个字段没用上）。
    /// 观察者（粘性/指标/日志）靠它判断「这个请求是不是靠换号重试才成功的」
    /// —— 那正是账号池是否在恶化的信号，只看 <see cref="Outcome"/>
    /// 是看不出来的（成功的请求也可能试了 3 次）。
    /// </summary>
    public int Attempts { get; init; } = 1;

    /// <summary>耗时（毫秒）。</summary>
    public long ElapsedMs { get; init; }

    /// <summary>上游的 usage（含上游私有字段，脱敏与否由观察者决定）。</summary>
    public JsonNode? Usage { get; init; }

    /// <summary>是不是流式请求。</summary>
    public bool Streamed { get; init; }
}

/// <summary>
/// 聊天请求观察者。
///
/// ## 这是给 observability 留的挂载点
///
/// P1 会话粘性 / P2 请求指标 + 脱敏日志 **都不在这里实现** ——
/// 那是 observability 那条线的活。网关只负责在**终局点**把事件推出去，
/// 并且保证：
///
/// · 事件里**没有**提示词、没有响应正文、没有 token 原值（只有 AccountTag）
/// · 观察者抛异常**绝不影响**已经发给用户的响应（见 <see cref="ChatObservers"/>）
///
/// ## 怎么接上去
///
/// ```
/// // Program.cs，DI 里注册任意多个实现即可：
/// builder.Services.AddSingleton<IChatObserver, MyStickySessionObserver>();
/// builder.Services.AddSingleton<IChatObserver, MyMetricsObserver>();
/// ```
///
/// 之前不存在任何实现时走 <see cref="NullChatObserver"/> 的等价路径（空列表）。
/// </summary>
public interface IChatObserver
{
    /// <summary>一次聊天请求走到终局时调用一次。实现必须是线程安全且非阻塞的。</summary>
    void OnChatCompleted(ChatObservation observation);
}

/// <summary>什么都不做的观察者。注册它只为了让 DI 在没有任何实现时也能解析出实例。</summary>
public sealed class NullChatObserver : IChatObserver
{
    public void OnChatCompleted(ChatObservation observation)
    {
        // 故意为空：网关本身不实现粘性/指标/日志
    }
}

/// <summary>
/// 观察者的分发器。
///
/// **吞掉观察者的异常** —— 指标/账本挂掉不能让用户的请求失败。
/// 但也不能静默：打得进日志（由调用方 ILogger 记）。
/// </summary>
public sealed partial class ChatObservers
{
    private readonly IReadOnlyList<IChatObserver> _observers;
    private readonly Microsoft.Extensions.Logging.ILogger<ChatObservers> _log;

    public ChatObservers(
        IEnumerable<IChatObserver> observers,
        Microsoft.Extensions.Logging.ILogger<ChatObservers> log)
    {
        _observers = observers as IReadOnlyList<IChatObserver> ?? observers.ToArray();
        _log = log;
    }

    public bool HasObservers => _observers.Count > 0;

    public void Notify(ChatObservation observation)
    {
        foreach (var observer in _observers)
        {
            try
            {
                observer.OnChatCompleted(observation);
            }
            catch (Exception ex)
            {
                // 观察者是旁路 —— 它挂了不能影响用户已经拿到的响应
                LogObserverFailed(ex, observer.GetType().Name);
            }
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "观察者 {Observer} 抛异常，已忽略（不影响请求结果）")]
    private partial void LogObserverFailed(Exception ex, string observer);
}

/// <summary>
/// 账号池的凭据来源。
///
/// 每次请求都读一遍保险库（而不是启动时读一次缓存）—— 用户在面板上
/// 导入新账号、切号、删号之后，网关要立刻跟上，不能要求重启。
/// </summary>
public interface ICredentialSource
{
    /// <summary>当前可用的账号；没有可用账号时返回空列表。</summary>
    IReadOnlyList<VaultEntry> Load();
}

/// <summary>从保险库文件读账号。</summary>
public sealed partial class VaultCredentialSource : ICredentialSource
{
    private readonly Func<string> _path;
    private readonly Microsoft.Extensions.Logging.ILogger<VaultCredentialSource> _log;

    public VaultCredentialSource(
        Func<string> path,
        Microsoft.Extensions.Logging.ILogger<VaultCredentialSource> log)
    {
        _path = path;
        _log = log;
    }

    public IReadOnlyList<VaultEntry> Load()
    {
        try
        {
            var path = _path();
            var doc = Vault.Read(path);
            return doc?.Accounts ?? [];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // 保险库读不了：报空池，让请求得到「没有可用账号」而不是 500
            LogVaultUnreadable(ex, _path());
            return [];
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Error,
        Message = "保险库读取失败：{Path}")]
    private partial void LogVaultUnreadable(Exception ex, string path);
}
