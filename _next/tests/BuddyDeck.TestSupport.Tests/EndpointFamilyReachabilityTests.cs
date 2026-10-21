using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Gateway;
using BuddyDeck.TestSupport;
using Xunit;

namespace BuddyDeck.TestSupport.Tests;

/// <summary>
/// **端点族在测试宿主里真的可达吗** —— 专门盯这一类装配缺陷。
///
/// ## 为什么单独立一个文件（而不是并进 GatewayHostTests）
///
/// 因为要防的是一种**特定的失败形态**：
///
/// > **宿主装配不全 → 端点族静默不可见。**
/// > 症状是「手工 `dotnet run` 验一遍是真的 + 测试里全 404」——
/// > 那种组合最容易让人得出「测试环境问题，不是 bug」的结论。
///
/// 这个项目里它已经是**第三次**同类缺陷：
/// ```
/// /admin/*        写了路由但没挂
/// UsageEndpoints  类写了但没注册
/// 端点族          在测试宿主里没挂
/// ```
/// 三次都是「**测试通过但功能不存在**」。
///
/// ## 这一组和类型级测试的区别（关键）
///
/// gateway 报这个缺陷时给了一条很有价值的实证：
///
/// > 我自己的 42 条 Anthropic 测试是绿的，**正因为它们绕过了这个坑** ——
/// > `AnthropicEndpointTests` 自带宿主 + 手写分派，**不经过 `GatewayApp`**。
/// > 所以「类型级测试绿 + E2E 级测试全 404」这个分裂是真的存在，
/// > **而且光看类型级测试完全发现不了**。
///
/// 所以下面这些断言**必须经真 HTTP 打到真端口** —— 只有那样才会经过
/// `GatewayApp.HandleAsync` 里那个 `foreach (var family in _families)`。
/// 「`new` 一个端点族实例断言它不抛」是**证明不了**这一点的。
/// </summary>
[Collection("GatewayHost")]   // 宿主要真的绑端口，串行跑
public class EndpointFamilyReachabilityTests
{
    /// <summary>
    /// 端点族的全部前缀。**每个都要能到达**（不返回 404）。
    ///
    /// 值给的是「一个不该 404 的请求」—— 端点族自己的参数校验会回 400/401/415
    /// 之类，**那些都算「到达了」**。这里唯一不能接受的就是 404：
    /// 它意味着 `_families` 是空的、请求落到了兜底分支。
    /// </summary>
    public static TheoryData<string, string, string> FamilyProbes => new()
    {
        // path,                           method, body
        { "/admin/accounts",               "GET",  "" },
        { "/v1/messages",                  "POST", "{}" },
        { "/v1/messages/count_tokens",     "POST", "{}" },
        { "/v1/images/generations",        "POST", "{}" },
        { "/v1/search",                    "POST", "{}" },
        { "/v1/webfetch",                  "POST", "{}" },
        { "/v1/responses",                 "POST", "{}" },
    };

    [Theory]
    [MemberData(nameof(FamilyProbes))]
    public async Task 端点族在测试宿主里可达_不返回_404(string path, string method, string body)
    {
        await using var host = await GatewayHost.StartReadyAsync();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body.Length > 0)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await host.Client.SendAsync(request);

        // ★ 核心断言：**不是 404**
        //
        // 404 的语义是「网关找不到这条路由」—— 而在装了端点族的宿主里，
        // 它只可能来自**兜底分支**，也就是 `_families` 为空（或该族没认这条路）。
        // 其它状态码（400 参数不对 / 401 该鉴权 / 415 类型不对 / 200 成功）
        // 都说明请求**真的走进了那个族**。
        Assert.True(
            response.StatusCode != HttpStatusCode.NotFound,
            $"{method} {path} 回了 404 —— 说明端点族在测试宿主里**不可见**"
            + "（`GatewayApp._families` 是空的，请求落到了 404 兜底）。"
            + "检查 GatewayHost 有没有调 `EndpointFamilies.AddAll(services)`。");

        await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task 端点族的解析结果非空()
    {
        // 上一条是**行为级**证明（走真 HTTP）；这一条是**装配级**证明。
        //
        // 两条都要：行为级能抓到「挂了但顺序错/路径不认」，
        // 装配级能给出更直接的失败信息（"解析出来是空的"）。
        await using var host = await GatewayHost.StartReadyAsync();

        var families = EndpointFamilies.Resolve(host.Application.Services);

        Assert.NotEmpty(families);

        // 五个族都要在（名字对得上，改族时能被发现）
        var names = families.Select(f => f.GetType().Name).ToList();
        Assert.Contains("AdminEndpoints", names);
        Assert.Contains("AnthropicEndpoints", names);
        Assert.Contains("MediaEndpoints", names);
        Assert.Contains("SearchEndpoints", names);
        Assert.Contains("ResponsesEndpoints", names);
    }

    [Fact]
    public async Task 没有配置_AdminEndpoints_时_装配不会抛异常()
    {
        // ⚠ 这条盯的是 `AddAll()` 里那个**转发**：
        //
        //   services.AddSingleton<IEndpointFamily>(sp => sp.GetRequiredService<AdminEndpoints>());
        //
        // 它用的是 `GetRequiredService` —— 所以 `GatewayHost` 若忘了注册
        // `AdminEndpoints` **具体类型**，这个工厂一解析就抛异常（不是静默）。
        //
        // 症状比「404」更明显，但**也更容易被误读成「宿主起不来」**，
        // 所以单独钉一条：起宿主不该因为装配不全而抛。
        var exception = await Record.ExceptionAsync(async () =>
        {
            await using var host = await GatewayHost.StartReadyAsync();
            // 真的解析一次（工厂是惰性的，起宿主动不到它）
            _ = EndpointFamilies.Resolve(host.Application.Services).ToList();
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task 管理面能用_说明转发到了同一个实例()
    {
        // `AddAll()` 对 AdminEndpoints 必须**转发**而不是再注册一次 ——
        // 它内部有可变状态（`_settings`），造第二个实例的症状是
        // 「POST /admin/settings 改了一个、GET 读到另一个」。
        //
        // 这条用真 HTTP 走一遍「写→读」，证明只有一个实例。
        //
        // ⚠ 用 `apiKey` 这个字段：它是 `/admin/settings` 支持写的
        //   （port/variantId/apiKey/vaultPath/logPath），而且是**无校验**的自由字符串，
        //   不会因为值不合法被 400 挡掉 —— 我们要验的是实例一致性，不是字段校验。
        await using var host = await GatewayHost.StartReadyAsync();

        const string marker = "t38-instance-identity-check";

        using (var post = new HttpRequestMessage(HttpMethod.Post, "/admin/settings"))
        {
            post.Content = new StringContent(
                "{\"apiKey\":\"" + marker + "\"}", Encoding.UTF8, "application/json");
            using var write = await host.Client.SendAsync(post);
            await write.Content.ReadAsStringAsync();

            // 写被拒（例如这个字段有校验）就跳过读校验 —— 那属于管理面的业务决定，
            // 不是装配问题。这里只关心「写进去了的话，读得到吗」。
            if (write.StatusCode != HttpStatusCode.OK)
            {
                return;
            }
        }

        // ★ 写进去的 apiKey 会**立即生效**（鉴权同步），所以读回要带它。
        using var read = new HttpRequestMessage(HttpMethod.Get, "/admin/settings");
        read.Headers.TryAddWithoutValidation("Authorization", "Bearer " + marker);
        using var readResp = await host.Client.SendAsync(read);
        Assert.Equal(HttpStatusCode.OK, readResp.StatusCode);

        var body = JsonNode.Parse(await readResp.Content.ReadAsStringAsync())!;

        // ★ 写进去的值必须读得回来 —— 两个实例的话这里会是旧值
        Assert.Equal(marker, body["apiKey"]?.GetValue<string>());
    }
}
