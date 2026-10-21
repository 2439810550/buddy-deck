using BuddyDeck.Core;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.TestSupport;

/// <summary>
/// 「第 N 次请求返回什么」的脚本队列。
///
/// ## 为什么需要它
///
/// 上游的行为是**有状态**的：第一次被限流、换了号之后就好了。
/// 固定响应的假上游表达不了这件事 —— 只能写成「永远 429」或者「永远 200」，
/// 于是「换号重试」这条最重要的路径根本测不到。
///
/// ```
/// var fake = new FakeUpstreamHandler(_ => UpstreamResponses.Unknown());
/// fake.Script(
///     UpstreamResponses.TooManyRequests(),              // 第 1 次：账号级限流
///     UpstreamResponses.SseText(chunkJson, "[DONE]"));  // 第 2 次：好了
/// ```
///
/// ## 用完最后一步之后
///
/// **继续用最后一步**，不是抛异常。理由：绝大多数用例只关心前几步，
/// 把第 4、第 5 步也写出来是噪声。要断言「一共请求了 N 次」，
/// 直接读 <see cref="FakeUpstreamHandler.Count"/>。
///
/// ## 它和被测代码的关系
///
/// 它**不做轮询、不选号、不重试** —— 只负责「第几次请求回什么」。
/// 重试循环是产品代码的活（<see cref="SwappingChatLoop"/> 是测试侧的一个
/// 最小参考实现，用来演示这些夹具怎么串起来）。
/// </summary>
public sealed class UpstreamScript
{
    private readonly Func<CapturedRequest, HttpResponseMessage>[] _steps;

    /// <summary>@param steps 每一步的响应工厂，按请求次数取用。</summary>
    public UpstreamScript(params Func<CapturedRequest, HttpResponseMessage>[] steps)
    {
        if (steps.Length == 0) throw new ArgumentException("脚本至少要有一个步骤", nameof(steps));
        _steps = steps;
    }

    /// <summary>用现成的响应构造脚本。</summary>
    public static UpstreamScript Of(params HttpResponseMessage[] steps)
        => new(steps.Select<HttpResponseMessage, Func<CapturedRequest, HttpResponseMessage>>(
            r => _ => UpstreamResponses.Clone(r)).ToArray());

    /// <summary>脚本有多少步。</summary>
    public int Length => _steps.Length;

    /// <summary>
    /// 取第 N 次（从 0 开始）请求该用的响应工厂。
    /// 超出脚本长度时返回最后一步 —— 见类型注释里的理由。
    /// </summary>
    public Func<CapturedRequest, HttpResponseMessage> Next(int sequence)
        => _steps[Math.Min(sequence, _steps.Length - 1)];

    /// <summary>第 N 次请求会拿到哪一步（想要显式读出来断言时用）。</summary>
    public Func<CapturedRequest, HttpResponseMessage> this[int index] => Next(index);
}

/// <summary>一次聊天尝试的结果（测试视图）。</summary>
/// <param name="Ok">这一轮成功了吗。</param>
/// <param name="Status">上游 HTTP 状态（0 = 连接层失败）。</param>
/// <param name="Kind">错误分类（<see cref="UpstreamErrorKind"/>），成功时为 null。</param>
/// <param name="MemberId">这一轮用的是哪个池成员。</param>
/// <param name="Sse">成功时读到的整条 SSE 正文；失败时为 null。</param>
public sealed record ChatAttempt(bool Ok, int Status, string? Kind, string MemberId, string? Sse);

/// <summary>
/// 一个**最小可用**的「上游限流 → 换号 → 重试」循环。
///
/// ## 它为什么存在
///
/// 网关那条线本身不做重试（<c>GatewayApp</c> 只负责把失败映射成客户端状态码），
/// 但账号池（<see cref="AccountPool"/>）的冷却/选号语义必须被真正跑通一次 ——
/// 「第一次 429 → 换号 → 第二次 200」是 <see cref="FakeUpstreamHandler"/> +
/// <see cref="UpstreamScript"/> + <see cref="TestVault"/> 三件夹具串起来的
/// 验收场景。
///
/// 这个类就是那条串联路径的**测试侧参考实现**：不碰网络、不睡等、可重复。
/// 别人要测自己的调度器时，可以照这个形状写，也可以直接用它做对照。
///
/// ## 它做对了什么
///
/// · 从 <see cref="AccountPool.Pick"/> 选号，**带 exclude** —— 刚失败过的号不再选
/// · 限流按 <see cref="UpstreamErrorKind"/> 分类，**账号级 429 记冷却、模型级 6004 不换号**
/// · 每一轮都走真的 <see cref="UpstreamClient"/>，也就是真的发 HTTP（打到假上游）
/// </summary>
public sealed class SwappingChatLoop
{
    private readonly FakeUpstreamHandler _fake;
    private readonly AccountPool _pool;
    private readonly UpstreamVariant _variant;
    private readonly ILogger _log;
    private readonly Func<Credential, string> _bodyFactory;

    public SwappingChatLoop(
        FakeUpstreamHandler fake,
        AccountPool pool,
        UpstreamVariant variant,
        ILogger? log = null,
        Func<Credential, string>? bodyFactory = null)
    {
        _fake = fake;
        _pool = pool;
        _variant = variant;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _bodyFactory = bodyFactory ?? (credential => $$"""{"model":"hy3","stream":true,"messages":[{"role":"user","content":"ping"}],"tag":"{{credential.AccountTag}}"}""");
    }

    /// <summary>
    /// 跑到成功或者没号可用为止。
    ///
    /// @param model   要用的模型（冷却按模型记）。
    /// @param maxAttempts 最多几轮 —— 有上限才不会在「所有号都被限」时死循环。
    /// @param roundsPerMember 每个号最多试几轮，超了就把它排除掉、换下一个号。
    ///                        默认 1 = 「一个号被限了就换」。
    /// @param ct      取消令牌。
    /// @returns 每一轮的记录。最后一轮 <see cref="ChatAttempt.Ok"/> 为 true
    ///          表示换号成功。空列表 = 一开始就没有可用号。
    /// </summary>
    public async Task<IReadOnlyList<ChatAttempt>> RunAsync(
        string model = "hy3",
        int maxAttempts = 5,
        int roundsPerMember = 1,
        CancellationToken ct = default)
    {
        var attempts = new List<ChatAttempt>();
        var ledger = new Dictionary<string, int>(StringComparer.Ordinal);
        var exhausted = new List<string>();

        using var http = _fake.CreateClient();
        var client = new UpstreamClient(http, _variant, new TypedLogger<UpstreamClient>(_log));

        for (var round = 0; round < maxAttempts; round++)
        {
            var member = _pool.Pick(model, exhausted);
            if (member is null) break;                      // 没有可用号了 —— 交给调用方断言

            var credential = member.Entry.ToCredential();
            var result = await client
                .ChatStreamAsync(credential, _bodyFactory(credential), null, null, ct)
                .ConfigureAwait(false);

            if (result.Ok && result.Response is not null)
            {
                using var response = result.Response;
                var sse = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _pool.MarkSuccess(member.Id, model);
                attempts.Add(new ChatAttempt(true, result.Status, null, member.Id, sse));
                return attempts;
            }

            var kind = result.Kind ?? UpstreamErrorKind.Unknown;
            attempts.Add(new ChatAttempt(false, result.Status, kind, member.Id, null));

            if (kind is UpstreamErrorKind.RateLimited or UpstreamErrorKind.ModelRateLimited)
            {
                // 限流要记账 —— 下次选号就跳过这个号的这个模型。
                // 不分模型的话，一个模型被限就把整个号拉黑，白扔配额。
                _pool.MarkRateLimited(member.Id, model, null);
            }
            else
            {
                _pool.MarkFailure(member.Id, model);
            }

            ledger[member.Id] = ledger.TryGetValue(member.Id, out var used) ? used + 1 : 1;
            if (ledger[member.Id] >= roundsPerMember && !exhausted.Contains(member.Id))
            {
                exhausted.Add(member.Id);
            }
        }

        return attempts;
    }
}

/// <summary>
/// 把 <see cref="ILogger"/> 适配成 <c>ILogger&lt;T&gt;</c>。
///
/// 为什么要这个：<see cref="UpstreamClient"/> 要的是 <c>ILogger&lt;UpstreamClient&gt;</c>，
/// 而夹具里常常只有一个 <see cref="ILogger"/>（或者干脆没有）。
/// </summary>
internal sealed class TypedLogger<T> : ILogger<T>
{
    private readonly ILogger _inner;

    public TypedLogger(ILogger inner) => _inner = inner;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}
