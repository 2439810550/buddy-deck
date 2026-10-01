/**
 * Agent 循环 + 工具注入的测试。
 *
 * 这里测的是**协议正确性**，不是「模型聪不聪明」——
 * 模型判断该不该调工具是它的事，我们保证的是：
 *
 *   ① 工具定义格式对（name 有前缀、schema 合法）
 *   ② 注入逻辑对（不抢客户端的同名工具、客户端没 tools 也注入）
 *   ③ 循环逻辑对（tool_calls → 执行 → 回填 → 再来一轮 → 直到结束）
 *   ④ appendToolExchange 拼出的对话能被上游接受
 *
 * 上游用一个**假的** chatStream 替身，不真发请求 ——
 * 这样测试快、可重复、不花钱。
 *
 * 跑法：node scripts/test-agent.mjs
 */

import { strict as assert } from "node:assert";
import {
  mediaTools, injectMediaTools, isMediaTool, stripPrefix,
  allToolNames, prefixedName, MEDIA_TOOL_DEFS,
} from "../src/media-tools.js";
import {
  runTool, appendToolExchange, toMediaPayload,
  runAgentNonStreaming, runAgentStreaming,
} from "../src/agent.js";

let pass = 0;
let fail = 0;
const lines = [];

function ok(cond, label) {
  if (cond) { pass += 1; lines.push(`  PASS  ${label}`); }
  else { fail += 1; lines.push(`  FAIL  ${label}`); }
}
function eq(actual, expected, label) {
  const same = JSON.stringify(actual) === JSON.stringify(expected);
  if (same) { pass += 1; lines.push(`  PASS  ${label}`); }
  else {
    fail += 1;
    lines.push(`  FAIL  ${label}\n        期望 ${JSON.stringify(expected)}\n        实际 ${JSON.stringify(actual)}`);
  }
}
function group(t) { lines.push(""); lines.push(`  ── ${t} ──`); }

// ── 假的 Miora 客户端 ────────────────────────────────────────
//
// 记录调用了什么，返回固定结果。测试里不碰网络。

function fakeMiora(options = {}) {
  const calls = [];
  return {
    calls,
    async submit(kind, action, body) {
      calls.push({ kind, action, body });
      if (options.fail) throw new Error("上游炸了");
      return {
        files: [{ url: "https://upstream.example/img.png", fileKey: "bga/abc123" }],
        model: kind === "video" ? "minimax-h3-iOA" : "hy-image-3.5-wb",
      };
    },
    async submitRaw(kind, body) {
      calls.push({ kind, body, raw: true });
      if (options.fail) throw new Error("上游炸了");
      return { upstreamTaskId: "vid-task-001", model: "minimax-h3-iOA", status: "pending" };
    },
    async download(url) {
      calls.push({ download: url });
      return { file: "deadbeef1234.png", path: "/tmp/x.png" };
    },
    async getTempKey() { return "ck_t_fake"; },
  };
}

// 假的 SSE 流
function sseResponse(chunks) {
  const text = chunks.map((c) => `data: ${JSON.stringify(c)}\n`).join("") + "data: [DONE]\n";
  const bytes = new TextEncoder().encode(text);
  let sent = false;
  return {
    body: {
      getReader() {
        return {
          async read() {
            if (sent) return { done: true, value: undefined };
            sent = true;
            return { done: false, value: bytes };
          },
          async cancel() {},
        };
      },
    },
  };
}

function deltaChunk(delta, finish = null) {
  return {
    id: "chatcmpl-x", object: "chat.completion.chunk", created: 1, model: "m",
    choices: [{ index: 0, delta, finish_reason: finish }],
  };
}

function toolCallChunk(name, args, id = "call_1") {
  return deltaChunk({ tool_calls: [{ index: 0, id, type: "function", function: { name, arguments: args } }] });
}

const makeUrl = (file) => `http://127.0.0.1:8787/v1/images/file/${file}?s=sig`;
const silent = () => {};

/**
 * 造一个**形状和真实 sendWithFallback 一致**的 send 返回值。
 *
 * ⚠ 别偷懒直接写 {ok:true, response} —— 踩过这个坑：
 * 真实形状是 { result: {ok, response}, model, attempts }，多一层 result。
 * 测试里写浅了，结果测试全绿而真实环境每个请求都 500。
 * 所以这里强制走这个函数，改契约时只有一处要改。
 */
const sent = (response) => ({ result: { ok: true, response }, model: "m", attempts: [] });
const sendFailed = (kind = "upstream_error", status = 500) =>
  ({ result: { ok: false, kind, status, message: "炸了" }, model: "m", attempts: [kind] });

// ══════════════════════════════════════════════════════════════
group("工具定义");

{
  const tools = mediaTools();
  eq(tools.length, MEDIA_TOOL_DEFS.length, `导出 ${MEDIA_TOOL_DEFS.length} 个工具`);
  ok(tools.every((t) => t.type === "function"), "全部是 function 类型");
  ok(tools.every((t) => t.function.name.startsWith("workbuddy_")), "全部有 workbuddy_ 前缀");
  ok(tools.every((t) => typeof t.function.description === "string" && t.function.description.length > 100),
    "每个都有够长的 description（模型靠它做判断）");
  ok(tools.every((t) => t.function.parameters?.type === "object"), "parameters 都是 object schema");
  ok(tools.every((t) => Array.isArray(t.function.parameters.required)), "都有 required 数组");

  // 工具名不能重复
  const names = tools.map((t) => t.function.name);
  eq(new Set(names).size, names.length, "工具名不重复");
}

{
  // description 必须说清楚「不要做什么」—— 这是减少误调的关键
  const img = mediaTools().find((t) => t.function.name === "workbuddy_generate_image");
  ok(img.function.description.includes("Do NOT"), "生图工具说明了不该用的情况");
  ok(img.function.description.includes("flowchart"), "生图工具排除了流程图");

  const vid = mediaTools().find((t) => t.function.name === "workbuddy_generate_video");
  ok(vid.function.description.includes("MINUTES"), "视频工具提醒了耗时");
  ok(vid.function.description.includes("expensive"), "视频工具提醒了成本");
  ok(vid.function.description.includes("ask the user"), "视频工具要求模糊时先问用户");
}

group("工具名辅助函数");

eq(prefixedName("generate_image"), "workbuddy_generate_image", "prefixedName");
eq(stripPrefix("workbuddy_generate_image"), "generate_image", "stripPrefix");
eq(stripPrefix("Read"), undefined, "stripPrefix 拒绝外部工具");
ok(isMediaTool("workbuddy_x"), "isMediaTool 认自己的");
ok(!isMediaTool("Read"), "isMediaTool 不认外部工具");
ok(!isMediaTool(undefined), "isMediaTool 容忍 undefined");
eq(allToolNames().length, MEDIA_TOOL_DEFS.length, "allToolNames 数量对");

group("注入：客户端没有 tools（Claude Code / pi 的场景）");

{
  const req = { model: "m", messages: [] };
  const r = injectMediaTools(req);
  eq(r.injected.length, MEDIA_TOOL_DEFS.length, "全部注入");
  eq(r.skipped, [], "没有跳过");
  ok(Array.isArray(req.tools) && req.tools.length === MEDIA_TOOL_DEFS.length, "req.tools 被建立");
}

group("注入：客户端有自己的 tools");

{
  const own = { type: "function", function: { name: "Read", description: "读文件", parameters: { type: "object" } } };
  const req = { tools: [own] };
  const r = injectMediaTools(req);
  ok(req.tools.length === MEDIA_TOOL_DEFS.length + 1, "自己的工具被保留");
  eq(req.tools[0], own, "自己的工具还在原位");
  eq(r.skipped, [], "没有同名冲突");
}

group("注入：客户端有同名工具（不能抢）");

{
  const own = {
    type: "function",
    function: { name: "workbuddy_generate_image", description: "客户端自己的实现", parameters: { type: "object" } },
  };
  const req = { tools: [own] };
  const r = injectMediaTools(req);
  eq(r.skipped, ["workbuddy_generate_image"], "同名的被跳过");
  const kept = req.tools.find((t) => t.function.name === "workbuddy_generate_image");
  eq(kept.function.description, "客户端自己的实现", "保留的是客户端的定义，不是我们的");
}

group("注入：开关");

{
  const req = { tools: [] };
  const r = injectMediaTools(req, { enabled: false });
  eq(r.injected, [], "关掉时一个都不注入");
  eq(req.tools, [], "req.tools 保持原样");
}

group("注入：tools 不是数组（脏数据）");

{
  const req = { tools: "垃圾" };
  const r = injectMediaTools(req);
  eq(r.injected.length, MEDIA_TOOL_DEFS.length, "照样注入");
  ok(Array.isArray(req.tools), "req.tools 被修正成数组");
}

group("工具执行：generate_image");

{
  const miora = fakeMiora();
  const r = await runTool({
    id: "c1", type: "function",
    function: { name: "workbuddy_generate_image", arguments: '{"prompt":"一只猫"}' },
  }, { miora, makeUrl, log: silent });

  ok(r.ok, "执行成功");
  eq(miora.calls[0].kind, "image", "调的是 image");
  eq(miora.calls[0].action, "generate", "action=generate");
  eq(miora.calls[0].body, { prompt: "一只猫" }, "body 正确");
  ok(r.text.includes("![生成结果](http://127.0.0.1:8787/v1/images/file/deadbeef1234.png?s=sig)"),
    "返回里带完整 markdown 图片");
  ok(r.text.includes("fileKey: bga/abc123"), "回传 fileKey 供链式调用");
}

{
  const miora = fakeMiora();
  await runTool({
    id: "c1", function: { name: "workbuddy_generate_image", arguments: '{"prompt":"猫","size":"1024x1536"}' },
  }, { miora, makeUrl, log: silent });
  eq(miora.calls[0].body, { prompt: "猫", size: "1024x1536" }, "size 被透传");
}

group("工具执行：参数错误");

{
  const miora = fakeMiora();
  const r = await runTool({
    id: "c1", function: { name: "workbuddy_generate_image", arguments: '{}' },
  }, { miora, makeUrl, log: silent });
  ok(!r.ok, "缺 prompt → 失败");
  eq(miora.calls.length, 0, "没去调上游");
}

{
  const miora = fakeMiora();
  const r = await runTool({
    id: "c1", function: { name: "workbuddy_generate_image", arguments: "不是json" },
  }, { miora, makeUrl, log: silent });
  ok(!r.ok, "参数不是 JSON → 失败");
  ok(r.text.includes("JSON"), "错误信息说清楚原因");
}

{
  const miora = fakeMiora();
  const r = await runTool({
    id: "c1", function: { name: "Read", arguments: "{}" },
  }, { miora, makeUrl, log: silent });
  ok(!r.ok, "外部工具被拒绝（不该由我们执行）");
}

group("工具执行：上游失败");

{
  const miora = fakeMiora({ fail: true });
  const r = await runTool({
    id: "c1", function: { name: "workbuddy_generate_image", arguments: '{"prompt":"猫"}' },
  }, { miora, makeUrl, log: silent });
  ok(!r.ok, "上游抛异常 → 失败");
  ok(r.text.includes("工具执行失败"), "错误信息可读，不会把异常直接扔给用户");
}

group("工具执行：generate_video（提交就走）");

{
  const miora = fakeMiora();
  const r = await runTool({
    id: "c1", function: { name: "workbuddy_generate_video", arguments: '{"prompt":"猫跳舞","duration":5}' },
  }, { miora, makeUrl, log: silent, trackVideoTask: () => {} });

  ok(r.ok, "执行成功");
  eq(miora.calls[0].raw, true, "用的是 submitRaw（不等结果）");
  eq(miora.calls[0].body, { prompt: "猫跳舞", duration: 5 }, "body 正确");
  ok(r.text.includes("vid-task-001"), "返回任务 id");
  ok(r.text.includes("/v1/video/status/vid-task-001"), "返回查询地址");
  ok(r.text.includes("不要") && r.text.includes("轮询"), "明确告诉模型不要等");
}

group("工具执行：image_to_video");

{
  const miora = fakeMiora();
  const r = await runTool({
    id: "c1",
    function: {
      name: "workbuddy_image_to_video",
      arguments: '{"first_frame":"https://example.com/a.jpg","last_frame":"bga/xyz","prompt":"动起来"}',
    },
  }, { miora, makeUrl, log: silent, trackVideoTask: () => {} });

  ok(r.ok, "执行成功");
  eq(miora.calls[0].body.input.first_frame_image, { url: "https://example.com/a.jpg" }, "首帧转 url payload");
  eq(miora.calls[0].body.input.last_frame_image, { file_key: "bga/xyz" }, "尾帧转 file_key payload");
}

group("media payload 转换");

eq(toMediaPayload("https://x.com/a.png"), { url: "https://x.com/a.png" }, "https → url");
eq(toMediaPayload("data:image/png;base64,AAAA"), { base64: "AAAA" }, "data URI → base64");
eq(toMediaPayload("bga/abc"), { file_key: "bga/abc" }, "bga/ → file_key");
eq(toMediaPayload("file/abc"), { file_key: "file/abc" }, "file/ → file_key");
eq(toMediaPayload("aGVsbG8="), { base64: "aGVsbG8=" }, "裸 base64 → base64");

group("对话回填：appendToolExchange");

{
  const messages = [];
  const calls = [
    { id: "c1", type: "function", function: { name: "workbuddy_generate_image", arguments: '{"prompt":"猫"}' } },
  ];
  appendToolExchange(messages, calls, [{ ok: true, text: "图片已生成" }]);

  eq(messages.length, 2, "追加两条消息");
  eq(messages[0].role, "assistant", "第一条是 assistant");
  eq(messages[0].content, null, "assistant 的 content 是 null");
  eq(messages[0].tool_calls.length, 1, "带上了 tool_calls");
  eq(messages[1].role, "tool", "第二条是 tool");
  eq(messages[1].tool_call_id, "c1", "tool_call_id 对得上");
  eq(messages[1].content, "图片已生成", "内容传过去了");
}

{
  // 多个工具一起调 —— assistant 消息必须带全部
  const messages = [];
  const calls = [
    { id: "c1", function: { name: "workbuddy_generate_image", arguments: "{}" } },
    { id: "c2", function: { name: "workbuddy_generate_video", arguments: "{}" } },
  ];
  appendToolExchange(messages, calls, [{ ok: true, text: "A" }, { ok: true, text: "B" }]);
  eq(messages.length, 3, "1 个 assistant + 2 个 tool");
  eq(messages[0].tool_calls.length, 2, "assistant 带了全部 2 个调用");
  eq(messages[1].tool_call_id, "c1", "第一个 tool 消息");
  eq(messages[2].tool_call_id, "c2", "第二个 tool 消息");
}

{
  // 工具失败也要回填 —— 不回填上游会报错
  const messages = [];
  appendToolExchange(messages, [{ id: "c1", function: { name: "x", arguments: "{}" } }], [{ ok: false, text: "炸了" }]);
  eq(messages[1].content, "炸了", "失败信息也回填");
}

group("循环（非流式）：模型不调工具");

{
  const miora = fakeMiora();
  const responses = [
    sseResponse([deltaChunk({ role: "assistant" }), deltaChunk({ content: "你好" }), deltaChunk({}, "stop")]),
  ];
  let n = 0;
  const r = await runAgentNonStreaming({
    request: { messages: [] }, miora, makeUrl, log: silent,
    send: async () => sent(responses[n++]),
  });
  eq(r.rounds, 0, "没跑工具轮次");
  eq(r.completion.choices[0].message.content, "你好", "拿到正文");
  eq(n, 1, "只发了一次请求");
}

group("循环（非流式）：调一次工具然后回答");

{
  const miora = fakeMiora();
  const responses = [
    // 第 1 轮：要调工具
    sseResponse([
      deltaChunk({ role: "assistant" }),
      toolCallChunk("workbuddy_generate_image", '{"prompt":"一只猫"}'),
      deltaChunk({}, "tool_calls"),
    ]),
    // 第 2 轮：给最终答案
    sseResponse([
      deltaChunk({ role: "assistant" }),
      deltaChunk({ content: "画好了：![生成结果](url)" }),
      deltaChunk({}, "stop"),
    ]),
  ];
  let n = 0;
  const sentBodies = [];
  const r = await runAgentNonStreaming({
    request: { messages: [{ role: "user", content: "画一只猫" }] },
    miora, makeUrl, log: silent,
    send: async (req) => {
      sentBodies.push(JSON.parse(JSON.stringify(req.messages)));
      return sent(responses[n++]);
    },
  });

  eq(r.rounds, 1, "跑了 1 轮工具");
  eq(n, 2, "发了 2 次请求");
  eq(miora.calls.filter((c) => c.kind === "image").length, 1, "真调了一次生图");
  eq(r.completion.choices[0].message.content, "画好了：![生成结果](url)", "最终答案是第 2 轮的");

  // 第二次请求的 messages 里应该多了 assistant(tool_calls) + tool
  const second = sentBodies[1];
  eq(second.length, 3, "第 2 次请求带了 3 条消息");
  eq(second[1].role, "assistant", "第 2 条是 assistant");
  eq(second[2].role, "tool", "第 3 条是 tool");
  ok(second[2].content.includes("deadbeef1234.png"), "工具结果里带图片地址");
}

group("循环（非流式）：上游报错");

{
  const miora = fakeMiora();
  const r = await runAgentNonStreaming({
    request: { messages: [] }, miora, makeUrl, log: silent,
    send: async () => sendFailed(),
  });
  ok(r.error !== undefined, "错误被传出来");
  eq(r.rounds, 0, "没跑工具");
}

group("循环（非流式）：超过轮数上限");

{
  const miora = fakeMiora();
  // 永远回 tool_calls —— 模拟模型卡在循环里
  const forever = () => sseResponse([
    deltaChunk({ role: "assistant" }),
    toolCallChunk("workbuddy_generate_image", '{"prompt":"猫"}'),
    deltaChunk({}, "tool_calls"),
  ]);
  const r = await runAgentNonStreaming({
    request: { messages: [] }, miora, makeUrl, log: silent, maxRounds: 3,
    send: async () => sent(forever()),
  });
  eq(r.rounds, 4, "跑满 3 轮后停（rounds 记到 4 是因为超限那轮也算）");
  ok(r.overRounds === true, "标记了 overRounds");
  eq(miora.calls.filter((c) => c.kind === "image").length, 3, "工具最多执行 maxRounds 次");
}

group("循环（流式）：纯说话");

{
  const miora = fakeMiora();
  const emitted = [];
  let n = 0;
  const r = await runAgentStreaming({
    request: { messages: [] }, miora, makeUrl, log: silent,
    emit: (c) => emitted.push(c),
    send: async () => sent(sseResponse([deltaChunk({ role: "assistant" }), deltaChunk({ content: "你好呀" }), deltaChunk({}, "stop")])),
  });

  ok(r.emitted, "有输出");
  eq(r.rounds, 0, "没跑工具");
  const text = emitted.map((c) => c.choices?.[0]?.delta?.content ?? "").join("");
  eq(text, "你好呀", "正文完整转发");
}

group("循环（流式）：调工具后回答（正文不能漏出去）");

{
  const miora = fakeMiora();
  const emitted = [];
  const responses = [
    // 第 1 轮：模型先说「好的我来画」然后调工具 —— 那半句不能给客户端
    sseResponse([
      deltaChunk({ role: "assistant" }),
      deltaChunk({ content: "好的我来画" }),
      toolCallChunk("workbuddy_generate_image", '{"prompt":"猫"}'),
      deltaChunk({}, "tool_calls"),
    ]),
    // 第 2 轮：最终答案
    sseResponse([
      deltaChunk({ role: "assistant" }),
      deltaChunk({ content: "画好了" }),
      deltaChunk({}, "stop"),
    ]),
  ];
  let n = 0;
  let keepalives = 0;

  const r = await runAgentStreaming({
    request: { messages: [] }, miora, makeUrl, log: silent,
    emit: (c) => emitted.push(c),
    keepalive: () => { keepalives += 1; },
    send: async () => sent(responses[n++]),
  });

  eq(r.rounds, 1, "跑了 1 轮工具");
  eq(n, 2, "发了 2 次请求");
  const text = emitted.map((c) => c.choices?.[0]?.delta?.content ?? "").join("");
  eq(text, "画好了", "只转发了最终答案");
  ok(!text.includes("好的我来画"), "第 1 轮那句被丢弃了（关键）");
}

group("循环（流式）：上游报错");

{
  const miora = fakeMiora();
  const emitted = [];
  const r = await runAgentStreaming({
    request: { messages: [] }, miora, makeUrl, log: silent,
    emit: (c) => emitted.push(c),
    send: async () => sendFailed(),
  });
  ok(r.error !== undefined, "错误传出来");
  eq(emitted.length, 0, "什么都没往外写");
}

group("循环（流式）：工具失败后模型仍能回答");

{
  const miora = fakeMiora({ fail: true });
  const emitted = [];
  const responses = [
    sseResponse([
      deltaChunk({ role: "assistant" }),
      toolCallChunk("workbuddy_generate_image", '{"prompt":"猫"}'),
      deltaChunk({}, "tool_calls"),
    ]),
    sseResponse([
      deltaChunk({ role: "assistant" }),
      deltaChunk({ content: "抱歉，生图失败了" }),
      deltaChunk({}, "stop"),
    ]),
  ];
  let n = 0;
  const r = await runAgentStreaming({
    request: { messages: [] }, miora, makeUrl, log: silent,
    emit: (c) => emitted.push(c),
    send: async () => sent(responses[n++]),
  });

  eq(r.toolCalls, ["generate_image:fail"], "工具失败被记录");
  const text = emitted.map((c) => c.choices?.[0]?.delta?.content ?? "").join("");
  eq(text, "抱歉，生图失败了", "模型据实回答（不是静默失败）");
}

group("流式：空 delta 不该触发「在说话」");

{
  const miora = fakeMiora();
  const emitted = [];
  let n = 0;
  await runAgentStreaming({
    request: { messages: [] }, miora, makeUrl, log: silent,
    emit: (c) => emitted.push(c),
    send: async () => sent(
      n++ === 0
        // role 分片 + reasoning 不算说话
        ? sseResponse([
            deltaChunk({ role: "assistant" }),
            deltaChunk({ reasoning_content: "让我想想…" }),
            toolCallChunk("workbuddy_generate_image", '{"prompt":"猫"}'),
            deltaChunk({}, "tool_calls"),
          ])
        : sseResponse([deltaChunk({ content: "好了" }), deltaChunk({}, "stop")]),
    ),
  });
  const text = emitted.map((c) => c.choices?.[0]?.delta?.content ?? "").join("");
  eq(text, "好了", "reasoning 分片没被当成正文放出去");
}

// ══════════════════════════════════════════════════════════════
group("客户端的工具必须原样交回（不能替他执行）");

{
  // 这是真实踩过的 bug：非流式路径一开始看到任何 tool_calls 都去执行，
  // 客户端调 get_weather 结果模型收到「未知工具」，回了一句
  // 「天气工具好像不可用」——用户看到一切正常，只是永远查不出数据。
  const miora = fakeMiora();
  const responses = [
    sseResponse([
      deltaChunk({ role: "assistant" }),
      deltaChunk({ tool_calls: [{ index: 0, id: "call_w", type: "function", function: { name: "get_weather", arguments: '{"city":"北京"}' } }] }),
      deltaChunk({}, "tool_calls"),
    ]),
  ];
  let n = 0;
  const r = await runAgentNonStreaming({
    request: { messages: [] }, miora, makeUrl, log: silent,
    send: async () => sent(responses[n++]),
  });

  eq(r.rounds, 0, "没跑工具轮次");
  eq(n, 1, "只发了一次请求（不介入）");
  eq(miora.calls.length, 0, "没去碰 Miora");
  ok(r.passthrough === true, "标记了 passthrough");
  const calls = r.completion.choices[0].message.tool_calls;
  eq(calls.length, 1, "tool_calls 原样返回给客户端");
  eq(calls[0].function.name, "get_weather", "名字没被改");
  eq(r.completion.choices[0].finish_reason, "tool_calls", "finish_reason 也对");
}

group("混合调用（我们的 + 客户端的）");

{
  const miora = fakeMiora();
  const responses = [
    sseResponse([
      deltaChunk({ role: "assistant" }),
      toolCallChunk("workbuddy_generate_image", '{"prompt":"猫"}', "call_ours"),
      deltaChunk({ tool_calls: [{ index: 1, id: "call_theirs", type: "function", function: { name: "Read", arguments: "{}" } }] }),
      deltaChunk({}, "tool_calls"),
    ]),
    sseResponse([deltaChunk({ content: "好了" }), deltaChunk({}, "stop")]),
  ];
  let n = 0;
  const sentMessages = [];
  const r = await runAgentNonStreaming({
    request: { messages: [] }, miora, makeUrl, log: silent,
    send: async (req) => { sentMessages.push(JSON.parse(JSON.stringify(req.messages))); return sent(responses[n++]); },
  });

  eq(r.rounds, 1, "跑了一轮");
  eq(miora.calls.filter((c) => c.kind === "image").length, 1, "我们的工具被执行了");

  // 回填的 assistant 消息必须带全部两个调用，否则上游报错
  const second = sentMessages[1];
  eq(second[0].tool_calls.length, 2, "回填带上了全部 2 个调用");
  eq(second.length, 3, "1 个 assistant + 2 个 tool");
  ok(second[2].content.includes("客户端执行"), "外部工具给了一条说明性回复");
}

group("流式：客户端的工具原样流出去");

{
  const miora = fakeMiora();
  const emitted = [];
  const r = await runAgentStreaming({
    request: { messages: [] }, miora, makeUrl, log: silent,
    emit: (c) => emitted.push(c),
    send: async () => sent(sseResponse([
      deltaChunk({ role: "assistant" }),
      deltaChunk({ content: "我查一下" }),
      deltaChunk({ tool_calls: [{ index: 0, id: "call_w", type: "function", function: { name: "get_weather", arguments: '{"city":"东京"}' } }] }),
      deltaChunk({}, "tool_calls"),
    ])),
  });

  eq(r.rounds, 0, "不介入");
  eq(miora.calls.length, 0, "没碰 Miora");
  ok(r.passthrough === true, "标记了 passthrough");

  const text = emitted.map((c) => c.choices?.[0]?.delta?.content ?? "").join("");
  eq(text, "我查一下", "正文放出去了");
  const toolDeltas = emitted.flatMap((c) => c.choices?.[0]?.delta?.tool_calls ?? []);
  eq(toolDeltas.length, 1, "tool_calls 也放出去了（客户端要自己执行）");
  eq(toolDeltas[0].function.name, "get_weather", "工具名完整");
}

console.log(lines.join("\n"));
console.log("");
console.log(`  ${fail === 0 ? "ALL GREEN" : "FAILED"} — ${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
