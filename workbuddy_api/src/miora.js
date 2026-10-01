/**
 * Miora —— WorkBuddy 桌面 App 内置的生图/生视频服务。
 *
 * ## 为什么单独一个模块
 *
 * WorkBuddy 的能力分两块：
 *
 *   · 聊天模型（16 个）→ `/v1/chat/completions` 那条路，也就是本网关一直在做的
 *   · **生成能力（画图、改图、生视频）→ 走 Miora**，一个独立服务
 *
 * 第二块以前没接。用户问「能不能画图」，答案曾经是「上游没有生图模型」——
 * 那是我只看了 `/v1/models` 的 16 个聊天模型就下的结论，错了。
 *
 * 事实是：WorkBuddy 把生成能力做成了**工具调用**（`miora_text_to_image` 等
 * 8 个工具），走 MCP 协议连一个叫 Miora 的独立服务，域名
 * `public.miora.qq.com`。聊天模型被要求画图时只会回文字，因为它那一层
 * 看不到这些工具。
 *
 * ## 认证链路（实测验证过）
 *
 * 两层 key：
 *
 *   ① 保险库里的登录 JWT（我们本来就有）
 *         ↓ POST {workbuddy}/v2/api-keys
 *   ② 一个 15 分钟有效的临时 key（`ck_t_...`）
 *         ↓ 后续所有 Miora 请求都用它
 *   ③ Miora 的 /api/ai/workbuddy-proxy/*
 *
 * **关键是第 ① 步**：tempKey 是用 JWT 换的，不需要桌面 App 运行 ——
 * 所以网关这个独立进程能自己完成整条链路。
 *
 * ## 生成的图片放哪
 *
 * Miora 返回的是腾讯云 COS 的**签名 URL，1 小时失效**。
 * 直接把它转给客户端有两个问题：
 *
 *   · 客户端如果存下来晚点再看，链接已经失效
 *   · URL 很长（带签名参数），有些客户端会截断
 *
 * 所以本模块提供两种交付方式：
 *   · 默认：**下载下来存到本地**，返回网关自己的 URL（不过期）
 *   · `deliver: "url"`：直接转发上游 URL（省一次下载，但会过期）
 *
 * @module workbuddy-api/miora
 */

import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { stateHome } from "./paths.js";

/** Miora 的正式域名。App 里还有个 `public.miora.qq.com` 的联调兜底，同一个。 */
const DEFAULT_MIORA_BASE = "https://public.miora.qq.com";

/** 换 tempKey 的路径。App 源码里的 `CLIENT_TEMPKEY_API_PATH`。 */
const TEMPKEY_PATH = "/v2/api-keys";

/** 审计名 —— 上游用它标识「这个 key 是给谁用的」。必须和 App 一致。 */
const TEMPKEY_AUDIT_NAME = "workbuddy-connect-cloud-service";

/** Miora 的 API 前缀。 */
const MIORA_API_BASE = "/api/ai/workbuddy-proxy";

/**
 * tempKey 的 TTL。
 *
 * 上游给 900 秒（15 分钟）。本地缓存用略短一点的值 ——
 * 提前失效重取，比让请求撞在上游刚好过期的那一秒强。
 */
const TEMPKEY_TTL_SECONDS = 900;
const TEMPKEY_REFRESH_MARGIN_MS = 60_000; // 提前 1 分钟

/** 单次请求超时。生图是同步阻塞的，给宽松点。 */
const SUBMIT_TIMEOUT_MS = 180_000;
const MISC_TIMEOUT_MS = 30_000;

/**
 * 轮询超时。
 *
 * 图片和视频差很多 —— App 源码里的值是：
 *   图片  POLL_TIMEOUT_MS = 90 秒
 *   视频  VIDEO_POLL_TIMEOUT_MS = 15 分钟
 *
 * 视频慢得多（要渲染几百帧），用图片那个超时会让它必然失败。
 */
const IMAGE_POLL_TIMEOUT_MS = 90_000;
const VIDEO_POLL_TIMEOUT_MS = 15 * 60_000;

/** 轮询间隔。App 里图片 2.5 秒、视频 3 秒 —— 都取 3 秒。 */
const POLL_INTERVAL_MS = 3_000;

/** 下载生成的图片时的超时。 */
const DOWNLOAD_TIMEOUT_MS = 120_000;

/**
 * 一个可用的 Miora 客户端。
 *
 * 有状态（缓存 tempKey），所以按账号建、复用。
 */
export class MioraClient {
  /**
   * @param {object} options
   * @param {() => Promise<{accessToken: string, enterpriseId?: string, label?: string}>} options.credential
   *        拿当前账号凭据。每次要用时现取 —— 这样账号切换后自动跟上。
   * @param {string} options.workbuddyBase 换 tempKey 的域名（如 https://www.codebuddy.cn）
   * @param {string} [options.mioraBase]
   * @param {object} [options.logger]
   */
  constructor(options) {
    this.credential = options.credential;
    this.workbuddyBase = options.workbuddyBase.replace(/\/+$/, "");
    this.mioraBase = (options.mioraBase ?? DEFAULT_MIORA_BASE).replace(/\/+$/, "");
    this.log = options.logger ?? (() => {});

    /** @type {{key: string, expiresAt: number, forToken: string} | undefined} */
    this.tempKey = undefined;
  }

  /**
   * 拿一个可用的 tempKey。
   *
   * 缓存规则：
   *   · 同一个 JWT 且没过期 → 复用
   *   · JWT 变了（切号了）→ 重取
   *   · 快过期了 → 重取
   *
   * @param {boolean} [force] 强制重取（上游说 key 无效时用）
   */
  async getTempKey(force = false) {
    const credential = await this.credential();
    const token = credential.accessToken;
    if (typeof token !== "string" || token === "") {
      throw new MioraError("AUTH_MISSING_TOKEN", "保险库里没有可用的登录凭据");
    }

    const cached = this.tempKey;
    if (!force && cached !== undefined) {
      const stillSameAccount = cached.forToken === token;
      const notExpiringSoon = cached.expiresAt - Date.now() > TEMPKEY_REFRESH_MARGIN_MS;
      if (stillSameAccount && notExpiringSoon) return cached.key;
    }

    const headers = {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
      "User-Agent": "WorkBuddy/5.6.2",
    };
    // 企业账号要带上，个人账号没有这个字段 —— 实测两种都行
    if (typeof credential.enterpriseId === "string" && credential.enterpriseId !== "") {
      headers["X-Enterprise-Id"] = credential.enterpriseId;
    }

    const body = {
      name: TEMPKEY_AUDIT_NAME,
      expire_in_seconds: TEMPKEY_TTL_SECONDS,
      temporary: true,
    };

    const response = await this.#fetch(
      `${this.workbuddyBase}${TEMPKEY_PATH}`,
      { method: "POST", headers, body: JSON.stringify(body) },
      MISC_TIMEOUT_MS,
    );

    if (!response.ok) {
      const text = await response.text().catch(() => "");
      throw new MioraError(
        "AUTH_TEMPKEY_FAILED",
        `换临时凭据失败（HTTP ${response.status}）：${text.slice(0, 300)}`,
      );
    }

    const payload = await response.json().catch(() => undefined);
    const key = payload?.data?.key;
    if (typeof key !== "string" || !key.startsWith("ck_t_")) {
      throw new MioraError(
        "AUTH_TEMPKEY_FAILED",
        `上游没返回合法的临时凭据：${JSON.stringify(payload).slice(0, 300)}`,
      );
    }

    const expiresAt = Date.parse(payload?.data?.expires_at ?? "") ||
      (Date.now() + TEMPKEY_TTL_SECONDS * 1000);

    this.tempKey = { key, expiresAt, forToken: token };
    this.log(`[miora] 临时凭据已获取，${Math.round((expiresAt - Date.now()) / 1000)} 秒后过期`);
    return key;
  }

  /**
   * 提交一个生成任务。
   *
   * 上游有两种模式：
   *   · `sync`  —— 提交时就带着结果回来（生图通常是这样，实测）
   *   · `async` —— 只给任务 id，要轮询（视频通常是这种）
   *
   * 这里统一处理：sync 直接用返回结果，async 才轮询。
   *
   * @param {"image"|"video"} kind 图片还是视频。两者的轮询超时差很多。
   * @param {"generate"|"edit"|undefined} action image 才有 edit
   * @param {object} body
   * @param {object} [options]
   * @param {number} [options.pollTimeoutMs] 覆盖默认轮询超时
   */
  async submit(kind, action, body, options = {}) {
    const queryPath = `${MIORA_API_BASE}/${kind}/query-task`;

    const submitted = await this.#postWithAuth(
      this.#submitPath(kind, action),
      body,
      SUBMIT_TIMEOUT_MS,
    );
    const data = submitted?.data ?? submitted;

    const taskId = data?.upstreamTaskId;
    if (typeof taskId !== "string" || taskId === "") {
      throw new MioraError(
        "UPSTREAM_ERROR",
        `上游没返回任务 id：${JSON.stringify(submitted).slice(0, 300)}`,
      );
    }

    // sync：提交即完成，不用再问。
    //
    // 这里也看 failed —— 同步模式可能带着失败回来。
    // 那种情况不抛异常，走 normalize（里面带 errorMessage），
    // 让调用方看到「任务跑了但没出图」，而不是一个笼统的异常。
    if (data.mode === "sync" || data.status === "completed" || data.status === "failed") {
      return this.#normalize(data, taskId);
    }

    // async：轮询到终态。视频默认给 15 分钟，图片 90 秒。
    const defaultTimeout = kind === "video" ? VIDEO_POLL_TIMEOUT_MS : IMAGE_POLL_TIMEOUT_MS;
    const entry = await this.#poll(
      queryPath,
      taskId,
      options.pollTimeoutMs ?? defaultTimeout,
      kind,
    );
    return this.#normalize({ ...entry, upstreamTaskId: taskId }, taskId);
  }

  /**
   * 只提交、不等待 —— 拿到任务 id 就返回。
   *
   * 为什么需要它：**视频太慢了**（几分钟到十几分钟）。
   * 「提交就走 + 客户端轮询」比把 HTTP 连接挂在那儿好得多：
   *   · 客户端的超时设置通常撑不住十几分钟
   *   · 网关重启一下连接就断了，任务白跑
   *
   * @returns {Promise<{upstreamTaskId: string, status?: string, model?: string, mode?: string}>}
   */
  async submitRaw(kind, body) {
    const payload = await this.#postWithAuth(
      this.#submitPath(kind, undefined),
      body,
      SUBMIT_TIMEOUT_MS,
    );
    const data = payload?.data ?? payload;

    const taskId = data?.upstreamTaskId;
    if (typeof taskId !== "string" || taskId === "") {
      throw new MioraError(
        "UPSTREAM_ERROR",
        `上游没返回任务 id：${JSON.stringify(payload).slice(0, 300)}`,
      );
    }

    return {
      upstreamTaskId: taskId,
      status: data.status ?? "pending",
      model: data.model ?? "",
      provider: data.provider ?? "",
      mode: data.mode ?? "async",
    };
  }

  /**
   * 查一个任务的状态，**不轮询、不下载**。
   *
   * 给 /v1/video/status/<id> 那种「客户端自己问」的场景用。
   *
   * @returns {Promise<object>} 上游的 task 对象（含 status / resultFiles）
   */
  async query(kind, taskId) {
    const queryPath = `${MIORA_API_BASE}/${kind}/query-task`;
    const payload = await this.#postWithAuth(
      queryPath,
      { upstreamTaskIds: [taskId] },
      MISC_TIMEOUT_MS,
    );

    const data = payload?.data ?? payload;

    // 上游的返回结构有两种（实测都见过）：
    //   · data 直接是那个 task 对象
    //   · data 是 { "<taskId>": {...} } 的映射
    // query-task 用的是后者。
    if (Array.isArray(data)) return data[0];
    if (Array.isArray(data?.tasks)) return data.tasks[0];
    if (data && typeof data === "object" && data[taskId]) return data[taskId];
    return data;
  }

  /**
   * 把生成结果里的图片下载到本地，返回网关自己的路径。
   *
   * @param {string} signedUrl 上游给的签名 URL
   * @returns {Promise<{file: string, bytes: number, mimeType: string}>}
   */
  async download(signedUrl) {
    const response = await this.#fetch(signedUrl, { method: "GET" }, DOWNLOAD_TIMEOUT_MS);
    if (!response.ok) {
      throw new MioraError(
        "DOWNLOAD_FAILED",
        `下载生成的图片失败（HTTP ${response.status}）`,
      );
    }

    const buffer = Buffer.from(await response.arrayBuffer());
    const mimeType = response.headers.get("content-type") ?? "image/png";
    const ext = mimeType.includes("jpeg") ? "jpg"
      : mimeType.includes("webp") ? "webp"
      : mimeType.includes("mp4") ? "mp4"
      : "png";

    // 文件名用内容哈希 —— 同一张图重复下载会覆盖，不会越积越多
    const { createHash } = await import("node:crypto");
    const name = `${createHash("sha256").update(buffer).digest("hex").slice(0, 24)}.${ext}`;

    const dir = mediaDir();
    await mkdir(dir, { recursive: true, mode: 0o700 });
    await writeFile(join(dir, name), buffer, { mode: 0o600 });

    return { file: name, bytes: buffer.length, mimeType };
  }

  /**
   * 读回之前下载过的文件（给 HTTP 端点用）。
   */
  static mediaDir() {
    return mediaDir();
  }

  // ── 内部 ──────────────────────────────────────────────────────

  #submitPath(kind, action) {
    if (kind === "video") return `${MIORA_API_BASE}/video/submit`;
    return action === "edit"
      ? `${MIORA_API_BASE}/image/edit/submit`
      : `${MIORA_API_BASE}/image/generate/submit`;
  }

  /**
   * 带认证的 POST。tempKey 失效时自动重取一次。
   *
   * 为什么要自动重取：tempKey 只有 15 分钟。缓存再准也可能赶上
   * 网关休眠、时钟跳变之类的边界。上游会明确回 AUTH_INVALID_TEMP_KEY，
   * 那时重取是确定正确的动作 —— 不该把这个错误抛给用户让他重试。
   */
  async #postWithAuth(path, body, timeoutMs) {
    for (let attempt = 0; attempt < 2; attempt += 1) {
      const tempKey = await this.getTempKey(attempt > 0);
      const response = await this.#fetch(
        `${this.mioraBase}${path}`,
        {
          method: "POST",
          headers: {
            Authorization: `Bearer ${tempKey}`,
            "Content-Type": "application/json",
          },
          body: JSON.stringify(body),
        },
        timeoutMs,
      );

      const text = await response.text().catch(() => "");

      if (!response.ok) {
        throw new MioraError(
          "UPSTREAM_ERROR",
          `上游返回 HTTP ${response.status}：${text.slice(0, 300)}`,
        );
      }

      let payload;
      try {
        payload = JSON.parse(text);
      } catch {
        throw new MioraError("UPSTREAM_ERROR", `上游返回的不是 JSON：${text.slice(0, 300)}`);
      }

      // 上游的错误码可能藏在 code 字段里（HTTP 200 但业务失败）
      const code = String(payload?.code ?? "0");
      if (code !== "0") {
        if (code.includes("AUTH_INVALID_TEMP_KEY") || code.includes("AUTH_MISSING_TEMP_KEY")) {
          this.log("[miora] 临时凭据失效，重新获取");
          continue; // 下一轮 force 重取
        }
        throw new MioraError(
          "UPSTREAM_ERROR",
          payload?.msg ?? payload?.message ?? `上游错误码 ${code}`,
        );
      }

      return payload;
    }

    throw new MioraError("AUTH_TEMPKEY_FAILED", "重新获取临时凭据后仍然失败");
  }

  async #poll(queryPath, taskId, timeoutMs, kind = "image") {
    const deadline = Date.now() + timeoutMs;
    let attempt = 0;

    while (Date.now() < deadline) {
      attempt += 1;
      await new Promise((resolve) => setTimeout(resolve, POLL_INTERVAL_MS));

      let payload;
      try {
        payload = await this.#postWithAuth(
          queryPath,
          { upstreamTaskIds: [taskId] },
          MISC_TIMEOUT_MS,
        );
      } catch (error) {
        // 单次查询失败不致命（网络抖一下很正常）—— 接着轮，别放弃整个任务
        this.log(`[miora] 轮询第 ${attempt} 次失败：${error.message}`);
        continue;
      }

      const data = payload?.data ?? payload;

      // 上游的返回结构有两种（实测都见过）：
      //   · data 直接是那个 task 对象
      //   · data 是 { "<taskId>": {...} } 的映射
      // 前者是 submit 的风格，后者是 query-task 的风格。
      let entry;
      if (Array.isArray(data)) {
        entry = data[0];
      } else if (Array.isArray(data?.tasks)) {
        entry = data.tasks[0];
      } else if (data && typeof data === "object" && data[taskId]) {
        entry = data[taskId];
      } else {
        entry = data;
      }

      const status = entry?.status;
      this.log(`[miora] ${kind} 轮询第 ${attempt} 次：${status ?? "未知状态"}`);

      if (status === "completed" || status === "failed" || status === "cancelled") {
        return entry;
      }
    }

    throw new MioraError(
      "TIMEOUT",
      `等待${kind === "video" ? "视频" : "图片"}生成超时（${Math.round(timeoutMs / 1000)} 秒）`,
    );
  }

  #normalize(data, taskId) {
    const files = data.resultFiles ?? data.result_files ?? [];
    return {
      taskId,
      model: data.model ?? "",
      provider: data.provider ?? "",
      status: data.status ?? "completed",
      errorMessage: data.errorMessage ?? null,
      files: Array.isArray(files)
        ? files.filter((f) => f && typeof f === "object").map((f) => ({
          url: f.signedUrl ?? f.url ?? "",
          mimeType: f.mimeType ?? f.mime_type ?? "image/png",
          expiresAt: f.expiresAt ?? f.expires_at ?? 0,
        }))
        : [],
    };
  }

  /** 带超时的 fetch。node 内置的 fetch 超时要自己接 AbortController。 */
  async #fetch(url, init, timeoutMs) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
      return await fetch(url, { ...init, signal: controller.signal });
    } catch (error) {
      if (error?.name === "AbortError") {
        throw new MioraError("TIMEOUT", `请求超时（${Math.round(timeoutMs / 1000)} 秒）`);
      }
      throw new MioraError("NETWORK", `网络错误：${error?.message ?? String(error)}`);
    } finally {
      clearTimeout(timer);
    }
  }
}

/** Miora 相关的错误。code 用来决定 HTTP 状态码。 */
export class MioraError extends Error {
  constructor(code, message) {
    super(message);
    this.name = "MioraError";
    this.code = code;
  }

  /** 映射成 HTTP 状态码。 */
  get status() {
    switch (this.code) {
      case "AUTH_MISSING_TOKEN":
      case "AUTH_TEMPKEY_FAILED":
        return 401;
      case "TIMEOUT":
        return 504;
      case "NETWORK":
      case "UPSTREAM_ERROR":
      case "DOWNLOAD_FAILED":
      default:
        return 502;
    }
  }

  /** 映射成 OpenAI 的错误类型 —— 客户端按这个显示文案。 */
  get openaiType() {
    switch (this.code) {
      case "AUTH_MISSING_TOKEN":
      case "AUTH_TEMPKEY_FAILED":
        return "authentication_error";
      case "TIMEOUT":
        return "timeout_error";
      default:
        return "upstream_error";
    }
  }
}

/** 生成的媒体文件放哪。跟其他状态一起放在 state home 下面。 */
function mediaDir() {
  return join(stateHome(), "media");
}
