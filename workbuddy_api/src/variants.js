/**
 * The two WorkBuddy products this tool can talk to.
 *
 * Both desktop apps are the same client in different regions. They share one
 * auth *directory* and differ by auth-file basename, base URL, catalog
 * endpoint, and display identity. Everything that varies is collected into a
 * descriptor here so no other module carries an `if (international)` branch.
 *
 * @module workbuddy-api/variants
 */

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));

/** Load one compiled-in fallback roster shipped beside this module. */
function loadRoster(filename) {
  return JSON.parse(readFileSync(join(here, filename), "utf8"));
}

/**
 * CN, the long-standing default.
 *
 * Since plugin 0.6.1 its catalog also comes from `/v3/config`, the product
 * document the desktop client itself fetches. It used to read
 * `/console/enterprises/personal/models`, which is why its roster drifted from
 * the App's selector — the console document lags the product one. What
 * distinguishes the two variants is now the User-Agent rather than the path:
 * the gateway splits `/v3/config` by client identity, so CN sends its CLI UA and
 * receives the CLI roster (exactly the models its own chat requests can use),
 * while the international variant sends the App-shaped UA.
 *
 * `promoBadgePath` is CN-only: `/v3/config` carries no `badge:*` tags, so the
 * discount labels are read from the console document in a second, best-effort
 * request. See `fetchPromoBadges` in `upstream.js`.
 */
export const CN_VARIANT = {
  id: "workbuddy",
  displayName: "WorkBuddy",
  appName: "WorkBuddy",
  region: "cn",
  env: "WORKBUDDY_AUTH_FILE",
  ownFilename: "auth.cn.json",
  modelsFilename: "models.cn.json",
  desktopFilename: "workbuddy-desktop.info",
  chatBase: "https://copilot.tencent.com",
  billingBase: "https://www.codebuddy.cn",
  catalogPath: "/v3/config",
  promoBadgePath: "/console/enterprises/personal/models",
  catalogSource: "workbuddy:cli",
};

/**
 * International WorkBuddy AI. Same `/v3/config` path as CN, but read with the
 * App-shaped UA because this variant has no CLI identity of its own.
 */
export const AI_VARIANT = {
  id: "workbuddy-ai",
  displayName: "WorkBuddy AI",
  appName: "WorkBuddy AI",
  region: "global",
  env: "WORKBUDDY_AI_AUTH_FILE",
  ownFilename: "auth.ai.json",
  modelsFilename: "models.ai.json",
  desktopFilename: "workbuddy-desktop-ai.info",
  chatBase: "https://www.workbuddy.ai",
  billingBase: "https://www.workbuddy.ai",
  catalogPath: "/v3/config",
  catalogSource: "workbuddy-ai:app",
};

/** Both variants, CN first (it owns the historical default provider id). */
export const VARIANTS = [CN_VARIANT, AI_VARIANT];

/** Look up a variant descriptor by provider id. */
export function variantFor(id) {
  return VARIANTS.find((variant) => variant.id === id);
}

/** Resolve a variant id, throwing a user-facing error for an unknown one. */
export function requireVariant(id) {
  const variant = variantFor(id);
  if (variant === undefined) {
    throw new Error(
      `unknown provider ${JSON.stringify(id)}; expected one of ${VARIANTS.map((v) => v.id).join(", ")}`,
    );
  }
  return variant;
}

const rosterCache = new Map();

/** The compiled-in fallback roster for a variant (used before/without a fetch). */
export function fallbackRoster(variant) {
  const cached = rosterCache.get(variant.id);
  if (cached !== undefined) return cached;
  const roster = loadRoster(variant.modelsFilename);
  rosterCache.set(variant.id, roster);
  return roster;
}
