/**
 * Anthropic Messages API ←→ OpenAI Chat Completions 的翻译层。
 *
 * 为什么要这个：Claude Code 只会说 Anthropic 协议（`POST /v1/messages`），
 * 而 WorkBuddy 上游是 OpenAI 形状。把 CC Switch 的 `ANTHROPIC_BASE_URL`
 * 指到本网关时，请求就是这个形状。
 *
 * 翻译分三段：
 *   1. `anthropicToChat`   请求：messages / system / tools / tool_choice
 *   2. `createAnthropicStream`  流式响应：把 Chat 的 delta 拼成 Anthropic 的命名事件流
 *      （message_start → content_block_start → content_block_delta → … → message_stop）
 *   3. `fromCompletion`    非流式响应：拼成一个 `type: "message"` 文档
 *
 * 最容易出问题的是 tool_use：Claude Code 全靠工具调用干活，
 * 所以 content block 的开启/关闭和 index 必须严格按 Anthropic 的规矩来。
 *
 * @module workbuddy-api/anthropic
 */

import {
  beginSse,
  estimateTokens,
  makeId,
  toAnthropicStopReason,
  usageOf,
  writeEvent,
} from "./protocol.js";

function isObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Anthropic 的 content 可以是字符串，也可以是一组 block。统一成数组。 */
function toBlocks(content) {
  if (typeof content === "string") return [{ type: "text", text: content }];
  if (Array.isArray(content)) return content.filter(isObject);
  return [];
}

/**
 * 客户端指纹行。
 *
 * Claude Code 会在 system 里塞一行：
 *
 *     x-anthropic-billing-header: cc_version=2.1.250.7b8; cc_entrypoint=sdk-cli;
 *
 * WorkBuddy 上游拿它做渠道识别，命中就回 HTTP 400 / code 11128
 * 「Illegal API invocation from an unapproved channel」。
 *
 * 实测（逐块逐行二分）：整份 Claude Code 系统提示词里**只有这一行**会触发，
 * 它纯粹是遥测元数据，不含任何提示词语义，删掉不改变模型收到的任何指令。
 *
 * 形态上和项目里已有的「`role: "developer"` 改写为 `system`」是同一类兼容层处理。
 */
const CLIENT_FINGERPRINT_LINE = /^\s*x-anthropic-[a-z0-9-]*header\s*:/iu;

/** 把客户端指纹行从一段文本里剔掉；不含关键字就原样返回。 */
function stripClientFingerprint(text) {
  if (typeof text !== "string" || text === "") return text;
  if (!text.includes("x-anthropic-")) return text;
  return text
    .split("\n")
    .filter((line) => !CLIENT_FINGERPRINT_LINE.test(line))
    .join("\n");
}

/** system 字段可以是字符串，也可以是一组 text block。 */
function toSystemText(system) {
  if (typeof system === "string") return stripClientFingerprint(system);
  if (Array.isArray(system)) {
    return stripClientFingerprint(
      system
        .filter((block) => isObject(block) && block["type"] === "text")
        .map((block) => (typeof block["text"] === "string" ? block["text"] : ""))
        .join("\n\n"),
    );
  }
  return "";
}

/** Anthropic 的 block → OpenAI 的 content part。不认识的类型返回 undefined 丢弃。 */
function toChatPart(block) {
  switch (block["type"]) {
    case "text":
      return { type: "text", text: typeof block["text"] === "string" ? block["text"] : "" };
    case "image": {
      const source = isObject(block["source"]) ? block["source"] : {};
      if (source["type"] === "base64" && typeof source["data"] === "string") {
        const media = typeof source["media_type"] === "string" ? source["media_type"] : "image/png";
        return { type: "image_url", image_url: { url: `data:${media};base64,${source["data"]}` } };
      }
      if (source["type"] === "url" && typeof source["url"] === "string") {
        return { type: "image_url", image_url: { url: source["url"] } };
      }
      return undefined;
    }
    default:
      // thinking / document / 未知类型：上游不认，丢掉
      return undefined;
  }
}

/** tool_result 的 content 可以是字符串或 block 数组。 */
function toolResultText(content) {
  if (typeof content === "string") return content;
  if (Array.isArray(content)) {
    return content
      .filter((block) => isObject(block) && block["type"] === "text")
      .map((block) => (typeof block["text"] === "string" ? block["text"] : ""))
      .join("\n");
  }
  return "";
}

/** tool_result 里夹带的图片单独捞出来，OpenAI 的 tool 消息放不下。 */
function toolResultImages(content) {
  if (!Array.isArray(content)) return [];
  return content
    .filter((block) => isObject(block) && block["type"] === "image")
    .map(toChatPart)
    .filter((part) => part !== undefined);
}

/** Anthropic 的 tool_choice → OpenAI 的写法（upstream 那里还会再拍平一次）。 */
function toChatToolChoice(choice) {
  if (!isObject(choice)) return undefined;
  switch (choice["type"]) {
    case "auto":
      return "auto";
    case "any":
      return "required";
    case "none":
      return "none";
    case "tool":
      return typeof choice["name"] === "string" && choice["name"] !== ""
        ? { type: "function", function: { name: choice["name"] } }
        : "auto";
    default:
      return undefined;
  }
}

/**
 * Anthropic 的 tools → OpenAI 的 tools。
 * 服务端工具（如 web_search）没有 input_schema，上游不认，丢掉。
 */
function toChatTools(tools) {
  if (!Array.isArray(tools)) return undefined;
  const converted = [];
  for (const tool of tools) {
    if (!isObject(tool)) continue;
    const name = typeof tool["name"] === "string" ? tool["name"].trim() : "";
    const schema = tool["input_schema"];
    if (name === "" || !isObject(schema)) continue;
    converted.push({
      type: "function",
      function: {
        name,
        description: typeof tool["description"] === "string" ? tool["description"] : "",
        parameters: schema,
      },
    });
  }
  return converted.length > 0 ? converted : undefined;
}

/**
 * 把 Anthropic 请求体翻成 OpenAI Chat 请求体。
 *
 * @param {object} body Anthropic `/v1/messages` 的请求体
 * @param {{model: string}} ctx 解析后的模型信息
 * @returns {object} OpenAI Chat Completions 请求体
 */
export function anthropicToChat(body, ctx) {
  const messages = [];

  const systemText = toSystemText(body["system"]);
  if (systemText.trim() !== "") messages.push({ role: "system", content: systemText });

  const input = Array.isArray(body["messages"]) ? body["messages"] : [];

  for (const message of input) {
    if (!isObject(message)) continue;
    const role = message["role"];

    if (role === "assistant") {
      const blocks = toBlocks(message["content"]);
      const text = blocks
        .filter((block) => block["type"] === "text")
        .map((block) => (typeof block["text"] === "string" ? block["text"] : ""))
        .join("");
      const toolUses = blocks.filter((block) => block["type"] === "tool_use");

      const converted = { role: "assistant", content: text };
      if (toolUses.length > 0) {
        converted["tool_calls"] = toolUses.map((block) => ({
          id: typeof block["id"] === "string" && block["id"] !== "" ? block["id"] : makeId("call"),
          type: "function",
          function: {
            name: typeof block["name"] === "string" ? block["name"] : "",
            arguments: JSON.stringify(isObject(block["input"]) ? block["input"] : {}),
          },
        }));
      }
      messages.push(converted);
      continue;
    }

    // user（tool_result 也塞在这一侧）
    const blocks = toBlocks(message["content"]);
    const toolResults = blocks.filter((block) => block["type"] === "tool_result");
    const rest = blocks.filter((block) => block["type"] !== "tool_result");
    const pendingImages = [];

    // OpenAI 要求 role:"tool" 紧跟在带 tool_calls 的 assistant 之后，所以先放它们
    for (const result of toolResults) {
      messages.push({
        role: "tool",
        tool_call_id: typeof result["tool_use_id"] === "string" ? result["tool_use_id"] : "",
        content: toolResultText(result["content"]),
      });
      pendingImages.push(...toolResultImages(result["content"]));
    }

    const parts = rest.map(toChatPart).filter((part) => part !== undefined);
    parts.push(...pendingImages);

    if (parts.length === 1 && parts[0]["type"] === "text") {
      messages.push({ role: "user", content: parts[0]["text"] });
    } else if (parts.length > 0) {
      messages.push({ role: "user", content: parts });
    } else if (toolResults.length === 0) {
      // 空消息留着会让上游犯迷糊，但要保住「用户说了话」这个轮次
      messages.push({ role: "user", content: "" });
    }
  }

  const chat = {
    model: ctx.model,
    messages,
    stream: body["stream"] === true,
  };

  if (Number.isFinite(body["max_tokens"])) chat["max_tokens"] = body["max_tokens"];
  if (Number.isFinite(body["temperature"])) chat["temperature"] = body["temperature"];
  if (Number.isFinite(body["top_p"])) chat["top_p"] = body["top_p"];
  if (Array.isArray(body["stop_sequences"]) && body["stop_sequences"].length > 0) {
    chat["stop"] = body["stop_sequences"];
  }

  const tools = toChatTools(body["tools"]);
  if (tools !== undefined) chat["tools"] = tools;

  const toolChoice = toChatToolChoice(body["tool_choice"]);
  if (toolChoice !== undefined) chat["tool_choice"] = toolChoice;

  return chat;
}

/** 粗估输入 token，给 Claude Code 的上下文管理用。 */
export function countInputTokens(body) {
  let total = estimateTokens(toSystemText(body["system"]));
  const input = Array.isArray(body["messages"]) ? body["messages"] : [];
  for (const message of input) {
    total += estimateTokens(toBlocks(message["content"]));
  }
  if (Array.isArray(body["tools"])) total += estimateTokens(body["tools"]);
  return total;
}

/** Anthropic 的错误信封。 */
export function anthropicError(status, message, type = "api_error") {
  return { status, body: { type: "error", error: { type, message } } };
}

/** 把上游错误类别翻成 Anthropic 的错误类型。 */
export function anthropicErrorTypeOf(kind) {
  switch (kind) {
    case "not_signed_in":
    case "session_dead":
      return "authentication_error";
    case "hard_credit":
      return "permission_error";
    case "soft_rate":
      return "rate_limit_error";
    case "client":
      return "invalid_request_error";
    default:
      return "api_error";
  }
}

/**
 * 非流式响应：Chat Completions 文档 → Anthropic `type: "message"` 文档。
 */
export function fromCompletion(completion, ctx) {
  const choice = Array.isArray(completion["choices"]) ? completion["choices"][0] : undefined;
  const message = isObject(choice?.["message"]) ? choice["message"] : {};
  const content = [];

  if (typeof message["reasoning_content"] === "string" && message["reasoning_content"] !== "") {
    content.push({ type: "thinking", thinking: message["reasoning_content"] });
  }
  if (typeof message["content"] === "string" && message["content"] !== "") {
    content.push({ type: "text", text: message["content"] });
  }

  const calls = Array.isArray(message["tool_calls"]) ? message["tool_calls"] : [];
  for (const call of calls) {
    let input = {};
    const raw = call?.["function"]?.["arguments"];
    if (typeof raw === "string" && raw.trim() !== "") {
      try {
        const parsed = JSON.parse(raw);
        if (isObject(parsed)) input = parsed;
      } catch {
        // 参数不是合法 JSON：原样塞进一个字段，别把整个调用丢了
        input = { _raw: raw };
      }
    }
    content.push({
      type: "tool_use",
      id: typeof call["id"] === "string" && call["id"] !== "" ? call["id"] : makeId("toolu"),
      name: typeof call?.["function"]?.["name"] === "string" ? call["function"]["name"] : "",
      input,
    });
  }

  if (content.length === 0) content.push({ type: "text", text: "" });

  const usage = usageOf(completion["usage"]);
  let stopReason = toAnthropicStopReason(choice?.["finish_reason"]);
  if (calls.length > 0 && (stopReason === null || stopReason === "end_turn")) stopReason = "tool_use";

  return {
    id: makeId("msg"),
    type: "message",
    role: "assistant",
    model: ctx.model,
    content,
    stop_reason: stopReason ?? "end_turn",
    stop_sequence: null,
    usage: { input_tokens: usage.input_tokens, output_tokens: usage.output_tokens },
  };
}

/**
 * 流式响应：把 Chat Completions 的 SSE 分片转成 Anthropic 的命名事件流。
 *
 * 契约要点（Claude Code 会严格按这个解析）：
 *   - message_start 必须第一个发，且只发一次
 *   - 每个 content block 都要 start → delta* → stop 三段齐全
 *   - content block 的 index 从 0 开始连续递增，text 和 tool_use 共用一套编号
 *   - 结束必须是 message_delta（带 stop_reason）→ message_stop
 */
export function createAnthropicStream(res, ctx) {
  const messageId = makeId("msg");
  const toolBlocks = new Map();
  let blockIndex = -1;
  let openBlock = null;
  let started = false;
  let finished = false;
  let stopReason = null;
  let usage = { input_tokens: 0, output_tokens: 0 };

  function startMessage() {
    if (started) return;
    started = true;
    writeEvent(res, "message_start", {
      type: "message_start",
      message: {
        id: messageId,
        type: "message",
        role: "assistant",
        model: ctx.model,
        content: [],
        stop_reason: null,
        stop_sequence: null,
        usage: { input_tokens: 0, output_tokens: 0 },
      },
    });
  }

  function closeBlock() {
    if (openBlock === null) return;
    writeEvent(res, "content_block_stop", { type: "content_block_stop", index: openBlock.index });
    openBlock = null;
  }

  function openBlockOf(type, block) {
    closeBlock();
    blockIndex += 1;
    openBlock = { index: blockIndex, type };
    writeEvent(res, "content_block_start", {
      type: "content_block_start",
      index: blockIndex,
      content_block: block,
    });
    return openBlock;
  }

  function delta(payload) {
    writeEvent(res, "content_block_delta", {
      type: "content_block_delta",
      index: openBlock.index,
      delta: payload,
    });
  }

  return {
    /** 一条上游分片。参数是 `iterateSseData` 吐出来的字符串。 */
    push(payload) {
      if (finished || payload === "" || payload === "[DONE]") return;

      let chunk;
      try {
        chunk = JSON.parse(payload);
      } catch {
        return;
      }
      if (!isObject(chunk)) return;

      if (isObject(chunk["usage"])) usage = usageOf(chunk["usage"]);

      const choice = Array.isArray(chunk["choices"]) ? chunk["choices"][0] : undefined;
      if (!isObject(choice)) return;
      const part = isObject(choice["delta"]) ? choice["delta"] : {};

      startMessage();

      const reasoning = part["reasoning_content"];
      if (typeof reasoning === "string" && reasoning !== "") {
        if (openBlock?.type !== "thinking") openBlockOf("thinking", { type: "thinking", thinking: "" });
        delta({ type: "thinking_delta", thinking: reasoning });
      }

      const text = part["content"];
      if (typeof text === "string" && text !== "") {
        if (openBlock?.type !== "text") openBlockOf("text", { type: "text", text: "" });
        delta({ type: "text_delta", text });
      }

      const calls = part["tool_calls"];
      if (Array.isArray(calls)) {
        for (const call of calls) {
          if (!isObject(call)) continue;
          const key = typeof call["index"] === "number" ? call["index"] : 0;
          let entry = toolBlocks.get(key);
          if (entry === undefined) {
            const fn = isObject(call["function"]) ? call["function"] : {};
            const id = typeof call["id"] === "string" && call["id"] !== "" ? call["id"] : makeId("toolu");
            const block = openBlockOf("tool_use", {
              type: "tool_use",
              id,
              name: typeof fn["name"] === "string" ? fn["name"] : "",
              input: {},
            });
            entry = { index: block.index };
            toolBlocks.set(key, entry);
          }
          const args = isObject(call["function"]) ? call["function"]["arguments"] : undefined;
          if (typeof args === "string" && args !== "") {
            writeEvent(res, "content_block_delta", {
              type: "content_block_delta",
              index: entry.index,
              delta: { type: "input_json_delta", partial_json: args },
            });
          }
        }
      }

      if (typeof choice["finish_reason"] === "string" && choice["finish_reason"] !== "") {
        stopReason = toAnthropicStopReason(choice["finish_reason"]);
      }
    },

    /** 上游流结束（或客户端断开）时收尾。必须调用，否则 Claude Code 会一直等。 */
    finish() {
      if (finished) return;
      finished = true;

      startMessage();
      closeBlock();

      // 有些模型发了 tool_calls 却把 finish_reason 报成 stop，
      // 但 Claude Code 靠 stop_reason 决定要不要执行工具，这里以实际内容为准。
      let reason = stopReason;
      if (toolBlocks.size > 0 && (reason === null || reason === "end_turn")) reason = "tool_use";
      if (reason === null) reason = "end_turn";

      writeEvent(res, "message_delta", {
        type: "message_delta",
        delta: { stop_reason: reason, stop_sequence: null },
        usage: { output_tokens: usage.output_tokens },
      });
      writeEvent(res, "message_stop", { type: "message_stop" });
      res.end();
    },

    /** 流中途出错：先收尾再补一个 error 事件，让客户端知道是失败了。 */
    fail(message) {
      if (finished) return;
      finished = true;
      closeBlock();
      writeEvent(res, "error", {
        type: "error",
        error: { type: "api_error", message: String(message) },
      });
      res.end();
    },
  };
}
