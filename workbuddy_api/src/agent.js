/**
 * Agent 循环：让模型自己调生图 / 生视频工具，网关负责执行。
 *
 * ## 为什么需要这个循环
 *
 * 客户端（Claude Code / pi / DSH）发来一个请求。我们把生图工具注入进去，
 * 模型看到工具就可能会回：
 *
 *     finish_reason: "tool_calls"
 *     tool_calls: [{ function: { name: "workbuddy_generate_image",
 *                                arguments: '{"prompt":"..."}' } }]
 *
 * 正常情况（客户端自己的工具）这时候该把 tool_calls 交给客户端，
 * 客户端本地执行（跑 Bash、读文件）再把结果发回来。
 *
 * **但生图工具必须网关执行** —— 只有网关拿着 Miora 的凭证。
 * 而且 Claude Code 根本不认识 `workbuddy_generate_image`，交给它也没用。
 *
 * 所以网关自己把循环跑掉：
 *
 *     ① 把请求发给上游
 *     ② 上游说要调工具
 *     ③ **网关执行**（去调 Miora），拿到结果
 *     ④ 把结果作为 role:"tool" 消息追加进对话
 *     ⑤ 带着新对话再发一次给上游
 *     ⑥ 重复到模型不再要调工具，或者超过轮数上限
 *     ⑦ 把最终答案给客户端
 *
 * 对客户端来说这就是一次普通的聊天请求。
 *
 * ## 流式的关键设计：缓冲窗口
 *
 * 难点：模型可能先说「好的我来画」**然后**才发 tool_calls。等看到
 * tool_calls 时，那半句已经写给客户端了 —— 而客户端已经把前半句
 * 渲染出来了，后面的工具结果插不进去。
 *
 * 处理办法：**先攒着，不急着往外写**。
 *
 *   · 正文攒够 `FLUSH_THRESHOLD` 个字符，或者流结束 → 确定是说话，
 *     把攒下的一次性放出去，之后实时转发
 *   · 这期间出现**我们的**工具的 tool_calls → 整轮丢掉，
 *     闷头执行，然后开下一轮（客户端只看到最终答案）
 *
 * 为什么用「攒够 N 个字符」而不是「看到第一个字符就放行」：
 *
 *   模型的前言通常很短（「好的，我来画」「稍等」）—— 正好落在阈值内，
 *   会被完整丢弃。而真正在聊天时，正文很快超过阈值，流式体验不受影响。
 *
 *   实测踩过：第一版看到第一个正文分片就转发，结果「好的我来画」
 *   漏了出去，测试直接抓到。
 *
 * 阈值不能太小（前言漏出去）也不能太大（正常聊天首字节延迟明显）。
 * 40 字符是个折中 —— 比绝大多数前言长，又不到一行的长度。
 *
 * ### 混合调用的处理（已知限制）
 *
 * 如果模型在同一轮里**既调我们的工具、又调客户端的工具**（比如
 * 同时调 `workbuddy_generate_image` 和 `Bash`），我们只能认一边：
 * 先出现的那个决定这轮怎么走。因为网关没法替客户端执行 Bash，
 * 而客户端不认识我们的工具。实际很少见，遇到了会记日志。
 *
 * ## 工具执行期间要保活
 *
 * 生图要 10~30 秒，这段时间一个字节都不发。有些客户端有 30 秒的
 * 空闲超时，会以为连接死了。所以执行工具期间每 10 秒发一个
 * SSE 注释行（`: keepalive`）—— 按 SSE 规范，客户端会忽略它。
 *
 * @module workbuddy-api/agent
 */

import { collectChatCompletion, iterateSseData } from "./upstream.js";
import { isMediaTool, stripPrefix } from "./media-tools.js";

/** 最多循环几轮。防模型陷入「一直调工具」的死循环。 */
export const DEFAULT_MAX_ROUNDS = 5;

/** 执行工具期间的保活间隔。 */
const KEEPALIVE_MS = 10_000;

/**
 * 缓冲窗口：攒够这么多字符就确定「这轮是在说话」，放行。
 *
 * 见文件头的说明 —— 太小会漏前言，太大会拖慢正常聊天的首字节。
 */
const FLUSH_THRESHOLD = 40;

/**
 * 把 URL / data URI / fileKey 转成 Miora 要的 media payload。
 *
 * 和 gateway.js 里的 resolveMedia 同构，但那个是闭包内的，
 * 这里独立一份避免循环依赖。
 */
export function toMediaPayload(input) {
  const s = String(input ?? "").trim();
  if (s.startsWith("http://") || s.startsWith("https://")) return { url: s };
  if (s.startsWith("data:")) {
    const comma = s.indexOf(",");
    return comma < 0 ? { base64: s } : { base64: s.slice(comma + 1) };
  }
  // fileKey（bga/... 或 file/...）
  if (s.startsWith("bga/") || s.startsWith("file/")) return { file_key: s };
  return { base64: s };
}

/**
 * 执行一个工具调用。
 *
 * @param {object} call OpenAI 形状的 tool_call
 * @returns {Promise<{ok: boolean, text: string}>}
 */
export async function runTool(call, ctx) {
  const rawName = call?.function?.name ?? "";
  const bare = stripPrefix(rawName);
  if (bare === undefined) {
    return { ok: false, text: `未知工具：${rawName}` };
  }

  let args;
  try {
    args = JSON.parse(call.function.arguments || "{}");
  } catch {
    return { ok: false, text: `工具参数不是合法 JSON：${String(call.function.arguments).slice(0, 200)}` };
  }

  const { miora, makeUrl, log } = ctx;

  try {
    switch (bare) {
      case "generate_image": {
        const prompt = String(args.prompt ?? "").trim();
        if (prompt === "") return { ok: false, text: "缺少 prompt 参数" };

        const body = { prompt };
        if (typeof args.size === "string" && args.size !== "") body.size = args.size;

        const result = await miora.submit("image", "generate", body);
        if (result.files.length === 0) {
          return { ok: false, text: result.errorMessage ?? "生图失败，上游没有返回图片" };
        }
        const media = await miora.download(result.files[0].url);
        const url = makeUrl(media.file);
        log(`[agent] 生图完成 ${media.file}（模型 ${result.model}）`);
        return { ok: true, text: imageResultText("图片已生成", url, result.files[0].fileKey) };
      }

      case "edit_image": {
        const prompt = String(args.prompt ?? "").trim();
        const image = String(args.image ?? "").trim();
        if (prompt === "") return { ok: false, text: "缺少 prompt 参数" };
        if (image === "") return { ok: false, text: "缺少 image 参数" };

        const body = { prompt, images: [toMediaPayload(image)] };
        if (typeof args.size === "string" && args.size !== "") body.size = args.size;

        const result = await miora.submit("image", "edit", body);
        if (result.files.length === 0) {
          return { ok: false, text: result.errorMessage ?? "改图失败，上游没有返回图片" };
        }
        const media = await miora.download(result.files[0].url);
        const url = makeUrl(media.file);
        log(`[agent] 改图完成 ${media.file}`);
        return { ok: true, text: imageResultText("图片已修改", url, result.files[0].fileKey) };
      }

      case "generate_video": {
        const prompt = String(args.prompt ?? "").trim();
        const body = {};
        if (prompt !== "") body.prompt = prompt;
        if (typeof args.duration === "number" && Number.isFinite(args.duration)) body.duration = args.duration;
        if (typeof args.resolution === "string" && args.resolution !== "") body.resolution = args.resolution;
        if (typeof args.aspect_ratio === "string" && args.aspect_ratio !== "") body.aspect_ratio = args.aspect_ratio;

        return await submitVideo(ctx, body, "文生视频");
      }

      case "image_to_video": {
        const first = String(args.first_frame ?? "").trim();
        if (first === "") return { ok: false, text: "缺少 first_frame 参数" };

        const input = { first_frame_image: toMediaPayload(first) };
        const last = String(args.last_frame ?? "").trim();
        if (last !== "") input.last_frame_image = toMediaPayload(last);

        const body = { input };
        if (typeof args.prompt === "string" && args.prompt.trim() !== "") body.prompt = args.prompt.trim();
        if (typeof args.duration === "number" && Number.isFinite(args.duration)) body.duration = args.duration;
        if (typeof args.resolution === "string" && args.resolution !== "") body.resolution = args.resolution;
        if (typeof args.aspect_ratio === "string" && args.aspect_ratio !== "") body.aspect_ratio = args.aspect_ratio;

        return await submitVideo(ctx, body, last === "" ? "图生视频" : "首尾帧视频");
      }

      default:
        return { ok: false, text: `工具 ${bare} 还没实现` };
    }
  } catch (error) {
    const message = error?.message ?? String(error);
    log(`[agent] 工具 ${bare} 执行失败：${message}`);
    return { ok: false, text: `工具执行失败：${message}` };
  }
}

/**
 * 工具返回给模型的文字。
 *
 * 为什么要明确让模型「把 markdown 原样放进回复」：模型拿到工具结果后
 * 常常会自己改写一通，把地址弄丢或者加一堆说明。说清楚要它干什么
 * 比让它自由发挥可靠得多。
 */
function imageResultText(title, url, fileKey) {
  const lines = [
    `${title}。`,
    "",
    `![生成结果](${url})`,
    "",
    "把上面那行 markdown 图片原样放进你的回复里，用户就能看到图片。",
    "不要改写这个地址，也不要额外解释技术细节。",
  ];
  if (typeof fileKey === "string" && fileKey !== "") lines.push(`（fileKey: ${fileKey}）`);
  return lines.join("\n");
}

/** 视频都是「提交就走」—— 渲染要几分钟，等下去客户端早超时了。 */
async function submitVideo(ctx, body, label) {
  const submit = await ctx.miora.submitRaw("video", body);
  const taskId = submit?.upstreamTaskId;
  if (typeof taskId !== "string" || taskId === "") {
    return { ok: false, text: "提交视频任务失败，上游没有返回任务 id" };
  }
  ctx.trackVideoTask?.(taskId, submit.model);
  ctx.log(`[agent] ${label}任务已提交 ${taskId}`);
  return {
    ok: true,
    text: [
      `${label}任务已提交。`,
      "",
      `taskId: ${taskId}`,
      `查询进度: /v1/video/status/${taskId}`,
      "",
      "视频要几分钟渲染。告诉用户任务已提交，并把这个查询地址给他。",
      "不要在这里等待或轮询 —— 提交就结束了。",
    ].join("\n"),
  };
}

/**
 * 把 assistant 的 tool_calls 和执行结果追加进对话。
 *
 * assistant 那条消息必须带**完整**的 tool_calls 列表，否则上游会认为
 * 后面的 role:"tool" 消息是凭空出现的。
 */
export function appendToolExchange(messages, allCalls, results) {
  messages.push({
    role: "assistant",
    content: null,
    tool_calls: allCalls.map((c) => ({
      id: c.id,
      type: c.type ?? "function",
      function: { name: c.function.name, arguments: c.function.arguments ?? "{}" },
    })),
  });

  allCalls.forEach((call, i) => {
    messages.push({
      role: "tool",
      tool_call_id: call.id,
      content: results[i]?.text ?? "（工具没有返回内容）",
    });
  });
}

/** 从累积的 tool_calls map 里取出有序数组。 */
function orderedCalls(map) {
  if (map.size === 0) return null;
  return [...map.entries()].sort((a, b) => a[0] - b[0]).map(([, c]) => c);
}

/** 把一轮的 delta 攒进收集器。流式和非流式共用的解析逻辑。 */
function accumulate(collected, chunk) {
  const choice = Array.isArray(chunk["choices"]) ? chunk["choices"][0] : undefined;
  if (choice === undefined) return { choice: undefined, delta: undefined, deltaCalls: null };

  const delta = choice["delta"];
  if (typeof delta?.["content"] === "string" && delta["content"] !== "") {
    collected.text += delta["content"];
  }
  const deltaCalls = Array.isArray(delta?.["tool_calls"]) && delta["tool_calls"].length > 0
    ? delta["tool_calls"]
    : null;

  if (deltaCalls !== null) {
    for (const c of deltaCalls) {
      const idx = typeof c.index === "number" ? c.index : 0;
      const cur = collected.calls.get(idx) ?? { id: "", type: "function", function: { name: "", arguments: "" } };
      if (typeof c.id === "string" && c.id !== "") cur.id = c.id;
      if (typeof c.type === "string" && c.type !== "") cur.type = c.type;
      if (typeof c.function?.name === "string" && c.function.name !== "") cur.function.name += c.function.name;
      if (typeof c.function?.arguments === "string") cur.function.arguments += c.function.arguments;
      collected.calls.set(idx, cur);
    }
  }
  return { choice, delta, deltaCalls };
}

/**
 * 这一轮的 tool_calls 该不该由**网关**执行。
 *
 * 只有**我们的**工具才执行。客户端的工具（Read / Bash / get_weather…）
 * 必须原样交回客户端 —— 网关既没有那些工具的实现，也不该替客户端跑。
 *
 * ⚠ 踩过的坑：非流式路径一开始没做这个区分，看到任何 tool_calls 都去执行。
 * 结果客户端调 get_weather，模型收到「未知工具：get_weather」，
 * 于是回了一句「天气工具好像不可用」—— 用户看到的是一切正常，
 * 只是工具永远查不出数据。verify.mjs 的 4 个失败抓到过。
 *
 * @returns {{mine: object[], foreign: object[]}}
 */
function splitCalls(list) {
  const mine = [];
  const foreign = [];
  for (const c of list) {
    if (isMediaTool(c?.function?.name)) mine.push(c);
    else foreign.push(c);
  }
  return { mine, foreign };
}

/**
 * 回填用的结果数组。
 *
 * 必须覆盖**全部**调用（含外部工具）—— 上游要求每个 tool_call
 * 都有对应的 tool 消息，缺一个就报错。外部工具给一条说明性回复。
 */
function resultsForAll(list, mine, mineResults) {
  return list.map((c) => {
    const i = mine.indexOf(c);
    return i >= 0 ? mineResults[i] : { ok: false, text: "（这个工具由客户端执行，网关没有处理）" };
  });
}

/** 顺序执行一组工具调用，返回结果数组 + 执行摘要。 */
async function executeCalls(calls, ctx, log) {
  const results = [];
  const summary = [];
  for (const call of calls) {
    const bare = stripPrefix(call.function?.name ?? "");
    log(`[agent] 执行工具 ${call.function?.name}`);
    const r = await runTool(call, ctx);
    results.push(r);
    summary.push(`${bare ?? "?"}:${r.ok ? "ok" : "fail"}`);
  }
  return { results, summary };
}

/** 执行期间定期保活。返回一个 stop 函数。 */
function startKeepalive(keepalive, log) {
  if (typeof keepalive !== "function") return () => {};
  const timer = setInterval(() => {
    try { keepalive(); } catch { /* 客户端断开就算了 */ }
  }, KEEPALIVE_MS);
  timer.unref?.();
  return () => clearInterval(timer);
}

/**
 * 跑 agent 循环（非流式）。
 *
 * @param {object} options
 * @param {object} options.request 客户端请求（**会被改动**）
 * @param {Function} options.send 发一轮。契约和 sendWithFallback 一样：
 *        async (request) => { result: {ok: true, response} | {ok: false, kind, status, message}, model, attempts }
 *        （不是 {ok, response} —— 踩过这个坑：agent 里写成 round.ok
 *        永远为真，测试用假 send 全绿，真实环境每个请求都 500）
 * @param {object} options.miora
 * @param {(file: string) => string} options.makeUrl
 * @param {Function} [options.log]
 * @param {Function} [options.onRound] 每轮工具调用后回调（记账用）
 * @param {number} [options.maxRounds]
 * @returns {Promise<{completion?: object, error?: object, rounds: number, toolCalls: string[]}>}
 */
export async function runAgentNonStreaming(options) {
  const {
    request, send, miora, makeUrl,
    log = () => {},
    onRound,
    trackVideoTask,
    maxRounds = DEFAULT_MAX_ROUNDS,
  } = options;

  const messages = Array.isArray(request["messages"]) ? request["messages"] : [];
  const ctx = { miora, makeUrl, log, trackVideoTask };

  let rounds = 0;
  const executed = [];

  for (;;) {
    const round = await send(request);
    if (!round.result.ok) return { error: round, rounds, toolCalls: executed };

    const completion = await collectChatCompletion(round.result.response);
    const calls = completion?.choices?.[0]?.message?.tool_calls;
    const list = Array.isArray(calls) && calls.length > 0 ? calls : null;

    if (list === null) return { completion, rounds, toolCalls: executed };

    // 只有我们的工具才由网关执行。客户端的工具原样交回去 ——
    // 一个都不执行的话，这一轮就直接把 tool_calls 给客户端，
    // 让它自己去跑（标准 OpenAI 流程）。
    const { mine, foreign } = splitCalls(list);
    if (mine.length === 0) {
      return { completion, rounds, toolCalls: executed, passthrough: true };
    }
    if (foreign.length > 0) {
      log(`[agent] 这轮混了 ${foreign.length} 个外部工具（${foreign.map((c) => c.function?.name).join(", ")}）`);
    }

    rounds += 1;
    if (rounds > maxRounds) {
      log(`[agent] 工具调用超过 ${maxRounds} 轮，停止循环`);
      return { completion, rounds, toolCalls: executed, overRounds: true };
    }

    const stopKeepalive = startKeepalive(options.keepalive, log);
    let outcome;
    try {
      outcome = await executeCalls(mine, ctx, log);
    } finally {
      stopKeepalive();
    }

    executed.push(...outcome.summary);
    onRound?.({ round: rounds, calls: mine, results: outcome.results });
    appendToolExchange(messages, list, resultsForAll(list, mine, outcome.results));
  }
}

/**
 * 跑 agent 循环（流式）。
 *
 * 缓冲策略见文件头。这一版的分片处理是**先攒后放**：
 *
 *   · 攒的字节数 < FLUSH_THRESHOLD 且还没见到 tool_calls → 继续攒
 *   · 攒够了 → 确定是说话，把攒下的一次性写出去，之后逐片实时转发
 *   · 见到我们的工具调用 → 攒的全部丢掉
 *
 * @param {object} options
 * @param {object} options.request 客户端请求（**会被改动**）
 * @param {Function} options.send
 * @param {object} options.miora
 * @param {(file: string) => string} options.makeUrl
 * @param {(chunk: object) => void} options.emit 写一个 delta 分片给客户端
 * @param {() => void} [options.keepalive] 写一个 SSE 注释保活
 * @param {Function} [options.log]
 * @param {Function} [options.onRound]
 * @param {number} [options.maxRounds]
 * @returns {Promise<{rounds: number, toolCalls: string[], emitted: boolean, error?: object, finishReason?: string}>}
 */
export async function runAgentStreaming(options) {
  const {
    request, send, miora, makeUrl, emit, keepalive,
    log = () => {},
    onRound,
    trackVideoTask,
    maxRounds = DEFAULT_MAX_ROUNDS,
    flushThreshold = FLUSH_THRESHOLD,
  } = options;

  const messages = Array.isArray(request["messages"]) ? request["messages"] : [];
  const ctx = { miora, makeUrl, log, trackVideoTask };

  let rounds = 0;
  const executed = [];
  let anythingEmitted = false;

  for (;;) {
    const round = await send(request);
    if (!round.result.ok) {
      return { rounds, toolCalls: executed, emitted: anythingEmitted, error: round };
    }

    const collected = { text: "", calls: new Map() };
    /** 攒着还没放行的分片。 */
    const pending = [];
    /** 攒着的正文字符数（用来判断够不够阈值）。 */
    let pendingChars = 0;
    /** 已经放行了？放行之后逐片转发。 */
    let released = false;
    /** 这轮出现了**我们的**工具调用？出现了就整轮丢掉。 */
    let oursCalled = false;
    let finishReason;

    for await (const payload of iterateSseData(round.result.response.body)) {
      if (payload === "[DONE]") break;

      let chunk;
      try {
        chunk = JSON.parse(payload);
      } catch {
        continue;
      }

      const { choice, delta, deltaCalls } = accumulate(collected, chunk);
      if (typeof choice?.["finish_reason"] === "string" && choice["finish_reason"] !== "") {
        finishReason = choice["finish_reason"];
      }

      // 这一分片里有没有**我们的**工具调用？
      if (deltaCalls !== null && deltaCalls.some((c) => isMediaTool(c?.function?.name))) {
        oursCalled = true;
      }

      // 出现了我们的工具调用 → 攒的全部作废，后面的也不用看了
      if (oursCalled) {
        pending.length = 0;
        pendingChars = 0;
        continue;
      }

      if (released) {
        // 已经确定在说话，实时转发
        emit(chunk);
        anythingEmitted = true;
        continue;
      }

      // 还在攒
      pending.push(chunk);
      if (typeof delta?.["content"] === "string") pendingChars += delta["content"].length;

      if (pendingChars >= flushThreshold) {
        // 够长了，确定是说话 —— 把攒的一次性放出去
        for (const c of pending) emit(c);
        anythingEmitted = true;
        pending.length = 0;
        released = true;
      }
    }

    // 流结束了，还有攒着的 → 那就是这轮的全部正文，放出去
    if (!oursCalled && !released && pending.length > 0) {
      for (const c of pending) emit(c);
      anythingEmitted = true;
      pending.length = 0;
    }

    const list = orderedCalls(collected.calls);

    // 没有我们的工具调用 → 这轮结束了，正文该发的都发了
    if (list === null) {
      return { rounds, toolCalls: executed, emitted: anythingEmitted, finishReason };
    }

    // 只有我们的工具才由网关执行
    const { mine, foreign } = splitCalls(list);
    if (mine.length === 0) {
      // 全是客户端的工具 —— 这一轮不用管。但正文已经可能发出去了，
      // 只能如实结束（客户端自己会看到 tool_calls…… 可惜流式下
      // 我们没有把它们转发出去，见文件头的「已知限制」）。
      log(`[agent] 这轮的 ${list.length} 个工具全是客户端的，网关不介入`);
      return { rounds, toolCalls: executed, emitted: anythingEmitted, finishReason, passthrough: true };
    }
    if (foreign.length > 0) {
      log(`[agent] 这轮混了 ${foreign.length} 个外部工具（${foreign.map((c) => c.function?.name).join(", ")}）`);
    }

    rounds += 1;
    if (rounds > maxRounds) {
      log(`[agent] 工具调用超过 ${maxRounds} 轮，停止循环`);
      return { rounds, toolCalls: executed, emitted: anythingEmitted, finishReason: "tool_calls", overRounds: true };
    }

    log(`[agent] 模型要调 ${mine.length} 个工具（第 ${rounds} 轮）`);

    // 工具执行期间一个字节都不发，所以要保活
    const stopKeepalive = startKeepalive(keepalive, log);
    let outcome;
    try {
      outcome = await executeCalls(mine, ctx, log);
    } finally {
      stopKeepalive();
    }

    executed.push(...outcome.summary);
    onRound?.({ round: rounds, calls: mine, results: outcome.results });
    appendToolExchange(messages, list, resultsForAll(list, mine, outcome.results));
  }
}
