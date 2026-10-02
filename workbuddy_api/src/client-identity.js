/**
 * The desktop-client identity chat requests present as.
 *
 * WorkBuddy's gateway is strict about the User-Agent shape: the CN chat
 * endpoint rejects a bare "openai-style" caller, and the international catalog
 * endpoint splits its answer by UA. Chat and probe requests therefore carry the
 * shape the official desktop client composes —
 * `WorkBuddy/<v> <product>/<v> CLI/<cli>` — where the product token names the
 * app that owns the region's requests.
 *
 * Versions come from the installed App when it can be read (macOS bundles), and
 * otherwise from a compiled-in constant. A CLI version that does not resolve
 * drops the `CLI/…` token instead of inventing one. The gateway ignores the
 * version numbers for routing, so these are shape requirements, not currency
 * claims.
 *
 * @module workbuddy-api/client-identity
 */

import { execFileSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";

/** Compiled-in CN fallback for the `WorkBuddy/<v>` tokens. */
export const FALLBACK_CN_APP_VERSION = "5.5.6";
/** Compiled-in international fallback. */
export const FALLBACK_AI_APP_VERSION = "5.5.2";

/** Run a command and return its stdout, or undefined on any failure. */
export function tryExec(command, args) {
  try {
    return execFileSync(command, args, {
      encoding: "utf8",
      windowsHide: true,
      stdio: ["ignore", "pipe", "ignore"],
    });
  } catch {
    return undefined;
  }
}

/** Whether a string is safe to interpolate into an HTTP header as a version. */
export function validAppVersion(value) {
  return typeof value === "string" && /^\d{1,6}(?:\.\d{1,6}){1,3}$/u.test(value);
}

/** Tolerates a prerelease suffix; rejects anything with whitespace, CR or LF. */
export function validCliVersion(value) {
  return typeof value === "string" && /^\d{1,6}(?:\.\d{1,6}){1,3}(?:-[0-9A-Za-z.]+)?$/u.test(value);
}

/** macOS App-bundle roots, system-wide first, then the user's own install. */
function macAppRoots() {
  return ["/Applications", join(homedir(), "Applications")];
}

/**
 * Read `CFBundleShortVersionString` out of an `Info.plist`.
 *
 * Parsed as XML rather than grepped, because the plist contains several
 * `<string>` values and a regex would be one unrelated key away from returning
 * the wrong one.
 */
async function readBundleVersion(plistPath) {
  let text;
  try {
    text = await readFile(plistPath, "utf8");
  } catch {
    return undefined;
  }
  const version = /<key>\s*CFBundleShortVersionString\s*<\/key>\s*<string>([^<]*)<\/string>/u.exec(text)?.[1]?.trim();
  return validAppVersion(version) ? version : undefined;
}

/**
 * The installed desktop App's version, or undefined when it is not installed
 * (or not readable).
 *
 * macOS is read from the App bundle. On Windows the version also lives in the
 * bundled CLI package metadata; `readWindowsCliVersion` covers that. Linux has
 * no verified location, so the compiled-in value applies rather than a guess.
 */
async function installedAppVersion(appBundleName) {
  if (process.platform !== "darwin") return undefined;
  for (const root of macAppRoots()) {
    const bundle = join(root, `${appBundleName}.app`);
    const version = await readBundleVersion(join(bundle, "Contents", "Info.plist"));
    if (version !== undefined) return { version, bundle };
  }
  return undefined;
}

/** Path of the bundled agent CLI's package.json inside a macOS App bundle. */
function cliPackagePath(bundle) {
  return join(bundle, "Contents", "Resources", "app.asar.unpacked", "cli", "package.json");
}

/**
 * The bundled agent CLI's real version, or undefined when it does not resolve.
 *
 * `cli/package.json` ships a `0.0.0` placeholder in `version` with the real
 * version in `publishConfig.customPackage.version`; a valid non-placeholder
 * `version` wins, otherwise the custom-package value applies.
 */
async function readCliVersion(bundle) {
  let document;
  try {
    document = JSON.parse(await readFile(cliPackagePath(bundle), "utf8"));
  } catch {
    return undefined;
  }
  if (typeof document !== "object" || document === null || Array.isArray(document)) return undefined;
  const declared = document["version"];
  if (validCliVersion(declared) && declared !== "0.0.0") return declared;
  const publishConfig =
    typeof document["publishConfig"] === "object" && document["publishConfig"] !== null
      ? document["publishConfig"]
      : undefined;
  const customPackage =
    typeof publishConfig?.["customPackage"] === "object" && publishConfig["customPackage"] !== null
      ? publishConfig["customPackage"]
      : undefined;
  return validCliVersion(customPackage?.["version"]) ? customPackage["version"] : undefined;
}

/**
 * Read the WorkBuddy desktop App's version on Windows.
 *
 * The App is an Electron build installed per-user; its `resources/app.asar`
 * cannot be read without a parser, but `resources/app-update.yml` / the
 * packaged `package.json` beside it carry a plain version. Both the guessed
 * locations are probed and any failure degrades to the compiled-in constant.
 */
async function readWindowsAppVersion(appDirName) {
  const roots = [
    join(process.env["LOCALAPPDATA"] ?? "", "Programs", appDirName),
    join(process.env["LOCALAPPDATA"] ?? "", appDirName),
    join(process.env["PROGRAMFILES"] ?? "", appDirName),
  ];
  for (const root of roots) {
    for (const relative of ["resources/app-update.yml", "resources/package.json"]) {
      let text;
      try {
        text = await readFile(join(root, ...relative.split("/")), "utf8");
      } catch {
        continue;
      }
      const matched = /^version:\s*(\S+)$/mu.exec(text)?.[1] ?? /"version"\s*:\s*"([^"]+)"/u.exec(text)?.[1];
      if (validAppVersion(matched)) return { version: matched, bundle: root };
    }
  }
  return undefined;
}

/**
 * Read the installed App's version from the Windows uninstall registry.
 *
 * The App registers itself per-user as `WorkBuddy <version>`, and the display
 * name carries the exact version the client would put in its own User-Agent.
 * This is the most reliable Windows source: the Electron install tree does not
 * expose a readable version, and the packaging directory name is not the
 * product name.
 *
 * @returns {Promise<{version: string, bundle?: string}|undefined>}
 */
async function readWindowsRegistryVersion(appName) {
  const roots = [
    "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
    "HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
    "HKLM\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
  ];
  for (const root of roots) {
    const output = tryExec("reg", ["query", root, "/s", "/f", appName]);
    if (output === undefined) continue;
    const display = /DisplayName\s+REG_SZ\s+(.+)/u.exec(output)?.[1]?.trim();
    const version = /DisplayVersion\s+REG_SZ\s+(\S+)/u.exec(output)?.[1]?.trim();
    const location = /InstallLocation\s+REG_SZ\s+(.+)/u.exec(output)?.[1]?.trim();
    // The display name is "WorkBuddy 5.5.6"; the trailing token is the version.
    const fromName = display === undefined ? undefined : display.split(/\s+/u).pop();
    const candidate = validAppVersion(version) ? version : validAppVersion(fromName) ? fromName : undefined;
    if (candidate !== undefined) {
      return { version: candidate, ...(location === undefined || location === "" ? {} : { bundle: location }) };
    }
  }
  return undefined;
}

/** Resolve the best available App version for one variant. */
async function resolveAppVersion(variant) {
  if (process.platform === "darwin") {
    const installed = await installedAppVersion(variant.appName);
    if (installed !== undefined && validAppVersion(installed.version)) {
      const cliVersion = await readCliVersion(installed.bundle);
      return { clientVersion: installed.version, ...(cliVersion === undefined ? {} : { cliVersion }) };
    }
  }
  if (process.platform === "win32") {
    const registry = await readWindowsRegistryVersion(variant.appName);
    if (registry !== undefined) {
      const cliVersion = registry.bundle === undefined ? undefined : await readCliVersion(registry.bundle);
      return { clientVersion: registry.version, ...(cliVersion === undefined ? {} : { cliVersion }) };
    }
    const installed = await readWindowsAppVersion(variant.appName.replace(/\s+/gu, ""));
    if (installed !== undefined) {
      const cliVersion = await readCliVersion(installed.bundle);
      return { clientVersion: installed.version, ...(cliVersion === undefined ? {} : { cliVersion }) };
    }
  }
  return {
    clientVersion: variant.region === "global" ? FALLBACK_AI_APP_VERSION : FALLBACK_CN_APP_VERSION,
  };
}

const identityCache = new Map();

/** The region's compiled-in identity: the desktop form, no `CLI/…` segment. */
export function fallbackChatIdentity(variant) {
  return {
    clientVersion: variant.region === "global" ? FALLBACK_AI_APP_VERSION : FALLBACK_CN_APP_VERSION,
  };
}

/**
 * Resolve the chat identity for one variant: installed App → compiled-in
 * fallback. Never throws and caches per variant, because a message must not
 * re-read the install tree.
 */
export async function resolveChatIdentity(variant) {
  const cached = identityCache.get(variant.id);
  if (cached !== undefined) return cached;
  let identity;
  try {
    identity = await resolveAppVersion(variant);
  } catch {
    identity = fallbackChatIdentity(variant);
  }
  identityCache.set(variant.id, identity);
  return identity;
}

/**
 * Build the chat User-Agent for one variant.
 *
 * @throws when a version would be unsafe to interpolate into a header.
 */
export function chatUserAgent(identity, variant) {
  if (!validAppVersion(identity.clientVersion)) {
    throw new Error(`invalid client version for chat User-Agent: ${JSON.stringify(identity.clientVersion)}`);
  }
  if (identity.cliVersion !== undefined && !validCliVersion(identity.cliVersion)) {
    throw new Error(`invalid CLI version for chat User-Agent: ${JSON.stringify(identity.cliVersion)}`);
  }
  const product = variant.region === "global" ? "WorkBuddy AI" : "WorkBuddy";
  const parts = [`WorkBuddy/${identity.clientVersion}`, `${product}/${identity.clientVersion}`];
  if (identity.cliVersion !== undefined) parts.push(`CLI/${identity.cliVersion}`);
  return parts.join(" ");
}

/**
 * Build the App-shaped UA for international catalog requests.
 *
 * `WorkBuddyAI/<version>` with no space is the form measured to reach the App
 * document; the space form is rejected with HTTP 400.
 */
export function appUserAgent(version) {
  if (!validAppVersion(version)) {
    throw new Error(`invalid WorkBuddy AI version for User-Agent: ${JSON.stringify(version)}`);
  }
  return `WorkBuddyAI/${version}`;
}

/**
 * Best-effort detection of the desktop App's installation, for `doctor`.
 *
 * @returns {Promise<{ installed: boolean, version?: string, bundle?: string, source: string }>}
 */
export async function detectAppInstall(variant) {
  if (process.platform === "darwin") {
    const installed = await installedAppVersion(variant.appName);
    if (installed !== undefined) {
      return { installed: true, version: installed.version, bundle: installed.bundle, source: "installed" };
    }
    return { installed: false, source: "not-found" };
  }
  if (process.platform === "win32") {
    const registry = await readWindowsRegistryVersion(variant.appName);
    if (registry !== undefined) {
      return {
        installed: true,
        version: registry.version,
        ...(registry.bundle === undefined ? {} : { bundle: registry.bundle }),
        source: "registry",
      };
    }
    const appDir = await readWindowsAppVersion(variant.appName.replace(/\s+/gu, ""));
    if (appDir !== undefined) {
      return { installed: true, version: appDir.version, bundle: appDir.bundle, source: "install-tree" };
    }
    return { installed: false, source: "not-found" };
  }
  return { installed: false, source: "unsupported-platform" };
}

/** Whether a process with this pid is alive (signal 0 probes existence). */
export function processAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}
