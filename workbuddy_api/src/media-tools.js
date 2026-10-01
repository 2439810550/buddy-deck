/**
 * 把生图 / 改图 / 生视频注册成**工具**，让模型自己决定调哪个。
 *
 * ## 为什么不用关键词匹配
 *
 * 第一版我写的是关键词：命中「画」「生成」就去生图。它有两个死穴：
 *
 *   ① **死板**。「生成一个视频」命中「生成」→ 当成生图处理，
 *      用户说生成视频，拿到一张图。补词表能修一个case，修不了面。
 *
 *   ② **一接外挂客户端就废**。那段代码前面有个
 *      `!hasToolUse(request)` —— 一看客户端带了 tools 就跳过关键词。
 *      于是 Claude Code / pi / DSH 里说「画一只猫」，模型只会回
 *      「我没办法直接画图哦」。
 *
 * ## WorkBuddy 自己的做法
 *
 * 挖 `app.asar` 里那个 miora-mcp 插件，发现它**根本不认关键词**：
 * 它把工具通过 MCP 协议注册给模型，
 *
 *     miora_text_to_image      description: "Generate an image ... "
 *     miora_edit_image         description: "Edit an existing image ..."
 *     miora_text_to_video      description: "Generate a video from a prompt ..."
 *     miora_frame_to_video
 *     miora_reference_to_video
 *
 * 模型读这些 description，**自己判断**该不该调、调哪个。
 * 这就是 function calling 的正确用法 —— 判断权在模型，不在正则。
 *
 * ## 这里的做法
 *
 * 网关把工具**注入**到客户端请求里（客户端没自带同名工具时），
 * 上游回 `tool_calls` 时网关自己执行（去调 Miora），把结果作为
 * tool 消息回填，再让模型接着说。对客户端完全透明。
 *
 * 为什么必须网关自己执行：只有网关拿着 Miora 的凭证。
 * Claude Code 不认识这个工具，让它去执行等于没做。
 *
 * ## description 怎么写
 *
 * 直接照着 app.asar 里的原文改（那是官方调好的措辞），加上
 * 「什么时候该用、什么时候不该用」的边界说明。
 *
 * @module workbuddy-api/media-tools
 */

/** 工具名前缀。加前缀避免和客户端的同名工具撞（比如某些客户端有 "image"）。 */
export const TOOL_PREFIX = "workbuddy_";

/**
 * 工具定义表。每个条目：
 *   name        工具名（会加前缀）
 *   description 给模型看的说明 —— **模型就是靠这段文字做判断的**
 *   parameters  JSON Schema
 *   handle      真正干活（在 agent.js 里注入，这里只放定义）
 */
export const MEDIA_TOOL_DEFS = [
  {
    name: "generate_image",
    description: [
      "Generate an image from a text description and show it to the user.",
      "",
      "Use this when the user asks you to CREATE, DRAW, PAINT, or GENERATE a picture/image/illustration/photo.",
      "Examples of when to call it:",
      '  - "画一只戴墨镜的橘猫" (draw an orange cat with sunglasses)',
      '  - "generate an image of a cyberpunk city"',
      '  - "帮我做个 logo 草图" (make a logo sketch for me)',
      "",
      "Do NOT call it when the user is:",
      "  - asking HOW to draw something, or discussing image generation in general",
      "  - asking about the tool itself: how long it takes, what it costs, what sizes are supported,",
      "    how many you can make, whether it is available. Those are QUESTIONS — answer them in words.",
      "  - saying something vague like \"让我看看猫长什么样\" / \"show me what a cat looks like\" —",
      "    that is a request for information, not a request to create an image. Ask what they want first.",
      "  - asking for a flowchart / architecture diagram / UML / mermaid (write the code instead)",
      "  - asking you to WRITE a prompt for another tool (just write the prompt)",
      "",
      "Write the prompt in the same language the user used, describing what should be IN the picture.",
      "Do not include instruction words like \"画\" or \"generate\" in the prompt itself — describe the subject.",
    ].join("\n"),
    parameters: {
      type: "object",
      properties: {
        prompt: {
          type: "string",
          description: "What the image should show. Describe the subject, style, and composition. Do not include the words \"draw\" or \"generate\".",
        },
        size: {
          type: "string",
          description: 'Optional pixel size such as "1024x1024", "1024x1536", or "1536x1024". Omit to let the service decide.',
        },
      },
      required: ["prompt"],
    },
  },
  {
    name: "edit_image",
    description: [
      "Edit an existing image according to a text instruction.",
      "",
      "Use this when the user wants to MODIFY an image they provided or that was generated earlier —",
      "change colors, remove/add an element, restyle it, and so on.",
      "The image can be given as a public https URL, a data: URI, or a fileKey returned by a previous",
      "image tool call (prefer fileKey — it avoids a base64 round-trip).",
      "",
      "Do NOT use this to create a brand-new image from scratch — use the text-to-image tool for that.",
    ].join("\n"),
    parameters: {
      type: "object",
      properties: {
        prompt: { type: "string", description: "The edit instruction, e.g. \"make the background a sunset\"." },
        image: { type: "string", description: "The source image: https URL | data:image/...;base64,... | fileKey from a previous tool result." },
        size: { type: "string", description: "Optional output size." },
      },
      required: ["prompt", "image"],
    },
  },
  {
    name: "generate_video",
    description: [
      "Generate a short video from a text description.",
      "",
      "Use this when the user asks for a VIDEO, CLIP, ANIMATION, or MOVIE.",
      "Examples:",
      '  - "生成一个视频" (generate a video)',
      '  - "一只猫在跳舞的短片" (a short clip of a cat dancing)',
      '  - "make an animation of waves at sunset"',
      "",
      "IMPORTANT — video generation takes MINUTES and is much more expensive than an image.",
      "Only call it when the user clearly asked for video. If the request is ambiguous between",
      "a still image and a video, ask the user which they want instead of guessing.",
      "",
      "Do NOT call it when the user is asking ABOUT video generation rather than asking FOR a video:",
      "  - \"视频生成要多久\" / \"how long does video take\"  → answer the question in words",
      "  - \"视频生成贵吗\" / \"is video expensive\"          → answer the question in words",
      "  - \"支持生成视频吗\" / \"can you make videos\"       → say yes, then ask what they want",
      "",
      "This tool returns a task id. The user can poll for the result; do not wait for it in a loop.",
    ].join("\n"),
    parameters: {
      type: "object",
      properties: {
        prompt: { type: "string", description: "What the video should show. Describe the subject and the motion." },
        duration: { type: "number", description: "Optional target duration in seconds." },
        resolution: { type: "string", description: 'Optional resolution such as "720p" or "1080p".' },
        aspect_ratio: { type: "string", description: 'Optional aspect ratio such as "16:9" or "9:16".' },
      },
      required: ["prompt"],
    },
  },
  {
    name: "image_to_video",
    description: [
      "Generate a video that animates a given still image. Optionally specify an ending frame and the",
      "video will interpolate between the two.",
      "",
      "Use this when the user has an image (provided or generated earlier) and wants it to MOVE or",
      'become a video — e.g. "让这张图动起来" (make this image move), "把首尾帧做成视频".',
      "",
      "The image may be a public https URL, a data: URI, or a fileKey from a previous tool result.",
    ].join("\n"),
    parameters: {
      type: "object",
      properties: {
        first_frame: { type: "string", description: "Starting frame: https URL | data:image/...;base64,... | fileKey." },
        last_frame: { type: "string", description: "Optional ending frame. Same formats as first_frame." },
        prompt: { type: "string", description: "Optional description of the motion between the frames." },
        duration: { type: "number", description: "Optional target duration in seconds." },
        resolution: { type: "string", description: 'Optional resolution such as "720p".' },
        aspect_ratio: { type: "string", description: "Optional aspect ratio." },
      },
      required: ["first_frame"],
    },
  },
];

/** 加了前缀的工具名。 */
export function prefixedName(name) {
  return `${TOOL_PREFIX}${name}`;
}

/** 全部工具名（加了前缀）。 */
export function allToolNames() {
  return MEDIA_TOOL_DEFS.map((d) => prefixedName(d.name));
}

/**
 * 转成 OpenAI 的 `tools` 数组格式。
 *
 * @param {object} [options]
 * @param {string[]} [options.only] 只要这些（按**未加前缀**的名）—— 用于测试和裁剪
 */
export function mediaTools(options = {}) {
  const only = options.only;
  return MEDIA_TOOL_DEFS
    .filter((d) => only === undefined || only.includes(d.name))
    .map((d) => ({
      type: "function",
      function: {
        name: prefixedName(d.name),
        description: d.description,
        parameters: d.parameters,
      },
    }));
}

/**
 * 把原生工具定义**合并**进客户端的 tools 数组。
 *
 * 两条规则：
 *
 *   ① 客户端已经有同名工具 → 不动。尊重客户端的定义
 *      （万一是它自己的实现，抢过来执行反而是坏的）
 *   ② 客户端一个 tools 都没有 → 也要注入。这正是 Claude Code / pi 的场景
 *
 * @param {object} request 客户端请求（会被改动 —— 调用方传副本）
 * @param {object} [options]
 * @param {boolean} [options.enabled] 总开关
 * @returns {{injected: string[], skipped: string[]}} 注入和跳过的工具名（好记日志）
 */
export function injectMediaTools(request, options = {}) {
  if (options.enabled === false) return { injected: [], skipped: [] };

  const ours = mediaTools(options);
  const existing = Array.isArray(request["tools"]) ? request["tools"] : [];

  // 客户端已声明的函数名
  const declared = new Set();
  for (const t of existing) {
    const name = t?.function?.name ?? t?.name;
    if (typeof name === "string" && name !== "") declared.add(name);
  }

  const injected = [];
  const skipped = [];
  const additions = [];

  for (const tool of ours) {
    const name = tool.function.name;
    if (declared.has(name)) {
      skipped.push(name);
      continue;
    }
    additions.push(tool);
    injected.push(name);
  }

  if (additions.length > 0) {
    request["tools"] = [...existing, ...additions];
  }

  return { injected, skipped };
}

/** 这个工具名是不是我们的。 */
export function isMediaTool(name) {
  return typeof name === "string" && name.startsWith(TOOL_PREFIX);
}

/** 去掉前缀，拿到原始名。不是我们的工具就返回 undefined。 */
export function stripPrefix(name) {
  if (!isMediaTool(name)) return undefined;
  return name.slice(TOOL_PREFIX.length);
}
