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
import { createHmac } from "node:crypto";
import { MioraClient, MioraError } from "./miora.js";
import { injectMediaTools, mediaTools } from "./media-tools.js";
import { runAgentNonStreaming, runAgentStreaming } from "./agent.js";
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

/**
 * 给生成的文件名算个签名。
 *
 * 为什么要签名：图片端点如果要求 `Authorization` 头，用户把地址复制到
 * 浏览器里就打不开（浏览器不会带那个头）—— 实测就是这个体验：
 * 「图生成了，但找不到」。
 *
 * 但也不能完全敞开：那样任何人猜到文件名就能遍历。
 *
 * 所以把 API key 当 HMAC 密钥，给文件名签个名放进 URL：
 *
 *     /v1/images/file/<name>?s=<签名>
 *
 *   · 从 API 拿到 URL 的人（也就是有 key 的人）能直接打开
 *   · 不知道 key 的人签不出来，遍历不了
 *   · 同一个文件签名固定 → 浏览器/CDN 缓存友好
 *
 * apiKey 为空（网关没开鉴权）时返回空串，这时也不校验。
 */
function signName(name, apiKey) {
  if (apiKey === "") return "";
  return createHmac("sha256", apiKey).update(name).digest("hex").slice(0, 16);
}

/** 常量时间比较，避免时序侧信道。长度不同直接 false。 */
function signatureMatches(expected, presented) {
  if (typeof presented !== "string" || expected === "") return false;
  const a = Buffer.from(expected, "utf8");
  const b = Buffer.from(presented, "utf8");
  if (a.length !== b.length) return false;
  return timingSafeEqual(a, b);
}

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

  // Miora —— 生图/生视频服务。
  //
  // 为什么用惰性 credential 而不是启动时就取：账号可以在运行时切
  // （多账号轮询、用户手动换号），每次调用现取才能跟上。
  const miora = new MioraClient({
    credential: () => store.resolve(),
    workbuddyBase: variant.billingBase,
    log,
  });

  // 提交过的视频任务。只是为了日志和「这个 id 是我们发的」的粗校验 ——
  // 真正的状态永远以上游为准。
  const videoTasks = new Map();

  // 生图 / 生视频能力，作为**工具**注册给模型。
  //
  // ── 为什么不是关键词匹配 ──────────────────────────────
  //
  // 第一版是关键词：命中「画」「生成」就去生图。两个死穴：
  //
  //   ① 死板。「生成一个视频」命中「生成」→ 当生图处理，
  //      用户要视频拿到图。补词表只能修一个个案。
  //   ② 一接外挂客户端就废。那段代码前面有 !hasToolUse ——
  //      一看客户端带了 tools 就跳过关键词。于是 Claude Code /
  //      pi / DSH 里说「画一只猫」，模型只会回「我没办法直接画图」。
  //
  // 现在照 WorkBuddy 自己的做法（挖 app.asar 里那个 miora-mcp 插件）：
  // 把工具注册给模型，**模型自己判断**该不该调、调哪个。
  //
  // 默认开。要关掉设 WORKBUDDY_MEDIA_TOOLS=0。
  const mediaToolsEnabled = options.mediaTools !== false
    && process.env.WORKBUDDY_MEDIA_TOOLS !== "0";

  /**
   * 把生成的文件名变成**完整可用**的 URL。
   *
   * 两件事：
   *   ① 补上 host —— 以前只给 `/v1/images/file/xxx.png`，
   *      用户粘到浏览器里打不开（不知道去哪台机器找）
   *   ② 带上签名 —— 否则要 Authorization 头，浏览器不会带
   *
   * host 从进来的请求里取。这样反向代理（new-api 之类）转发过来时，
   * 返回的也是代理那侧的地址，客户端照样能用。
   *
   * @param {import("node:http").IncomingMessage} req
   * @param {string} file 文件名（内容哈希.扩展名）
   */
  function imageUrl(req, file) {
    const host = req.headers.host ?? `127.0.0.1:${variant.port ?? 8787}`;
    const signature = signName(file, apiKey);
    const query = signature === "" ? "" : `?s=${signature}`;
    return `http://${host}/v1/images/file/${file}${query}`;
  }

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
    // 原始 URL（含查询串）—— 图片签名在 ?s= 里，不能把查询串切掉
    const rawUrl = req.url ?? "/";
    const url = rawUrl.split("?")[0];

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

    // 图片端点允许用 URL 签名代替 Authorization 头。
    //
    // 为什么：用户在浏览器里直接打开图片时不会带那个头 ——
    // 不特殊处理的话他看到的是 401，而不是图。
    // 签名是用 API key 作为 HMAC 密钥算出来的，所以「能拿出有效签名」
    // 等价于「知道 key」。安全性不降低，但可用性天壤之别。
    const isSignedImageRequest = req.method === "GET"
      && url.startsWith("/v1/images/file/")
      && signatureMatches(
        signName(url.slice("/v1/images/file/".length), apiKey),
        new URL(rawUrl, "http://localhost").searchParams.get("s"),
      );

    if (apiKey !== "" && !isSignedImageRequest) {
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

    // ── 生图 ──────────────────────────────────────────────────
    //
    // 走 Miora（WorkBuddy 内置的生成服务），不是聊天模型。
    // 接口按 OpenAI 规范做，这样 Cherry Studio / 各种画图面板
    // 不用改任何代码就能用。
    if (req.method === "POST" && (url === "/v1/images/generations" || url === "/v1/images/generations/")) {
      await imagesGenerations(req, res);
      return;
    }

    if (req.method === "POST" && (url === "/v1/images/edits" || url === "/v1/images/edits/")) {
      await imagesEdits(req, res);
      return;
    }

    // ── 视频生成 ──────────────────────────────────────────────
    //
    // OpenAI 官方没有视频接口，业界主要有两种写法，这里都收：
    //   /v1/video/generations   （跟 images 对称，更常见）
    //   /v1/videos
    if (req.method === "POST" &&
        (url === "/v1/video/generations" || url === "/v1/video/generations/" ||
         url === "/v1/videos" || url === "/v1/videos/")) {
      await videoGenerations(req, res);
      return;
    }

    // 视频是异步的（要渲染几百帧），另给一个查任务的端点，
    // 免得客户端把 HTTP 连接一直挂着。
    if (req.method === "GET" && url.startsWith("/v1/video/status/")) {
      await videoStatus(req, res, url.slice("/v1/video/status/".length));
      return;
    }

    // 网关自己存下来的生成结果。
    //
    // 为什么不让客户端直接用上游的签名 URL：那个 URL 1 小时失效，
    // 客户端如果存下来晚点再看就打不开了。这里中转一层，永久可用。
    // 注意：要放在 API key 校验**之前**处理签名 —— 见下面的说明。
    if (req.method === "GET" && url.startsWith("/v1/images/file/")) {
      await serveGeneratedFile(req, res, url.slice("/v1/images/file/".length), rawUrl);
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


  // ── 生图 / 改图 / 交付生成结果 ──────────────────────────────
  //
  // 三个函数对应上面三个路由。都按 OpenAI 的图片接口规范做，
  // 这样客户端不用改代码。


  /**
   * POST /v1/video/generations
   *
   * 视频和图片有两个区别：
   *
   *   ① **慢**。图片通常 10-30 秒，视频要几分钟。所以这里默认
   *      「提交就走」——立刻返回任务 id，客户端轮询查状态。
   *      传 `wait: true` 才会一直等（最多 15 分钟）。
   *
   *   ② 参数多。duration / resolution / aspect_ratio，还有可选的
   *      首尾帧、参考图。
   *
   * 请求体：
   *   {
   *     prompt,                    // 必填（首尾帧模式下可省）
   *     mode?: "text" | "frame" | "reference",   // 默认 text
   *     duration?, resolution?, aspect_ratio?,
   *     first_frame_image?,        // mode=frame 时用
   *     last_frame_image?,
   *     references?: [图...],      // mode=reference 时用
   *     wait?: boolean             // true = 等到底再返回
   *   }
   */
  async function videoGenerations(req, res) {
    const parsed = await readJsonBody(req);
    if (!parsed.ok) {
      writeOpenAIError(res, parsed.status, parsed.kind, parsed.message);
      return;
    }

    const body = parsed.request ?? {};
    const mode = typeof body.mode === "string" ? body.mode : "text";
    const prompt = typeof body.prompt === "string" ? body.prompt.trim() : "";

    // prompt 在纯文生视频时必填；首尾帧/参考图模式可以只给图
    if (prompt === "" && mode === "text") {
      writeOpenAIError(res, 400, "invalid_request_error",
        "mode=text 时 prompt 不能为空");
      return;
    }

    // 组装上游要的 body
    let upstreamBody;
    if (mode === "frame") {
      const first = resolveMedia(body.first_frame_image);
      if (first === undefined) {
        writeOpenAIError(res, 400, "invalid_request_error",
          "mode=frame 时要给 first_frame_image");
        return;
      }
      const input = { first_frame_image: first };
      const last = resolveMedia(body.last_frame_image);
      if (last !== undefined) input.last_frame_image = last;
      upstreamBody = { input };
    } else if (mode === "reference") {
      const refs = Array.isArray(body.references) ? body.references : [];
      const resolved = refs.map(resolveMedia).filter((r) => r !== undefined);
      if (resolved.length === 0) {
        writeOpenAIError(res, 400, "invalid_request_error",
          "mode=reference 时 references 至少要有一张图");
        return;
      }
      upstreamBody = { input: { reference_images: resolved } };
    } else {
      upstreamBody = {};
    }

    // 公共字段
    if (prompt !== "") upstreamBody.prompt = prompt;
    if (typeof body.duration === "number" && Number.isFinite(body.duration)) {
      upstreamBody.duration = body.duration;
    }
    if (typeof body.resolution === "string" && body.resolution !== "") {
      upstreamBody.resolution = body.resolution;
    }
    if (typeof body.aspect_ratio === "string" && body.aspect_ratio !== "") {
      upstreamBody.aspect_ratio = body.aspect_ratio;
    }

    // 「提交就走」模式：立刻回一个任务 id，客户端自己轮询
    if (body.wait !== true) {
      try {
        const key = await miora.getTempKey();
        const submit = await miora.submitRaw("video", upstreamBody);
        const taskId = submit?.upstreamTaskId;
        if (typeof taskId !== "string" || taskId === "") {
          writeOpenAIError(res, 502, "upstream_error", "上游没返回任务 id");
          return;
        }
        videoTasks.set(taskId, { createdAt: Date.now(), model: submit.model ?? "" });
        log(`[video] 任务已提交 ${taskId}，模型 ${submit.model ?? "?"}`);
        writeJson(res, 200, {
          id: taskId,
          object: "video.generation",
          created: Math.floor(Date.now() / 1000),
          status: submit.status ?? "pending",
          model: submit.model ?? "",
          // 客户端可以拿这个查状态
          status_url: `/v1/video/status/${taskId}`,
        });
      } catch (error) {
        respondMioraError(res, error, "[video]");
      }
      return;
    }

    // 等待模式：一直轮询到完成
    try {
      const started = Date.now();
      const result = await miora.submit("video", undefined, upstreamBody);
      const files = result.files;
      if (files.length === 0) {
        writeOpenAIError(res, 502, "upstream_error",
          result.errorMessage ?? "上游没有返回视频");
        return;
      }
      const media = await miora.download(files[0].url);
      log(`[video] 生成完成，耗时 ${Math.round((Date.now() - started) / 1000)}s`);
      writeJson(res, 200, {
        created: Math.floor(Date.now() / 1000),
        data: [{ url: imageUrl(req, media.file) }],
        model: result.model,
      });
    } catch (error) {
      respondMioraError(res, error, "[video]");
    }
  }

  /**
   * GET /v1/video/status/<taskId>
   *
   * 查一个视频任务。完成后会把文件下载到本地，返回网关的 URL。
   *
   * 为什么在这里下载而不是让客户端自己取：上游给的是 1 小时过期的
   * 腾讯云签名 URL，客户端可能过几小时才来看。
   */
  async function videoStatus(req, res, taskId) {
    if (typeof taskId !== "string" || taskId === "") {
      writeOpenAIError(res, 400, "invalid_request_error", "缺少任务 id");
      return;
    }

    try {
      const entry = await miora.query("video", taskId);
      const status = entry?.status ?? "unknown";
      const files = entry?.resultFiles ?? entry?.result_files ?? [];

      const payload = {
        id: taskId,
        object: "video.generation",
        status,
        model: entry?.model ?? "",
      };

      if (status === "completed" && Array.isArray(files) && files.length > 0) {
        const url = files[0].signedUrl ?? files[0].url;
        if (typeof url === "string" && url !== "") {
          const media = await miora.download(url);
          payload.data = [{ url: imageUrl(req, media.file) }];
        }
      }

      if (entry?.errorMessage) payload.error = { message: entry.errorMessage };

      writeJson(res, 200, payload);
    } catch (error) {
      respondMioraError(res, error, "[video]");
    }
  }

  /**
   * 把一个「图片输入」规范化成 Miora 要的 media payload。
   *
   * 接受三种形态：
   *   · https URL        → { url }
   *   · data:image/...   → { base64 }
   *   · 裸 base64        → { base64 }
   *
   * @returns {object|undefined} 给不出有效值就返回 undefined（调用方报 400）
   */
  function resolveMedia(input) {
    if (typeof input !== "string" || input === "") return undefined;
    if (input.startsWith("http://") || input.startsWith("https://")) return { url: input };
    if (input.startsWith("data:")) {
      const comma = input.indexOf(",");
      if (comma < 0) return undefined;
      return { base64: input.slice(comma + 1) };
    }
    return { base64: input };
  }

  /**
   * POST /v1/images/generations
   *
   * 请求（OpenAI 规范）：
   *   { prompt, model?, n?, size?, quality?, response_format? }
   *
   * 我们支持 response_format:
   *   · "url"（默认）—— 返回网关自己的 URL（不过期）
   *   · "b64_json"   —— 直接给 base64，客户端不用再请求一次
   *
   * `n > 1` 时并发提交多个任务 —— Miora 一次只生一张。
   */
  async function imagesGenerations(req, res) {
    const parsed = await readJsonBody(req);
    if (!parsed.ok) {
      writeOpenAIError(res, parsed.status, parsed.kind, parsed.message);
      return;
    }

    const body = parsed.request ?? {};
    const prompt = typeof body.prompt === "string" ? body.prompt.trim() : "";
    if (prompt === "") {
      writeOpenAIError(res, 400, "invalid_request_error", "prompt 不能为空");
      return;
    }

    // n：OpenAI 允许 1-10。上游一次一张，这里并发多跑几次。
    const n = Number.isInteger(body.n) && body.n > 0 ? Math.min(body.n, 4) : 1;

    const upstreamBody = { prompt };
    if (typeof body.size === "string" && body.size !== "" && body.size !== "auto") {
      upstreamBody.size = body.size;
    }
    if (typeof body.quality === "string" && body.quality !== "") {
      upstreamBody.quality = body.quality;
    }

    const started = Date.now();
    try {
      const results = await Promise.all(
        Array.from({ length: n }, () => miora.submit("image", "generate", upstreamBody)),
      );

      const files = results.flatMap((r) => r.files);
      if (files.length === 0) {
        const failed = results.find((r) => r.errorMessage);
        writeOpenAIError(
          res, 502, "upstream_error",
          failed?.errorMessage ?? "上游没有返回图片",
        );
        return;
      }

      const wanted = body.response_format === "b64_json" ? "b64_json" : "url";
      const data = await Promise.all(
        files.map(async (f) => {
          const media = await miora.download(f.url);
          if (wanted === "b64_json") {
            const { readFile } = await import("node:fs/promises");
            const { join } = await import("node:path");
            const buffer = await readFile(join(MioraClient.mediaDir(), media.file));
            return { b64_json: buffer.toString("base64") };
          }
          return { url: imageUrl(req, media.file) };
        }),
      );

      log(`[images] 生成 ${data.length} 张，耗时 ${Date.now() - started}ms，模型 ${results[0]?.model ?? "?"}`);
      writeJson(res, 200, {
        created: Math.floor(Date.now() / 1000),
        data,
      });
    } catch (error) {
      respondMioraError(res, error, "[images]");
    }
  }

  /**
   * POST /v1/images/edits
   *
   * OpenAI 规范里这是 multipart/form-data（带 image 文件）。
   * 这里同时接受 JSON 形式 —— 因为很多客户端（尤其国内工具）
   * 更愿意直接传 base64：
   *
   *   { image: "<base64 或 data URI 或 https URL>", prompt, size? }
   */
  async function imagesEdits(req, res) {
    const parsed = await readJsonBody(req);
    if (!parsed.ok) {
      writeOpenAIError(res, parsed.status, parsed.kind, parsed.message);
      return;
    }

    const body = parsed.request ?? {};
    const prompt = typeof body.prompt === "string" ? body.prompt.trim() : "";
    if (prompt === "") {
      writeOpenAIError(res, 400, "invalid_request_error", "prompt 不能为空");
      return;
    }

    const image = body.image ?? body.image_url;
    if (typeof image !== "string" || image === "") {
      writeOpenAIError(
        res, 400, "invalid_request_error",
        "要提供 image：可以是 https URL、data URI，或者裸 base64",
      );
      return;
    }

    // 组装 Miora 要的 media payload。
    // 上游接受三种：base64 / url / file_key。这里按输入形态判断。
    let media;
    if (image.startsWith("http://") || image.startsWith("https://")) {
      media = { url: image };
    } else if (image.startsWith("data:")) {
      const comma = image.indexOf(",");
      if (comma < 0) {
        writeOpenAIError(res, 400, "invalid_request_error", "data URI 格式不对");
        return;
      }
      media = { base64: image.slice(comma + 1) };
    } else {
      media = { base64: image };
    }

    const upstreamBody = { prompt, images: [media] };
    if (typeof body.size === "string" && body.size !== "" && body.size !== "auto") {
      upstreamBody.size = body.size;
    }

    try {
      const result = await miora.submit("image", "edit", upstreamBody);
      const files = result.files;
      if (files.length === 0) {
        writeOpenAIError(res, 502, "upstream_error", result.errorMessage ?? "上游没有返回图片");
        return;
      }

      const wanted = body.response_format === "b64_json" ? "b64_json" : "url";
      const data = await Promise.all(
        files.map(async (f) => {
          const m = await miora.download(f.url);
          if (wanted === "b64_json") {
            const { readFile } = await import("node:fs/promises");
            const { join } = await import("node:path");
            const buffer = await readFile(join(MioraClient.mediaDir(), m.file));
            return { b64_json: buffer.toString("base64") };
          }
          return { url: imageUrl(req, m.file) };
        }),
      );

      log(`[images] 改图 ${data.length} 张，模型 ${result.model}`);
      writeJson(res, 200, {
        created: Math.floor(Date.now() / 1000),
        data,
      });
    } catch (error) {
      respondMioraError(res, error, "[images]");
    }
  }

  /** GET /v1/images/file/<name> —— 把网关存下来的生成结果发回去。 */
  async function serveGeneratedFile(req, res, name, rawUrl) {
    // 只允许「哈希.扩展名」这种名字 —— 防止有人用 ../ 读别的文件
    if (!/^[a-f0-9]{8,64}\.(png|jpg|jpeg|webp|mp4)$/i.test(name)) {
      writeOpenAIError(res, 400, "invalid_request_error", "文件名不合法");
      return;
    }

    const { readFile } = await import("node:fs/promises");
    const { join } = await import("node:path");
    const file = join(MioraClient.mediaDir(), name);

    try {
      const buffer = await readFile(file);
      const ext = name.split(".").pop().toLowerCase();
      const mime = ext === "jpg" || ext === "jpeg" ? "image/jpeg"
        : ext === "webp" ? "image/webp"
        : ext === "mp4" ? "video/mp4"
        : "image/png";
      res.writeHead(200, {
        "Content-Type": mime,
        "Content-Length": buffer.length,
        "Cache-Control": "public, max-age=31536000, immutable",
      });
      res.end(buffer);
    } catch {
      writeOpenAIError(res, 404, "not_found", "找不到这个文件（可能已被清理）");
    }
  }

  /** 统一把 MioraError 转成 OpenAI 格式的错误响应。 */
  function respondMioraError(res, error, tag) {
    if (error instanceof MioraError) {
      log(`${tag} ${error.code}: ${error.message}`);
      writeOpenAIError(res, error.status, error.openaiType, error.message);
      return;
    }
    log(`${tag} 未预期的错误: ${String(error)}`);
    writeOpenAIError(res, 500, "internal_error", String(error?.message ?? error));
  }


  // ── 自动画图的辅助函数 ──────────────────────────────────────

  /** 请求里有没有图片输入（有的话说明用户在讨论图，不是在要新图）。 */
  function hasImageContent(request) {
    const messages = Array.isArray(request.messages) ? request.messages : [];
    return messages.some((m) => {
      if (!m || typeof m !== "object") return false;
      const content = m.content;
      if (Array.isArray(content)) {
        return content.some((part) =>
          part && typeof part === "object" && part.type === "image_url");
      }
      return false;
    });
  }

  /** 请求里带工具了就不插手 —— 那是 agent 在干活，不该被截胡。 */
  function hasToolUse(request) {
    return Array.isArray(request.tools) && request.tools.length > 0;
  }

  /**
   * 最后一条 user 消息的纯文本。
   *
   * 只看最后一条 —— 历史里出现过的「画」不代表现在要画。
   */
  function lastUserText(request) {
    const messages = Array.isArray(request.messages) ? request.messages : [];
    for (let i = messages.length - 1; i >= 0; i -= 1) {
      const m = messages[i];
      if (!m || typeof m !== "object" || m.role !== "user") continue;
      if (typeof m.content === "string") return m.content;
      if (Array.isArray(m.content)) {
        return m.content
          .filter((p) => p && typeof p === "object" && p.type === "text")
          .map((p) => String(p.text ?? ""))
          .join("\n");
      }
      return "";
    }
    return "";
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

    // ── 把生图 / 生视频注册成工具 ──────────────────────────
    //
    // 客户端没自带同名工具时才注入（不抢客户端的实现）。
    // Claude Code / pi / DSH 本来就没有这些工具 —— 正好补上。
    //
    // request 是 readJsonBody 现解析出来的，每请求一份，直接改安全。
    const injected = [];
    if (mediaToolsEnabled && !hasImageContent(request)) {
      const info = injectMediaTools(request);
      injected.push(...info.injected);
      if (injected.length > 0) {
        log(`[agent] 注入 ${injected.length} 个媒体工具：${injected.join(", ")}`);
      }
    }

    // 一轮发送。agent 循环要用同一个入口，所以包成函数。
    const sendRound = () => sendWithFallback({
      credential,
      request,
      model: resolvedModel.model,
      signal: controller.signal,
      prepare: (body) => prepareBodyFor(variant, body),
      tag: "",
    });

    /** 把 agent 循环里的失败变成客户端能读的错误。 */
    const failWith = (round) => {
      const { result, attempts } = round;
      log(`upstream ${result.kind} (http ${result.status}) 试过：${attempts.join(" → ") || "-"}`);
      const extra = attempts.length > 1 ? `（已试过 ${attempts.join(" → ")}）` : "";
      writeOpenAIError(
        res,
        KIND_STATUS[result.kind] ?? 502,
        result.kind,
        `workbuddy upstream ${result.kind} (http ${result.status}): ${result.message.slice(0, 400)}${extra}`,
      );
    };

    const agentCtx = {
      request,
      miora,
      makeUrl: (file) => imageUrl(req, file),
      log,
      trackVideoTask: (taskId, model) => videoTasks.set(taskId, { createdAt: Date.now(), model: model ?? "" }),
    };

    const logRound = ({ round, calls, results }) => {
      log(`[agent] 第 ${round} 轮执行 ${calls.length} 个工具：`
        + calls.map((c, k) => `${c.function?.name}=${results[k]?.ok ? "ok" : "fail"}`).join(", "));
    };

    // ── 非流式 ────────────────────────────────────────────
    if (!wantsStream) {
      const outcome = await runAgentNonStreaming({ ...agentCtx, send: sendRound, onRound: logRound });

      if (outcome.error !== undefined) {
        failWith(outcome.error);
        return;
      }

      const completion = outcome.completion;
      // 换过模型时，把响应的 model 字段改成实际用的那个
      if (typeof completion === "object" && completion !== null) {
        completion["model"] = resolvedModel.model;
      }
      writeJson(res, 200, completion);
      void recordUsage({
        model: requestedModel, usedModel: resolvedModel.model, kind: "chat", account: accountTag(credential),
        usage: completion?.["usage"], outcome: "ok", attempts: 1,
        elapsedMs: Date.now() - startedAt,
      });
      return;
    }

    // ── 流式 ──────────────────────────────────────────────
    res.writeHead(200, {
      "Content-Type": "text/event-stream",
      "Cache-Control": "no-cache",
      Connection: "keep-alive",
      "X-Accel-Buffering": "no",
    });
    res.flushHeaders?.();

    // 写一个 delta 分片。过 sanitizeChunk（洗掉上游那些会骗到严格客户端的
    // 字段）和 renameChunkModel（换过模型时改名字）。
    const writeChunk = (chunk) => {
      if (res.writableEnded) return;
      const payload = JSON.stringify(chunk);
      captureUsage(payload);
      const sanitized = sanitizeChunk(renameChunkModel(payload, resolvedModel.model));
      if (sanitized === undefined) return;
      res.write(`data: ${sanitized}\n\n`);
    };

    // 工具执行期间保活。SSE 注释行，客户端按规范会忽略。
    const writeKeepalive = () => {
      if (res.writableEnded) return;
      res.write(": keepalive\n\n");
    };

    try {
      const outcome = await runAgentStreaming({
        ...agentCtx,
        send: sendRound,
        emit: writeChunk,
        keepalive: writeKeepalive,
        onRound: logRound,
      });

      if (outcome.error !== undefined) {
        log(`[agent] 流中断：${outcome.error.result?.kind ?? "?"}`);
        if (!res.writableEnded) {
          // 头已经发出去了，不能改状态码 —— 用 SSE 事件报错
          const shaped = {
            error: {
              message: `workbuddy upstream ${outcome.error.result?.kind ?? "error"}`,
              type: outcome.error.result?.kind ?? "upstream_error",
            },
          };
          res.write(`data: ${JSON.stringify(shaped)}\n\n`);
        }
      }

      if (!res.writableEnded) res.write("data: [DONE]\n\n");
      void recordUsage({
        model: requestedModel, usedModel: resolvedModel.model, kind: "chat", account: accountTag(credential),
        usage: lastUsage, outcome: outcome.error === undefined ? "ok" : "failed", attempts: 1,
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
        model: requestedModel, usedModel: resolvedModel.model, kind: "chat", account: accountTag(credential),
        usage: lastUsage, outcome: "failed", attempts: 1,
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
