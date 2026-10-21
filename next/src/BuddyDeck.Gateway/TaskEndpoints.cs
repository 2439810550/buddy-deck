using System.Text;
using System.Text.Json.Nodes;
using BuddyDeck.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 任务库端点。
///
/// ```
/// GET  /admin/tasks                     读任务列表（最近 N 个，含进度/描述）
/// POST /admin/tasks/refresh-credits     触发「批量拉积分」后台任务
/// POST /admin/tasks/checkin-all         触发「一键签到」后台任务
/// POST /admin/tasks/renew-all           触发「批量续期」后台任务
/// ```
///
/// ## 为什么是后台任务而不是同步端点
///
/// 批量拉 20 个账号积分要几十秒。同步等的话客户端 3 秒超时（实测「网关响应超时」）。
/// 所以：POST 立刻返回任务 id，网关后台跑，界面轮询 GET /admin/tasks 看进度。
/// </summary>
public sealed partial class TaskEndpoints : IEndpointFamily
{
    private const string PathPrefix = "/admin/tasks";

    private readonly Database _db;
    private readonly TaskHost _host;
    private readonly ICredentialSource _credentials;
    private readonly UpstreamClient _upstream;
    private readonly Checkin? _checkin;
    private readonly ILogger<TaskEndpoints> _log;

    public TaskEndpoints(
        Database db,
        TaskHost host,
        ICredentialSource credentials,
        UpstreamClient upstream,
        ILogger<TaskEndpoints> log,
        Checkin? checkin = null)
    {
        _db = db;
        _host = host;
        _credentials = credentials;
        _upstream = upstream;
        _log = log;
        _checkin = checkin;
    }

    public async Task<bool> TryHandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (!path.StartsWith(PathPrefix, StringComparison.Ordinal)) return false;

        if (path.Length > 1 && path.EndsWith('/')) path = path.TrimEnd('/');

        // GET /admin/tasks —— 读任务列表
        if (HttpMethods.IsGet(context.Request.Method) && string.Equals(path, PathPrefix, StringComparison.Ordinal))
        {
            await ListAsync(context).ConfigureAwait(false);
            return true;
        }

        // GET /admin/tasks/{id} —— 任务详情（含事件流）
        if (HttpMethods.IsGet(context.Request.Method) && path.StartsWith(PathPrefix + "/", StringComparison.Ordinal))
        {
            var taskId = Uri.UnescapeDataString(path[(PathPrefix.Length + 1)..]);
            await DetailAsync(context, taskId).ConfigureAwait(false);
            return true;
        }

        // POST /admin/tasks/{kind} —— 触发任务
        if (HttpMethods.IsPost(context.Request.Method) && path.StartsWith(PathPrefix + "/", StringComparison.Ordinal))
        {
            var kind = path[(PathPrefix.Length + 1)..];
            await StartAsync(context, kind).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>任务详情：任务本身 + 它的事件流（“这个任务做了什么”）。</summary>
    private async Task DetailAsync(HttpContext context, string taskId)
    {
        var all = _db.ReadTasks(200);
        var task = all.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.Ordinal));
        if (task is null)
        {
            await WriteErrorAsync(context, 404, "not_found", $"没有这个任务：{taskId}").ConfigureAwait(false);
            return;
        }

        var events = new JsonArray();
        foreach (var e in _db.ReadTaskEvents(taskId, 2000))
        {
            events.Add(new JsonObject
            {
                ["seq"] = e.Seq,
                ["ts"] = e.Ts,
                ["level"] = e.Level,
                ["message"] = e.Message,
                ["progress"] = e.Progress is null ? null : JsonValue.Create(e.Progress.Value),
            });
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = true,
            ["task"] = new JsonObject
            {
                ["id"] = task.Id,
                ["kind"] = task.Kind,
                ["title"] = task.Title,
                ["status"] = task.Status,
                ["progress"] = task.Progress,
                ["description"] = task.Description,
                ["error"] = task.Error,
                ["createdAtMs"] = task.CreatedAtMs,
                ["finishedAtMs"] = task.FinishedAtMs,
            },
            ["events"] = events,
        }).ConfigureAwait(false);
    }

    /// <summary>读任务列表（最近 50 个，倒序）。</summary>
    private async Task ListAsync(HttpContext context)
    {
        var limit = 50;
        if (context.Request.Query.TryGetValue("limit", out var raw) &&
            int.TryParse(raw.ToString(), out var parsed) && parsed > 0)
        {
            limit = Math.Min(parsed, 200);
        }

        var rows = _db.ReadTasks(limit);
        var items = new JsonArray();

        foreach (var row in rows)
        {
            items.Add(new JsonObject
            {
                ["id"] = row.Id,
                ["kind"] = row.Kind,
                ["title"] = row.Title,
                ["status"] = row.Status,
                ["progress"] = row.Progress,
                ["description"] = row.Description,
                ["result"] = row.Result is null ? null : JsonNode.Parse(row.Result),
                ["error"] = row.Error,
                ["createdAtMs"] = row.CreatedAtMs,
                ["finishedAtMs"] = row.FinishedAtMs,
            });
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = true,
            ["tasks"] = items,
        }).ConfigureAwait(false);
    }

    /// <summary>按 kind 触发一个后台任务。</summary>
    private async Task StartAsync(HttpContext context, string kind)
    {
        var entries = _credentials.Load();

        switch (kind)
        {
            case "refresh-credits":
            {
                if (entries.Count == 0)
                {
                    await WriteErrorAsync(context, 401, "not_signed_in", "保险库里没有账号。").ConfigureAwait(false);
                    return;
                }

                var total = entries.Count;
                var taskId = _host.Start(
                    "refresh-credits",
                    $"刷新全部积分（{total} 个账号）",
                    async (id, report, ct) =>
                    {
                        var done = 0;
                        var success = 0;
                        foreach (var entry in entries)
                        {
                            done++;
                            report("running",
                                (int)(done * 100.0 / total),
                                $"正在拉取积分：{done}/{total} {ShortLabel(entry)}");

                            try
                            {
                                // ★ 单个账号的积分拉取加**硬超时 30 秒** ——
                                //   上游积分接口偶尔会挂起（实测「正在拉取积分：1/20」
                                //   卡死十几分钟），没有超时任务就永远卡在 running，
                                //   数据库里留下一堆进行中的僵尸任务。
                                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                timeout.CancelAfter(TimeSpan.FromSeconds(30));

                                var snapshot = await _upstream
                                    .FetchCreditsAsync(
                                        entry.ToCredential(),
                                        CatalogRefresh.UserAgentFor(UpstreamVariant.China),
                                        DateTimeOffset.UtcNow,
                                        timeout.Token)
                                    .ConfigureAwait(false);

                                // ★ 拉到的积分**必须写进数据库** ——
                                //   界面从 cached 端点读的就是这里写进去的快照。
                                //   余额用 TotalRemaining（remain 累加），不是 TotalDosage。
                                _db.UpsertCredits(
                                    entry.Id,
                                    CreditsEndpoints.AccountTagOf(entry),
                                    entry.Label,
                                    snapshot.TotalDosage,
                                    snapshot.TotalRemaining,
                                    snapshot.TotalCount,
                                    CreditsEndpoints.SerializePackages(snapshot),
                                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                                success++;
                            }
                            catch
                            {
                                // 单个号失败不挡别的号，继续
                            }
                        }

                        report("done", 100, $"积分刷新完成：成功 {success}/{total}");
                    },
                    () => "{\"ok\":true}");

                await WriteJsonAsync(context, 202, new JsonObject
                {
                    ["ok"] = true,
                    ["taskId"] = taskId,
                }).ConfigureAwait(false);
                return;
            }

            case "checkin-all":
            {
                if (_checkin is null)
                {
                    await WriteErrorAsync(context, 501, "not_implemented", "这个实例没有接签到客户端。").ConfigureAwait(false);
                    return;
                }
                if (entries.Count == 0)
                {
                    await WriteErrorAsync(context, 401, "not_signed_in", "保险库里没有账号。").ConfigureAwait(false);
                    return;
                }

                var total = entries.Count;
                var taskId = _host.Start(
                    "checkin-all",
                    $"一键签到（{total} 个账号）",
                    async (id, report, ct) =>
                    {
                        var done = 0;
                        var claimed = 0;
                        foreach (var entry in entries)
                        {
                            done++;
                            report("running",
                                (int)(done * 100.0 / total),
                                $"正在签到：{done}/{total} {ShortLabel(entry)}");

                            try
                            {
                                var result = await _checkin
                                    .EnsureCheckedInAsync(entry.ToCredential(), entry.LastCheckinAt, ct)
                                    .ConfigureAwait(false);
                                if (result.Outcome == CheckinOutcome.Claimed) claimed++;
                            }
                            catch
                            {
                                // 单个失败不挡
                            }
                        }

                        report("done", 100, $"签到完成：新签 {claimed}/{total}");
                    });

                await WriteJsonAsync(context, 202, new JsonObject
                {
                    ["ok"] = true,
                    ["taskId"] = taskId,
                }).ConfigureAwait(false);
                return;
            }

            case "renew-all":
            {
                if (entries.Count == 0)
                {
                    await WriteErrorAsync(context, 401, "not_signed_in", "保险库里没有账号。").ConfigureAwait(false);
                    return;
                }

                var total = entries.Count;
                var taskId = _host.Start(
                    "renew-all",
                    $"批量续期（{total} 个账号）",
                    async (id, report, ct) =>
                    {
                        var done = 0;
                        var renewed = 0;
                        foreach (var entry in entries)
                        {
                            done++;
                            report("running",
                                (int)(done * 100.0 / total),
                                $"正在续期：{done}/{total} {ShortLabel(entry)}");

                            try
                            {
                                await _upstream
                                    .RefreshTokenAsync(entry.ToCredential(), ct)
                                    .ConfigureAwait(false);
                                renewed++;
                            }
                            catch
                            {
                                // 单个失败不挡
                            }
                        }

                        report("done", 100, $"续期完成：成功 {renewed}/{total}");
                    });

                await WriteJsonAsync(context, 202, new JsonObject
                {
                    ["ok"] = true,
                    ["taskId"] = taskId,
                }).ConfigureAwait(false);
                return;
            }

            default:
                await WriteErrorAsync(context, 404, "not_found", $"没有这个任务类型：{kind}").ConfigureAwait(false);
                return;
        }
    }

    private static string ShortLabel(VaultEntry entry)
        => string.IsNullOrEmpty(entry.Label)
            ? entry.Uid[..Math.Min(8, entry.Uid.Length)]
            : entry.Label;

    // ── 输出 ──────────────────────────────────────────────────

    private static readonly System.Text.Json.JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task WriteJsonAsync(HttpContext context, int status, JsonNode body)
    {
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
}
