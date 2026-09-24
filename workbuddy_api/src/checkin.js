/**
 * WorkBuddy 每日签到。
 *
 * 接口是从桌面客户端产物里扒出来的（`resources/app.asar` 里的
 * `WorkBuddyAgentAdapter`），不是官方文档：
 *
 *   POST {billingBase}/v2/billing/meter/checkin-activity-status   查签到状态
 *   POST {billingBase}/v2/billing/meter/daily-checkin             执行签到
 *
 * 两个都是空 body `{}`，用的是和计费接口同一套头（`billingHeaders`）。
 * 客户端还会带一个 `X-Device-Token`（腾讯 Turing 风控指纹），实测**不带也能通**，
 * 所以这里不发——那个 token 要靠客户端自己的原生模块拿，我们拿不到也不该伪造。
 *
 * 返回信封统一是 `{code, msg, requestId, data}`，`code === 0` 才算成功。
 * 字段是 snake_case，这里统一转成 camelCase 给界面用。
 *
 * @module workbuddy-api/checkin
 */

import { billingHeaders } from "./upstream.js";

const TIMEOUT_MS = 20_000;

function isObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function num(value) {
  return typeof value === "number" && Number.isFinite(value) ? value : 0;
}

function str(value) {
  return typeof value === "string" ? value : "";
}

/** 上游的 snake_case 状态 → 界面用的 camelCase。 */
export function normalizeCheckinStatus(data) {
  if (!isObject(data)) return null;
  return {
    active: data["active"] === true,
    todayCheckedIn: data["today_checked_in"] === true,
    streakDays: num(data["streak_days"]),
    dailyCredit: num(data["daily_credit"]),
    todayCredit: num(data["today_credit"]),
    totalCredits: num(data["total_credits"]),
    weekCheckinDays: num(data["week_checkin_days"]),
    weekProgress: Array.isArray(data["week_progress"]) ? data["week_progress"].map((v) => v === true) : [],
    checkinDates: Array.isArray(data["checkin_dates"]) ? data["checkin_dates"].map(str) : [],
    activityName: str(data["activity_name"]),
    themeName: str(data["theme_name"]),
    season: num(data["season"]),
    startTime: str(data["start_time"]),
    endTime: str(data["end_time"]),
    claimButtonText: str(data["claim_button_text"]),
    isStreakDay: data["is_streak_day"] === true,
    streakBonusDays: num(data["streak_bonus_days"]),
    streakBonusCredit: num(data["streak_bonus_credit"]),
  };
}

/** POST 一个签到相关的端点，统一解信封。 */
async function post(client, variant, credential, path) {
  const fetchImpl = client?.fetchImpl ?? fetch;
  const response = await fetchImpl(`${variant.billingBase}${path}`, {
    method: "POST",
    headers: billingHeaders(credential),
    body: "{}",
    signal: AbortSignal.timeout(TIMEOUT_MS),
  });

  const text = await response.text();
  let envelope;
  try {
    envelope = JSON.parse(text);
  } catch {
    throw new Error(`签到接口返回的不是 JSON（http ${response.status}）：${text.slice(0, 200)}`);
  }

  const code = num(envelope?.["code"]);
  const msg = str(envelope?.["msg"]);
  if (!response.ok && code === 0) {
    throw new Error(`签到接口 http ${response.status}：${text.slice(0, 200)}`);
  }

  return {
    ok: response.ok && code === 0,
    status: response.status,
    code,
    msg: msg === "" ? `code ${code}` : msg,
    requestId: str(envelope?.["requestId"]),
    data: envelope?.["data"],
  };
}

/**
 * 查某个账号的签到状态。查不到就返回 null，不抛——状态查不出来不该拦住别的账号。
 *
 * @returns {Promise<object|null>} 归一化后的状态
 */
export async function fetchCheckinStatus(client, variant, credential) {
  try {
    const result = await post(client, variant, credential, "/v2/billing/meter/checkin-activity-status");
    if (!result.ok) return null;
    return normalizeCheckinStatus(result.data);
  } catch {
    return null;
  }
}

/**
 * 执行签到。已经签过的话上游也会回 code 0，看返回里的状态判断是不是真拿到了积分。
 *
 * @returns {Promise<{ok: boolean, code: number, msg: string, status: object|null}>}
 */
export async function claimDailyCheckin(client, variant, credential) {
  const result = await post(client, variant, credential, "/v2/billing/meter/daily-checkin");
  return {
    ok: result.ok,
    code: result.code,
    msg: result.msg,
    requestId: result.requestId,
    status: normalizeCheckinStatus(result.data),
  };
}

/**
 * 「查一下，没签就签」——批量签到和界面按钮都调这个。
 *
 * 已经签过的直接返回现状，不多打一次请求。
 */
export async function ensureCheckedIn(client, variant, credential) {
  const before = await fetchCheckinStatus(client, variant, credential);
  if (before === null) {
    return { ok: false, code: -1, msg: "查不到签到状态（网络或令牌问题）", status: null, claimed: false };
  }
  if (!before.active) {
    return { ok: true, code: 0, msg: "当前没有进行中的签到活动", status: before, claimed: false };
  }
  if (before.todayCheckedIn) {
    return { ok: true, code: 0, msg: `今天已经签过了（连续 ${before.streakDays} 天）`, status: before, claimed: false };
  }

  const claim = await claimDailyCheckin(client, variant, credential);
  // 上游签成功未必把最新状态一起回，回查一次拿到准确天数
  const after = claim.ok ? await fetchCheckinStatus(client, variant, credential) : null;
  return {
    ...claim,
    status: after ?? claim.status ?? before,
    claimed: claim.ok,
  };
}
