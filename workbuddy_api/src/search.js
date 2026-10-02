/**
 * WorkBuddy 的联网能力：搜索（web_search）与网页抓取（web_fetch）。
 *
 * ## 为什么单独一个模块
 *
 * WorkBuddy 的能力分三块，前两块本工具早就接了：
 *
 *   · 聊天模型        → `/v1/chat/completions`（`upstream.js`）
 *   · 生成能力        → Miora（`miora.js`）
 *   · **联网能力**    → `/agenttool/v1/*`（本模块）
 *
 * 第三块是「搜索」和「抓取」两个服务端工具，官方 Agent 用它查实时信息。
 * 域外应用（CLI / 编辑器 / 其他 harness）通常没有联网工具，把它们反代出去
 * 就能让任何客户端用上 WorkBuddy 账号的联网额度。
 *
 * ## 认证（实测）
 *
 * **只需要两个头**：`Content-Type` + `Authorization: Bearer <accessToken>`。
 * 官方 CLI 还会发 `X-Agent-Intent`、`X-Conversation-ID`、`X-IDE-Type`、
 * `X-User-Id`、自定义 UA 等一堆 header —— 逐项实测**全部可省**，去掉仍 200。
 * 但 `Bearer ` 前缀不可省，去掉立即 401。
 *
 * 凭据由调用方按需提供（和 Miora 一样惰性取），所以**不自己维护 token 文件**：
 * 走 `CredentialStore` 就能自动跟上 App 换号与 token 续期。
 *
 * ## 两个上游坑（都已在代码里绕开）
 *
 * 1. **`allowed_domains` 上游不生效** —— 实测限定 `docs.python.org` 仍会返回
 *    百度、CSDN。所以「只看某域名」是**本模块在拿到结果后自己过滤**的
 *    （见 `includeDomains`）。有过滤时会把 `max_results` 拉满再筛，否则筛完不够数。
 * 2. **排除域名靠 `-site:` 折进 query 字符串** —— 上游没有排除字段，
 *    这是官方 CLI 的做法。
 *
 * @module workbuddy-api/search
 */

/** 搜索端点。 */
const SEARCH_PATH = "/agenttool/v1/search";

/** 网页抓取端点。 */
const FETCH_PATH = "/agenttool/v1/webfetch";

/** 搜索默认超时。搜索本身 2~4 秒，给足余量。 */
const SEARCH_TIMEOUT_MS = 20_000;

/** 抓取默认超时。整页正文可能很大，比搜索宽松。 */
const FETCH_TIMEOUT_MS = 30_000;

/** 上游一次最多给多少条。 */
const MAX_RESULTS_LIMIT = 20;

/** 单次抓取最多收多少字符。 */
const MAX_FETCH_LENGTH = 200_000;

/**
 * 联网相关的错误。`code` 决定 HTTP 状态码，和 `MioraError` 同一套约定。
 */
export class SearchError extends Error {
  constructor(code, message) {
    super(message);
    this.name = "SearchError";
    this.code = code;
  }

  /** 映射成 HTTP 状态码。 */
  get status() {
    switch (this.code) {
      case "INVALID_REQUEST":
        return 400;
      case "AUTH_MISSING_TOKEN":
      case "UNAUTHORIZED":
        return 401;
      case "TIMEOUT":
        return 504;
      case "NETWORK":
      case "UPSTREAM_ERROR":
      default:
        return 502;
    }
  }

  /** 映射成 OpenAI 风格的错误类型 —— 客户端按这个显示文案。 */
  get openaiType() {
    switch (this.code) {
      case "INVALID_REQUEST":
        return "invalid_request_error";
      case "AUTH_MISSING_TOKEN":
      case "UNAUTHORIZED":
        return "authentication_error";
      case "TIMEOUT":
        return "timeout_error";
      default:
        return "upstream_error";
    }
  }
}

/** 只接受字符串数组，逐项 trim + 小写，丢掉空值。 */
function normalizeDomainList(value) {
  if (!Array.isArray(value)) return [];
  return value
    .filter((item) => typeof item === "string" && item.trim() !== "")
    .map((item) => item.trim().toLowerCase());
}

/**
 * 把排除域名折进 query —— 上游没有排除字段，这是官方 CLI 的 `-site:` 做法。
 */
function buildQuery(query, excludeDomains) {
  const list = normalizeDomainList(excludeDomains);
  if (list.length === 0) return query;
  return `${query} (${list.map((domain) => `-site:${domain}`).join(" ")})`;
}

/** 取 URL 的 host，失败给空串。 */
function hostOf(url) {
  try {
    return new URL(url).hostname.toLowerCase();
  } catch {
    return "";
  }
}

/**
 * 域名相等或为其子域。
 *
 * 不能用 `endsWith(domain)` —— 那样 `a.com` 会误配 `nota.com`。
 */
function domainMatches(host, domain) {
  return host === domain || host.endsWith(`.${domain}`);
}

/** 校验 `freshness`：`d1`–`d30` / `m1`–`m12` / `y1`–`y5`，数字可省。 */
function validFreshness(value) {
  return /^(?:d(?:[1-9]|[12]\d|30)?|m(?:[1-9]|1[0-2])?|y[1-5]?)$/u.test(value);
}

/**
 * 上游返回的信封：`{code, msg, data}` 之外还有一层业务字段。
 *
 * 搜索/抓取是**扁平**返回的（字段和 `code` 同级），不是套在 `data` 里 ——
 * 和计费那些端点不一样，别照抄 `readEnvelope`。
 */
function unwrap(status, text) {
  if (status === 401 || status === 403) {
    throw new SearchError(
      "UNAUTHORIZED",
      `WorkBuddy 拒绝了这次联网请求（HTTP ${status}）：凭据无效或已过期，请在桌面 App 重新登录`,
    );
  }
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new SearchError("UPSTREAM_ERROR", `联网接口返回非 JSON（HTTP ${status}）：${text.slice(0, 200)}`);
  }
  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    throw new SearchError("UPSTREAM_ERROR", `联网接口返回了意外的结构（HTTP ${status}）`);
  }
  if (typeof parsed.code === "number" && parsed.code !== 0) {
    throw new SearchError("UPSTREAM_ERROR", `联网接口报错：${parsed.msg ?? "未知"}（code ${parsed.code}）`);
  }
  if (status >= 400) {
    throw new SearchError("UPSTREAM_ERROR", `联网接口返回 HTTP ${status}：${text.slice(0, 200)}`);
  }
  return parsed;
}

/**
 * 一个 WorkBuddy 联网客户端。
 *
 * 无状态（凭据每次现取），所以整个进程共用一个实例即可。
 */
export class SearchClient {
  /**
   * @param {object} options
   * @param {object} options.variant variant 描述符（决定 base URL）
   * @param {() => Promise<{accessToken: string}>} options.credential
   *        取当前账号凭据。**每次要用时现取** —— 这样账号切换、token 续期
   *        都能自动跟上，不需要自己存 token。
   * @param {{fetchImpl?: typeof fetch, logger?: (message: string) => void}} [options]
   */
  constructor(options) {
    this.base = String(options.variant.chatBase).replace(/\/+$/u, "");
    this.credential = options.credential;
    this.fetchImpl = options.fetchImpl ?? globalThis.fetch;
    this.log = options.logger ?? (() => {});
  }

  /**
   * 起一次 POST。凭据在**发送前**才取，所以续期后的 token 立刻生效。
   *
   * @returns {Promise<object>} 已校验的业务文档
   */
  async post(path, body, timeoutMs) {
    let credential;
    try {
      credential = await this.credential();
    } catch (error) {
      throw new SearchError("AUTH_MISSING_TOKEN", `拿不到 WorkBuddy 凭据：${String(error?.message ?? error)}`);
    }
    const accessToken = typeof credential?.accessToken === "string" ? credential.accessToken : "";
    if (accessToken === "") {
      throw new SearchError("AUTH_MISSING_TOKEN", "当前账号没有 accessToken，请先在 WorkBuddy 桌面 App 登录");
    }

    let response;
    try {
      response = await this.fetchImpl(`${this.base}${path}`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${accessToken}`,
        },
        body: JSON.stringify(body),
        signal: AbortSignal.timeout(timeoutMs),
      });
    } catch (error) {
      const name = error?.name ?? "";
      if (name === "AbortError" || name === "TimeoutError") {
        throw new SearchError("TIMEOUT", `联网请求超时（${timeoutMs}ms）`);
      }
      throw new SearchError("NETWORK", `联网请求失败：${String(error?.message ?? error)}`);
    }
    return unwrap(response.status, await response.text());
  }

  /**
   * 联网搜索。
   *
   * @param {object} options
   * @param {string} options.query 查询词，至少 2 个字符
   * @param {number} [options.maxResults=5] 1–20
   * @param {string} [options.freshness] `d1`–`d30` / `m1`–`m12` / `y1`–`y5`
   * @param {string[]} [options.includeDomains] 只要这些域名（**本地过滤**，见模块说明）
   * @param {string[]} [options.excludeDomains] 排除这些域名（折成 query 的 `-site:`）
   * @returns {Promise<{query: string, provider: string, results: object[], totalResults: number, responseTimeMs: number}>}
   */
  async search(options = {}) {
    const query = typeof options.query === "string" ? options.query.trim() : "";
    if (query.length < 2) {
      throw new SearchError("INVALID_REQUEST", "query 不能为空且至少 2 个字符");
    }

    const freshness = options.freshness;
    if (freshness !== undefined && freshness !== null && freshness !== "" && !validFreshness(String(freshness))) {
      throw new SearchError(
        "INVALID_REQUEST",
        `freshness 格式非法（${freshness}）：应为 d1–d30 / m1–m12 / y1–y5，数字可省略（如 d 等于 d1）`,
      );
    }

    const rawMax = options.maxResults ?? 5;
    if (!Number.isInteger(rawMax) || rawMax < 1 || rawMax > MAX_RESULTS_LIMIT) {
      throw new SearchError("INVALID_REQUEST", `maxResults 必须是 1–${MAX_RESULTS_LIMIT} 的整数`);
    }

    const include = normalizeDomainList(options.includeDomains);
    // 有本地过滤就多取一些，避免筛完不够数（上游最多给 MAX_RESULTS_LIMIT）。
    const fetchCount = include.length > 0 ? MAX_RESULTS_LIMIT : rawMax;

    const document = await this.post(
      SEARCH_PATH,
      {
        query: buildQuery(query, options.excludeDomains),
        type: "text2text",
        max_results: fetchCount,
        ...(freshness === undefined || freshness === null || freshness === "" ? {} : { freshness: String(freshness) }),
      },
      options.timeoutMs ?? SEARCH_TIMEOUT_MS,
    );

    let results = Array.isArray(document["results"]) ? document["results"] : [];
    if (include.length > 0) {
      results = results.filter((item) => {
        const host = hostOf(item?.url ?? "");
        return host !== "" && include.some((domain) => domainMatches(host, domain));
      });
    }
    const limited = results.slice(0, rawMax);

    return {
      query,
      provider: String(document["provider"] ?? ""),
      totalResults: limited.length,
      responseTimeMs: Number(document["response_time_ms"] ?? 0),
      results: limited.map((item) => ({
        title: String(item?.title ?? ""),
        url: String(item?.url ?? ""),
        snippet: String(item?.snippet ?? ""),
        ...(item?.site ? { site: String(item.site) } : {}),
      })),
    };
  }

  /**
   * 抓取一个网页，拿回正文（默认 markdown）。
   *
   * @param {object} options
   * @param {string} options.url 必填，http/https
   * @param {string} [options.prompt] 让上游按这个意图提取（不清洗就没有重点）
   * @param {string} [options.format="markdown"]
   * @param {number} [options.timeoutSec=20] 上游抓取自己的超时（秒）
   * @param {number} [options.maxLength] 正文上限
   */
  async fetchPage(options = {}) {
    const url = typeof options.url === "string" ? options.url.trim() : "";
    if (url === "") throw new SearchError("INVALID_REQUEST", "url 不能为空");
    let parsedUrl;
    try {
      parsedUrl = new URL(url);
    } catch {
      throw new SearchError("INVALID_REQUEST", `url 不是合法地址：${url}`);
    }
    if (parsedUrl.protocol !== "http:" && parsedUrl.protocol !== "https:") {
      throw new SearchError("INVALID_REQUEST", `url 只支持 http/https，收到 ${parsedUrl.protocol}`);
    }

    const maxLength = Number.isInteger(options.maxLength) && options.maxLength > 0
      ? Math.min(options.maxLength, MAX_FETCH_LENGTH)
      : MAX_FETCH_LENGTH;

    const document = await this.post(
      FETCH_PATH,
      {
        url,
        format: typeof options.format === "string" && options.format !== "" ? options.format : "markdown",
        prompt: typeof options.prompt === "string" && options.prompt.trim() !== ""
          ? options.prompt.trim()
          : "Summarize this page.",
        timeout: Number.isFinite(options.timeoutSec) && options.timeoutSec > 0 ? options.timeoutSec : 20,
        max_length: maxLength,
      },
      options.requestTimeoutMs ?? FETCH_TIMEOUT_MS,
    );

    return {
      url: String(document["url"] ?? url),
      title: String(document["title"] ?? ""),
      content: String(document["content"] ?? ""),
      contentType: String(document["content_type"] ?? ""),
      contentLength: Number(document["content_length"] ?? 0),
      truncated: document["truncated"] === true,
      statusCode: Number(document["status_code"] ?? 0),
    };
  }
}

/**
 * 把搜索结果格式化成给模型看的 markdown。
 *
 * 客户端（尤其 CLI/harness）拿到的是一段可直接塞进上下文的文本，
 * 而不是要它自己拼 JSON。
 */
export function formatSearchResults(result) {
  const { query, provider, results } = result;
  if (!Array.isArray(results) || results.length === 0) {
    return `No results found for query: "${query}"`;
  }
  const body = results
    .map((item, index) => {
      const meta = [item.site].filter(Boolean).join(" · ");
      return [
        `## ${index + 1}. [${item.title || item.url}](${item.url})`,
        "",
        item.snippet || "(no snippet)",
        "",
        `**URL:** ${item.url}${meta ? `\n\n**Site:** ${meta}` : ""}`,
        "",
        "---",
      ].join("\n");
    })
    .join("\n\n");
  const tail = `*Found ${results.length} result${results.length === 1 ? "" : "s"} • Provider: ${provider}*`;
  return `# Search Results for "${query}"\n\n${body}\n\n${tail}`;
}
