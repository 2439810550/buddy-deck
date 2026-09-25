/**
 * Local OpenAI-compatible gateway.
 *
 * Exposes WorkBuddy models over the OpenAI HTTP contract so any CLI, editor or
 * SDK that accepts a `base_url` can use them:
 *
 *   GET  /healthz            liveness, no auth
 *   GET  /v1/models          the model roster
 *   POST /v1/chat/completions  chat, streaming or whole-answer
 *
 * The upstream is streaming-only and rejects `role: "developer"` and object
 * `tool_choice`, so this gateway normalizes every request (see `upstream.js`)
 * and, when a caller asks for `stream: false`, re-assembles the SSE response
 * into a single `chat.completion` document.
 *
 * Security posture: binds loopback only. When an API key is configured (or
 * auto-generated) it is required as `Authorization: Bearer …`; without one the
 * port is an open local endpoint, which is why the default bind is 127.0.0.1
 * and non-loopback binds demand an explicit key.
 *
 * @module workbuddy-api/gateway
 */

import { createServer } from "node:http";
import { timingSafeEqual } from "node:crypto";
import {
  classifyUpstreamError,
  collectChatCompletion,
  iterateSseData,
  prepareBodyFor,
} from "./upstream.js";
import { beginSse, createModelResolver } from "./protocol.js";
import { RateLimitTracker, describeRateLimit, isRateLimited, pickFallbackModel } from "./ratelimit.js";
import * as anthropic from "./anthropic.js";
import * as responses from "./responses.js";
import { recordUsage } from "./usage.js";

const MAX_BODY_BYTES = 64 * 1024 * 1024;

/** HTTP status each upstream failure class surfaces as. */
const KIND_STATUS = {
  hard_credit: 402,
  soft_rate: 429,
  session_dead: 401,
  not_found: 502,
  server: 502,
  client: 400,
};

const LOOPBACK_HOSTS = new Set(["127.0.0.1", "localhost", "[::1]"]);

/**
 * 非 OpenAI 协议的适配层。
 *
 * 每个适配层都知道怎么把「客户端说的话」翻成 Chat Completions，
 * 再把它回给客户端的形状翻回来。translated() 只负责转发，不关心具体是哪种协议。
 */
const ANTHROPIC_ADAPTER = {
  name: "anthropic",
  toChat: (body, ctx) => anthropic.anthropicToChat(body, ctx),
  fromCompletion: (completion, ctx) => anthropic.fromCompletion(completion, ctx),
  createStream: (res, ctx) => anthropic.createAnthropicStream(res, ctx),
  error: (status, kind, message) => anthropic.anthropicError(status, message, anthropic.anthropicErrorTypeOf(kind)),
};

const RESPONSES_ADAPTER = {
  name: "responses",
  toChat: (body, ctx) => responses.responsesToChat(body, ctx),
  fromCompletion: (completion, ctx) => responses.fromCompletion(completion, ctx),
  createStream: (res, ctx) => responses.createResponsesStream(res, ctx),
  error: (status, kind, message) => responses.responsesError(status, message, responses.responsesErrorTypeOf(kind)),
};

/** Strip the optional :port from a Host header value, IPv6-bracket aware. */
function hostnameOfHost(host) {
  let hostname = host.trim().toLowerCase();
  if (hostname.startsWith("[")) {
    const end = hostname.indexOf("]");
    return end === -1 ? hostname : hostname.slice(0, end + 1);
  }
  const colon = hostname.lastIndexOf(":");
  if (colon !== -1 && !hostname.slice(0, colon).includes(":") && /^\d+$/u.test(hostname.slice(colon + 1))) {
    hostname = hostname.slice(0, colon);
  }
  return hostname;
}

function writeJson(res, status, body) {
  const payload = JSON.stringify(body);
  res.writeHead(status, {
    "Content-Type": "application/json",
    "Content-Length": Buffer.byteLength(payload),
  });
  res.end(payload);
}

function writeOpenAIError(res, status, kind, message) {
  writeJson(res, status, { error: { message, type: kind, code: kind } });
}

/** Read a request body with a size cap. */
function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on("data", (chunk) => {
      size += chunk.length;
      if (size > MAX_BODY_BYTES) {
        reject(new Error("request body too large"));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on("end", () => resolve(Buffer.concat(chunks)));
    req.on("error", reject);
  });
}

/**
 * Constant-time bearer comparison.
 *
 * @returns {boolean} whether the presented key matches the expected one.
 */
function keyMatches(expected, presented) {
  const a = Buffer.from(presented);
  const b = Buffer.from(expected);
  if (a.length !== b.length) return false;
  return timingSafeEqual(a, b);
}

/**
 * 把一段 SSE 分片里的 model 字段换成实际用的那个。
 *
 * 换过模型重试时，让客户端看到的模型名和最终生效的一致，不然对不上。
 * 解析失败就原样返回 —— 绝不因为改个名字把流转坏。
 */
function renameChunkModel(payload, model) {
  if (typeof model !== "string" || model === "") return payload;
  try {
    const chunk = JSON.parse(payload);
    if (typeof chunk !== "object" || chunk === null || Array.isArray(chunk)) return payload;
    if (typeof chunk["model"] === "string" && chunk["model"] !== model) {
      chunk["model"] = model;
      return JSON.stringify(chunk);
    }
    return payload;
  } catch {
    return payload;
  }
}

/** The bearer token from a request's Authorization header, if present. */
function bearerOf(req) {
  const header = req.headers.authorization;
  if (typeof header !== "string") return undefined;
  const match = /^Bearer\s+(.+)$/iu.exec(header.trim());
  return match === null ? undefined : match[1];
}

/**
 * Reshape one upstream SSE chunk so strict OpenAI clients accept it.
 *
 * Two upstream habits break strict parsers: `delta.function_call` is present
 * but empty on every chunk (and `"function_call":null` on some), and the
 * terminal usage block carries vendor-only counters. Both are cosmetic here
 * and are dropped, while `reasoning_content` is preserved because several
 * clients render it.
 *
 * @returns {string|undefined} the payload to forward, or undefined to drop it.
 */
function sanitizeChunk(payload) {
  if (payload === "" ) return undefined;
  let chunk;
  try {
    chunk = JSON.parse(payload);
  } catch {
    return undefined;
  }
  if (typeof chunk !== "object" || chunk === null || Array.isArray(chunk)) return undefined;
  const choices = chunk["choices"];
  if (Array.isArray(choices)) {
    for (const choice of choices) {
      if (typeof choice !== "object" || choice === null || Array.isArray(choice)) continue;
      const delta = choice["delta"];
      if (typeof delta !== "object" || delta === null || Array.isArray(delta)) continue;
      const functionCall = delta["function_call"];
      const isEmptyFunctionCall =
        functionCall === null ||
        (typeof functionCall === "object" &&
          functionCall !== null &&
          !Array.isArray(functionCall) &&
          (functionCall["name"] ?? "") === "" &&
          (functionCall["arguments"] ?? "") === "");
      // An empty function_call alongside a real tool_calls delta makes some
      // clients emit a phantom parallel call; a populated one is legacy and is
      // kept verbatim.
      if (isEmptyFunctionCall) delete delta["function_call"];
      if (delta["reasoning_content"] === "") delete delta["reasoning_content"];
      if (delta["refusal"] === "") delete delta["refusal"];
      if (delta["extra_fields"] === null) delete delta["extra_fields"];
    }
  }
  const usage = chunk["usage"];
  if (typeof usage === "object" && usage !== null && !Array.isArray(usage)) {
    // 记账要用的字段先**拷一份**。
    //
    // ⚠ 必须是拷贝，不能直接 lastUsage = usage ——
    // 下面那个 delete 循环会把 credit / 缓存字段从同一个对象上删掉，
    // 连带把账本里的 credit 也弄丢了。（踩过：账本只有 token 没有 credit）
    lastUsage = { ...usage };
    const keep = ["prompt_tokens", "completion_tokens", "total_tokens"];
    for (const key of Object.keys(usage)) if (!keep.includes(key)) delete usage[key];
  }
  return JSON.stringify(chunk);
}

/**
 * 当前请求的 usage 快照。
 *
 * 为什么用模块级变量：sanitizeChunk 是纯函数，改成带状态会污染它的语义。
 * 而这个网关是**单进程单请求串行处理流**的（SSE 是一条一条推的），
 * 同一时刻只有一个流在写，所以这个变量在「读取时刻」是对的。
 * 读取发生在流结束时，紧接着就清空，不会串到下一个请求。
 */
let lastUsage = null;

/** 从一个 SSE 分片里把 usage 抠出来（不改动分片本身）。 */
function captureUsage(payload) {
  if (typeof payload !== "string") return;
  // 快筛：没有 usage 的分片直接跳过，避免每个 token 都 JSON.parse
  if (!payload.includes('"usage"')) return;
  try {
    const chunk = JSON.parse(payload);
    const usage = chunk?.["usage"];
    if (typeof usage === "object" && usage !== null) lastUsage = { ...usage };
  } catch {
    // 分片不是完整 JSON 就跳过
  }
}

/**
 * 账号标记 —— 记账里用来区分是哪个号。
 *
 * 只取 uid 前 8 位：既能区分账号，又不会把完整凭据标识写进账本。
 */
function accountTag(credential) {
  const raw = credential?.["uid"] ?? credential?.["id"] ?? "";
  return typeof raw === "string" && raw.length >= 8 ? raw.slice(0, 8) : "";
}

/**
 * Read and validate a JSON request body.
 *
 * @returns {Promise<{ok: true, request: object} | {ok: false, status: number, kind: string, message: string}>}
 */
async function readJsonBody(req) {
  const contentType = req.headers["content-type"];
  if (typeof contentType !== "string" || !contentType.trim().toLowerCase().startsWith("application/json")) {
    return { ok: false, status: 415, kind: "unsupported_media_type", message: "Content-Type must be application/json" };
  }

  const raw = (await readBody(req)).toString("utf8");
  let request;
  try {
    request = JSON.parse(raw);
  } catch {
    return { ok: false, status: 400, kind: "invalid_request_error", message: "request body is not valid JSON" };
  }

  if (typeof request !== "object" || request === null || Array.isArray(request)) {
    return { ok: false, status: 400, kind: "invalid_request_error", message: "request body must be a JSON object" };
  }

  return { ok: true, request };
}

/**
 * Create the gateway server.
 *
 * @param {object} options
 * @param {import("./upstream.js").WorkBuddyClient} options.client upstream client
 * @param {import("./auth.js").CredentialStore} options.store credential store
 * @param {import("./catalog.js").Catalog} options.catalog catalog
 * @param {object} options.variant variant descriptor
 * @param {string} [options.apiKey] required bearer; empty means no auth
 * @param {string} [options.defaultModel] model used when a client asks for one we don't have
 * @param {(...args: any[]) => void} [options.log]
 * @returns {{server: import("node:http").Server, listen: (port: number, host: string) => Promise<{port: number, host: string}>, close: () => Promise<void>}}
 */
export function createGateway(options) {
  const { client, store, catalog, variant } = options;
  const apiKey = options.apiKey ?? "";
  const log = options.log ?? (() => {});
  const resolveModel = createModelResolver({
    catalog,
    defaultModel: options.defaultModel,
    log,
  });

  // 哪个「账号+模型」被限流到什么时候。进程内共用一份。
  //
  // 为什么要有这个：上游 429 的原文是「使用量已超出频率限制…您也可以切换其他模型
  // 继续使用」—— 限制是按账号+模型算的。所以被拦之后：
  //   1. 换一个模型接着发（用户完全无感）
  //   2. 把被拦的模型记进冷却表，窗口内不再拿它白试
  const rateLimits = new RateLimitTracker();

  /**
   * 发一次上游请求；被限流就**换模型重试**。
   *
   * 为什么能安全重试：`chatStream` 是在响应头阶段就判定失败的（还没开始吐内容），
   * 所以重试不会产生半截输出。
   *
   * @param {object} params
   * @param {object} params.credential
   * @param {object} params.request 已经解析好的、面向适配层的请求体
   * @param {string} params.model 本次用的模型
   * @param {AbortSignal} params.signal
   * @param {(body: object) => string} params.prepare 把请求体变成上游要的形状
   * @param {string} params.tag 日志前缀
   * @returns {Promise<{result: object, model: string, attempts: string[]}>}
   */
  async function sendWithFallback({ credential, request, model, signal, prepare, tag }) {
    const account = `${credential.uid}:${credential.enterpriseId ?? ""}`;
    const attempts = [];
    let current = model;
    let result;

    // 最多试 4 次（首个 + 3 个备用）。再多了就是白试，该让用户看见错误。
    for (let attempt = 0; attempt < 4; attempt += 1) {
      if (rateLimits.isCooling(account, current)) {
        // 这个模型还在冷却里，直接跳过，连请求都不发
        log(`${tag} 跳过 ${current}（冷却中）`);
        attempts.push(`${current}:冷却中`);
      } else {
        result = await client.chatStream(credential, prepare(request), signal);
        if (result.ok) return { result, model: current, attempts };
        if (!isRateLimited(result)) return { result, model: current, attempts };

        const info = describeRateLimit(result);
        const until = rateLimits.mark(account, current, { resetAt: info.resetAt, text: result.message });
        log(`${tag} ${current} 被限流，${new Date(until).toLocaleString()} 前不再使用`);
        attempts.push(`${current}:限流`);
      }

      const next = pickFallbackModel({
        models: catalog.current(),
        exclude: current,
        cooling: rateLimits.coolingModels(account),
      });
      if (next === undefined || next === current) {
        // 没有可用的备用模型了，把最后一次的真实错误交出去
        return { result: result ?? { ok: false, status: 429, kind: "soft_rate", message: "所有可用模型都在限流冷却中" }, model: current, attempts };
      }
      log(`${tag} 换成 ${next} 重试`);
      current = next;
    }

    return { result, model: current, attempts };
  }

  const server = createServer((req, res) => {
    handle(req, res).catch((error) => {
      if (!res.headersSent) writeOpenAIError(res, 500, "internal", String(error));
      else res.end();
    });
  });

  async function handle(req, res) {
    const url = (req.url ?? "/").split("?")[0];

    if (req.method === "GET" && (url === "/healthz" || url === "/healthz/")) {
      writeJson(res, 200, { ok: true, provider: variant.id, models: catalog.base().length });
      return;
    }

    // A non-loopback Host means a DNS-rebinding page or a remote caller: the
    // bearer alone is not enough for a local trust boundary.
    const host = req.headers.host;
    if (host === undefined || !LOOPBACK_HOSTS.has(hostnameOfHost(host))) {
      writeOpenAIError(res, 403, "host_not_allowed", "Host header must name the loopback interface");
      return;
    }

    if (apiKey !== "") {
      const presented = bearerOf(req);
      if (presented === undefined || !keyMatches(apiKey, presented)) {
        res.setHeader("WWW-Authenticate", 'Bearer realm="workbuddy-api"');
        writeOpenAIError(res, 401, "invalid_api_key", "missing or invalid API key");
        return;
      }
    }

    if (req.method === "GET" && (url === "/v1/models" || url === "/v1/models/")) {
      writeJson(res, 200, {
        object: "list",
        data: catalog.current().map((model) => ({
          id: model.id,
          object: "model",
          created: 0,
          owned_by: variant.id,
        })),
      });
      return;
    }

    if (req.method === "POST" && (url === "/v1/chat/completions" || url === "/v1/chat/completions/")) {
      await chatCompletions(req, res);
      return;
    }

    // Anthropic Messages —— Claude Code / CC Switch
    if (req.method === "POST" && (url === "/v1/messages" || url === "/v1/messages/")) {
      await translated(req, res, ANTHROPIC_ADAPTER);
      return;
    }

    // Claude Code 上下文管理会用；我们没上游分词器，只能粗估
    if (req.method === "POST" && url.startsWith("/v1/messages/count_tokens")) {
      const parsed = await readJsonBody(req);
      if (!parsed.ok) {
        const { status, body } = ANTHROPIC_ADAPTER.error(parsed.status, parsed.kind, parsed.message);
        writeJson(res, status, body);
        return;
      }
      writeJson(res, 200, { input_tokens: anthropic.countInputTokens(parsed.request) });
      return;
    }

    // OpenAI Responses —— Codex（wire_api = "responses"）
    if (req.method === "POST" && (url === "/v1/responses" || url === "/v1/responses/")) {
      await translated(req, res, RESPONSES_ADAPTER);
      return;
    }

    writeOpenAIError(res, 404, "not_found", `no such route: ${req.method} ${url}`);
  }

  /**
   * 走一轮「翻译 → 上游 → 翻译回来」。Anthropic 和 Responses 共用这一条路径。
   *
   * @param {import("node:http").IncomingMessage} req
   * @param {import("node:http").ServerResponse} res
   * @param {{name: string, toChat: Function, fromCompletion: Function, createStream: Function, error: Function}} adapter
   */
  async function translated(req, res, adapter) {
    const parsed = await readJsonBody(req);
    if (!parsed.ok) {
      const shaped = adapter.error(parsed.status, parsed.kind, parsed.message);
      writeJson(res, shaped.status, shaped.body);
      return;
    }

    let credential;
    try {
      credential = await store.resolve();
    } catch (error) {
      const shaped = adapter.error(401, "not_signed_in", String(error));
      writeJson(res, shaped.status, shaped.body);
      return;
    }

    const request = parsed.request;
    const startedAt = Date.now();
    const resolved = resolveModel(request["model"]);
    const ctx = {
      model: resolved.model,
      requestedModel: resolved.requested ?? request["model"],
      // 回给客户端时保留 tools/tool_choice 的原形，Codex 会核对
      tools: Array.isArray(request["tools"]) ? request["tools"] : [],
      toolChoice: request["tool_choice"] ?? "auto",
    };

    const wantsStream = request["stream"] === true;

    const controller = new AbortController();
    req.on("close", () => controller.abort());

    // 带换模型重试的发送：某个模型被限流时会自动换下一个，用户无感。
    const { result, model: usedModel, attempts } = await sendWithFallback({
      credential,
      request,
      model: ctx.model,
      signal: controller.signal,
      prepare: (body) => prepareBodyFor(variant, adapter.toChat(body, ctx)),
      tag: `[${adapter.name}]`,
    });

    if (!result.ok) {
      log(`[${adapter.name}] upstream ${result.kind} (http ${result.status}) 试过：${attempts.join(" → ") || "-"}`);
      const extra = attempts.length > 1 ? `（已试过 ${attempts.join(" → ")}）` : "";
      const shaped = adapter.error(
        KIND_STATUS[result.kind] ?? 502,
        result.kind,
        `workbuddy upstream ${result.kind} (http ${result.status}): ${result.message.slice(0, 400)}${extra}`,
      );
      writeJson(res, shaped.status, shaped.body);
      return;
    }

    // 回给客户端的模型名要用**实际用的那个** —— 换过模型的话，
    // 不然日志和界面对不上，排查时会很迷惑。
    ctx.model = usedModel;

    if (!wantsStream) {
      const completion = await collectChatCompletion(result.response);
      writeJson(res, 200, adapter.fromCompletion(completion, ctx));
      void recordUsage({
        model: ctx.requestedModel, usedModel, kind: adapter.name, account: accountTag(credential),
        usage: completion?.["usage"], outcome: "ok", attempts: attempts.length,
        elapsedMs: Date.now() - startedAt,
      });
      return;
    }

    beginSse(res);
    const writer = adapter.createStream(res, ctx);
    try {
      for await (const payload of iterateSseData(result.response.body)) {
        if (payload === "[DONE]") break;
        // 记账要用的 usage（含 credit）在 rename 之前先抠出来
        captureUsage(payload);
        writer.push(renameChunkModel(payload, usedModel));
      }
      writer.finish();
      void recordUsage({
        model: ctx.requestedModel, usedModel, kind: adapter.name, account: accountTag(credential),
        usage: lastUsage, outcome: "ok", attempts: attempts.length,
        elapsedMs: Date.now() - startedAt,
      });
      lastUsage = null;
    } catch (error) {
      log(`[${adapter.name}] stream failed mid-flight: ${String(error)}`);
      writer.fail(error);
    }
  }

  async function chatCompletions(req, res) {
    const parsed = await readJsonBody(req);
    if (!parsed.ok) {
      writeOpenAIError(res, parsed.status, parsed.kind, parsed.message);
      return;
    }
    const request = parsed.request;

    let credential;
    try {
      credential = await store.resolve();
    } catch (error) {
      writeOpenAIError(res, 401, "not_signed_in", String(error));
      return;
    }

    const wantsStream = request["stream"] === true;
    const controller = new AbortController();
    req.on("close", () => controller.abort());

    // 记账用：客户端请求的模型名 + 这次请求的起点
    const requestedModel = String(request["model"] ?? "");
    const startedAt = Date.now();

    const resolvedModel = resolveModel(request["model"]);
    const { result, model: usedModel, attempts } = await sendWithFallback({
      credential,
      request,
      model: resolvedModel.model,
      signal: controller.signal,
      prepare: (body) => prepareBodyFor(variant, body),
      tag: "",
    });

    if (!result.ok) {
      log(`upstream ${result.kind} (http ${result.status}) 试过：${attempts.join(" → ") || "-"}`);
      const extra = attempts.length > 1 ? `（已试过 ${attempts.join(" → ")}）` : "";
      writeOpenAIError(
        res,
        KIND_STATUS[result.kind] ?? 502,
        result.kind,
        `workbuddy upstream ${result.kind} (http ${result.status}): ${result.message.slice(0, 400)}${extra}`,
      );
      return;
    }

    if (!wantsStream) {
      const completion = await collectChatCompletion(result.response);
      // 换过模型时，把响应的 model 字段改成实际用的那个
      if (typeof completion === "object" && completion !== null && usedModel !== undefined) {
        completion["model"] = usedModel;
      }
      writeJson(res, 200, completion);
      void recordUsage({
        model: requestedModel, usedModel, kind: "chat", account: accountTag(credential),
        usage: completion?.["usage"], outcome: "ok", attempts: attempts.length,
        elapsedMs: Date.now() - startedAt,
      });
      return;
    }

    res.writeHead(200, {
      "Content-Type": "text/event-stream",
      "Cache-Control": "no-cache",
      Connection: "keep-alive",
      "X-Accel-Buffering": "no",
    });
    res.flushHeaders?.();

    try {
      for await (const payload of iterateSseData(result.response.body)) {
        if (payload === "[DONE]") {
          res.write("data: [DONE]\n\n");
          break;
        }
        // 记账要的 usage（含上游的 credit）在 sanitize 丢掉之前先抠一份
        captureUsage(payload);
        const sanitized = sanitizeChunk(renameChunkModel(payload, usedModel));
        if (sanitized === undefined) continue;
        res.write(`data: ${sanitized}\n\n`);
      }
      void recordUsage({
        model: requestedModel, usedModel, kind: "chat", account: accountTag(credential),
        usage: lastUsage, outcome: "ok", attempts: attempts.length,
        elapsedMs: Date.now() - startedAt,
      });
      lastUsage = null;
    } catch (error) {
      if (!res.writableEnded) {
        log(`stream failed mid-flight: ${String(error)}`);
        res.write(`data: ${JSON.stringify({ error: { message: String(error), type: "upstream_stream_error" } })}\n\n`);
        res.write("data: [DONE]\n\n");
      }
      void recordUsage({
        model: requestedModel, usedModel, kind: "chat", account: accountTag(credential),
        usage: lastUsage, outcome: "failed", attempts: attempts.length,
        elapsedMs: Date.now() - startedAt,
      });
      lastUsage = null;
    } finally {
      if (!res.writableEnded) res.end();
    }
  }

  return {
    server,
    /**
     * Bind and listen.
     *
     * @param {number} port 0 asks the OS for a free port
     * @param {string} host interface to bind
     */
    listen(port, host) {
      return new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(port, host, () => {
          server.removeListener("error", reject);
          const address = server.address();
          resolve({ host, port: typeof address === "object" && address !== null ? address.port : port });
        });
      });
    },
    close() {
      return new Promise((resolve) => {
        server.close(() => resolve());
        server.closeAllConnections?.();
      });
    },
  };
}

/** Re-exported for callers that want the raw classification table. */
export { KIND_STATUS, classifyUpstreamError };
