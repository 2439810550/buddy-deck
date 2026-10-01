/**
 * 画图意图识别的测试。
 *
 * 这个模块的**误判代价不对称**：
 *   · 漏判 → 用户再说一次，无害
 *   · 误判 → 花掉一次生图额度，而且答非所问
 *
 * 所以测试里「不该画」那组要写得比「该画」那组更狠。
 *
 * 跑法：node scripts/test-image-intent.mjs
 */

import { strict as assert } from "node:assert";
import { detectImageIntent, buildImagePrompt } from "../src/image-intent.js";

let pass = 0;
let fail = 0;
const lines = [];

function check(text, expected, why = "") {
  const r = detectImageIntent(text);
  const ok = r.wants === expected;
  if (ok) {
    pass += 1;
    lines.push(`  PASS  ${JSON.stringify(text).slice(0, 44)} → ${expected ? "要画" : "不画"}`);
  } else {
    fail += 1;
    lines.push(`  FAIL  ${JSON.stringify(text).slice(0, 44)}\n` +
      `        期望 ${expected ? "要画" : "不画"}，实际 ${r.wants ? "要画" : "不画"}（${r.reason}）${why}`);
  }
}

function group(t) {
  lines.push("");
  lines.push(`  ── ${t} ──`);
}

// ══════════════════════════════════════════════════════════════
group("该画：明确的开头动词");

check("画一张猫", true);
check("画个太阳", true);
check("画一只戴墨镜的橘猫", true);
check("帮我画一张赛博朋克风格的城市", true);
check("给我画个简单的图标", true);
check("请画一只柴犬", true);
check("绘制一幅水墨山水", true);
check("生成一张猫咪的图片", true);
check("draw a cat", true);
check("Draw me a sunset", true);
check("generate an image of a robot", true);

group("该画：结尾动词");

check("一只在草地上打滚的柴犬，画一下", true);
check("夕阳下的海边，画一张", true);
check("赛博朋克城市夜景，生成图片", true);

// ══════════════════════════════════════════════════════════════
group("不该画：疑问句（最重要的那组）");

check("画图功能怎么用？", false);
check("这个网关支持画图吗？", false);
check("你能画图吗", false);
check("如何用 API 生成图片？", false);
check("为什么画图失败了？", false);
check("生图功能是不是要积分？", false);
check("画图的原理是什么", false);
check("能不能画图", false);

group("不该画：讨论「画图这件事」");

check("画图接口的地址是多少", false);
check("画图能力支持哪些模型", false);
check("生图功能怎么开启", false);
check("画图是怎么实现的", false);

group("不该画：代码/技术场景");

check("这段代码怎么画流程图", false);
check("用 mermaid 画一个时序图", false);
check("帮我画一个架构图", false);
check("画个 UML 类图", false);
check("canvas.drawImage 怎么用", false);
check("这个 ER 图怎么画", false);
check("画个甘特图", false);

group("不该画：代码块和引用");

check("```js\nfoo()\n```", false);
check("看这段 `drawImage` 调用", false);
check('"画一张猫" 这句话是什么意思', false);

group("不该画：长文本");

check("画一张猫" + "很长的补充说明".repeat(30), false);

group("不该画：普通的聊天");

check("你好", false);
check("帮我看看这段代码", false);
check("今天天气怎么样", false);
check("解释一下什么是递归", false);
check("", false);
check("   ", false);

// ══════════════════════════════════════════════════════════════
group("prompt 提取");

{
  const r = detectImageIntent("画一张猫");
  assert.equal(r.wants, true);
  assert.ok(r.prompt.includes("猫"), `应该抠出「猫」，实际「${r.prompt}」`);
  lines.push(`  PASS  画一张猫 → prompt="${r.prompt}"`);

  const r2 = detectImageIntent("帮我画一只戴墨镜的橘猫");
  assert.ok(r2.prompt.includes("橘猫"), `实际「${r2.prompt}」`);
  lines.push(`  PASS  帮我画一只戴墨镜的橘猫 → prompt="${r2.prompt}"`);

  const r3 = detectImageIntent("一只在草地上打滚的柴犬，画一下");
  assert.ok(r3.prompt.includes("柴犬"), `实际「${r3.prompt}」`);
  lines.push(`  PASS  结尾动词 → prompt="${r3.prompt}"`);

  const r4 = detectImageIntent("draw a red circle");
  assert.ok(r4.prompt.toLowerCase().includes("red circle"), `实际「${r4.prompt}」`);
  lines.push(`  PASS  draw a red circle → prompt="${r4.prompt}"`);

  pass += 4;
}

group("buildImagePrompt");

{
  assert.equal(buildImagePrompt("一只猫"), "一只猫");
  assert.equal(buildImagePrompt("  空格  "), "空格");
  assert.equal(buildImagePrompt(""), "");
  lines.push("  PASS  buildImagePrompt 只做 trim，不加料");
  pass += 1;
}

// ══════════════════════════════════════════════════════════════
console.log(lines.join("\n"));
console.log("");
console.log(`  ${fail === 0 ? "ALL GREEN" : "FAILED"} — ${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
