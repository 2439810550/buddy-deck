/**
 * 媒体意图识别：用户这句话是要**图片**、要**视频**，还是普通聊天？
 *
 * ## 为什么需要单独一层
 *
 * 原来只有一个 `detectImageIntent`，它的动词表里有「生成」：
 *
 *     「生成一个视频」        → 命中「生成」，「视频」当画面描述 → **生图**
 *     「生成一个关于猫的视频」  → 同上
 *     「做个视频」           → 命中「做个」→ **生图**
 *
 * 用户说「生成视频」，拿到一张图。这不是「偶尔漏判」，是**路由错了**。
 *
 * 所以判定要分两步：
 *
 *     第 1 步：是不是在要**视频**？   ← 关键词优先
 *     第 2 步：不是的话，是不是在要**图片**？ ← 复用原来的 image-intent
 *
 * ## 为什么视频判定优先
 *
 * 「生成一段猫的视频」里既有「生成」（图片动词）又有「视频」（真意图）。
 * 单独看动词会误判成图片，所以**先查视频特征词**，命中就是视频。
 *
 * ## 视频判定为什么比图片判定宽松
 *
 * 代价不对称，方向和图片相反：
 *
 *   · 图片误判 → 花一次生图额度（便宜）
 *   · 视频误判 → 花一次生视频额度，而且视频**贵得多、慢得多**（几分钟）
 *
 * 所以视频判定**必须严格**：只有明确说出「视频 / 短片 / 动画 / video」
 * 这类词才算。说「动起来」不算，「做个大片」不算 —— 宁可漏判。
 *
 * 反过来，一旦确认是视频，**不在乎它有没有图片动词** —— 明确说了
 * 「视频」就是铁证。
 *
 * @module workbuddy-api/media-intent
 */

import { detectImageIntent } from "./image-intent.js";

/**
 * 视频特征词。
 *
 * 分三档：
 *
 *   ① 名词：视频 / 短片 / 影片 / 动画 / video / clip / animation
 *      —— 出现就强烈指向视频
 *
 *   ② 时长量词组合：段视频、条视频（「一段」「一条」+ 视频）
 *      —— 中文里说视频常这么讲。但光有「一段」不够，必须跟视频词
 *
 *   ③ 英文：video / clip / animation / movie
 *
 * 注意**不收**的词（收了会误判）：
 *   · 「动起来」「动态」  → 可能是 CSS 动画、GIF 请求
 *   · 「大片」「电影感」  → 多半是描述**图片风格**
 *   · 「剪辑」「播放」    → 是在处理已有视频，不是要生成
 */
const VIDEO_NOUNS = [
  "视频", "短片", "影片", "小视频", "短视频", "动画", "動畫",
  "video", "clip", "animation", "movie", "footage",
];

/**
 * 说「视频」但实际**不是**要生成视频的情形 —— 命中就退回普通聊天。
 *
 * 为什么要有：用户问「视频生成功能怎么用」「这段视频怎么剪辑」时，
 * 他是在**讨论/处理**视频，不是在**要**一个视频。
 */
const VIDEO_EXCLUSIONS = [
  // 疑问
  "？", "?", "吗", "呢", "怎么", "如何", "为什么", "为何",
  "是不是", "能不能", "可不可以", "可以吗", "有没有", "是什么",
  "什么叫", "什么是", "支持",
  // 处理已有视频的动作
  "剪辑", "下载", "播放", "上传", "转换", "压缩", "合并",
  "字幕", "转码", "解析", "抓取",
  // 代码
  "```", "`", "cv2.", "videocapture", "ffmpeg",
  // 引用别人的话
  "\"视频", "\"视频", "「视频", "『视频",
];

/**
 * 视频动作词 —— 「视频」前面要有「生成/做/来」这类动词才更可信。
 *
 * 但**不强制**：很多客户端会把用户原话直传，中间没有动词也可能
 * （比如就发了「一个猫咪视频」）。所以这一层只用来**加分**，不否决。
 */
const VIDEO_VERBS = [
  "生成", "做", "来", "出", "要", "给我", "帮我", "拍", "制作", "创建",
  "generate", "create", "make",
];

/**
 * 判断一条消息要什么媒体。
 *
 * @param {string} text 用户消息
 * @param {object} [options]
 * @param {number} [options.maxLength] 超过这个长度直接放弃（默认 200）
 * @returns {{kind: "image"|"video"|"none", prompt: string, reason: string}}
 */
export function detectMediaIntent(text, options = {}) {
  const maxLength = options.maxLength ?? 200;
  const none = (why) => ({ kind: "none", prompt: "", reason: why });
  const hit = (kind, prompt, why) => ({ kind, prompt, reason: why });

  if (typeof text !== "string") return none("不是文本");
  const trimmed = text.trim();
  if (trimmed === "") return none("空消息");
  if (trimmed.length > maxLength) return none(`${trimmed.length} 字符，超过 ${maxLength}`);

  const lower = trimmed.toLowerCase();

  // ── 第 1 步：视频（优先）──────────────────────────────────
  const videoWord = VIDEO_NOUNS.find((w) => lower.includes(w));
  if (videoWord !== undefined) {
    let excluded = null;
    for (const marker of VIDEO_EXCLUSIONS) {
      if (lower.includes(marker)) { excluded = marker; break; }
    }

    if (excluded !== null) {
      // 排除了不是「要视频」，但**可能是要图片** ——
      // 比如「画图接口支持视频吗」既不画也不生视频。
      // 这里直接交给图片判定，让它按自己的规则答。
      return fromImage(trimmed, options, `提到「${videoWord}」但含排除标记「${excluded}」`);
    }

    const prompt = extractVideoPrompt(trimmed, videoWord);
    if (prompt !== "") {
      const hasVerb = VIDEO_VERBS.some((v) => lower.includes(v));
      return hit("video", prompt,
        `命中视频词「${videoWord}」${hasVerb ? " + 动作词" : "（无动作词，词本身已足够明确）"}`);
    }

    // 只有「视频」两个字、没有别的描述 —— 也算要视频
    // （用户可能想让它自由发挥，或客户端会补上下文）
    return hit("video", "", `命中视频词「${videoWord}」，但没有额外描述`);
  }

  // ── 第 2 步：图片（复用原判定）──────────────────────────
  return fromImage(trimmed, options, "没有视频词");
}

/** 走原来的图片判定，把结果翻译成统一的 kind。 */
function fromImage(trimmed, options, prefix) {
  const r = detectImageIntent(trimmed, options);
  if (!r.wants) return { kind: "none", prompt: "", reason: `${prefix}；${r.reason}` };
  return { kind: "image", prompt: r.prompt, reason: `${prefix}；${r.reason}` };
}

/**
 * 从「生成一个关于猫的视频」里抠出「关于猫」。
 *
 * 要做的：
 *   ① 去掉视频词本身（视频/短片/video…）—— 那是需求类型，不是画面描述
 *   ② 去掉动词和量词前缀（生成/做/一个/一段…）
 *   ③ 去掉残留的连接词（关于/的/一段）
 *
 * 抠不出来就返回空串 —— 调用方会用一个默认描述，让模型自由发挥。
 */
function extractVideoPrompt(text, videoWord) {
  // 用不区分大小写的替换，去掉所有视频词
  let s = text;
  for (const w of VIDEO_NOUNS) {
    s = s.replace(new RegExp(escapeRegExp(w), "gi"), " ");
  }

  // 去掉开头的动词 + 量词
  s = s.replace(
    /^\s*(?:请|帮我|给我|麻烦|来|要)?\s*(?:生成|做|制作|创建|拍|make|create|generate|give me)?\s*(?:一|两|二|三|四|五|六|七|八|九|十|几)?\s*(?:个|段|条|部|支|张|只|匹|朵)?\s*/u,
    "",
  );

  // 去掉残留的连接词
  s = s.replace(/^(?:关于|有关|的|是)\s*/u, "");
  s = s.replace(/[，,、。.\s的和]+$/u, "");

  return s.trim();
}

function escapeRegExp(s) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
