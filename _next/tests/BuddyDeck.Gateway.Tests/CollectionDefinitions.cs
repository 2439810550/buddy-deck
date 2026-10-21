using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.TestSupport;
using Xunit;

namespace BuddyDeck.Gateway.Tests;

/// <summary>
/// **测试集合隔离**的定义 + 一个会互踩的复现用例。
///
/// ## ⚠ 这个文件为什么必须存在于**本程序集**
///
/// xUnit 的 `[CollectionDefinition]` **不跨程序集生效**。
///
/// 本程序集里 6 个测试类标了 `[Collection("GatewayHost")]`，但那个集合的
/// **定义**原先只写在 `tests/BuddyDeck.TestSupport.Tests/GatewayHostTests.cs` 里 ——
/// **另一个程序集**。于是这里的标记**完全失效**，xUnit 把每个类当成
/// 独立的匿名集合，**并行开跑**。
///
/// 定义在本程序集里补一份，标记才真正开始起作用。
///
/// （同一个坑在两个程序集里都存在：见同级的 `tests/BuddyDeck.E2E.Tests/CollectionDefinitions.cs`。）
///
/// ## 为什么并行会互踩（实测到的通道）
///
/// 每个用例起的是**自己的** `GatewayHost`（自己的 `AccountPool`、自己的假上游），
/// 所以**不共享池状态**。互踩发生在**机器级资源**上：
///
/// ```
/// · 每个宿主都真的绑一个端口、真的起 Kestrel —— 并发时同时有 5~8 个宿主
/// · 每个宿主都真的发 HTTP —— 抢线程池与 socket
/// · 于是「起宿主 + 探活 + 断言耗时/顺序」这类用例在负载下**时快时慢**，
///   表现成**同样代码、失败数每次不同**
/// ```
///
/// gateway 本轮就被它咬过：一次跑出「失败 3」、一次「失败 84」，
/// 一度以为自己改坏了东西 —— **失败数抖动本身就是这个缺陷的症状**。
/// </summary>
[CollectionDefinition("GatewayHost", DisableParallelization = true)]
public class GatewayHostCollection
{
}

/// <summary>
/// 会与并行用例互踩的复现用例。
///
/// ## 它复现的是什么
///
/// 用**同一批物理资源**（端口 + 线程池）反复起宿主，并且**对耗时敏感** ——
/// 并行跑时其它用例同时在起 Kestrel、发 HTTP，这个用例的「起→探活→收摊」
/// 周期会被拉长/抢占，断言随之抖动。
///
/// ## ⚠ 这个用例的写法规避了什么（免得它自己变成 flaky 源）
///
/// · **不睡等**：只做「起宿主 → 探活 → 释放」的确定性操作
/// · **不断言绝对耗时**：绝对耗时本身就依赖机器负载，钉不住
/// · **断言的是「同一批操作的失败数/结果一致」**：这是隔离要保证的**不变量**
///
/// 换句话说：单跑它**永远绿**；只有和其它用例并行时才可能露馅 ——
/// 这正是隔离缺陷的形态。
/// </summary>
[Collection("GatewayHost")]
public class SharedResourceContentionTests
{
    /// <summary>
    /// 反复起宿主并打一批请求 —— 模拟「同时有多个宿主在抢端口/线程池」。
    ///
    /// 每轮都做**完全一样**的事，并断言每轮结果完全一样。
    /// 并行负载下宿主启动会被拖慢，`/healthz` 探活窗口变紧 → 抖动。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task 反复起宿主时每轮结果必须一致(int round)
    {
        _ = round;

        // 这一批操作用同一个宿主做多次请求：并行时别的用例在抢端口与线程池，
        // 结果应当**逐轮一致**（隔离成立时成立，失效时抖动）。
        await using var host = await GatewayHost.StartReadyAsync(
            vault: new TestVault().AddMany(2));

        var results = new List<int>();

        for (var i = 0; i < 3; i++)
        {
            using var response = await host.Client.GetAsync("/v1/models");
            results.Add((int)response.StatusCode);
            await response.Content.ReadAsStringAsync();
        }

        // ★ 三次必须完全一样 —— 隔离失效时这里会抖（比如某次探活/请求超时）
        Assert.All(results, code => Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)code));
        Assert.Single(results.Distinct());
    }

    /// <summary>
    /// 多个宿主**同时**存活时，各自必须都能正常服务。
    ///
    /// 这是最接近真实互踩形态的用例：同时开 4 个真 Kestrel，
    /// 每个都探活 + 打请求。隔离失效时，并行集合会再叠加更多宿主，
    /// 端口/socket/线程池压力上升，某些宿主的探活窗口会被挤掉。
    /// </summary>
    [Fact]
    public async Task 同时存活多个宿主时每个都能正常服务()
    {
        var hosts = new List<GatewayHost>();

        try
        {
            // 先把 4 个宿主都起起来（同时存活）
            for (var i = 0; i < 4; i++)
            {
                hosts.Add(await GatewayHost.StartReadyAsync(vault: new TestVault().Add()));
            }

            // 端口必须互不冲突（端口 0 由系统分配）
            var ports = hosts.Select(h => h.Port).ToArray();
            Assert.Equal(ports.Length, ports.Distinct().Count());

            // 每个都还能正常服务
            foreach (var host in hosts)
            {
                using var response = await host.Client.GetAsync("/healthz");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                Assert.True(body["ok"]!.GetValue<bool>());
            }
        }
        finally
        {
            foreach (var host in hosts) await host.DisposeAsync();
        }
    }
}
