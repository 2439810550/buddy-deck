using BuddyDeck.Core;
using Microsoft.Extensions.Logging;

namespace BuddyDeck.Gateway;

/// <summary>
/// 用量账本的**记账钩子** —— 把每次聊天请求的终局结果写进账本。
///
/// ## 为什么挂在 <see cref="IChatObserver"/> 上
///
/// 因为那是**唯一一个「终局点」统一出口**。`GatewayApp` 在三条终局路径
/// （成功 / 失败 / 取消）各调一次 `Notify(...)`，参数里已经带齐了记账需要的
/// 全部字段：请求模型、实际模型、账号标签、usage、outcome、尝试次数、耗时。
///
/// 挂在别处都有坑：
///   · 挂在 `StreamAsync` 里 → 只能覆盖流式成功路径，失败/取消全漏
///   · 挂在自己的 try/catch → 要和主流程那几处 `return` 对齐，漏一个分支就漏记
///   · 挂在端点里 → 那是**读**账本的地方，不是写
///
/// ## 为什么这是 t29 的关键一半
///
/// 光把 `/usage/*` 端点注册上是**不够的** —— 端点只是读账本。
/// 账本要**在真实请求路径上被写入**才有内容。
/// 实测：t29 之前「跑了几十次 chat 请求，`usage.jsonl` 一个字节都没落盘」。
///
/// ## 硬约束：记账失败绝不能让用户请求失败
///
/// 与 <see cref="RequestLog"/> 同款旁路语义：
///   · `ChatObservers.Notify` 本身就会吞掉观察者的异常（并记警告）
///   · 这里**再兜一层** —— 写盘是 IO，可能失败的是「磁盘满 / 权限 / 路径不存在」
///     这类运维问题，不该变成用户看到的 500
///
/// 写不进去就记一条警告，**不抛**。
/// </summary>
public sealed partial class UsageLedgerObserver : IChatObserver
{
    private readonly UsageLedger _ledger;
    private readonly Database? _database;
    private readonly ILogger<UsageLedgerObserver> _log;

    public UsageLedgerObserver(UsageLedger ledger, ILogger<UsageLedgerObserver> log, Database? database = null)
    {
        _ledger = ledger;
        _database = database;
        _log = log;
    }

    public void OnChatCompleted(ChatObservation observation)
    {
        try
        {
            // ⚠ 只在**终局**记 —— 中间失败的重试尝试不该各记一条。
            //
            //   为什么：账本回答的是「我这个月烧了多少额度」（见 UsageLedger 的
            //   类型注释里与 RequestLog 的分工）。一次客户端请求 = 一次计费事件，
            //   内层换模型重试 4 次不该记 4 条 —— 那会把账目翻 4 倍。
            //
            //   `ChatObservation` 本来就是**终局事件**（`Notify` 只在三条终局路径调），
            //   所以这里不需要额外判断。这行注释是写给「将来想在中间尝试也记账」的人：
            //   那属于**诊断**（去看看 RequestLog），不是账本。
            if (string.IsNullOrEmpty(observation.UsedModel) && string.IsNullOrEmpty(observation.RequestedModel))
            {
                // 连模型都不知道 —— 不是一次有效的计费事件（比如请求还没解析到模型就失败了）
                return;
            }

            var model = string.IsNullOrEmpty(observation.RequestedModel)
                ? observation.UsedModel
                : observation.RequestedModel;

            var record = _ledger.Append(
                model: model,
                account: observation.AccountTag,
                // usage 可能是 null（失败/取消时上游没给）—— `UsageSummary.From` 会当 0 处理
                usage: observation.Usage,
                at: DateTimeOffset.UtcNow,
                // 换过模型时 `UsedModel != model`，Append 会把它记进 UsedModel 字段
                usedModel: observation.UsedModel,
                kind: "chat",
                outcome: observation.Outcome,
                elapsedMs: observation.ElapsedMs,
                attempts: observation.Attempts);

            // ★ 同步写数据库（查询加速层）。
            _database?.AppendUsage(record);
        }
        catch (Exception ex)
        {
            // ⚠ 绝不能往上抛 —— 那会让一次成功的聊天变成 500。
            //   账本写不进去是运维问题（磁盘满/权限/路径），不是用户的问题。
            if (_log.IsEnabled(LogLevel.Warning))
            {
                LogLedgerWriteFailed(ex, observation.UsedModel);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "用量账本写入失败（模型 {Model}）—— 已忽略，不影响用户响应")]
    private partial void LogLedgerWriteFailed(Exception ex, string model);
}
