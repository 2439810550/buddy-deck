/**
 * 账号迁移：把这台机器的账号导出成**加密**的一个文件，在另一台机器导入。
 *
 * 为什么要加密：导出的东西里有 refreshToken，等效于账号密码 ——
 * 而且服务端给的是滑动窗口，只要不断续，它可以一直用下去。
 * 明文拷贝（微信 / U 盘 / 网盘）等于把这些号的额度公开了。
 *
 * 方案：scrypt 派生密钥 + AES-256-GCM 认证加密，零第三方依赖。
 * GCM 带认证标签，所以密码输错会直接报错，不会解出一堆乱码还接着往下走。
 *
 * 文件里除了密文，只留了 KDF 参数和盐 —— 这些都是可以公开的。
 *
 * @module workbuddy-api/transfer
 */

import { createCipheriv, createDecipheriv, randomBytes, scryptSync, timingSafeEqual } from "node:crypto";

/** 导出文件的格式标识。导入时先认这个，避免拿错文件报一堆莫名其妙的错。 */
export const TRANSFER_FORMAT = "workbuddy-accounts";
export const TRANSFER_VERSION = 1;

/** scrypt 参数。N=16384 大约 16MB 内存、几十毫秒，桌面端够用。 */
const KDF = { name: "scrypt", N: 16_384, r: 8, p: 1, keylen: 32 };
const SALT_BYTES = 16;
const IV_BYTES = 12;

/** 最短口令长度。太短的口令挡不住离线爆破，直接拒绝比事后提醒有用。 */
export const MIN_PASSPHRASE_LENGTH = 8;

function assertPassphrase(passphrase) {
  if (typeof passphrase !== "string" || passphrase.trim().length < MIN_PASSPHRASE_LENGTH) {
    throw new Error(`口令至少 ${MIN_PASSPHRASE_LENGTH} 个字符。这个文件里是账号凭据，口令太短等于没加密。`);
  }
}

function deriveKey(passphrase, salt) {
  // normalize：避免在另一台机器上用不同 Unicode 归一化方式输入同一个口令导致解不开
  return scryptSync(passphrase.normalize("NFKC"), salt, KDF.keylen, {
    N: KDF.N,
    r: KDF.r,
    p: KDF.p,
    maxmem: 128 * 1024 * 1024,
  });
}

/**
 * 把保险库里的账号打包成加密文档。
 *
 * @param {Array<object>} records 保险库里的原始记录（含 token）
 * @param {string} passphrase
 * @param {{provider: string}} meta
 * @returns {object} 可以直接 JSON 序列化后写文件的文档
 */
export function sealAccounts(records, passphrase, meta) {
  assertPassphrase(passphrase);

  const payload = {
    provider: meta.provider,
    exportedAt: new Date().toISOString(),
    accounts: records,
  };

  const salt = randomBytes(SALT_BYTES);
  const iv = randomBytes(IV_BYTES);
  const key = deriveKey(passphrase, salt);

  const cipher = createCipheriv("aes-256-gcm", key, iv);
  const sealed = Buffer.concat([cipher.update(Buffer.from(JSON.stringify(payload), "utf8")), cipher.final()]);

  return {
    format: TRANSFER_FORMAT,
    version: TRANSFER_VERSION,
    provider: meta.provider,
    createdAt: payload.exportedAt,
    cipher: "aes-256-gcm",
    kdf: { ...KDF, salt: salt.toString("base64") },
    iv: iv.toString("base64"),
    tag: cipher.getAuthTag().toString("base64"),
    payload: sealed.toString("base64"),
  };
}

/**
 * 解开一个导出文档。
 *
 * 口令错、文件被改过、文件截断 —— 这三种都会被 GCM 的认证标签拦下，
 * 统一报成「口令不对或文件损坏」，不泄露哪一步出的问题。
 *
 * @param {object} document
 * @param {string} passphrase
 * @returns {{provider: string, exportedAt: string, accounts: Array<object>}}
 */
export function openAccounts(document, passphrase) {
  if (typeof document !== "object" || document === null || Array.isArray(document)) {
    throw new Error("这不是一个导出的账号文件");
  }
  if (document["format"] !== TRANSFER_FORMAT) {
    throw new Error(`文件格式不对：期望 ${TRANSFER_FORMAT}，实际 ${JSON.stringify(document["format"])}`);
  }
  if (document["version"] !== TRANSFER_VERSION) {
    throw new Error(`导出文件版本是 ${document["version"]}，这个版本的程序只认 ${TRANSFER_VERSION}`);
  }
  assertPassphrase(passphrase);

  let plaintext;
  try {
    const salt = Buffer.from(document["kdf"]["salt"], "base64");
    const iv = Buffer.from(document["iv"], "base64");
    const tag = Buffer.from(document["tag"], "base64");
    const key = deriveKey(passphrase, salt);

    const decipher = createDecipheriv("aes-256-gcm", key, iv);
    decipher.setAuthTag(tag);
    plaintext = Buffer.concat([
      decipher.update(Buffer.from(document["payload"], "base64")),
      decipher.final(),
    ]);
  } catch {
    throw new Error("解不开：口令不对，或者文件在传输中被改坏了。");
  }

  let payload;
  try {
    payload = JSON.parse(plaintext.toString("utf8"));
  } catch {
    throw new Error("解开后的内容不是合法 JSON，文件可能损坏了。");
  }

  const accounts = Array.isArray(payload?.["accounts"]) ? payload["accounts"] : [];
  if (accounts.length === 0) throw new Error("文件里没有账号");

  return {
    provider: typeof payload["provider"] === "string" ? payload["provider"] : "",
    exportedAt: typeof payload["exportedAt"] === "string" ? payload["exportedAt"] : "",
    accounts,
  };
}

/** 导出文件建议的文件名（带日期，便于区分）。 */
export function suggestedFileName(provider) {
  const stamp = new Date().toISOString().slice(0, 10);
  return `workbuddy-accounts-${provider}-${stamp}.json`;
}

/** 两个口令是否一致（界面里确认用）。用定时安全比较，虽然这里不敏感，图个一致。 */
export function samePassphrase(a, b) {
  const left = Buffer.from(String(a), "utf8");
  const right = Buffer.from(String(b), "utf8");
  if (left.length !== right.length) return false;
  return timingSafeEqual(left, right);
}
