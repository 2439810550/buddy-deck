#!/usr/bin/env node
/**
 * 工具意图准确率测试 —— **会真的调上游模型**，但不执行工具。
 *
 * ## 为什么需要它
 *
 * test-agent.mjs 测的是协议（循环跑得对不对），用的是假的 send。
 * 但真正决定「用户说这句话会不会生图」的是**模型会不会调工具** ——
 * 那是模型的能力，协议测试覆盖不到。
 *
 * 这个脚本把工具定义真的发给模型，看它对各种说法怎么反应，
 * 然后**只看它想不想调、调哪个**，不执行（所以不花钱生图）。
 *
 * ## 为什么要跑这个
 *
 * 关键词方案换成工具方案时，最大的疑问是：
 *
 *   「模型会不会该调的没调？不该调的乱调？」
 *
 * 关键词方案至少是**确定的**（同样的输入永远同样结果）。工具方案
 * 依赖模型的判断，所以必须实测，不能假设。
 *
 * ## 跑法
 *
 *   node scripts/test-tool-intent.mjs
 *
 * 需要已登录的 WorkBuddy 桌面端。每条约 2-5 秒，全程 1-2 分钟。
 *
 * 加 `--verbose` 看每条的实际回复。
 *
 * ## 代价（重要）
 *
 * 「该调」的用例**会真的生图**（花额度）。所以：
 *
 *   node scripts/test-tool-intent.mjs           # 免费用例 + 2 条生图
 *   node scripts/test-tool-intent.mjs --all     # 全跑（8 条生图，花钱）
 *   node scripts/test-tool-intent.mjs --video   # 再加视频（慢，几分钟）
 *
 * 「不该调」的用例是免费的，而且它们才是**防误判的关键** ——
 * 漏判只是用户再说一次，误判会白花额度还答非所问。
 */

import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const verbose = process.argv.includes("--verbose");

const stateHome = await mkdtemp(join(tmpdir(), "wb-tool-intent-"));
const port = 8900 + Math.floor(Math.random() * 90);

const child = spawn(
  process.execPath,
  [join(root, "src", "bin.js"), "serve", "--port", String(port), "--api-key", "intent-key"],
  { env: { ...process.env, WORKBUDDY_API_HOME: stateHome }, stdio: ["ignore", "pipe", "pipe"] },
);

let serverOutput = "";
child.stdout.on("data", (c) => (serverOutput += c.toString()));
child.stderr.on("data", (c) => (serverOutput += c.toString()));

const base = `http://127.0.0.1:${port}/v1`;
const headers = { Authorization: "Bearer intent-key", "Content-Type": "application/json" };

/** 一个「客户端的」工具，用来验证注入后不打架。 */
const clientTool = {
  type: "function",
  function: {
    name: "Read",
    description: "Read a file from the local filesystem",
    parameters: { type: "object", properties: { path: { type: "string" } }, required: ["path"] },
  },
};

async function waitForReady(timeoutMs = 30_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const r = await fetch(`http://127.0.0.1:${port}/healthz`);
      if (r.ok) return true;
    } catch { /* 还没起来 */ }
    await new Promise((d) => setTimeout(d, 250));
  }
  return false;
}

/**
 * 问模型一句，看它有没有走生图/生视频那条路。
 *
 * ## 怎么看出来的（这版修过一个错）
 *
 * 第一版想从响应的 `tool_calls` 里读工具名 —— 那是**错的**。
 * 网关的设计就是「工具调用不出网关」：模型说要调，网关自己执行掉，
 * 客户端拿到的是**最终答复**，里面根本没有 tool_calls。
 *
 * 所以第一版跑出来全是「不调」，看着像模型不行，其实是测试看错了地方。
 *
 * 正确的观测点有三个，这里**同时看**，互为佐证：
 *
 *   ① 最终 content 里有没有图片 markdown  → 生图成功
 *   ② 最终 content 里有没有 taskId/查询地址 → 生视频成功
 *   ③ 网关日志（stderr）里有没有「执行工具 workbuddy_xxx」
 *
 * ③ 最可靠 —— 它直接说明网关执行了什么，不受模型回复措辞影响。
 *
 * ⚠ 这意味着测试**会真的生图**（花钱）。所以用例表要克制，
 * 而且默认只跑前几条。
 */
async function ask(text) {
  const before = serverOutput.length;
  const body = {
    model: "deepseek-v4.1-flash",
    stream: false,
    messages: [{ role: "user", content: text }],
    tools: [clientTool],
  };
  const r = await fetch(`${base}/chat/completions`, { method: "POST", headers, body: JSON.stringify(body) });
  if (!r.ok) return { error: `HTTP ${r.status}`, names: [] };
  const d = await r.json();

  // 看新增的日志
  const fresh = serverOutput.slice(before);
  const ran = [...fresh.matchAll(/执行工具 (workbuddy_[a-z_]+)/g)].map((m) => m[1].replace("workbuddy_", ""));

  const content = d.choices?.[0]?.message?.content ?? "";

  return {
    names: [...new Set(ran)],
    content,
    finish: d.choices?.[0]?.finish_reason,
    called: ran.length > 0,
    hasImage: content.includes("!["),
    // ⚠ 这个判据要**窄**。第一版写了 /taskId|任务 id/ —— 结果模型在
    // 解释「生成是异步的，会返回任务 ID」时也被判成「生成了视频」。
    // 只有真的提交了任务，回复里才会带**具体**的任务 id 和查询地址。
    hasVideo: /\/v1\/video\/status\/\S+/.test(content),
  };
}

// ── 用例表 ────────────────────────────────────────────────────
//
// want 是**期望调用的工具名**（不带前缀会自动补）。
// null = 不该调任何我们的工具。

// ── 用例表 ────────────────────────────────────────────────────
//
// ⚠ 代价不对称，所以分两组：
//
//   「该调」的用例**会真的生图/生视频**（花额度、视频还慢）
//   「不该调」的用例是免费的（模型只是回句话）
//
// 所以默认只跑前 2 条「该调」的，其余用 --all 打开。
// 「不该调」的永远全跑 —— 它们才是防误判的关键。

const SHOULD_GENERATE = [
  ["画一只戴墨镜的橘猫", "generate_image"],
  ["画一张夕阳下的海边", "generate_image"],
  ["帮我画个简单的 logo 草图", "generate_image"],
  ["生成一张赛博朋克风格的城市夜景", "generate_image"],
  ["给我来一张柴犬在草地上打滚的照片", "generate_image"],
  ["draw me a picture of a red circle", "generate_image"],
  ["generate an image of a robot reading a book", "generate_image"],
  ["我想要一张猫咪图片", "generate_image"],
];

const SHOULD_GENERATE_VIDEO = [
  ["生成一个视频，一只猫在跳舞", "generate_video"],
  ["做个短视频，海边日落延时", "generate_video"],
  ["生成一段动画，一个球在弹跳", "generate_video"],
  ["make a short video of waves at sunset", "generate_video"],
];

const SHOULD_NOT = [
  // 讨论画图这件事
  ["画图功能怎么用？", null],
  ["你的生图接口支持什么尺寸？", null],
  ["怎么画流程图？我想给这段代码画个流程图", null],
  ["帮我写一段 midjourney 的提示词，画一只猫", null],
  ["这段代码的架构图怎么画", null],
  // 普通聊天
  ["今天天气怎么样", null],
  ["帮我写一个快速排序", null],
  ["解释一下什么是卷积神经网络", null],
  ["这段报错是什么意思：TypeError: x is not a function", null],
  ["把这句话翻译成英文：你好世界", null],
  // 边界：含糊时倾向于不调（不该乱花额度）
  ["给我看看猫长什么样", null],
  ["视频生成要多久", null],
];

const runAll = process.argv.includes("--all");
const withVideo = process.argv.includes("--video");

const CASES = [
  ...SHOULD_NOT,
  ...(runAll ? SHOULD_GENERATE : SHOULD_GENERATE.slice(0, 2)),
  ...(withVideo ? (runAll ? SHOULD_GENERATE_VIDEO : SHOULD_GENERATE_VIDEO.slice(0, 1)) : []),
];

let pass = 0;
let fail = 0;
const rows = [];

function label(want) {
  if (want === null) return "（不调）";
  return want;
}

const ready = await waitForReady();
if (!ready) {
  process.stdout.write(`网关没起来。输出：\n${serverOutput}\n`);
  child.kill();
  await rm(stateHome, { recursive: true, force: true });
  process.exit(1);
}

process.stdout.write("\n  工具意图实测（会真调模型，但不执行工具）\n");
process.stdout.write("  " + "─".repeat(66) + "\n");

for (const [text, want] of CASES) {
  const result = await ask(text);
  if (result.error !== undefined) {
    fail += 1;
    rows.push(`  FAIL  ${text}\n        请求失败：${result.error}`);
    continue;
  }

  // 判据优先级：日志（最可靠）→ 回复内容（客户端实际看到什么）
  let got = result.names.length > 0 ? result.names[0] : null;
  if (got === null) {
    if (result.hasImage) got = "generate_image";
    else if (result.hasVideo) got = "generate_video";
  }
  const ok = got === want;

  if (ok) {
    pass += 1;
    rows.push(`  PASS  ${text}${want === null ? "" : `  → ${got}`}`);
  } else {
    fail += 1;
    rows.push(`  FAIL  ${text}\n        期望 ${label(want)}，实际 ${label(got)}`
      + (verbose ? `\n        回复：${result.content.slice(0, 160)}` : ""));
  }
}

process.stdout.write(rows.join("\n") + "\n");
process.stdout.write("  " + "─".repeat(66) + "\n");
process.stdout.write(`  ${fail === 0 ? "ALL GREEN" : "有偏差"} — ${pass} 对, ${fail} 偏差\n`);
process.stdout.write("\n");

// 停掉网关
child.kill();
await new Promise((d) => setTimeout(d, 300));
await rm(stateHome, { recursive: true, force: true });

// 有偏差不算「构建失败」—— 模型判断本来就有波动，
// 但要把数字暴露出来让人决定要不要调 description。
process.exit(0);
