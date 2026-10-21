using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// `IGatewayClient` 的「**永不抛**」契约测试。
///
/// ## 为什么必须覆盖全部 17 个操作
///
/// ui 写的 `--selftest` 只走 9 个操作（7 个查询 + 1 个流式 + 1 个 ViewModel 刷新链）。
/// 剩下的写操作（导入/改名/删除/续期/激活/池增删改/存设置）**没被覆盖** ——
/// 而它们恰恰是最可能在网关没起时抛异常的那批（写操作常有额外的校验分支）。
///
/// 更关键的是：`--selftest` 的**真实网关路径目前跑不出来**（UI 线程
/// sync-over-async 死锁，已单独报告）。所以这条契约必须有**不依赖进程启动**的
/// 自动化覆盖，否则它实际上处于无人看守状态。
///
/// ## 怎么造「网关没起」
///
/// 不用 `NotConnectedGatewayClient` —— 那测的是它自己。
/// 要测**真实的** `HttpGatewayClient` 在一个**必然连不上**的端口上的行为。
///
/// 端口用「先占住再释放」的方式拿到，保证此刻没有进程在监听：
/// 绑一个 TcpListener 到端口 0 让 OS 分配，拿到端口号后**立刻关掉**，
/// 于是那个端口在测试期间是空的。这样写比硬编码一个端口可靠 ——
/// 硬编码撞上真有服务在跑就会假绿。
/// </summary>
public class GatewayClientNeverThrowsTests
{
    /// <summary>拿一个当前确定没人在监听的端口。</summary>
    private static int GetDeadPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();          // 立刻释放 —— 此后这个端口是空的
        return port;
    }

    private static HttpGatewayClient CreateClient() => new(new HttpClient(), GetDeadPort());

    /// <summary>
    /// 断言一个异步操作「不抛，且返回带原因的失败」。
    /// </summary>
    private static async Task AssertFailsGracefullyAsync(string opName, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Assert.Fail($"{opName} 抛异常了，违反「永不抛」契约：{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ========================================================================
    //  17 个操作 × 网关未启动
    // ========================================================================

    [Fact]
    public async Task 网关没起时_全部查询操作都不抛()
    {
        var c = CreateClient();

        await AssertFailsGracefullyAsync("GetHealthAsync", async () =>
        {
            var r = await c.GetHealthAsync();
            Assert.False(r.Online);                  // 必须报告「不在线」
            Assert.False(string.IsNullOrWhiteSpace(r.Error));  // 必须给原因
        });

        await AssertFailsGracefullyAsync("GetAccountsAsync", async () =>
        {
            var r = await c.GetAccountsAsync();
            Assert.False(r.Ok);
            Assert.False(string.IsNullOrWhiteSpace(r.Error));
        });

        await AssertFailsGracefullyAsync("GetMetricsAsync", async () =>
        {
            var r = await c.GetMetricsAsync();
            Assert.False(r.Ok);
        });

        await AssertFailsGracefullyAsync("GetPoolsAsync", async () =>
        {
            var r = await c.GetPoolsAsync();
            Assert.False(r.Ok);
        });

        await AssertFailsGracefullyAsync("GetModelsAsync", async () =>
        {
            var r = await c.GetModelsAsync();
            Assert.False(r.Ok);
        });

        await AssertFailsGracefullyAsync("ReadLogAsync", async () =>
        {
            var r = await c.ReadLogAsync(100);
            Assert.False(r.Ok);
        });

        await AssertFailsGracefullyAsync("GetSettingsAsync", async () =>
        {
            var r = await c.GetSettingsAsync();
            Assert.False(r.Ok);
        });
    }

    [Fact]
    public async Task 网关没起时_全部写操作都不抛()
    {
        var c = CreateClient();

        await AssertFailsGracefullyAsync("ImportAccountsAsync", async () =>
        {
            var r = await c.ImportAccountsAsync("{}");
            Assert.False(r.Ok);
        });

        await AssertFailsGracefullyAsync("RenameAccountAsync", async () =>
        {
            var r = await c.RenameAccountAsync("some-id", "新名字");
            Assert.False(r.Ok);
        });

        await AssertFailsGracefullyAsync("RemoveAccountAsync", async () =>
        {
            var r = await c.RemoveAccountAsync("some-id");
            Assert.False(r.Ok);
        });

        await AssertFailsGracefullyAsync("RefreshTokenAsync", async () =>
        {
            var r = await c.RefreshTokenAsync("some-id");
            Assert.False(r.Ok);
        });

        await AssertFailsGracefullyAsync("ActivateAccountAsync", async () =>
        {
            var r = await c.ActivateAccountAsync("some-id");
            Assert.False(r.Ok);
        });

        await AssertFailsGracefullyAsync("SaveSettingsAsync", async () =>
        {
            var r = await c.SaveSettingsAsync(new GatewaySettings());
            Assert.False(r.Ok);
        });

        await AssertFailsGracefullyAsync("UpsertPoolAsync", async () =>
        {
            var r = await c.UpsertPoolAsync(new PoolSummary { Id = "p1" });
            Assert.False(r.Ok);
        });

        await AssertFailsGracefullyAsync("SetPoolRunningAsync", async () =>
        {
            var r = await c.SetPoolRunningAsync("p1", running: true);
            Assert.False(r.Ok);
        });

        await AssertFailsGracefullyAsync("RemovePoolAsync", async () =>
        {
            var r = await c.RemovePoolAsync("p1");
            Assert.False(r.Ok);
        });
    }

    /// <summary>
    /// 流式操作：网关没起时必须给一句人话，且**迭代本身**不能抛。
    ///
    /// 注意 `await foreach` 的异常会在**迭代时**抛出，而不是在拿到 IAsyncEnumerable 时 ——
    /// 所以必须真迭代一遍。
    /// </summary>
    [Fact]
    public async Task 网关没起时_流式聊天不抛且给人话()
    {
        var c = CreateClient();

        var chunks = new List<string>();

        try
        {
            await foreach (var chunk in c.ChatStreamAsync("some-model", "你好"))
            {
                chunks.Add(chunk);
            }
        }
        catch (Exception ex)
        {
            Assert.Fail($"ChatStreamAsync 迭代时抛异常，违反「永不抛」契约：{ex.GetType().Name}: {ex.Message}");
        }

        // 必须至少给一段文本 —— 静默什么都不吐会让界面看起来像卡住。
        Assert.NotEmpty(chunks);
        Assert.All(chunks, t => Assert.False(string.IsNullOrWhiteSpace(t)));
    }

    /// <summary>
    /// 边界：null / 空 / 超长参数也不能把「永不抛」打破。
    ///
    /// ⚠ **这一条当前是红的，而且是一个真实缺陷**（已单独报告给 captain，不在此处修 ——
    ///   `src/BuddyDeck.App/**` 不在本任务的 inScope 内）。
    ///
    /// 实测：
    /// <code>
    /// RenameAccountAsync(null, null)
    ///   → ArgumentNullException: Value cannot be null. (Parameter 'stringToEscape')
    /// </code>
    /// 抛出点在 `Uri.EscapeDataString(id)` —— 它在发请求**之前**对参数做转义，
    /// 空值没被拦下来，于是异常穿透出 `IGatewayClient`，违反它声明的「永不抛」契约。
    ///
    /// **影响面（说实话，不大）**：界面上的 id 来自列表选中项，正常操作不会是 null。
    /// 所以这不是一个用户会遇到的 bug。但它**确实是契约的破口** ——
    /// 而契约的价值就在于「无条件成立」；留一个已知的例外，下次有人写
    /// `RenameAccountAsync(vm.Selected?.Id, ...)`（Selected 为 null 时）
    /// 就会以一个莫名其妙的 `ArgumentNullException` 崩在 UI 线程上。
    ///
    /// **建议修法**（一行）：在 `RenameAccountAsync` 入口对入参做一次空判定，
    /// 直接返回 `GatewayResult<bool>.Failure("...")`，不要走到 `EscapeDataString`。
    /// 或者更省事：`Uri.EscapeDataString(id ?? string.Empty)`。
    ///
    /// **为了让这条测试反映真实状态而不是假装通过**，它用
    /// <see cref="FactAttribute"/> + 显式断言「当前会抛」的方式记录缺陷 ——
    /// 缺陷修好那天，这条测试会红，提醒把它改成「不抛」的断言。
    /// 用 Skip 会把它藏起来，那等于没有这条检查。
    /// </summary>
    [Fact]
    public async Task 已知缺陷_空id会让RenameAccountAsync抛异常()
    {
        var c = CreateClient();

        // 记录当前行为（这是缺陷，不是期望）。
        var threw = false;
        try
        {
            await c.RenameAccountAsync(null!, null!);
        }
        catch (ArgumentNullException)
        {
            threw = true;
        }

        Assert.True(
            threw,
            "RenameAccountAsync(null) 现在不抛了 —— 说明缺陷已被修复！"
            + "请把这条测试改成断言「返回失败结果且不抛」，并更新设计文档里的已知缺陷记录。");
    }

    /// <summary>
    /// 与上一条配套：**非 null 的**异常参数组合必须不抛。
    ///
    /// 把「空值缺陷」和「其它边界正常」分开，是为了让上面那条缺陷记录
    /// 不会把整组边界检查一起拖红 —— 其余边界是好的，这一点要说清楚。
    /// </summary>
    [Fact]
    public async Task 网关没起时_非空异常参数组合不抛()
    {
        var c = CreateClient();

        await AssertFailsGracefullyAsync("RemoveAccountAsync(空串)", async () =>
            await c.RemoveAccountAsync(string.Empty));

        await AssertFailsGracefullyAsync("ImportAccountsAsync(超长)", async () =>
            await c.ImportAccountsAsync(new string('x', 100_000)));

        await AssertFailsGracefullyAsync("ChatStreamAsync(空串)", async () =>
        {
            await foreach (var _ in c.ChatStreamAsync(string.Empty, string.Empty)) { /* 迭代掉 */ }
        });

        await AssertFailsGracefullyAsync("ReadLogAsync(负数)", async () =>
            await c.ReadLogAsync(-1));

        await AssertFailsGracefullyAsync("ReadLogAsync(超大)", async () =>
            await c.ReadLogAsync(int.MaxValue));

        await AssertFailsGracefullyAsync("RenameAccountAsync(超长标签)", async () =>
            await c.RenameAccountAsync("id", new string('标', 10_000)));
    }

    /// <summary>
    /// 失败结果里必须带**能指导下一步**的原因文本，不能是空串或含糊话。
    ///
    /// 这是设计计划 3.3 节的硬要求：错误要讲清「发生了什么 + 在哪 + 怎么修」。
    ///
    /// ⚠ 这里刻意**不**断言「文案里必须出现端口号」—— 实际的文案是
    ///   「网关没有启动 —— 到「设置」确认端口，或者先启动 BuddyDeck.Gateway。」，
    ///   它把「去哪找端口」说出来了，这**同样是可操作的**。
    ///   要求字面包含端口号是过度约束：端口是运行时才知道的值，
    ///   而「到设置里看」是稳定的指路。断言会太脆。
    /// </summary>
    [Fact]
    public async Task 连不上的原因文案要能指导下一步()
    {
        var c = CreateClient();
        var r = await c.GetAccountsAsync();

        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));

        // 必须说清「怎么了」—— 出现「没启动」这类具体诊断，
        // 而不是「失败」「错误」这种什么也没说的词。
        Assert.Contains("启动", r.Error, StringComparison.Ordinal);

        // 必须给出下一步动作（要么指路去哪看，要么直接叫人做什么）。
        var actionable =
            r.Error.Contains("设置", StringComparison.Ordinal)
            || r.Error.Contains("启动", StringComparison.Ordinal)
            || r.Error.Contains("检查", StringComparison.Ordinal);

        Assert.True(actionable, $"错误文案没给出下一步动作：「{r.Error}」");

        // 禁用词：含糊的说法（设计计划 3.3 节的明确禁令）。
        foreach (var vague in new[] { "未知错误", "操作失败", "出错了", "请稍后重试", "抱歉" })
        {
            Assert.DoesNotContain(vague, r.Error, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// `NotConnectedGatewayClient`（离线路）也必须守同样的契约 ——
    /// 它是截图与演示用的实现，同样不能被网关状态影响。
    /// </summary>
    [Fact]
    public async Task 离线实现同样永不抛()
    {
        var c = new NotConnectedGatewayClient(8787);

        var health = await c.GetHealthAsync();
        Assert.False(health.Online);

        Assert.False((await c.GetAccountsAsync()).Ok);
        Assert.False((await c.GetMetricsAsync()).Ok);
        Assert.False((await c.GetPoolsAsync()).Ok);
        Assert.False((await c.GetModelsAsync()).Ok);
        Assert.False((await c.ReadLogAsync(10)).Ok);
        Assert.False((await c.GetSettingsAsync()).Ok);
        Assert.False((await c.ImportAccountsAsync("{}")).Ok);
        Assert.False((await c.RenameAccountAsync("id", "n")).Ok);
        Assert.False((await c.RemoveAccountAsync("id")).Ok);
        Assert.False((await c.RefreshTokenAsync("id")).Ok);
        Assert.False((await c.ActivateAccountAsync("id")).Ok);
        Assert.False((await c.SaveSettingsAsync(new GatewaySettings())).Ok);
        Assert.False((await c.UpsertPoolAsync(new PoolSummary { Id = "p" })).Ok);
        Assert.False((await c.SetPoolRunningAsync("p", true)).Ok);
        Assert.False((await c.RemovePoolAsync("p")).Ok);

        var chunks = new List<string>();
        await foreach (var chunk in c.ChatStreamAsync("m", "p")) chunks.Add(chunk);
        Assert.NotEmpty(chunks);
    }

    /// <summary>
    /// 自证：`AssertFailsGracefullyAsync` 真的会抓住抛出的异常。
    ///
    /// 不证明这一点的话，「17 个操作都不抛」可能只是因为检查器从来不报错。
    /// </summary>
    [Fact]
    public async Task 自证_不抛检查器真的会抓到异常()
    {
        var threw = false;

        try
        {
            await AssertFailsGracefullyAsync("故意抛的测试操作", () =>
                throw new InvalidOperationException("这是故意的"));
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // Assert.Fail 抛的是 XunitException；其它类型说明检查器没兜住。
            Assert.Fail($"检查器没有把异常转成断言失败，而是漏出来了：{ex.GetType().Name}");
        }
        catch (Xunit.Sdk.XunitException)
        {
            threw = true;   // 期望走到这里：检查器正确判定为失败
        }

        // 这一步其实走不到（上面已经 catch 了），保留是为了语义清楚。
        Assert.True(threw, "检查器没有对抛异常的操作报失败 —— 说明它是个空壳");
    }
}
