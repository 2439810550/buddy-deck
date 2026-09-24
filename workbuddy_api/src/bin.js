#!/usr/bin/env node
/**
 * workbuddy-api — standalone WorkBuddy auth + OpenAI-compatible local gateway.
 *
 * Commands:
 *   serve     run the OpenAI-compatible gateway (the main command)
 *   status    sign-in state, remaining credit, catalog provenance
 *   models    print the model roster
 *   doctor    secret-free environment diagnostics
 *   logout    remove this tool's credential copy
 *   config    get/set persisted configuration
 *   accounts  manage multiple WorkBuddy accounts (list/add/use/remove/rename/export/import)
 *   checkin   claim the daily check-in for every stored account
 *
 * Zero runtime dependencies; requires Node 20.10+ (or 22/24).
 *
 * @module workbuddy-api/bin
 */

import { randomBytes } from "node:crypto";
import { pathToFileURL } from "node:url";
import { createGateway } from "./gateway.js";
import { Catalog, CatalogStore, catalogPath } from "./catalog.js";
import { CredentialStore, desktopAuthCandidates, ownAuthPath } from "./auth.js";
import { AccountRouter, AccountVault, accountsPath } from "./accounts.js";
import { ensureCheckedIn, fetchCheckinStatus } from "./checkin.js";
import { WorkBuddyClient, normalizeCredits } from "./upstream.js";
import { CN_VARIANT, requireVariant } from "./variants.js";
import { configPath, loadConfig, saveConfig } from "./config.js";
import { logPath, stateHome } from "./paths.js";
import { detectAppInstall } from "./client-identity.js";
import {
  classifyDesktopAuthDocument,
  defaultElectronPaths,
  resolveKeyHelperPath,
  WORKBUDDY_ELECTRON_BIN_ENV,
} from "./desktop-credential-protection.js";
import { readFile, writeFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { join } from "node:path";
import { createInterface } from "node:readline";
import {
  MIN_PASSPHRASE_LENGTH,
  openAccounts,
  samePassphrase,
  sealAccounts,
  suggestedFileName,
} from "./transfer.js";

const VERSION = "0.1.0";
const SCHEMA_VERSION = 1;

/** Print the usage banner. */
function printHelp() {
  process.stdout.write(
    [
      "workbuddy-api — use WorkBuddy desktop models from any CLI",
      "",
      "Usage: workbuddy-api <serve|status|models|doctor|logout|config|accounts|checkin> [options]",
      "",
      "  serve    Run a local OpenAI-compatible gateway (default: 127.0.0.1:8787)",
      "  status   Sign-in state, remaining credit, and catalog provenance",
      "  models   Print the model roster (live when reachable, else cached)",
      "  doctor   Secret-free sign-in and environment diagnostics",
      "  logout   Remove this tool's credential copy (the desktop app keeps its sign-in)",
      "  config   Read or write persisted configuration",
      "  accounts List stored accounts, or: accounts add [--label X] | use <id> | clear | remove <id> | rename <id> <name> | renew [id] | export | import <file>",
      "  checkin  Claim today's check-in (all stored accounts, or --id <id>)",
      "",
      "Common options:",
      "  --provider <id>   workbuddy (default) | workbuddy-ai",
      "  --auth-file <p>   explicit desktop auth-file path",
      "  --json            machine-readable output (status/models/doctor/accounts/checkin)",
      "  --pass <s>        passphrase for accounts export/import (omit to read one line from stdin)",
      "  --out <file>      where accounts export writes (default: current directory)",
      "",
      "serve options:",
      "  --port <n>        listen port (0 = OS-assigned; default 8787)",
      "  --host <h>        bind interface (default 127.0.0.1)",
      "  --api-key <k>     require this bearer token (default: generate one)",
      "  --no-auth         serve without a bearer (loopback only)",
      "  --default-model <id>  model to use when a client asks for one we don't have",
      "                    (Claude Code sends claude-*, Codex sends gpt-*; default: pick a free model)",
      "  --quiet           suppress per-request logging",
      "",
      "config options:",
      "  --set key=value   persist a setting (provider/host/port/apiKey/authFile/authFileAI/defaultModel)",
      "  --unset <key>     clear a setting",
      "",
    ].join("\n"),
  );
}

/** Remove token-like strings from any message that reaches a terminal. */
function safeMessage(error) {
  return (error instanceof Error ? error.message : String(error))
    .replace(/\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b/gu, "[redacted token]")
    .replace(/(\b(?:code|token|refresh_token|access_token)=)[^&\s]+/giu, "$1[redacted]");
}

/** Parse argv into a command and a flag map. */
function parseArgv(argv) {
  const [command, ...rest] = argv;
  const flags = { _: [] };
  for (let index = 0; index < rest.length; index += 1) {
    const token = rest[index];
    if (!token.startsWith("--")) {
      flags._.push(token);
      continue;
    }
    const body = token.slice(2);
    const equals = body.indexOf("=");
    if (equals !== -1) {
      flags[body.slice(0, equals)] = body.slice(equals + 1);
      continue;
    }
    const next = rest[index + 1];
    if (next === undefined || next.startsWith("--")) {
      flags[body] = true;
      continue;
    }
    flags[body] = next;
    index += 1;
  }
  return { command, flags };
}

/**
 * Build the credential store, client and catalog for one variant, honoring
 * flag → config → environment precedence for the desktop auth path.
 */
async function makeContext(flags, config) {
  const providerId = typeof flags["provider"] === "string" ? flags["provider"] : config.provider;
  const variant = requireVariant(providerId);
  const configuredPath =
    typeof flags["auth-file"] === "string"
      ? flags["auth-file"]
      : variant.id === CN_VARIANT.id
        ? config.authFile
        : config.authFileAI;
  const client = new WorkBuddyClient(variant);
  const store = new CredentialStore({
    variant,
    ...(configuredPath === "" || configuredPath === undefined ? {} : { desktopPath: configuredPath }),
    refresh: (credential) => client.refreshToken(credential),
  });
  // 多账号：保险库里选了号就用它，没选就退回桌面 App 当前登录的那个。
  // router 的接口和 store 一样（resolve / status），所以网关那边换个对象就能用。
  const vault = new AccountVault({
    variant,
    refresh: (credential) => client.refreshToken(credential),
  });
  const router = new AccountRouter({ vault, store });
  const catalog = new Catalog(variant, { store: new CatalogStore(catalogPath(variant)) });
  return { variant, client, store, vault, router, catalog };
}

/**
 * Prime the catalog: try upstream, then the saved roster, then the built-in one.
 *
 * Never throws: a gateway that cannot reach the network must still start and
 * serve the models it already knows about.
 *
 * @returns {Promise<{refreshed: boolean, reason?: string, usedSaved?: boolean}>}
 */
async function primeCatalog({ client, catalog, store, router }) {
  const source = router ?? store;
  const status = await source.status();
  if (status.state !== "signed-in") {
    catalog.recordError("not signed in");
    return { refreshed: false, reason: "not signed in" };
  }
  const account = `${status.uid ?? ""}:${status.enterpriseId ?? ""}`;
  try {
    const credential = await source.resolve();
    const models = await client.fetchModels(credential);
    catalog.adopt(models, account);
    return { refreshed: true };
  } catch (error) {
    const message = safeMessage(error);
    catalog.recordError(message);
    const usedSaved = catalog.adoptSaved(account);
    return { refreshed: false, reason: message, usedSaved };
  }
}

/** `status` — sign-in, credit, catalog provenance. */
async function commandStatus(flags, config) {
  const context = await makeContext(flags, config);
  const { variant, client, store, catalog } = context;
  const signIn = await store.status();
  const json = flags["json"] === true;
  const prime = await primeCatalog(context);

  if (signIn.state !== "signed-in") {
    if (json) {
      process.stdout.write(
        `${JSON.stringify({
          schemaVersion: SCHEMA_VERSION,
          tool: "workbuddy-api",
          version: VERSION,
          provider: variant.id,
          status: "signed-out",
          ...(signIn.reason === undefined ? {} : { reason: signIn.reason }),
          desktopAuthFile: store.desktopAuthPath(),
        })}\n`,
      );
    } else {
      process.stdout.write(
        [
          `${variant.displayName}: signed out`,
          `Expected sign-in: ${store.desktopAuthPath()}`,
          `Sign in once in the ${variant.appName} desktop app, then retry.`,
          "",
        ].join("\n"),
      );
    }
    return 1;
  }

  let credits;
  try {
    const credential = await store.resolve();
    credits = await client.fetchCredits(credential);
  } catch (error) {
    credits = { total: undefined, error: safeMessage(error) };
  }

  if (json) {
    process.stdout.write(
      `${JSON.stringify({
        schemaVersion: SCHEMA_VERSION,
        tool: "workbuddy-api",
        version: VERSION,
        provider: variant.id,
        status: "signed-in",
        ...(signIn.nickname === undefined ? {} : { nickname: signIn.nickname }),
        ...(signIn.domain === undefined ? {} : { domain: signIn.domain }),
        ...(signIn.expiresAtMs === undefined ? {} : { accessTokenExpires: new Date(signIn.expiresAtMs).toISOString() }),
        source: signIn.source,
        credits: credits.total,
        ...(credits.unlimited === true ? { creditsUnlimited: true } : {}),
        ...(credits.error === undefined ? {} : { creditsError: credits.error }),
        catalog: catalog.provenance(),
        configPath: configPath(),
        stateHome: stateHome(),
      })}\n`,
    );
    return 0;
  }

  process.stdout.write(
    [
      `${variant.displayName}: signed in${signIn.nickname === undefined ? "" : ` as ${signIn.nickname}`}`,
      ...(signIn.expiresAtMs === undefined
        ? []
        : [`Access token expires ${new Date(signIn.expiresAtMs).toISOString()} (refresh is automatic)`]),
      credits.error !== undefined
        ? `Remaining credit: unavailable (${credits.error})`
        : credits.unlimited === true
          ? "Remaining credit: unlimited"
          : `Remaining credit: ${credits.total ?? "unknown"}`,
      `Catalog: ${catalog.provenance().source} (${catalog.base().length} models)` +
        (prime.reason === undefined ? "" : ` — ${prime.reason}`),
      `State: ${stateHome()}`,
      "",
    ].join("\n"),
  );
  return 0;
}

/** `models` — the roster, live when reachable. */
async function commandModels(flags, config) {
  const context = await makeContext(flags, config);
  const { variant, catalog } = context;
  await primeCatalog(context);
  const models = catalog.current();

  if (flags["json"] === true) {
    process.stdout.write(
      `${JSON.stringify(
        {
          schemaVersion: SCHEMA_VERSION,
          provider: variant.id,
          catalog: catalog.provenance(),
          data: models.map((model) => ({
            id: model.id,
            name: model.name,
            contextWindow: model.contextWindow,
            maxTokens: model.maxTokens,
            supportsImages: model.supportsImages === true,
            reasoning: model.reasoning?.supports === true,
            supportedEfforts: model.reasoning?.supportedEfforts,
            credits: model.billing?.credits,
            badges: model.billing?.badges,
          })),
        },
        null,
        2,
      )}\n`,
    );
    return 0;
  }

  const width = Math.max(...models.map((model) => model.id.length), 4);
  process.stdout.write(`# ${variant.id} — ${catalog.provenance().source} (${models.length} models)\n`);
  for (const model of models) {
    const rate = normalizeCredits(model.billing?.credits);
    const badges = model.billing?.badges ?? [];
    const suffix = [rate, ...badges].filter((part) => part !== undefined && part !== "").join(" · ");
    const images = model.supportsImages === true ? " [images]" : "";
    process.stdout.write(`${model.id.padEnd(width)}  ${model.name}${suffix === "" ? "" : ` · ${suffix}`}${images}\n`);
  }
  return 0;
}

/**
 * Describe how a variant's desktop credential is stored, for `doctor`.
 *
 * Reads the auth file directly (a few KB) rather than going through the store,
 * so the report can distinguish "encrypted and readable" from "encrypted and
 * no key helper reachable" without the store collapsing both into one error.
 *
 * @returns {Promise<{format: string, encryptedFields?: string[], keyHelperAvailable: boolean, detail?: string}>}
 */
async function describeCredentialStorage(store) {
  const path = store.desktopAuthPath();
  let text;
  try {
    text = await readFile(path, "utf8");
  } catch {
    return { format: "missing", keyHelperAvailable: false, detail: "no auth file at the primary candidate" };
  }
  const classification = classifyDesktopAuthDocument(text);
  if (classification.format !== "encrypted") {
    return { format: classification.format, keyHelperAvailable: true };
  }
  const fields = classification.wrapped.fields.map((field) => field.field);
  let keyHelperAvailable = false;
  let detail;
  try {
    const helperPath = await resolveKeyHelperPath();
    keyHelperAvailable = true;
    detail = `WorkBuddy 5.6+ at-rest encryption; key helper at ${helperPath}`;
  } catch (error) {
    detail = safeMessage(error);
  }
  return { format: "encrypted", encryptedFields: fields, keyHelperAvailable, ...(detail === undefined ? {} : { detail }) };
}

/** `doctor` — secret-free diagnostics. */
async function commandDoctor(flags, config) {
  const context = await makeContext(flags, config);
  const { variant, store, catalog } = context;
  const signIn = await store.status();
  const desktopPresent = await store.desktopFilePresent();
  const install = await detectAppInstall(variant);
  const prime = await primeCatalog(context);
  const candidates = desktopAuthCandidates(variant);
  const storage = await describeCredentialStorage(store);
  const hints = [];
  if (signIn.state !== "signed-in") hints.push(`Sign in once in the ${variant.appName} desktop app, then rerun.`);
  if (!desktopPresent) {
    hints.push(`No ${variant.appName} auth file at the expected path; set ${variant.env} if it lives elsewhere.`);
  }
  if (install.installed === false && install.source === "not-found") {
    hints.push(`The ${variant.appName} desktop app was not found at the usual install location.`);
  }
  if (storage.format === "encrypted" && !storage.keyHelperAvailable) {
    hints.push(
      `The credential is encrypted (WorkBuddy 5.6+) and no usable app binary was found for the key helper; set ${WORKBUDDY_ELECTRON_BIN_ENV} to the ${variant.appName} executable.`,
    );
  }
  if (storage.format === "unrecognized") {
    hints.push(
      `The ${variant.appName} auth file is not in a format this tool recognizes; it may be from an unsupported app version.`,
    );
  }
  if (catalog.provenance().source === "builtin") {
    hints.push("Serving the compiled-in model roster; run `models` while online to refresh it.");
  }

  const report = {
    schemaVersion: SCHEMA_VERSION,
    tool: "workbuddy-api",
    version: VERSION,
    node: process.version,
    platform: process.platform,
    provider: variant.id,
    displayName: variant.displayName,
    region: variant.region,
    stateHome: stateHome(),
    configPath: configPath(),
    logPath: logPath(),
    signIn: signIn.state,
    ...(signIn.reason === undefined ? {} : { signInReason: signIn.reason }),
    desktopAuthFile: {
      candidates,
      present: desktopPresent,
      env: variant.env,
      envValue: process.env[variant.env] === undefined ? "(unset)" : "(set)",
      ...storage,
    },
    keyHelper: {
      env: WORKBUDDY_ELECTRON_BIN_ENV,
      envValue: process.env[WORKBUDDY_ELECTRON_BIN_ENV] === undefined ? "(unset)" : "(set)",
      candidates: await defaultElectronPaths(),
    },
    ownAuthFile: { path: ownAuthPath(variant), present: existsSync(ownAuthPath(variant)) },
    catalogFile: { path: catalogPath(variant), present: existsSync(catalogPath(variant)) },
    desktopApp: install,
    catalog: catalog.provenance(),
    hints,
  };

  if (flags["json"] === true) {
    process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);
    return signIn.state === "signed-in" ? 0 : 1;
  }

  process.stdout.write(
    [
      `workbuddy-api ${VERSION} on ${process.version} (${process.platform})`,
      `Provider: ${variant.id} (${variant.region})`,
      `Sign-in: ${signIn.state}${signIn.reason === undefined ? "" : ` — ${signIn.reason}`}`,
      `Desktop auth file: ${desktopPresent ? "present" : "missing"}`,
      ...candidates.map((candidate) => `  candidate: ${candidate}`),
      `Tool credential copy: ${existsSync(ownAuthPath(variant)) ? "present" : "absent"} (${ownAuthPath(variant)})`,
      `Desktop app: ${install.installed ? `found (${install.version ?? "unknown version"}, via ${install.source})` : install.source}`,
      `Credential format: ${storage.format}${storage.detail === undefined ? "" : ` — ${storage.detail}`}`,
      `Catalog: ${catalog.provenance().source} (${catalog.base().length} models)` +
        (prime.reason === undefined ? "" : ` — ${prime.reason}`),
      ...hints.map((hint) => `Hint: ${hint}`),
      "",
    ].join("\n"),
  );
  return signIn.state === "signed-in" ? 0 : 1;
}

/** `logout` — drop this tool's credential copy only. */
async function commandLogout(flags, config) {
  const { variant, store } = await makeContext(flags, config);
  await store.logout();
  process.stdout.write(
    `${variant.displayName}: removed ${store.ownAuthPath()}; the desktop app's sign-in is untouched\n`,
  );
  return 0;
}

/** `config` — read or write the persisted configuration. */
async function commandConfig(flags) {
  const current = await loadConfig();
  const set = flags["set"];
  const unset = flags["unset"];

  if (set === undefined && unset === undefined) {
    process.stdout.write(`${JSON.stringify({ path: configPath(), ...current }, null, 2)}\n`);
    return 0;
  }

  const patch = {};
  if (typeof set === "string") {
    const equals = set.indexOf("=");
    if (equals === -1) {
      process.stderr.write(`config: --set expects key=value, got ${JSON.stringify(set)}\n`);
      return 1;
    }
    const key = set.slice(0, equals).trim();
    const value = set.slice(equals + 1);
    if (!(key in current)) {
      process.stderr.write(`config: unknown key ${JSON.stringify(key)}; expected ${Object.keys(current).join(", ")}\n`);
      return 1;
    }
    patch[key] = key === "port" ? Number(value) : value;
    if (key === "port" && !Number.isInteger(patch[key])) {
      process.stderr.write(`config: port must be an integer\n`);
      return 1;
    }
  }
  if (typeof unset === "string") {
    if (!(unset in current)) {
      process.stderr.write(`config: unknown key ${JSON.stringify(unset)}\n`);
      return 1;
    }
    patch[unset] = unset === "port" ? 8787 : "";
  }

  const next = await saveConfig(patch);
  process.stdout.write(`${JSON.stringify({ path: configPath(), ...next }, null, 2)}\n`);
  return 0;
}

/** `serve` — the OpenAI-compatible gateway. */
async function commandServe(flags, config) {
  const context = await makeContext(flags, config);
  const { variant, client, store, catalog } = context;

  const port = flags["port"] === undefined ? config.port : Number(flags["port"]);
  if (!Number.isInteger(port) || port < 0 || port > 65535) {
    process.stderr.write(`serve: invalid --port ${JSON.stringify(flags["port"])}\n`);
    return 1;
  }
  const host = typeof flags["host"] === "string" ? flags["host"] : config.host;
  const noAuth = flags["no-auth"] === true;
  const isLoopback = host === "127.0.0.1" || host === "localhost" || host === "::1";
  if (noAuth && !isLoopback) {
    process.stderr.write("serve: --no-auth is only allowed on a loopback bind\n");
    return 1;
  }
  const configuredKey = typeof flags["api-key"] === "string" ? flags["api-key"] : config.apiKey;
  const apiKey = noAuth ? "" : configuredKey !== "" ? configuredKey : randomBytes(24).toString("base64url");
  const quiet = flags["quiet"] === true;

  const log = quiet ? () => {} : (message) => process.stderr.write(`[workbuddy] ${message}\n`);

  // 优先级：命令行 > 环境变量 > 配置文件。
  // Claude Code 发的是 claude-* 、Codex 发的是 gpt-*，花名册里都没有，
  // 所以这两种客户端必须有一个兜底模型，否则每次请求都会 400。
  const defaultModel =
    typeof flags["default-model"] === "string"
      ? flags["default-model"]
      : process.env["WORKBUDDY_DEFAULT_MODEL"]?.trim() || config.defaultModel;

  // 网关走 router：保险库里选了号就用它，没选就用桌面 App 当前登录的号
  const gateway = createGateway({ client, store: context.router, catalog, variant, apiKey, log, defaultModel });

  // Prime the catalog before announcing readiness so `GET /v1/models` is
  // already live for the first client that connects.
  const prime = await primeCatalog(context);
  if (prime.refreshed) log(`catalog: ${catalog.base().length} models from upstream`);
  else log(`catalog: ${catalog.provenance().source} (${prime.reason ?? "no refresh"})`);

  let bound;
  try {
    bound = await gateway.listen(port, host);
  } catch (error) {
    process.stderr.write(`serve: cannot bind ${host}:${port} — ${safeMessage(error)}\n`);
    return 1;
  }

  const baseUrl = `http://${bound.host}:${bound.port}/v1`;
  process.stdout.write(
    [
      `workbuddy-api ${VERSION} — ${variant.displayName}`,
      `  base_url : ${baseUrl}`,
      `  api_key  : ${apiKey === "" ? "(none — loopback only)" : apiKey}`,
      `  models   : ${catalog.base().length} (${catalog.provenance().source})`,
      `  sign-in  : ${(await store.status()).state}`,
      "",
      "Point any OpenAI-compatible client at the base_url above.",
      "Ctrl+C to stop.",
      "",
    ].join("\n"),
  );

  // Keep the roster and the token fresh while serving.
  const poll = setInterval(async () => {
    const before = (await store.status()).state;
    const result = await primeCatalog(context);
    const after = (await store.status()).state;
    if (before !== after) log(`sign-in: ${before} -> ${after}`);
    else if (result.refreshed) log(`catalog refreshed: ${catalog.base().length} models`);
  }, 5 * 60_000);
  poll.unref?.();

  const shutdown = async (signal) => {
    clearInterval(poll);
    log(`received ${signal}; shutting down`);
    await gateway.close();
    process.exit(0);
  };
  process.on("SIGINT", () => void shutdown("SIGINT"));
  process.on("SIGTERM", () => void shutdown("SIGTERM"));

  // Never resolve: the caller's await is the server's lifetime.
  await new Promise(() => {});
  return 0;
}

/** Entry point. */
// ── 多账号 ──────────────────────────────────────────────

/** 中英文混排的对齐：中文按两个字符宽算。 */
function displayWidth(text) {
  let width = 0;
  for (const char of text) width += char.codePointAt(0) > 0x2e80 ? 2 : 1;
  return width;
}

function pad(text, width) {
  const value = text ?? "";
  return value + " ".repeat(Math.max(0, width - displayWidth(value)));
}

/** 允许只写 id 前缀，免得让用户拄一整串 `uid:enterpriseId`。 */
async function resolveAccountId(vault, token) {
  const list = await vault.list();
  if (list.some((account) => account.id === token)) return token;
  const matches = list.filter((account) => account.id.startsWith(token));
  if (matches.length === 1) return matches[0].id;
  if (matches.length === 0) throw new Error(`没有 id 以 ${JSON.stringify(token)} 开头的账号`);
  throw new Error(`${JSON.stringify(token)} 匹配到 ${matches.length} 个账号，多写几位`);
}

/** 拉每个账号的积分和签到状态。单个账号出错不影响其它账号。 */
async function collectAccountStates({ variant, client, vault }, { withCheckin }) {
  const list = await vault.list();
  const out = [];

  for (const account of list) {
    const entry = { ...account, credits: null, checkin: null, error: null };
    try {
      const credential = await vault.credentialFor(account.id);
      if (credential === undefined) {
        entry.error = "这个账号已经不在库里了";
        out.push(entry);
        continue;
      }
      const credits = await client.fetchCredits(credential);
      if (typeof credits.total === "number") entry.credits = credits.total;
      if (credits.unlimited === true) entry.creditsUnlimited = true;
      if (typeof credits.error === "string") entry.error = credits.error;
      if (withCheckin) entry.checkin = await fetchCheckinStatus(client, variant, credential);
    } catch (error) {
      entry.error = safeMessage(error);
    }
    out.push(entry);
  }

  return out;
}

/** 给终端看的一张表。 */
function renderAccounts(states, activeId) {
  const rows = states.map((account) => {
    const name = account.label || account.nickname || account.uid.slice(0, 8);
    const checkin = account.checkin;
    const checkinText = checkin === null
      ? "—"
      : checkin.todayCheckedIn
        ? `已签 连续${checkin.streakDays}天`
        : `未签 连续${checkin.streakDays}天`;
    const left = account.refreshDaysLeft;
    const renewText = left === null || left === undefined
      ? "未知"
      : left < 0
        ? "已失效"
        : `${left}天`;
    return [
      account.id === activeId ? "*" : " ",
      name,
      account.creditsUnlimited === true ? "无限" : account.credits === null ? "—" : String(account.credits),
      checkinText,
      renewText,
      account.error ?? "",
    ];
  });

  const widths = [1, 14, 8, 16, 8].map((min, column) =>
    Math.max(min, ...rows.map((row) => displayWidth(row[column]))),
  );

  const lines = [
    [" ", "账号", "积分", "签到", "可续期"].map((cell, i) => pad(cell, widths[i])).join("  "),
    "-".repeat(widths.reduce((sum, w) => sum + w, 0) + 10),
  ];
  for (const row of rows) {
    lines.push([0, 1, 2, 3, 4].map((i) => pad(row[i], widths[i])).join("  ") + (row[5] ? `  ${row[5]}` : ""));
  }
  lines.push(
    "",
    "* = 网关当前使用的账号",
    "可续期 = 登录还能自动续多久。到期前跑一次 `accounts renew` 就能再续 60 天，",
    "        所以只要定期续，永远不会掉登录。",
  );
  return lines.join("\n");
}

/** `accounts` — 多账号管理。 */
async function commandAccounts(flags, config) {
  const context = await makeContext(flags, config);
  const { variant, vault, store } = context;
  const json = flags["json"] === true;
  const [action, ...rest] = flags._;

  if (action === "add") {
    // 从桌面 App **当前登录**的那个号导入。换号就在 App 里换一次，再跑一遍这个命令。
    const label = typeof flags["label"] === "string" ? flags["label"] : "";
    const credential = await store.resolve();
    const before = await vault.list();
    const { id, created } = await vault.upsert(credential, { label, makeActive: before.length === 0 });
    const message = created ? `已导入账号 ${id}` : `账号 ${id} 已存在，信息已更新`;
    if (json) process.stdout.write(`${JSON.stringify({ ok: true, id, created })}\n`);
    else process.stdout.write(`${message}\n`);
    return 0;
  }

  if (action === "use") {
    if (rest.length === 0) {
      process.stderr.write("accounts use: 需要一个账号 id（写前几位就行）\n");
      return 1;
    }
    const id = await resolveAccountId(vault, rest[0]);
    await vault.setActive(id);
    if (json) process.stdout.write(`${JSON.stringify({ ok: true, activeId: id })}\n`);
    else process.stdout.write(`网关现在使用：${id}\n`);
    return 0;
  }

  if (action === "clear") {
    await vault.clearActive();
    process.stdout.write("已取消选定；网关退回使用桌面 App 当前登录的账号\n");
    return 0;
  }

  if (action === "remove") {
    if (rest.length === 0) {
      process.stderr.write("accounts remove: 需要一个账号 id\n");
      return 1;
    }
    const id = await resolveAccountId(vault, rest[0]);
    await vault.remove(id);
    process.stdout.write(`已删除 ${id}\n`);
    return 0;
  }

  if (action === "rename") {
    if (rest.length < 2) {
      process.stderr.write("accounts rename: 需要 <id> <新名字>\n");
      return 1;
    }
    const id = await resolveAccountId(vault, rest[0]);
    await vault.rename(id, rest.slice(1).join(" "));
    process.stdout.write(`已重命名 ${id}\n`);
    return 0;
  }

  if (action === "renew") {
    // 主动续期：服务端每次刷新都会重新给一个满额的 refreshExpiresIn，
    // 所以只要在窗口内刷一次，就又能续 60 天。
    const force = flags["force"] === true;
    const list = await vault.list();
    const targets = rest.length > 0
      ? [await resolveAccountId(vault, rest[0])]
      : list.map((account) => account.id);

    if (targets.length === 0) {
      process.stderr.write("accounts renew: 保险库里没有账号\n");
      return 1;
    }

    const results = [];
    for (const id of targets) {
      const account = list.find((item) => item.id === id);
      const name = account?.label || account?.nickname || id.slice(0, 8);
      try {
        const outcome = await vault.renewIfStale(id, { force });
        results.push({
          id,
          label: name,
          ok: true,
          renewed: outcome.renewed,
          daysLeft: outcome.daysLeft,
          msg: outcome.renewed ? `已续期，现在还能续 ${outcome.daysLeft} 天` : `还够（${outcome.daysLeft} 天），这次不用续`,
        });
      } catch (error) {
        results.push({ id, label: name, ok: false, renewed: false, daysLeft: null, msg: safeMessage(error) });
      }
    }

    if (json) {
      process.stdout.write(
        `${JSON.stringify({ schemaVersion: SCHEMA_VERSION, provider: variant.id, data: results }, null, 2)}\n`,
      );
    } else {
      for (const result of results) {
        const mark = result.ok ? (result.renewed ? "✔" : "·") : "✘";
        process.stdout.write(`${mark} ${pad(result.label, 14)} ${result.msg}\n`);
      }
      const renewed = results.filter((result) => result.renewed).length;
      process.stdout.write(`\n共 ${results.length} 个账号，本次续期 ${renewed} 个\n`);
    }

    return results.every((result) => result.ok) ? 0 : 1;
  }

  if (action === "export") return await accountsExport(flags, context);

  if (action === "import") return await accountsImport(flags, context, rest[0]);

  if (action !== undefined && action !== "list") {
    process.stderr.write(`accounts: 未知子命令 ${JSON.stringify(action)}；可用 add / list / use / clear / remove / rename / renew / export / import\n`);
    return 1;
  }

  // 默认：列表
  const activeId = await vault.activeId();
  const states = await collectAccountStates(context, { withCheckin: true });
  if (json) {
    process.stdout.write(
      `${JSON.stringify({
        schemaVersion: SCHEMA_VERSION,
        provider: variant.id,
        activeId,
        vaultPath: accountsPath(variant),
        desktopAuthFile: store.desktopAuthPath(),
        data: states,
      }, null, 2)}\n`,
    );
    return 0;
  }

  if (states.length === 0) {
    process.stdout.write(
      [
        "保险库是空的。",
        `先登录一个号，然后：workbuddy-api accounts add --label 主号`,
        `（想在 App 里换号再导入一次，就能攒出多个）`,
        "",
      ].join("\n"),
    );
    return 1;
  }

  process.stdout.write(`${renderAccounts(states, activeId)}\n`);
  return 0;
}

/** `checkin` — 给保险库里的账号签到。默认全部。 */
async function commandCheckin(flags, config) {
  const context = await makeContext(flags, config);
  const { variant, client, vault } = context;
  const json = flags["json"] === true;

  const list = await vault.list();
  if (list.length === 0) {
    process.stderr.write("保险库里没有账号。先跑：workbuddy-api accounts add\n");
    return 1;
  }

  let targets = list;
  if (typeof flags["id"] === "string") {
    const id = await resolveAccountId(vault, flags["id"]);
    targets = list.filter((account) => account.id === id);
  }

  const results = [];
  for (const account of targets) {
    const name = account.label || account.nickname || account.uid.slice(0, 8);
    try {
      // 顺手续期：签到的频率（通常每天）远高于 60 天窗口，所以只要在跑就不会掉登录
      let renewed = false;
      let renewError;
      try {
        renewed = (await vault.renewIfStale(account.id)).renewed;
      } catch (error) {
        // 续期失败不拦住签到（下面用旧 token 照样能试），但**必须记下来** ——
        // 吞掉错误会让「为什么没续上」变成谜案。
        renewError = safeMessage(error);
      }

      const credential = await vault.credentialFor(account.id);
      if (credential === undefined) throw new Error("账号已不在库里");
      const outcome = await ensureCheckedIn(client, variant, credential);
      if (outcome.claimed) await vault.markCheckedIn(account.id);
      results.push({
        id: account.id,
        label: name,
        ok: outcome.ok,
        claimed: outcome.claimed,
        renewed,
        ...(renewError === undefined ? {} : { renewError }),
        code: outcome.code,
        msg: renewError === undefined ? outcome.msg : `${outcome.msg}（续期失败：${renewError}）`,
        checkin: outcome.status,
      });
    } catch (error) {
      results.push({
        id: account.id,
        label: name,
        ok: false,
        claimed: false,
        renewed: false,
        code: -1,
        msg: safeMessage(error),
        checkin: null,
      });
    }
  }

  if (json) {
    process.stdout.write(
      `${JSON.stringify({ schemaVersion: SCHEMA_VERSION, provider: variant.id, data: results }, null, 2)}\n`,
    );
  } else {
    for (const result of results) {
      const mark = result.ok ? (result.renewed ? "✔" : "·") : "✘";
      process.stdout.write(`${mark} ${pad(result.label, 14)} ${result.msg}\n`);
    }
    const claimed = results.filter((result) => result.claimed).length;
    process.stdout.write(`\n共 ${results.length} 个账号，本次新签到 ${claimed} 个\n`);
  }

  return results.every((result) => result.ok) ? 0 : 1;
}

// ── 账号迁移 ──────────────────────────────────────────

/**
 * 共享的 stdin 行读取器。
 *
 * 关键点：不能每次提问都新建一个 readline —— 多个 readline 会互相抢
 * 管道里的缓冲，第二次读就拿到空字符串（第一次已经把数据吃走了）。
 */
let lineReader;
const lineQueue = [];
let lineWaiter = null;
let stdinEnded = false;

function getLineReader() {
  if (lineReader !== undefined) return lineReader;

  const isTty = process.stdin.isTTY === true;
  const rl = createInterface({ input: process.stdin, output: process.stderr, terminal: isTty });

  if (isTty) {
    // 口令不回显；管道输入本来就不会显到屏幕上
    const original = rl._writeToOutput.bind(rl);
    rl._writeToOutput = (text) => {
      if (typeof text === "string" && text.includes(":")) original(text);
    };
  }

  // 持续监听、先排队再分发。
  // 反面教材：每次提问才挂一个 once("line") —— 两行同时到达时，
  // 第二行发出的一瞬没人监听，就被 readline 丢掉了。
  rl.on("line", (line) => {
    if (lineWaiter !== null) {
      const resolve = lineWaiter;
      lineWaiter = null;
      resolve(line);
      return;
    }
    lineQueue.push(line);
  });

  rl.on("close", () => {
    stdinEnded = true;
    if (lineWaiter !== null) {
      const resolve = lineWaiter;
      lineWaiter = null;
      resolve("");
    }
  });

  lineReader = rl;
  return rl;
}

function closeLineReader() {
  if (lineReader === undefined) return;
  try {
    lineReader.close();
  } catch {
    // 已经关了
  }
  lineReader = undefined;
}

/** 提一个问，读一行。stdin 提前结束就返回空串（下面会当口令太短报错）。 */
function askLine(prompt) {
  getLineReader();
  process.stderr.write(prompt);

  if (lineQueue.length > 0) return Promise.resolve(lineQueue.shift());
  if (stdinEnded) return Promise.resolve("");

  return new Promise((resolve) => {
    lineWaiter = resolve;
  });
}

/**
 * 拿到口令：优先 `--pass`，否则从 stdin 读。
 *
 * 界面那边把口令写到子进程的 stdin，比放在命令行参数里安全。
 */
async function readPassphrase(flags, { confirm }) {
  if (typeof flags["pass"] === "string" && flags["pass"] !== "") return flags["pass"];

  try {
    const first = await askLine(`口令（至少 ${MIN_PASSPHRASE_LENGTH} 位）: `);
    if (confirm !== true) return first;

    const second = await askLine("再输一遍确认: ");
    if (!samePassphrase(first, second)) throw new Error("两次输入的口令不一样");
    return first;
  } finally {
    // 读完就关，否则 stdin 挂着会让进程退不掉
    if (lineReader !== undefined) closeLineReader();
  }
}

/** `accounts export` — 导出成加密文件。 */
async function accountsExport(flags, context) {
  const { variant, vault } = context;
  const json = flags["json"] === true;

  const document = await vault.load();
  if (document.accounts.length === 0) {
    process.stderr.write("保险库里没有账号，没东西可导出。先跑 accounts add。\n");
    return 1;
  }

  const passphrase = await readPassphrase(flags, { confirm: true });
  const sealed = sealAccounts(document.accounts, passphrase, { provider: variant.id });

  const target = typeof flags["out"] === "string" && flags["out"] !== ""
    ? flags["out"]
    : join(process.cwd(), suggestedFileName(variant.id));

  // 0600：里面是账号凭据（加密过的，但少给别人看总没坏处）
  await writeFile(target, `${JSON.stringify(sealed, null, 2)}\n`, { encoding: "utf8", mode: 0o600 });

  if (json) {
    process.stdout.write(`${JSON.stringify({ ok: true, path: target, accounts: document.accounts.length })}\n`);
  } else {
    process.stdout.write(
      [
        `已导出 ${document.accounts.length} 个账号 →`,
        `  ${target}`,
        "",
        `这个文件是用口令加密的（scrypt + AES-256-GCM）。`, 
        "口令丢了就解不开，只能重新扫码；口令泄露就等于把这些号的额度给出去了。",
        "",
      ].join("\n"),
    );
  }
  return 0;
}

/** `accounts import` — 把导出文件合并进本机保险库，并立刻验证能不能刷新。 */
async function accountsImport(flags, context, file) {
  const { variant, vault, client } = context;
  const json = flags["json"] === true;

  if (typeof file !== "string" || file === "") {
    process.stderr.write("accounts import: 需要导出文件的路径\n");
    return 1;
  }

  let raw;
  try {
    raw = await readFile(file, "utf8");
  } catch (error) {
    process.stderr.write(`打不开文件 ${file}：${safeMessage(error)}\n`);
    return 1;
  }

  let document;
  try {
    document = JSON.parse(raw);
  } catch {
    process.stderr.write("这个文件不是 JSON，是不是拿错了？\n");
    return 1;
  }

  const passphrase = await readPassphrase(flags, { confirm: false });
  const opened = openAccounts(document, passphrase);

  if (opened.provider !== "" && opened.provider !== variant.id) {
    process.stderr.write(
      `这份导出属于 ${opened.provider}，而当前处理的是 ${variant.id}。` +
        `换一个版本再导：--provider ${opened.provider}\n`,
    );
    return 1;
  }

  // 合并（同一个号就更新 token，本地已起的名字不会被覆盖）
  const imported = [];
  for (const record of opened.accounts) {
    if (typeof record?.uid !== "string" || record.uid === "") continue;
    const credential = {
      uid: record.uid,
      enterpriseId: typeof record.enterpriseId === "string" ? record.enterpriseId : "",
      domain: typeof record.domain === "string" ? record.domain : "",
      ...(typeof record.nickname === "string" ? { nickname: record.nickname } : {}),
      accessToken: typeof record.accessToken === "string" ? record.accessToken : "",
      ...(typeof record.refreshToken === "string" ? { refreshToken: record.refreshToken } : {}),
      expiresAtMs: typeof record.expiresAtMs === "number" ? record.expiresAtMs : 0,
      ...(typeof record.refreshExpiresAtMs === "number" ? { refreshExpiresAtMs: record.refreshExpiresAtMs } : {}),
    };
    const { id } = await vault.upsert(credential, {
      ...(typeof record.label === "string" && record.label !== "" ? { label: record.label } : {}),
    });
    imported.push({ id, label: record.label ?? "", uid: record.uid });
  }

  if (imported.length === 0) {
    process.stderr.write("文件里没有可用的账号\n");
    return 1;
  }

  // 保险库原来是空的就把第一个设为当前使用
  if ((await vault.activeId()) === "") await vault.setActive(imported[0].id);

  // 立刻验一遍：能刷新才叫「无缝接上」
  const results = [];
  for (const item of imported) {
    try {
      const outcome = await vault.renewIfStale(item.id, { force: true });
      results.push({ ...item, ok: true, daysLeft: outcome.daysLeft, msg: `可以继续使用，还能自动续 ${outcome.daysLeft} 天` });
    } catch (error) {
      results.push({ ...item, ok: false, daysLeft: null, msg: safeMessage(error) });
    }
  }

  if (json) {
    process.stdout.write(
      `${JSON.stringify({ schemaVersion: SCHEMA_VERSION, provider: variant.id, data: results }, null, 2)}\n`,
    );
  } else {
    for (const result of results) {
      const mark = result.ok ? "✔" : "✘";
      process.stdout.write(`${mark} ${pad(result.label || result.uid.slice(0, 8), 14)} ${result.msg}\n`);
    }
    const usable = results.filter((result) => result.ok).length;
    process.stdout.write(`\n导入 ${results.length} 个，可正常使用 ${usable} 个\n`);
    if (usable < results.length) {
      process.stdout.write("失败的那些可能是服务端风控（换机器/换 IP 会拦）或令牌已作废，重新登录一次就好。\n");
    }
  }

  return results.every((result) => result.ok) ? 0 : 1;
}

async function run(argv) {
  const { command, flags } = parseArgv(argv);
  if (command === undefined || command === "--help" || command === "-h" || command === "help") {
    printHelp();
    return 0;
  }
  if (command === "--version" || command === "-v" || command === "version") {
    process.stdout.write(`${VERSION}\n`);
    return 0;
  }

  const known = ["serve", "status", "models", "doctor", "logout", "config", "accounts", "checkin"];
  if (!known.includes(command)) {
    process.stderr.write(`workbuddy-api: expected ${known.join(", ")}; got ${JSON.stringify(command)}\n`);
    return 1;
  }

  const config = await loadConfig();
  try {
    switch (command) {
      case "serve":
        return await commandServe(flags, config);
      case "status":
        return await commandStatus(flags, config);
      case "models":
        return await commandModels(flags, config);
      case "doctor":
        return await commandDoctor(flags, config);
      case "logout":
        return await commandLogout(flags, config);
      case "config":
        return await commandConfig(flags);
      case "accounts":
        return await commandAccounts(flags, config);
      case "checkin":
        return await commandCheckin(flags, config);
      default:
        printHelp();
        return 1;
    }
  } catch (error) {
    process.stderr.write(`workbuddy-api: ${command} failed: ${safeMessage(error)}\n`);
    return 1;
  }
}

export { run };

// Only execute when invoked as a program, not when imported by tests.
const invokedDirectly =
  process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href;
if (invokedDirectly) process.exitCode = await run(process.argv.slice(2));
