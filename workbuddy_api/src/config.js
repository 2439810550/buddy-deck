/**
 * Persisted configuration for workbuddy-api.
 *
 * Deliberately tiny: an API key for the local gateway, a default provider, an
 * optional explicit desktop auth-file path, and gateway bind defaults. Every
 * value can be overridden by a flag or an environment variable, and nothing
 * here is required — the tool works with no config file at all.
 *
 * @module workbuddy-api/config
 */

import { configPath, ensureDir, readJson, writeFileAtomic } from "./paths.js";
import { dirname } from "node:path";

/** Shape this reader accepts. */
const CONFIG_VERSION = 1;

/** Defaults applied when the file or a field is absent. */
const DEFAULTS = {
  provider: "workbuddy",
  host: "127.0.0.1",
  port: 8787,
  apiKey: "",
  authFile: "",
  authFileAI: "",
  defaultModel: "",
};

/**
 * Read the config document, applying defaults.
 *
 * @returns {Promise<{version: number, provider: string, host: string, port: number, apiKey: string, authFile: string, authFileAI: string, defaultModel: string}>}
 */
export async function loadConfig() {
  const document = await readJson(configPath());
  const source = typeof document === "object" && document !== null && !Array.isArray(document) ? document : {};
  return {
    version: CONFIG_VERSION,
    provider: typeof source["provider"] === "string" && source["provider"] !== "" ? source["provider"] : DEFAULTS.provider,
    host: typeof source["host"] === "string" && source["host"] !== "" ? source["host"] : DEFAULTS.host,
    port:
      typeof source["port"] === "number" && Number.isInteger(source["port"]) && source["port"] >= 0
        ? source["port"]
        : DEFAULTS.port,
    apiKey: typeof source["apiKey"] === "string" ? source["apiKey"] : DEFAULTS.apiKey,
    authFile: typeof source["authFile"] === "string" ? source["authFile"] : DEFAULTS.authFile,
    authFileAI: typeof source["authFileAI"] === "string" ? source["authFileAI"] : DEFAULTS.authFileAI,
    // Claude Code 报的是 claude-*，Codex 报的是 gpt-*，都不在花名册里。
    // 这个值就是它们被换成的那个模型；留空则自动选（优先免费的）。
    defaultModel:
      typeof source["defaultModel"] === "string" ? source["defaultModel"] : DEFAULTS.defaultModel,
  };
}

/**
 * Merge a partial update into the config document and persist it.
 *
 * @param {object} patch fields to set
 * @returns {Promise<object>} the new full config
 */
export async function saveConfig(patch) {
  const current = await loadConfig();
  const next = { ...current, ...patch, version: CONFIG_VERSION };
  await ensureDir(dirname(configPath()));
  await writeFileAtomic(configPath(), `${JSON.stringify(next, null, 2)}\n`, { mode: 0o600 });
  return next;
}

/** Where the config document lives, for `status` and `doctor`. */
export { configPath };
