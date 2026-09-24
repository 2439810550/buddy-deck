/**
 * 多账号保险库。
 *
 * 桌面 App 一次只登录一个账号，登录文件里也就只有一份凭据。
 * 想同时挂着几个号，就得把每份凭据单独存下来，并且记得住「当前用哪个」。
 *
 * 这个模块就是干这个的：
 *   - 存：`~/.workbuddy-api/accounts.<region>.json`（0600）
 *   - 记：每个账号的 refreshToken / 到期时间 / 上次签到时间
 *   - 选：activeId 决定网关用哪个号
 *   - 续：按账号各自刷新 token，单飞，互不干扰
 *
 * 国内版和国际版分开存（文件名带 region），两边的账号不会串。
 *
 * @module workbuddy-api/accounts
 */

import { basename, join } from "node:path";
import { ensureDir, readJson, stateHome, writeFileAtomic } from "./paths.js";
import { credentialIdentity } from "./auth.js";

const VAULT_VERSION = 1;

/** 保险库文件的位置：跟着凭据副本的命名习惯走（auth.cn.json → accounts.cn.json）。 */
export function accountsPath(variant) {
  const tag = basename(variant.ownFilename).replace(/^auth\./u, "").replace(/\.json$/u, "");
  return join(stateHome(), `accounts.${tag}.json`);
}

/** 账号 id 用 `uid:enterpriseId`，和 `credentialIdentity` 保持一致。 */
function idOf(credential) {
  return credentialIdentity(credential);
}

/** 从一份凭据里抽出要存下来的字段。绝不带上 accessToken 之外的任何个人信息。 */
function snapshotOf(credential) {
  return {
    id: idOf(credential),
    uid: credential.uid ?? "",
    enterpriseId: credential.enterpriseId ?? "",
    domain: credential.domain ?? "",
    ...(credential.nickname === undefined ? {} : { nickname: credential.nickname }),
    accessToken: credential.accessToken,
    ...(credential.refreshToken === undefined ? {} : { refreshToken: credential.refreshToken }),
    expiresAtMs: credential.expiresAtMs ?? 0,
    ...(credential.refreshExpiresAtMs === undefined ? {} : { refreshExpiresAtMs: credential.refreshExpiresAtMs }),
  };
}

/** 存进去的记录 → 能直接发给上游的 credential 形状。 */
function credentialOf(record) {
  return {
    uid: record.uid,
    enterpriseId: record.enterpriseId ?? "",
    domain: record.domain ?? "",
    ...(record.nickname === undefined ? {} : { nickname: record.nickname }),
    accessToken: record.accessToken,
    ...(record.refreshToken === undefined ? {} : { refreshToken: record.refreshToken }),
    expiresAtMs: record.expiresAtMs ?? 0,
    ...(record.refreshExpiresAtMs === undefined ? {} : { refreshExpiresAtMs: record.refreshExpiresAtMs }),
    source: "vault",
  };
}

/** 对外展示用的一条账号信息（不含任何 token）。 */
function publicOf(record, activeId) {
  return {
    id: record.id,
    label: record.label ?? "",
    uid: record.uid,
    enterpriseId: record.enterpriseId ?? "",
    domain: record.domain ?? "",
    ...(record.nickname === undefined ? {} : { nickname: record.nickname }),
    active: record.id === activeId,
    expiresAt: record.expiresAtMs > 0 ? new Date(record.expiresAtMs).toISOString() : null,
    // 续期窗口：到这个时间前刷新一次，就能再续 60 天。这是「会不会掉登录」的唯一指标。
    refreshExpiresAt:
      typeof record.refreshExpiresAtMs === "number" && record.refreshExpiresAtMs > 0
        ? new Date(record.refreshExpiresAtMs).toISOString()
        : null,
    refreshDaysLeft:
      typeof record.refreshExpiresAtMs === "number" && record.refreshExpiresAtMs > 0
        ? Math.floor((record.refreshExpiresAtMs - Date.now()) / 86_400_000)
        : null,
    addedAt: record.addedAt ?? null,
    lastCheckinAt: record.lastCheckinAt ?? null,
    hasRefreshToken: typeof record.refreshToken === "string" && record.refreshToken !== "",
  };
}

/**
 * 多账号保险库。
 *
 * 接口特意和 `CredentialStore` 对齐（`resolve()` / `status()`），
 * 这样网关那边换个对象就能用，不用改一行。
 */
export class AccountVault {
  /**
   * @param {object} options
   * @param {object} options.variant variant descriptor
   * @param {(credential: object) => Promise<object>} options.refresh token 刷新函数
   * @param {string} [options.path] 覆盖保险库文件路径（测试用）
   * @param {number} [options.refreshMarginMs] 提前多久刷新
   */
  constructor(options) {
    this.variant = options.variant;
    this.refresh = options.refresh;
    this.path = options.path ?? accountsPath(options.variant);
    this.refreshMarginMs = options.refreshMarginMs ?? 300_000;
    this.inflight = new Map();
  }

  /** 原始文档；文件不存在或读坏了都当作空库。 */
  async load() {
    const document = await readJson(this.path);
    if (typeof document !== "object" || document === null || Array.isArray(document)) {
      return { version: VAULT_VERSION, activeId: "", accounts: [] };
    }
    const accounts = Array.isArray(document["accounts"]) ? document["accounts"] : [];
    return {
      version: VAULT_VERSION,
      activeId: typeof document["activeId"] === "string" ? document["activeId"] : "",
      accounts: accounts.filter((record) => typeof record === "object" && record !== null),
    };
  }

  async save(document) {
    await ensureDir(stateHome());
    // 0600：这里面有 refreshToken，等同于账号密码
    await writeFileAtomic(this.path, `${JSON.stringify(document, null, 2)}\n`, { mode: 0o600 });
  }

  /** 全部账号（去掉 token 字段），给界面和 `accounts` 命令用。 */
  async list() {
    const document = await this.load();
    return document.accounts.map((record) => publicOf(record, document.activeId));
  }

  /** 当前选中的账号 id（空串 = 没选，走桌面 App 的那个号）。 */
  async activeId() {
    return (await this.load()).activeId;
  }

  /**
   * 把一个账号存进库（同一个 id 就更新）。
   *
   * @param {object} credential 来自 CredentialStore 或手动录入
   * @param {{label?: string, makeActive?: boolean}} [options]
   * @returns {Promise<{id: string, created: boolean}>}
   */
  async upsert(credential, options = {}) {
    const id = idOf(credential);
    if (id === ":" || credential.uid === "") {
      throw new Error("这份凭据里没有 uid，没法当账号存");
    }

    const document = await this.load();
    const index = document.accounts.findIndex((record) => record.id === id);
    const now = new Date().toISOString();
    const previous = index === -1 ? undefined : document.accounts[index];

    const merged = {
      ...snapshotOf(credential),
      // 名字的规则：本地已有的名字优先（导入不该改掉你起好的名），
      // 本地没名字才用传进来的；传空字符串不算「改名」。
      label:
        typeof previous?.label === "string" && previous.label !== ""
          ? previous.label
          : options.label ?? "",
      addedAt: previous?.addedAt ?? now,
      lastSeenAt: now,
      ...(previous?.lastCheckinAt === undefined ? {} : { lastCheckinAt: previous.lastCheckinAt }),
    };

    // 刷新接口偶尔不回 refreshToken，那就沿用旧的，别把能用的号弄废
    if (merged.refreshToken === undefined && previous?.refreshToken !== undefined) {
      merged.refreshToken = previous.refreshToken;
    }

    if (index === -1) document.accounts.push(merged);
    else document.accounts[index] = merged;

    if (options.makeActive === true || document.activeId === "") document.activeId = id;

    await this.save(document);
    return { id, created: index === -1 };
  }

  async setActive(id) {
    const document = await this.load();
    if (!document.accounts.some((record) => record.id === id)) {
      throw new Error(`没有这个账号：${id}`);
    }
    document.activeId = id;
    await this.save(document);
    return id;
  }

  /** 取消选定：网关退回用桌面 App 当前登录的那个号。 */
  async clearActive() {
    const document = await this.load();
    document.activeId = "";
    await this.save(document);
  }

  async remove(id) {
    const document = await this.load();
    const before = document.accounts.length;
    document.accounts = document.accounts.filter((record) => record.id !== id);
    if (document.accounts.length === before) throw new Error(`没有这个账号：${id}`);
    if (document.activeId === id) document.activeId = document.accounts[0]?.id ?? "";
    await this.save(document);
  }

  async rename(id, label) {
    const document = await this.load();
    const record = document.accounts.find((item) => item.id === id);
    if (record === undefined) throw new Error(`没有这个账号：${id}`);
    record.label = label;
    await this.save(document);
  }

  /** 记一笔「这个号刚签到过」，界面用来显示。 */
  async markCheckedIn(id) {
    const document = await this.load();
    const record = document.accounts.find((item) => item.id === id);
    if (record === undefined) return;
    record.lastCheckinAt = new Date().toISOString();
    await this.save(document);
  }

  /**
   * 某个账号当前可用的凭据，必要时先刷新。
   *
   * @param {string} id
   * @returns {Promise<object|undefined>}
   */
  async credentialFor(id) {
    const document = await this.load();
    const record = document.accounts.find((item) => item.id === id);
    if (record === undefined) return undefined;

    const credential = credentialOf(record);
    if (!this.#needsRefresh(credential)) return credential;

    // 单飞：并发的多个请求共用同一次刷新
    const pending = this.inflight.get(id);
    if (pending !== undefined) return pending;

    const task = this.#refreshRecord(id, credential).finally(() => this.inflight.delete(id));
    this.inflight.set(id, task);
    return task;
  }

  #needsRefresh(credential) {
    if (typeof credential.refreshToken !== "string" || credential.refreshToken === "") return false;
    if (credential.accessToken === "") return true;
    if (credential.expiresAtMs === 0) return true;
    return credential.expiresAtMs - Date.now() <= this.refreshMarginMs;
  }

  /**
   * 刷新并落盘；失败就抛。
   *
   * `#refreshRecord` 在它外面包了一层「旧 token 还能用就继续用」的宽容；
   * 主动续期（renewIfStale）需要确切知道成没成，所以直接调这个。
   */
  async #renewRecord(id, credential) {
    const outcome = await this.refresh(credential);
    const refreshed = {
      ...credential,
      accessToken: outcome.accessToken,
      ...(outcome.refreshToken === undefined ? {} : { refreshToken: outcome.refreshToken }),
      ...(outcome.domain === undefined ? {} : { domain: outcome.domain }),
      expiresAtMs:
        typeof outcome.expiresInSec === "number" && outcome.expiresInSec > 0
          ? Date.now() + outcome.expiresInSec * 1000
          : credential.expiresAtMs,
      // 服务端这次给的续期窗口重新开始 → 记下来，下次就知道什么时候该再续
      ...(typeof outcome.refreshExpiresInSec === "number" && outcome.refreshExpiresInSec > 0
        ? { refreshExpiresAtMs: Date.now() + outcome.refreshExpiresInSec * 1000 }
        : {}),
    };

    const document = await this.load();
    const index = document.accounts.findIndex((record) => record.id === id);
    if (index !== -1) {
      const previous = document.accounts[index];
      document.accounts[index] = {
        ...previous,
        ...snapshotOf(refreshed),
        label: previous.label ?? "",
        addedAt: previous.addedAt,
        lastSeenAt: new Date().toISOString(),
        ...(previous.lastCheckinAt === undefined ? {} : { lastCheckinAt: previous.lastCheckinAt }),
      };
      await this.save(document);
    }

    return refreshed;
  }

  /**
   * 用到就刷新：刷新失败时，只要旧 token 还没真正过期就继续用——
   * 刷新端点抖一下，不该让正在跑的会话断掉。
   */
  async #refreshRecord(id, credential) {
    try {
      return await this.#renewRecord(id, credential);
    } catch (error) {
      if (credential.accessToken !== "" && credential.expiresAtMs > Date.now()) return credential;
      throw new Error(
        `账号 ${id} 的令牌刷新失败，且旧令牌已过期，需要重新登录：${error instanceof Error ? error.message : String(error)}`,
      );
    }
  }

  /**
   * 主动续期：把续期窗口往后推。**这就是「一次扫码终身服务」的那个开关。**
   *
   * 服务端每次刷新都会重新给一个满额的 `refreshExpiresIn`（实测 60 天），
   * 所以只要在窗口内刷新一次，就又能续 60 天。
   * 默认在剩余不足 45 天时执行 —— 也就是距上次续期 15 天，留足容错。
   *
   * @param {string} id
   * @param {{minRemainingDays?: number, force?: boolean}} [options]
   * @returns {Promise<{id: string, renewed: boolean, daysLeft: number|null}>}
   */
  async renewIfStale(id, options = {}) {
    const minRemainingDays = options.minRemainingDays ?? 45;

    const document = await this.load();
    const record = document.accounts.find((item) => item.id === id);
    if (record === undefined) throw new Error(`没有这个账号：${id}`);

    const known = typeof record.refreshExpiresAtMs === "number" && record.refreshExpiresAtMs > 0;
    const remainingMs = known ? record.refreshExpiresAtMs - Date.now() : null;

    // 不知道还能续多久（老的库没有这个字段）就当成需要续，宁可多刷一次
    const stale = options.force === true || !known || remainingMs < minRemainingDays * 86_400_000;
    if (!stale) {
      return { id, renewed: false, daysLeft: Math.floor(remainingMs / 86_400_000) };
    }

    if (typeof record.refreshToken !== "string" || record.refreshToken === "") {
      throw new Error(`账号 ${id} 没有 refreshToken，只能重新登录`);
    }

    await this.#renewRecord(id, credentialOf(record));

    const after = (await this.load()).accounts.find((item) => item.id === id);
    const afterMs =
      typeof after?.refreshExpiresAtMs === "number" && after.refreshExpiresAtMs > 0
        ? after.refreshExpiresAtMs - Date.now()
        : null;

    return { id, renewed: true, daysLeft: afterMs === null ? null : Math.floor(afterMs / 86_400_000) };
  }

  /**
   * 网关要用的凭据：优先当前选中的账号，没选就交给调用方兜底。
   *
   * @returns {Promise<object|undefined>}
   */
  async resolveActive() {
    const id = await this.activeId();
    if (id === "") return undefined;
    const credential = await this.credentialFor(id);
    if (credential === undefined) {
      throw new Error(`保险库里选中的账号 ${id} 不存在了，用 accounts use 重新选一个`);
    }
    return credential;
  }

  /** 只读摘要，和 CredentialStore.status() 同形状。 */
  async status() {
    try {
      const id = await this.activeId();
      if (id === "") return { state: "signed-out", reason: "没有选中任何保险库账号" };
      const document = await this.load();
      const record = document.accounts.find((item) => item.id === id);
      if (record === undefined) return { state: "signed-out", reason: `账号 ${id} 不在库里` };
      return {
        state: "signed-in",
        expiresAtMs: record.expiresAtMs,
        ...(record.nickname === undefined ? {} : { nickname: record.nickname }),
        ...(record.domain === "" ? {} : { domain: record.domain }),
        uid: record.uid,
        ...(record.enterpriseId === undefined ? {} : { enterpriseId: record.enterpriseId }),
        source: "vault",
      };
    } catch (error) {
      return { state: "signed-out", reason: error instanceof Error ? error.message : String(error) };
    }
  }
}

/**
 * 网关和 CLI 实际用的凭据来源。
 *
 * 保险库里选了账号就用它；没选就退回 `CredentialStore`（也就是桌面 App 当前登录的那个）。
 * 对外只暴露 `resolve()` / `status()`，所以 `createGateway` 完全不用知道自己拿到的是谁。
 */
export class AccountRouter {
  constructor({ vault, store }) {
    this.vault = vault;
    this.store = store;
  }

  async resolve() {
    const fromVault = await this.vault.resolveActive();
    if (fromVault !== undefined) return fromVault;
    return this.store.resolve();
  }

  async status() {
    const fromVault = await this.vault.status();
    if (fromVault.state === "signed-in") return fromVault;
    return this.store.status();
  }
}
