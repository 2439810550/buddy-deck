namespace BuddyDeck.Core;

/// <summary>
/// 一个账号的「在途租约」—— 用 `using` 保证归还，漏不掉。
///
/// ## 为什么要有这个类型，而不是让调用方自己 `pool.Release(id)`
///
/// 在途计数**漏减一次**的后果极其隐蔽：
///
/// ```
/// 那个号被永久标记为「在用」→ Pick 永远跳过它
/// → 池子的可用容量慢慢缩到零
/// → 但**没有任何地方会报错**，只是越来越慢、越来越容易 429
/// ```
///
/// 靠「记得在三条路径都减回去」是**不可靠**的：正常结束、上游失败、
/// 客户端取消 —— 尤其**取消**那条，异常直接从 `await` 冒泡出去，
/// 只要归还写在 `finally` 之外就必然漏。
///
/// 所以这里让「归还」变成一个**资源**：拿到租约就 `using`，
/// 作用域怎么退出都会还（含异常、含 `return`、含取消）。
/// 这是**结构上的保证**，不是自觉。
///
/// ## 用法
///
/// ```csharp
/// var lease = pool.Acquire(model);        // 内部已 Pick 并 +1 在途
/// if (lease is null) { /* 达上限 → 换模型 / 回 429 */ }
/// using (lease)                            // ← 一行搞定归还
/// {
///     await upstream.ChatStreamAsync(...);
/// }                                        // ← 这里（或任何异常路径）自动归还
/// ```
///
/// ## 它和 `PoolMember` 的关系
///
/// 租约**持有**那个成员（调用方要拿它的 `Entry.ToCredential()` 去发请求），
/// 同时负责在 `Dispose` 时把该成员的在途计数减回去。
///
/// `Dispose` 是**幂等**的 —— 重复 `Dispose` 只减一次。测试里显式还过一次、
/// 又被 `using` 兜一次时，不会把计数减成负数。
/// </summary>
public sealed class InFlightLease : IDisposable
{
    private readonly AccountPool _pool;
    private readonly PoolMember _member;
    private readonly string _model;
    private int _disposed;

    internal InFlightLease(AccountPool pool, PoolMember member, string model)
    {
        _pool = pool;
        _member = member;
        _model = model;
    }

    /// <summary>被租用的那个号。发请求就用它的凭据。</summary>
    public PoolMember Member => _member;

    /// <summary>它被租用时的模型（归还时按同一维度记账，虽然目前只减计数）。</summary>
    public string Model => _model;

    /// <summary>便捷：直接取凭据。</summary>
    public Credential Credential => _member.Entry.ToCredential();

    public void Dispose()
    {
        // ⚠ 必须幂等：Interlocked 保证「只减一次」，
        // 哪怕调用方自己的 finally 已经还过、using 又兜了一次。
        // 不幂等的话计数会被减成负数 —— 那会让上限形同虚设（负数永远不超限）。
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _pool.Release(_member.Id, _model);
        }
    }
}

/// <summary>
/// 在途上限策略 —— **达上限时怎么办**。
///
/// ## 为什么必须显式选一个，而不是「允许超额」
///
/// ui 的实测（`docs/concurrency-verification-howto.md`）证明了一条数学事实：
///
/// > **「并发请求不压到同一个号」在「号数 &lt; 并发数」时是不可达的** ——
/// > 请求比号多，总得有号承担两个。
///
/// 所以正确的目标不是「消灭复用」，而是**给单号叠加设一个上限，
/// 并说清超限时怎么办**。没有策略的话，这个上限只是一个
/// 「没人知道该设多少」的旋钮。
/// </summary>
public enum InFlightPolicy
{
    /// <summary>
    /// 达上限就不给号（`Acquire` 返回 null）。
    ///
    /// **这是默认值**，理由：
    ///   · 用户原话「大不了不从一个账号请求」—— 他接受「换个号/换个模型」，
    ///     而不是「排队等」；
    ///   · 它与网关已有的换号重试循环（`GatewayApp.RunWithRetryAsync`）**天然衔接**：
    ///     拿到 null 就换个模型/换个号再试，全试光就回 429。整条链路不需要新概念。
    ///   · 排队等待要引入超时、防死锁、公平性 —— 收益不明确而复杂度实打实。
    /// </summary>
    Reject = 0,

    /// <summary>
    /// 达上限就复用（允许超额）。
    ///
    /// 提供这个选项是为了**可回退**：万一某个部署里「宁可挤也不能拒」，
    /// 改配置即可，不必改代码。但**它不是默认** —— 允许超额等于没有上限。
    /// </summary>
    AllowExcess = 1,
}

/// <summary>在途上限的配置。</summary>
public sealed record InFlightOptions
{
    /// <summary>
    /// 单个号最多同时承担几个在途请求。默认 <b>2</b>。
    ///
    /// ## 为什么是 2 而不是 1
    ///
    /// 上限 1 意味着「一个号一次只能服务一个请求」—— 那在
    /// 号数 ≥ 并发数时确实最干净（零重叠），但它有个副作用：
    /// **号数少于并发数时会把大量请求挡掉**（2 个号 / 6 并发 → 只能放行 2 个，
    /// 其余 4 个直接失败/换模型）。而 ui 实测显示当前自然重叠就是 2~3，
    /// 说明「2」是这套上游比较舒服的档位。
    ///
    /// ## 为什么不是 3 或更高
    ///
    /// ui 实测：2 个号 / 6 并发时**峰值并发 3** —— 那正是团队自己踩过的
    /// `soft_rate`（三个成员同时被限流）的形态。把上限设在 2，
    /// 就是**把「已经出事过的那个值」挡在外面**。
    ///
    /// 上限可配（<see cref="AccountPool.Configure"/>），不写死。
    /// </summary>
    public int MaxPerAccount { get; init; } = 2;

    /// <summary>达上限时的策略。默认 <see cref="InFlightPolicy.Reject"/>。</summary>
    public InFlightPolicy Policy { get; init; } = InFlightPolicy.Reject;

    /// <summary>默认配置。</summary>
    public static readonly InFlightOptions Default = new();
}
