/**
 * 非 OpenAI 协议的公共零件。
 *
 * 网关对外要同时讲三种话：
 *
 *   /v1/chat/completions   OpenAI Chat Completions   —— pi、以及一切「OpenAI 兼容」客户端
 *   /v1/messages           Anthropic Messages        —— Claude Code
 *   /v1/responses          OpenAI Responses          —— Codex
 *
 * 后两种在这里翻译成第一种，再交给 upstream.js 走 WorkBuddy 的私有协议。
 * 本文件放三种协议都要用到的东西：模型别名解析、token 估算、SSE 写出、stop_reason 映射。
 *
 * @module workbuddy-api/protocol
 */

/**
 * 取出一个模型的倍率字符串。
 *
 * 上游把倍率放在 `billing.credits`（形如 `x0.29 credits`），
 * 但早期版本和部分测试夹具把它平铺在顶层 `credits`。
 * 两处都认，避免免费模型永远选不中。
 */
function modelCredits(model) {
  const nested = model?.["billing"]?.["credits"];
  if (typeof nested === "string") return nested.toLowerCase();
  const flat = model?.["credits"];
  if (typeof flat === "string") return flat.toLowerCase();
  return "";
}

/** 归一化后的倍率，用来判断一个模型是不是免费。 */
function isFree(model) {
  const credits = modelCredits(model);
  if (credits === "") return false;
  return credits.replace(/\s*credits\s*$/u, "").trim() === "x0.00";
}

/**
 * 这个模型能不能真的吐出正文。
 *
 * 国内版的花名册里 20 个模型全部是 `onlyReasoning: true`，其中只有两个
 * 允许关掉思考（`canDisableThinking: true`）。其余的在 maxTokens 不大时
 * 会把预算全烧在 `reasoning_content` 上，`content` 回来是空串 ——
 * 客户端看到的就是「打开了但没有输出」。
 *
 * 所以挑兜底模型时优先避开这类「只能思考、且关不掉」的模型。
 */
function canEmitContent(model) {
  const reasoning = model?.["reasoning"];
  if (reasoning === undefined || reasoning === null) return true;
  if (reasoning["onlyReasoning"] !== true) return true;
  return reasoning["canDisableThinking"] === true;
}

/**
 * 选一个兜底模型。
 *
 * Claude Code 会报 `claude-sonnet-4-5-20250929`，Codex 会报 `gpt-5.6-sol`，
 * 这两个名字在 WorkBuddy 的花名册里都不存在，必须换掉。
 * 没显式配置时优先挑免费的，因为这个适配层的典型用途就是「省掉官方额度」。
 */
export function pickDefaultModel(list, configured) {
  if (typeof configured === "string" && configured.trim() !== "") {
    return { model: configured.trim(), reason: "配置的默认模型" };
  }

  // 第一优先：既免费、又能吐出正文的
  const free = list.find((model) => isFree(model) && canEmitContent(model));
  if (free !== undefined) return { model: free["id"], reason: "免费且能输出正文的模型（x0.00）" };

  // 第二优先：只要不是「只能思考且关不掉」，就还能用
  const usable = list.find(canEmitContent);
  if (usable !== undefined) return { model: usable["id"], reason: "能输出正文的模型" };

  // 全都只能思考 —— 矮子里拔将军，退回免费的那个，再退回第一个
  const anyFree = list.find(isFree);
  if (anyFree !== undefined) return { model: anyFree["id"], reason: "免费模型（x0.00，仅推理）" };
  if (list.length > 0) return { model: list[0]["id"], reason: "花名册第一个" };
  return { model: configured ?? "", reason: "花名册为空" };
}

/**
 * 建一个「客户端给的模型名 → 我们真正要调的模型名」的解析函数。
 *
 * 规则：
 *   1. 花名册里有的，原样用（pi 可以直接填 glm-5.3）
 *   2. 带前缀的写法规整一下（`workbuddy/glm-5.3` → `glm-5.3`、`openai/glm-5.3` 同理）
 *   3. 都不是 → 换成默认模型，并在日志里留一句，别让用户对着结果纳闷
 *
 * @param {object} options
 * @param {{current: () => object[]}} options.catalog
 * @param {string} [options.defaultModel]
 * @param {(line: string) => void} [options.log]
 */
export function createModelResolver({ catalog, defaultModel, log = () => {} }) {
  return function resolveModel(requested) {
    const list = catalog.current();
    const ids = new Set(list.map((model) => model["id"]));

    if (typeof requested === "string" && ids.has(requested)) {
      return { model: requested, aliased: false };
    }

    if (typeof requested === "string") {
      const slash = requested.lastIndexOf("/");
      if (slash !== -1) {
        const tail = requested.slice(slash + 1).trim();
        if (ids.has(tail)) return { model: tail, aliased: true, requested };
      }
    }

    const fallback = pickDefaultModel(list, defaultModel);
    if (requested !== undefined && requested !== "") {
      log(`模型别名：${requested} → ${fallback.model}（${fallback.reason}）`);
    }
    return { model: fallback.model, aliased: true, requested };
  };
}

/**
 * 估算 token 数。
 *
 * Anthropic 的 `/v1/messages/count_tokens` 需要给个数，但我们没法精确算
 * （上游不给分词器）。Claude Code 拿它做上下文管理，粗略值够用：
 * 英文约 4 字符 1 token，中日文约 1 字符 1 token，这里按保守的 3 字符 1 token 估。
 */
export function estimateTokens(value) {
  const text = typeof value === "string" ? value : JSON.stringify(value ?? "");
  let wide = 0;
  for (const char of text) {
    if (char.codePointAt(0) > 0x2e80) wide += 1;
  }
  const narrow = text.length - wide;
  return Math.max(1, Math.ceil(narrow / 3) + wide);
}

/** SSE 响应头。三种协议共用。 */
export function beginSse(res) {
  res.writeHead(200, {
    "Content-Type": "text/event-stream",
    "Cache-Control": "no-cache",
    Connection: "keep-alive",
    "X-Accel-Buffering": "no",
  });
  res.flushHeaders?.();
}

/**
 * 写一个 SSE 事件。
 *
 * Anthropic 和 Responses 都是命名事件（`event: x` 后面跟 `data: {...}`），
 * 这点和 Chat Completions 的匿名 data-only 不一样。
 */
export function writeEvent(res, name, payload) {
  res.write(`event: ${name}\ndata: ${JSON.stringify(payload)}\n\n`);
}

/** OpenAI 的 finish_reason → Anthropic 的 stop_reason。 */
export function toAnthropicStopReason(finishReason) {
  switch (finishReason) {
    case "tool_calls":
    case "function_call":
      return "tool_use";
    case "length":
      return "max_tokens";
    case "stop":
      return "end_turn";
    case "content_filter":
      return "refusal";
    case null:
    case undefined:
      return null;
    default:
      return "end_turn";
  }
}

/** 生成一个带前缀的随机 id，形状照着各家真实 API 抄。 */
export function makeId(prefix) {
  const random = Math.random().toString(36).slice(2, 12);
  return `${prefix}_${Date.now().toString(36)}${random}`;
}

/** usage 缺字段时补齐，客户端对 undefined 很敏感。 */
export function usageOf(usage) {
  const input = Number.isFinite(usage?.["prompt_tokens"]) ? usage["prompt_tokens"] : 0;
  const output = Number.isFinite(usage?.["completion_tokens"]) ? usage["completion_tokens"] : 0;
  return { input_tokens: input, output_tokens: output, total_tokens: input + output };
}
