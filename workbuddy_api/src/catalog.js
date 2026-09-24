/**
 * Model catalog: a compiled-in fallback roster, replaced by the upstream's
 * dynamic answer once it loads, and cached per account on disk so an offline
 * start still serves the right models.
 *
 * @module workbuddy-api/catalog
 */

import { readFileSync, writeFileSync, existsSync, mkdirSync, renameSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { stateHome } from "./paths.js";
import { fallbackRoster } from "./variants.js";
import { modelWithCurrentPromotion } from "./upstream.js";

/** On-disk format this reader accepts; other versions are discarded. */
const CATALOG_FORMAT_VERSION = 1;

/** Saved-catalog path for one variant. */
export function catalogPath(variant) {
  return join(stateHome(), `catalog.${variant.id}.json`);
}

/** Whether a parsed value is a model row worth keeping. */
function isModel(value) {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  return (
    typeof value["id"] === "string" &&
    value["id"] !== "" &&
    typeof value["name"] === "string" &&
    typeof value["contextWindow"] === "number" &&
    Number.isFinite(value["contextWindow"]) &&
    typeof value["maxTokens"] === "number" &&
    Number.isFinite(value["maxTokens"])
  );
}

/** Whether a parsed value is a saved catalog this reader can trust. */
function isSaved(value) {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  if (typeof value["account"] !== "string" || value["account"] === "") return false;
  if (typeof value["source"] !== "string" || value["source"] === "") return false;
  if (typeof value["fetchedAtMs"] !== "number" || !Number.isFinite(value["fetchedAtMs"])) return false;
  const models = value["models"];
  return Array.isArray(models) && models.length > 0 && models.every(isModel);
}

/**
 * The last successful catalog per account, read once and written atomically.
 *
 * Malformed content reads as "nothing saved" rather than throwing: this file is
 * an optimization for the offline and first-seconds cases, and a corrupt one
 * must never be able to stop the gateway from serving models.
 */
export class CatalogStore {
  constructor(path) {
    this.path = path;
    this.entries = undefined;
  }

  /** Resolved state-file path, for diagnostics. */
  filePath() {
    return this.path;
  }

  load() {
    if (this.entries !== undefined) return this.entries;
    const entries = {};
    if (existsSync(this.path)) {
      try {
        const parsed = JSON.parse(readFileSync(this.path, "utf8"));
        if (typeof parsed === "object" && parsed !== null && !Array.isArray(parsed)) {
          const raw = parsed["version"] === CATALOG_FORMAT_VERSION ? parsed["entries"] : undefined;
          if (typeof raw === "object" && raw !== null && !Array.isArray(raw)) {
            for (const [key, value] of Object.entries(raw)) if (isSaved(value)) entries[key] = value;
          }
        }
      } catch {
        /* keep the empty map */
      }
    }
    this.entries = entries;
    return entries;
  }

  /** The saved catalog for one account, or undefined when there is none. */
  get(account) {
    const entry = this.load()[account];
    return entry === undefined ? undefined : entry;
  }

  /** Remember a catalog for an account, replacing whatever was saved before. */
  set(account, catalog) {
    const entries = this.load();
    entries[account] = { account, ...catalog };
    this.persist();
  }

  /** Forget one account's catalog — used when that account signs out. */
  delete(account) {
    const entries = this.load();
    if (!(account in entries)) return;
    delete entries[account];
    this.persist();
  }

  persist() {
    try {
      const directory = dirname(this.path);
      if (!existsSync(directory)) mkdirSync(directory, { recursive: true, mode: 0o700 });
      const document = { version: CATALOG_FORMAT_VERSION, entries: this.load() };
      const temporary = resolve(`${this.path}.tmp`);
      writeFileSync(temporary, `${JSON.stringify(document, null, 2)}\n`, { mode: 0o600 });
      renameSync(temporary, this.path);
    } catch {
      /* losing the memory of a catalog is never worth failing a request */
    }
  }
}

/**
 * Live catalog state for one variant.
 *
 * Holds the compiled-in roster until an upstream fetch lands, applies
 * time-boxed promotions on every read, and exposes provenance so `status` can
 * say whether the list is live, saved, or built in.
 */
export class Catalog {
  /**
   * @param {object} variant
   * @param {{store?: CatalogStore}} [options]
   */
  constructor(variant, options = {}) {
    this.variant = variant;
    this.fallback = fallbackRoster(variant);
    this.models = this.fallback;
    this.source = "builtin";
    this.fetchedAtMs = undefined;
    this.error = undefined;
    this.account = undefined;
    this.store = options.store;
  }

  /** Models to serve right now, with live promotions applied. */
  current() {
    return this.models.map((model) => modelWithCurrentPromotion(model));
  }

  /** Models ignoring promotion re-evaluation (the base roster). */
  base() {
    return this.models;
  }

  /** Adopt a freshly fetched roster and remember it for this account. */
  adopt(models, account) {
    this.models = models;
    this.source = "live";
    this.fetchedAtMs = Date.now();
    this.error = undefined;
    this.account = account;
    if (this.store !== undefined && account !== undefined && account !== "") {
      this.store.set(account, {
        source: this.variant.catalogSource,
        fetchedAtMs: this.fetchedAtMs,
        models,
      });
    }
  }

  /**
   * Fall back to a previously saved catalog for this account, if one exists.
   *
   * @returns {boolean} whether a saved catalog was adopted.
   */
  adoptSaved(account) {
    if (this.store === undefined || account === undefined || account === "") return false;
    const saved = this.store.get(account);
    if (saved === undefined) return false;
    this.models = saved.models;
    this.source = "saved";
    this.fetchedAtMs = saved.fetchedAtMs;
    this.account = account;
    return true;
  }

  /** Record a failed fetch without discarding what is already being served. */
  recordError(error) {
    this.error = error instanceof Error ? error.message : String(error);
  }

  /** Catalog provenance for `status` output. */
  provenance() {
    return {
      source: this.source,
      ...(this.fetchedAtMs === undefined ? {} : { fetchedAt: new Date(this.fetchedAtMs).toISOString() }),
      ...(this.error === undefined ? {} : { error: this.error }),
      count: this.models.length,
    };
  }
}
