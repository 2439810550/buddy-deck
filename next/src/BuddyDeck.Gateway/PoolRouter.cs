using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 多池路由 —— 一个网关进程内按 API key 路由到不同的账号池（new_api 的渠道模型）。
///
/// ## 为什么需要（用户需求的架构升级）
///
/// 旧架构「一个网关进程 = 一个池（一个保险库 + 一个 activeId）」，多个池要起多个进程。
/// new_api 的效果是**一个服务内多渠道**：请求带不同的 key，路由到不同的上游账号组。
///
/// 这里把「池」定义为：一组账号（独立保险库文件）+ 一个 API key。
/// 网关启动时扫描所有池（默认池 + pools/ 下的子池目录），为每个池维护独立的
/// <see cref="AccountPool"/>（冷却/限流/在途状态互不干扰）。
///
/// ## 路由规则
///
/// · 请求带 Authorization: Bearer &lt;key&gt; → 匹配到对应池
/// · 无 key 或 key 不匹配 → 默认池（保持单池用户无感）
/// </summary>
public sealed class PoolRouter
{
    private readonly Dictionary<string, PoolInstance> _byKey = new(StringComparer.Ordinal);
    private readonly List<PoolInstance> _all = [];
    private readonly PoolInstance _default;

    public PoolRouter(IReadOnlyList<PoolInstance> pools, ILogger<PoolRouter> log)
    {
        _all.AddRange(pools);
        _default = pools.FirstOrDefault(p => string.IsNullOrEmpty(p.ApiKey)) ?? pools[0];

        foreach (var pool in pools)
        {
            if (!string.IsNullOrEmpty(pool.ApiKey))
            {
                _byKey[pool.ApiKey] = pool;
            }
        }
    }

    /// <summary>所有池。</summary>
    public IReadOnlyList<PoolInstance> All => _all;

    /// <summary>默认池（无 key / key 不匹配时落到这里）。</summary>
    public PoolInstance Default => _default;

    /// <summary>按 API key 解析池。无 key 或匹配不上 → 默认池。停用的池跳过。</summary>
    public PoolInstance Resolve(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return _default;
        if (_byKey.TryGetValue(apiKey, out var pool) && pool.Running) return pool;
        return _default;
    }

    /// <summary>移除一个子池（默认池不能删）。返回是否成功。</summary>
    public bool Remove(string poolId)
    {
        var instance = _all.FirstOrDefault(p => string.Equals(p.PoolId, poolId, StringComparison.OrdinalIgnoreCase));
        if (instance is null || instance.IsDefault) return false;

        _all.Remove(instance);
        if (!string.IsNullOrEmpty(instance.ApiKey)) _byKey.Remove(instance.ApiKey);
        return true;
    }
}
