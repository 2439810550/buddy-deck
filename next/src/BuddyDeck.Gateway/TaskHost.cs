using BuddyDeck.Core;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 后台任务执行器 —— 把费时操作（批量拉积分 / 一键签到 / 批量续期）
/// 放进任务列表后台跑，实时写进度到数据库，**不阻塞 HTTP 响应与 UI**。
///
/// ## 为什么需要（用户需求）
///
/// 批量拉 20 个账号的积分要几十秒。如果让 `GET /admin/credits/all`
/// 同步等，界面 3 秒超时直接失败（实测「网关响应超时」）。正确做法：
///   ① 界面发一个「开始任务」请求 → 立刻拿到任务 id
///   ② 网关后台逐账号拉积分，每拉一个就更新进度（写 tasks 表）
///   ③ 界面轮询 `/admin/tasks`，任务库显示进度条 + 实时描述
///
/// ## 进度语义
///
/// 每个任务的 progress 是 0-100，description 是一句实时描述
/// （如「正在拉取积分：3/20 主号」）。完成后 status=done + result JSON，
/// 失败 status=failed + error。
/// </summary>
public sealed partial class TaskHost
{
    private readonly Database _db;
    private readonly ILogger<TaskHost> _log;

    public TaskHost(Database db, ILogger<TaskHost> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// 启动一个后台任务并立刻返回任务 id（不等待完成）。
    ///
    /// @param kind   任务类型（refresh-credits / checkin-all / renew-all）
    /// @param title  界面显示的任务标题
    /// @param work   任务体。参数是 (taskId, report) —— report(status, progress, description)
    ///               由任务体按进度调用，本方法把它转成数据库写入。
    /// @param onDone 完成时的结果序列化（可选）。返回 JSON 字符串存进 result。
    /// </summary>
    public string Start(
        string kind,
        string title,
        Func<string, Action<string, int, string>, CancellationToken, Task> work,
        Func<string>? onDone = null)
    {
        var id = _db.CreateTask(kind, title, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        _ = Task.Run(async () =>
        {
            var seq = 0;
            try
            {
                // report 同时做两件事：
                //   ① 更新任务进度（tasks 表，列表用）
                //   ② 追一条任务流事件（task_events 表，详情用）
                Action<string, int, string> report = (status, progress, description) =>
                {
                    _db.UpdateTask(id, status, progress, description);
                    _db.AppendTaskEvent(id, Interlocked.Increment(ref seq), "info", description, progress, null);
                };

                await work(id, report, CancellationToken.None).ConfigureAwait(false);

                var result = onDone?.Invoke() ?? "{}";
                _db.AppendTaskEvent(id, Interlocked.Increment(ref seq), "ok", "任务完成", 100, null);
                _db.FinishTask(id, "done", result, null);
                PruneIfNeeded();
            }
            catch (OperationCanceledException)
            {
                _db.AppendTaskEvent(id, Interlocked.Increment(ref seq), "warn", "任务被取消", null, null);
                _db.FinishTask(id, "cancelled", "{}", null);
                PruneIfNeeded();
            }
            catch (Exception ex)
            {
                // ★ 把内层异常也带出来 —— EF 的 SaveChanges 失败时
                //   外层只有「An error occurred while saving...」一句话，
                //   真正的约束冲突/类型错误在内层，不打印等于盲查。
                var detail = ex.InnerException is { } inner ? $"{ex.Message} → {inner.Message}" : ex.Message;
                LogTaskFailed(kind, detail);
                _db.AppendTaskEvent(id, Interlocked.Increment(ref seq), "error", detail, null, null);
                _db.FinishTask(id, "failed", "{}", detail);
                PruneIfNeeded();
            }
        });

        return id;
    }

    /// <summary>
    /// 任务结束时清理旧记录：只保留最近 50 条任务 + 它们的事件流，
    /// 更老的连事件一起删（防止 tasks/task_events 无限增长拖慢列表）。
    /// </summary>
    private void PruneIfNeeded() => _db.PruneTasks(KeepTasks);

    /// <summary>数据库里最多保留多少条任务（超出删最老的）。</summary>
    private const int KeepTasks = 200;

    [LoggerMessage(Level = LogLevel.Warning, Message = "后台任务失败（{Kind}）：{Message}")]
    private partial void LogTaskFailed(string kind, string message);
}
