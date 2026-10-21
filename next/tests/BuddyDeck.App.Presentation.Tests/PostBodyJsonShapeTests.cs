using System.Text.Json;
using System.Text.Json.Nodes;
using BuddyDeck.App.Services;

namespace BuddyDeck.App.Presentation.Tests;

/// <summary>
/// POST 请求体的 JSON 键名回归测试。
///
/// ## 这组防的是什么（真实案例：保存按钮点了没反应）
///
/// <see cref="HttpGatewayClient"/> 用 <see cref="JsonContent.Create{T}"/>
/// 把 record 序列化成 POST body。它的 JsonSerializerOptions 只有
/// `PropertyNameCaseInsensitive = true` —— **那只影响反序列化（读）**，
/// 不影响序列化（写）。序列化默认用**属性名原样**（PascalCase）。
///
/// 于是：
/// <code>
/// 客户端 POST 出去   { "Port": 8788, "ApiKey": "...", "DisplayName": "..." }
/// 网关读            body["port"] / body["apiKey"] / body["displayName"]
///                   → 全是 null（PascalCase 键名 vs camelCase 键名）
///                   → 返回 200 但**什么都没改**
/// </code>
///
/// 用户看到的：点「保存」→ 提示「已保存」→ 重启后设置还是旧的。
/// **编译不报、运行不报、还回 200** —— 是最阴的静默失败。
///
/// 所以这组测试钉住的是**序列化方向**：把 record 序列化后，
/// 断言 JSON 里的键名是网关实际会读的 camelCase。
/// （BooleanJsonBindingTests 钉的是反向 —— 反序列化方向。）
/// </summary>
public class PostBodyJsonShapeTests
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static JsonObject RoundTrip(object body)
    {
        var json = JsonSerializer.Serialize(body, WriteOptions);
        return JsonNode.Parse(json)!.AsObject();
    }

    // ══════════════════════════════════════════════════════════
    //  GatewaySettings → POST /admin/settings
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 设置_序列化键名是网关读的_camelCase()
    {
        var settings = new GatewaySettings
        {
            Port = 8799,
            ApiKey = "sk-test",
            VariantId = "workbuddy",
            VaultPath = "C:\\vault.json",
            LogPath = "C:\\log",
        };

        var obj = RoundTrip(settings);

        // ⚠ 网关读的是这些 camelCase 键（见 AdminEndpoints.HandleSettingsAsync）
        Assert.Equal(8799, obj["port"]?.GetValue<int>());
        Assert.Equal("sk-test", obj["apiKey"]?.GetValue<string>());
        Assert.Equal("workbuddy", obj["variantId"]?.GetValue<string>());
        Assert.Equal("C:\\vault.json", obj["vaultPath"]?.GetValue<string>());
        Assert.Equal("C:\\log", obj["logPath"]?.GetValue<string>());

        // ⚠ 反证：PascalCase 键必须**不存在**（网关不读它们）
        Assert.Null(obj["Port"]);
        Assert.Null(obj["ApiKey"]);
    }

    // ══════════════════════════════════════════════════════════
    //  PoolSummary → POST /admin/pools
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void 池_序列化键名是网关读的_camelCase()
    {
        var pool = new PoolSummary
        {
            Id = "pool-8799",
            DisplayName = "志强池",
            Port = 8799,
            ApiKey = "sk-pool",
        };

        var obj = RoundTrip(pool);

        // ⚠ 网关读的是这些 camelCase 键（见 AdminEndpoints.UpsertPoolAsync）
        Assert.Equal("pool-8799", obj["id"]?.GetValue<string>());
        Assert.Equal("志强池", obj["displayName"]?.GetValue<string>());
        Assert.Equal(8799, obj["port"]?.GetValue<int>());
        Assert.Equal("sk-pool", obj["apiKey"]?.GetValue<string>());

        // ⚠ 反证：PascalCase 键必须**不存在**
        Assert.Null(obj["DisplayName"]);
        Assert.Null(obj["Port"]);
        Assert.Null(obj["ApiKey"]);
    }
}
