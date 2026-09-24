/**
 * OpenAI Responses API ←→ OpenAI Chat Completions 的翻译层。
 *
 * 为什么要这个：Codex CLI 的 `model_providers.<x>.wire_api` 有两种取值，
 * 其中 `"responses"` 走的是 Responses API（`POST /v1/responses`），
 * 和我们网关原本只提供的 Chat Completions 不是一回事。
 *
 * 翻译分三段：
 *   1. `responsesToChat`        请求：instructions / input 数组 / 扁平 tools
 *   2. `createResponsesStream`  流式响应：response.created → output_item.* → response.completed
 *   3. `fromCompletion`         非流式响应：一个完整的 `object: "response"` 文档
 *
 * 和 Anthropic 那边一样，难点在 output item 的「开-写-关」顺序：
 * Codex 会按 output_index 逐个接收，index 必须连续、且上一项关掉才能开下一项。
 *
 * @module workbuddy-api/responses
 */

import {
  beginSse,
  makeId,
  usageOf,
  writeEvent,
} from "./protocol.js";

function isObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Responses 的 content part → Chat 的 content part。 */
function toChatPart(part) {
  if (!isObject(part)) return undefined;
  switch (part["type"]) {
    case "input_text":
    case "output_text":
    case "text":
      return { type: "text", text: typeof part["text"] === "string" ? part["text"] : "" };
    case "input_image": {
      const url = part["image_url"];
      if (typeof url === "string" && url !== "") return { type: "image_url", image_url: { url } };
      if (typeof part["file_id"] === "string" && part["file_id"] !== "") {
        // 上游拿不到上传的文件，占个位比整条消息丢掉强
        return { type: "text", text: `[image:${part["file_id"]}]` };
      }
      return undefined;
    }
    default:
      return undefined;
  }
}

/** `function_call_output` 的 output 可以是字符串，也可能是个对象。 */
function outputText(output) {
  if (typeof output === "string") return output;
  if (Array.isArray(output)) {
    return output
      .map((part) => (isObject(part) && typeof part["text"] === "string" ? part["text"] : ""))
      .join("\n");
  }
  if (output === undefined || output === null) return "";
  return JSON.stringify(output);
}

function toChatToolChoice(choice) {
  if (typeof choice === "string") return choice;
  if (!isObject(choice)) return undefined;
  if (choice["type"] === "function" && typeof choice["name"] === "string") {
    return { type: "function", function: { name: choice["name"] } };
  }
  return undefined;
}

/** Responses 的工具是扁平的（name 在顶层），Chat 的嵌在 function 里。 */
function toChatTools(tools) {
  if (!Array.isArray(tools)) return undefined;
  const converted = [];
  for (const tool of tools) {
    if (!isObject(tool) || tool["type"] !== "function") continue;
    const name = typeof tool["name"] === "string" ? tool["name"].trim() : "";
    if (name === "") continue;
    converted.push({
      type: "function",
      function: {
        name,
        description: typeof tool["description"] === "string" ? tool["description"] : "",
        parameters: isObject(tool["parameters"]) ? tool["parameters"] : { type: "object", properties: {} },
      },
    });
  }
  return converted.length > 0 ? converted : undefined;
}

/**
 * 把 Responses 请求体翻成 Chat 请求体。
 *
 * @param {object} body Responses `/v1/responses` 的请求体
 * @param {{model: string}} ctx
 */
export function responsesToChat(body, ctx) {
  const messages = [];

  const instructions = body["instructions"];
  if (typeof instructions === "string" && instructions.trim() !== "") {
    messages.push({ role: "system", content: instructions });
  }

  const input = body["input"];

  if (typeof input === "string") {
    messages.push({ role: "user", content: input });
  } else if (Array.isArray(input)) {
    for (const item of input) {
      if (!isObject(item)) continue;

      switch (item["type"]) {
        case "message": {
          const rawRole = typeof item["role"] === "string" ? item["role"] : "user";
          const role = rawRole === "developer" ? "system" : rawRole;
          const parts = (Array.isArray(item["content"]) ? item["content"] : [])
            .map(toChatPart)
            .filter((part) => part !== undefined);

          if (parts.length === 1 && parts[0]["type"] === "text") {
            messages.push({ role, content: parts[0]["text"] });
          } else if (parts.length > 0) {
            messages.push({ role, content: parts });
          } else if (typeof item["content"] === "string") {
            messages.push({ role, content: item["content"] });
          }
          break;
        }

        case "function_call": {
          // 上一条 assistant 的并行工具调用要并到同一条消息里
          const call = {
            id: typeof item["call_id"] === "string" && item["call_id"] !== "" ? item["call_id"] : makeId("call"),
            type: "function",
            function: {
              name: typeof item["name"] === "string" ? item["name"] : "",
              arguments: typeof item["arguments"] === "string" ? item["arguments"] : "",
            },
          };
          const last = messages[messages.length - 1];
          if (isObject(last) && last["role"] === "assistant" && Array.isArray(last["tool_calls"])) {
            last["tool_calls"].push(call);
          } else {
            messages.push({ role: "assistant", content: "", tool_calls: [call] });
          }
          break;
        }

        case "function_call_output": {
          messages.push({
            role: "tool",
            tool_call_id: typeof item["call_id"] === "string" ? item["call_id"] : "",
            content: outputText(item["output"]),
          });
          break;
        }

        case "reasoning":
        default:
          // reasoning / 未知类型：上游不认，丢掉
          break;
      }
    }
  }

  const chat = { model: ctx.model, messages, stream: body["stream"] === true };

  if (Number.isFinite(body["max_output_tokens"])) chat["max_tokens"] = body["max_output_tokens"];
  if (Number.isFinite(body["temperature"])) chat["temperature"] = body["temperature"];
  if (Number.isFinite(body["top_p"])) chat["top_p"] = body["top_p"];

  const tools = toChatTools(body["tools"]);
  if (tools !== undefined) chat["tools"] = tools;

  const toolChoice = toChatToolChoice(body["tool_choice"]);
  if (toolChoice !== undefined) chat["tool_choice"] = toolChoice;

  return chat;
}

/** Responses 用的是 OpenAI 风格的错误信封，`type` 字段放类别。 */
export function responsesError(status, message, type = "api_error") {
  return { status, body: { error: { message, type, code: type, param: null } } };
}

/** 把上游错误类别翻成给 Codex 看的类型。 */
export function responsesErrorTypeOf(kind) {
  switch (kind) {
    case "not_signed_in":
    case "session_dead":
      return "authentication_error";
    case "hard_credit":
      return "insufficient_quota";
    case "soft_rate":
      return "rate_limit_exceeded";
    case "client":
      return "invalid_request_error";
    default:
      return "server_error";
  }
}

/** Chat 的 tool_calls / content → Responses 的 output item 数组。 */
function outputItemsFromMessage(message) {
  const items = [];

  const reasoning = message["reasoning_content"];
  if (typeof reasoning === "string" && reasoning !== "") {
    items.push({
      type: "reasoning",
      id: makeId("rs"),
      summary: [{ type: "summary_text", text: reasoning }],
    });
  }

  const text = message["content"];
  if (typeof text === "string" && text !== "") {
    items.push({
      type: "message",
      id: makeId("msg"),
      status: "completed",
      role: "assistant",
      content: [{ type: "output_text", text, annotations: [] }],
    });
  }

  const calls = Array.isArray(message["tool_calls"]) ? message["tool_calls"] : [];
  for (const call of calls) {
    items.push({
      type: "function_call",
      id: makeId("fc"),
      call_id: typeof call["id"] === "string" && call["id"] !== "" ? call["id"] : makeId("call"),
      name: typeof call?.["function"]?.["name"] === "string" ? call["function"]["name"] : "",
      arguments: typeof call?.["function"]?.["arguments"] === "string" ? call["function"]["arguments"] : "",
      status: "completed",
    });
  }

  if (items.length === 0) {
    items.push({
      type: "message",
      id: makeId("msg"),
      status: "completed",
      role: "assistant",
      content: [{ type: "output_text", text: "", annotations: [] }],
    });
  }

  return items;
}

function responseShell(ctx, { id, createdAt, status, output, usage, tools, toolChoice, incompleteReason }) {
  const shell = {
    id,
    object: "response",
    created_at: createdAt,
    status,
    model: ctx.model,
    output,
    parallel_tool_calls: true,
    tool_choice: toolChoice ?? "auto",
    tools: tools ?? [],
    usage: usage ?? null,
    error: null,
    incomplete_details: incompleteReason === null || incompleteReason === undefined
      ? null
      : { reason: incompleteReason },
  };
  return shell;
}

/** 非流式响应：Chat Completions → 一个完整的 `response` 文档。 */
export function fromCompletion(completion, ctx) {
  const choice = Array.isArray(completion["choices"]) ? completion["choices"][0] : undefined;
  const message = isObject(choice?.["message"]) ? choice["message"] : {};
  const usage = usageOf(completion["usage"]);
  const finish = choice?.["finish_reason"];
  const incomplete = finish === "length" ? "max_output_tokens" : null;

  return responseShell(ctx, {
    id: makeId("resp"),
    createdAt: Math.floor(Date.now() / 1000),
    status: incomplete === null ? "completed" : "incomplete",
    output: outputItemsFromMessage(message),
    usage: {
      input_tokens: usage.input_tokens,
      output_tokens: usage.output_tokens,
      total_tokens: usage.total_tokens,
      input_tokens_details: { cached_tokens: 0 },
      output_tokens_details: { reasoning_tokens: 0 },
    },
    tools: ctx.tools,
    toolChoice: ctx.toolChoice,
    incompleteReason: incomplete,
  });
}

/**
 * 流式响应：把 Chat 的分片拼成 Responses 的事件流。
 *
 * 事件顺序（Codex 按这个解析）：
 *   response.created
 *   response.in_progress
 *   response.output_item.added        ← 开一项
 *   response.output_text.delta …      ← 文本项的内容
 *   response.output_item.done         ← 关这一项
 *   …
 *   response.completed                ← 带上完整的 output 和 usage
 */
export function createResponsesStream(res, ctx) {
  const responseId = makeId("resp");
  const createdAt = Math.floor(Date.now() / 1000);

  const items = [];
  const toolItems = new Map();
  let sequenceNumber = 0;
  let started = false;
  let finished = false;
  let open = null;
  let usage = { input_tokens: 0, output_tokens: 0 };
  let incompleteReason = null;

  function send(name, payload) {
    writeEvent(res, name, { ...payload, sequence_number: sequenceNumber++ });
  }

  function shell(status, output) {
    return responseShell(ctx, {
      id: responseId,
      createdAt,
      status,
      output,
      usage: status === "in_progress"
        ? null
        : {
            input_tokens: usage.input_tokens,
            output_tokens: usage.output_tokens,
            total_tokens: usage.input_tokens + usage.output_tokens,
            input_tokens_details: { cached_tokens: 0 },
            output_tokens_details: { reasoning_tokens: 0 },
          },
      tools: ctx.tools,
      toolChoice: ctx.toolChoice,
      incompleteReason: status === "in_progress" ? null : incompleteReason,
    });
  }

  function ensureStarted() {
    if (started) return;
    started = true;
    send("response.created", { type: "response.created", response: shell("in_progress", []) });
    send("response.in_progress", { type: "response.in_progress", response: shell("in_progress", []) });
  }

  /** 关掉当前打开的 output item（如果开着）。 */
  function closeOpen() {
    if (open === null) return;

    if (open.kind === "message") {
      send("response.output_text.done", {
        type: "response.output_text.done",
        item_id: open.item.id,
        output_index: open.outputIndex,
        content_index: 0,
        text: open.text,
      });
      send("response.content_part.done", {
        type: "response.content_part.done",
        item_id: open.item.id,
        output_index: open.outputIndex,
        content_index: 0,
        part: { type: "output_text", text: open.text, annotations: [] },
      });
      open.item.status = "completed";
      send("response.output_item.done", {
        type: "response.output_item.done",
        output_index: open.outputIndex,
        item: open.item,
      });
    } else {
      open.item.arguments = open.arguments;
      open.item.status = "completed";
      send("response.function_call_arguments.done", {
        type: "response.function_call_arguments.done",
        item_id: open.item.id,
        output_index: open.outputIndex,
        arguments: open.arguments,
      });
      send("response.output_item.done", {
        type: "response.output_item.done",
        output_index: open.outputIndex,
        item: open.item,
      });
    }

    open = null;
  }

  function openMessage() {
    closeOpen();
    const outputIndex = items.length;
    const item = {
      type: "message",
      id: makeId("msg"),
      status: "in_progress",
      role: "assistant",
      content: [{ type: "output_text", text: "", annotations: [] }],
    };
    items.push(item);
    open = { kind: "message", outputIndex, item, text: "" };

    send("response.output_item.added", {
      type: "response.output_item.added",
      output_index: outputIndex,
      item,
    });
    send("response.content_part.added", {
      type: "response.content_part.added",
      item_id: item.id,
      output_index: outputIndex,
      content_index: 0,
      part: { type: "output_text", text: "", annotations: [] },
    });
  }

  function openFunctionCall(name) {
    closeOpen();
    const outputIndex = items.length;
    const item = {
      type: "function_call",
      id: makeId("fc"),
      call_id: makeId("call"),
      name,
      arguments: "",
      status: "in_progress",
    };
    items.push(item);
    open = { kind: "function_call", outputIndex, item, arguments: "" };

    send("response.output_item.added", {
      type: "response.output_item.added",
      output_index: outputIndex,
      item,
    });
  }

  return {
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

      ensureStarted();

      const text = part["content"];
      if (typeof text === "string" && text !== "") {
        if (open?.kind !== "message") openMessage();
        open.text += text;
        open.item.content[0]["text"] = open.text;
        send("response.output_text.delta", {
          type: "response.output_text.delta",
          item_id: open.item.id,
          output_index: open.outputIndex,
          content_index: 0,
          delta: text,
        });
      }

      const calls = part["tool_calls"];
      if (Array.isArray(calls)) {
        for (const call of calls) {
          if (!isObject(call)) continue;
          const key = typeof call["index"] === "number" ? call["index"] : 0;
          let entry = toolItems.get(key);
          if (entry === undefined) {
            const fn = isObject(call["function"]) ? call["function"] : {};
            openFunctionCall(typeof fn["name"] === "string" ? fn["name"] : "");
            entry = open;
            if (typeof call["id"] === "string" && call["id"] !== "") entry.item.call_id = call["id"];
            toolItems.set(key, entry);
          }
          const args = isObject(call["function"]) ? call["function"]["arguments"] : undefined;
          if (typeof args === "string" && args !== "") {
            entry.arguments += args;
            entry.item.arguments = entry.arguments;
            send("response.function_call_arguments.delta", {
              type: "response.function_call_arguments.delta",
              item_id: entry.item.id,
              output_index: entry.outputIndex,
              delta: args,
            });
          }
        }
      }

      if (choice["finish_reason"] === "length") incompleteReason = "max_output_tokens";
    },

    finish() {
      if (finished) return;
      finished = true;

      ensureStarted();
      closeOpen();

      if (items.length === 0) {
        openMessage();
        closeOpen();
      }

      const status = incompleteReason === null ? "completed" : "incomplete";
      send("response.completed", {
        type: status === "completed" ? "response.completed" : "response.incomplete",
        response: shell(status, items),
      });
      res.end();
    },

    fail(message) {
      if (finished) return;
      finished = true;
      closeOpen();
      send("response.failed", {
        type: "response.failed",
        response: {
          ...shell("failed", items),
          error: { code: "server_error", message: String(message) },
        },
      });
      res.end();
    },
  };
}
