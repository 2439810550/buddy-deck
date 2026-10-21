using Microsoft.AspNetCore.Http;

namespace BuddyDeck.Gateway;

/// <summary>
/// 一个「端点族」—— 自己判断能不能处理这个请求，能就处理掉。
///
/// ## 为什么要有这个接口
///
/// `GatewayApp.HandleAsync` 原来是**一串手写的 if**：
///
/// ```csharp
/// if (GET  /v1/models)            { … return; }
/// if (GET  /metrics)              { … return; }
/// if (POST /v1/chat/completions)  { … return; }
/// if (_admin.TryHandleAsync(…))   { return; }
/// // 加一条协议线 = 再插一个 if + 再动构造函数
/// ```
///
/// 后果是**所有人共用一个单点文件**：Anthropic 翻译层、Responses 翻译层、
/// 媒体端点、admin 扩展 —— 每条线都要改同一个方法，**谁都没法并行**。
/// 队长排任务时为此**连续撞了三次路径冲突**，每次都得改一次契约。
///
/// 有了这个接口：**新增一条协议线 = 新建一个文件 + 一行 DI 注册**，
/// 不再动 `GatewayApp`。
///
/// ## 接口为什么只有一个方法
///
/// 刻意最小：调用方需要知道的只有「你能不能处理这个请求」。
/// **生命周期、优先级、元数据这些「以后可能用得上」的东西一律不放进来** ——
/// 那些是猜出来的需求，加进来就要维护、要测试、要解释。
/// 真需要时再加，那时它有真实用途支撑。
///
/// ## ⚠ 顺序是一个真实的正确性属性，不是风格问题
///
/// 端点族**按注册顺序依次询问**，第一个说「我能处理」的赢。
/// 所以：
///
/// · **前缀重叠的族，必须把更具体的排在前面**。现成的例子：
///   `AnthropicEndpoints` 同时管 `/v1/messages` 与 `/v1/messages/count_tokens`
///   —— 它内部先比精确路径再比前缀，所以自己就能区分；
///   但如果将来有人再写一个只认 `/v1/messages` 前缀的族并**排在它前面**，
///   `count_tokens` 就会被那个族先吃掉。
/// · 顺序**写在 <see cref="EndpointFamilies"/> 的注册注释里**（显式、可读），
///   不靠 DI 的隐式注册顺序 —— 后者在有人重排 `Program.cs` 时会静默失效。
/// </summary>
public interface IEndpointFamily
{
    /// <summary>
    /// 处理这个请求吗？
    ///
    /// @returns true = **我处理了**（响应已写完，或至少在写），调用方不要再动它。
    ///          false = 不是我的请求，**一个字节都没写**，调用方继续问下一个族。
    ///
    /// ⚠ 返回 false 时**绝不能已经写过响应头** —— 那会让后面的族报
    /// "headers already sent"。判断逻辑必须只读 `Request`，不碰 `Response`。
    /// </summary>
    Task<bool> TryHandleAsync(HttpContext context);
}

/// <summary>
/// 端点族的**显式有序**注册表。
///
/// ## 为什么不直接用 DI 的注册顺序
///
/// `IEnumerable&lt;IEndpointFamily&gt;` 的顺序 = DI 注册顺序，而注册顺序散在
/// `Program.cs` 里、可能被任何一次重排打乱，**而且没有任何地方写明「谁该在谁前面」**。
/// 顺序一旦错了，症状是「某个端点被另一个族静默吃掉」—— 很难查。
///
/// 所以这里把顺序变成一个**显式的列表**，并附上理由。
/// </summary>
public static class EndpointFamilies
{
    /// <summary>
    /// 把一个族登记进服务容器。
    ///
    /// 用法（在 <see cref="AddAll"/> 里）：
    /// ```csharp
    /// EndpointFamilies.Register<AdminEndpoints>(services);   // 先
    /// EndpointFamilies.Register<AnthropicEndpoints>(services); // 后
    /// ```
    /// 注册顺序 = 询问顺序。**在调用处写明为什么是这个顺序。**
    ///
    /// ⚠ 一般情况下**不要直接调它** —— 用 <see cref="AddAll"/>，
    /// 那是「顺序」的唯一真源（理由见那里的注释）。
    /// </summary>
    public static IServiceCollection Register<TFamily>(IServiceCollection services)
        where TFamily : class, IEndpointFamily
        => services.AddSingleton<IEndpointFamily, TFamily>();

    /// <summary>
    /// **注册全部端点族** —— 顺序的唯一真源。
    ///
    /// ## 为什么必须有这个方法（而不是让调用方各自 Register 一遍）
    ///
    /// 生产装配（`Program.cs`）和测试装配（`tests/BuddyDeck.TestSupport/GatewayHost.cs`）
    /// 都要把端点族挂上。**如果两边各写一份注册清单，它们必然会漂移** ——
    /// 加一族时改了一边忘另一边，症状是「真进程正常、测试里 404」。
    ///
    /// **这个坑真发生了**（e2e 报的）：`GatewayHost` 一开始没注册任何族，
    /// 于是 `GatewayApp._families` 是空的 —— 端点族在**所有测试宿主里完全不可见**，
    /// 20/20 全 404，而真进程（`dotnet run`）一切正常。
    ///
    /// 那种「手工验一遍是真的 + 测试里全红」的组合最容易让人得出
    /// 「测试环境问题，不是 bug」的结论 —— **而那正是要防的**。
    ///
    /// ## 顺序为什么这样排（改动时看这里，不要另起一份）
    ///
    /// ```
    /// ① AdminEndpoints      /admin/*        —— 前缀独立，与谁都不重叠
    /// ② AnthropicEndpoints  /v1/messages    —— 内部先比精确路径再比前缀，
    ///                                         所以自己就分得清 count_tokens
    /// ③ MediaEndpoints      /v1/images/* /v1/video/* —— 独立前缀
    /// ④ SearchEndpoints     /v1/search /v1/webfetch  —— 独立前缀
    /// ⑤ ResponsesEndpoints  /v1/responses   —— **只认精确路径**（用 Equals 而非
    ///                                         StartsWith），所以放最后也安全
    /// ```
    ///
    /// ⚠ **规则：认前缀的族，必须排在「认更具体路径」的族之后。**
    /// 反过来的话，宽前缀会把窄路径先吃掉（症状是「某个端点悄无声息地失效」）。
    /// 目前五族前缀互不包含，顺序还不构成约束；**加第六族时先看这一条**。
    ///
    /// ⚠ 尤其：将来若有人加一个「认 `/v1/` 前缀」的族，**必须排在最后** ——
    ///   否则它会一次吃掉 messages / images / video / search / responses。
    /// </summary>
    public static IServiceCollection AddAll(IServiceCollection services)
    {
        // AdminEndpoints 的构造参数带 **可变状态**（`_settings`），
        // 所以它在 `Program.cs` 里已经被 `AddSingleton(sp => new ...)` 注册过一次。
        //
        // ⚠ 这里必须**转发到那个实例**，不能再 `AddSingleton<IEndpointFamily, AdminEndpoints>`
        //   —— 那会造出第二个实例，症状是「POST /admin/settings 改了一个、
        //   GET 读到另一个」（设置看起来改了但读不回来）。
        services.AddSingleton<IEndpointFamily>(sp => sp.GetRequiredService<AdminEndpoints>());

        // UsageEndpoints 同样有依赖（UsageLedger），已在 Program.cs 用工厂注册过 ——
        // 理由与 AdminEndpoints 一样：转发到同一实例，不要造第二个。
        services.AddSingleton<IEndpointFamily>(sp =>
            new UsageFamily(sp.GetRequiredService<UsageEndpoints>()));

        Register<AnthropicEndpoints>(services);
        Register<MediaEndpoints>(services);
        Register<SearchEndpoints>(services);
        Register<ResponsesEndpoints>(services);

        // ★ 任务库端点（后台任务：拉积分/签到/续期）——
        //   构造函数带可选参数（Checkin），DI 不会为可选参数注入，
        //   所以用转发写法复用 Program.cs 里显式工厂注册的实例。
        services.AddSingleton<IEndpointFamily>(sp => sp.GetRequiredService<TaskEndpoints>());

        // ⚠ **`CreditsEndpoints` 必须在这里注册** —— 否则 `/admin/credits` 回 404。
        //
        // 实测踩过（用户反馈「余量那一块你再研究一下」）：t70 交付了
        // `CreditsEndpoints.cs`、它的测试也全绿，**但它从没进过 `AddAll`**——
        // 于是真网关上 `GET /admin/credits` 是 404，
        // 界面拿到 404 就静默显示空（积分那一块一直是空的）。
        //
        // ⇒ 这正是 t42 那条规则的又一例：**族写好了不等于被挂上了**。
        //   它的单测自己把族挂进宿主 → 测不到"生产装配漏了它"。
        //
        // ⚠ 顺序：放在 Admin 之后 —— `/admin/credits` 与 Admin 的路由不重叠
        //   （Admin 的分发按 area 走，没有 "credits" 分支，会落到 404）。
        Register<CreditsEndpoints>(services);
        return services;
    }

    /// <summary>
    /// 把 <see cref="UsageEndpoints"/> 适配成 <see cref="IEndpointFamily"/>。
    ///
    /// ## 为什么要适配器而不是给那个类加接口
    ///
    /// `UsageEndpoints` 是 t24（observability）写的，它**没实现** `IEndpointFamily`
    /// （那时接口还不存在）。而给它加接口要动**别人的文件**。
    ///
    /// `IEndpointFamily` 只有一个方法，**一行适配器**比改别人的类更干净：
    /// 不动它的任何代码、不影响它的既有测试、也不需要协调文件所有权。
    /// </summary>
    private sealed class UsageFamily(UsageEndpoints inner) : IEndpointFamily
    {
        public Task<bool> TryHandleAsync(HttpContext context) => inner.TryHandleAsync(context);
    }

    /// <summary>
    /// 把端点族按注册顺序取出来。
    ///
    /// 拿不到任何族时返回空列表（不是 null）—— 调用方可以直接 foreach，
    /// 不必先判空。**空列表的语义是「没有端点族」，网关照样能跑**
    /// （它还有自己那几条内置路由）。
    /// </summary>
    public static IReadOnlyList<IEndpointFamily> Resolve(IServiceProvider provider)
        => provider.GetService<IEnumerable<IEndpointFamily>>()?.ToArray() ?? [];
}
