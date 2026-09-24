/**
 * WorkBuddy credential resolution.
 *
 * The primary source is the WorkBuddy desktop app's own auth file, opened
 * read-only. A tool-owned copy holds token refreshes so the desktop file is
 * never modified. The effective credential is whichever of the two expires
 * later, so a refresh performed by either side wins.
 *
 * Two on-disk generations are supported transparently:
 *
 * - **pre-5.6 (plaintext)** — `auth.accessToken` is the token string itself.
 * - **5.6+ (encrypted)** — the token fields are `{$wbEncrypted: 1, envelope}`
 *   wrappers, opened with a key derived from the app's own at-rest secret. See
 *   `desktop-credential-protection.js`; the decryption is delegated there so
 *   this module stays unaware of the wire format.
 *
 * This is a dependency-free re-implementation of the same credential model the
 * DSH plugin uses, with the Harness-specific state directory swapped for this
 * tool's own home. The on-disk shapes are otherwise identical so an existing
 * plugin-owned copy can be adopted in place.
 *
 * @module workbuddy-api/auth
 */

import { readFile, rm, stat } from "node:fs/promises";
import { homedir, release } from "node:os";
import { basename, dirname, join } from "node:path";
import { ensureDir, isENOENT, readJson, removeIfPresent, stateHome, writeFileAtomic } from "./paths.js";
import {
  AtRestKeyProvider,
  classifyDesktopAuthDocument,
  unwrapDesktopAuthDocument,
  WorkBuddyKeyError,
} from "./desktop-credential-protection.js";

/** Current on-disk format of the tool-owned copy; readers reject others. */
const OWN_FORMAT_VERSION = 1;

/** Tool-owned copy path for one variant. */
export function ownAuthPath(variant) {
  return join(stateHome(), variant.ownFilename);
}

/** Directory (relative to an AppData root) that holds both apps' auth files. */
const DESKTOP_AUTH_RELATIVE_PATH = [
  "CodeBuddyExtension",
  "Data",
  "Public",
  "auth",
];

/** Whether this Linux process is running inside Windows Subsystem for Linux. */
function isWsl() {
  if (process.platform !== "linux") return false;
  if (process.env["WSL_DISTRO_NAME"] !== undefined || process.env["WSL_INTEROP"] !== undefined) return true;
  return release().toLowerCase().includes("microsoft");
}

/** Convert a Windows drive path to WSL's conventional `/mnt/<drive>` form. */
function windowsPathForWsl(value) {
  const path = value?.trim();
  if (!path) return undefined;
  if (path.startsWith("/")) return path;
  const drivePath = /^([a-z]):[\\/](.*)$/iu.exec(path);
  if (drivePath === null) return undefined;
  return join("/mnt", drivePath[1].toLowerCase(), ...drivePath[2].split(/[\\/]+/u));
}

/** Windows desktop credential candidates visible from a WSL process. */
function wslDesktopAuthCandidates(home) {
  const profile = windowsPathForWsl(process.env["USERPROFILE"]) ?? join("/mnt/c/Users", basename(home));
  const localAppData = windowsPathForWsl(process.env["LOCALAPPDATA"]) ?? join(profile, "AppData", "Local");
  const roamingAppData = windowsPathForWsl(process.env["APPDATA"]) ?? join(profile, "AppData", "Roaming");
  return [
    join(localAppData, ...DESKTOP_AUTH_RELATIVE_PATH),
    join(roamingAppData, ...DESKTOP_AUTH_RELATIVE_PATH),
  ];
}

/**
 * Platform-default candidates for the shared desktop auth *directory*, in probe
 * order. Windows probes both AppData roots: current builds write under
 * `%LOCALAPPDATA%` (Local), older ones under `%APPDATA%` (Roaming). WSL probes
 * those same Windows locations through its mounted profile before the native
 * Linux locations.
 *
 * Linux honors the XDG Base Directory spec: `$XDG_CONFIG_HOME` (falling back to
 * `~/.config`) is probed first, then `$XDG_DATA_HOME` (falling back to
 * `~/.local/share`), because Electron builds vary in which of the two they use
 * for user data. Duplicates are dropped so a candidate is never probed twice.
 */
function defaultDesktopAuthDirs() {
  const home = homedir();
  if (process.platform === "darwin") {
    return [join(home, "Library", "Application Support", ...DESKTOP_AUTH_RELATIVE_PATH)];
  }
  if (process.platform === "win32") {
    return [
      join(home, "AppData", "Local", ...DESKTOP_AUTH_RELATIVE_PATH),
      join(home, "AppData", "Roaming", ...DESKTOP_AUTH_RELATIVE_PATH),
    ];
  }
  if (process.platform === "linux") {
    const configHome = xdgBase("XDG_CONFIG_HOME", join(home, ".config"));
    const dataHome = xdgBase("XDG_DATA_HOME", join(home, ".local", "share"));
    const linux = dedupeCandidates([
      join(configHome, ...DESKTOP_AUTH_RELATIVE_PATH),
      join(dataHome, ...DESKTOP_AUTH_RELATIVE_PATH),
    ]);
    return isWsl() ? dedupeCandidates([...wslDesktopAuthCandidates(home), ...linux]) : linux;
  }
  return [];
}

/**
 * The XDG base directory for one env variable, or its platform default.
 *
 * Only an absolute path is accepted: an XDG variable that is empty or relative
 * is ignored rather than resolved against the current directory.
 */
function xdgBase(envName, fallback) {
  const value = process.env[envName]?.trim();
  if (value !== undefined && value !== "" && value.startsWith("/")) return value;
  return fallback;
}

/** Drop duplicate candidates while keeping probe order. */
function dedupeCandidates(candidates) {
  return [...new Set(candidates)];
}

/**
 * The desktop auth-file candidates for one variant, in probe order.
 *
 * Both apps write into the same shared `CodeBuddyExtension` directory and
 * differ only in the file's basename, so the per-platform ordering is reused
 * verbatim and just the filename is swapped.
 */
export function desktopAuthCandidates(variant) {
  return dedupeCandidates(defaultDesktopAuthDirs().map((dir) => join(dir, variant.desktopFilename)));
}

/** Normalize an expiry that may arrive in seconds or milliseconds. */
function expiryToMs(value) {
  if (typeof value !== "number" || !Number.isFinite(value) || value <= 0) return 0;
  // Anything above ~year 12000 in ms is really seconds; the upstream is
  // inconsistent between the two, so the magnitude decides.
  return value > 0xe8d4a51000 ? value : value * 1e3;
}

function optionalString(value) {
  return typeof value === "string" && value !== "" ? value : undefined;
}

/**
 * Parse a WorkBuddy auth document in either on-disk shape: the nested
 * `{"auth":{...},"account":{...}}` form and the flat form.
 *
 * @returns {object|undefined} a credential, or undefined when the document
 *   carries no access token.
 */
export function parseWorkBuddyAuth(text) {
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    return undefined;
  }
  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) return undefined;
  let auth;
  let identity;
  if (typeof parsed["auth"] === "object" && parsed["auth"] !== null) {
    auth = parsed["auth"];
    identity = typeof parsed["account"] === "object" && parsed["account"] !== null ? parsed["account"] : {};
  } else {
    auth = parsed;
    identity = parsed;
  }
  const accessToken = typeof auth["accessToken"] === "string" ? auth["accessToken"] : "";
  if (accessToken === "") return undefined;
  const refreshExpiresAtMs =
    typeof auth["refreshExpiresAt"] === "number" ? expiryToMs(auth["refreshExpiresAt"]) : undefined;
  const enterpriseId = optionalString(identity["enterpriseId"]);
  const nickname = optionalString(identity["nickname"]);
  return {
    accessToken,
    refreshToken: typeof auth["refreshToken"] === "string" ? auth["refreshToken"] : "",
    expiresAtMs: typeof auth["expiresAt"] === "number" ? expiryToMs(auth["expiresAt"]) : 0,
    ...(refreshExpiresAtMs === undefined ? {} : { refreshExpiresAtMs }),
    domain: optionalString(auth["domain"]) ?? "",
    uid: optionalString(identity["uid"]) ?? "",
    ...(enterpriseId === undefined ? {} : { enterpriseId }),
    ...(nickname === undefined ? {} : { nickname }),
    accountType: optionalString(identity["accountType"]),
    source: "desktop",
  };
}

/** Serialize the tool-owned copy. */
function ownDocument(credential) {
  return { version: OWN_FORMAT_VERSION, credential };
}

/** Parse the tool-owned copy; other versions and shapes are rejected. */
function parseOwnDocument(text) {
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    return undefined;
  }
  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) return undefined;
  if (parsed["version"] !== OWN_FORMAT_VERSION) return undefined;
  if (typeof parsed["credential"] !== "object" || parsed["credential"] === null) return undefined;
  const stored = parsed["credential"];
  const accessToken = typeof stored["accessToken"] === "string" ? stored["accessToken"] : "";
  if (accessToken === "") return undefined;
  const refreshExpiresAtMs =
    typeof stored["refreshExpiresAtMs"] === "number" ? stored["refreshExpiresAtMs"] : undefined;
  const enterpriseId = optionalString(stored["enterpriseId"]);
  const nickname = optionalString(stored["nickname"]);
  return {
    accessToken,
    refreshToken: typeof stored["refreshToken"] === "string" ? stored["refreshToken"] : "",
    expiresAtMs: typeof stored["expiresAtMs"] === "number" ? stored["expiresAtMs"] : 0,
    ...(refreshExpiresAtMs === undefined ? {} : { refreshExpiresAtMs }),
    domain: optionalString(stored["domain"]) ?? "",
    uid: optionalString(stored["uid"]) ?? "",
    ...(enterpriseId === undefined ? {} : { enterpriseId }),
    ...(nickname === undefined ? {} : { nickname }),
    accountType: optionalString(stored["accountType"]),
    source: "workbuddy-api",
  };
}

/** Region for a login domain; an empty domain means CN. */
export function regionOf(domain) {
  const lowered = String(domain ?? "").trim().toLowerCase();
  if (lowered === "workbuddy.ai" || lowered.endsWith(".workbuddy.ai")) return "global";
  return "cn";
}

/**
 * Stable identity key for one credential, used to scope saved catalogs.
 *
 * @param {object} credential
 * @returns {string} e.g. `"<uid>:<enterpriseId>"`.
 */
export function credentialIdentity(credential) {
  return `${credential.uid}:${credential.enterpriseId ?? ""}`;
}

/**
 * Read-only credential store with demand-driven refresh.
 *
 * Refresh policy: refresh only when the access token is inside the margin (or
 * already expired), keep the refreshed credential in the tool-owned copy, and
 * never write the desktop app's file. A failed refresh still returns a
 * not-yet-expired token so an unreachable refresh endpoint does not take down
 * a working session.
 */
export class CredentialStore {
  /**
   * @param {object} options
   * @param {object} options.variant variant descriptor
   * @param {(credential: object) => Promise<{accessToken: string, refreshToken?: string, expiresInSec?: number, domain?: string}>} options.refresh
   * @param {string} [options.desktopPath] explicit desktop auth-file path
   * @param {string} [options.electronPath] explicit WorkBuddy binary for the
   *   at-rest key helper (5.6+ encrypted credentials); see
   *   `WORKBUDDY_ELECTRON_BIN`
   * @param {number} [options.refreshMarginMs] refresh early by this much
   */
  constructor(options) {
    this.variant = options.variant;
    this.refresh = options.refresh;
    this.refreshMarginMs = options.refreshMarginMs ?? 300_000;
    this.ownPath = options.ownPath ?? ownAuthPath(options.variant);
    this.desktopPathOverride = options.desktopPath;
    this.electronPath = options.electronPath;
    /** Lazily created so a plaintext credential never spawns a helper. */
    this.keyProvider = undefined;
    this.inflight = undefined;
  }

  /**
   * Configuration precedence for the desktop file: an explicit path (flag or
   * config), then the variant's environment variable, then the platform
   * defaults. An explicit path is used verbatim; the defaults are a probe order.
   */
  resolveDesktopCandidates() {
    const fromEnv = process.env[this.variant.env];
    const explicit =
      this.desktopPathOverride ?? (fromEnv !== undefined && fromEnv.trim() !== "" ? fromEnv : undefined);
    if (explicit !== undefined) return [explicit];
    return desktopAuthCandidates(this.variant);
  }

  /** The first (preferred) desktop auth-file candidate, for diagnostics. */
  desktopAuthPath() {
    return this.resolveDesktopCandidates()[0];
  }

  /** The tool-owned copy path, for diagnostics. */
  ownAuthPath() {
    return this.ownPath;
  }

  /** Read the freshest stored credential without refreshing anything. */
  async current() {
    const [desktop, own] = await Promise.all([this.readDesktop(), this.readOwn()]);
    for (const [label, credential] of [
      ["desktop file", desktop],
      ["tool copy", own],
    ]) {
      if (credential === undefined) continue;
      const region = regionOf(credential.domain);
      if (region !== this.variant.region) {
        throw new Error(
          `${this.variant.displayName} received a ${region === "cn" ? "WorkBuddy (CN)" : "WorkBuddy AI"} credential in its ${label} ` +
            `(domain ${JSON.stringify(credential.domain)}); point ${this.variant.env} at the ${this.variant.appName} sign-in, or remove the mismatched file`,
        );
      }
    }
    if (desktop === undefined) return own;
    if (own === undefined) return desktop;
    // A different account signed in on either side wins outright; otherwise the
    // later expiry is the fresher token.
    if (desktop.uid !== own.uid || desktop.enterpriseId !== own.enterpriseId) return desktop;
    return own.expiresAtMs > desktop.expiresAtMs ? own : desktop;
  }

  /**
   * The credential to send upstream: {@link current}, refreshed on demand.
   * Single-flight, so parallel requests share one refresh.
   */
  async resolve() {
    const credential = await this.current();
    if (credential === undefined) {
      const candidates = this.resolveDesktopCandidates();
      const desktop = candidates.length > 0 ? candidates.join(" or ") : "(no desktop path on this platform)";
      throw new Error(
        `workbuddy: no signed-in ${this.variant.appName} account found; sign in once in the ${this.variant.appName} desktop app ` +
          `(expected ${desktop} or ${this.variant.env}), or refresh an existing session`,
      );
    }
    if (!this.needsRefresh(credential)) return credential;
    this.inflight ??= this.refreshNow(credential).finally(() => {
      this.inflight = undefined;
    });
    return this.inflight;
  }

  /** Read-only sign-in summary; never refreshes and never throws. */
  async status() {
    try {
      const credential = await this.current();
      if (credential === undefined) return { state: "signed-out" };
      return {
        state: "signed-in",
        expiresAtMs: credential.expiresAtMs,
        ...(credential.refreshExpiresAtMs === undefined ? {} : { refreshExpiresAtMs: credential.refreshExpiresAtMs }),
        ...(credential.nickname === undefined ? {} : { nickname: credential.nickname }),
        ...(credential.domain === "" ? {} : { domain: credential.domain }),
        uid: credential.uid,
        ...(credential.enterpriseId === undefined ? {} : { enterpriseId: credential.enterpriseId }),
        source: credential.source,
      };
    } catch (error) {
      return { state: "signed-out", reason: error instanceof Error ? error.message : String(error) };
    }
  }

  /**
   * Whether a credential is inside the refresh margin (or already expired).
   *
   * An expiry of 0 means the document carried no usable expiry, which is
   * treated as "refresh required" so the token is validated before use.
   */
  needsRefresh(credential) {
    if (credential.expiresAtMs <= 0) return true;
    return Date.now() + this.refreshMarginMs >= credential.expiresAtMs;
  }

  /** Force a refresh now, regardless of expiry, and re-read the result. */
  async refreshNow(credential) {
    if (credential.refreshToken === "") {
      if (credential.expiresAtMs > Date.now() + 30_000) return credential;
      throw new Error(
        `workbuddy: access token expired and no refresh token is stored; sign in again in the ${this.variant.appName} desktop app`,
      );
    }
    try {
      const outcome = await this.refresh(credential);
      const refreshed = {
        ...credential,
        accessToken: outcome.accessToken,
        ...(outcome.refreshToken === undefined ? {} : { refreshToken: outcome.refreshToken }),
        expiresAtMs:
          outcome.expiresInSec !== undefined ? Date.now() + outcome.expiresInSec * 1e3 : credential.expiresAtMs,
        ...(outcome.domain === undefined || outcome.domain === "" ? {} : { domain: outcome.domain }),
        source: "workbuddy-api",
      };
      await this.saveOwn(refreshed);
      return refreshed;
    } catch (error) {
      if (credential.expiresAtMs > Date.now() + 30_000) return credential;
      throw new Error(
        `workbuddy: token refresh failed and the access token is expired (${String(error)}); ` +
          `open the ${this.variant.appName} desktop app once to sign in again`,
      );
    }
  }

  /** Persist a refreshed credential into the tool-owned copy. */
  async saveOwn(credential) {
    await ensureDir(dirname(this.ownPath));
    await writeFileAtomic(this.ownPath, `${JSON.stringify(ownDocument(credential), null, 2)}\n`, { mode: 0o600 });
  }

  /**
   * Read the first desktop candidate that exists. Only an absent file (ENOENT)
   * falls through to the next candidate; a file that is present but unparsable
   * is authoritative for its slot, so a stale older-version file never silently
   * wins over a broken newer one.
   *
   * A WorkBuddy 5.6+ file has its token fields encrypted; it is decrypted in
   * place before parsing, which spawns the app's own binary once per key id.
   * That spawn is the expensive part of a read, so the plaintext fast path
   * avoids it entirely.
   */
  async readDesktop() {
    for (const desktopPath of this.resolveDesktopCandidates()) {
      let text;
      try {
        text = await readFile(desktopPath, "utf8");
      } catch (error) {
        if (!isENOENT(error)) throw error;
        continue;
      }
      return parseWorkBuddyAuth(await this.plaintextDesktopAuth(text));
    }
    return undefined;
  }

  /**
   * Return a desktop auth document as plaintext, decrypting a 5.6+ file.
   *
   * An unrecognized document is returned unchanged so the regular parser can
   * report "no token" in the usual way rather than this module inventing a
   * different failure for it.
   */
  async plaintextDesktopAuth(text) {
    const classification = classifyDesktopAuthDocument(text);
    if (classification.format !== "encrypted") return text;
    this.keyProvider ??= new AtRestKeyProvider(
      this.electronPath === undefined ? {} : { electronPath: this.electronPath },
    );
    try {
      return await unwrapDesktopAuthDocument(classification, this.keyProvider);
    } catch (error) {
      if (error instanceof WorkBuddyKeyError) {
        // Re-thrown with the app name attached: the bare key error is precise but
        // does not tell the user which product's credential is stuck.
        throw new Error(
          `workbuddy: the ${this.variant.appName} desktop credential is encrypted and could not be read (${error.message})`,
        );
      }
      throw error;
    }
  }

  /** Read the tool-owned copy, tolerating its absence. */
  async readOwn() {
    try {
      return parseOwnDocument(await readFile(this.ownPath, "utf8"));
    } catch (error) {
      if (isENOENT(error)) return undefined;
      throw error;
    }
  }

  /** Whether any desktop-file candidate exists as a regular file; diagnostics only. */
  async desktopFilePresent() {
    for (const desktopPath of this.resolveDesktopCandidates()) {
      try {
        if ((await stat(desktopPath)).isFile()) return true;
      } catch {
        /* keep probing */
      }
    }
    return false;
  }

  /** Remove the tool-owned copy; the desktop app's sign-in is untouched. */
  async logout() {
    await removeIfPresent(this.ownPath);
    await rm(`${this.ownPath}.lock`, { force: true });
  }
}

/** Read a saved catalog document for a variant, or undefined when absent. */
export async function readSavedCatalog(path) {
  return readJson(path);
}
