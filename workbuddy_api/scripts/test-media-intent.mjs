/**
 * 媒体意图识别的测试 —— 图片 vs 视频。
 *
 * ## 为什么单独一个文件
 *
 * 原来的 image-intent.js 只认「画图」，于是：
 *
 *     「生成一个视频」      → 命中「生成」，把「视频」当画面描述 → 生图
 *     「生成一个关于猫的视频」→ 同上
 *     「做个视频」         → 命中「做个」→ 生图
 *
 * 用户看到的就是：**说生成视频，出来一张图**。
 *
 * 现在要区分三种意图：
 *
 *     image   → 调生图
 *     video   → 调生视频
 *     none    → 走普通聊天
 *
 * ## 判定优先级
 *
 * **视频关键词优先于图片动词**。因为「生成一段猫的视频」里既有
 * 「生成」也有「视频」—— 视频才是用户的真实意图。
 *
 * 跑法：node scripts/test-media-intent.mjs
 */

import { strict as assert } from "node:assert";
import { detectMediaIntent } from "../src/media-intent.js";

let pass = 0;
let fail = 0;
const lines = [];

/** 断言意图类型。 */
function want(text, expected) {
  const r = detectMediaIntent(text);
  if (r.kind === expected) {
    pass += 1;
    lines.push(`  PASS  ${JSON.stringify(text)} → ${r.kind}`);
  } else {
    fail += 1;
    lines.push(`  FAIL  ${JSON.stringify(text)}\n` +
      `        期望 ${expected}，实际 ${r.kind}（${r.reason}）`);
  }
}

function group(t) {
  lines.push("");
  lines.push(`  ── ${t} ──`);
}

// ══════════════════════════════════════════════════════════════
group("视频：明确说「视频」");

want("生成一个视频", "video");
want("生成一个关于猫的视频", "video");
want("做个视频", "video");
want("生成视频", "video");
want("一段猫的视频", "video");
want("一只猫在跳舞的视频", "video");
want("帮我做个猫咪跳舞的短视频", "video");
want("生成一段动画", "video");
want("做一个小视频", "video");
want("生成一个猫咪的短视频", "video");
want("来段视频", "video");
want("create a video of a cat", "video");
want("generate a video", "video");
want("make a short video about sunsets", "video");

group("视频：应抠出正确的描述（不含「视频」这俩字）");

{
  const cases = [
    ["生成一个关于猫的视频", "猫"],
    ["生成一个小猫打滚的视频", "小猫打滚"],
    ["做个柴犬在草地上的视频", "柴犬在草地上"],
  ];
  for (const [text, expect] of cases) {
    const r = detectMediaIntent(text);
    const ok = r.prompt.includes(expect) && !r.prompt.includes("视频");
    if (ok) {
      pass += 1;
      lines.push(`  PASS  ${JSON.stringify(text)} → prompt=${JSON.stringify(r.prompt)}`);
    } else {
      fail += 1;
      lines.push(`  FAIL  ${JSON.stringify(text)} → prompt=${JSON.stringify(r.prompt)}，期望含「${expect}」且不含「视频」`);
    }
  }
}

group("图片：不能被「视频」抢走");

want("画一只猫", "image");
want("生成一张猫的图片", "image");
want("画一张夕阳的插画", "image");
want("帮我画一幅水墨山水", "image");
want("draw a red circle", "image");
want("generate an image of a robot", "image");
// 这句里有「动画」吗？没有，是「图片」—— 必须仍是 image
want("生成一张猫咪图片", "image");

group("都不是：普通聊天");

want("今天天气怎么样", "none");
want("帮我写一个快速排序", "none");
want("视频生成功能怎么用", "none");
want("这段视频怎么剪辑", "none");
want("怎么做视频", "none");
want("什么是短视频", "none");
want("画图接口支持视频吗", "none");
want("这段代码怎么画流程图", "none");

group("边界：长文本、代码、引用");

want("```python\nvideo = cv2.VideoCapture(0)\n```", "none");
want("a".repeat(300) + " 生成一个视频", "none");
want("他说「画一张猫」", "none");

group("边界：空与非字符串");

want("", "none");
want("   ", "none");
want(null, "none");
want(undefined, "none");
want(123, "none");

// ══════════════════════════════════════════════════════════════
console.log(lines.join("\n"));
console.log("");
console.log(`  ${fail === 0 ? "ALL GREEN" : "FAILED"} — ${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
