/**
 * WorkBuddy 5.6.x at-rest credential protection.
 *
 * Since WorkBuddy 5.6 the desktop app encrypts `auth.accessToken` and
 * `auth.refreshToken` at rest, so the auth file holds
 * `{$wbEncrypted: 1, envelope: "<base64>"}` wrappers instead of token strings.
 * Everything needed to open them lives on the same machine:
 *
 * - the sealed payload (`{version: 1, atRestSecretKey, …}`) comes from the
 *   WorkBuddy-modified Electron's private `workbuddyStorage` binding, reached by
 *   running *its own* binary once with `ELECTRON_RUN_AS_NODE=1`;
 * - `protectorKey = sha256(atRestSecretKey, utf8)` opens the envelopes with
 *   AES-256-GCM under a fixed associated-data framing (`WB-AAD\0` + WBEV1).
 *
 * This module never logs, caches to disk, or returns the payload or the key
 * beyond what the caller needs: `protectorKeyFor` hands back a Buffer, and
 * error messages carry sizes, ids, and exit codes only.
 *
 * @module workbuddy-api/desktop-credential-protection
 */

import { execFile, execFileSync } from "node:child_process";
import { createDecipheriv, createHash } from "node:crypto";
import { accessSync, constants } from "node:fs";
import { join } from "node:path";

/** Env variable that overrides the WorkBuddy binary used as the key helper. */
export const WORKBUDDY_ELECTRON_BIN_ENV = "WORKBUDDY_ELECTRON_BIN";

/**
 * The fields the app encrypts.
 *
 * Only `accessToken` and `refreshToken` are needed for authorization, so those
 * are the only ones unwrapped. WorkBuddy 5.6 also encrypts the display-only
 * `account.nickname` and `account.phoneNumber`; leaving them wrapped is
 * deliberate — they are not required to make a request, and decrypting them
 * would widen this module's job from "read the credential" to "read the
 * account's personal data". A caller that wants the nickname should ask the
 * billing/console endpoint instead.
 */
const AUTH_FIELDS = ["accessToken", "refreshToken"];

/** The helper script: print the sealed payload and nothing else. */
const HELPER_SCRIPT =
  'process.stdout.write(String(process._linkedBinding("electron_browser_workbuddy_storage").loggerGet()))';

/** Argv flag the Electron binary accepts for a one-shot script. */
const HELPER_SCRIPT_ARGUMENT_FLAG = "-e";

/** How long one helper spawn may take. */
const HELPER_TIMEOUT_MS = 10_000;

/** Cap the helper's stdout; the payload is a few KB. */
const HELPER_MAX_OUTPUT_BYTES = 1_048_576;

/**
 * A failure the caller can classify rather than string-match.
 *
 * @property {string} code one of `electron-binary-unavailable`,
 *   `electron-path-invalid`, or `encrypted-credential-unreadable`.
 */
export class WorkBuddyKeyError extends Error {
  constructor(code, message) {
    super(message);
    this.name = "WorkBuddyKeyError";
    this.code = code;
  }
}

/**
 * Whether a path exists and is executable; never throws.
 *
 * On Windows `X_OK` is not meaningful (there is no execute bit), so an existing
 * regular file counts as executable there.
 */
function isExecutable(path) {
  try {
    accessSync(path, constants.X_OK);
    return true;
  } catch {
    return process.platform === "win32" && isFile(path);
  }
}

function isFile(path) {
  try {
    accessSync(path, constants.F_OK);
    return true;
  } catch {
    return false;
  }
}

/**
 * Candidate locations of the WorkBuddy desktop binary, in probe order.
 *
 * macOS is the only platform whose layout the upstream plugin verifies, so it
 * keeps a fixed default. Windows has no single install root (the app can live on
 * any drive), so the registry's uninstall entry is consulted — that is where the
 * real path was found — followed by the usual per-user and machine-wide roots.
 * Linux returns nothing: no layout has been verified, and guessing would spawn
 * the wrong binary.
 *
 * @returns {Promise<string[]>} absolute candidate paths, most likely first
 */
export async function defaultElectronPaths() {
  if (process.platform === "darwin") {
    return ["/Applications/WorkBuddy.app/Contents/MacOS/Electron"];
  }
  if (process.platform !== "win32") return [];

  const candidates = [];

  // The registry's UninstallString names the real install root even when the app
  // was installed to a non-standard drive, which DisplayName alone cannot show.
  const uninstall = readRegistryUninstallString("WorkBuddy");
  if (uninstall !== undefined) {
    const quoted = /^"([^"]+)"/u.exec(uninstall.trim())?.[1];
    const bare = quoted ?? uninstall.trim().split(/\s+/u)[0];
    if (bare !== undefined && bare !== "") candidates.push(join(dirnameOf(bare), "WorkBuddy.exe"));
  }

  const roots = [process.env["LOCALAPPDATA"], process.env["PROGRAMFILES"], process.env["PROGRAMFILES(X86)"]];
  for (const root of roots) {
    if (root === undefined || root === "") continue;
    candidates.push(join(root, "Programs", "WorkBuddy", "WorkBuddy.exe"));
    candidates.push(join(root, "WorkBuddy", "WorkBuddy.exe"));
  }

  return [...new Set(candidates)];
}

/** dirname without importing node:path's platform-specific helper twice. */
function dirnameOf(path) {
  const index = Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\"));
  return index === -1 ? "." : path.slice(0, index);
}

/**
 * Read an uninstall entry's `UninstallString` from the Windows registry.
 *
 * Uses `reg query` rather than a native module so the package stays
 * dependency-free. The uninstall string is preferred over `InstallLocation`
 * because the latter is commonly empty for per-user Electron installs.
 */
function readRegistryUninstallString(displayNameFragment) {
  const roots = [
    "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
    "HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
    "HKLM\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
  ];
  for (const root of roots) {
    let output;
    try {
      output = readRegistryValue("reg", ["query", root, "/s", "/f", displayNameFragment]);
    } catch {
      continue;
    }
    if (output === undefined) continue;
    // `reg query /f` matches any value; confirm the DisplayName really is the app
    // before trusting an UninstallString from the same block.
    const display = /DisplayName\s+REG_SZ\s+WorkBuddy\b/u.test(output);
    if (!display) continue;
    const match = /UninstallString\s+REG_SZ\s+(.+)/u.exec(output)?.[1]?.trim();
    if (match !== undefined && match !== "") return match;
  }
  return undefined;
}

/** Run a command synchronously without leaking stderr into our own output. */
function readRegistryValue(command, args) {
  try {
    return execFileSync(command, args, {
      encoding: "utf8",
      windowsHide: true,
      stdio: ["ignore", "pipe", "ignore"],
      timeout: 5_000,
      maxBuffer: 1_048_576,
    });
  } catch {
    return undefined;
  }
}

/** Decode a base64 value and check its exact byte length when given. */
function parseBase64(value, length) {
  if (typeof value !== "string" || value === "") return undefined;
  let decoded;
  try {
    decoded = Buffer.from(value, "base64");
  } catch {
    return undefined;
  }
  // Reject non-canonical base64: re-encoding must reproduce the input.
  if (decoded.length === 0 || decoded.toString("base64").replace(/=+$/u, "") !== value.replace(/=+$/u, "")) {
    return undefined;
  }
  return length === undefined || decoded.length === length ? decoded : undefined;
}

/**
 * Parse the 5.6 field wrapper into a usable envelope.
 *
 * The wrapper is `{$wbEncrypted: 1, envelope: <base64 of a JSON
 * {suite, keyId, nonce, authTag, ciphertext}>}`. Anything claiming the flag
 * whose envelope cannot be decoded makes the whole document unrecognized rather
 * than encrypted, because no key could ever open it.
 */
function parseWrappedField(field, value) {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return undefined;
  if (value["$wbEncrypted"] !== 1 || typeof value["envelope"] !== "string") return undefined;
  let inner;
  try {
    inner = JSON.parse(Buffer.from(value["envelope"], "base64").toString("utf8"));
  } catch {
    return undefined;
  }
  if (typeof inner !== "object" || inner === null || Array.isArray(inner)) return undefined;
  const nonce = parseBase64(inner["nonce"], 12);
  const authTag = parseBase64(inner["authTag"], 16);
  const ciphertext = parseBase64(inner["ciphertext"]);
  if (nonce === undefined || authTag === undefined || ciphertext === undefined) return undefined;
  if (!Number.isInteger(inner["suite"]) || inner["suite"] !== 1) return undefined;
  if (typeof inner["keyId"] !== "string" || !/^[0-9a-f]{16}$/u.test(inner["keyId"])) return undefined;
  return {
    field,
    envelope: { suite: inner["suite"], keyId: inner["keyId"], nonce, authTag, ciphertext },
  };
}

/**
 * Classify a desktop auth document's on-disk format.
 *
 * - `absent` — an empty file.
 * - `plaintext` — any document the regular parser could read (even one without
 *   a token), i.e. pre-5.6.
 * - `encrypted` — at least one field is in a decodable wrapper.
 * - `unrecognized` — unparsable JSON, a non-object, or a wrapper that will not
 *   decode.
 *
 * @returns {{format: string, wrapped?: {document: object, fields: object[]}}}
 */
export function classifyDesktopAuthDocument(text) {
  if (typeof text !== "string" || text.trim() === "") return { format: "absent" };
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    return { format: "unrecognized" };
  }
  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) return { format: "unrecognized" };
  const auth = typeof parsed["auth"] === "object" && parsed["auth"] !== null ? parsed["auth"] : parsed;
  const fields = [];
  for (const field of AUTH_FIELDS) {
    const value = auth[field];
    // A plaintext string is the pre-5.6 shape: fine, nothing to unwrap.
    if (typeof value === "string") continue;
    const wrapped = parseWrappedField(field, value);
    if (wrapped === undefined && value !== undefined) return { format: "unrecognized" };
    if (wrapped !== undefined) fields.push(wrapped);
  }
  if (fields.length === 0) return { format: "plaintext" };
  return { format: "encrypted", wrapped: { document: parsed, fields } };
}

/** Distinct key ids across the wrapped fields, in field order. */
function keyIdsOf(fields) {
  return [...new Set(fields.map((wrapped) => wrapped.envelope.keyId))];
}

/** Validate the helper's payload against the app's own rules. */
function parseAtRestPayload(text) {
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    return undefined;
  }
  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) return undefined;
  if (parsed["version"] !== 1) return undefined;
  const secret = parsed["atRestSecretKey"];
  if (typeof secret !== "string" || secret === "") return undefined;
  const decoded = parseBase64(secret, 32);
  if (decoded === undefined) return undefined;
  if (decoded.every((byte) => byte === 0)) return undefined;
  return { atRestSecretKey: secret };
}

/** Derive the protector key from the payload's secret (sha256 over its UTF-8 string). */
function deriveProtectorKey(secret) {
  return createHash("sha256").update(secret, "utf8").digest();
}

/**
 * The authenticated-context AAD for one field envelope, transcribed from the app
 * bundle's `buildAuthenticatedContextAad`.
 *
 * Credential fields are always suite 1 under the `field` framing (WBEV1); the
 * framing family's other members belong to other document kinds and are
 * deliberately not implemented — opening a field is not a place to guess at
 * future formats.
 */
function buildAuthenticatedContextAad(keyId, suite) {
  const prefix = Buffer.from("WB-AAD\0", "ascii");
  const lengthPrefixed = (value) => {
    const bytes = Buffer.from(value, "utf8");
    const header = Buffer.allocUnsafe(4);
    header.writeUInt32BE(bytes.length);
    return Buffer.concat([header, bytes]);
  };
  const suiteBytes = Buffer.allocUnsafe(4);
  suiteBytes.writeUInt32BE(suite);
  return Buffer.concat([
    prefix,
    Buffer.from([1]),
    lengthPrefixed("WBEV1"),
    lengthPrefixed("sym-v1"),
    suiteBytes,
    lengthPrefixed(keyId),
    Buffer.from([2]),
    Buffer.from([0]),
    Buffer.from([0]),
  ]);
}

/**
 * Open one envelope with a protector key; `undefined` when it will not open.
 *
 * A failure means "not this format / wrong key" and is reported as such rather
 * than retried against other framings.
 */
function openAuthField(key, envelope) {
  try {
    const decipher = createDecipheriv("aes-256-gcm", key, envelope.nonce, { authTagLength: 16 });
    decipher.setAAD(buildAuthenticatedContextAad(envelope.keyId, envelope.suite));
    decipher.setAuthTag(envelope.authTag);
    return Buffer.concat([decipher.update(envelope.ciphertext), decipher.final()]).toString("utf8");
  } catch {
    return undefined;
  }
}

/**
 * Spawn the WorkBuddy binary as a Node helper and return its sealed payload.
 *
 * @param {string} binaryPath
 * @returns {Promise<string>} the raw stdout payload
 * @throws {WorkBuddyKeyError} when the helper cannot run or prints nothing
 */
function spawnKeyHelper(binaryPath) {
  return new Promise((resolve, reject) => {
    execFile(
      binaryPath,
      [HELPER_SCRIPT_ARGUMENT_FLAG, HELPER_SCRIPT],
      {
        timeout: HELPER_TIMEOUT_MS,
        maxBuffer: HELPER_MAX_OUTPUT_BYTES,
        windowsHide: true,
        env: { ...process.env, ELECTRON_RUN_AS_NODE: "1" },
      },
      (error, stdout) => {
        if (error !== null && error !== undefined) {
          const why =
            error.killed === true
              ? `timed out or was killed after ${HELPER_TIMEOUT_MS}ms`
              : error.code !== undefined
                ? `exited with code ${String(error.code)}`
                : "could not be started";
          reject(new WorkBuddyKeyError("encrypted-credential-unreadable", `the WorkBuddy key helper (${binaryPath}) ${why}`));
          return;
        }
        const output = String(stdout).trim();
        if (output === "") {
          reject(
            new WorkBuddyKeyError(
              "encrypted-credential-unreadable",
              `the WorkBuddy key helper (${binaryPath}) produced no payload`,
            ),
          );
          return;
        }
        resolve(output);
      },
    );
  });
}

/**
 * Resolve the binary to use as the key helper.
 *
 * Order is the contract: an explicit path (option or `WORKBUDDY_ELECTRON_BIN`)
 * is used as-is and never falls back; a binary that turns out to be unusable is
 * an error, never a reason to look for a different app. Otherwise the platform
 * defaults are probed in order.
 *
 * @param {{electronPath?: string}} [options]
 * @returns {Promise<string>}
 * @throws {WorkBuddyKeyError} with code `electron-path-invalid` or
 *   `electron-binary-unavailable`
 */
export async function resolveKeyHelperPath(options = {}) {
  const fromEnv = process.env[WORKBUDDY_ELECTRON_BIN_ENV]?.trim();
  const explicit = options.electronPath ?? (fromEnv === undefined || fromEnv === "" ? undefined : fromEnv);
  if (explicit !== undefined) {
    if (!isExecutable(explicit)) {
      throw new WorkBuddyKeyError(
        "electron-path-invalid",
        `the configured WorkBuddy binary is not available at ${explicit}; check ${WORKBUDDY_ELECTRON_BIN_ENV} or unset it to let this tool look for the app itself`,
      );
    }
    return explicit;
  }
  for (const candidate of await defaultElectronPaths()) {
    if (isExecutable(candidate)) return candidate;
  }
  throw new WorkBuddyKeyError(
    "electron-binary-unavailable",
    `no WorkBuddy desktop binary was found; set ${WORKBUDDY_ELECTRON_BIN_ENV} to the app's Electron binary`,
  );
}

/**
 * In-memory protector-key resolver: one spawn per key id, single-flight, never
 * persisted.
 *
 * The cache is keyed by the id the envelopes ask for, so an envelope sealed
 * under a rotated key triggers exactly one fresh resolution.
 */
export class AtRestKeyProvider {
  /** @param {{electronPath?: string, spawnHelper?: (path: string) => Promise<string>}} [options] */
  constructor(options = {}) {
    this.electronPath = options.electronPath;
    this.spawnHelper = options.spawnHelper ?? spawnKeyHelper;
    this.cache = undefined;
    this.inflight = undefined;
    /** The binary path last used successfully, for diagnostics. */
    this.helperPathUsed = undefined;
  }

  /**
   * A protector key matching one of the requested envelope key ids.
   *
   * @param {string[]} requested key ids the envelopes name
   * @returns {Promise<Buffer>} the 32-byte AES-256-GCM key
   * @throws {WorkBuddyKeyError} when no reachable key opens these envelopes
   */
  async protectorKeyFor(requested) {
    if (requested.length === 0) {
      throw new WorkBuddyKeyError("encrypted-credential-unreadable", "encrypted desktop credential carries no key ids");
    }
    const cached = this.cache;
    if (cached !== undefined && requested.includes(cached.keyId)) return cached.key;
    this.inflight ??= this.resolve().finally(() => {
      this.inflight = undefined;
    });
    const resolved = await this.inflight;
    if (!requested.includes(resolved.keyId)) {
      throw new WorkBuddyKeyError(
        "encrypted-credential-unreadable",
        `WorkBuddy's current at-rest key (id ${resolved.keyId}) does not match the credential's envelope (id ${requested.join(" or ")}); ` +
          "the desktop credential was sealed by a different WorkBuddy installation",
      );
    }
    return resolved.key;
  }

  /** Spawn the helper and derive the key. */
  async resolve() {
    const binaryPath = await resolveKeyHelperPath(
      this.electronPath === undefined ? {} : { electronPath: this.electronPath },
    );
    const payload = parseAtRestPayload(await this.spawnHelper(binaryPath));
    if (payload === undefined) {
      throw new WorkBuddyKeyError(
        "encrypted-credential-unreadable",
        "WorkBuddy key helper returned an unusable at-rest payload (expected {version:1, atRestSecretKey})",
      );
    }
    const key = deriveProtectorKey(payload.atRestSecretKey);
    const resolved = { key, keyId: createHash("sha256").update(key).digest("hex").slice(0, 16) };
    this.cache = resolved;
    this.helperPathUsed = binaryPath;
    return resolved;
  }
}

/**
 * Decrypt a wrapped document into the plaintext text the regular parser reads.
 *
 * @param {{wrapped: {document: object, fields: object[]}}} classification
 * @param {AtRestKeyProvider} provider
 * @returns {Promise<string>} JSON text with the wrapped fields replaced
 * @throws {WorkBuddyKeyError} naming the field and key ids — never envelope or
 *   token content — when any wrapped field cannot be opened
 */
export async function unwrapDesktopAuthDocument(classification, provider) {
  const wrapped = classification.wrapped;
  const key = await provider.protectorKeyFor(keyIdsOf(wrapped.fields));
  const rebuilt = structuredClone(wrapped.document);
  const auth = typeof rebuilt["auth"] === "object" && rebuilt["auth"] !== null ? rebuilt["auth"] : rebuilt;
  for (const field of wrapped.fields) {
    const plaintext = openAuthField(key, field.envelope);
    if (plaintext === undefined) {
      throw new WorkBuddyKeyError(
        "encrypted-credential-unreadable",
        `the desktop credential's ${field.field} field could not be decrypted with WorkBuddy's at-rest key (id ${field.envelope.keyId}); ` +
          "the file may have been sealed by a different installation",
      );
    }
    auth[field.field] = plaintext;
  }
  return JSON.stringify(rebuilt);
}

/**
 * Whether a desktop auth document needs the encrypted path at all.
 *
 * A cheap pre-check so callers can avoid spawning the helper for the common
 * pre-5.6 plaintext file.
 */
export function isEncryptedDesktopAuth(text) {
  return classifyDesktopAuthDocument(text).format === "encrypted";
}
