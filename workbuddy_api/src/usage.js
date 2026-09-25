/**
 * 用量账本 —— 把每次调用的真实成本记下来。
 *
 * ## 为什么需要
 *
 * 上游的 usage 里带一个 `credit` 字段，是**这次请求实际扣了多少积分**。
 * 它已经把输入价、输出价、缓存命中折扣全算好了 —— 实测：
 *
 *     第一次（全 miss，2541 prompt）            credit 0.22
 *     第二次（命中 2304，miss 237）             credit 0.03
 *                                                 ↑ 命中 91%，只花 14%
 *
 * 但这个字段之前被丢掉了：`sanitizeChunk` 只保留 prompt/completion/total，
 * 因为它不是标准 OpenAI 字段，客户端会嫌它多余。
 *
 * 丢掉它就意味着：**你永远不知道每笔生意赚了还是亏了。**
 *
 * ## 记在哪
 *
 * JSONL（一行一个 JSON 对象），放在 `~/.workbuddy-api/usage.jsonl`。
 *
 * 为什么不用 sqlite：这个模块要保持零依赖（整个网关只用 node 内置模块）。
 * 而且这个文件是**追加写的账本**，一行一条，用 `jq`/`awk`/Python 都能直接分析，
 * 不需要额外的工具。
 *
 * ## 为什么不用 stderr 日志
 *
 * 日志是给人看的、会滚动、格式不稳定。这个是要做聚合计算的，
 * 必须是机器可解析的定长格式。
 *
 * @module workbuddy-api/usage
 */

import { appendFile, mkdir } from "node:fs/promises";
import { join } from "node:path";
import { stateHome } from "./paths.js";

/** 账本文件名。 */
const LEDGER = "usage.jsonl";

/** 写到这个大小就轮转一次（10MB，约几万条）。 */
const MAX_BYTES = 10 * 1024 * 1024;

let _dirReady = false;
let _written = 0;

async function ensureHome() {
  if (_dirReady) return;
  const dir = stateHome();
  await mkdir(dir, { recursive: true, mode: 0o700 });
  _dirReady = true;
}

/** 账本文件的绝对路径。 */
export function ledgerPath() {
  return join(stateHome(), LEDGER);
}

/**
 * 从一份 usage 对象里抽出要记的字段。
 *
 * `credit` 是核心 —— 上游给的实际扣费。
 * 缓存字段单独记，因为它是「为什么这个模型成本低」的解释变量。
 *
 * @param {object} usage 上游返回的 usage
 * @returns {object} 精简后的用量摘要
 */
export function summarizeUsage(usage) {
  if (typeof usage !== "object" || usage === null) return {};

  const num = (v) => (typeof v === "number" && Number.isFinite(v) ? v : undefined);

  return {
    promptTokens: num(usage["prompt_tokens"]),
    completionTokens: num(usage["completion_tokens"]),
    totalTokens: num(usage["total_tokens"]),
    // 上游给的真实成本。有它就不用再推算。
    credit: num(usage["credit"]),
    // 缓存命中。命中率高 → 实际成本远低于标称价。
    cacheHit: num(usage["prompt_cache_hit_tokens"]),
    cacheMiss: num(usage["prompt_cache_miss_tokens"]),
    cacheRead: num(usage["cache_read_input_tokens"]),
    cacheWrite: num(usage["prompt_cache_write_tokens"]),
    // 思考型模型的内部推理 token（不计入 completion 的话要单独看）
    reasoning: num(usage["completion_thinking_tokens"]) ?? num(usage["completion_tokens_details"]?.["reasoning_tokens"]),
  };
}

/**
 * 记一笔。
 *
 * 设计原则：**绝不能影响正常请求**。
 * 写盘失败就静默跳过 —— 记账重要，但比业务可用性次要。
 *
 * @param {object} record
 * @param {string} record.model      请求的模型名（客户端要的那个）
 * @param {string} [record.usedModel] 实际用的模型（被限流换过的话和 model 不同）
 * @param {string} [record.account]  用的哪个账号（保险库里的 id 前缀，脱敏）
 * @param {object} record.usage      上游返回的 usage
 * @param {string} [record.kind]     请求类型：chat / messages / responses
 * @param {number} [record.elapsedMs] 这次请求花了多久
 * @param {number} [record.attempts] 重试了几次（含首次）
 * @param {string} [record.outcome]  ok / failed
 */
export async function recordUsage(record) {
  try {
    await ensureHome();

    const entry = {
      ts: Date.now(),
      iso: new Date().toISOString(),
      model: record.model,
      ...(record.usedModel && record.usedModel !== record.model ? { usedModel: record.usedModel } : {}),
      ...(record.account ? { account: record.account } : {}),
      kind: record.kind ?? "chat",
      outcome: record.outcome ?? "ok",
      ...(record.elapsedMs === undefined ? {} : { elapsedMs: record.elapsedMs }),
      ...(record.attempts === undefined ? {} : { attempts: record.attempts }),
      ...summarizeUsage(record.usage),
    };

    await appendFile(ledgerPath(), JSON.stringify(entry) + "\n", { encoding: "utf8", mode: 0o600 });

    // 轮转：超过阈值就把当前文件改名归档，下次写新的
    _written += 1;
    if (_written % 500 === 0) await maybeRotate();
  } catch (error) {
    // 记账失败不能影响请求。但设了 DEBUG 就能看到原因。
    if (process.env["WORKBUDDY_DEBUG"] === "1") {
      process.stderr.write(`[usage] 记账失败：${String(error)}
`);
    }
  }
}

async function maybeRotate() {
  try {
    const { stat, rename } = await import("node:fs/promises");
    const path = ledgerPath();
    const info = await stat(path);
    if (info.size < MAX_BYTES) return;
    const stamp = new Date().toISOString().slice(0, 10);
    await rename(path, join(stateHome(), `usage-${stamp}-${Date.now()}.jsonl`));
  } catch {
    // 轮转失败也无所谓，接着往原文件写
  }
}

/**
 * 聚合账本，算成本和定价参考。
 *
 * 这是给「一周后回头看账」用的 —— 不用它做实时计费，
 * 那次是用 new-api 自己的 quota。
 *
 * @param {Array<object>} entries 解析出来的账本条目
 * @returns {{byModel: object[], totals: object, byAccount: object[]}}
 */
export function aggregate(entries) {
  const models = new Map();
  const accounts = new Map();
  const totals = {
    calls: 0,
    credit: 0,
    promptTokens: 0,
    completionTokens: 0,
    cacheHit: 0,
    cacheMiss: 0,
    failures: 0,
  };

  for (const e of entries) {
    totals.calls += 1;
    if (e.outcome !== "ok") totals.failures += 1;
    totals.credit += e.credit ?? 0;
    totals.promptTokens += e.promptTokens ?? 0;
    totals.completionTokens += e.completionTokens ?? 0;
    totals.cacheHit += e.cacheHit ?? 0;
    totals.cacheMiss += e.cacheMiss ?? 0;

    const m = models.get(e.model) ?? {
      model: e.model, calls: 0, credit: 0,
      promptTokens: 0, completionTokens: 0, cacheHit: 0, cacheMiss: 0,
    };
    m.calls += 1;
    m.credit += e.credit ?? 0;
    m.promptTokens += e.promptTokens ?? 0;
    m.completionTokens += e.completionTokens ?? 0;
    m.cacheHit += e.cacheHit ?? 0;
    m.cacheMiss += e.cacheMiss ?? 0;
    models.set(e.model, m);

    if (e.account) {
      const a = accounts.get(e.account) ?? { account: e.account, calls: 0, credit: 0 };
      a.calls += 1;
      a.credit += e.credit ?? 0;
      accounts.set(e.account, a);
    }
  }

  /** 算派生指标：每千 token 花多少积分、缓存命中率。 */
  const enrich = (row) => {
    const tokens = row.promptTokens + row.completionTokens;
    const cacheTotal = row.cacheHit + row.cacheMiss;
    return {
      ...row,
      tokens,
      credit: Number(row.credit.toFixed(4)),
      creditPerKToken: tokens > 0 ? Number((row.credit / tokens * 1000).toFixed(6)) : null,
      tokensPerCredit: row.credit > 0 ? Math.round(tokens / row.credit) : null,
      cacheHitRate: cacheTotal > 0 ? Number((row.cacheHit / cacheTotal).toFixed(3)) : null,
    };
  };

  return {
    totals: enrich(totals),
    byModel: [...models.values()].map(enrich).sort((a, b) => b.credit - a.credit),
    byAccount: [...accounts.values()].map(enrich).sort((a, b) => b.credit - a.credit),
  };
}

/**
 * 从 JSONL 文本解析出账本条目。坏行直接跳过（写的时候可能正好断电）。
 */
export function parseLedger(text) {
  const out = [];
  for (const line of text.split("\n")) {
    const trimmed = line.trim();
    if (trimmed === "") continue;
    try {
      out.push(JSON.parse(trimmed));
    } catch {
      // 半截的行（正在写）或格式坏掉的行，跳过
    }
  }
  return out;
}
