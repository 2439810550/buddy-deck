using System.Text.Json;
using System.Text.Json.Serialization;
using BuddyDeckHub;

// ─────────────────────────────────────────────────────────────
//  BuddyDeck Hub —— 把本机多个 workbuddy_api 实例聚合成多租户中转站
//
//  使用：
//      dotnet run                          # 开发
//      BuddyDeckHub.exe                    # 默认监听 config 里的地址
//      BuddyDeckHub.exe --urls http://0.0.0.0:8890
//
//  数据目录（默认 %APPDATA%\BuddyDeckHub）：
//      config.json   渠道 / 成员 / 分组
//      usage.db      逐条调用日志 + 每日用量
// ─────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

var dataDir = builder.Configuration["data-dir"]
              ?? Environment.GetEnvironmentVariable("BUDDYDECK_HUB_HOME")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BuddyDeckHub");

var store = new HubStore(dataDir);
await store.LoadAsync();
await store.InitDatabaseAsync();

if (store.Config.AdminKey == "")
{
    store.Config.AdminKey = "adm-" + Convert.ToBase64String(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    await store.SaveAsync();
    Console.WriteLine($"[hub] 已生成管理密钥：{store.Config.AdminKey}");
    Console.WriteLine($"[hub] 数据目录：{dataDir}");
}

builder.Services.AddSingleton(store);
builder.Services.AddSingleton<ChannelRouter>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<ProxyService>();
builder.Services.AddHostedService<ChannelSupervisor>();

builder.Services.AddHttpClient("upstream")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // 连接池：多成员并发时不至于每次都新建连接
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 64,
        // 流式响应不能自动解压，否则会缓冲
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        AllowAutoRedirect = false,
    });

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

builder.WebHost.UseUrls(store.Config.Listen);

var app = builder.Build();

var log = app.Logger;
log.LogInformation("BuddyDeck Hub 启动 —— {Listen}", store.Config.Listen);
log.LogInformation("数据目录 {Dir}，渠道 {Channels} 个，成员 {Members} 人",
    dataDir, store.Config.Channels.Count, store.Config.Members.Count);

// ─────────────────────────────────────────────────────────────
//  健康检查（不鉴权，供反代和监控用）
// ─────────────────────────────────────────────────────────────
app.MapGet("/healthz", (ChannelRouter router, HubStore s) => Results.Ok(new
{
    ok = true,
    service = "buddydeck-hub",
    version = "1.0.0",
    channels = s.Config.Channels.Count,
    members = s.Config.Members.Count,
    healthyChannels = router.Health().Count(IsHealthy),
}));

static bool IsHealthy(object o)
{
    var p = o.GetType().GetProperty("available");
    return p?.GetValue(o) is true;
}

app.MapGet("/", () => Results.Ok(new
{
    service = "BuddyDeck Hub",
    docs = "所有 /v1/* 接口需要 Authorization: Bearer <你的 key>",
    endpoints = new[]
    {
        "POST /v1/chat/completions   OpenAI Chat（pi / Cline / Aider / 任意 OpenAI 客户端）",
        "POST /v1/messages           Anthropic Messages（Claude Code）",
        "POST /v1/responses          OpenAI Responses（Codex）",
        "GET  /v1/models             可用模型",
        "POST /v1/messages/count_tokens",
    },
    admin = "/admin/* 需要 X-Admin-Key",
}));

// ─────────────────────────────────────────────────────────────
//  /v1/* —— 代理入口
// ─────────────────────────────────────────────────────────────
app.Map("/v1/{**rest}", async (
    HttpContext http,
    string? rest,
    AuthService auth,
    ProxyService proxy,
    HubStore s,
    CancellationToken ct) =>
{
    var presented = BearerOf(http.Request.Headers.Authorization.ToString());
    var authed = auth.Authenticate(presented, http.Connection.RemoteIpAddress, out var fail);

    if (authed is null)
    {
        http.Response.StatusCode = fail == AuthService.Fail.MissingKey ? 401 : 401;
        http.Response.Headers.WWWAuthenticate = "Bearer realm=\"buddydeck\"";
        await http.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message = AuthService.FailMessage(fail),
                type = "invalid_api_key",
                code = "invalid_api_key",
            }
        }, ct);
        return;
    }

    var path = http.Request.Path.ToString();
    rest ??= "";

    // /v1/models 由 Hub 直接回答 —— 它知道分组能看哪些，比转发更准
    if (http.Request.Method == "GET" && rest.TrimEnd('/') == "models")
    {
        var models = await ListModelsAsync(s, authed.Group, ct);
        await http.Response.WriteAsJsonAsync(new { @object = "list", data = models }, ct);
        return;
    }

    // /healthz 之类不该走代理
    await proxy.ForwardAsync(http, new ProxyService.ProxyContext(
        authed.Member, authed.Token, authed.Group, path, http.Request.QueryString.Value ?? ""), ct);
});

app.MapMethods("/v1/{**rest}", ["OPTIONS"], (HttpContext http) =>
{
    // CORS 预检。默认只回本机页面用得到的那几个头，不用 "*"。
    var origin = http.Request.Headers.Origin.ToString();
    if (origin != "")
    {
        http.Response.Headers.AccessControlAllowOrigin = origin;
        http.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type, X-Admin-Key";
        http.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
        http.Response.Headers.AccessControlMaxAge = "600";
    }
    http.Response.StatusCode = 204;
    return Task.CompletedTask;
});

// ─────────────────────────────────────────────────────────────
//  /admin/* —— 管理接口
//
//  管理接口**只绑回环时**可用。和 workbuddy_api 一个道理：
//  管理面不该出现在公网入口上。要远程管理就走 SSH 隧道。
// ─────────────────────────────────────────────────────────────
var admin = app.MapGroup("/admin");

admin.AddEndpointFilter(async (ctx, next) =>
{
    var http = ctx.HttpContext;

    var remote = http.Connection.RemoteIpAddress;
    if (remote is not null && !System.Net.IPAddress.IsLoopback(remote))
    {
        http.Response.StatusCode = 403;
        await http.Response.WriteAsJsonAsync(new { error = "管理接口只能从本机访问" });
        return null;
    }

    var key = http.Request.Headers["X-Admin-Key"].ToString();
    if (key != store.Config.AdminKey)
    {
        http.Response.StatusCode = 401;
        await http.Response.WriteAsJsonAsync(new { error = "管理密钥不对" });
        return null;
    }

    return await next(ctx);
});

admin.MapGet("/overview", async (HubStore s, ChannelRouter router) =>
{
    var day = DateTime.Now.ToString("yyyy-MM-dd");
    var usage = await s.DashboardAsync(day);

    return Results.Ok(new
    {
        day,
        channels = router.Health(),
        members = s.Config.Members.Select(m =>
        {
            var u = usage.FirstOrDefault(x => (string?)x["memberId"] == m.Id);
            return new
            {
                id = m.Id,
                name = m.Name,
                note = m.Note,
                group = m.Group,
                groupName = s.Config.Groups.FirstOrDefault(g => g.Id == m.Group)?.Name ?? m.Group,
                enabled = m.Enabled,
                dailyQuota = m.DailyQuota,
                usedToday = u?["quota"] ?? 0L,
                callsToday = u?["calls"] ?? 0L,
                tokensToday = (long)(u?["promptTokens"] ?? 0L) + (long)(u?["completionTokens"] ?? 0L),
                tokens = m.Tokens.Select(t => new
                {
                    id = t.Id,
                    prefix = t.Prefix,
                    name = t.Name,
                    enabled = t.Enabled,
                    lastUsedAt = t.LastUsedAt?.ToString("MM-dd HH:mm"),
                    expiresAt = t.NeverExpires ? "永久" : t.ExpiresAt?.ToString("yyyy-MM-dd"),
                }),
            };
        }),
        groups = s.Config.Groups,
        rates = QuotaCalculator.RateTable(),
    });
});

admin.MapGet("/calls", async (HubStore s, int? limit) =>
    Results.Ok(await s.RecentCallsAsync(limit ?? 100)));

// ── 成员管理 ──

admin.MapPost("/members", async (HubStore s, AuthService auth, MemberInput input) =>
{
    var m = new Member
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        Name = input.Name ?? "未命名",
        Note = input.Note ?? "",
        Group = input.Group ?? "default",
        DailyQuota = input.DailyQuota ?? 5_000_000,
    };
    s.Config.Members.Add(m);
    await s.SaveAsync();
    auth.Invalidate();
    return Results.Ok(new { id = m.Id, name = m.Name });
});

admin.MapPatch("/members/{id}", async (HubStore s, AuthService auth, string id, MemberInput input) =>
{
    var m = s.Config.Members.FirstOrDefault(x => x.Id == id);
    if (m is null) return Results.NotFound(new { error = "没这个成员" });

    if (input.Name is not null) m.Name = input.Name;
    if (input.Note is not null) m.Note = input.Note;
    if (input.Group is not null) m.Group = input.Group;
    if (input.DailyQuota is not null) m.DailyQuota = input.DailyQuota.Value;
    if (input.Enabled is not null) m.Enabled = input.Enabled.Value;
    if (input.RateLimitPerMinute is not null) m.RateLimitPerMinute = input.RateLimitPerMinute.Value;

    await s.SaveAsync();
    auth.Invalidate();
    return Results.Ok(new { ok = true });
});

admin.MapDelete("/members/{id}", async (HubStore s, AuthService auth, string id) =>
{
    var m = s.Config.Members.FirstOrDefault(x => x.Id == id);
    if (m is null) return Results.NotFound(new { error = "没这个成员" });
    s.Config.Members.Remove(m);
    await s.SaveAsync();
    auth.Invalidate();
    return Results.Ok(new { ok = true });
});

// ── 令牌管理 ──

admin.MapPost("/members/{id}/tokens", async (HubStore s, AuthService auth, string id, TokenInput? input) =>
{
    var m = s.Config.Members.FirstOrDefault(x => x.Id == id);
    if (m is null) return Results.NotFound(new { error = "没这个成员" });

    // 明文只在这一刻返回一次，库里只留哈希
    var plaintext = TokenHasher.GenerateToken();
    var t = new ApiToken
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        Prefix = TokenHasher.PrefixOf(plaintext),
        Hash = TokenHasher.Hash(plaintext),
        Name = input?.Name ?? $"{m.Name} 的 key",
        ExpiresAt = input?.ExpiresAt,
        NeverExpires = input?.ExpiresAt is null,
        Models = input?.Models ?? [],
    };
    m.Tokens.Add(t);
    await s.SaveAsync();
    auth.Invalidate();

    return Results.Ok(new
    {
        id = t.Id,
        prefix = t.Prefix,
        key = plaintext,
        warning = "这个 key 只显示这一次，请立刻复制保存。库里只存了哈希，丢了无法找回。",
    });
});

admin.MapDelete("/members/{id}/tokens/{tokenId}", async (HubStore s, AuthService auth, string id, string tokenId) =>
{
    var m = s.Config.Members.FirstOrDefault(x => x.Id == id);
    var t = m?.Tokens.FirstOrDefault(x => x.Id == tokenId);
    if (m is null || t is null) return Results.NotFound(new { error = "没这把令牌" });
    m.Tokens.Remove(t);
    await s.SaveAsync();
    auth.Invalidate();
    return Results.Ok(new { ok = true });
});

admin.MapPost("/tokens/{tokenId}/toggle", async (HubStore s, AuthService auth, string tokenId) =>
{
    foreach (var m in s.Config.Members)
    {
        var t = m.Tokens.FirstOrDefault(x => x.Id == tokenId);
        if (t is null) continue;
        t.Enabled = !t.Enabled;
        await s.SaveAsync();
        auth.Invalidate();
        return Results.Ok(new { enabled = t.Enabled });
    }
    return Results.NotFound(new { error = "没这把令牌" });
});

// ── 渠道管理 ──

admin.MapPost("/channels", async (HubStore s, ChannelInput input) =>
{
    var c = new Channel
    {
        Id = input.Id ?? Guid.NewGuid().ToString("N")[..12],
        Name = input.Name ?? "未命名渠道",
        BaseUrl = input.BaseUrl ?? "http://127.0.0.1:8787",
        ApiKey = input.ApiKey ?? "",
        DataHome = input.DataHome ?? "",
        NodePath = input.NodePath ?? "",
        ScriptDir = input.ScriptDir ?? "",
        Priority = input.Priority ?? 0,
        Models = input.Models ?? [],
        Groups = input.Groups ?? [],
    };
    s.Config.Channels.Add(c);
    await s.SaveAsync();
    return Results.Ok(new { id = c.Id });
});

admin.MapPatch("/channels/{id}", async (HubStore s, string id, ChannelInput input) =>
{
    var c = s.Config.Channels.FirstOrDefault(x => x.Id == id);
    if (c is null) return Results.NotFound(new { error = "没这个渠道" });

    if (input.Name is not null) c.Name = input.Name;
    if (input.ApiKey is not null) c.ApiKey = input.ApiKey;
    if (input.Priority is not null) c.Priority = input.Priority.Value;
    if (input.Enabled is not null) c.Enabled = input.Enabled.Value;
    if (input.Models is not null) c.Models = input.Models;

    await s.SaveAsync();
    return Results.Ok(new { ok = true });
});

admin.MapDelete("/channels/{id}", async (HubStore s, string id) =>
{
    var c = s.Config.Channels.FirstOrDefault(x => x.Id == id);
    if (c is null) return Results.NotFound(new { error = "没这个渠道" });
    s.Config.Channels.Remove(c);
    await s.SaveAsync();
    return Results.Ok(new { ok = true });
});

// 探测一个渠道真实可用性：发一个最小请求
admin.MapPost("/channels/{id}/probe", async (HubStore s, IHttpClientFactory f, string id, CancellationToken ct) =>
{
    var c = s.Config.Channels.FirstOrDefault(x => x.Id == id);
    if (c is null) return Results.NotFound(new { error = "没这个渠道" });

    var client = f.CreateClient("upstream");
    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, c.BaseUrl.TrimEnd('/') + "/v1/models");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + c.ApiKey);
        req.Headers.Host = new Uri(c.BaseUrl).Authority;

        var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            var n = doc.RootElement.GetProperty("data").GetArrayLength();
            return Results.Ok(new { ok = true, models = n });
        }
        return Results.Ok(new { ok = false, status = (int)resp.StatusCode, body = body[..Math.Min(300, body.Length)] });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { ok = false, error = ex.Message });
    }
});

// ── 分组 ──

admin.MapPost("/groups", async (HubStore s, GroupInput input) =>
{
    if (input.Id is null) return Results.BadRequest(new { error = "要给个 id" });
    if (s.Config.Groups.Any(g => g.Id == input.Id))
        return Results.Conflict(new { error = "这个分组已经存在" });

    s.Config.Groups.Add(new Group
    {
        Id = input.Id,
        Name = input.Name ?? input.Id,
        Rate = input.Rate ?? 1.0,
        Models = input.Models ?? ["*"],
        Description = input.Description ?? "",
        Channels = input.Channels ?? [],
    });
    await s.SaveAsync();
    return Results.Ok(new { id = input.Id });
});

admin.MapPatch("/groups/{id}", async (HubStore s, AuthService auth, string id, GroupInput input) =>
{
    var g = s.Config.Groups.FirstOrDefault(x => x.Id == id);
    if (g is null) return Results.NotFound(new { error = "没这个分组" });

    if (input.Name is not null) g.Name = input.Name;
    if (input.Rate is not null) g.Rate = input.Rate.Value;
    if (input.Models is not null) g.Models = input.Models;
    if (input.Description is not null) g.Description = input.Description;
    if (input.Channels is not null) g.Channels = input.Channels;

    await s.SaveAsync();
    auth.Invalidate();
    return Results.Ok(new { ok = true });
});

// 把某个成员的今日用量清零（有人搞砸了要重来）
admin.MapPost("/members/{id}/reset-usage", async (HubStore s, string id) =>
{
    await using var conn = s.OpenDb();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM daily_usage WHERE member_id=$m AND day=$d";
    cmd.Parameters.AddWithValue("$m", id);
    cmd.Parameters.AddWithValue("$d", DateTime.Now.ToString("yyyy-MM-dd"));
    await cmd.ExecuteNonQueryAsync();
    return Results.Ok(new { ok = true });
});

app.Run();
return;

// ─────────────────────────────────────────────────────────────
//  工具
// ─────────────────────────────────────────────────────────────

static string? BearerOf(string? header)
{
    if (string.IsNullOrWhiteSpace(header)) return null;
    var m = System.Text.RegularExpressions.Regex.Match(header.Trim(), @"^Bearer\s+(.+)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return m.Success ? m.Groups[1].Value : null;
}

/// <summary>
/// 列出这个分组能用的模型。
///
/// 这里不直接转发上游花名册，因为：
///   1. 不同分组的可见范围不同（免费组只该看到那两个）
///   2. 分组可能是渠道子集，得取交集
/// </summary>
static async Task<List<object>> ListModelsAsync(HubStore s, Group group, CancellationToken ct)
{
    var seen = new SortedDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    foreach (var channel in s.Config.Channels.Where(c => c.Enabled))
    {
        if (group.Channels.Count > 0 && !group.Channels.Contains(channel.Id)) continue;

        // 渠道自己声明了清单就用它，否则去问上游
        List<string> ids;
        if (channel.Models.Count > 0)
        {
            ids = channel.Models;
        }
        else
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var req = new HttpRequestMessage(HttpMethod.Get, channel.BaseUrl.TrimEnd('/') + "/v1/models");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + channel.ApiKey);
                req.Headers.Host = new Uri(channel.BaseUrl).Authority;
                var resp = await http.SendAsync(req, ct);
                var payload = await resp.Content.ReadAsStringAsync(ct);

                // 注意：这里必须立刻 ToList()。
                // 写成惰性 LINQ 的话，枚举发生在 doc 被 dispose 之后，
                // 会抛 ObjectDisposedException —— 踩过一次。
                using var doc = JsonDocument.Parse(payload);
                ids = doc.RootElement.GetProperty("data").EnumerateArray()
                    .Select(x => x.GetProperty("id").GetString() ?? "")
                    .Where(x => x != "")
                    .ToList();
            }
            catch
            {
                continue;   // 渠道不通就跳过，别让整个 /v1/models 挂掉
            }
        }

        foreach (var id in ids)
        {
            if (!ChannelRouter.GroupAllows(group, id)) continue;
            if (id == "*") continue;
            if (seen.ContainsKey(id)) continue;

            var (inR, outR, note) = QuotaCalculator.RateOf(id);
            seen[id] = new
            {
                id,
                @object = "model",
                created = 0,
                owned_by = "buddydeck",
                free = inR == 0 && outR == 0,
                rate = new { input = inR, output = outR, note },
            };
        }
    }

    return seen.Values.ToList();
}

// ── 请求 DTO ──

record MemberInput(string? Name, string? Note, string? Group, long? DailyQuota,
                   bool? Enabled, int? RateLimitPerMinute);

record TokenInput(string? Name, List<string>? Models, DateTimeOffset? ExpiresAt);

record ChannelInput(string? Id, string? Name, string? BaseUrl, string? ApiKey,
                    string? DataHome, string? NodePath, string? ScriptDir,
                    int? Priority, bool? Enabled, List<string>? Models, List<string>? Groups);

record GroupInput(string? Id, string? Name, double? Rate, List<string>? Models,
                  string? Description, List<string>? Channels);
