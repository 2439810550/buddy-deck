using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// `/admin/*` 端点族 —— 界面五个功能面的后端。
///
/// ## 为什么单独一个文件
///
/// `GatewayApp` 管的是「面向模型客户端的 OpenAI 兼容面」（/v1/*、/metrics、/healthz）。
/// 这一族管的是「面向桌面界面自己的管理面」。两者的**信任模型不同**：
/// 前者是「用户拿来接第三方客户端」，后者会暴露账号标签与配置 ——
/// 所以全部要求鉴权，且绝不回 token 明文。分开放，边界清楚。
///
/// ## 响应形状以 App 的 DTO 为准
///
/// 键名必须与 `src/BuddyDeck.App/Services/IGatewayClient.cs` 里那批
/// `private sealed class XxxDto` **逐字对齐**。对不上界面就是空白，
/// 而且**编译期发现不了**（一堆 JSON 字符串）。所以这里的每个 JSON 键
/// 都在注释里标了它对应 App 的哪个字段。
///
/// ## 两条硬要求（写在这里当提醒，别绕过）
///
/// 1. **绝不回 token 明文**。`AccountSummary` 里压根没有 token 字段，
///    这是**结构上**的保证 —— 别往响应里加。
/// 2. **写操作一律走 <see cref="Vault.Write"/>**（临时文件 + 原子替换）。
///    保险库里是明文 token，直接覆盖写坏了等于用户的全部账号没了。
///
/// ## 与 <see cref="IEndpointFamily"/> 的关系
///
/// 本类**本来就已经是**「就地接管」形态（暴露 `TryHandleAsync`、由网关在鉴权后
/// 调一次、返回 bool 表示「我处理了」），所以抽路由注册表时它天然就是
/// <see cref="IEndpointFamily"/> 的实现 —— **接口是照它的形状定的，不是反过来**。
/// 实现接口未改动它任何内部逻辑（既有 40 条测试 + 52 项真网关契约检查全绿）。
/// </summary>
public sealed partial class AdminEndpoints : IEndpointFamily
{
    private readonly GatewayOptions _options;
    private readonly UpstreamVariant _variant;
    private readonly ICredentialSource _credentials;
    private readonly RequestLog? _requestLog;
    private readonly ILogger<AdminEndpoints> _log;

    /// <summary>
    /// 账号池 —— **可空**。
    ///
    /// 用来回答「这个号有哪些模型正在冷却」（用户点名要的可观测性）。
    /// 为什么可空：既有测试构造 `AdminEndpoints` 时没有池（它们只测
    /// 设置/日志那些不碰池的面），加一个必需参数会让那些测试全部编译不过。
    /// **为可观测性加字段不该强制所有人改构造点。**
    /// 为 null 时 `cooldowns` 回空数组、`failureCount` 回 0 ——
    /// 账号列表本身照常能读，不会因为缺池就整个 500。
    /// </summary>
    private readonly AccountPool? _pool;

    /// <summary>时钟 —— 算冷却剩余时间要用，可注入以便测试。</summary>
    private readonly TimeProvider _clock;

    /// <summary>
    /// API Key 存储 —— **可空**。
    ///
    /// 为什么可空：既有测试构造 `AdminEndpoints` 时没有它。
    /// 为 null 时 `/admin/keys` 回 503 并说清原因，而不是抛 ——
    /// 让"没配置"表现为一个可读的错误，而不是崩溃。
    /// </summary>
    private readonly KeyStore? _keys;

    /// <summary>
    /// 设置的落盘存储 —— **可空**（既有测试没有它）。
    ///
    /// 为 null 时改设置只改内存（与 M14 之前的行为一致），不会抛 ——
    /// 但那样重启就丢，所以生产装配里**必须**传进来。
    /// </summary>
    private readonly SettingsStore? _settingsStore;

    /// <summary>
    /// 模型目录 —— **可空**。`/admin/pool-health` 要枚举模型才知道该问哪些。
    ///
    /// 为 null 时那个端点回空矩阵（而不是崩）——
    /// 它是只读的可观测面，缺依赖不该让整个管理面 500。
    /// </summary>
    private readonly ModelCatalog? _catalog;

    /// <summary>
    /// 请求指标 —— **可空**。`/admin/pool-health` 用它算**降级次数**。
    ///
    /// 为什么非要它不可：`cooldowns` 只能看到"现在冷却中"，
    /// 而"刚才被限流、靠换模型救回来了"这件事**不留痕**（`MarkSuccess` 会清冷却）。
    /// 用户要判断"这个模型是不是废了"，看的正是"最近有多常被降级"。
    /// </summary>
    private readonly IMetricsSource? _metrics;

    /// <summary>
    /// 上游客户端 —— **可空**。手动续期（真刷 token）要用。
    ///
    /// 为 null 时续期回 501 并说明（既有测试构造点没传它）。
    /// 生产装配里**必须**传进来（见 Program.cs）。
    /// </summary>
    private readonly UpstreamClient? _upstream;

    /// <summary>签到客户端 —— **可空**。一键签到要用。</summary>
    private readonly Checkin? _checkin;

    /// <summary>
    /// 数据库 —— **可空**。池快照 / 池↔账号关联 / 跨池积分汇总用它。
    /// 为 null 时这些查询退化（跨池积分返回 0），不影响其他端点。
    /// </summary>
    private readonly Database? _database;

    /// <summary>
    /// 多池路由 —— **可空**。为 null 时池列表退化成只读 pools.json。
    /// 挂上后 /admin/pools 会返回 PoolRouter 扫描到的**全部**池（含 pools/ 子目录）。
    /// </summary>
    private readonly PoolRouter? _poolRouter;

    /// <summary>
    /// 设置的内存副本。
    ///
    /// ## 为什么是内存而不是落盘
    ///
    /// 端口 / API key / 变体 这几项**改了对正在跑的进程不生效** ——
    /// Kestrel 已经绑好端口了，鉴权中间件读的是构造时注入的同一个
    /// `GatewayOptions` 实例（改属性倒是会生效，但那是**运行中悄悄改安全策略**，
    /// 更糟）。所以这里的语义是「改的是下次启动的配置」，必须让用户知道。
    ///
    /// 落盘成文件这件事留到有明确的「配置文件位置」约定之后再做 ——
    /// 现在硬塞一个路径进去，等于替用户决定他机器上的文件布局。
    /// </summary>
    private readonly GatewaySettingsState _settings;

    public AdminEndpoints(
        GatewayOptions options,
        UpstreamVariant variant,
        ICredentialSource credentials,
        RequestLog? requestLog,
        ILogger<AdminEndpoints> log,
        AccountPool? pool = null,
        TimeProvider? clock = null,
        KeyStore? keys = null,
        SettingsStore? settingsStore = null,
        ModelCatalog? catalog = null,
        IMetricsSource? metrics = null,
        UpstreamClient? upstream = null,
        Checkin? checkin = null,
        Database? database = null,
        PoolRouter? poolRouter = null)
    {
        _options = options;
        _variant = variant;
        _credentials = credentials;
        _requestLog = requestLog;
        _log = log;
        _pool = pool;
        _clock = clock ?? TimeProvider.System;
        _keys = keys;
        _settingsStore = settingsStore;
        _database = database;
        _poolRouter = poolRouter;
        _catalog = catalog;
        _metrics = metrics;
        _upstream = upstream;
        _checkin = checkin;
        _database = database;
        _checkin = checkin;

        _settings = new GatewaySettingsState
        {
            Port = options.Port,
            ApiKey = options.ApiKey,
            VariantId = variant.Id,
            VaultPath = options.ResolveVaultPath(),
            LogPath = options.ResolveLogDirectory(),
        };
    }

    /// <summary>
    /// 这个请求是不是 `/admin/*`。是的话就地处理并返回 true。
    ///
    /// 由 <see cref="GatewayApp"/> 在**鉴权之后**调用 —— 也就是说
    /// 走到这里时已经过了 Host 检查与 Bearer 校验。
    /// （不要把本方法挪到鉴权之前，那会让账号标签对无 key 的调用者可见。）
    /// </summary>
    public async Task<bool> TryHandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (!path.StartsWith("/admin/", StringComparison.Ordinal) &&
            !string.Equals(path, "/admin", StringComparison.Ordinal))
        {
            return false;
        }

        // 尾部斜杠归一（/admin/accounts/ 与 /admin/accounts 等价），
        // 与 GatewayApp.NormalizePath 同一套规则。
        if (path.Length > 1 && path.EndsWith('/')) path = path.TrimEnd('/');

        var method = context.Request.Method;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // /admin/{area}/...
        var area = segments.Length >= 2 ? segments[1] : "";

        switch (area)
        {
            case "accounts":
                await HandleAccountsAsync(context, method, segments).ConfigureAwait(false);
                return true;
            case "pools":
                await HandlePoolsAsync(context, method, segments).ConfigureAwait(false);
                return true;
            case "logs":
                await HandleLogsAsync(context, method).ConfigureAwait(false);
                return true;
            case "settings":
                await HandleSettingsAsync(context, method).ConfigureAwait(false);
                return true;
            case "keys":
                await HandleKeysAsync(context, method, segments).ConfigureAwait(false);
                return true;
            case "pool-health":
                // 只支持 GET —— 它是只读的可用性快照，没有"改"的语义。
                if (method == HttpMethods.Get)
                {
                    await PoolHealthAsync(context).ConfigureAwait(false);
                    return true;
                }
                return false;
            case "checkin":
                await HandleCheckinAsync(context, method, segments).ConfigureAwait(false);
                return true;
            default:
                // ⚠ **返回 false，不是 404**。
                //
                // 这里原来直接回 404 并 `return true`（"我处理了"）——
                // 那会**吃掉所有它不认识的 `/admin/*`**，
                // 后面的族（`CreditsEndpoints` 就挂在 `/admin/credits`）**永远轮不到**。
                //
                // 实测症状（用户反馈「余量那一块你再研究一下」）：
                //   `/admin/credits` 的族注册得好好的、自己的单测 23/23 全绿，
                //   但真网关上一直是 404 —— 因为它被 AdminEndpoints 半路截走了。
                //
                // ⇒ 归属原则：**不认识就让路**，而不是"凡 /admin/* 都是我的"。
                //   最终没人认领由 `GatewayApp` 统一回 404（那里是唯一的兜底）。
                return false;
        }
    }

    // ══ 账号 ══════════════════════════════════════════════════

    /// <summary>
    /// ```
    /// GET  /admin/accounts                     列表
    /// POST /admin/accounts/{id}/activate       切当前号
    /// POST /admin/accounts/{id}/rename         改标签
    /// POST /admin/accounts/{id}/remove         删号
    /// POST /admin/accounts/{id}/refresh        手动续期
    /// POST /admin/accounts/import              导入
    /// ```
    /// </summary>
    private async Task HandleAccountsAsync(HttpContext context, string method, string[] segments)
    {
        // GET /admin/accounts
        if (method == HttpMethods.Get && segments.Length == 2)
        {
            await ListAccountsAsync(context).ConfigureAwait(false);
            return;
        }

        if (method != HttpMethods.Post)
        {
            await WriteErrorAsync(context, 404, "not_found",
                $"no admin route for {method} {context.Request.Path}").ConfigureAwait(false);
            return;
        }

        // POST /admin/accounts/import
        if (segments.Length == 3 && segments[2] == "import")
        {
            await ImportAccountsAsync(context).ConfigureAwait(false);
            return;
        }

        // POST /admin/accounts/export
        if (segments.Length == 3 && segments[2] == "export")
        {
            await ExportAccountsAsync(context).ConfigureAwait(false);
            return;
        }

        // POST /admin/accounts/{id}/{verb}
        if (segments.Length == 4)
        {
            // ⚠ id 里可能有冒号（`uuid:enterpriseId`）—— 客户端用
            // Uri.EscapeDataString 编过，这里解回来。**不要 trim**，
            // 末尾那个冒号是格式的一部分（HANDOVER 第三节第 5 条）。
            var id = Uri.UnescapeDataString(segments[2]);
            var verb = segments[3];

            switch (verb)
            {
                case "activate":
                    await ActivateAsync(context, id).ConfigureAwait(false);
                    return;
                case "rename":
                    await RenameAsync(context, id).ConfigureAwait(false);
                    return;
                case "remove":
                    await RemoveAsync(context, id).ConfigureAwait(false);
                    return;
                case "refresh":
                    await RefreshAsync(context, id).ConfigureAwait(false);
                    return;
            }
        }

        await WriteErrorAsync(context, 404, "not_found",
            $"no admin route for {method} {context.Request.Path}").ConfigureAwait(false);
    }

    /// <summary>
    /// 账号列表。
    ///
    /// ⚠ **绝不返回 accessToken / refreshToken**。App 的 `AccountSummary`
    /// 里压根没有这两个字段（我读了那个文件确认过），所以这里也不需要
    /// 「记得别写」—— 只要不主动加就行。
    /// </summary>
    private async Task ListAccountsAsync(HttpContext context)
    {
        var doc = ReadVault();
        var entries = doc?.Accounts ?? [];

        var array = new JsonArray();
        foreach (var entry in entries)
        {
            array.Add(new JsonObject
            {
                // App: AccountSummary.Id
                ["id"] = entry.Id,
                // App: Label
                ["label"] = entry.Label,
                // App: Uid
                ["uid"] = entry.Uid,
                // App: EnterpriseId
                ["enterpriseId"] = entry.EnterpriseId,
                // App: Domain
                ["domain"] = entry.Domain,
                // App: ExpiresAtMs
                ["expiresAtMs"] = entry.ExpiresAtMs,
                // App: RefreshExpiresAtMs
                ["refreshExpiresAtMs"] = entry.RefreshExpiresAtMs,
                // App: Active —— 当前网关正在用的那个
                ["active"] = string.Equals(entry.Id, doc?.ActiveId, StringComparison.Ordinal),

                // App: Cooldowns / FailureCount / Frozen —— 账号的**健康状况**。
                //
                // 用户实测反馈「每一个账号的模型冷却情况也没有」：
                // 限流是按「号+模型」二元组记的，所以同一个号可能 hy3 冷却、
                // hy4 正常。界面看不到这层，用户只会感觉"这个号时好时坏"。
                //
                // ⚠ pool 里用的是 `entry.Id`（带末尾冒号的旧 id）；
                //   若池里没有这个号（还没被 Replace 进去），这三个字段给空/零，
                //   而不是抛异常 —— 账号列表本身必须能读出来。
                ["cooldowns"] = CooldownsOf(entry.Id),
                ["failureCount"] = FailuresOf(entry.Id),
                ["frozen"] = IsFrozenOf(entry.Id),
            });

            // ★ 冷却入库：每次读账号列表时把当前冷却同步进数据库，
            //   这样「账号模型冷却」有持久化来源，前端可秒读。
            if (_pool is not null && _database is not null)
            {
                var cooldowns = _pool.CooldownsOf(entry.Id, _clock);
                _database.ReplaceAccountCooldowns(
                    entry.Id, cooldowns, _clock.GetUtcNow().ToUnixTimeMilliseconds());
            }
        }

        await WriteJsonAsync(context, 200, array).ConfigureAwait(false);
    }

    /// <summary>
    /// 把一个号的冷却表转成 JSON。
    ///
    /// 形状：`[{model, untilMs, remainSeconds}]` —— 界面可以直接拿
    /// `remainSeconds` 显示「还剩 12 分钟」，不用自己做时间换算
    /// （时区/单位换算放在界面是最容易出错的一环）。
    /// </summary>
    private JsonArray CooldownsOf(string memberId)
    {
        var arr = new JsonArray();

        // 池缺席（老构造点/纯设置测试）时回空数组，而不是抛 ——
        // 账号列表是主功能，不该因为"顺带加的可观测字段"读不到就整个失败。
        if (_pool is null) return arr;

        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();

        foreach (var (model, until) in _pool.CooldownsOf(memberId, _clock))
        {
            arr.Add(new JsonObject
            {
                ["model"] = model,
                ["untilMs"] = until,
                ["remainSeconds"] = Math.Max(0, (until - now) / 1000),
            });
        }

        return arr;
    }

    /// <summary>连续失败次数。池缺席时回 0。</summary>
    private int FailuresOf(string memberId) => _pool?.FailuresOf(memberId) ?? 0;

    /// <summary>是否被冷冻。池缺席时回 false。</summary>
    private bool IsFrozenOf(string memberId) => _pool?.IsFrozen(memberId) ?? false;

    /// <summary>切成某一条。改的是 `activeId`，并原子写回。</summary>
    private async Task ActivateAsync(HttpContext context, string id)
    {
        var doc = ReadVault();
        if (doc is null)
        {
            await WriteErrorAsync(context, 404, "not_found", "保险库还没有建（先导入一个账号）").ConfigureAwait(false);
            return;
        }

        if (!doc.Accounts.Any(a => string.Equals(a.Id, id, StringComparison.Ordinal)))
        {
            await WriteErrorAsync(context, 404, "not_found", $"保险库里没有这个账号：{id}").ConfigureAwait(false);
            return;
        }

        // ⚠ 这里的 ActiveId 原样写回，**不做 trim** ——
        // 末尾那个冒号是格式的一部分。
        WriteVault(doc with { ActiveId = id });

        await WriteOkAsync(context).ConfigureAwait(false);
    }

    /// <summary>只改标签，不碰 token / 过期时间。</summary>
    private async Task RenameAsync(HttpContext context, string id)
    {
        var body = await ReadJsonObjectAsync(context).ConfigureAwait(false);
        if (body is null) return;

        // App: RenameAccountAsync 发的体是 `new { label }`
        var label = body["label"]?.GetValue<string>() ?? "";

        var doc = ReadVault();
        if (doc is null)
        {
            await WriteErrorAsync(context, 404, "not_found", "保险库还没有建").ConfigureAwait(false);
            return;
        }

        var index = doc.Accounts.FindIndex(a => string.Equals(a.Id, id, StringComparison.Ordinal));
        if (index < 0)
        {
            await WriteErrorAsync(context, 404, "not_found", $"保险库里没有这个账号：{id}").ConfigureAwait(false);
            return;
        }

        var accounts = new List<VaultEntry>(doc.Accounts);
        accounts[index] = accounts[index] with { Label = label };
        WriteVault(doc with { Accounts = accounts });

        await WriteOkAsync(context).ConfigureAwait(false);
    }

    /// <summary>删号。如果删的是当前 activeId，把它挪给剩下的第一条（或清空）。</summary>
    private async Task RemoveAsync(HttpContext context, string id)
    {
        var doc = ReadVault();
        if (doc is null)
        {
            await WriteErrorAsync(context, 404, "not_found", "保险库还没有建").ConfigureAwait(false);
            return;
        }

        var remaining = doc.Accounts
            .Where(a => !string.Equals(a.Id, id, StringComparison.Ordinal))
            .ToList();

        if (remaining.Count == doc.Accounts.Count)
        {
            await WriteErrorAsync(context, 404, "not_found", $"保险库里没有这个账号：{id}").ConfigureAwait(false);
            return;
        }

        // 删掉的正好是当前用的那个 → 挪给剩下的第一条。
        // 不挪的话 activeId 会指向一个不存在的条目，界面上「当前号」就空了。
        var activeId = doc.ActiveId;
        if (string.Equals(activeId, id, StringComparison.Ordinal))
        {
            activeId = remaining.Count > 0 ? remaining[0].Id : "";
        }

        WriteVault(doc with { ActiveId = activeId, Accounts = remaining });

        await WriteOkAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// 手动续期 —— **真刷新 token 并原子写回保险库**。
    ///
    /// ## 为什么必须是「真刷新 + 写回」
    ///
    /// 只改内存/只回个「成功」是静默失败：界面显示「续期成功」而实际
    /// token 没变，下次调上游还是 401。所以：
    ///   ① 打上游 `/v2/plugin/auth/token/refresh`（UpstreamClient.RefreshTokenAsync）
    ///   ② 把新 token 原子写回保险库（Vault.Write）
    ///   ③ 更新池里的账号（Replace 会读新保险库）
    ///
    /// 未注入 UpstreamClient 时回 501（测试构造点）。
    /// </summary>
    private async Task RefreshAsync(HttpContext context, string id)
    {
        var doc = ReadVault();
        if (doc is null || !doc.Accounts.Any(a => string.Equals(a.Id, id, StringComparison.Ordinal)))
        {
            await WriteErrorAsync(context, 404, "not_found", $"保险库里没有这个账号：{id}").ConfigureAwait(false);
            return;
        }

        if (_upstream is null)
        {
            if (_log.IsEnabled(LogLevel.Warning)) LogRefreshNotImplemented(id);
            await WriteErrorAsync(context, 501, "not_implemented",
                "这个实例没有接上游客户端，续期不可用。").ConfigureAwait(false);
            return;
        }

        var account = doc.Accounts.First(a => string.Equals(a.Id, id, StringComparison.Ordinal));

        try
        {
            var outcome = await _upstream
                .RefreshTokenAsync(account.ToCredential(), context.RequestAborted)
                .ConfigureAwait(false);

            // 写回：新 token + 滑动窗口 + 过期时刻。
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var updated = account with
            {
                AccessToken = outcome.AccessToken,
                RefreshToken = string.IsNullOrEmpty(outcome.RefreshToken)
                    ? account.RefreshToken
                    : outcome.RefreshToken,
                ExpiresAtMs = outcome.ExpiresInSec is { } sec && sec > 0
                    ? nowMs + sec * 1000L
                    : account.ExpiresAtMs,
                RefreshExpiresAtMs = outcome.RefreshExpiresInSec is { } rsec && rsec > 0
                    ? nowMs + rsec * 1000L
                    : account.RefreshExpiresAtMs,
                LastSeenAt = Iso(DateTimeOffset.UtcNow),
            };

            WriteVault(doc with { Accounts = doc.Accounts.Select(a => a.Id == id ? updated : a).ToList() });

            // 池里也换上新 token（下次请求用新的）。
            _pool?.Replace(_credentials.Load());

            if (_log.IsEnabled(LogLevel.Information))
            {
                LogRefreshDone(id, outcome.ExpiresInSec ?? 0);
            }

            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = true,
                ["expiresInSec"] = outcome.ExpiresInSec ?? 0,
                ["refreshExpiresInSec"] = outcome.RefreshExpiresInSec ?? 0,
            }).ConfigureAwait(false);
        }
        catch (UpstreamException ex)
        {
            await WriteErrorAsync(context, 502, ex.Kind,
                $"续期失败：{ex.Message}").ConfigureAwait(false);
        }
    }

    // ══ 签到（一键签到，用户需求）══════════════════════════

    /// <summary>
    /// `POST /admin/checkin` —— 给**全部账号**签到（或指定账号）。
    ///
    /// 逐个账号执行，单个失败不挡别的（与积分批量端点同一原则）。
    /// 每个账号的结果：已签 / 新签 / 失败（含原因）。
    /// </summary>
    private async Task HandleCheckinAsync(HttpContext context, string method, string[] segments)
    {
        // POST /admin/checkin           全部账号
        // POST /admin/checkin/{id}      指定账号
        if (method != HttpMethods.Post)
        {
            await WriteErrorAsync(context, 405, "method_not_allowed",
                "签到只支持 POST").ConfigureAwait(false);
            return;
        }

        if (_checkin is null)
        {
            await WriteErrorAsync(context, 501, "not_implemented",
                "这个实例没有接签到客户端。").ConfigureAwait(false);
            return;
        }

        var doc = ReadVault();
        if (doc is null || doc.Accounts.Count == 0)
        {
            await WriteErrorAsync(context, 401, "not_signed_in",
                "保险库里没有账号。").ConfigureAwait(false);
            return;
        }

        // 指定了账号就只签那一个，否则全部。
        var targets = doc.Accounts;
        if (segments.Length >= 3)
        {
            var id = Uri.UnescapeDataString(segments[2]);
            targets = targets.Where(a => string.Equals(a.Id, id, StringComparison.Ordinal)).ToList();
            if (targets.Count == 0)
            {
                await WriteErrorAsync(context, 404, "not_found", $"没有这个账号：{id}").ConfigureAwait(false);
                return;
            }
        }

        var results = new JsonArray();
        foreach (var account in targets)
        {
            results.Add(await CheckinOneAsync(account, doc, context.RequestAborted).ConfigureAwait(false));
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = true,
            ["results"] = results,
        }).ConfigureAwait(false);
    }

    /// <summary>给一个账号签到，返回结构化结果（失败也不抛）。</summary>
    private async Task<JsonNode> CheckinOneAsync(VaultEntry account, VaultDocument doc, CancellationToken ct)
    {
        try
        {
            var result = await _checkin!
                .EnsureCheckedInAsync(account.ToCredential(), account.LastCheckinAt, ct)
                .ConfigureAwait(false);

            // 新签到的要写回保险库（lastCheckinAt 更新），否则下次还会重复打上游。
            if (result.Outcome == CheckinOutcome.Claimed)
            {
                var updated = account with { LastCheckinAt = Iso(DateTimeOffset.UtcNow) };
                WriteVault(doc with
                {
                    Accounts = doc.Accounts.Select(a => a.Id == account.Id ? updated : a).ToList(),
                });
            }

            return new JsonObject
            {
                ["ok"] = result.Ok,
                ["id"] = account.Id,
                ["label"] = string.IsNullOrEmpty(account.Label) ? account.Uid[..Math.Min(8, account.Uid.Length)] : account.Label,
                ["claimed"] = result.Outcome == CheckinOutcome.Claimed,
                ["message"] = result.Message,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["id"] = account.Id,
                ["label"] = string.IsNullOrEmpty(account.Label) ? account.Uid[..Math.Min(8, account.Uid.Length)] : account.Label,
                ["claimed"] = false,
                ["message"] = $"签到失败：{ex.Message}",
            };
        }
    }

    /// <summary>
    /// 导入账号。
    ///
    /// 支持两种输入（App 的 `ImportAccountsAsync` 只发 `{ payload }`，
    /// payload 里既可能是整份登录文件、也可能是一段裸凭证 JSON）：
    ///
    /// ```jsonc
    /// // 形态 A：桌面 App 的登录文件（含嵌套 auth/account）
    /// { "auth": { "accessToken": "…", "refreshToken": "…" }, "account": { "uid": "…" } }
    ///
    /// // 形态 B：裸凭证
    /// { "accessToken": "…", "uid": "…", "enterpriseId": "…", "domain": "…" }
    /// ```
    ///
    /// 两种都走 Core 的 <see cref="AuthDocument.Parse"/> —— 识别逻辑不在这里重写。
    /// </summary>
    private async Task ImportAccountsAsync(HttpContext context)
    {
        var body = await ReadJsonObjectAsync(context).ConfigureAwait(false);
        if (body is null) return;

        // App: ImportAccountsAsync 发的体是 `new { payload }`
        var payload = body["payload"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(payload))
        {
            await WriteErrorAsync(context, 400, "invalid_request_error", "缺少 payload 字段").ConfigureAwait(false);
            return;
        }

        Credential? credential;
        try
        {
            credential = AuthDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            // ⚠ 先试**批量形态**（旧版账号库），再报"不是合法 JSON"。
            //
            // 为什么放在这里：旧版账号库 `~/.workbuddy-api/accounts.cn.json`
            // 也是合法 JSON，但形状是 `{accounts:[…]}` 而不是单条凭证 ——
            // `AuthDocument.Parse` 认不出它。界面上「从旧版导入账号」按钮
            // 发过来的正是这个形状，实测会拿到
            //   「payload 里没有 accessToken —— 这不是一份有效的登录凭证」
            // 用户看到这句完全不知道该怎么办（20 个账号明明就在文件里）。
            if (await TryImportLegacyLibraryAsync(context, payload).ConfigureAwait(false)) return;

            await WriteErrorAsync(context, 400, "invalid_request_error",
                $"payload 不是合法 JSON：{ex.Message}").ConfigureAwait(false);
            return;
        }

        if (credential is null || string.IsNullOrEmpty(credential.AccessToken))
        {
            // 同上：不是单条凭证时，再给批量形态一次机会
            if (await TryImportLegacyLibraryAsync(context, payload).ConfigureAwait(false)) return;

            await WriteErrorAsync(context, 400, "invalid_request_error",
                "payload 里没有 accessToken —— 这不是一份有效的登录凭证").ConfigureAwait(false);
            return;
        }

        var doc = ReadVault() ?? new VaultDocument();

        // id 的格式是 `uuid:enterpriseId`，**末尾冒号是格式的一部分**。
        // 同一个 uid + 企业再导入一次视为「更新那一条」，不新增重复项 ——
        // 用户重新登录后重复导入是常态，每次都堆一条会让列表越来越乱。
        var newId = $"{Guid.NewGuid()}:{credential.EnterpriseId ?? ""}";
        var accounts = new List<VaultEntry>(doc.Accounts);

        var existing = accounts.FindIndex(a =>
            string.Equals(a.Uid, credential.Uid, StringComparison.Ordinal) &&
            string.Equals(a.EnterpriseId ?? "", credential.EnterpriseId ?? "", StringComparison.Ordinal));

        var entry = new VaultEntry
        {
            Id = existing >= 0 ? accounts[existing].Id : newId,
            Uid = credential.Uid,
            EnterpriseId = credential.EnterpriseId,
            Domain = credential.Domain,
            AccessToken = credential.AccessToken,
            RefreshToken = credential.RefreshToken,
            ExpiresAtMs = credential.ExpiresAtMs,
            // ⚠ 这三个是 **ISO 字符串**（不是 Unix 毫秒）—— 见 HANDOVER 坑 2。
            // 写成数字的话读真实保险库会抛 JsonException。
            Label = string.IsNullOrEmpty(credential.Label) ? (existing >= 0 ? accounts[existing].Label : "") : credential.Label,
            AddedAt = existing >= 0 ? accounts[existing].AddedAt : Iso(DateTimeOffset.UtcNow),
            LastSeenAt = Iso(DateTimeOffset.UtcNow),
            LastCheckinAt = existing >= 0 ? accounts[existing].LastCheckinAt : null,
        };

        if (existing >= 0) accounts[existing] = entry;
        else accounts.Add(entry);

        var activeId = string.IsNullOrEmpty(doc.ActiveId) ? entry.Id : doc.ActiveId;
        WriteVault(doc with { ActiveId = activeId, Accounts = accounts });

        if (_log.IsEnabled(LogLevel.Information))
        {
            LogImported(existing >= 0 ? "更新" : "新增", entry.Label);
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            // App 的 ImportAccountsAsync 只关心成功与否（它把成功映射成 1）
            ["ok"] = true,
            ["id"] = entry.Id,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量导入：识别**旧版账号库**形状并逐条写入。返回 true 表示"已经处理并回过响应了"。
    ///
    /// ## 为什么需要它（用户实测点名的硬伤）
    ///
    /// 旧版面板把账号存在 `~/.workbuddy-api/accounts.cn.json`，形状是：
    /// <code>
    /// { "version":1, "activeId":"…", "accounts": [ { "id","uid","domain",
    ///   "accessToken","refreshToken","expiresAtMs","label",… }, … ] }
    /// </code>
    ///
    /// 而上面的单条路径只认「一份客户端登录文件」或「一条裸凭证」——
    /// 两者形状完全不同。用户点「从旧版导入账号」时，20 个账号就在文件里，
    /// 却收到「payload 里没有 accessToken」，**完全无从处置**。
    ///
    /// ## 语义
    ///
    /// · 按 `uid + enterpriseId` 去重：已存在的**更新**（重新登录后重复导入是常态），
    ///   不新增重复项 —— 与单条路径同一条规则。
    /// · 逐条独立：某一条缺 accessToken 只跳过它，不让整批失败
    ///   （一份文件里有个坏条目是常见的，整批失败会让用户以为是文件格式错）。
    /// · 结果回 `{ok, added, updated, skipped:[{index,label,reason}]}` ——
    ///   **跳过要有原因**，否则用户看到"导入了 18 个"但不知道另外 2 个去哪了。
    /// </summary>
    private async Task<bool> TryImportLegacyLibraryAsync(HttpContext context, string payload)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        // 只有当它**确实**是账号库形状时才接管 —— 否则交回给单条路径报原本的错。
        if (root?["accounts"] is not JsonArray incoming || incoming.Count == 0) return false;

        var doc = ReadVault() ?? new VaultDocument();
        var accounts = new List<VaultEntry>(doc.Accounts);

        var added = 0;
        var updated = 0;
        var skipped = new JsonArray();

        for (var i = 0; i < incoming.Count; i++)
        {
            if (incoming[i] is not JsonObject row)
            {
                skipped.Add(new JsonObject { ["index"] = i, ["label"] = "", ["reason"] = "不是对象" });
                continue;
            }

            var uid = Str(row, "uid");
            var token = Str(row, "accessToken");
            var label = Str(row, "label");

            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(token))
            {
                skipped.Add(new JsonObject
                {
                    ["index"] = i,
                    ["label"] = label,
                    ["reason"] = string.IsNullOrEmpty(uid) ? "缺 uid" : "缺 accessToken",
                });
                continue;
            }

            var enterpriseId = Str(row, "enterpriseId");
            var existing = accounts.FindIndex(a =>
                string.Equals(a.Uid, uid, StringComparison.Ordinal) &&
                string.Equals(a.EnterpriseId ?? "", enterpriseId, StringComparison.Ordinal));

            var entry = new VaultEntry
            {
                // ⚠ id 沿用旧库那个（末尾带冒号，那是格式的一部分）；
                //   重复导入时保持同一个 id，界面上的"当前用哪个号"才跟得住。
                Id = existing >= 0
                    ? accounts[existing].Id
                    : (string.IsNullOrEmpty(Str(row, "id")) ? $"{Guid.NewGuid()}:{enterpriseId}" : Str(row, "id")),
                Uid = uid,
                EnterpriseId = string.IsNullOrEmpty(enterpriseId) ? null : enterpriseId,
                Domain = Str(row, "domain"),
                AccessToken = token,
                RefreshToken = Str(row, "refreshToken"),
                ExpiresAtMs = Long(row, "expiresAtMs"),
                Label = label,
                // ISO 字符串（不是 Unix 毫秒）—— 见 HANDOVER 坑 2
                AddedAt = existing >= 0 ? accounts[existing].AddedAt : Str(row, "addedAt"),
                LastSeenAt = Iso(DateTimeOffset.UtcNow),
                LastCheckinAt = existing >= 0 ? accounts[existing].LastCheckinAt : null,
            };

            if (existing >= 0)
            {
                accounts[existing] = entry;
                updated++;
            }
            else
            {
                accounts.Add(entry);
                added++;
            }
        }

        if (added == 0 && updated == 0)
        {
            await WriteErrorAsync(context, 400, "invalid_request_error",
                "这份文件里没有一条可导入的账号（每条都缺 uid 或 accessToken）").ConfigureAwait(false);
            return true;
        }

        // 旧库的 activeId 也一并带过来（用户在原面板里选中的那个号）
        var legacyActive = Str(root, "activeId");
        var activeId = !string.IsNullOrEmpty(doc.ActiveId)
            ? doc.ActiveId
            : (!string.IsNullOrEmpty(legacyActive) && accounts.Exists(a => a.Id == legacyActive)
                ? legacyActive
                : accounts[0].Id);

        WriteVault(doc with { ActiveId = activeId, Accounts = accounts });

        // ⚠ CA1873：实参位置上不能有计算 —— 日志关掉时那些计算就是白做的。
        //   所以先判级别、再在局部变量里算好，最后才传（见 HANDOVER 坑 6）。
        if (_log.IsEnabled(LogLevel.Information))
        {
            var summary = $"{added} 新增 / {updated} 更新";
            LogImported("批量", summary);
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = true,
            ["added"] = added,
            ["updated"] = updated,
            ["skipped"] = skipped,
        }).ConfigureAwait(false);
        return true;
    }

    /// <summary>读一个字符串字段；不是字符串就返回空串。</summary>
    private static string Str(JsonObject obj, string name)
        => obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>读一个数值字段；不是数字就返回 0。</summary>
    private static long Long(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return 0;
    }

    // ══ 分池 ══════════════════════════════════════════════════
    //
    // ## t58：从「单实例硬编码」改成真多池
    //
    // 之前这里固定回一个长度为 1 的数组、新增池回 501（见 t17 的注释），
    // 理由是「进程内多池需要每池独立端口 = 多实例架构」。
    //
    // **那个理由本身没错，但结论错了**：正因为「池 = 一套账号 + 一个端口」，
    // 池注册表才必须是**跨进程的文件**（`pools.json`），而不是内存对象。
    // 本实例的角色是：**读这张表、回答有哪些池、按表去改池的配置**。
    //
    // 这与旧版 `WorkBuddyPanel/Services/PoolStore.cs` 完全同形，概念照抄。
    //
    // ⚠ **不自己实现「起进程」**：本实例只维护注册表（谁该在哪个端口上用哪套账号）。
    //   真正拉起第二个网关实例是进程管理的事（面板的启停按钮）——
    //   网关给自己派生一个新进程，生命周期没人管得住。

    private async Task HandlePoolsAsync(HttpContext context, string method, string[] segments)
    {
        // GET /admin/pools —— 列表
        if (method == HttpMethods.Get && segments.Length == 2)
        {
            await ListPoolsAsync(context).ConfigureAwait(false);
            return;
        }

        // GET /admin/pools/{id}/accounts —— 某个池的账号列表（层级导航：点池看账号）
        if (method == HttpMethods.Get && segments.Length == 4 &&
            string.Equals(segments[3], "accounts", StringComparison.Ordinal))
        {
            var poolId = Uri.UnescapeDataString(segments[2]);
            await ListPoolAccountsAsync(context, poolId).ConfigureAwait(false);
            return;
        }

        if (method != HttpMethods.Post)
        {
            await WriteErrorAsync(context, 404, "not_found",
                $"no admin route for {method} {context.Request.Path}").ConfigureAwait(false);
            return;
        }

        // POST /admin/pools （App: UpsertPoolAsync）—— 新增或修改
        if (segments.Length == 2)
        {
            await UpsertPoolAsync(context).ConfigureAwait(false);
            return;
        }

        // POST /admin/pools/{id}/{verb}
        if (segments.Length == 4)
        {
            var id = Uri.UnescapeDataString(segments[2]);
            switch (segments[3])
            {
                case "state":
                    await SetPoolStateAsync(context, id).ConfigureAwait(false);
                    return;
                case "remove":
                    await RemovePoolAsync(context, id).ConfigureAwait(false);
                    return;
            }
        }

        await WriteErrorAsync(context, 404, "not_found",
            $"no admin route for {method} {context.Request.Path}").ConfigureAwait(false);
    }

    /// <summary>
    /// 读某个池的账号列表（层级导航用）。
    ///
    /// 直接读磁盘上那个池的保险库文件（<see cref="VaultPathOf"/>），
    /// 不跨进程 —— 这样「点号池看账号」不依赖那个池的网关在不在跑。
    /// 账号的冷却/冷冻等健康信息来自**当前实例**的 AccountPool（可能为空/缺失，
    /// 属于正常 —— 别的池的号不在本实例的池子里）。
    /// </summary>
    private async Task ListPoolAccountsAsync(HttpContext context, string poolId)
    {
        var pool = LoadPools().FirstOrDefault(p =>
            string.Equals(PoolIdOf(p), poolId, StringComparison.OrdinalIgnoreCase));

        if (pool is null)
        {
            await WriteErrorAsync(context, 404, "not_found", $"没有这个池：{poolId}").ConfigureAwait(false);
            return;
        }

        var vaultPath = VaultPathOf(pool);
        if (string.IsNullOrEmpty(vaultPath) || !File.Exists(vaultPath))
        {
            // 池存在但还没导入号（文件不存在）—— 空列表是正常状态。
            await WriteJsonAsync(context, 200, new JsonArray()).ConfigureAwait(false);
            return;
        }

        VaultDocument? doc;
        try
        {
            doc = Core.Vault.Read(vaultPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            await WriteErrorAsync(context, 500, "vault_unreadable",
                $"读不到「{pool.DisplayName}」的账号库：{ex.Message}").ConfigureAwait(false);
            return;
        }

        var entries = doc?.Accounts ?? [];
        var array = new JsonArray();
        foreach (var entry in entries)
        {
            array.Add(new JsonObject
            {
                ["id"] = entry.Id,
                ["label"] = entry.Label,
                ["uid"] = entry.Uid,
                ["enterpriseId"] = entry.EnterpriseId,
                ["domain"] = entry.Domain,
                ["expiresAtMs"] = entry.ExpiresAtMs,
                ["refreshExpiresAtMs"] = entry.RefreshExpiresAtMs,
                // 别的池的号不可能是本实例「当前正在用」的
                ["active"] = false,
                // ★ 冷却/冷冻/失败次数：从当前实例的 AccountPool 读（别的池的号
                //   若恰好在本实例池子里有记录，就如实报；没有则空/零）。
                //   之前这里硬编码空数组，导致「池子导航到的账号看不到冷却」—— 用户实测反馈。
                ["cooldowns"] = CooldownsOf(entry.Id),
                ["failureCount"] = FailuresOf(entry.Id),
                ["frozen"] = IsFrozenOf(entry.Id),
            });
        }

        await WriteJsonAsync(context, 200, array).ConfigureAwait(false);
    }

    /// <summary>
    /// `GET /admin/pools` —— **真实的多池列表**。
    ///
    /// 键名以 App 的 `PoolSummary` 为准（我读了 `IGatewayClient.cs`）：
    /// `id / displayName / port / apiKey / running / accountCount / isDefault / lastError`
    /// </summary>
    private async Task ListPoolsAsync(HttpContext context)
    {
        var pools = LoadPools();
        var array = new JsonArray();

        foreach (var pool in pools)
        {
            array.Add(new JsonObject
            {
                ["id"] = PoolIdOf(pool),
                ["displayName"] = pool.DisplayName,
                ["port"] = pool.Port,
                ["apiKey"] = pool.ApiKey,
                ["running"] = IsPoolRunning(pool),
                ["accountCount"] = AccountCountOf(pool),
                ["isDefault"] = pool.IsDefault,
                ["lastError"] = null,
                // ★ 面板要真正拉起/停止这个池的网关子进程，必须知道它的保险库路径。
                ["vaultPath"] = VaultPathOf(pool),
            });

            // ★ 池快照写库 + 建立池↔账号关联（跨池积分汇总用）。
            _database?.UpsertPool(
                PoolIdOf(pool),
                pool.DisplayName,
                pool.Port,
                pool.ApiKey,
                VaultPathOf(pool),
                pool.DirectoryName,
                pool.IsDefault,
                IsPoolRunning(pool),
                null,
                _clock.GetUtcNow().ToUnixTimeMilliseconds());

            // 关联这个池的账号（读它的保险库）
            var vaultPath = VaultPathOf(pool);
            if (!string.IsNullOrEmpty(vaultPath) && File.Exists(vaultPath))
            {
                try
                {
                    var doc = Core.Vault.Read(vaultPath);
                    if (doc?.Accounts is { } accounts)
                    {
                        foreach (var a in accounts)
                        {
                            _database?.LinkPoolAccount(PoolIdOf(pool), a.Id);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // 关联失败不影响池列表返回 —— 只是跨池汇总少了这个池。
                }
            }
        }

        await WriteJsonAsync(context, 200, array).ConfigureAwait(false);
    }

    /// <summary>
    /// `POST /admin/pools/{id}/state` —— 启停一个池。
    ///
    /// ⚠ **如实说明能力边界**：本端点能改的是**注册表里的状态**与**本实例的开关**。
    /// 「启动另一个池」= 起一个新进程，那是进程管理的事（面板按钮），
    /// 网关不给自己派生进程（生命周期没人管得住）。
    ///
    /// 所以：
    ///   · 对本实例自己那个池 → 真的落到进程级开关（`_poolRunning`，立刻生效）
    ///   · 对别的池 → 改 `pools.json` 里的 `autoStart`，由面板下次拉起时生效
    ///
    /// 这样界面点「启动」**不会什么也不发生**（旧版 501 的问题），
    /// 但也不会假装「已经起来了」。
    /// </summary>
    private async Task SetPoolStateAsync(HttpContext context, string id)
    {
        var pools = LoadPools();
        var pool = pools.FirstOrDefault(p => string.Equals(PoolIdOf(p), id, StringComparison.OrdinalIgnoreCase));
        if (pool is null)
        {
            await WriteErrorAsync(context, 404, "not_found", $"没有这个池：{id}").ConfigureAwait(false);
            return;
        }

        var body = await ReadJsonObjectAsync(context).ConfigureAwait(false);
        if (body is null) return;

        if (body["running"] is not JsonValue v || !v.TryGetValue<bool>(out var running))
        {
            await WriteErrorAsync(context, 400, "invalid_request_error", "缺少 running 字段").ConfigureAwait(false);
            return;
        }

        if (pool.IsDefault)
        {
            // 本实例就是这个池 → 真正生效的进程级开关
            _poolRunning = running;
        }
        else
        {
            // ★ 子池：路由级开关（new_api 渠道模型）。
            //   一个网关进程内多池，子池的「启停」= 请求是否还路由到它，
            //   而不是拉起独立进程（那又回到「一池一进程」的老架构）。
            var instance = _poolRouter?.All.FirstOrDefault(p =>
                string.Equals(p.PoolId, PoolIdOf(pool), StringComparison.OrdinalIgnoreCase));
            if (instance is not null)
            {
                instance.Running = running;
            }
        }

        // 不管是哪个池，都把「下次该不该自动起」记进注册表。
        pool.AutoStart = running;
        SavePools(pools);

        if (_log.IsEnabled(LogLevel.Information))
        {
            LogPoolState(id, running);
        }

        await WriteOkAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// `POST /admin/pools` —— **真正新增/修改池**（不再是 501）。
    ///
    /// 新池拿到：独立的 `DirectoryName`、独立的端口、独立的 key、独立的保险库路径。
    ///
    /// ⚠ **账号隔离靠保险库隔离**：每个池有自己的保险库文件，
    /// 所以 A 池的号天然不会出现在 B 池（它们读的是不同的文件）。
    /// 这是**结构性**保证，不是靠代码里记得去过滤。
    /// </summary>
    private async Task UpsertPoolAsync(HttpContext context)
    {
        var body = await ReadJsonObjectAsync(context).ConfigureAwait(false);
        if (body is null) return;

        var pools = LoadPools();

        var requestedId = body["id"]?.GetValue<string>() ?? "";
        var name = body["name"]?.GetValue<string>() ?? body["displayName"]?.GetValue<string>() ?? "";
        var requestedPort = body["port"] is JsonValue pv && pv.TryGetValue<int>(out var p) ? p : (int?)null;
        var apiKey = body["apiKey"]?.GetValue<string>();
        var vaultPath = body["vaultPath"]?.GetValue<string>();
        var note = body["note"]?.GetValue<string>();

        // ── 修改已有的 ────────────────────────────────────────
        var existing = pools.FirstOrDefault(p =>
            string.Equals(PoolIdOf(p), requestedId, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (name.Length > 0) existing.Name = name;
            if (apiKey is not null) existing.ApiKey = apiKey;
            if (note is not null) existing.Note = note;
            if (requestedPort is > 0 and <= 65535) existing.Port = requestedPort.Value;

            SavePools(pools);
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = true,
                ["id"] = PoolIdOf(existing),
            }).ConfigureAwait(false);
            return;
        }

        // ── 新增 ──────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(name))
        {
            await WriteErrorAsync(context, 400, "invalid_request_error", "新增池必须给 name").ConfigureAwait(false);
            return;
        }

        // 端口：用户给了就用（但要避开已分配/已被监听的），没给就自动挑。
        //
        // ⚠ `PickPort` 会同时看「已分配给别的池的端口」与「此刻有人监听的」
        //   —— 只看后者会让两个池分到同一个端口（旧版实测踩过）。
        var port = PoolRegistry.PickPort(pools, requestedPort);

        var directoryName = PoolRegistry.UniqueDirectoryName(
            PoolRegistry.MakeDirectoryName(name, port), pools);

        var created = new PoolConfig
        {
            Name = name.Trim(),
            DirectoryName = directoryName,
            Port = port,
            ApiKey = string.IsNullOrWhiteSpace(apiKey)
                ? "sk-pool-" + Guid.NewGuid().ToString("N")[..12]
                : apiKey!,
            VaultPath = vaultPath ?? "",
            Note = note,
            AutoStart = false,
        };

        // 目录名必须安全 —— 它来自我们自己的 MakeDirectoryName，理论上一定安全；
        // 这里再确认一次（防御性：万一将来有人改了生成规则）。
        if (!PoolRegistry.IsSafeDirectoryName(created.DirectoryName))
        {
            await WriteErrorAsync(context, 500, "internal_error",
                $"生成的目录名不合法：{created.DirectoryName}").ConfigureAwait(false);
            return;
        }

        pools.Add(created);
        SavePools(pools);

        if (_log.IsEnabled(LogLevel.Information))
        {
            LogPoolCreated(created.DisplayName, created.Port);
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = true,
            ["id"] = PoolIdOf(created),
            ["port"] = created.Port,
            ["vaultPath"] = VaultPathOf(created),
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// `POST /admin/pools/{id}/remove` —— 删池。
    ///
    /// ⚠ 默认池**不能删**（删了「单池用户察觉不到分池」这个保证就没了，
    /// 而且它的保险库就是主保险库）。旧版同款限制。
    ///
    /// ⚠ **不删保险库文件**：那里面是明文 token，是用户最贵的东西。
    /// 删注册表条目只让它从列表里消失，文件留在原地 —— 用户后悔了还能手工捞回来。
    /// </summary>
    private async Task RemovePoolAsync(HttpContext context, string id)
    {
        var pools = LoadPools();
        var pool = pools.FirstOrDefault(p => string.Equals(PoolIdOf(p), id, StringComparison.OrdinalIgnoreCase));

        if (pool is null)
        {
            await WriteErrorAsync(context, 404, "not_found", $"没有这个池：{id}").ConfigureAwait(false);
            return;
        }

        if (pool.IsDefault)
        {
            await WriteErrorAsync(context, 400, "invalid_request_error",
                "默认池不能删 —— 它是主保险库的入口，也是「只有一个池」用户的默认形态").ConfigureAwait(false);
            return;
        }

        pools.Remove(pool);
        SavePools(pools);

        // ★ 从 PoolRouter 内存移除（子池是扫描来的，不删的话下次 LoadPools 又合并回来，
        //   删除等于没删 —— 用户实测反馈「删除没法删除」）。
        _poolRouter?.Remove(PoolIdOf(pool));

        if (_log.IsEnabled(LogLevel.Information))
        {
            LogPoolRemoved(pool.DisplayName);
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = true,
            ["vaultKeptAt"] = VaultPathOf(pool),   // 告诉调用方：文件没删，在这儿
        }).ConfigureAwait(false);
    }

    // ── 池相关的小工具 ────────────────────────────────────────

    /// <summary>池的对外 id。默认池的空 DirectoryName 映射成 "default"（URL 里要能写）。</summary>
    private static string PoolIdOf(PoolConfig pool) => pool.IsDefault ? "default" : pool.DirectoryName;

    /// <summary>`pools.json` 的路径：日志目录旁边（和 usage.jsonl 一样，长跑数据放一起）。</summary>
    private string PoolsFilePath() => Path.Combine(_options.ResolveLogDirectory(), "pools.json");

    /// <summary>
    /// 读池列表（pools.json + PoolRouter 扫描到的子池目录）。
    ///
    /// ⚠ 子池（pools/ 下的旧版遗留目录）没有登记在 pools.json 里，
    /// 但 PoolRouter 启动时扫到了它们。这里统一合并，否则启停/编辑
    /// 会「找不到这个池」404 —— 用户实测反馈。
    /// </summary>
    private List<PoolConfig> LoadPools()
    {
        var pools = PoolRegistry.Load(PoolsFilePath(), _options.Port, out _);

        if (_poolRouter is not null)
        {
            foreach (var instance in _poolRouter.All)
            {
                if (instance.PoolId == "default") continue;
                if (pools.Any(p => string.Equals(PoolIdOf(p), instance.PoolId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                pools.Add(new PoolConfig
                {
                    Name = instance.DisplayName,
                    DirectoryName = instance.PoolId,
                    Port = 0,
                    ApiKey = instance.ApiKey,
                    VaultPath = instance.VaultPath,
                });
            }
        }

        return pools;
    }

    private void SavePools(List<PoolConfig> pools)
        => PoolRegistry.Save(PoolsFilePath(), pools);

    /// <summary>
    /// 这个池现在算不算「在跑」。
    ///
    /// · 本实例自己那个池 → 看进程级开关（`_poolRunning`）
    /// · 别的池 → 看那个端口上有没有人在监听（这是**唯一**能跨进程观察的证据）
    /// </summary>
    private bool IsPoolRunning(PoolConfig pool)
    {
        if (pool.IsDefault) return _poolRunning;

        // ★ 子池：路由级开关（PoolRouter 的 Running），不是端口探测。
        //   一个网关内多池，子池没有独立端口，探测端口永远是 false。
        var instance = _poolRouter?.All.FirstOrDefault(p =>
            string.Equals(p.PoolId, PoolIdOf(pool), StringComparison.OrdinalIgnoreCase));
        return instance?.Running ?? PoolRegistry.IsPortInUse(pool.Port);
    }

    /// <summary>
    /// 池的保险库文件路径。
    ///
    /// ## ⚠ 默认池必须用 `--vault` 解析出来的那个路径（e2e 报的真缺陷）
    ///
    /// 原先这里对**所有**池都按「池目录约定」推：
    /// ```
    /// <vault 所在目录>/<变体文件名>      例：.local/auth.cn.json
    /// ```
    /// 但默认池的号**来自 `--vault`**（例：`.local/vault-test.json`）——
    /// 两者不是同一个路径来源。于是读了一个不存在的文件：
    ///
    /// ```
    /// Vault.Read(不存在的文件) → null → ?? 0 → **静默报「0 个号」**
    /// ```
    ///
    /// **后果是「三个端点讲三个故事」**（e2e 真机实测）：
    /// ```
    /// /admin/accounts  → 1 个号（简单快乐）
    /// /metrics.recent  → accountTag = "a23db308:"（证明正在用这个号）
    /// /admin/pools     → accountCount: 0      ← 只有它说 0
    /// ```
    /// 用户会以为「默认池没号了」，而网关其实正在用。
    ///
    /// **修法**：默认池直接复用 <see cref="GatewayOptions.ResolveVaultPath"/> ——
    /// 那是**唯一的真源**（网关本来就照它读号）。非默认池才是「池目录约定」。
    ///
    /// 这条与 t53（UA 缺失被静默吞掉）同源：**「读不到」与「本来就没有」
    /// 长得一样，是最容易藏住缺陷的形状**。
    /// </summary>
    private string VaultPathOf(PoolConfig pool)
    {
        // 显式指定了路径就用它（最高优先级）
        if (!string.IsNullOrEmpty(pool.VaultPath)) return pool.VaultPath;

        // ★ 默认池：就是 `--vault` 那个路径本身，**不要**按池目录约定重推。
        //   重推会拼出一个不存在的文件名（见上面的说明）。
        if (pool.IsDefault) return _options.ResolveVaultPath();

        // 非默认池：按池目录约定（`pools/<目录名>/<变体文件名>`）
        var poolsRoot = Path.Combine(_options.ResolveLogDirectory(), "pools");

        try
        {
            var home = PoolRegistry.ResolveVaultHome(poolsRoot, _options.ResolveLogDirectory(), pool);
            return Path.Combine(home, _variant.VaultFilename);
        }
        catch (InvalidOperationException)
        {
            // 目录名不安全 → 不给路径（调用方按空处理），而不是抛出去把端点打挂
            return "";
        }
    }

    /// <summary>
    /// 这个池里有几个账号（读它自己的保险库 —— 隔离就体现在这里）。
    ///
    /// ## 为什么不能把「读失败」和「没有号」都返回 0
    ///
    /// 这正是上面那个缺陷藏了这么久的原因：`Vault.Read` 对不存在的文件返回 `null`，
    /// `?? 0` 把它变成「0 个号」—— 与「池里真的没号」**完全不可区分**。
    ///
    /// 现在区分开了：
    ///   · 文件不存在 → 返 0，但记 **Debug**（正常状态：新池就是没号）
    ///   · 读失败（IO/权限/JSON 坏）→ 返 -1 并记 **Warning**
    ///
    /// 返回值 -1 的语义是「不知道有几个」，界面该显示「读不到」而不是「0」。
    /// </summary>
    private int AccountCountOf(PoolConfig pool)
    {
        var path = VaultPathOf(pool);
        if (string.IsNullOrEmpty(path)) return -1;   // 路径都推不出来 → 不知道

        if (!File.Exists(path))
        {
            // 文件不存在是**正常状态**（新池还没导入号）—— 不吵
            if (_log.IsEnabled(LogLevel.Debug))
            {
                var display = pool.DisplayName;
                LogPoolVaultAbsent(display, path);
            }
            return 0;
        }

        try
        {
            return Core.Vault.Read(path)?.Accounts.Count ?? 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // ⚠ 读失败 ≠ 没有号。记 Warning 并返回 -1（「不知道」），不伪装成 0。
            if (_log.IsEnabled(LogLevel.Warning))
            {
                var display = pool.DisplayName;
                var reason = ex.GetType().Name;
                LogPoolVaultUnreadable(display, path, reason, ex.Message);
            }
            return -1;
        }
    }


    // ══ 日志 ══════════════════════════════════════════════════

    /// <summary>
    /// `GET /admin/logs?lines=N`
    ///
    /// App 侧的形状是 `{ "lines": [ { at, level, message } ] }`（我读了
    /// `LogListDto` 确认的）—— **不是** `RequestLog` 的原始 `LogEntry` 形状。
    /// 所以这里做一层映射。
    ///
    /// ⚠ **脱敏**：`LogEntry` 结构上就没有能装正文的字段（t2 的设计，
    /// 有反射级守卫测试钉着），所以直接映射它是安全的 —— 不用（也不能）
    /// 自己去做「过滤掉提示词」这种事：那意味着正文先进了内存。
    /// </summary>
    private async Task HandleLogsAsync(HttpContext context, string method)
    {
        if (method != HttpMethods.Get)
        {
            await WriteErrorAsync(context, 404, "not_found",
                $"no admin route for {method} {context.Request.Path}").ConfigureAwait(false);
            return;
        }

        var raw = context.Request.Query["lines"].ToString();
        var maxLines = 200;
        if (int.TryParse(raw, out var parsed) && parsed > 0)
        {
            // 上限 5000：日志端点是给界面看的，不是导出工具。
            // 不设上限的话 ?lines=2000000000 会让它去分配一个大数组。
            maxLines = Math.Min(parsed, 5000);
        }

        var lines = new JsonArray();

        if (_requestLog is not null)
        {
            // ⚠ `Tail(n)` 在 LogBuffer 上，`RequestLog.Entries` 是完整快照。
            // 这里要的是「尾部 N 条」，所以取快照再切尾 —— 不用 LogBuffer
            // 是因为 RequestLog 才是注入进来的那个（LogBuffer 是它的内部件）。
            var all = _requestLog.Entries;
            var start = Math.Max(0, all.Count - maxLines);

            for (var i = start; i < all.Count; i++)
            {
                var entry = all[i];
                lines.Add(new JsonObject
                {
                    // App: LogLineDto.At —— LogEntry.At 本身就是 ISO 字符串
                    // （不是 DateTimeOffset），直接透传即是 App 要的形状。
                    ["at"] = entry.At,
                    // App: LogLineDto.Level
                    ["level"] = entry.Level,
                    // App: LogLineDto.Message —— 一句话摘要（受控短串，无正文）
                    ["message"] = Summarize(entry),
                });
            }
        }

        await WriteJsonAsync(context, 200, new JsonObject { ["lines"] = lines }).ConfigureAwait(false);
    }

    /// <summary>
    /// 把一条 `LogEntry` 压成界面读的一行。
    ///
    /// 为什么不是直接 `entry.ToLine()`：那是给**日志文件**用的（含全部
    /// 结构化字段），而界面要的是「一行能读的话」。两者用途不同。
    /// 这里仍然只用 `LogEntry` 上那些**装不下正文**的字段拼。
    /// </summary>
    private static string Summarize(LogEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append(entry.Model.Length > 0 ? entry.Model : "(未知模型)");
        sb.Append(" → ").Append(string.IsNullOrEmpty(entry.Outcome) ? "?" : entry.Outcome);
        sb.Append(" http ").Append(entry.Status);

        if (entry.Attempts > 1) sb.Append("，尝试 ").Append(entry.Attempts).Append(" 次");
        if (!entry.Terminal) sb.Append("（中间尝试）");
        if (entry.ErrorKind.Length > 0) sb.Append('，').Append(entry.ErrorKind);
        if (entry.Detail.Length > 0) sb.Append('：').Append(entry.Detail);
        if (entry.AccountTag.Length > 0) sb.Append(" [").Append(entry.AccountTag).Append(']');
        if (entry.DurationMs > 0) sb.Append(' ').Append(entry.DurationMs).Append("ms");

        return sb.ToString();
    }

    // ══ 设置 ══════════════════════════════════════════════════

    /// <summary>
    /// ```
    /// POST /admin/accounts/export   { "passphrase": "…", "path": "…" }  导出到文件
    /// ```
    ///
    /// ## 为什么口令由调用方传、且**不落日志**
    ///
    /// 导出文件是加密的，口令是它唯一的钥匙 —— 丢了就永久打不开。
    /// 网关**不保存**口令（`AccountTransfer` 也是用完即弃），
    /// 日志里也不能出现它。
    ///
    /// ## 为什么不回"文件内容"
    ///
    /// 导出文件含**全部账号凭据**，走 HTTP 回给客户端等于多一次暴露面
    /// （可能落进浏览器缓存、代理日志）。直接写盘，只回路径。
    /// </summary>
    private async Task ExportAccountsAsync(HttpContext context)
    {
        var body = await ReadJsonObjectAsync(context).ConfigureAwait(false);
        if (body is null) return;

        var passphrase = body["passphrase"]?.GetValue<string>() ?? "";
        var path = body["path"]?.GetValue<string>() ?? "";

        if (string.IsNullOrWhiteSpace(path))
        {
            await WriteErrorAsync(context, 400, "invalid_request_error",
                "缺少 path —— 导出要写到哪里").ConfigureAwait(false);
            return;
        }

        // ⚠ 口令强度由 AccountTransfer 自己校验（最短长度那条），
        //   这里只做"非空"检查，避免两处各判一份、标准不一致。
        if (string.IsNullOrEmpty(passphrase))
        {
            await WriteErrorAsync(context, 400, "invalid_request_error",
                "缺少 passphrase —— 导出文件是加密的，没有口令它打不开").ConfigureAwait(false);
            return;
        }

        var doc = ReadVault();
        if (doc is null || doc.Accounts.Count == 0)
        {
            await WriteErrorAsync(context, 400, "invalid_request_error",
                "保险库里没有账号 —— 没有可导出的东西").ConfigureAwait(false);
            return;
        }

        try
        {
            AccountTransfer.ExportToFile(
                path, doc.Accounts, passphrase, _variant.Id, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await WriteErrorAsync(context, 400, "invalid_request_error",
                $"导出失败：{ex.Message}").ConfigureAwait(false);
            return;
        }

        if (_log.IsEnabled(LogLevel.Information))
        {
            // ⚠ 只记路径与条数，**不记口令**。
            LogAccountsExported(doc.Accounts.Count, path);
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = true,
            ["count"] = doc.Accounts.Count,
            ["path"] = path,
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// ```
    /// GET    /admin/keys           列出已签发的密钥（**只回前缀，永不回明文**）
    /// POST   /admin/keys           签发一个（**这是唯一一次能拿到明文**）
    /// DELETE /admin/keys/{id}      撤销（标记，不删记录 —— 排查"某个已撤的 key 在捣乱"要用）
    /// ```
    ///
    /// ## ⚠ 明文只出现一次 —— 这是结构性约束，接线层必须保住
    ///
    /// `KeyStore.ApiKeyRecord` 里**没有能装明文的字段**（想存都编译不过），
    /// 所以 GET 天然回不出明文。但接线时有两处会把它破坏掉：
    ///   ① 把 `IssuedApiKey.Plaintext` 记进日志
    ///   ② 在响应里加缓存/回显字段
    /// 这里两处都刻意避开了：日志只记前缀，响应只有 POST 那一次带明文。
    /// </summary>
    private async Task HandleKeysAsync(HttpContext context, string method, string[] segments)
    {
        if (_keys is null)
        {
            await WriteErrorAsync(context, 503, "unavailable",
                "密钥存储没有配置（这个实例没接 KeyStore）").ConfigureAwait(false);
            return;
        }

        // GET /admin/keys
        if (method == HttpMethods.Get && segments.Length == 2)
        {
            var array = new JsonArray();
            foreach (var rec in _keys.List())
            {
                array.Add(KeyToJson(rec));
            }

            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["keys"] = array,
            }).ConfigureAwait(false);
            return;
        }

        // POST /admin/keys  { "name": "我的笔记本" }
        if (method == HttpMethods.Post && segments.Length == 2)
        {
            var body = await ReadJsonObjectAsync(context).ConfigureAwait(false);
            var name = body?["name"]?.GetValue<string>() ?? "";
            if (name.Length > 64) name = name[..64];

            var issued = _keys.Issue(name);

            if (_log.IsEnabled(LogLevel.Information))
            {
                // ⚠ **只记前缀**。`IssuedApiKey.ToString()` 也被刻意写成只输出前缀
                //   （防"顺手 log 一下"），这里同理。
                var prefix = issued.Record.Prefix;
                LogKeyIssued(prefix);
            }

            await WriteJsonAsync(context, 201, new JsonObject
            {
                // ★ 明文只在这里出现一次。之后任何接口都拿不到它。
                ["plaintext"] = issued.Plaintext,
                ["key"] = KeyToJson(issued.Record),
            }).ConfigureAwait(false);
            return;
        }

        // DELETE /admin/keys/{id}
        if (method == HttpMethods.Delete && segments.Length == 3)
        {
            var id = Uri.UnescapeDataString(segments[2]);
            var ok = _keys.Revoke(id);

            if (!ok)
            {
                await WriteErrorAsync(context, 404, "not_found",
                    $"没有这个密钥：{id}（可能已被撤销，或者 id 不对）").ConfigureAwait(false);
                return;
            }

            if (_log.IsEnabled(LogLevel.Information))
            {
                var shortId = id.Length > 8 ? id[..8] : id;
                LogKeyRevoked(shortId);
            }

            await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true }).ConfigureAwait(false);
            return;
        }

        await WriteErrorAsync(context, 404, "not_found",
            $"no admin route for {method} {context.Request.Path}").ConfigureAwait(false);
    }

    /// <summary>把一条密钥记录转成 JSON —— **不含明文，也不含哈希**。</summary>
    private static JsonObject KeyToJson(ApiKeyRecord rec) => new()
    {
        ["id"] = rec.Id,
        ["name"] = rec.Name,
        ["prefix"] = rec.Prefix,
        ["createdAt"] = rec.CreatedAt,
        ["revokedAt"] = rec.RevokedAt,
        ["lastUsedAt"] = rec.LastUsedAt,
        ["active"] = rec.Active,
    };

    // ══ 池健康矩阵 ════════════════════════════════════════════

    /// <summary>
    /// ```
    /// GET /admin/pool-health   模型 × 账号 的可用性矩阵
    /// ```
    ///
    /// ## 为什么需要它（用户实测反馈）
    ///
    /// 用户原话：「我那二十个号基本 deepseek-flash 模型都限流了，
    /// **你显示全绿池也健康**」。
    ///
    /// 实测确认他是对的：
    /// <code>
    /// 请求 deepseek-v4.1-flash 连续 5 次：
    ///   第 1-3 次 → 实际用 glm-5.3（被降级）
    ///   第 4-5 次 → 实际用 deepseek-v4.1-flash（恢复）
    /// 而同期 /admin/accounts 全程显示「冷却=0 失败=0 冷冻=False」
    /// </code>
    ///
    /// **根因**：`/admin/accounts` 的 `cooldowns` 只反映**账号这一侧**的
    /// 记录，而"某个模型被限流"这件事在它恢复之后**什么痕迹都不留** ——
    /// 用户看到的是"全绿"，体验到的是"老是换模型"。
    ///
    /// ⇒ 这个端点回答另一个问题：**「现在这一刻，哪些号还能用某个模型」**。
    ///   它直接问池子（`CanPick`），不依赖"有没有留下记录"。
    /// </summary>
    private async Task PoolHealthAsync(HttpContext context)
    {
        // ⚠ 整个块包一层 try/catch。
        //
        // 实测踩过：这里一旦抛异常 ⇒ 客户端拿到 **500 且 body 为空**，
        // 界面无法判断发生了什么 —— 只能静默不渲染面板。
        //
        // 「可观测性端点自己不可观测」是最糟糕的一族静默失败。
        // 抛异常要**落到日志 + 响应里**。
        try
        {
            await PoolHealthCoreAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_log.IsEnabled(LogLevel.Warning)) LogPoolHealthFailed(ex, ex.Message);

            await WriteErrorAsync(context, 500, "pool_health_failed",
                $"读池健康失败：{ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task PoolHealthCoreAsync(HttpContext context)
    {
        var models = new JsonArray();
        var anyCooling = false;
        var totalAccounts = _pool?.Members.Count ?? 0;

        // ★ **降级统计**（用户实测反馈「基本都限流了，你显示全绿」）。
        //
        // ## 为什么必须单独统计，而不是靠 `cooldowns`
        //
        // `AccountPool.MarkSuccess` 成功一次就 `Cooldowns.Remove(model)` ——
        // 于是「刚才被限流、靠降级救回来了」这件事**不留痕迹**：
        //   用户看到的：全绿、池健康
        //   实际发生的：请求 deepseek-flash → 连续 3 次被换成 glm-5.3
        //
        // ⇒ 冷却表回答"现在能不能用"，**降级统计回答"最近有多常不能用"**。
        //   前者是状态，后者是趋势 —— 用户要判断"这个模型是不是废了"看的是后者。
        // ⚠ 用 `Snapshot().Recent` —— `IMetricsSource` 只暴露快照，
        //   没有单独的 "Recent()" 方法（快照是**一致性读**：读的过程
        //   不会看到写了一半的状态）。
        var fallbacks = new Dictionary<string, int>(StringComparer.Ordinal);
        if (_metrics is not null)
        {
            foreach (var rec in _metrics.Snapshot().Recent)
            {
                // 请求的模型 ≠ 实际用的模型 ⇒ 这一次被降级了
                if (!string.IsNullOrEmpty(rec.UsedModel) &&
                    !string.IsNullOrEmpty(rec.Model) &&
                    !string.Equals(rec.Model, rec.UsedModel, StringComparison.Ordinal))
                {
                    fallbacks.TryGetValue(rec.Model, out var n);
                    fallbacks[rec.Model] = n + 1;
                }
            }
        }

        foreach (var model in _catalog?.Models.Select(m => m.Id).Distinct(StringComparer.Ordinal) ?? [])
        {
            var usable = 0;
            var cooling = new JsonArray();

            if (_pool is not null)
            {
                foreach (var m in _pool.Members)
                {
                    if (_pool.CanPick(m.Id, model)) usable++;
                    else cooling.Add(m.Label);
                }
            }

            if (cooling.Count > 0) anyCooling = true;

            fallbacks.TryGetValue(model, out var fellBack);

            // ★ **限流历史**（用户实测反馈：「应该告诉用户这些模型限流了、
            //   啥时候解除限流」）。
            //
            // ⚠ 它与 `cooling` 是两回事，两个都要：
            //   · `cooling`        —— 此刻正在冷却的号（瞬时，决定"现在能不能用"）
            //   · `rateLimited*`   —— **曾经**被限流过的号（历史，成功不清）
            //
            // 只看前者会得到用户抱怨的那个现象：降级救回来之后
            // `MarkSuccess` 清掉冷却 ⇒ 界面「全绿」，而用户体感"老是换模型"。
            var limited = 0;
            var recovered = 0;
            long lastReset = 0;
            var coolingNow = false;

            if (_pool is not null)
            {
                var summary = _pool.RateLimitSummaryByModel();
                if (summary.TryGetValue(model, out var s))
                {
                    limited = s.LimitedAccounts;
                    recovered = s.RecoveredAccounts;
                    lastReset = s.LastResetAtMs;
                    coolingNow = s.AnyCoolingNow;
                }
            }

            models.Add(new JsonObject
            {
                ["model"] = model,
                ["usableCount"] = usable,
                ["total"] = totalAccounts,
                // ⚠ 只回**没在冷却的号名**：可用号的完整列表在
                //   32 个模型 × 20 个号下会有几百条，界面用不上；
                //   而"哪些号不能用"恰恰是用户要看的（他要去查那些号）。
                ["cooling"] = cooling,
                // 最近 200 次请求里，这个模型被降级了多少次。
                // **这是唯一能看出"平时老换模型"的字段** —— 冷却表看不到它。
                ["fallbackCount"] = fellBack,
                // 限流历史：多少号限过、多少号已恢复、最晚何时解除
                ["rateLimitedAccounts"] = limited,
                ["recoveredAccounts"] = recovered,
                ["lastResetAtMs"] = lastReset,
                ["coolingNow"] = coolingNow,
            });
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["accountCount"] = totalAccounts,
            // 界面上那句总结要用的：**有任何一个模型在冷却就为 false**
            ["allHealthy"] = !anyCooling,
            ["models"] = models,
        }).ConfigureAwait(false);
    }

    // ══ 设置 ══════════════════════════════════════════════════

    private async Task HandleSettingsAsync(HttpContext context, string method)
    {
        if (method == HttpMethods.Get)
        {
            await WriteJsonAsync(context, 200, new JsonObject
            {
                // App: GatewaySettings.Port
                ["port"] = _settings.Port,
                // App: ApiKey
                ["apiKey"] = _settings.ApiKey,
                // App: VariantId
                ["variantId"] = _settings.VariantId,
                // App: VaultPath
                ["vaultPath"] = _settings.VaultPath,
                // App: LogPath
                ["logPath"] = _settings.LogPath,
            }).ConfigureAwait(false);
            return;
        }

        if (method != HttpMethods.Post)
        {
            await WriteErrorAsync(context, 404, "not_found",
                $"no admin route for {method} {context.Request.Path}").ConfigureAwait(false);
            return;
        }

        // App: SaveSettingsAsync 发的体就是 GatewaySettings 本身（不是包装过的）
        var body = await ReadJsonObjectAsync(context).ConfigureAwait(false);
        if (body is null) return;

        if (body["port"] is JsonValue pv && pv.TryGetValue<int>(out var port))
        {
            if (port is <= 0 or > 65535)
            {
                await WriteErrorAsync(context, 400, "invalid_request_error",
                    $"端口必须在 1-65535 之间，收到 {port}").ConfigureAwait(false);
                return;
            }
            _settings.Port = port;
        }

        if (body["variantId"] is JsonValue vv && vv.TryGetValue<string>(out var variantId) && variantId.Length > 0)
        {
            // 变体必须是已知的 —— 存一个不认识的进去，下次启动会直接抛。
            if (UpstreamVariant.Find(variantId) is null)
            {
                await WriteErrorAsync(context, 400, "invalid_request_error",
                    $"未知变体：{variantId}（可选 {string.Join(" / ", UpstreamVariant.All.Select(v => v.Id))}）")
                    .ConfigureAwait(false);
                return;
            }
            _settings.VariantId = variantId;
        }

        if (body["apiKey"] is JsonValue av && av.TryGetValue<string>(out var apiKey))
        {
            _settings.ApiKey = apiKey;

            // ★ **立即生效**（不是只改内存副本）——
            //   鉴权（GatewayApp.Authorized）读的是 `_options.ApiKey`，
            //   而 `_options` 是同一个 GatewayOptions 实例（Program.cs 注册的 singleton）。
            //   这里不同步的话：用户保存了新 key，紧接着的请求还是按旧 key 鉴权
            //   → 客户端用新 key 访问 → 401「网关要求 API key 而当前没有或不对」。
            //   用户看到的就是「保存没生效」。
            //
            //   空串 = 关闭鉴权（与 GatewayApp.Authorized 的判空语义一致）。
            _options.ApiKey = apiKey;
        }
        if (body["vaultPath"] is JsonValue vp && vp.TryGetValue<string>(out var vaultPath) && vaultPath.Length > 0) _settings.VaultPath = vaultPath;
        if (body["logPath"] is JsonValue lp && lp.TryGetValue<string>(out var logPath) && logPath.Length > 0) _settings.LogPath = logPath;

        // ★ **落盘**（用户实测反馈的"重启即丢"）。
        //
        // 在 M14 之前，这里改的只是内存副本 —— 用户改了端口/key，
        // 重启网关就全没了，而界面上**看不出任何异常**（改的时候是成功的）。
        // 这是"看起来成功了、结果没保存"的典型静默失败。
        //
        // ⚠ **只落用户显式改过的字段**：命令行 / 环境变量传进来的临时值不写盘，
        //   否则下次启动用户没传命令行、却从文件里读到上次的临时值 —— 行为无法解释。
        //   判据在 `SettingsStore.ToDocument(persistable)`（t46 交付）。
        PersistSettings(body);

        if (_log.IsEnabled(LogLevel.Warning))
        {
            LogSettingsChanged(_settings.Port, _settings.VariantId);
        }

        await WriteOkAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// 把本次请求里**显式出现**的字段落盘。
    ///
    /// ## 为什么按"本次请求里出现过的键"来算
    ///
    /// 这是"用户显式改过"最直接的判据：他在这次 POST 里传了 `port`，
    /// 就说明他想改端口；没传的字段保持文件里原样。
    ///
    /// ⚠ 与"把当前内存值全量写盘"的区别很大：后者会把**命令行传进来的临时值**
    ///   也固化下来，下次启动读到的就不是用户以为的那个配置。
    /// </summary>
    private void PersistSettings(JsonObject body)
    {
        if (_settingsStore is null) return;

        // 这些键 → 落盘文档里的字段名（一一对应）
        // ⚠ `vaultPath` / `logPath` 不在 SettingsDocument 里 —— 它们是**路径**，
        //   由启动参数决定，不参与"用户设置持久化"（见 SettingsDocument 的注释）。
        var persistable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in new[] { "port", "variantId", "apiKey", "defaultModel" })
        {
            if (body[key] is not null) persistable.Add(key);
        }

        if (persistable.Count == 0) return;

        try
        {
            var doc = _settingsStore.Load();
            _settingsStore.Save(new SettingsDocument
            {
                // 只覆盖本次改过的字段，其余保留文件里的原值
                Port = persistable.Contains("port") ? _settings.Port : doc.Port,
                Host = doc.Host,
                ApiKey = persistable.Contains("apiKey") ? _settings.ApiKey : doc.ApiKey,
                Provider = persistable.Contains("variantId") ? _settings.VariantId : doc.Provider,
                AuthFile = doc.AuthFile,
                // ⚠ `GatewaySettingsState` 里**没有** DefaultModel（它是启动参数，
                //   不在运行时可改的设置面上）—— 保持文件里的原值。
                DefaultModel = doc.DefaultModel,
                LogDir = doc.LogDir,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // ⚠ **落盘失败不影响本次响应**：内存里的值已经改好了，
            //   用户这次操作是成功的。但必须记日志 ——
            //   否则"改成功了、重启没了"就无从排查。
            if (_log.IsEnabled(LogLevel.Warning))
            {
                LogSettingsPersistFailed(ex, _settingsStore.Path);
            }
        }
    }

    // ══ 保险库读写 ════════════════════════════════════════════

    /// <summary>读保险库。读不到返回 null（不是空文档）—— 调用方要区分「没建库」和「库是空的」。</summary>
    private VaultDocument? ReadVault()
    {
        try
        {
            return Vault.Read(_settings.VaultPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            if (_log.IsEnabled(LogLevel.Error)) LogVaultReadFailed(ex, _settings.VaultPath);
            return null;
        }
    }

    /// <summary>
    /// 原子写保险库。
    ///
    /// ⚠ **必须走 <see cref="Vault.Write"/>** —— 它是「临时文件 + 原子替换」，
    /// 直接 `File.WriteAllText` 覆盖的话，写到一半断电就是半个 JSON，
    /// 用户的全部账号没了（HANDOVER 第六节第 6 条）。
    /// </summary>
    private void WriteVault(VaultDocument doc)
    {
        Vault.Write(_settings.VaultPath, doc);
    }

    private static string Iso(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    // ══ 输出 ══════════════════════════════════════════════════

    /// <summary>App 的 PostAsync 只看状态码，不看体 —— 回一个最小的 ok 即可。</summary>
    private static Task WriteOkAsync(HttpContext context)
        => WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true });

    private static async Task WriteJsonAsync(HttpContext context, int status, JsonNode body)
    {
        // 与 GatewayApp 同一套输出约定：宽松编码器（不转义中文）。
        var payload = body.ToJsonString(RelaxedJson);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(payload);
        await context.Response.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string kind, string message)
        => WriteJsonAsync(context, status, new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = kind,
                ["code"] = kind,
            },
        });

    /// <summary>
    /// 输出用序列化选项：**不转义中文**（与 GatewayApp 同一套约定）。
    ///
    /// ## ⚠ 必须显式给 `TypeInfoResolver` —— 否则嵌套 JsonNode 会抛异常
    ///
    /// 实测踩过（用户反馈「账号页没有模型限流面板」）：
    /// <code>
    /// JsonSerializerOptions instance must specify a TypeInfoResolver setting
    /// before being marked as read-only.
    ///   at JsonValueCustomized`1.WriteTo
    ///   at JsonArray.WriteTo / JsonObject.WriteTo
    /// </code>
    ///
    /// **只有 `/admin/pool-health` 触发** —— 因为它是唯一一个把
    /// `JsonArray`（内含嵌套 `JsonArray`）装进外层 `JsonObject` 的端点：
    /// 序列化嵌套节点时会走到 `JsonValueCustomized`，那条路径**要求 resolver**；
    /// 而只装 `string`/`int` 的端点走的是另一条（不需要）。
    ///
    /// ⇒ 缺 resolver 的写法**编译通过、只装标量的端点也正常**，
    ///   只有嵌套结构才在**运行时**炸 —— 这正是"静默失败"那一族的又一次现身。
    /// </summary>
    private static readonly JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // ⚠ 这一行不能删（理由见上）。
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>读请求体为 JsonObject。畸形 JSON / 非对象时**自己写错误响应**并返回 null。</summary>
    private static async Task<JsonObject?> ReadJsonObjectAsync(HttpContext context)
    {
        string raw;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
        {
            raw = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }

        // App 的 PostAsync 对 `new { }` 会发 `{}`；也有客户端发空体。
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();

        try
        {
            return JsonNode.Parse(raw) as JsonObject;
        }
        catch (JsonException)
        {
            await WriteErrorAsync(context, 400, "invalid_request_error",
                "request body is not valid JSON").ConfigureAwait(false);
            return null;
        }
    }

    // ══ 进程级状态（本实例那一个池） ═══════════════════════════

    /// <summary>本实例唯一那个池的 id。固定值 —— 它是「这个进程」的标识，不是数据。</summary>
    public const string DefaultPoolId = "default";

    private volatile bool _poolRunning = true;

    /// <summary>池现在是不是在服务。false 时 <see cref="GatewayApp"/> 会把聊天请求回 503。</summary>
    public bool PoolRunning => _poolRunning;

    private static bool IsKnownPool(string id)
        => string.Equals(id, DefaultPoolId, StringComparison.OrdinalIgnoreCase);

    // ══ 日志 ══════════════════════════════════════════════════

    [LoggerMessage(Level = LogLevel.Warning, Message = "手动续期未实现，已回 501（账号 {Id}）")]
    private partial void LogRefreshNotImplemented(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "账号 {Id} 续期成功，{Seconds} 秒后到期")]
    private partial void LogRefreshDone(string id, int seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "导入账号（{Action}）：{Label}")]
    private partial void LogImported(string action, string label);

    [LoggerMessage(Level = LogLevel.Information, Message = "池 {Id} 的 running 改为 {Running}")]
    private partial void LogPoolState(string id, bool running);

    // 新增/删除池。（t58 半成品收尾：调用点已在 UpsertPoolAsync/RemovePoolAsync，
    // 但这两个 partial 声明被打断漏了 —— 补上即恢复编译。）
    [LoggerMessage(Level = LogLevel.Information, Message = "新增池 {DisplayName}（端口 {Port}）")]
    private partial void LogPoolCreated(string displayName, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "删除池 {DisplayName}")]
    private partial void LogPoolRemoved(string displayName);

    // ── 「读不到」与「本来就没有」必须分开（e2e 报的那条缺陷的同源教训）──
    [LoggerMessage(Level = LogLevel.Debug, Message = "池 {DisplayName} 的保险库还没建：{Path}（新池的正常状态）")]
    private partial void LogPoolVaultAbsent(string displayName, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "池 {DisplayName} 的保险库读不了（{Reason}）：{Path} —— {Detail}；账号数按「未知」处理，不伪装成 0")]
    private partial void LogPoolVaultUnreadable(string displayName, string path, string reason, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "设置已变更（端口={Port} 变体={Variant}）—— 注意：对正在运行的进程不生效，需重启网关")]
    private partial void LogSettingsChanged(int port, string variant);

    [LoggerMessage(Level = LogLevel.Error, Message = "读保险库失败：{Path}")]
    private partial void LogVaultReadFailed(Exception ex, string path);

    // ⚠ 只记前缀，**永远不记明文** —— 明文只出现一次，进了日志就等于永久泄露。
    [LoggerMessage(Level = LogLevel.Information, Message = "签发 API Key（前缀 {Prefix}…）")]
    private partial void LogKeyIssued(string prefix);

    [LoggerMessage(Level = LogLevel.Information, Message = "撤销 API Key（id 前 8 位 {Id}）")]
    private partial void LogKeyRevoked(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "设置落盘失败（{Path}）—— 本次改动只存在内存里，重启会丢")]
    private partial void LogSettingsPersistFailed(Exception ex, string path);

    // ⚠ **不记口令**。导出文件加密，口令是唯一钥匙，进了日志就等于泄露。
    [LoggerMessage(Level = LogLevel.Information, Message = "导出 {Count} 个账号到 {Path}")]
    private partial void LogAccountsExported(int count, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "读池健康失败：{Message}")]
    private partial void LogPoolHealthFailed(Exception ex, string message);
}

/// <summary>
/// 设置的当前值（<see cref="AdminEndpoints"/> 的内存副本）。
/// 单独一个类型是为了让「这是可变状态」这件事在类型上看得见。
/// </summary>
internal sealed class GatewaySettingsState
{
    public int Port { get; set; }
    public string ApiKey { get; set; } = "";
    public string VariantId { get; set; } = "";
    public string VaultPath { get; set; } = "";
    public string LogPath { get; set; } = "";
}
