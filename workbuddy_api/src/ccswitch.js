/**
 * CC Switch 集成。
 *
 * ## 为什么需要
 *
 * CC Switch 是「供应商管理器」：每个供应商的配置存在它自己的库里，
 * 切换时**整体覆盖**对应 agent 的配置文件。所以如果我们只改 agent 的配置文件、
 * 不告诉 CC Switch，会有两个后果：
 *
 *   1. 用户在 CC Switch 里一切换，我们写的东西就被覆盖掉
 *   2. CC Switch 的列表里看不见 WorkBuddy，用户以为没配
 *
 * ## 做法：镜像，不重复实现
 *
 * 本模块**不自己算配置内容**，而是把当前 agent 配置文件里已有的那份
 * 原样读出来写进 CC Switch 的库。这样：
 *
 *   - 内容永远和实际生效的配置一致（不管是谁写的）
 *   - 不需要在 JS 和 C# 里各实现一遍 TOML 生成之类的逻辑
 *
 * ```
 * ~/.claude/settings.json          → providers['claude',  'workbuddy-local']  { env }
 * ~/.codex/config.toml + auth.json → providers['codex',   'workbuddy-local']  { auth, config }
 * ~/.pi/agent/models.json          → providers['pi',      'workbuddy-local']  { ...provider }
 * ```
 *
 * ## 库结构（实测）
 *
 * `~/.cc-switch/cc-switch.db`，SQLite，`journal_mode=delete`，`busy_timeout=5000`。
 * `providers` 表主键是 `(id, app_type)` 复合键。
 *
 * ## 两条硬约束
 *
 * - **不动 `is_current`**：这是“用户现在用哪个供应商”，切换该是用户自己的决定。
 * - **不改别人的条目**：只 upsert 自己那条（id 固定），其它供应商原样不碰。
 *
 * @module workbuddy-api/ccswitch
 */

import { copyFileSync, existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { homedir } from "node:os";

/** 本项目在 CC Switch 里的供应商 id。多个 app_type 共用一个名字。 */
export const PROVIDER_ID = "workbuddy-local";
export const PROVIDER_NAME = "WorkBuddy 本机网关";

/** 我们覆盖得到的 app_type。 */
export const SUPPORTED_APP_TYPES = ["claude", "codex", "pi"];

/** 库文件位置。`CCSWITCH_DB` 可覆盖，方便测试。 */
export function defaultDbPath() {
  const override = process.env["CCSWITCH_DB"];
  if (override !== undefined && override.trim() !== "") return override.trim();
  return join(homedir(), ".cc-switch", "cc-switch.db");
}

/** CC Switch 装没装。 */
export function isAvailable(dbPath = defaultDbPath()) {
  return existsSync(dbPath);
}

/** 当前 agent 配置文件的位置，跟着 `ClientIntegrator` 走。 */
export function agentConfigPaths(home = homedir()) {
  return {
    claudeSettings: join(home, ".claude", "settings.json"),
    codexConfig: join(home, ".codex", "config.toml"),
    codexAuth: join(home, ".codex", "auth.json"),
    piModels: join(home, ".pi", "agent", "models.json"),
  };
}

// ── 读：把 agent 文件里的现状抽成 CC Switch 的条目 ─────────────

function readJson(path) {
  if (!existsSync(path)) return undefined;
  try {
    const text = readFileSync(path, "utf8").trim();
    if (text === "") return undefined;
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}

function readText(path) {
  if (!existsSync(path)) return undefined;
  try {
    return readFileSync(path, "utf8");
  } catch {
    return undefined;
  }
}

/**
 * 这条供应商是不是指向本机网关。
 *
 * 不靠 id 认人 —— 用户给供应商起的 id 五花八门（实测 claude 那条叫 `default`，
 * pi 那条叫 `workbuddy-local`）。固定 id 会导致：要么重复建一条，
 * 要么根本找不到用户已有的那条。
 *
 * 改用“内容里有没有回环地址”来判，因为本机网关必须是 127.0.0.1/localhost。
 */
const LOOPBACK = /(?:127\.0\.0\.1|localhost|\[::1\]):\d+/iu;

export function pointsAtLocalGateway(settingsConfig) {
  try {
    const text = typeof settingsConfig === "string" ? settingsConfig : JSON.stringify(settingsConfig);
    return LOOPBACK.test(text);
  } catch {
    return false;
  }
}

/**
 * 扫一遍库，找出「属于本机网关」的条目。
 *
 * @returns {Promise<Array<{id: string, appType: string, name: string, isCurrent: boolean, settingsConfig: object}>>}
 */
async function findLocalGatewayEntries(dbPath) {
  const { DatabaseSync } = await import("node:sqlite");
  const db = new DatabaseSync(dbPath, { readOnly: true });
  try {
    const rows = db
      .prepare("select id, app_type, name, is_current, settings_config from providers")
      .all();

    return rows
      .filter((row) => pointsAtLocalGateway(row["settings_config"]))
      .map((row) => ({
        id: row["id"],
        appType: row["app_type"],
        name: row["name"],
        isCurrent: row["is_current"] === 1,
        settingsConfig: JSON.parse(row["settings_config"]),
      }));
  } finally {
    db.close();
  }
}

/**
 * 从当前 agent 配置文件里抽出要镜像进 CC Switch 的条目。
 *
 * 读不到的那一项就跳过 —— 比如用户没装 Codex，就不该凭空造一条 Codex 供应商。
 *
 * @returns {Array<{appType: string, settingsConfig: object, present: boolean}>}
 */
export function buildEntriesFromAgentFiles(paths = agentConfigPaths()) {
  const entries = [];

  // ── Claude Code：CC Switch 只关心 settings.json 的 env 字段 ──
  const claude = readJson(paths.claudeSettings);
  const claudeEnv = claude?.["env"];
  if (claudeEnv !== undefined && typeof claudeEnv === "object" && claudeEnv !== null) {
    entries.push({ appType: "claude", settingsConfig: { env: claudeEnv }, present: true });
  } else {
    entries.push({ appType: "claude", settingsConfig: undefined, present: false });
  }

  // ── Codex：config.toml 是整段文本，auth.json 是密钥 ──
  const codexToml = readText(paths.codexConfig);
  const codexAuth = readJson(paths.codexAuth);
  if (codexToml !== undefined) {
    entries.push({
      appType: "codex",
      settingsConfig: {
        auth: typeof codexAuth === "object" && codexAuth !== null ? codexAuth : {},
        config: codexToml,
      },
      present: true,
    });
  } else {
    entries.push({ appType: "codex", settingsConfig: undefined, present: false });
  }

  // ── pi：models.json 里我们那条 provider 原样搬 ──
  const pi = readJson(paths.piModels);
  const piProvider = pi?.["providers"]?.[PROVIDER_ID];
  if (piProvider !== undefined && typeof piProvider === "object" && piProvider !== null) {
    entries.push({ appType: "pi", settingsConfig: piProvider, present: true });
  } else {
    entries.push({ appType: "pi", settingsConfig: undefined, present: false });
  }

  return entries;
}

// ── 查：只读看一眼现状 ────────────────────────────────────────

export async function inspect(dbPath = defaultDbPath()) {
  if (!isAvailable(dbPath)) return { ok: false, reason: `没找到 CC Switch 的库：${dbPath}` };

  try {
    const { DatabaseSync } = await import("node:sqlite");
    const db = new DatabaseSync(dbPath, { readOnly: true });
    try {
      const rows = db
        .prepare("select id, app_type, name, is_current, settings_config, length(settings_config) as size from providers")
        .all();

      return {
        ok: true,
        appTypes: [...new Set(rows.map((row) => row["app_type"]))].sort(),
        entries: rows.map((row) => ({
          id: row["id"],
          appType: row["app_type"],
          name: row["name"],
          isCurrent: row["is_current"] === 1,
          // “是不是指向本机网关”用内容判，不靠 id —— 用户给供应商起的 id 五花八门
          isOurs:
            row["id"] === PROVIDER_ID || pointsAtLocalGateway(row["settings_config"]),
          size: row["size"],
        })),
      };
    } finally {
      db.close();
    }
  } catch (error) {
    return { ok: false, reason: String(error instanceof Error ? error.message : error) };
  }
}

// ── 写 ────────────────────────────────────────────────────────

/**
 * 写入（或更新）指向本机网关的供应商条目。
 *
 * **认领而不是新建**：如果某个 app_type 下已经有一条指向本机网关的供应商
 * （不管它叫什么 id、什么名字），就更新那一条；没有才用默认 id 新建。
 * 这样不会出现“两个 WorkBuddy 供应商”的迷惑局面。
 *
 * @param {object} options
 * @param {Array<{appType: string, settingsConfig: object}>} options.entries
 * @param {string} [options.dbPath]
 * @param {boolean} [options.backup] 写前备份库文件（默认 true）
 * @returns {Promise<{ok: boolean, written: string[], created: string[], adopted: object[], backupPath?: string, reason?: string}>}
 */
export async function upsertProviders(options) {
  const dbPath = options.dbPath ?? defaultDbPath();
  const usable = options.entries.filter((entry) => entry.settingsConfig !== undefined);

  if (!isAvailable(dbPath)) {
    return { ok: false, written: [], created: [], adopted: [], reason: `没找到 CC Switch 的库：${dbPath}` };
  }
  if (usable.length === 0) {
    return { ok: false, written: [], created: [], adopted: [], reason: "没有可写的条目（agent 配置文件都没读到）" };
  }

  let backupPath;
  if (options.backup !== false) {
    try {
      // CC Switch 自己要写的时候会备份，但我们是外部写入 —— 自己先留一份能回滚的
      const stamp = new Date().toISOString().replace(/[:.]/gu, "-");
      backupPath = `${dbPath}.before-workbuddy-${stamp}`;
      copyFileSync(dbPath, backupPath);
    } catch (error) {
      return {
        ok: false,
        written: [],
        created: [],
        adopted: [],
        reason: `备份失败，为了安全没有继续写：${error instanceof Error ? error.message : error}`,
      };
    }
  }

  const written = [];
  const created = [];
  const adopted = [];

  try {
    // 先扫一遍看哪些是他已有的（只读连接）
    const existing = await findLocalGatewayEntries(dbPath);
    const byAppType = new Map();
    for (const item of existing) byAppType.set(item.appType, item);

    const { DatabaseSync } = await import("node:sqlite");
    const db = new DatabaseSync(dbPath);
    try {
      db.exec("pragma busy_timeout = 8000");

      const now = Date.now();
      const upsert = db.prepare(`
        insert into providers
          (id, app_type, name, settings_config, website_url, category,
           created_at, sort_index, notes, icon, icon_color, meta,
           is_current, in_failover_queue, cost_multiplier, provider_type)
        values
          (?, ?, ?, ?, '', 'custom',
           ?, 0, ?, 'pi', '#2F6FEB', '{}',
           0, 0, '1.0', 'custom')
        on conflict (id, app_type) do update set
          settings_config = excluded.settings_config,
          notes = excluded.notes
      `);
      const prior = db.prepare("select created_at from providers where id = ? and app_type = ?");

      for (const entry of usable) {
        const found = byAppType.get(entry.appType);
        const id = found === undefined ? PROVIDER_ID : found.id;
        const name = found === undefined ? PROVIDER_NAME : found.name;
        const createdAt = prior.get(id, entry.appType)?.["created_at"] ?? now;

        upsert.run(
          id,
          entry.appType,
          name,
          JSON.stringify(entry.settingsConfig),
          createdAt,
          found === undefined
            ? "由 WorkBuddy 控制台写入。删掉这条不影响本机网关本身。"
            : "由 WorkBuddy 控制台更新（这条供应商原本就指向本机网关）。",
        );

        written.push(entry.appType);
        if (found === undefined) created.push(entry.appType);
        else adopted.push({ appType: entry.appType, id: found.id, name: found.name });
      }
    } finally {
      db.close();
    }
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    return {
      ok: false,
      written,
      created,
      adopted,
      ...(backupPath === undefined ? {} : { backupPath }),
      reason:
        `写 CC Switch 的库失败：${detail}` +
        (backupPath === undefined ? "" : `。原库已备份到 ${backupPath}`),
    };
  }

  return {
    ok: true,
    written,
    created,
    adopted,
    ...(backupPath === undefined ? {} : { backupPath }),
  };
}

// ── 反向：把库里的配置应用到 agent 文件 ────────────────────
//
// 用途有两个：修「库里有、文件里没有」的不一致；
// 以及用户在 CC Switch 界面里编好了供应商，不用再去点 CC Switch 的“启用”。

function backupFile(path) {
  if (!existsSync(path)) return undefined;
  const stamp = new Date().toISOString().replace(/[:.]/gu, "-");
  const target = `${path}.before-workbuddy-${stamp}`;
  try {
    copyFileSync(path, target);
    return target;
  } catch {
    return undefined;
  }
}

function writeJson(path, value) {
  // 目录得先存在（用户可能没装过那个客户端）
  if (!existsSync(dirname(path))) return false;
  writeFileSync(path, `${JSON.stringify(value, null, 2)}\n`, "utf8");
  return true;
}

/**
 * 把库里属于我们的条目写回 agent 配置文件。
 *
 * 合并规则与 `ClientIntegrator` 一致：**只写我们负责的字段，用户自己加的原样保留**。
 * 比如 claude 的 env 里，用户自己加的 `ANTHROPIC_DEFAULT_OPUS_MODEL` 之类不会被冲掉。
 *
 * @param {object} [options]
 * @param {string} [options.dbPath]
 * @param {string[]} [options.appTypes] 限定只处理哪几个（默认全部）
 * @param {string} [options.home]
 * @returns {Promise<{ok: boolean, applied: string[], skipped: string[], backups: string[], reason?: string}>}
 */
export async function applyToAgentFiles(options = {}) {
  const dbPath = options.dbPath ?? defaultDbPath();
  const home = options.home ?? homedir();
  const paths = agentConfigPaths(home);

  if (!isAvailable(dbPath)) {
    return { ok: false, applied: [], skipped: [], backups: [], reason: `没找到 CC Switch 的库：${dbPath}` };
  }

  let rows;
  try {
    // 扫全部，按“内容里有没有回环地址”认人 —— 不靠 id，
    // 因为用户自己建的供应商 id 可能是 default / 任意名字。
    const everything = await findLocalGatewayEntries(dbPath);
    rows = everything.map((item) => ({
      app_type: item.appType,
      settings_config: JSON.stringify(item.settingsConfig),
    }));
  } catch (error) {
    return {
      ok: false,
      applied: [],
      skipped: [],
      backups: [],
      reason: error instanceof Error ? error.message : String(error),
    };
  }

  const wanted = options.appTypes === undefined ? undefined : new Set(options.appTypes);
  const applied = [];
  const skipped = [];
  const backups = [];

  for (const row of rows) {
    const appType = row["app_type"];
    if (wanted !== undefined && !wanted.has(appType)) continue;

    let config;
    try {
      config = JSON.parse(row["settings_config"]);
    } catch {
      skipped.push(`${appType}（库里的内容不是合法 JSON）`);
      continue;
    }

    if (appType === "claude" || appType === "claude-desktop") {
      if (!existsSync(dirname(paths.claudeSettings))) {
        skipped.push(`${appType}（~/.claude 目录不存在）`);
        continue;
      }
      const existing = readJson(paths.claudeSettings) ?? {};
      const merged = { ...existing, env: { ...(existing["env"] ?? {}), ...(config["env"] ?? {}) } };
      const backup = backupFile(paths.claudeSettings);
      if (backup !== undefined) backups.push(backup);
      writeJson(paths.claudeSettings, merged);
      applied.push(appType);
      continue;
    }

    if (appType === "codex") {
      if (typeof config["config"] !== "string" || config["config"].trim() === "") {
        skipped.push(`${appType}（库里的 config 是空的，写进去会把配置文件清空）`);
      } else if (!existsSync(dirname(paths.codexConfig))) {
        skipped.push(`${appType}（~/.codex 目录不存在）`);
      } else {
        const backup = backupFile(paths.codexConfig);
        if (backup !== undefined) backups.push(backup);
        try {
          writeFileSync(paths.codexConfig, config["config"], "utf8");
          applied.push(appType);
        } catch {
          skipped.push(`${appType}（写 config.toml 失败）`);
        }
      }

      if (config["auth"] !== undefined && Object.keys(config["auth"]).length > 0) {
        const existing = readJson(paths.codexAuth) ?? {};
        const backup = backupFile(paths.codexAuth);
        if (backup !== undefined) backups.push(backup);
        writeJson(paths.codexAuth, { ...existing, ...config["auth"] });
      }
      continue;
    }

    if (appType === "pi") {
      if (!existsSync(dirname(paths.piModels))) {
        skipped.push(`${appType}（~/.pi/agent 目录不存在）`);
        continue;
      }
      const existing = readJson(paths.piModels) ?? {};
      const providers = { ...(existing["providers"] ?? {}), [PROVIDER_ID]: config };
      const backup = backupFile(paths.piModels);
      if (backup !== undefined) backups.push(backup);
      writeJson(paths.piModels, { ...existing, providers });
      applied.push(appType);
      continue;
    }

    skipped.push(`${appType}（本工具不管这个客户端）`);
  }

  return { ok: true, applied, skipped, backups };
}

/** 删掉我们写进去的条目（只删自己的）。 */
export async function removeProviders(dbPath = defaultDbPath()) {
  if (!isAvailable(dbPath)) return { ok: false, removed: 0, reason: "没找到 CC Switch 的库" };

  try {
    const { DatabaseSync } = await import("node:sqlite");
    const db = new DatabaseSync(dbPath);
    try {
      db.exec("pragma busy_timeout = 8000");
      const result = db.prepare("delete from providers where id = ?").run(PROVIDER_ID);
      return { ok: true, removed: Number(result["changes"] ?? 0) };
    } finally {
      db.close();
    }
  } catch (error) {
    return { ok: false, removed: 0, reason: String(error instanceof Error ? error.message : error) };
  }
}
