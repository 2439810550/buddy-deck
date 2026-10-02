#!/usr/bin/env node
/**
 * wbsearch — 调 **workbuddy-api 网关**做联网搜索 / 抓网页。
 *
 * 与旧版（直连 WorkBuddy、自己维护 token.json）的区别：
 *
 *   旧：本脚本读 token.json → 直接打 copilot.tencent.com
 *   新：本脚本打 **本机网关** → 网关用自己的登录态去打上游
 *
 * 为什么这样更好：
 *   · 不需要再维护一份 token 文件（网关那边已经有 CredentialStore，
 *     续期与换号都自动跟上）
 *   · 凭据只存在于一个地方，不随 skill 分发
 *   · 搜索与抓取走的是同一个 base_url + api_key，其他客户端也是这套
 *
 * 零依赖，只用内置 fetch。
 *
 * 用法：
 *   node wbsearch.mjs "查询词" [--max 5] [--freshness d7] [--sites a.com] [--exclude b.com] [--json]
 *   node wbsearch.mjs --url <url> [--prompt "总结要点"] [--json]
 *
 * 网关地址与密钥的来源（按优先级）：
 *   1. 环境变量 WORKBUDDY_GATEWAY_URL / WORKBUDDY_API_KEY
 *   2. 网关的状态目录 config.json（apiKey / host / port）
 *      —— 目录默认 ~/.workbuddy-api，可用 WORKBUDDY_API_HOME 覆盖
 *   3. 内置默认 http://127.0.0.1:8787
 */

import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

/** 网关默认地址。 */
const DEFAULT_BASE = "http://127.0.0.1:8787";

/** 搜索一次的时间上限（毫秒）。 */
const SEARCH_TIMEOUT_MS = 30_000;

/** 抓网页的时间上限（毫秒）—— 整页正文，比搜索宽松。 */
const FETCH_TIMEOUT_MS = 60_000;

/** 网关状态目录（和 workbuddy-api 的 paths.js 保持一致）。 */
function stateHome() {
  const override = process.env["WORKBUDDY_API_HOME"];
  if (typeof override === "string" && override.trim() !== "") return override.trim();
  return join(homedir(), ".workbuddy-api");
}

/** 读网关自己的 config.json；读不到就给空对象。 */
function readGatewayConfig() {
  try {
    const parsed = JSON.parse(readFileSync(join(stateHome(), "config.json"), "utf8"));
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

/**
 * 解析出网关的 base URL 与 api_key。
 *
 * base 要去掉末尾斜杠，并且**带上 /v1** —— 网关的搜索端点是 `/v1/search`。
 */
export function resolveTarget(env = process.env) {
  const config = readGatewayConfig();

  const explicitUrl = typeof env["WORKBUDDY_GATEWAY_URL"] === "string" ? env["WORKBUDDY_GATEWAY_URL"].trim() : "";
  let base = explicitUrl;
  if (base === "") {
    const host = typeof config["host"] === "string" && config["host"] !== "" ? config["host"] : "127.0.0.1";
    const port = Number.isInteger(config["port"]) && config["port"] > 0 ? config["port"] : 8787;
    base = `http://${host}:${port}`;
  }
  base = base.replace(/\/+$/u, "");
  if (!/\/v1$/u.test(base)) base = `${base}/v1`;

  const fromEnv = typeof env["WORKBUDDY_API_KEY"] === "string" ? env["WORKBUDDY_API_KEY"].trim() : "";
  const key = fromEnv !== "" ? fromEnv : (typeof config["apiKey"] === "string" ? config["apiKey"].trim() : "");

  return { base, key };
}

/** 一句话说清为什么连不上/没权限，并给出下一步。 */
function explain(status, body) {
  if (status === 401) {
    return [
      "网关拒绝了这次请求（401）。",
      "原因通常是 api_key 不对或没带上。两种做法：",
      "  1) 固定一个密钥再启动网关：node src/bin.js config --set apiKey=sk-xxx",
      "     然后 serve 时就会一直用它（skill 会从 config.json 读）",
      "  2) 或者把当前密钥放进环境变量：",
      "     PowerShell:  $env:WORKBUDDY_API_KEY = \"<key>\"",
      "     bash:        export WORKBUDDY_API_KEY=<key>",
      `网关返回：${body.slice(0, 200)}`,
    ].join("\n");
  }
  if (status === 403) {
    return `网关拒绝了这次请求（403）：Host 必须是回环地址。\n网关返回：${body.slice(0, 200)}`;
  }
  return `网关返回 HTTP ${status}：${body.slice(0, 300)}`;
}

/**
 * 发一次请求到网关。
 *
 * @param {object} options
 * @param {string} options.path 网关路径，如 `/search`
 * @param {object} options.body 请求体
 * @param {number} options.timeoutMs
 */
async function callGateway(options) {
  const { base, key } = resolveTarget();
  const url = `${base}${options.path}`;

  let response;
  try {
    response = await fetch(url, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        ...(key === "" ? {} : { Authorization: `Bearer ${key}` }),
      },
      body: JSON.stringify(options.body),
      signal: AbortSignal.timeout(options.timeoutMs),
    });
  } catch (error) {
    const name = error?.name ?? "";
    if (name === "AbortError" || name === "TimeoutError") {
      throw new Error(`请求网关超时（${options.timeoutMs}ms）：${url}`);
    }
    throw new Error(
      `连不上本机网关 ${url}\n` +
        "请先启动它： node src/bin.js serve\n" +
        `（原始错误：${error?.message ?? error}）`,
    );
  }

  const text = await response.text();
  if (!response.ok) throw new Error(explain(response.status, text));
  return text;
}

/** 搜索，返回 markdown 文本；`asJson` 时返回结构化 JSON 文本。 */
export async function search(query, options = {}) {
  return await callGateway({
    path: "/search",
    timeoutMs: options.timeoutMs ?? SEARCH_TIMEOUT_MS,
    body: {
      query,
      format: options.asJson === true ? "json" : "markdown",
      ...(options.max === undefined ? {} : { max_results: options.max }),
      ...(options.freshness ? { freshness: options.freshness } : {}),
      ...(options.sites?.length ? { include_domains: options.sites } : {}),
      ...(options.exclude?.length ? { exclude_domains: options.exclude } : {}),
    },
  });
}

/** 抓网页，返回 markdown 正文；`asJson` 时返回结构化 JSON 文本。 */
export async function fetchPage(url, options = {}) {
  return await callGateway({
    path: "/webfetch",
    timeoutMs: options.timeoutMs ?? FETCH_TIMEOUT_MS,
    body: {
      url,
      format: options.asJson === true ? "json" : "markdown",
      ...(options.prompt ? { prompt: options.prompt } : {}),
    },
  });
}

// ────────────────────────────── CLI ──────────────────────────────

/** 解析 `--k v` 形式；开关类参数收成 true。 */
function parseArgv(argv) {
  const positional = [];
  const flags = new Map();
  for (let index = 0; index < argv.length; index += 1) {
    const item = argv[index];
    if (!item.startsWith("--")) {
      positional.push(item);
      continue;
    }
    const key = item.slice(2);
    const next = argv[index + 1];
    if (next === undefined || next.startsWith("--")) {
      flags.set(key, true);
    } else {
      flags.set(key, next);
      index += 1;
    }
  }
  return { positional, flags };
}

function main() {
  const { positional, flags } = parseArgv(process.argv.slice(2));
  const json = flags.has("json");
  const value = (name) => {
    const item = flags.get(name);
    return item === undefined || item === true ? undefined : String(item);
  };
  const list = (name) => {
    const item = value(name);
    return item === undefined ? undefined : item.split(",").map((part) => part.trim()).filter(Boolean);
  };

  const url = value("url");
  const query = positional.join(" ").trim();

  if (url === undefined && query === "") {
    process.stderr.write(
      '用法: node wbsearch.mjs "查询词" [--max 5] [--freshness d7] [--sites a.com] [--exclude b.com] [--json]\n' +
        '      node wbsearch.mjs --url <url> [--prompt "总结要点"] [--json]\n',
    );
    process.exitCode = 1;
    return;
  }

  const task = url !== undefined
    ? fetchPage(url, { prompt: value("prompt"), asJson: json })
    : search(query, {
        asJson: json,
        max: value("max") === undefined ? undefined : Number(value("max")),
        freshness: value("freshness"),
        sites: list("sites"),
        exclude: list("exclude"),
      });

  task
    .then((output) => {
      // 网关回 markdown 时直接透传；--json 由网关按 format 返回 JSON。
      process.stdout.write(output.endsWith("\n") ? output : `${output}\n`);
    })
    .catch((error) => {
      process.stderr.write(`${error.message}\n`);
      process.exitCode = 1;
    });
}

// 只在被直接运行时走 CLI（用 realpath 比对，避免相对/绝对路径写法差异）。
if (process.argv[1] !== undefined) {
  const { pathToFileURL } = await import("node:url");
  const { realpathSync } = await import("node:fs");
  try {
    if (pathToFileURL(realpathSync(process.argv[1])).href === import.meta.url) main();
  } catch {
    // realpath 失败（文件刚被删等）就当不是直接运行
  }
}
