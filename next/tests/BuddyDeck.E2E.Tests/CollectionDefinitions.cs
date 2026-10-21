using Xunit;

namespace BuddyDeck.E2E.Tests;

/// <summary>
/// **测试集合隔离**的定义 —— 必须存在于**本程序集**。
///
/// ## ⚠ 这个文件解决的是什么
///
/// 本程序集里 5 个测试类标了 `[Collection("GatewayHost")]`：
///
/// ```
/// AnthropicE2ETests / ChatE2ETests / HealthAndAuthE2ETests /
/// PoolAndRetryE2ETests / RequestBoundaryE2ETests
/// ```
///
/// **但那个集合的「定义」原先只写在另一个程序集里**
/// （`tests/BuddyDeck.TestSupport.Tests/GatewayHostTests.cs`）。
///
/// **xUnit 的 `[CollectionDefinition]` 不跨程序集生效** —— 所以这里所有的
/// `[Collection("GatewayHost")]` 标记**完全失效**：xUnit 找不到名为
/// `GatewayHost` 的集合定义，就把每个类当成**独立的匿名集合**，
/// **并行开跑**。
///
/// 定义在本程序集里补一份，那些标记才真正开始起作用。
///
/// ## 为什么这个失效是「静默」的（最危险的地方）
///
/// `[Collection("SomeUndefinedName")]` **不报错、不警告**。
/// 编译器不管、xUnit 也不抛 —— 它只是**找不到定义**，于是退化成
/// 「每个类一个匿名集合」= 全并行。
///
/// ⇒ 症状是**一批无关测试同时红**，而且**失败数每次不同**
/// （并行互踩的典型特征），而不是一条清晰的错误。
///
/// ## 为什么并行会踩（实测到的通道）
///
/// 每个用例起的是**自己的** `GatewayHost`（自己的池、自己的假上游），
/// 所以**不共享对象状态**。互踩在**机器级资源**上：
///
/// ```
/// · 每个宿主都真的绑端口、真的起 Kestrel —— 并发时同时有 5~8 个宿主
/// · 每个宿主都真的发 HTTP —— 抢线程池与 socket
/// · 「起宿主 → 探活(/healthz 轮询) → 断言」这条链在负载下会被拖长，
///   于是对时序敏感的用例随机红
/// ```
///
/// **实测数字**（Gateway.Tests，393 条）：
/// ```
/// 并行（标记失效）：失败 103
/// 串行（本定义生效）：失败   1   ← 连跑 3 次完全一致
/// ```
///
/// ## 与 TestSupport.Tests 那份的关系
///
/// 三份定义（TestSupport.Tests / Gateway.Tests / E2E.Tests）**内容相同、
/// 必须各自存在** —— 这不是重复代码，是 xUnit 的机制要求。
/// 改这里的时候，另外两份要一起改。
/// </summary>
[CollectionDefinition("GatewayHost", DisableParallelization = true)]
public class GatewayHostCollection
{
}
