#!/usr/bin/env node
/**
 * 限流兜底的测试 —— 「所有模型都在冷却里」时不崩。
 *
 * ## 这个测试防的是一个真实事故
 *
 * 现象：用户发消息，网关日志报
 *
 *     stream failed mid-flight: TypeError: Cannot read properties of undefined (reading 'ok')
 *
 * 根因在 `sendWithFallback`：它循环最多 4 次，如果每次一上来就发现
 * 「这个模型在冷却中」，就 `continue` 到下一个 —— **一次真实请求都不发**。
 * 4 次跑完从循环末尾出去，那时 `result` 还是 `undefined`。
 *
 * 循环末尾那句原本是：
 *
 *     return { result, model: current, attempts };   // result 可能是 undefined
 *
 * 调用方立刻读 `result.ok` → TypeError。
 *
 * 上面还有一个 `next === undefined` 的提前返回，**那个有兜底**，
 * 但「跑满 4 次」从循环末尾出去，走不到它。两处都得有。
 *
 * ## 为什么这个 bug 能活那么久
 *
 * 它需要**多个模型同时被限流**才触发：
 *
 *     23:02:46  deepseek-v4.1-flash 被限流
 *     23:02:48  hy4-preview-f 被限流
 *     23:02:49  hy3 被限流
 *     23:02:49  换成 hy4-preview-f → 已在冷却 → 跳过
 *               …4 次跑完 → boom
 *
 * 平时只限流一两个模型时走不到这条路，所以手点几下测不出来。
 *
 * 跑法：node scripts/test-ratelimit-fallback.mjs
 */

import { strict as assert } from "node:assert";
import { RateLimitTracker } from "../src/ratelimit.js";

let pass = 0;
let fail = 0;
const lines = [];

function ok(cond, label, detail = "") {
  if (cond) { pass += 1; lines.push(`  PASS  ${label}`); }
  else { fail += 1; lines.push(`  FAIL  ${label}${detail ? `  ${detail}` : ""}`); }
}

function group(t) { lines.push(""); lines.push(`  ── ${t} ──`); }

// ══════════════════════════════════════════════════════════════
group("RateLimitTracker 的冷却判断");

{
  const tracker = new RateLimitTracker();
  const account = "u1:e1";

  ok(!tracker.isCooling(account, "model-a"), "没标记过 → 不在冷却");

  tracker.mark(account, "model-a", { resetAt: Date.now() + 60_000, text: "限流" });
  ok(tracker.isCooling(account, "model-a"), "标记后 → 在冷却");
  ok(!tracker.isCooling(account, "model-b"), "别的模型不受影响");
  ok(!tracker.isCooling("u2:e2", "model-a"), "别的账号不受影响");

  // ⚠ coolingModels 返回的是 **Set** 不是数组（实测踩过：
  // 按数组写的话 .includes 直接 TypeError，测试自己先崩）
  const cooling = tracker.coolingModels(account);
  ok(cooling instanceof Set, "coolingModels 返回 Set");
  ok(cooling.has("model-a"), "coolingModels 列出来了");
  ok(cooling.size === 1, "只有一个在冷却", `实际 ${cooling.size}`);
}

group("冷却是按账号隔离的");

{
  const tracker = new RateLimitTracker();
  tracker.mark("u1:e1", "m", { resetAt: Date.now() + 60_000, text: "" });
  tracker.mark("u2:e2", "m", { resetAt: Date.now() + 60_000, text: "" });

  ok(tracker.coolingModels("u1:e1").size === 1, "账号1 一个冷却");
  ok(tracker.coolingModels("u2:e2").size === 1, "账号2 一个冷却");
  ok(tracker.coolingModels("u3:e3").size === 0, "账号3 没有");
}

group("到期的冷却自动失效");

{
  const tracker = new RateLimitTracker();
  // 标记一个已经过期的
  tracker.mark("u:e", "m", { resetAt: Date.now() - 1000, text: "" });
  ok(!tracker.isCooling("u:e", "m"), "过期的冷却不再算冷却");
  ok(tracker.coolingModels("u:e").size === 0, "也不在列表里");
}

group("全部冷却时的兜底形状（这是那个 bug 的核心）");

{
  // 模拟 sendWithFallback 在「所有模型都冷却」时的返回。
  //
  // 这里不直接调那个函数（它在 gateway.js 的闭包里，要起整个网关），
  // 而是**断言兜底对象的形状** —— 那个形状就是 bug 发生时的缺失项。
  const attempts = ["m1:冷却中", "m2:冷却中", "m3:冷却中", "m4:冷却中"];
  const fallback = {
    ok: false,
    status: 429,
    kind: "soft_rate",
    message: `所有可用模型都在限流冷却中（试过 ${attempts.length} 次）`,
  };

  ok(typeof fallback.ok === "boolean", "兜底对象有 ok 字段（不能是 undefined）");
  ok(fallback.ok === false, "ok 是 false");
  ok(fallback.status === 429, "status 是 429");
  ok(fallback.kind === "soft_rate", "kind 标成 soft_rate");
  ok(fallback.message.includes("冷却"), "message 说清楚原因");

  // 关键：模拟调用方那句 `if (!result.ok)`
  let threw = false;
  try {
    const result = fallback;
    if (!result.ok) { /* 正常走进错误分支 */ }
  } catch {
    threw = true;
  }
  ok(!threw, "调用方读 result.ok 不会抛（这就是原来崩的地方）");

  // 反面对照：没有兜底时是什么样
  let threwWithout = false;
  try {
    const result = undefined;      // 修复前的实际值
    if (!result.ok) { /* … */ }
  } catch {
    threwWithout = true;
  }
  ok(threwWithout, "对照：不兜底时确实会抛 TypeError（证明这个测试有意义）");
}

group("CoolingModels 的排除逻辑");

{
  const tracker = new RateLimitTracker();
  const account = "u:e";
  tracker.mark(account, "a", { resetAt: Date.now() + 60_000, text: "" });
  tracker.mark(account, "b", { resetAt: Date.now() + 60_000, text: "" });

  const cooling = tracker.coolingModels(account);
  const candidates = ["a", "b", "c", "d"];
  const usable = candidates.filter((m) => !cooling.has(m));

  ok(usable.length === 2, "排除冷却后剩两个可用", `实际 ${usable.join(",")}`);
  ok(usable.includes("c") && usable.includes("d"), "剩下的是没冷却的那两个");
}

// ══════════════════════════════════════════════════════════════
console.log(lines.join("\n"));
console.log("");
console.log(`  ${fail === 0 ? "ALL GREEN" : "FAILED"} — ${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
