/**
 * Anthropic `document` block 的转换。
 *
 * 背景（2026-10-23）：DSH 反馈「看不见我的 Word 文档」。查下来是
 * `toChatPart` 的 default 分支把 `document` 静默丢弃 —— 注释写的是
 * 「上游不认，丢掉」，但 `source.type === "text"` 的 document 就是纯文本，
 * 上游完全认，丢掉等于把用户发来的正文吞了。
 *
 * 实测证据：发一个带「机密代号：蓝色鲸鱼」的 document block，
 * 修复前模型回「我没能读出你问的内容」，修复后能复述内容。
 *
 * 跑：node scripts/test-document-blocks.mjs
 */

import assert from "node:assert/strict";

import { anthropicToChat } from "../src/anthropic.js";

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

/** 把 Anthropic 请求转成 Chat 请求，取回 user 消息的 content parts。 */
function partsOf(content, extra = {}) {
  const chat = anthropicToChat(
    {
      model: "deepseek-v4.1-flash",
      max_tokens: 100,
      messages: [{ role: "user", content }],
      ...extra,
    },
    {},
  );
  const user = chat.messages.find((message) => message.role === "user");
  assert.ok(user, "转换后没有 user 消息");
  if (typeof user.content === "string") return [{ type: "text", text: user.content }];
  return user.content;
}

/** 取出所有 text part 拼起来，方便断言内容。 */
function textOf(content) {
  return partsOf(content)
    .filter((part) => part.type === "text")
    .map((part) => part.text)
    .join("\n");
}

// ── source.type = "text"：核心修复 ────────────────────────────────────

test("document(text) 的正文进到模型可见的文本里", () => {
  const text = textOf([
    { type: "text", text: "这份文档写了什么？" },
    { type: "document", source: { type: "text", media_type: "text/plain", data: "机密代号：蓝色鲸鱼。" } },
  ]);
  assert.ok(text.includes("机密代号：蓝色鲸鱼。"), `正文丢了，实际：${text}`);
});

test("document(text) 带 title 时标题也保留", () => {
  const text = textOf([
    { type: "document", title: "季度报告", source: { type: "text", media_type: "text/plain", data: "营收 42 万" } },
  ]);
  assert.ok(text.includes("季度报告"), `标题丢了：${text}`);
  assert.ok(text.includes("营收 42 万"), `正文丢了：${text}`);
});

test("document(text) 带 context 时上下文也保留", () => {
  const text = textOf([
    {
      type: "document",
      context: "这是上周的会议记录",
      source: { type: "text", media_type: "text/plain", data: "决定推迟上线" },
    },
  ]);
  assert.ok(text.includes("上周的会议记录"), `context 丢了：${text}`);
  assert.ok(text.includes("决定推迟上线"), `正文丢了：${text}`);
});

test("document(text) 空 data 不炸，产出空文本而不是丢 part", () => {
  const parts = partsOf([{ type: "document", source: { type: "text", media_type: "text/plain", data: "" } }]);
  assert.equal(parts.length, 1);
  assert.equal(parts[0].type, "text");
});

test("多份 document(text) 都能到达模型", () => {
  const text = textOf([
    { type: "document", source: { type: "text", media_type: "text/plain", data: "第一份：甲" } },
    { type: "document", source: { type: "text", media_type: "text/plain", data: "第二份：乙" } },
  ]);
  assert.ok(text.includes("第一份：甲"), text);
  assert.ok(text.includes("第二份：乙"), text);
});

// ── source.type = "content"：嵌套 block ──────────────────────────────

test("document(content) 把嵌套的 text block 拼起来", () => {
  const text = textOf([
    {
      type: "document",
      source: {
        type: "content",
        content: [
          { type: "text", text: "第一段" },
          { type: "text", text: "第二段" },
        ],
      },
    },
  ]);
  assert.ok(text.includes("第一段"), text);
  assert.ok(text.includes("第二段"), text);
});

test("document(content) 里的非 text block 被跳过", () => {
  const text = textOf([
    {
      type: "document",
      source: {
        type: "content",
        content: [
          { type: "text", text: "有用的文字" },
          { type: "image", source: { type: "url", url: "https://example.com/x.png" } },
        ],
      },
    },
  ]);
  assert.ok(text.includes("有用的文字"), text);
});

test("document(content) 全是非 text 时 part 被丢弃，轮次保留为空消息", () => {
  // 转换器会把「所有 part 都被丢弃」的轮次保留成一条空 user 消息
  // （见 anthropicToChat 里的 else 分支：空消息会留下，但不带内容）。
  // 这里断言的是「没有把 image 误当文本塞进去」，而不是「整条消息消失」。
  const parts = partsOf([
    {
      type: "document",
      source: { type: "content", content: [{ type: "image", source: { type: "url", url: "https://e.com/x.png" } }] },
    },
  ]);
  assert.equal(parts.filter((part) => part.text !== "").length, 0, `不该产出非空文本：${JSON.stringify(parts)}`);
});

// ── source.type = "base64"：二进制留占位，不能静默丢 ─────────────────

test("document(base64) 留下占位提示而不是静默消失", () => {
  const text = textOf([
    { type: "document", title: "合同.docx", source: { type: "base64", media_type: "application/vnd.openxmlformats-officedocument.wordprocessingml.document", data: "UEsDBBQ=" } },
  ]);
  assert.ok(text.includes("合同.docx"), `文件名没进占位：${text}`);
  assert.ok(text.includes("无法解析"), `没有说明读不到：${text}`);
});

test("document(base64) 无 title 时用「未命名文件」兜底", () => {
  const text = textOf([{ type: "document", source: { type: "base64", media_type: "application/pdf", data: "JVBERi0=" } }]);
  assert.ok(text.includes("未命名文件"), text);
  assert.ok(text.includes("application/pdf"), `media_type 没带上：${text}`);
});

test("document(base64) 缺 media_type 时用 octet-stream 兜底", () => {
  const text = textOf([{ type: "document", source: { type: "base64", data: "AAAA" } }]);
  assert.ok(text.includes("application/octet-stream"), text);
});

// ── 回归：不能破坏原有的 text / image ────────────────────────────────

test("普通 text block 不受影响", () => {
  const text = textOf([{ type: "text", text: "你好" }]);
  assert.equal(text, "你好");
});

test("image(base64) 仍然转成 image_url", () => {
  const parts = partsOf([{ type: "image", source: { type: "base64", media_type: "image/png", data: "iVBORw0KGgo=" } }]);
  assert.equal(parts.length, 1);
  assert.equal(parts[0].type, "image_url");
  assert.ok(parts[0].image_url.url.startsWith("data:image/png;base64,"), parts[0].image_url.url);
});

test("image(url) 仍然原样透传", () => {
  const parts = partsOf([{ type: "image", source: { type: "url", url: "https://example.com/a.png" } }]);
  assert.equal(parts[0].image_url.url, "https://example.com/a.png");
});

test("thinking block 仍然被丢弃（上游没有对应物）", () => {
  const parts = partsOf([
    { type: "thinking", thinking: "内部推理" },
    { type: "text", text: "正式回复" },
  ]);
  assert.equal(parts.length, 1);
  assert.equal(parts[0].text, "正式回复");
});

test("未知类型仍然被丢弃", () => {
  const parts = partsOf([{ type: "some_future_type", data: "x" }, { type: "text", text: "ok" }]);
  assert.equal(parts.length, 1);
});

// ── 边界：畸形输入不抛 ──────────────────────────────────────────────

test("document 缺 source 时返回 undefined 而不是抛", () => {
  const parts = partsOf([{ type: "document" }, { type: "text", text: "ok" }]);
  assert.equal(parts.length, 1);
  assert.equal(parts[0].text, "ok");
});

test("document 的 source 是字符串时不抛", () => {
  const parts = partsOf([{ type: "document", source: "not-an-object" }, { type: "text", text: "ok" }]);
  assert.equal(parts.length, 1);
});

test("document 的 source.type 未知时不抛", () => {
  const parts = partsOf([{ type: "document", source: { type: "weird" } }, { type: "text", text: "ok" }]);
  assert.equal(parts.length, 1);
});

test("content 为字符串时走字符串路径，不炸", () => {
  const parts = partsOf("纯字符串消息");
  assert.equal(parts[0].text, "纯字符串消息");
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
