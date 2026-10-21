using BuddyDeck.Core;

namespace BuddyDeck.Gateway;

/// <summary>
/// 当前请求的池上下文（AsyncLocal）—— 单网关多池路由的核心。
///
/// ## 为什么用 AsyncLocal 而不是 HttpContext.Items
///
/// <c>HttpContext.Items</c> 需要把 context 一路传进每个方法，
/// 但 GatewayApp 里 <c>_pool</c> 被 18 处引用，其中不少在**没有 context 参数**
/// 的辅助方法里（选号、建候选模型等）。AsyncLocal 随异步流传播，
/// 任何方法都能读「当前请求属于哪个池」，不用改方法签名。
/// </summary>
internal static class PoolContext
{
    /// <summary>当前请求的池实例。空 = 默认池（单池用户无感）。</summary>
    private static readonly System.Threading.AsyncLocal<PoolInstance?> Current = new();

    /// <summary>在请求开始时设置当前池。</summary>
    public static void Set(PoolInstance? pool) => Current.Value = pool;

    /// <summary>读当前池。空 → 默认池（由调用方传入兜底）。</summary>
    public static PoolInstance? Pool => Current.Value;
}

/// <summary>一个池的运行时实例（账号池 + 凭证源 + 启用状态）。</summary>
public sealed class PoolInstance
{
    public PoolInstance(string poolId, string displayName, string apiKey, string vaultPath,
        AccountPool pool, ICredentialSource credentials)
    {
        PoolId = poolId;
        DisplayName = displayName;
        ApiKey = apiKey;
        VaultPath = vaultPath;
        Pool = pool;
        Credentials = credentials;
    }

    public string PoolId { get; }
    public string DisplayName { get; }
    public string ApiKey { get; }
    public string VaultPath { get; }
    public AccountPool Pool { get; }
    public ICredentialSource Credentials { get; }

    /// <summary>路由级开关：false = 这个池停用，请求不再路由到它。</summary>
    public bool Running { get; set; } = true;

    public bool IsDefault => PoolId == "default";
}