/**
 * Filesystem locations and durable writes for workbuddy-api.
 *
 * Everything this tool owns lives in one directory (`~/.workbuddy-api` by
 * default, `WORKBUDDY_API_HOME` overrides it) so uninstalling is a single
 * `rm -rf` and nothing is ever written into the WorkBuddy desktop app's own
 * files. The desktop app's auth file is opened read-only, always.
 *
 * Zero dependencies: `node:fs`, `node:os`, `node:path` only.
 *
 * @module workbuddy-api/paths
 */

import { mkdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import { existsSync, mkdirSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";

/** Directory name under the user's home when no override is set. */
const DEFAULT_HOME_DIRNAME = ".workbuddy-api";

/**
 * The directory holding this tool's own state.
 *
 * `WORKBUDDY_API_HOME` wins when set to a non-empty value; otherwise the
 * conventional dotted directory under the user's home is used.
 *
 * @returns {string} absolute path (not guaranteed to exist)
 */
export function stateHome() {
  const override = process.env["WORKBUDDY_API_HOME"];
  if (override !== undefined && override.trim() !== "") return resolve(override.trim());
  return join(homedir(), DEFAULT_HOME_DIRNAME);
}

/** Absolute path of the config document. */
export function configPath() {
  return join(stateHome(), "config.json");
}

/** Absolute path of the log file used when running as a service. */
export function logPath() {
  return join(stateHome(), "workbuddy-api.log");
}

/** Create a directory tree if any part of it is missing. */
export async function ensureDir(path) {
  await mkdir(path, { recursive: true, mode: 0o700 });
}

/**
 * Write a file atomically: a temp sibling is written and then renamed over the
 * target, so a reader never observes a half-written credential document.
 *
 * @param {string} path
 * @param {string} contents
 * @param {{mode?: number}} [options]
 */
export async function writeFileAtomic(path, contents, options = {}) {
  const directory = dirname(path);
  if (!existsSync(directory)) mkdirSync(directory, { recursive: true, mode: 0o700 });
  const temporary = `${path}.${process.pid}.tmp`;
  await writeFile(temporary, contents, { encoding: "utf8", mode: options.mode ?? 0o600 });
  await rename(temporary, path);
}

/**
 * Read and parse a JSON document.
 *
 * @param {string} path
 * @returns {Promise<any|undefined>} the parsed value, or undefined when the
 *   file is absent, unreadable, or not valid JSON.
 */
export async function readJson(path) {
  try {
    return JSON.parse(await readFile(path, "utf8"));
  } catch (error) {
    if (error?.code === "ENOENT") return undefined;
    return undefined;
  }
}

/** Remove a file, ignoring its absence. */
export async function removeIfPresent(path) {
  await rm(path, { force: true });
}

/** Whether a filesystem error reports an absent path. */
export function isENOENT(error) {
  return error?.code === "ENOENT";
}
