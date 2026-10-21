using System.Text.Json;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// 批量积分端点的反序列化 + 账号匹配回归测试。
///
/// 真实数据形状（网关 `/admin/credits/all` 返回）：
/// <code>
/// { "ok": true, "accounts": [
///   { "ok": true, "accountTag": "3e563042:", "accountId": "3e563042-d9da-...:",
///     "label": "主号", "totalCount": 16, "totalDosage": 2717, "packages": [] }
/// ]}
/// </code>
///
/// 用户实测反馈「总积分没了 + 只有前几个号有积分明细」——
/// 本测试用**真实形状**钉住：反序列化后 `Id`（accountTag）能正确
/// trim 掉冒号、与账号 uid 前缀匹配。
/// </summary>
public class CreditsAllMatchingTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>模拟网关 `/admin/credits/all` 返回的真实 JSON。</summary>
    private const string RealShape = """
    {"ok":true,"accounts":[
      {"ok":true,"accountTag":"3e563042:","accountId":"3e563042-d9da-4ce6-bbb8-bf52debdbff0:","label":"主号","totalCount":16,"totalDosage":2717,"packages":[]},
      {"ok":true,"accountTag":"a23db308:","accountId":"a23db308-0f40-4569-b98f-88b5905f33ca:","label":"简单快乐","totalCount":16,"totalDosage":2558,"packages":[]}
    ]}
    """;

    [Fact]
    public void AccountCredits_Id_从_accountTag_正确映射()
    {
        var cred = JsonSerializer.Deserialize<AccountCredits>(
            """{"accountTag":"3e563042:","totalDosage":2717}""", Options);

        Assert.NotNull(cred);
        Assert.Equal("3e563042:", cred!.Id);
    }

    [Fact]
    public void 批量积分_反序列化后_Id_能_tim_冒号并与_uid_前缀匹配()
    {
        // 模拟 AllCreditsDto 反序列化（HttpGatewayClient.GetAllCreditsAsync 内部做的事）
        var dto = JsonSerializer.Deserialize<AllCreditsDtoForTest>(RealShape, Options);
        Assert.NotNull(dto?.Accounts);
        Assert.Equal(2, dto!.Accounts!.Count);

        // 第一个账号的 accountTag 是 "3e563042:"，trim 冒号后是 "3e563042"，
        // 账号列表的 uid 是完整 uuid，StartsWith 应该命中。
        var tag = dto.Accounts[0].Id.TrimEnd(':');
        const string uid = "3e563042-d9da-4ce6-bbb8-bf52debdbff0";

        Assert.True(uid.StartsWith(tag, StringComparison.Ordinal));
    }

    /// <summary>与网关 UsageEndpoints/CreditsEndpoints 返回形状一致的 DTO。</summary>
    private sealed class AllCreditsDtoForTest
    {
        public List<AccountCredits>? Accounts { get; set; }
    }
}
