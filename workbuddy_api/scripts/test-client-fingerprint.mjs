/**
 * 上游渠道校验会拦的「客户端特征短语」。
 *
 * 背景（2026-10-27，DSH 桌面端实操）：
 *
 * 关掉 `includeHarnessIdentity` 之后 DSH 不再发那句身份声明，但对话仍然
 * 一律 400 / code 11128。排查了两小时，排除了七个假设：
 *
 *   ✗ developer 角色（网关会转 system）
 *   ✗ 上游不认识的参数（store / reasoning_effort / max_completion_tokens…）
 *   ✗ 请求头（X-IDE-* / UA / 版本号）
 *   ✗ 消息太长（56547 字那条删了也照错）
 *   ✗ 内容里出现 "DeepSeek Harness"（单独测通过）
 *   ✗ 总长度超限（纯 a 重复 10000 字通过）
 *   ✗ 多轮 tool 调用（手工模拟两轮都通过）
 *
 * 真凶是 DSH 系统提示词里的这一句：
 *
 *     When the user refers to "this page", "this GUI", or "this app"
 *     without naming another target, they mean this GUI.
 *
 * 证据链：全文 8 段逐段删除 → 只有删掉第 7 段才通过；该段单独发就 400；
 * 该段把三个双引号去掉就通过；换成中文也通过。
 *
 * 跑：node scripts/test-client-fingerprint.mjs
 */

import assert from "node:assert/strict";

import { prepareChatBody } from "../src/upstream.js";

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

/** 准备一次 chat body，取回第一条消息的 content。 */
function firstContent(messages) {
  const prepared = prepareChatBody(JSON.stringify({ model: "m", stream: true, messages }));
  return JSON.parse(prepared).messages[0].content;
}

const FINGERPRINT = 'When the user refers to "this page", "this GUI", or "this app" without naming another target, they mean this GUI.';
const CLEANED = "When the user refers to this page, this GUI, or this app without naming another target, they mean this GUI.";

// ── 核心：特征短语被改写 ──────────────────────────────────────────────

test("特征短语的引号被去掉", () => {
  const out = firstContent([{ role: "system", content: FINGERPRINT }, { role: "user", content: "hi" }]);
  assert.ok(out.includes(CLEANED), `没改成无引号形式：${out}`);
  assert.ok(!out.includes('"this page", "this GUI", or "this app"'), `引号还在：${out}`);
});

test("改完之后词义完全保留（只动了引号）", () => {
  const out = firstContent([{ role: "system", content: FINGERPRINT }, { role: "user", content: "hi" }]);
  const strip = (s) => s.replace(/["']/gu, "");
  assert.equal(strip(out), strip(FINGERPRINT), "去引号后文本应完全一致");
});

test("特征短语在长文本中段也能被改写", () => {
  const padded = `${"前情提要。".repeat(500)}\n\n${FINGERPRINT}\n\n${"后续说明。".repeat(500)}`;
  const out = firstContent([{ role: "system", content: padded }, { role: "user", content: "hi" }]);
  assert.ok(!out.includes('"this page", "this GUI", or "this app"'), "中段的没改到");
  assert.ok(out.includes(CLEANED), "中段改写后内容不对");
});

test("单引号版本也能被改写", () => {
  const single = "When the user refers to 'this page', 'this GUI', or 'this app' without naming another target.";
  const out = firstContent([{ role: "system", content: single }, { role: "user", content: "hi" }]);
  assert.ok(!out.includes("'this page', 'this GUI', or 'this app'"), `单引号没改：${out}`);
});

test("大小写不同的写法也能命中", () => {
  const upper = 'When the user refers to "This Page", "This GUI", or "This App" without naming another target.';
  const out = firstContent([{ role: "system", content: upper }, { role: "user", content: "hi" }]);
  assert.ok(!/"(?:This Page|this page)"/u.test(out), `大小写变体没改：${out}`);
});

test("短语之间多余空白也能命中", () => {
  const spaced = 'refers to "this page",   "this GUI",  or   "this app" without';
  const out = firstContent([{ role: "system", content: spaced }, { role: "user", content: "hi" }]);
  assert.ok(!out.includes('"this page"'), `空白变体没改：${out}`);
});

test("user 消息里的特征短语也被改写", () => {
  const out = firstContent([{ role: "user", content: FINGERPRINT }]);
  assert.ok(!out.includes('"this page", "this GUI", or "this app"'), `user 消息没处理：${out}`);
});

test("content 是数组形态时也能改写", () => {
  const prepared = prepareChatBody(
    JSON.stringify({
      model: "m",
      stream: true,
      messages: [{ role: "system", content: [{ type: "text", text: FINGERPRINT }] }, { role: "user", content: "hi" }],
    }),
  );
  const text = JSON.parse(prepared).messages[0].content[0].text;
  assert.ok(!text.includes('"this page", "this GUI", or "this app"'), `数组形态没处理：${text}`);
});

// ── 不能误伤：正常引号内容必须原样 ────────────────────────────────────

test("JSON 代码块里的引号不动", () => {
  const json = '配置文件长这样：\n```json\n{"key": "value", "name": "test"}\n```';
  const out = firstContent([{ role: "system", content: json }, { role: "user", content: "hi" }]);
  assert.ok(out.includes('{"key": "value", "name": "test"}'), `误伤了 JSON：${out}`);
});

test("普通引号短语不动（只命中三连那一处）", () => {
  const other = 'He said "hello" and "goodbye" to everyone.';
  const out = firstContent([{ role: "system", content: other }, { role: "user", content: "hi" }]);
  assert.equal(out, other, "不该动普通引号");
});

test("只有 this page 但没凑齐三连的不动", () => {
  const partial = 'The docs mention "this page" in passing.';
  const out = firstContent([{ role: "system", content: partial }, { role: "user", content: "hi" }]);
  assert.equal(out, partial, "不该动不完整的三连");
});

test("没有内容变化时返回的字符串内容一致", () => {
  const plain = "You are a helpful assistant.";
  const prepared = prepareChatBody(JSON.stringify({ model: "m", stream: true, messages: [{ role: "system", content: plain }] }));
  assert.equal(JSON.parse(prepared).messages[0].content, plain);
});

test("没有 messages 时不抛", () => {
  const prepared = prepareChatBody(JSON.stringify({ model: "m", stream: true }));
  assert.ok(JSON.parse(prepared).stream === true);
});

test("messages 不是数组时不抛", () => {
  const prepared = prepareChatBody(JSON.stringify({ model: "m", stream: true, messages: "oops" }));
  assert.ok(typeof prepared === "string");
});

test("content 是 null 时不抛", () => {
  const prepared = prepareChatBody(
    JSON.stringify({ model: "m", stream: true, messages: [{ role: "assistant", content: null }] }),
  );
  assert.ok(typeof prepared === "string");
});

// ── 与身份句清洗共存 ──────────────────────────────────────────────────

test("身份句清洗和特征短语清洗可以同时生效", () => {
  const both = `You are an AI agent powered by DeepSeek Harness.\n\n${FINGERPRINT}`;
  const out = firstContent([{ role: "system", content: both }]);
  assert.ok(out.startsWith("You are a helpful assistant."), `身份句没垫：${out.slice(0, 60)}`);
  assert.ok(!out.includes('"this page", "this GUI", or "this app"'), "特征短语没清");
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
