/**
 * 国际版渠道校验：会话三元组请求头。
 *
 * 背景（2026-10-23 实测）：
 *
 * 国际版网关（www.workbuddy.ai）的渠道校验会返回 HTTP 400 / code 11128
 * 「Illegal API invocation from an unapproved channel」。UA 和版本号都对也不行，
 * 因为它查的是**会话三元组**：
 *
 *   X-Conversation-ID
 *   X-Conversation-Request-ID
 *   X-Conversation-Message-ID
 *
 * App 是真会话，天然带这三个；无状态网关没有，必须自己造。
 * 实测：三个少任何一个都是 11128，三个全带就是 200。
 *
 * CN 网关不查这一族，所以只在 global 变体上发 —— 给 CN 带上并不报错，
 * 但那是无意义的噪声，而且 CN 侧的行为没有验证过，不该顺带改。
 *
 * 跑：node scripts/test-intl-headers.mjs
 */

import assert from "node:assert/strict";

import { chatHeaders } from "../src/upstream.js";
import { AI_VARIANT, CN_VARIANT } from "../src/variants.js";

const TRIPLE = ["X-Conversation-ID", "X-Conversation-Request-ID", "X-Conversation-Message-ID"];

let passed = 0;
const failures = [];

function test(name, fn) {
  try {
    fn();
    passed += 1;
  } catch (error) {
    failures.push({ name, error });
  }
}

const credential = { uid: "3e563042-d9da-4ce6-bbb8-bf52debdbff0", enterpriseId: "", domain: "www.workbuddy.ai" };
const intl = () => chatHeaders(credential, AI_VARIANT, "WorkBuddy/5.8.4", "5.8.4");
const cn = () => chatHeaders(credential, CN_VARIANT, "WorkBuddy/5.8.3", "5.8.3");

// ── 国际版必须带三元组 ────────────────────────────────────────────────

test("国际版带全部三个会话头", () => {
  const headers = intl();
  for (const name of TRIPLE) assert.equal(typeof headers[name], "string", `${name} 缺失`);
});

test("三个会话头的值是 32 位小写十六进制", () => {
  const headers = intl();
  for (const name of TRIPLE) {
    assert.match(headers[name], /^[0-9a-f]{32}$/u, `${name} 格式不对：${headers[name]}`);
  }
});

test("三个会话头的值互不相同", () => {
  const headers = intl();
  const values = TRIPLE.map((name) => headers[name]);
  assert.equal(new Set(values).size, 3, `三个 id 有重复：${values.join(", ")}`);
});

test("每次调用生成新的会话 id（无状态网关的前提）", () => {
  const first = intl();
  const second = intl();
  for (const name of TRIPLE) {
    assert.notEqual(first[name], second[name], `${name} 两次调用相同，说明被缓存了`);
  }
});

test("并发调用之间也不共享 id", () => {
  const batch = Array.from({ length: 20 }, () => intl());
  const seen = new Set();
  for (const headers of batch) {
    for (const name of TRIPLE) seen.add(headers[name]);
  }
  assert.equal(seen.size, 60, `20 次调用应产生 60 个唯一 id，实际 ${seen.size}`);
});

// ── CN 版不该带 ──────────────────────────────────────────────────────

test("CN 版不带会话头", () => {
  const headers = cn();
  for (const name of TRIPLE) {
    assert.equal(headers[name], undefined, `CN 版不该带 ${name}`);
  }
});

// ── 国际版的其他身份头不受影响 ────────────────────────────────────────

test("国际版仍然带 X-IDE 身份族", () => {
  const headers = intl();
  assert.equal(headers["X-IDE-Type"], "WorkBuddy");
  assert.equal(headers["X-IDE-Name"], "WorkBuddy");
  assert.equal(headers["X-IDE-Version"], "5.8.4");
  assert.equal(headers["X-Product"], "SaaS");
});

test("国际版仍然带 UA 和用户标识", () => {
  const headers = intl();
  assert.equal(headers["User-Agent"], "WorkBuddy/5.8.4");
  assert.equal(headers["X-User-Id"], credential.uid);
  assert.equal(headers["X-Domain"], credential.domain);
});

test("承诺不带上游不认识的头：CN 版没有任何会话族残留", () => {
  const headers = cn();
  const leftover = Object.keys(headers).filter((name) => /^x-conversation-/iu.test(name));
  assert.deepEqual(leftover, []);
});

// ── 空 uid / 空 domain 的边界 ────────────────────────────────────────

test("空 uid 的国际版：改用 X-No-User-Id，三元组照带", () => {
  const headers = chatHeaders({ uid: "", enterpriseId: "", domain: "www.workbuddy.ai" }, AI_VARIANT, "WorkBuddy/5.8.4", "5.8.4");
  assert.equal(headers["X-No-User-Id"], "1");
  assert.equal(headers["X-User-Id"], undefined);
  for (const name of TRIPLE) assert.match(headers[name], /^[0-9a-f]{32}$/u);
});

test("没有 clientVersion 时不带 X-IDE-Version，三元组照带", () => {
  const headers = chatHeaders(credential, AI_VARIANT, "WorkBuddy/5.8.4", undefined);
  assert.equal(headers["X-IDE-Version"], undefined);
  for (const name of TRIPLE) assert.match(headers[name], /^[0-9a-f]{32}$/u);
});

// ── 报告 ────────────────────────────────────────────────────────────

if (failures.length > 0) {
  console.error(`\n  ✘ ${failures.length} 项失败 / ${passed + failures.length} 项\n`);
  for (const { name, error } of failures) {
    console.error(`  ── ${name} ──`);
    console.error(`     ${String(error.message || error).split("\n").slice(0, 4).join("\n     ")}\n`);
  }
  process.exit(1);
}

console.log(`  ✔ ${passed}/${passed} 项通过`);
