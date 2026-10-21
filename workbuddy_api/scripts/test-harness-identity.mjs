#!/usr/bin/env node
/**
 * 客户端身份声明清洗的测试。
 *
 * ## 这个模块防的是 2026-10-20 那次风控封禁
 *
 * 腾讯给 copilot.tencent.com 加了渠道校验：system 消息首句如果是
 * 客户端身份声明原文，回 400 / code 11128。
 *
 * 实测（本地复现 + 社区 PR #204 一致）：
 *
 *   ① 以固定句开头           → 400 被拦
 *   ② 固定句前垫一句         → 200 通过
 *   ③ 固定句换成中性表述     → 200 通过
 *   ④ 完全不带               → 200 通过
 *
 * 所以清洗策略是「垫一句中性话」（零信息损失）。
 *
 * 跑法：node scripts/test-harness-identity.mjs
 */

import { strict as assert } from "node:assert";
import { prepareChatBody } from "../src/upstream.js";

let pass = 0;
let fail = 0;
const lines = [];

function check(label, actual, expected) {
  const ok = JSON.stringify(actual) === JSON.stringify(expected);
  if (ok) { pass++; lines.push(`  PASS  ${label}`); }
  else {
    fail++;
    lines.push(`  FAIL  ${label}\n        期望 ${JSON.stringify(expected)}\n        实际 ${JSON.stringify(actual)}`);
  }
}

function group(t) { lines.push(""); lines.push(`  ── ${t} ──`); }

const DSH = "You are an AI agent powered by DeepSeek Harness.";

/** 准备请求体并返回 system 文本。 */
function systemOf(body) {
  const prepared = JSON.parse(prepareChatBody(JSON.stringify(body)));
  const sys = prepared.messages.find((m) => m.role === "system");
  if (!sys) return null;
  if (typeof sys.content === "string") return sys.content;
  return sys.content.map((b) => b.text ?? "").join("\n");
}

// ══════════════════════════════════════════════════════════════
group("该清洗：DSH 身份句在开头");

{
  const sys = systemOf({
    model: "m",
    messages: [
      { role: "system", content: DSH + "\n\nYou are a coding assistant." },
      { role: "user", content: "hi" },
    ],
  });
  // 关键：不能再以身份句开头
  check("不再以身份句开头", sys.startsWith(DSH), false);
  check("原内容还在（只是前面多了一句）", sys.includes(DSH), true);
  check("原内容还在（后半段）", sys.includes("You are a coding assistant."), true);
}

group("该清洗：多段内容形态");

{
  const sys = systemOf({
    model: "m",
    messages: [
      { role: "system", content: [{ type: "text", text: DSH }, { type: "text", text: "More." }] },
      { role: "user", content: "hi" },
    ],
  });
  check("数组形态也被清洗", sys.startsWith(DSH), false);
  check("第一段原文还在", sys.includes(DSH), true);
}

group("该清洗：前面有空白");

{
  const sys = systemOf({
    model: "m",
    messages: [
      { role: "system", content: "\n\n  " + DSH },
      { role: "user", content: "hi" },
    ],
  });
  check("前导空白不影响识别", sys.replace(/^[\s]+/, "").startsWith(DSH), false);
}

// ══════════════════════════════════════════════════════════════
group("不该动：普通 system");

{
  const text = "You are a helpful assistant.";
  const sys = systemOf({
    model: "m",
    messages: [{ role: "system", content: text }, { role: "user", content: "hi" }],
  });
  check("普通 system 原样保留", sys, text);
}

{
  // 身份句在中间（社区实测这种情况本来就通过，不该动）
  const text = "First line.\n" + DSH + "\nMore.";
  const sys = systemOf({
    model: "m",
    messages: [{ role: "system", content: text }, { role: "user", content: "hi" }],
  });
  check("句中出现的身份句不动", sys, text);
}

{
  // 已经在 user 消息里（实测通过，不动）
  const prepared = JSON.parse(prepareChatBody(JSON.stringify({
    model: "m",
    messages: [
      { role: "system", content: "Normal." },
      { role: "user", content: DSH },
    ],
  })));
  const user = prepared.messages.find((m) => m.role === "user");
  check("user 消息里的不动", user.content, DSH);
}

group("边界：不该崩");

{
  const cases = [
    ["没有 messages", { model: "m" }],
    ["messages 不是数组", { model: "m", messages: "垃圾" }],
    ["messages 里有 null", { model: "m", messages: [null, { role: "user", content: "hi" }] }],
    ["system content 是数字", { model: "m", messages: [{ role: "system", content: 123 }] }],
    ["system content 是 null", { model: "m", messages: [{ role: "system", content: null }] }],
    ["system content 是空数组", { model: "m", messages: [{ role: "system", content: [] }] }],
  ];
  for (const [label, body] of cases) {
    let threw = false;
    try { prepareChatBody(JSON.stringify(body)); } catch { threw = true; }
    check(label + " 不抛异常", threw, false);
  }
}

group("其它改写仍然生效（不能碰坏已有逻辑）");

{
  const prepared = JSON.parse(prepareChatBody(JSON.stringify({
    model: "m", stream: false,
    messages: [{ role: "developer", content: "be nice" }, { role: "user", content: "hi" }],
    tool_choice: "none",
    tools: [{ type: "function", function: { name: "x" } }],
  })));
  check("stream 被强制成 true", prepared.stream, true);
  check("developer → system", prepared.messages[0].role, "system");
  check("tool_choice=none 时 tools 被删", "tools" in prepared, false);
}

// ══════════════════════════════════════════════════════════════
console.log(lines.join("\n"));
console.log("");
console.log(`  ${fail === 0 ? "ALL GREEN" : "FAILED"} — ${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
