/**
 * 限流（429 / soft_rate）的自动规避。
 *
 * ## 上游到底怎么限的
 *
 * 实测抓到的原文：
 *
 * ```
 * HTTP 429  {"code":6004,
 *   "msg":"您的使用量已超出频率限制，将在 2026-09-25 17:07:48 UTC+8 重置，
 *          您也可以切换其他模型继续使用。"}
 * ```
 *
 * 这一句把三件事都说了：
 *
 *   1. 限制**按「账号 + 模型」**算 —— 上游自己建议“切换其他模型继续使用”
 *   2. 有**明确的恢复时间**，可以解出来告诉用户/自己等
 *   3. 这是个**周期性配额**，不是封号
 *
 * 另外 `code 6004` 和 `code 10081`（RESPONSE_CODE_IP_LIMIT，客户端里那个）不是一回事：
 * 前者是模型配额，后者是 IP 层面的拦截。
 *
 * ## 所以要做的两件事
 *
 * - **解析重置时间**：把 `2026-09-25 17:07:48 UTC+8` 变成真正的时刻，
 *   这样能告诉用户“什么时候恢复”，也能在窗口内直接跳过这个模型不再白试。
 * - **自动换模型**：被拦了就换一个没用过的模型重试。因为限制是按模型算的，
 *   换个模型就是真能继续跑，而不是“碰运气”。
 *
 * ## 为什么不重试同一个模型
 *
 * 上游说的是“使用量已超出”，同一个模型重试只是白等 —— 得等到重置时间。
 * 所以对已被限流的模型记为“冷却中”，在窗口内直接跳过。
 *
 * @module workbuddy-api/ratelimit
 */

/** 上游的限流专用码。 */
export const RATE_LIMIT_CODES = new Set([6004, 6005, 6006]);

/**
 * 从上游的错误文案里把重置时刻挖出来。
 *
 * 文案形如：
 *   您的使用量已超出频率限制，将在 2026-09-25 17:07:48 UTC+8 重置，…
 *   您的使用量已超出频率限制，将在 2026-09-25 09:07:48 UTC 重置，…
 *
 * @param {string} text 上游返回的原文（可能是整个 JSON）
 * @returns {number|undefined} 重置时刻的毫秒时间戳
 */
export function parseRateLimitReset(text) {
  if (typeof text !== "string" || text === "") return undefined;

  // 2026-09-25 17:07:48 UTC+8   /   2026-09-25T17:07:48+08:00
  const match =
    /(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2}):(\d{2})\s*UTC([+-]\d{1,2})?/iu.exec(text) ??
    /(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2}):(\d{2})\s*([+-]\d{2}):?(\d{2})/iu.exec(text);

  if (match === null) return undefined;

  const [, y, mo, d, h, mi, s] = match;
  // 时区偏移：`UTC+8` 抓到的是 "+8"；带冒号的抓到 "+08" 和 "00"
  const offsetHours = match[7] === undefined ? 0 : Number(match[7]);
  const offsetMinutes = match[8] === undefined ? 0 : Number(match[8]) * Math.sign(offsetHours || 1);

  const utcMs = Date.UTC(Number(y), Number(mo) - 1, Number(d), Number(h), Number(mi), Number(s));
  return utcMs - (offsetHours * 60 + offsetMinutes) * 60_000;
}

/** 这是不是一条限流错误。 */
export function isRateLimited({ status, kind, message } = {}) {
  if (kind === "soft_rate") return true;
  if (status === 429) return true;
  if (typeof message !== "string") return false;

  // 服务端也会把它包在 400 里发过来，所以不能只看状态码
  if (/"?code"?\s*:\s*(600[4-6])\b/u.test(message)) return true;
  return /使用量已超出频率限制|超出频率限制/u.test(message);
}

/**
 * 从一条上游错误里抽出对人类有用的信息。
 *
 * @returns {{limited: boolean, resetAt?: number, hint?: string}}
 */
export function describeRateLimit({ status, kind, message } = {}) {
  if (!isRateLimited({ status, kind, message })) return { limited: false };

  const resetAt = parseRateLimitReset(String(message ?? ""));
  return {
    limited: true,
    ...(resetAt === undefined ? {} : { resetAt }),
    hint:
      resetAt === undefined
        ? "该模型已被限流，且上游没给出恢复时间。换一个模型可以继续用。"
        : `该模型要到 ${new Date(resetAt).toLocaleString()} 才能再用。换一个模型可以继续用。`,
  };
}

/**
 * 挑一个「没在当前冷却列表里」的备用模型。
 *
 * 挑的顺序有讲究：**优先挑免费的（x0.00）**。
 * 被限流时人已经不爽了，这时候再烧贵的模型只会更不爽。
 *
 * @param {object} options
 * @param {Array<{id: string, credits?: string}>} options.models 可选模型
 * @param {string} options.exclude 首选模型（刚被限流的那个）
 * @param {Set<string>} [options.cooling] 还在冷却中的模型 id
 * @returns {string|undefined}
 */
export function pickFallbackModel({ models, exclude, cooling }) {
  const skip = cooling ?? new Set();
  const usable = models.filter((model) => model.id !== exclude && !skip.has(model.id));
  if (usable.length === 0) return undefined;

  const isFree = (model) => {
    const credits = typeof model.credits === "string" ? model.credits.toLowerCase() : "";
    return credits.replace(/\s*credits\s*$/u, "").trim() === "x0.00";
  };

  return (usable.find(isFree) ?? usable[0]).id;
}

/**
 * 一张「哪个模型被限流到什么时候」的表。
 *
 * 网关进程内共用一份。被限流的模型在窗口内直接跳过 ——
 * 反正重试也是白搭，不如把请求直接打到别的模型上，用户完全无感。
 */
export class RateLimitTracker {
  constructor() {
    /** @type {Map<string, {until: number, at: number, text: string}>} */
    this.entries = new Map();
  }

  /** 记一笔限流。没解析出恢复时间就按 5 分钟算（保守，别一直试）。 */
  mark(account, model, { resetAt, text } = {}) {
    const key = `${account}:${model}`;
    const until = typeof resetAt === "number" ? resetAt : Date.now() + 5 * 60_000;
    this.entries.set(key, { until, at: Date.now(), text: String(text ?? "").slice(0, 200) });
    return until;
  }

  /** 这个账号 + 模型现在是否还在冷却中。 */
  isCooling(account, model, now = Date.now()) {
    const entry = this.entries.get(`${account}:${model}`);
    if (entry === undefined) return false;
    if (entry.until <= now) {
      this.entries.delete(`${account}:${model}`);
      return false;
    }
    return true;
  }

  /** 某个账号下所有还在冷却的模型 id。 */
  coolingModels(account, now = Date.now()) {
    const out = new Set();
    for (const [key, entry] of this.entries) {
      const [owner, model] = key.split(":");
      if (owner !== account) continue;
      if (entry.until > now) out.add(model);
      else this.entries.delete(key);
    }
    return out;
  }

  /** 冷却中、但已经过了恢复时间的，清掉。 */
  prune(now = Date.now()) {
    for (const [key, entry] of this.entries) {
      if (entry.until <= now) this.entries.delete(key);
    }
  }
}
