using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BuddyDeckHub;

/// <summary>
/// 认证：把 Authorization: Bearer bd-xxx 认成「哪个成员的哪把令牌」。
///
/// ## 为什么要缓存
///
/// PBKDF2 迭代 21 万次，单次校验约 100ms。每次请求都全表扫一遍哈希，
/// 在高频调用下会变成瓶颈。所以：
///
///   令牌的明文前缀 → 先快速筛出候选（前缀是明文存储的）
///   候选命中后 → 校验哈希，结果连同令牌原文一起进内存缓存（5 分钟）
///
/// 缓存键用「令牌原文的 SHA256」，不存明文 —— 内存转储也捞不到 key。
/// </summary>
public sealed class AuthService
{
    private readonly HubStore _store;
    private readonly ILogger<AuthService> _log;

    private readonly record struct CacheEntry(string MemberId, string TokenId, DateTimeOffset Expiry);
    private readonly Dictionary<string, CacheEntry> _cache = new(256);
    private readonly object _gate = new();

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public AuthService(HubStore store, ILogger<AuthService> log)
    {
        _store = store;
        _log = log;
    }

    public sealed record AuthResult(Member Member, ApiToken Token, Group Group);

    public enum Fail
    {
        None,
        MissingKey,
        InvalidKey,
        Disabled,
        Expired,
        IpNotAllowed,
    }

    /// <summary>校验一把令牌。返回 null 时看 <paramref name="fail"/> 知道原因。</summary>
    public AuthResult? Authenticate(string? presented, IPAddress? clientIp, out Fail fail)
    {
        fail = Fail.None;

        if (string.IsNullOrWhiteSpace(presented))
        {
            fail = Fail.MissingKey;
            return null;
        }

        var presentedToken = presented.Trim();
        var cacheKey = CacheKeyOf(presentedToken);

        // 快路：缓存命中
        CacheEntry entry;
        lock (_gate)
        {
            if (_cache.TryGetValue(cacheKey, out entry))
            {
                if (entry.Expiry > DateTimeOffset.Now)
                    return ResolveCached(entry, clientIp, out fail);
                _cache.Remove(cacheKey);
            }
        }

        // 慢路：按前缀筛候选，逐个验哈希
        var prefix = TokenHasher.PrefixOf(presentedToken);
        var candidates = _store.Config.Members
            .Where(m => m.Tokens.Any(t => t.Prefix == prefix))
            .ToList();

        // 前缀可能撞（前 11 位相同），也可能令牌是手改过的、前缀对不上。
        // 前者按前缀筛，后者退化成全表扫。两种情况都能正确处理。
        if (candidates.Count == 0)
            candidates = _store.Config.Members.ToList();

        foreach (var member in candidates)
        {
            foreach (var token in member.Tokens)
            {
                if (!TokenHasher.Verify(presentedToken, token.Hash)) continue;

                lock (_gate)
                {
                    _cache[cacheKey] = new CacheEntry(member.Id, token.Id, DateTimeOffset.Now.Add(CacheTtl));
                }
                return Validate(member, token, clientIp, out fail);
            }
        }

        fail = Fail.InvalidKey;
        _log.LogWarning("无效令牌，前缀 {Prefix}", prefix);
        return null;
    }

    private AuthResult? ResolveCached(CacheEntry entry, IPAddress? ip, out Fail fail)
    {
        var member = _store.Config.Members.FirstOrDefault(m => m.Id == entry.MemberId);
        var token = member?.Tokens.FirstOrDefault(t => t.Id == entry.TokenId);

        if (member is null || token is null)
        {
            fail = Fail.InvalidKey;
            return null;
        }
        return Validate(member, token, ip, out fail);
    }

    private AuthResult? Validate(Member member, ApiToken token, IPAddress? ip, out Fail fail)
    {
        fail = Fail.None;

        if (!member.Enabled) { fail = Fail.Disabled; return null; }
        if (!token.Enabled) { fail = Fail.Disabled; return null; }

        if (!token.NeverExpires && token.ExpiresAt is { } exp && exp <= DateTimeOffset.Now)
        {
            fail = Fail.Expired;
            return null;
        }

        if (token.AllowedIps.Count > 0 && ip is not null && !IpAllowed(token.AllowedIps, ip))
        {
            fail = Fail.IpNotAllowed;
            return null;
        }

        var group = _store.Config.Groups.FirstOrDefault(g => g.Id == member.Group)
                    ?? _store.Config.Groups.FirstOrDefault(g => g.Id == "default")
                    ?? new Group { Id = "default", Name = "默认组", Rate = 1.0, Models = ["*"] };

        return new AuthResult(member, token, group);
    }

    /// <summary>IP 白名单，支持单个 IP 和 CIDR。</summary>
    private static bool IpAllowed(List<string> allowed, IPAddress ip)
    {
        foreach (var rule in allowed)
        {
            var r = rule.Trim();
            if (r == "*") return true;

            if (r.Contains('/'))
            {
                if (InCidr(ip, r)) return true;
            }
            else if (IPAddress.TryParse(r, out var single) && single.Equals(ip))
            {
                return true;
            }
        }
        return false;
    }

    private static bool InCidr(IPAddress ip, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2) return false;
        if (!IPAddress.TryParse(parts[0], out var network)) return false;
        if (!int.TryParse(parts[1], out var bits)) return false;

        var ipBytes = ip.GetAddressBytes();
        var netBytes = network.GetAddressBytes();
        if (ipBytes.Length != netBytes.Length) return false;
        if (bits < 0 || bits > ipBytes.Length * 8) return false;

        var fullBytes = bits / 8;
        var remaining = bits % 8;

        for (var i = 0; i < fullBytes; i++)
            if (ipBytes[i] != netBytes[i]) return false;

        if (remaining > 0)
        {
            var mask = (byte)(0xFF << (8 - remaining));
            if ((ipBytes[fullBytes] & mask) != (netBytes[fullBytes] & mask)) return false;
        }
        return true;
    }

    private static string CacheKeyOf(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>成员/令牌改动后清缓存，让改动立刻生效。</summary>
    public void Invalidate()
    {
        lock (_gate) _cache.Clear();
    }

    public static string FailMessage(Fail fail) => fail switch
    {
        Fail.MissingKey => "缺少 API key。请求头加上 Authorization: Bearer <你的 key>",
        Fail.InvalidKey => "API key 无效",
        Fail.Disabled => "这把 key 已被停用",
        Fail.Expired => "这把 key 已过期",
        Fail.IpNotAllowed => "当前 IP 不在允许列表内",
        _ => "认证失败",
    };
}
