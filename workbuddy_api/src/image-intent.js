/**
 * 画图意图识别。
 *
 * ## 为什么需要它
 *
 * 网关现在有两种能力，但它们之间是断开的：
 *
 *     用户说「画一只猫」
 *        ↓
 *     聊天模型收到 → 回一段提示词（它看不见生图工具）
 *        ↓
 *     用户还得自己去调 /v1/images/generations
 *
 * 这个模块把两者接起来：**识别出「用户在要求画图」就直接调生图**，
 * 把生成的图片作为回复返回。用户说一句话就拿到图。
 *
 * ## 为什么必须保守
 *
 * 误判的代价不对称：
 *
 *   · 漏判（该画没画）→ 用户再说一次，最多多一句话，**无害**
 *   · 误判（不该画却画了）→ 用户问「这段代码怎么画流程图」，结果
 *     网关跑去生图，**浪费一次生图额度、答非所问**
 *
 * 生图是有成本的（要积分），所以：
 *
 *   1. **必须命中明确句式**，不做「模糊语义猜测」
 *   2. **只对最后一条用户消息**判断（历史里的「画」不算）
 *   3. **有问题迹象就不画**（问号、代码块、引号包住的关键词）
 *   4. 可以整体关掉（环境变量 `WORKBUDDY_AUTO_IMAGE=0`）
 *
 * ## 判定规则
 *
 * 命中条件（全部满足）：
 *
 *   · 短句（默认 ≤ 200 字符）—— 长文本多半是代码/文档
 *   · 有明确的动词开头或结尾：「画」「绘制」「生成图片」「画一张」
 *   · 不包含疑问标记（？? 吗 呢 怎么 如何 为什么）
 *   · 不包含代码块（```）
 *   · 不像是「在讨论画图这件事」（如「画图功能怎么用」）
 *
 * @module workbuddy-api/image-intent
 */

/**
 * 明确的「要画」动词开头。
 *
 * ── 为什么用正则而不是穷举 ──────────────────────────────
 *
 * 第一版写的是字符串列表：画一张 / 画个 / 画只 / 画一幅…
 * 测试立刻漏了：
 *
 *     画一只戴墨镜的橘猫   → 漏（列表里没「画一只」）
 *     画一匹马            → 漏
 *     画两朵云            → 漏
 *     画三只鸟            → 漏
 *
 * 中文量词太多（只/匹/朵/条/张/幅/个/座/棵/…），还带数词
 * （一/两/三/几/数十）—— 穷举不可能穷尽。改成模式匹配。
 *
 * 模式：可选的「帮我/给我/请」+ 画/绘制/生成 … 数字 + 量词
 * 后面必须跟东西（不然「画个」这种半截话不该命中）。
 */
const LEADING_PATTERN = new RegExp(
  "^(?:请|帮我|给我|麻烦)?\\s*" +            // 可选敬语
  "(?:画|绘制|生成|绘制出|来|做)\\s*" +        // 动词
  "(?:一|两|二|三|四|五|六|七|八|九|十|几|数)?\\s*" + // 可选数词
  "(?:张|幅|个|只|匹|朵|条|座|棵|面|枚|颗|把|支|串|片|块|轮|道)?\\s*" + // 可选量词
  "(?=\\S)",                                  // 后面必须还有东西
  "u",
);

/** 英文的。词形变化少，用列表就够。 */
const LEADING_VERBS_EN = [
  "draw me", "draw a", "draw an", "draw the", "draw ",
  "paint me", "paint a", "paint an",
  "generate an image", "generate a picture", "generate image",
  "create an image", "create a picture", "make an image", "make a picture",
  "illustrate ", "sketch ",
];

/** 结尾是「画个…」这类。有些用户会说「一只猫的插画，画一下」。 */
const TRAILING_VERBS = [
  "画一下", "画一张", "画个图", "画幅画", "画下来",
  "帮我画一下", "帮我画一张", "给我画一下",
  "生成图片", "生成一张图", "生成个图",
  "draw it", "generate it",
];

/** 出现这些就判定「不是在要图」，直接放弃。 */
const NEGATIVE_MARKERS = [
  // 疑问
  "？", "?", "吗", "呢", "怎么", "如何", "为什么", "为何", "是不是",
  "能不能", "可不可以", "可以吗", "有没有", "是什么", "什么叫",
  // 代码 / 引用
  "```", "`", "\"画", "\"画", "「画", "『画",
  // 讨论「画图这件事」而不是要画
  "画图功能", "画图接口", "画图的 api", "图像生成功能", "生图功能",
  "怎么画图", "如何画图", "画图怎么", "支持画图", "能画图",
  "画图能力", "画图的原理", "画图是怎么",
  // 代码里的「画」很常见（canvas.drawImage 之类）
  "drawimage", "canvas.", ".draw(", "paint(", "画笔",
];

/** 命中这些说明是在要「流程图/架构图/图表」—— 那是代码活儿，不是生图。 */
const CODE_DIAGRAM_MARKERS = [
  "流程图", "架构图", "时序图", "类图", "状态图", "er图", "er 图",
  "uml", "mermaid", "plantuml", "graphviz",
  "甘特图", "思维导图", "组织架构",
  "flowchart", "sequence diagram",
];

/**
 * 判断一条消息是不是在要求画图。
 *
 * @param {string} text 用户消息（纯文本）
 * @param {object} [options]
 * @param {number} [options.maxLength] 超过这个长度直接放弃（默认 200）
 * @returns {{wants: boolean, prompt: string, reason: string}}
 *          wants=false 时 prompt 是空串；reason 说明为什么（调试用）
 */
export function detectImageIntent(text, options = {}) {
  const maxLength = options.maxLength ?? 200;
  const reason = (why, wants = false, prompt = "") => ({ wants, prompt, reason: why });

  if (typeof text !== "string") return reason("不是文本");
  const trimmed = text.trim();
  if (trimmed === "") return reason("空消息");

  // 短句判断 —— 长文本多半是代码、文档、日志
  if (trimmed.length > maxLength) return reason(`${trimmed.length} 字符，超过 ${maxLength}`);

  const lower = trimmed.toLowerCase();

  // 否定标记优先（疑问、代码、讨论性质）
  for (const marker of NEGATIVE_MARKERS) {
    if (lower.includes(marker)) return reason(`含排除标记「${marker}」`);
  }

  // 流程图/架构图那类是要写代码，不是生图
  for (const marker of CODE_DIAGRAM_MARKERS) {
    if (lower.includes(marker)) return reason(`是图表需求（${marker}）`);
  }

  // 命中开头模式
  const patternHit = LEADING_PATTERN.exec(trimmed);
  if (patternHit !== null) {
    const prompt = extractAfterVerb(trimmed, patternHit[0].length);
    if (prompt === "") return reason("命中动词但提取不出描述");
    return reason(`开头命中「${patternHit[0].trim()}」`, true, prompt);
  }

  // 英文的用列表
  for (const verb of LEADING_VERBS_EN) {
    if (lower.startsWith(verb)) {
      const prompt = extractAfterVerb(trimmed, verb.length);
      if (prompt === "") return reason("命中动词但提取不出描述");
      return reason(`开头命中「${verb.trim()}」`, true, prompt);
    }
  }

  // 命中结尾动词
  for (const verb of TRAILING_VERBS) {
    if (lower.endsWith(verb.toLowerCase())) {
      const prompt = trimmed.slice(0, trimmed.length - verb.length).trim()
        .replace(/[，,、。.]+$/, "").trim();
      if (prompt === "") return reason("命中结尾动词但提取不出描述");
      return reason(`结尾命中「${verb}」`, true, prompt);
    }
  }

  return reason("没有明确的画图请求");
}

/**
 * 把动词前缀切掉，剩下的就是画面描述。
 *
 * 抠不出来就返回原文 —— 让模型自己从整句里理解。
 */
function extractAfterVerb(text, prefixLength) {
  const rest = text.slice(prefixLength).trim();
  // 去掉开头的连接词和标点
  return rest
    .replace(/^[，,：:、。.\s]+/, "")
    .trim();
}

/**
 * 把提取出的描述补全成一个适合生图的 prompt。
 *
 * 为什么不直接用原文：生图模型对「画一张…」这种祈使句的响应不如
 * 直接的名词描述。去掉动词之后它的表现更稳定。
 */
export function buildImagePrompt(description) {
  const text = description.trim();
  if (text === "") return "";
  // 不额外加风格词 —— 用户想要什么风格应该自己说，
  // 我们擅自加「高清」「4K」只会干扰。
  return text;
}
