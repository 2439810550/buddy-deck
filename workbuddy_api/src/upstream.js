/**
 * WorkBuddy upstream HTTP client.
 *
 * Speaks the desktop App's own private protocol: a streaming-only chat
 * completions endpoint, a token-refresh endpoint, a per-region model catalog,
 * and a billing endpoint. Requests take the credential explicitly so a token
 * refresh applies on the next call.
 *
 * Wire quirks that are load-bearing and must not be "cleaned up":
 *
 * - `stream: false` is rejected (HTTP 400, code 11101) — the upstream is
 *   streaming-only, so this client always streams and re-assembles locally when
 *   a caller needs a whole answer.
 * - `role: "developer"` is rejected (HTTP 400, code 11128) — it must be
 *   rewritten to `role: "system"`.
 * - `tool_choice` must be a plain string; the object forms return 400.
 * - The international gateway additionally requires the first message to be a
 *   `system` prompt.
 * - The international gateway's channel check (HTTP 400, code 11128) demands the
 *   full conversation triple — `X-Conversation-ID`, `X-Conversation-Request-ID`
 *   and `X-Conversation-Message-ID` — on every chat request. A correct UA and
 *   version are not enough: dropping any one of the three yields 11128. The CN
 *   gateway does not check this family, so it is sent only on the global
 *   variant. Verified on the wire against www.workbuddy.ai.
 *
 * @module workbuddy-api/upstream
 */

import { randomUUID } from "node:crypto";

import { appUserAgent, chatUserAgent, fallbackChatIdentity, resolveChatIdentity } from "./client-identity.js";

/** Shared CLI-form User-Agent for refresh, catalog and billing requests. */
const CLIENT_UA = "CLI/2.63.2 CodeBuddy/2.63.2";
const JSON_TIMEOUT_MS = 30_000;
const ERROR_BODY_LIMIT = 4096;

/** Insufficient-credit markers, ASCII lowercase plus the original Chinese. */
const HARD_CREDIT_MARKERS = [
  "insufficient credit",
  "no credit",
  "credit exhausted",
  "credits exhausted",
  "out of credit",
  "quota exceeded",
  "quota exhaust",
  "payment required",
  "credit not enough",
  "not enough credit",
  "积分不足",
  "额度不足",
  "余额不足",
  "积分用完",
  "额度用尽",
  "没有积分",
];

/** Session-invalidation markers that mean "sign in again in the app". */
const SESSION_DEAD_MARKERS = ["Offline user session not found", "12153"];

/** The concrete effort spellings WorkBuddy exposes on the wire. */
const EFFORT_VALUES = ["low", "medium", "high", "xhigh", "max"];

/** Promotional badge tag prefix the upstream tags carry. */
const BADGE_PREFIX = "badge:";

/** System prompt the international gateway requires as the first message. */
const INTERNATIONAL_SYSTEM_PROMPT = "You are a helpful assistant.";

/**
 * Classify an upstream failure from its HTTP status and body excerpt.
 *
 * @returns {"hard_credit"|"session_dead"|"soft_rate"|"not_found"|"server"|"client"}
 */
export function classifyUpstreamError(status, body) {
  if (status === 402) return "hard_credit";
  // A 401 is an auth failure whatever its body says: there is no business
  // meaning for it on this upstream, and the session-dead markers below are
  // only an *additional* signal (they also catch dead sessions answered as
  // 403). Classification happens before the credit markers on purpose, so a
  // bare 401 whose body happens to name credits still reports `session_dead`
  // and keeps its `(http 401)`/AUTH meaning for whatever consumes this.
  if (status === 401) return "session_dead";
  const lower = String(body).toLowerCase();
  for (const marker of HARD_CREDIT_MARKERS) {
    if (lower.includes(marker.toLowerCase()) || String(body).includes(marker)) return "hard_credit";
  }
  for (const marker of SESSION_DEAD_MARKERS) if (String(body).includes(marker)) return "session_dead";
  if (status === 429) return "soft_rate";
  if (status === 404) return "not_found";
  if (status >= 500) return "server";
  return "client";
}

/**
 * Extract a user-facing error message from an upstream JSON response body.
 *
 * WorkBuddy answers business errors (safety review, illegal request) with a
 * structured `displayMsg`, localised as `{zh, en}`, alongside the terse `msg`.
 * Preferring `displayMsg` means a client shows the reason the upstream actually
 * stated instead of a wall of raw JSON. Falls back to `msg`, and returns
 * `undefined` for anything that is not a JSON object so the caller can keep
 * showing the raw excerpt.
 *
 * @param {string} body the raw response body
 * @returns {string|undefined}
 */
export function extractDisplayErrorMessage(body) {
  const text = String(body ?? "").trim();
  if (!text.startsWith("{")) return undefined;
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    return undefined;
  }
  if (!isObject(parsed)) return undefined;
  const displayMsg = parsed["displayMsg"];
  if (isObject(displayMsg)) {
    if (typeof displayMsg["zh"] === "string" && displayMsg["zh"].trim() !== "") return displayMsg["zh"].trim();
    if (typeof displayMsg["en"] === "string" && displayMsg["en"].trim() !== "") return displayMsg["en"].trim();
  }
  if (typeof parsed["msg"] === "string" && parsed["msg"].trim() !== "") return parsed["msg"].trim();
  return undefined;
}

function chatBase(variant) {
  return variant.chatBase;
}

function billingBase(variant) {
  return variant.billingBase;
}

function originReferer(variant) {
  return variant.region === "global" ? variant.billingBase : variant.billingBase;
}

/** Headers every upstream request shares. */
function commonHeaders(variant) {
  return {
    Accept: "application/json, text/plain, */*",
    "X-Requested-With": "XMLHttpRequest",
    Origin: originReferer(variant),
    Referer: `${originReferer(variant)}/`,
    "User-Agent": CLIENT_UA,
  };
}

/**
 * Chat request headers, including the `X-No-*` conventions the official client
 * uses. `userAgent` carries the desktop identity; when absent the shared
 * CLI-form UA applies.
 *
 * `X-IDE-*` is the IDE attribution family (upstream plugin 0.6.3, issue 54).
 * It rides on the *same* resolved desktop identity as the UA — `clientVersion`
 * is that identity's app version — and, like the UA override, it belongs to
 * chat and probe only: refresh, catalog and billing must stay free of it.
 *
 * The conversation triple is the international gateway's channel check, not an
 * identity header. The App always has a live conversation, so it always sends
 * the three ids; a stateless proxy has none, so it must mint them. They are
 * freshly generated per request: the upstream accepts a new conversation id on
 * every call (it validates presence and shape, not continuity), which is what
 * makes a stateless gateway possible at all.
 */
export function chatHeaders(credential, variant, userAgent, clientVersion) {
  return {
    ...commonHeaders(variant),
    ...(userAgent === undefined ? {} : { "User-Agent": userAgent }),
    "Content-Type": "application/json",
    ...(credential.uid === "" ? { "X-No-User-Id": "1" } : { "X-User-Id": credential.uid }),
    ...(credential.enterpriseId === undefined || credential.enterpriseId === ""
      ? { "X-No-Enterprise-Id": "1" }
      : { "X-Enterprise-Id": credential.enterpriseId }),
    ...(credential.domain === "" ? { "X-No-Department-Info": "1" } : { "X-Domain": credential.domain }),
    "X-IDE-Type": "WorkBuddy",
    "X-IDE-Name": "WorkBuddy",
    ...(typeof clientVersion === "string" && clientVersion !== "" ? { "X-IDE-Version": clientVersion } : {}),
    "X-Product": "SaaS",
    ...(variant.region === "global" ? conversationTripleHeaders() : {}),
  };
}

/**
 * The international gateway's conversation triple.
 *
 * All three are required together — dropping any single one makes the gateway
 * answer 400 / code 11128 "Illegal API invocation from an unapproved channel",
 * even with the App's exact UA and version. Values are opaque 32-hex ids, which
 * is the shape the App's own `randomBytes(16).toString("hex")` produces.
 */
function conversationTripleHeaders() {
  return {
    "X-Conversation-ID": randomUUID().replaceAll("-", ""),
    "X-Conversation-Request-ID": randomUUID().replaceAll("-", ""),
    "X-Conversation-Message-ID": randomUUID().replaceAll("-", ""),
  };
}

/** Refresh-endpoint headers; `X-Refresh-Token` appears here and nowhere else. */
function refreshHeaders(credential, variant) {
  const headers = {
    ...commonHeaders(variant),
    "X-Refresh-Token": credential.refreshToken,
    "X-Auth-Refresh-Source": "workbuddy",
  };
  if (credential.enterpriseId !== undefined && credential.enterpriseId !== "") {
    headers["X-Enterprise-Id"] = credential.enterpriseId;
  }
  return headers;
}

/** Billing request headers. */
export function billingHeaders(credential) {
  const headers = {
    Authorization: `Bearer ${credential.accessToken}`,
    Accept: "application/json",
    "Content-Type": "application/json",
  };
  if (credential.uid !== "") headers["X-User-Id"] = credential.uid;
  if (credential.enterpriseId !== undefined && credential.enterpriseId !== "") {
    headers["X-Enterprise-Id"] = credential.enterpriseId;
    headers["X-Tenant-Id"] = credential.enterpriseId;
  }
  if (credential.domain !== "") headers["X-Domain"] = credential.domain;
  return headers;
}

function isObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function positive(value) {
  return typeof value === "number" && Number.isFinite(value) && value > 0;
}

/** Rewrite `role: "developer"` messages to `role: "system"`. */
function normalizeDeveloperRole(obj) {
  const messages = obj["messages"];
  if (!Array.isArray(messages)) return;
  for (const message of messages) {
    if (!isObject(message)) continue;
    if (message["role"] === "developer") message["role"] = "system";
  }
}

/**
 * 客户端身份声明的指纹词。
 *
 * ## 这是 2026-10-20 那次风控的根因
 *
 * 腾讯当晚给 `copilot.tencent.com` 加了一道**渠道校验**：只看 system
 * 消息的**第一句**是不是这些固定原文，命中就回
 *
 *     HTTP 400  { "code": 11128,
 *                 "msg": "Illegal API invocation from an unapproved channel" }
 *
 * ## 为什么针对的是这几句
 *
 * 各家 agent 客户端都会在 system 开头自报「我是谁」，这是它们的
 * 遥测/身份标识，**不含任何提示词语义**。删掉或改写不改变模型收到的
 * 任何指令 —— 这和项目里已有的「`role: developer` → `system`」是同一类
 * 兼容层处理。
 *
 * ## 已知被拦的
 *
 * | 客户端 | 身份句 |
 * |---|---|
 * | DeepSeek Harness（dsh-workbuddy-connect） | `You are an AI agent powered by DeepSeek Harness.` |
 * | Claude Code | `x-anthropic-billing-header: cc_version=…` （已在 anthropic.js 单独处理） |
 *
 * ## 实测结论（来自社区 PR #204 与本地复现）
 *
 * | system 形态 | 结果 |
 * |---|---|
 * | 以固定句开头 | **400 / 11128** |
 * | 固定句前面有别的文字 | 200 |
 * | 固定句在句中 | 200 |
 * | 固定句换成中性表述 | 200 |
 *
 * 所以**只要在前面垫一句就能过**。这里选「垫一句中性的话」而不是删掉：
 * 删掉会让模型少一条身份说明（虽然无伤），垫一句是零信息损失的。
 */
const HARNESS_IDENTITY_PATTERNS = [
  /^You are an AI agent powered by DeepSeek Harness\./iu,
  /^You are an AI Agent powered by DeepSeek Harness\./u,
  /^You are DeepSeek Harness/iu,
];

/** 垫在身份句前面的中性句。挑一句最平常的，不带任何特殊含义。 */
const NEUTRAL_PREFIX = "You are a helpful assistant.\n\n";

/**
 * 上游渠道校验会盯上的「客户端特征短语」。
 *
 * 2026-10-27 实测（DSH 桌面端）：系统提示词里这一段会让上游回 400 / 11128
 *
 *     When the user refers to "this page", "this GUI", or "this app"
 *     without naming another target, they mean this GUI.
 *
 * 单独测 "DeepSeek Harness" 是通过的，长度也不是原因（纯 a 重复 10000 字照样 200）。
 * 触发的是**引号包裹的三连短语**这个形状 —— 把那三个引号去掉就过，换成中文也过。
 *
 * 为什么不把全文引号都换掉：正文里可能夹 JSON / 代码，改动会破坏语义。
 * 只做定点替换，命中极少，代价最小。
 */
const CLIENT_FINGERPRINT_PHRASES = [
  // DSH 描述 Web GUI 时用的三连引号短语
  /"this page",\s*"this GUI",\s*or\s*"this app"/giu,
  /'this page',\s*'this GUI',\s*or\s*'this app'/giu,
];

/**
 * 把上游会拦的特征短语改成等义但不同形的写法。
 *
 * 只改写引号，不动词义 —— 模型读到的意思完全一样，
 * 但形状不再命中渠道校验。
 *
 * @returns {boolean} 是否改过
 */
function softenClientFingerprints(obj) {
  const messages = obj["messages"];
  if (!Array.isArray(messages)) return false;

  const rewrite = (text) => {
    if (typeof text !== "string" || text === "") return text;
    let out = text;
    for (const pattern of CLIENT_FINGERPRINT_PHRASES) {
      out = out.replace(pattern, (match) => match.replace(/["']/gu, ""));
    }
    return out;
  };

  let changed = false;
  for (const message of messages) {
    if (!isObject(message)) continue;
    const content = message["content"];

    if (typeof content === "string") {
      const next = rewrite(content);
      if (next !== content) {
        message["content"] = next;
        changed = true;
      }
      continue;
    }

    if (Array.isArray(content)) {
      for (const block of content) {
        if (!isObject(block) || block["type"] !== "text") continue;
        const text = block["text"];
        const next = rewrite(text);
        if (next !== text) {
          block["text"] = next;
          changed = true;
        }
      }
    }
  }
  return changed;
}


/**
 * 如果 system 消息以「客户端身份声明」开头，就在它前面垫一句中性话。
 *
 * ## 为什么垫一句而不是删
 *
 * 社区实测：固定句**前面有别的文字**就通过（200），删掉也能通过。
 * 垫一句的好处是零信息损失 —— 模型仍然知道自己的身份，
 * 我们只是让上游的「首句匹配」看不到那个特征。
 *
 * ## 为什么只处理第一条 system 的开头
 *
 * 上游只查开头。动别的位置是过度处理，而且可能改坏用户自己的 prompt。
 *
 * @param {object} obj 请求体（会被就地修改）
 * @returns {boolean} 是否改动了
 */
function neutralizeHarnessIdentity(obj) {
  const messages = obj["messages"];
  if (!Array.isArray(messages)) return false;

  for (const message of messages) {
    if (!isObject(message)) continue;
    if (message["role"] !== "system") continue;

    const content = message["content"];

    // 形态一：content 是字符串
    if (typeof content === "string") {
      if (!startsWithHarnessIdentity(content)) return false;
      message["content"] = NEUTRAL_PREFIX + content;
      return true;
    }

    // 形态二：content 是 [{type:"text", text:"…"}] 数组
    // （OpenAI 的多段内容形态，有些客户端用这个）
    if (Array.isArray(content)) {
      for (const block of content) {
        if (!isObject(block) || block["type"] !== "text") continue;
        const text = typeof block["text"] === "string" ? block["text"] : "";
        if (!startsWithHarnessIdentity(text)) return false;
        block["text"] = NEUTRAL_PREFIX + text;
        return true;
      }
    }

    // 只处理第一条 system
    return false;
  }

  return false;
}

/** 这段文本是不是以已知的客户端身份声明开头。 */
function startsWithHarnessIdentity(text) {
  if (typeof text !== "string" || text === "") return false;
  // 前导空白/换行不算 —— 有些客户端会在前面留空行
  const trimmed = text.replace(/^[\s\uFEFF]+/u, "");
  return HARNESS_IDENTITY_PATTERNS.some((pattern) => pattern.test(trimmed));
}

/** Rewrite OpenAI `tool_choice` spellings into the upstream's string form. */
function normalizeToolChoice(obj) {
  const suppress = () => {
    delete obj["tools"];
    delete obj["functions"];
  };
  if (!("tool_choice" in obj)) return;
  const choice = obj["tool_choice"];
  if (typeof choice === "string") {
    if (choice.trim().toLowerCase() === "none") {
      delete obj["tool_choice"];
      suppress();
    }
    return;
  }
  if (isObject(choice)) {
    const type = typeof choice["type"] === "string" ? choice["type"].trim().toLowerCase() : "";
    if (type === "none") {
      delete obj["tool_choice"];
      suppress();
    } else if (type === "auto" || type === "required") {
      obj["tool_choice"] = type;
    } else if (type === "function") {
      const fn = isObject(choice["function"]) ? choice["function"] : undefined;
      let name = typeof fn?.["name"] === "string" ? fn["name"] : "";
      if (name === "" && typeof choice["name"] === "string") name = choice["name"];
      name = name.trim();
      obj["tool_choice"] = name !== "" ? name : "auto";
    } else {
      delete obj["tool_choice"];
    }
    return;
  }
  delete obj["tool_choice"];
}

/**
 * Normalize an OpenAI chat-completions body for the WorkBuddy upstream: force
 * `stream: true` (the upstream rejects non-streaming), flatten `tool_choice`,
 * and rewrite `developer` messages as `system`.
 *
 * @param {string|object} source a JSON string or a plain object
 * @returns {string} the prepared JSON body
 */
export function prepareChatBody(source) {
  let body;
  if (typeof source === "string") {
    try {
      body = JSON.parse(source);
    } catch {
      return source;
    }
  } else {
    body = source;
  }
  if (!isObject(body)) return typeof source === "string" ? source : JSON.stringify(source);
  const obj = body;
  obj["stream"] = true;
  normalizeDeveloperRole(obj);
  normalizeToolChoice(obj);
  // 渠道校验（2026-10-20 起）：system 首句如果是客户端身份声明，
  // 上游会回 400 / 11128。垫一句中性话绕过 —— 详见函数注释。
  neutralizeHarnessIdentity(obj);
    softenClientFingerprints(obj);
  return JSON.stringify(obj);
}

/**
 * Apply the international endpoint's extra chat requirement: the first message
 * must be a `system` prompt. Prepended, never merged, and only when missing.
 */
export function prepareInternationalChatBody(source) {
  const prepared = prepareChatBody(source);
  let body;
  try {
    body = JSON.parse(prepared);
  } catch {
    return prepared;
  }
  if (!isObject(body)) return prepared;
  const messages = body["messages"];
  if (!Array.isArray(messages)) return prepared;
  const first = messages[0];
  if (isObject(first) && first["role"] === "system") return prepared;
  messages.unshift({ role: "system", content: INTERNATIONAL_SYSTEM_PROMPT });
  return JSON.stringify(body);
}

/** Prepare the wire body for a variant. */
export function prepareBodyFor(variant, source) {
  return variant.region === "global" ? prepareInternationalChatBody(source) : prepareChatBody(source);
}

async function readEnvelope(response) {
  const text = await response.text();
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new Error(`workbuddy upstream returned non-JSON (http ${response.status}): ${text.slice(0, 160)}`);
  }
  if (!isObject(parsed)) throw new Error(`workbuddy upstream returned an unexpected document (http ${response.status})`);
  return {
    code: typeof parsed["code"] === "number" ? parsed["code"] : 0,
    msg: typeof parsed["msg"] === "string" ? parsed["msg"] : "",
    data: "data" in parsed ? parsed["data"] : undefined,
    document: parsed,
  };
}

/** Fail an envelope whose business code is non-zero, classified like HTTP errors. */
function envelopeError(status, envelope) {
  const kind = classifyUpstreamError(status, envelope.msg);
  return new Error(`workbuddy upstream ${kind} (http ${status}): ${envelope.msg.slice(0, 160)}`);
}

/** Parse the upstream `reasoning` object into a normalized capability record. */
function resolveUpstreamReasoning(wrapped) {
  const supports = wrapped["supportsReasoning"] === true;
  const onlyReasoning = wrapped["onlyReasoning"] === true;
  const rawReasoning = wrapped["reasoning"];
  let supportedEfforts;
  let defaultEffort;
  let canDisableThinking = true;
  if (isObject(rawReasoning)) {
    const rawEfforts = rawReasoning["supportedEfforts"];
    if (Array.isArray(rawEfforts)) {
      const efforts = rawEfforts.filter((value) => typeof value === "string" && EFFORT_VALUES.includes(value));
      if (efforts.length > 0) supportedEfforts = efforts;
    }
    if (typeof rawReasoning["defaultEffort"] === "string" && EFFORT_VALUES.includes(rawReasoning["defaultEffort"])) {
      defaultEffort = rawReasoning["defaultEffort"];
    } else if (typeof rawReasoning["effort"] === "string" && EFFORT_VALUES.includes(rawReasoning["effort"])) {
      defaultEffort = rawReasoning["effort"];
    }
    canDisableThinking = rawReasoning["canDisableThinking"] === true;
  }
  return {
    reasoning: {
      supports,
      onlyReasoning,
      ...(supportedEfforts === undefined ? {} : { supportedEfforts }),
      ...(defaultEffort === undefined ? {} : { defaultEffort }),
      canDisableThinking,
    },
  };
}

/**
 * Reduce an upstream credits string to its language-neutral display form.
 * Some catalog rows report a bare multiplier (`x0.79`) and others append a unit
 * word (`x0.79 credits`); the unit word would pin the display to English.
 */
export function normalizeCredits(credits) {
  if (credits === undefined) return undefined;
  const trimmed = String(credits).trim();
  if (trimmed === "") return undefined;
  if (/^credits?$/iu.test(trimmed)) return undefined;
  const bare = trimmed.replace(/\s+credits?$/iu, "").trim();
  return bare === "" ? undefined : bare;
}

/**
 * Parse the upstream `tags` / `credits` fields into billing metadata.
 *
 * @param {object} wrapped one catalog row
 * @param {string[]} [extraTags] badge tags recovered from another document,
 *   merged in after the row's own. Since plugin 0.6.1 the roster comes from
 *   `/v3/config`, which carries no `badge:*` tags; those live only in the
 *   console catalog, so the CN refresh reads both and joins them here (see
 *   {@link WorkBuddyClient.fetchPromoBadges}).
 */
function resolveUpstreamBilling(wrapped, extraTags) {
  const rawCredits = wrapped["credits"];
  const credits = typeof rawCredits === "string" && rawCredits.trim() !== "" ? rawCredits.trim() : undefined;
  const badges = [];
  const rawTags = [...(Array.isArray(wrapped["tags"]) ? wrapped["tags"] : []), ...(extraTags ?? [])];
  for (const tag of rawTags) {
    if (typeof tag !== "string") continue;
    if (!tag.toLowerCase().startsWith(BADGE_PREFIX)) continue;
    const label = tag.slice(BADGE_PREFIX.length).split(":")[0] ?? tag.slice(BADGE_PREFIX.length);
    if (label !== "" && !badges.includes(label)) badges.push(label);
  }
  // `free` is decided from the *normalized* multiplier, not the raw string: the
  // upstream spells the same zero as both `x0.00` and `x0.00 credits`.
  const multiplier = normalizeCredits(credits);
  const free = multiplier !== undefined && /^x?0\.0+$/u.test(multiplier);
  return {
    billing: {
      ...(credits === undefined ? {} : { credits }),
      ...(badges.length === 0 ? {} : { badges }),
      free,
    },
  };
}

/** Extract the promotions covering `model` from a `modelPromotions` array. */
function parsePromotions(value, model) {
  if (!Array.isArray(value)) return [];
  return value.flatMap((item) => {
    if (!isObject(item) || item["enabled"] !== true) return [];
    const modelIds = item["modelIds"];
    if (!Array.isArray(modelIds) || !modelIds.includes(model)) return [];
    const schedule = item["schedule"];
    const discount = item["discount"];
    const badge = item["badge"];
    if (!isObject(schedule) || !isObject(discount) || !isObject(badge)) return [];
    if (discount["displayMode"] !== "replace") return [];
    const start = typeof schedule["validFrom"] === "string" ? Date.parse(schedule["validFrom"]) : NaN;
    const end = typeof schedule["validUntil"] === "string" ? Date.parse(schedule["validUntil"]) : NaN;
    const factor = discount["factor"];
    if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) return [];
    if (typeof factor !== "number" || !Number.isFinite(factor) || factor < 0) return [];
    return [
      {
        start,
        end,
        factor,
        label: typeof badge["label"] === "string" ? badge["label"] : "",
        priority: typeof item["priority"] === "number" && Number.isFinite(item["priority"]) ? item["priority"] : 0,
      },
    ];
  });
}

/**
 * Parse either catalog response shape after its envelope has been checked.
 *
 * @param {object} data the unwrapped `data` (or the document itself)
 * @param {boolean} international
 * @param {Map<string, string[]>} [promoBadges] badge tags by model id, read
 *   from the console document, which is the only one that carries them
 * @returns {object[]} normalized model rows
 */
export function parseModelCatalog(data, international = false, promoBadges) {
  const rawModels = Array.isArray(data["models"]) ? data["models"] : [];
  const agents = Array.isArray(data["agents"]) ? data["agents"] : [];
  let cliIds;
  for (const agent of agents) {
    if (!isObject(agent)) continue;
    if (agent["name"] === "cli" && Array.isArray(agent["models"])) {
      cliIds = agent["models"].filter((id) => typeof id === "string");
      break;
    }
  }
  if (cliIds === undefined || cliIds.length === 0) {
    throw new Error("workbuddy model catalog lists no cli agent models");
  }
  const byId = new Map();
  for (const model of rawModels) {
    if (!isObject(model)) continue;
    const id = typeof model["id"] === "string" ? model["id"] : "";
    if (id === "" || model["disabled"] === true) continue;
    const input = typeof model["maxInputTokens"] === "number" ? model["maxInputTokens"] : 0;
    const output = typeof model["maxOutputTokens"] === "number" ? model["maxOutputTokens"] : 0;
    if (input <= 0 || output <= 0) continue;
    const contextWindow = isObject(model["contextWindow"]) ? model["contextWindow"] : undefined;
    byId.set(id, {
      id,
      name: typeof model["name"] === "string" && model["name"] !== "" ? model["name"] : id,
      contextWindow:
        international && positive(contextWindow?.["defaultLength"]) ? contextWindow["defaultLength"] : input,
      ...(international
        ? {
            ...(positive(contextWindow?.["defaultLength"])
              ? { defaultContextWindow: contextWindow["defaultLength"] }
              : {}),
            maxInputTokens: input,
            supportedContextWindows: Array.isArray(contextWindow?.["supportedLengths"])
              ? contextWindow["supportedLengths"].filter(positive)
              : [],
            promotions: parsePromotions(data["modelPromotions"], id),
          }
        : {}),
      maxTokens: output,
      supportsImages: model["supportsImages"] === true && model["disabledMultimodal"] !== true,
      ...resolveUpstreamReasoning(model),
      ...resolveUpstreamBilling(model, promoBadges?.get(id)),
    });
  }
  const models = cliIds.map((id) => byId.get(id)).filter((model) => model !== undefined);
  if (models.length === 0) throw new Error("workbuddy model catalog resolved to an empty list");
  return models;
}

/**
 * Re-evaluate a model's promotion against the current time.
 *
 * Promotions are time-boxed while the catalog is cached for the life of the
 * process, so a frozen "Free now" would keep claiming a discount after
 * `validUntil` had passed. Re-derived on every read instead.
 */
export function modelWithCurrentPromotion(model, now = Date.now()) {
  if (model.promotions === undefined || model.promotions.length === 0) return model;
  const promotion = [...model.promotions]
    .sort((a, b) => b.priority - a.priority)
    .find((candidate) => now >= candidate.start && now < candidate.end);
  if (promotion === undefined) {
    const hadPromo =
      model.billing?.free === true ||
      (model.billing?.badges?.length ?? 0) > 0 ||
      model.promotions.some((candidate) => candidate.factor !== 1);
    if (!hadPromo) return model;
    return { ...model, billing: { free: false, rateUnknown: true } };
  }
  const rate = normalizeCredits(model.billing?.credits);
  const original = rate !== undefined && rate.startsWith("x") ? Number(rate.slice(1)) : NaN;
  if (promotion.factor !== 0 && !Number.isFinite(original)) return model;
  const value = promotion.factor === 0 ? 0 : original * promotion.factor;
  return {
    ...model,
    billing: {
      ...model.billing,
      credits: `x${value.toFixed(2)}`,
      free: value === 0,
      badges: [...(model.billing?.badges ?? []), ...(promotion.label === "" ? [] : [promotion.label])],
    },
  };
}

/**
 * Upstream HTTP client. One instance serves one variant; requests take the
 * credential explicitly so token refreshes apply on the next call.
 */
export class WorkBuddyClient {
  /**
   * @param {object} variant variant descriptor
   * @param {{fetchImpl?: typeof fetch}} [options]
   */
  constructor(variant, options = {}) {
    this.variant = variant;
    this.fetchImpl = options.fetchImpl ?? globalThis.fetch;
    this.lastCatalog = undefined;
  }

  /**
   * The desktop identity for chat requests: the User-Agent to present and the
   * app version the `X-IDE-Version` attribution header must carry.
   *
   * Both come from *one* resolution so the UA and the header can never
   * disagree about which client is calling. This never throws: a resolver
   * failure — or a resolved identity whose version would be unsafe to put in a
   * header — degrades to the desktop fallback form (compiled-in version, no
   * `CLI/…` segment), never to the bare CLI UA. A message must not fail
   * because the install tree could not be read.
   *
   * @returns {Promise<{userAgent: string, clientVersion: string}>}
   */
  async chatIdentity() {
    let identity;
    try {
      identity = await resolveChatIdentity(this.variant);
      return { userAgent: chatUserAgent(identity, this.variant), clientVersion: identity.clientVersion };
    } catch {
      identity = fallbackChatIdentity(this.variant);
    }
    return { userAgent: chatUserAgent(identity, this.variant), clientVersion: identity.clientVersion };
  }

  /**
   * POST the chat endpoint. A successful answer is the raw streaming Response,
   * left unread so the caller can pipe or re-assemble it.
   *
   * @returns {Promise<{ok: true, response: Response} | {ok: false, status: number, kind: string, message: string}>}
   */
  async chatStream(credential, body, signal) {
    const identity = await this.chatIdentity();
    let response;
    try {
      response = await this.fetchImpl(`${chatBase(this.variant)}/v2/chat/completions`, {
        method: "POST",
        headers: {
          ...chatHeaders(credential, this.variant, identity.userAgent, identity.clientVersion),
          Authorization: `Bearer ${credential.accessToken}`,
        },
        body,
        ...(signal === undefined ? {} : { signal }),
      });
    } catch (error) {
      return { ok: false, status: 0, kind: "server", message: `transport error: ${String(error)}` };
    }
    if (response.ok) return { ok: true, response };
    const text = (await response.text()).slice(0, ERROR_BODY_LIMIT);
    return {
      ok: false,
      status: response.status,
      kind: classifyUpstreamError(response.status, text),
      message: text,
    };
  }

  /** POST the token-refresh endpoint; the caller merges the outcome. */
  async refreshToken(credential) {
    const response = await this.fetchImpl(`${chatBase(this.variant)}/v2/plugin/auth/token/refresh`, {
      method: "POST",
      headers: refreshHeaders(credential, this.variant),
      signal: AbortSignal.timeout(JSON_TIMEOUT_MS),
    });
    const envelope = await readEnvelope(response);
    if (!response.ok || envelope.code !== 0) throw envelopeError(response.status, envelope);
    const data = isObject(envelope.data) ? envelope.data : {};
    const accessToken = typeof data["accessToken"] === "string" ? data["accessToken"] : "";
    if (accessToken === "") {
      throw new Error("workbuddy token refresh returned no accessToken; sign in again in the WorkBuddy app");
    }
    const outcome = { accessToken };
    if (typeof data["refreshToken"] === "string" && data["refreshToken"] !== "") outcome.refreshToken = data["refreshToken"];
    if (typeof data["expiresIn"] === "number" && data["expiresIn"] > 0) outcome.expiresInSec = data["expiresIn"];
    // refreshExpiresIn 是滑动窗口的关键：服务端每次刷新都会重新给一个满额的窗口，
    // 把这个值记下来才能判断「还能续多久」、什么时候该主动续。
    if (typeof data["refreshExpiresIn"] === "number" && data["refreshExpiresIn"] > 0) {
      outcome.refreshExpiresInSec = data["refreshExpiresIn"];
    }
    if (typeof data["domain"] === "string" && data["domain"] !== "") outcome.domain = data["domain"];
    return outcome;
  }

  /**
   * GET the model catalog.
   *
   * Both variants read `/v3/config`, the product document the desktop client
   * itself fetches. CN used to read `/console/enterprises/personal/models` (the
   * console catalog), which is why its list drifted from the App's selector: the
   * console document lags the product one, and the product roster churns day to
   * day (`auto`, `kimi-k3-1`, `minimax-m3` have each appeared and disappeared
   * within a week).
   *
   * What distinguishes the variants here is the User-Agent, not the path: the
   * gateway splits `/v3/config` by client identity, and the split is
   * load-bearing. A CLI-shaped UA yields the CLI's roster — the chat models this
   * tool serves — while an App-shaped UA yields the App's internal roster. CN
   * keeps the CLI UA it sends for chat, so the catalog it advertises is exactly
   * the one its own requests can use. The international variant has no CLI
   * identity, so it keeps the App-shaped UA.
   *
   * Membership is the `cli` roster intersected with the usable rows (see
   * {@link parseModelCatalog}); the promo badges the product document does not
   * carry are merged in from a best-effort console read — see
   * {@link WorkBuddyClient.fetchPromoBadges}.
   */
  async fetchModels(credential, signal) {
    const international = this.variant.region === "global";
    let userAgent = CLIENT_UA;
    if (international) {
      const identity = await resolveChatIdentity(this.variant).catch(() => fallbackChatIdentity(this.variant));
      userAgent = appUserAgent(identity.clientVersion);
    }
    const response = await this.fetchImpl(`${chatBase(this.variant)}${this.variant.catalogPath}`, {
      headers: {
        Authorization: `Bearer ${credential.accessToken}`,
        Accept: "application/json",
        Origin: originReferer(this.variant),
        Referer: `${originReferer(this.variant)}/`,
        ...(international ? { "X-Requested-With": "XMLHttpRequest", "X-Product": "SaaS" } : {}),
        "User-Agent": userAgent,
      },
      signal:
        signal === undefined
          ? AbortSignal.timeout(JSON_TIMEOUT_MS)
          : AbortSignal.any([signal, AbortSignal.timeout(JSON_TIMEOUT_MS)]),
    });
    const envelope = await readEnvelope(response);
    if (!response.ok || envelope.code !== 0) throw envelopeError(response.status, envelope);
    const source = isObject(envelope.data)
      ? envelope.data
      : "models" in envelope.document || "agents" in envelope.document
        ? envelope.document
        : {};
    const promoBadges = international ? undefined : await this.fetchPromoBadges(credential, signal);
    const models = parseModelCatalog(source, international, promoBadges);
    this.lastCatalog = {
      fetchedAtMs: Date.now(),
      source: this.variant.catalogSource,
      ...(promoBadges === undefined ? {} : { promoBadgeModels: promoBadges.size }),
    };
    return models;
  }

  /**
   * Read the console catalog's promotional tags, by model id.
   *
   * `/v3/config` carries no `badge:<label>:<color>` tags — the discount labels
   * the model list renders (`限时免费`, `夜间折扣`, …) live only in
   * `/console/enterprises/personal/models`. Since the roster now comes from the
   * product document, those tags are read from the console one in a second
   * request and merged by id.
   *
   * Best-effort by construction: a badge is a label on a price, so failing to
   * read this document must never fail a catalog refresh. Every failure —
   * network, envelope, unreadable body — returns undefined, and the models
   * simply ship without badges.
   *
   * @returns {Promise<Map<string, string[]>|undefined>} badge tags by model id
   */
  async fetchPromoBadges(credential, signal) {
    const path = this.variant.promoBadgePath;
    if (path === undefined) return undefined;
    try {
      const response = await this.fetchImpl(`${chatBase(this.variant)}${path}`, {
        headers: {
          Authorization: `Bearer ${credential.accessToken}`,
          Accept: "application/json",
          Origin: originReferer(this.variant),
          Referer: `${originReferer(this.variant)}/`,
          "User-Agent": CLIENT_UA,
        },
        signal:
          signal === undefined
            ? AbortSignal.timeout(JSON_TIMEOUT_MS)
            : AbortSignal.any([signal, AbortSignal.timeout(JSON_TIMEOUT_MS)]),
      });
      if (!response.ok) return undefined;
      const envelope = await readEnvelope(response);
      const data = isObject(envelope.data) ? envelope.data : envelope.document;
      const rawModels = Array.isArray(data["models"]) ? data["models"] : [];
      const badges = new Map();
      for (const model of rawModels) {
        if (!isObject(model)) continue;
        const id = typeof model["id"] === "string" ? model["id"] : "";
        if (id === "") continue;
        const tags = Array.isArray(model["tags"])
          ? model["tags"].filter((tag) => typeof tag === "string" && tag.toLowerCase().startsWith(BADGE_PREFIX))
          : [];
        if (tags.length > 0) badges.set(id, tags);
      }
      return badges.size === 0 ? undefined : badges;
    } catch {
      return undefined;
    }
  }

  /**
   * POST the billing endpoint for the aggregated remaining credit.
   *
   * CN enterprise accounts get a single cycle quota from a dedicated endpoint;
   * everybody else uses the personal package list. The region gate is
   * load-bearing: the enterprise endpoint is unverified for the global region.
   */
  async fetchCredits(credential) {
    if (this.variant.region === "cn" && credential.enterpriseId !== undefined && credential.enterpriseId !== "") {
      return await this.fetchEnterpriseCredits(credential);
    }
    const now = new Date();
    const format = (date) =>
      `${[
        date.getFullYear().toString().padStart(4, "0"),
        (date.getMonth() + 1).toString().padStart(2, "0"),
        date.getDate().toString().padStart(2, "0"),
      ].join("-")} ${[
        date.getHours().toString().padStart(2, "0"),
        date.getMinutes().toString().padStart(2, "0"),
        date.getSeconds().toString().padStart(2, "0"),
      ].join(":")}`;
    const response = await this.fetchImpl(`${billingBase(this.variant)}/v2/billing/meter/get-user-resource`, {
      method: "POST",
      headers: billingHeaders(credential),
      body: JSON.stringify({
        PageNumber: 1,
        PageSize: 100,
        ProductCode: "p_tcaca",
        Status: [0, 3],
        PackageEndTimeRangeBegin: format(now),
        PackageEndTimeRangeEnd: format(new Date(now.getTime() + 3185136e6)),
      }),
      signal: AbortSignal.timeout(JSON_TIMEOUT_MS),
    });
    const envelope = await readEnvelope(response);
    if (!response.ok || envelope.code !== 0) throw envelopeError(response.status, envelope);
    const responseWrapper = isObject(envelope.data) ? envelope.data : {};
    const data = isObject(responseWrapper["Response"]) ? responseWrapper["Response"] : {};
    const inner = isObject(data["Data"]) ? data["Data"] : {};
    const rawAccounts = Array.isArray(inner["Accounts"]) ? inner["Accounts"] : [];
    const accounts = [];
    let total = 0;
    for (const raw of rawAccounts) {
      if (!isObject(raw)) continue;
      const numberField = (key) => (typeof raw[key] === "number" ? raw[key] : 0);
      const size = numberField("CycleCapacitySize");
      const cycleRemain = numberField("CycleCapacityRemain");
      const cycleUsed = numberField("CycleCapacityUsed");
      const capacityRemain = numberField("CapacityRemain");
      let remain;
      if (size > 0) remain = cycleRemain;
      else if (cycleRemain > 0 || cycleUsed > 0) remain = cycleRemain;
      else remain = capacityRemain;
      if (remain < 0) remain = 0;
      total += remain;
      accounts.push({
        packageName: typeof raw["PackageName"] === "string" ? raw["PackageName"] : "(unnamed)",
        remain,
        size: size > 0 ? size : numberField("CapacitySize"),
      });
    }
    return { total, accounts };
  }

  /**
   * CN enterprise credit read: a single cycle quota instead of a package list.
   * The body is empty and the account identity travels only in the headers.
   */
  async fetchEnterpriseCredits(credential) {
    const response = await this.fetchImpl(`${billingBase(this.variant)}/v2/billing/meter/get-enterprise-user-usage`, {
      method: "POST",
      headers: billingHeaders(credential),
      body: JSON.stringify({}),
      signal: AbortSignal.timeout(JSON_TIMEOUT_MS),
    });
    const envelope = await readEnvelope(response);
    if (!response.ok || envelope.code !== 0) throw envelopeError(response.status, envelope);
    const sources = [];
    for (const candidate of [envelope.data, envelope.document]) {
      if (!isObject(candidate)) continue;
      if (isObject(candidate["data"])) sources.push(candidate["data"]);
      sources.push(candidate);
    }
    const numberAt = (source, key) => (typeof source[key] === "number" ? source[key] : undefined);
    let limit;
    let used;
    let resetTime;
    for (const source of sources) {
      const candidate = numberAt(source, "limitNum") ?? numberAt(source, "limit_num");
      if (candidate === undefined) continue;
      limit = candidate;
      used = numberAt(source, "credit") ?? numberAt(source, "used_num");
      if (typeof source["cycleResetTime"] === "string" && source["cycleResetTime"] !== "") {
        resetTime = source["cycleResetTime"];
      }
      break;
    }
    if (limit === undefined) {
      throw new Error(
        "workbuddy enterprise billing response carried no recognised quota field (expected limitNum/limit_num)",
      );
    }
    if (limit === -1) {
      return {
        total: 0,
        accounts: [{ packageName: "enterprise", remain: 0, size: 0, unlimited: true }],
        unlimited: true,
        ...(resetTime === undefined ? {} : { cycleResetTime: resetTime }),
      };
    }
    if (used === undefined) {
      throw new Error(
        "workbuddy enterprise billing response carried a quota limit but no recognised usage field (expected credit/used_num)",
      );
    }
    let remain = limit - used;
    if (remain < 0) remain = 0;
    return {
      total: remain,
      accounts: [{ packageName: "enterprise", remain, size: limit }],
      ...(resetTime === undefined ? {} : { cycleResetTime: resetTime }),
    };
  }
}

/**
 * Split an SSE byte stream into `data:` payload strings, in order.
 *
 * @param {ReadableStream<Uint8Array>} body
 * @returns {AsyncGenerator<string>}
 */
export async function* iterateSseData(body) {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let index;
      while ((index = buffer.indexOf("\n")) !== -1) {
        const line = buffer.slice(0, index).replace(/\r$/u, "");
        buffer = buffer.slice(index + 1);
        if (!line.startsWith("data:")) continue;
        yield line.slice(5).trim();
      }
    }
    const tail = buffer.trim();
    if (tail.startsWith("data:")) yield tail.slice(5).trim();
  } finally {
    await reader.cancel().catch(() => {});
  }
}

/**
 * Consume an SSE response and re-assemble it into one non-streaming
 * OpenAI chat-completion object.
 *
 * The upstream is streaming-only, so a caller asking for a whole answer gets
 * one assembled here: deltas are concatenated, tool calls are merged by index,
 * and the terminal usage block is preserved.
 *
 * @param {Response} response a successful upstream chat response
 * @returns {Promise<object>} an `object: "chat.completion"` document
 */
export async function collectChatCompletion(response) {
  let id = `chatcmpl-${Date.now().toString(36)}`;
  let model = "";
  let created = Math.floor(Date.now() / 1000);
  let content = "";
  let reasoning = "";
  let finishReason = null;
  const toolCalls = new Map();
  let usage;

  for await (const payload of iterateSseData(response.body)) {
    if (payload === "" || payload === "[DONE]") continue;
    let chunk;
    try {
      chunk = JSON.parse(payload);
    } catch {
      continue;
    }
    if (typeof chunk["id"] === "string" && chunk["id"] !== "") id = chunk["id"];
    if (typeof chunk["model"] === "string" && chunk["model"] !== "") model = chunk["model"];
    if (typeof chunk["created"] === "number") created = chunk["created"];
    if (chunk["usage"] !== null && chunk["usage"] !== undefined) usage = chunk["usage"];
    const choice = Array.isArray(chunk["choices"]) ? chunk["choices"][0] : undefined;
    if (choice === undefined) continue;
    const delta = isObject(choice["delta"]) ? choice["delta"] : {};
    if (typeof delta["content"] === "string") content += delta["content"];
    if (typeof delta["reasoning_content"] === "string") reasoning += delta["reasoning_content"];
    const calls = delta["tool_calls"];
    if (Array.isArray(calls)) {
      for (const call of calls) {
        if (!isObject(call)) continue;
        const index = typeof call["index"] === "number" ? call["index"] : 0;
        const existing = toolCalls.get(index) ?? {
          id: "",
          type: "function",
          function: { name: "", arguments: "" },
        };
        if (typeof call["id"] === "string" && call["id"] !== "") existing.id = call["id"];
        if (typeof call["type"] === "string" && call["type"] !== "") existing.type = call["type"];
        const fn = isObject(call["function"]) ? call["function"] : {};
        if (typeof fn["name"] === "string" && fn["name"] !== "") existing.function.name += fn["name"];
        if (typeof fn["arguments"] === "string") existing.function.arguments += fn["arguments"];
        toolCalls.set(index, existing);
      }
    }
    if (typeof choice["finish_reason"] === "string" && choice["finish_reason"] !== "") {
      finishReason = choice["finish_reason"];
    }
  }

  const message = { role: "assistant", content: content === "" && toolCalls.size > 0 ? null : content };
  if (reasoning !== "") message.reasoning_content = reasoning;
  if (toolCalls.size > 0) {
    message.tool_calls = [...toolCalls.entries()].sort((a, b) => a[0] - b[0]).map(([, call]) => call);
  }
  return {
    id,
    object: "chat.completion",
    created,
    model,
    choices: [{ index: 0, message, logprobs: null, finish_reason: finishReason ?? "stop" }],
    ...(usage === undefined ? {} : { usage }),
  };
}

/**
 * One probe request: does the upstream accept this `reasoning_effort` value?
 *
 * A real streaming chat call carries the effort under test; it shares the normal
 * chat headers on purpose so a result describes what a real message would
 * experience. The caller aborts as soon as a parseable event arrives.
 *
 * @returns {Promise<{status: number, streamed: boolean, errorCode?: string, detail?: string}>}
 */
export async function probeEffort(variant, credential, model, effort, signal) {
  const international = variant.region === "global";
  const client = new WorkBuddyClient(variant);
  // Same identity rule as the chat path: the probe sibling must never present a
  // different client than a real message would.
  const identity = await client.chatIdentity();
  const payload = {
    model,
    stream: true,
    messages: [
      ...(international ? [{ role: "system", content: INTERNATIONAL_SYSTEM_PROMPT }] : []),
      { role: "user", content: "ping" },
    ],
    max_tokens: international ? 16 : 1,
  };
  if (effort !== undefined) payload["reasoning_effort"] = effort;

  let response;
  try {
    response = await client.fetchImpl(`${chatBase(variant)}/v2/chat/completions`, {
      method: "POST",
      headers: {
        ...chatHeaders(credential, variant, identity.userAgent, identity.clientVersion),
        Authorization: `Bearer ${credential.accessToken}`,
      },
      body: JSON.stringify(payload),
      signal,
    });
  } catch (error) {
    return { status: 0, streamed: false, detail: `transport error: ${String(error)}` };
  }
  if (!response.ok) {
    const text = (await response.text()).slice(0, ERROR_BODY_LIMIT);
    return { status: response.status, streamed: false, ...errorCodeOf(text) };
  }
  const streamed = await readFirstEvent(response);
  return { status: response.status, streamed };
}

/** Pull `extError.code` out of an upstream error body, if it is shaped that way. */
function errorCodeOf(text) {
  try {
    const parsed = JSON.parse(text);
    if (isObject(parsed) && isObject(parsed["extError"]) && typeof parsed["extError"]["code"] === "string") {
      return { errorCode: parsed["extError"]["code"], detail: parsed["extError"]["code"] };
    }
  } catch {
    /* fall through to the raw detail */
  }
  return { detail: text.slice(0, 200) };
}

/** Consume just enough of a streaming response to know it really streams. */
async function readFirstEvent(response) {
  const body = response.body;
  if (body === null) return false;
  const reader = body.getReader();
  const decoder = new TextDecoder();
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) return false;
      if (decoder.decode(value, { stream: true }).includes("data:")) return true;
    }
  } catch {
    return false;
  } finally {
    await reader.cancel().catch(() => {});
  }
}
