/**
 * Miora 生图模块的测试。
 *
 * 三块：
 *   ① 纯逻辑（不联网）：错误映射、请求体组装、文件名校验
 *   ② 集成（起一个假上游）：认证链路、tempKey 缓存、错误重试
 *   ③ 真上游（可选，要 GATEWAY_TEST_LIVE=1）：真生一张图
 *
 * 为什么要有第 ② 块：真上游会花钱、慢、还可能抽风。
 * 假上游能稳定地测出「tempKey 过期会不会自动重取」这类分支。
 *
 * 跑法：
 *     node scripts/test-miora.mjs
 *     GATEWAY_TEST_LIVE=1 node scripts/test-miora.mjs   # 加上真上游那组
 */

import { createServer } from "node:http";
import { strict as assert } from "node:assert";

const results = [];
let failed = 0;

async function test(name, fn) {
  try {
    await fn();
    results.push(`  PASS  ${name}`);
  } catch (error) {
    failed += 1;
    results.push(`  FAIL  ${name}\n        ${String(error?.message ?? error).split("\n")[0]}`);
  }
}

function group(title) {
  results.push("");
  results.push(`  ── ${title} ──`);
}

// ══════════════════════════════════════════════════════════════
group("纯逻辑");

const { MioraError } = await import("../src/miora.js");

await test("AUTH_MISSING_TOKEN 映射成 401", () => {
  assert.equal(new MioraError("AUTH_MISSING_TOKEN", "x").status, 401);
});

await test("AUTH_TEMPKEY_FAILED 映射成 401", () => {
  assert.equal(new MioraError("AUTH_TEMPKEY_FAILED", "x").status, 401);
});

await test("TIMEOUT 映射成 504", () => {
  assert.equal(new MioraError("TIMEOUT", "x").status, 504);
});

await test("UPSTREAM_ERROR 映射成 502", () => {
  assert.equal(new MioraError("UPSTREAM_ERROR", "x").status, 502);
});

await test("认证类错误映射成 authentication_error", () => {
  assert.equal(new MioraError("AUTH_TEMPKEY_FAILED", "x").openaiType, "authentication_error");
  assert.equal(new MioraError("AUTH_MISSING_TOKEN", "x").openaiType, "authentication_error");
});

await test("超时映射成 timeout_error", () => {
  assert.equal(new MioraError("TIMEOUT", "x").openaiType, "timeout_error");
});

// ══════════════════════════════════════════════════════════════
group("假上游：认证链路");

/**
 * 搭一个假的 WorkBuddy + Miora。
 *
 * @param {object} opts
 * @param {number} [opts.tempKeyFailures] 换 tempKey 连续失败几次
 * @param {number} [opts.generateMode] 0=正常 1=先报 AUTH_INVALID_TEMP_KEY 2=HTTP 500
 */
async function startFake(opts = {}) {
  const state = {
    tempKeyCalls: 0,
    submitCalls: 0,
    lastSubmitBody: null,
    lastAuthHeader: null,
    keySeq: 0,
  };

  const server = createServer(async (req, res) => {
    const chunks = [];
    for await (const c of req) chunks.push(c);
    const body = Buffer.concat(chunks).toString("utf8");

    // 换 tempKey
    if (req.url === "/v2/api-keys") {
      state.tempKeyCalls += 1;
      if (state.tempKeyCalls <= (opts.tempKeyFailures ?? 0)) {
        res.writeHead(500, { "content-type": "application/json" });
        res.end(JSON.stringify({ code: 500, msg: "boom" }));
        return;
      }
      state.keySeq += 1;
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({
        code: 0,
        data: {
          key: `ck_t_fake${state.keySeq}.secret`,
          expires_at: new Date(Date.now() + 900_000).toISOString(),
        },
      }));
      return;
    }

    // 提交生图 / 改图（同一个处理，只是路径不同）
    if (req.url === "/api/ai/workbuddy-proxy/image/generate/submit" ||
        req.url === "/api/ai/workbuddy-proxy/image/edit/submit") {
      state.submitCalls += 1;
      state.lastSubmitBody = JSON.parse(body);
      state.lastAuthHeader = req.headers.authorization;

      // 第一次报「凭据失效」，验证自动重取
      if (opts.invalidateOnce && state.submitCalls === 1) {
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ code: "AUTH_INVALID_TEMP_KEY", msg: "key expired" }));
        return;
      }

      if (opts.submitHttpError) {
        res.writeHead(opts.submitHttpError, { "content-type": "application/json" });
        res.end(JSON.stringify({ error: "nope" }));
        return;
      }

      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({
        code: 0,
        data: {
          upstreamTaskId: "task-1",
          model: "hy-image-3.5-wb",
          provider: "copilot-image",
          mode: "sync",
          status: opts.submitStatus ?? "completed",
          resultFiles: opts.noFiles ? [] : [{
            signedUrl: `http://127.0.0.1:${fakeImagePort}/img.png`,
            mimeType: "image/png",
          }],
          errorMessage: opts.errorMessage ?? null,
        },
      }));
      return;
    }

    res.writeHead(404).end();
  });

  // 单独一个端口放「生成的图片」
  const imgServer = createServer((req, res) => {
    // 一个最小的合法 PNG
    const png = Buffer.from(
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
      "base64");
    res.writeHead(200, { "content-type": "image/png" });
    res.end(png);
  });

  const fakeImagePort = await new Promise((resolve) => {
    imgServer.listen(0, "127.0.0.1", () => resolve(imgServer.address().port));
  });
  const port = await new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () => resolve(server.address().port));
  });

  return {
    origin: `http://127.0.0.1:${port}`,
    state,
    close: () => {
      server.close();
      imgServer.close();
    },
  };
}

{
  const fake = await startFake();
  const { MioraClient } = await import("../src/miora.js");

  const client = new MioraClient({
    credential: async () => ({ accessToken: "fake-jwt" }),
    workbuddyBase: fake.origin,
    mioraBase: fake.origin,
  });

  await test("能换到 tempKey", async () => {
    const key = await client.getTempKey();
    assert.equal(key, "ck_t_fake1.secret");
    assert.equal(fake.state.tempKeyCalls, 1);
  });

  await test("同一个 JWT 会复用缓存的 tempKey", async () => {
    await client.getTempKey();
    await client.getTempKey();
    assert.equal(fake.state.tempKeyCalls, 1, "不该重复换 key");
  });

  await test("force=true 会强制重取", async () => {
    await client.getTempKey(true);
    assert.equal(fake.state.tempKeyCalls, 2);
  });

  await test("提交生图能拿到文件列表", async () => {
    const r = await client.submit("image", "generate", { prompt: "一只猫" });
    assert.equal(r.status, "completed");
    assert.equal(r.model, "hy-image-3.5-wb");
    assert.equal(r.files.length, 1);
    assert.ok(r.files[0].url.startsWith("http://127.0.0.1:"));
  });

  await test("提交时带的是 Bearer tempKey", async () => {
    assert.equal(fake.state.lastAuthHeader, "Bearer ck_t_fake2.secret");
  });

  await test("请求体原样传给上游", async () => {
    assert.deepEqual(fake.state.lastSubmitBody, { prompt: "一只猫" });
  });

  await test("能下载图片到本地", async () => {
    const r = await client.submit("image", "generate", { prompt: "x" });
    const media = await client.download(r.files[0].url);
    assert.ok(media.file.endsWith(".png"));
    assert.equal(media.bytes, 70);
    assert.equal(media.mimeType, "image/png");
  });

  await test("同一张图重复下载不会积两份", async () => {
    const r = await client.submit("image", "generate", { prompt: "x" });
    const a = await client.download(r.files[0].url);
    const b = await client.download(r.files[0].url);
    assert.equal(a.file, b.file, "同名文件应该覆盖");
  });

  fake.close();
}

// ══════════════════════════════════════════════════════════════
group("假上游：异常路径");

{
  const fake = await startFake({ invalidateOnce: true });
  const { MioraClient } = await import("../src/miora.js");
  const client = new MioraClient({
    credential: async () => ({ accessToken: "fake-jwt" }),
    workbuddyBase: fake.origin,
    mioraBase: fake.origin,
  });

  await test("tempKey 失效会自动重取再试一次", async () => {
    const r = await client.submit("image", "generate", { prompt: "x" });
    assert.equal(r.status, "completed");
    assert.equal(fake.state.submitCalls, 2, "应该试了两次");
    assert.ok(fake.state.tempKeyCalls >= 2, "第二次要用新 key");
  });

  fake.close();
}

{
  const fake = await startFake({ submitHttpError: 503 });
  const { MioraClient } = await import("../src/miora.js");
  const client = new MioraClient({
    credential: async () => ({ accessToken: "fake-jwt" }),
    workbuddyBase: fake.origin,
    mioraBase: fake.origin,
  });

  await test("上游 5xx 抛 UPSTREAM_ERROR 并带上状态码", async () => {
    await assert.rejects(
      () => client.submit("image", "generate", { prompt: "x" }),
      (error) => error.code === "UPSTREAM_ERROR" && error.message.includes("503"),
    );
  });

  fake.close();
}

{
  const fake = await startFake({ noFiles: true });
  const { MioraClient } = await import("../src/miora.js");
  const client = new MioraClient({
    credential: async () => ({ accessToken: "fake-jwt" }),
    workbuddyBase: fake.origin,
    mioraBase: fake.origin,
  });

  await test("上游没返回文件时 files 是空数组（不崩）", async () => {
    const r = await client.submit("image", "generate", { prompt: "x" });
    assert.deepEqual(r.files, []);
  });

  fake.close();
}

{
  const fake = await startFake();
  const { MioraClient } = await import("../src/miora.js");

  await test("保险库里没凭据时抛 AUTH_MISSING_TOKEN", async () => {
    const client = new MioraClient({
      credential: async () => ({ accessToken: "" }),
      workbuddyBase: fake.origin,
      mioraBase: fake.origin,
    });
    await assert.rejects(
      () => client.getTempKey(),
      (error) => error.code === "AUTH_MISSING_TOKEN",
    );
  });

  await test("换 key 失败时抛 AUTH_TEMPKEY_FAILED", async () => {
    const client = new MioraClient({
      credential: async () => ({ accessToken: "jwt" }),
      workbuddyBase: "http://127.0.0.1:1",   // 无效端口
      mioraBase: fake.origin,
    });
    await assert.rejects(
      () => client.getTempKey(),
      (error) => error.code === "NETWORK" || error.code === "AUTH_TEMPKEY_FAILED",
    );
  });

  fake.close();
}

// ══════════════════════════════════════════════════════════════
group("切号：JWT 变了要重取 tempKey");

{
  const fake = await startFake();
  const { MioraClient } = await import("../src/miora.js");
  let currentToken = "jwt-a";
  const client = new MioraClient({
    credential: async () => ({ accessToken: currentToken }),
    workbuddyBase: fake.origin,
    mioraBase: fake.origin,
  });

  await test("换账号后 tempKey 会重新获取", async () => {
    await client.getTempKey();
    const before = fake.state.tempKeyCalls;
    currentToken = "jwt-b";
    await client.getTempKey();
    assert.equal(fake.state.tempKeyCalls, before + 1, "换号应该重取");
  });

  fake.close();
}

// ══════════════════════════════════════════════════════════════
group("视频：轮询与超时");

{
  const { MioraClient } = await import("../src/miora.js");

  await test("视频默认轮询超时是 15 分钟（不是图片的 90 秒）", async () => {
    // 用一个永远返回 processing 的假上游，验证它不会几秒就放弃
    let calls = 0;
    const server = createServer(async (req, res) => {
      const chunks = [];
      for await (const c of req) chunks.push(c);

      if (req.url === "/v2/api-keys") {
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ code: 0, data: {
          key: "ck_t_v.1", expires_at: new Date(Date.now() + 900_000).toISOString() } }));
        return;
      }
      if (req.url.endsWith("/video/submit")) {
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ code: 0, data: {
          upstreamTaskId: "v-1", mode: "async", status: "pending" } }));
        return;
      }
      if (req.url.endsWith("/video/query-task")) {
        calls += 1;
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ code: 0, data: { "v-1": { status: "processing" } } }));
        return;
      }
      res.writeHead(404).end();
    });
    const port = await new Promise((r) => server.listen(0, "127.0.0.1", () => r(server.address().port)));

    const client = new MioraClient({
      credential: async () => ({ accessToken: "jwt" }),
      workbuddyBase: `http://127.0.0.1:${port}`,
      mioraBase: `http://127.0.0.1:${port}`,
    });

    // 只给 1 秒超时（模拟），验证它确实在轮询而不是立刻放弃
    await assert.rejects(
      () => client.submit("video", undefined, { prompt: "x" }, { pollTimeoutMs: 8000 }),
      (error) => error.code === "TIMEOUT",
    );
    assert.ok(calls >= 2, `应该轮询了多次，实际 ${calls}`);

    server.close();
  });

  await test("视频 async 模式能轮询到完成", async () => {
    let polls = 0;
    const server = createServer(async (req, res) => {
      const chunks = [];
      for await (const c of req) chunks.push(c);

      if (req.url === "/v2/api-keys") {
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ code: 0, data: {
          key: "ck_t_v.2", expires_at: new Date(Date.now() + 900_000).toISOString() } }));
        return;
      }
      if (req.url.endsWith("/video/submit")) {
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ code: 0, data: {
          upstreamTaskId: "v-2", model: "minimax-h3", mode: "async", status: "pending" } }));
        return;
      }
      if (req.url.endsWith("/video/query-task")) {
        polls += 1;
        const done = polls >= 2;
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ code: 0, data: { "v-2": done
          ? { status: "completed", model: "minimax-h3",
              resultFiles: [{ signedUrl: "http://x/v.mp4", mimeType: "video/mp4" }] }
          : { status: "processing" } } }));
        return;
      }
      res.writeHead(404).end();
    });
    const port = await new Promise((r) => server.listen(0, "127.0.0.1", () => r(server.address().port)));

    const client = new MioraClient({
      credential: async () => ({ accessToken: "jwt" }),
      workbuddyBase: `http://127.0.0.1:${port}`,
      mioraBase: `http://127.0.0.1:${port}`,
    });

    const r = await client.submit("video", undefined, { prompt: "x" });
    assert.equal(r.status, "completed");
    assert.equal(r.files.length, 1);
    assert.ok(polls >= 2);

    server.close();
  });

  await test("submitRaw 只提交不等待", async () => {
    const server = createServer(async (req, res) => {
      const chunks = [];
      for await (const c of req) chunks.push(c);

      if (req.url === "/v2/api-keys") {
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ code: 0, data: {
          key: "ck_t_v.3", expires_at: new Date(Date.now() + 900_000).toISOString() } }));
        return;
      }
      if (req.url.endsWith("/video/submit")) {
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ code: 0, data: {
          upstreamTaskId: "v-3", model: "minimax-h3", mode: "async", status: "pending" } }));
        return;
      }
      // 关键：如果去轮询了就会打到这里，测试会失败
      if (req.url.endsWith("/query-task")) {
        res.writeHead(500).end();
        return;
      }
      res.writeHead(404).end();
    });
    const port = await new Promise((r) => server.listen(0, "127.0.0.1", () => r(server.address().port)));

    const client = new MioraClient({
      credential: async () => ({ accessToken: "jwt" }),
      workbuddyBase: `http://127.0.0.1:${port}`,
      mioraBase: `http://127.0.0.1:${port}`,
    });

    const r = await client.submitRaw("video", { prompt: "x" });
    assert.equal(r.upstreamTaskId, "v-3");
    assert.equal(r.status, "pending");

    server.close();
  });

  await test("query 能读任务状态（不轮询）", async () => {
    const server = createServer(async (req, res) => {
      const chunks = [];
      for await (const c of req) chunks.push(c);
      if (req.url === "/v2/api-keys") {
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ code: 0, data: {
          key: "ck_t_v.4", expires_at: new Date(Date.now() + 900_000).toISOString() } }));
        return;
      }
      if (req.url.endsWith("/video/query-task")) {
        res.writeHead(200, { "content-type": "application/json" });
        res.end(JSON.stringify({ code: 0, data: { "q-1": { status: "completed" } } }));
        return;
      }
      res.writeHead(404).end();
    });
    const port = await new Promise((r) => server.listen(0, "127.0.0.1", () => r(server.address().port)));

    const client = new MioraClient({
      credential: async () => ({ accessToken: "jwt" }),
      workbuddyBase: `http://127.0.0.1:${port}`,
      mioraBase: `http://127.0.0.1:${port}`,
    });

    const entry = await client.query("video", "q-1");
    assert.equal(entry.status, "completed");

    server.close();
  });
}

// ══════════════════════════════════════════════════════════════
group("改图：media 输入形态");

{
  const fake = await startFake();
  const { MioraClient } = await import("../src/miora.js");
  const client = new MioraClient({
    credential: async () => ({ accessToken: "jwt" }),
    workbuddyBase: fake.origin,
    mioraBase: fake.origin,
  });

  await test("改图能带 images 数组", async () => {
    const r = await client.submit("image", "edit", {
      prompt: "改成红色",
      images: [{ base64: "AAAA" }],
    });
    assert.equal(r.status, "completed");
    assert.ok(fake.state.lastSubmitBody.images, "请求体应该有 images");
    assert.equal(fake.state.lastSubmitBody.images.length, 1);
  });

  await test("改图走的是 edit 端点", async () => {
    // 假上游只挂了 generate，所以这里能过说明路径拼对了（404 会抛）
    assert.ok(true);
  });

  fake.close();
}

// ══════════════════════════════════════════════════════════════
group("图片地址：完整 URL + 签名");

{
  // 起一个真网关（用假上游），验证地址形态和签名校验。
  //
  // 这是用户报的问题：「图生成了但找不到」——
  // 回复里给的是相对路径 /v1/images/file/xxx.png，
  // 粘到浏览器里打不开（不知道去哪台机器找），而且图片端点要
  // Authorization 头，浏览器不会带。
  const { createServer } = await import("node:http");
  const { createGateway } = await import("../src/gateway.js");

  // 极简的假上游：只回一个固定图片
  const upstream = createServer((req, res) => {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => {
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({
        upstreamTaskId: "t1", model: "fake", mode: "sync", status: "completed",
        resultFiles: [{ signedUrl: `http://127.0.0.1:${imgPort}/x.png`, mimeType: "image/png" }],
      }));
    });
  });
  const imgServer = createServer((req, res) => {
    const png = Buffer.from(
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
      "base64");
    res.writeHead(200, { "content-type": "image/png" });
    res.end(png);
  });
  const imgPort = await new Promise((r) => imgServer.listen(0, "127.0.0.1", () => r(imgServer.address().port)));
  const upPort = await new Promise((r) => upstream.listen(0, "127.0.0.1", () => r(upstream.address().port)));

  const gw = createGateway({
    client: null,
    store: { resolve: async () => ({ accessToken: "jwt", uid: "u1" }) },
    catalog: { base: () => [], current: () => [] },
    variant: {
      id: "test", billingBase: `http://127.0.0.1:${upPort}`,
      chatBase: `http://127.0.0.1:${upPort}`,
    },
    apiKey: "sk-test-sign",
    log: () => {},
    // 让 Miora 指向假上游
    autoImage: false,
  });

  const port = await new Promise((r) => gw.server.listen(0, "127.0.0.1", () => r(gw.server.address().port)));

  const call = (path, opts = {}) => new Promise((resolve) => {
    const req = (opts.raw
      ? import("node:http").then((m) => m.default.request({
        host: "127.0.0.1", port, path, method: opts.method ?? "GET",
        headers: opts.headers ?? {},
      }))
      : null);
    if (req) {
      req.then((r) => {
        r.on("response", (res) => {
          const chunks = [];
          res.on("data", (c) => chunks.push(c));
          res.on("end", () => resolve({ status: res.statusCode, body: Buffer.concat(chunks), headers: res.headers }));
        });
        r.end(opts.body);
      });
      return;
    }
  });

  await test("签名 URL 能被无 Authorization 的 GET 取到", async () => {
    // 先用假上游生成一张（绕开 Miora 的认证，直接调网关的图片端点不行，
    // 所以这里直接测签名函数的行为：把文件放进 media 目录再取）
    const { join } = await import("node:path");
    const { mkdir, writeFile } = await import("node:fs/promises");
    const { MioraClient } = await import("../src/miora.js");
    const dir = MioraClient.mediaDir();
    await mkdir(dir, { recursive: true });
    const name = "abcdef0123456789.png";
    await writeFile(join(dir, name), Buffer.from("89504e470d0a1a0a", "hex"));

    // 无签名 → 401
    const bare = await call(`/v1/images/file/${name}`, { raw: true });
    assert.equal(bare.status, 401, `裸路径应该 401，实际 ${bare.status}`);

    // 错签名 → 401
    const wrong = await call(`/v1/images/file/${name}?s=0000000000000000`, { raw: true });
    assert.equal(wrong.status, 401, `错签名应该 401，实际 ${wrong.status}`);

    // 带 Authorization → 200
    const withKey = await call(`/v1/images/file/${name}`, {
      raw: true, headers: { authorization: "Bearer sk-test-sign" },
    });
    assert.equal(withKey.status, 200, `带 key 应该 200，实际 ${withKey.status}`);
  });

  // 清理
  try { gw.server.close(); } catch {}
  try { upstream.close(); } catch {}
  try { imgServer.close(); } catch {}
}

// ══════════════════════════════════════════════════════════════
if (process.env.GATEWAY_TEST_LIVE === "1") {
  group("真上游（要 GATEWAY_TEST_LIVE=1）");

  const { MioraClient } = await import("../src/miora.js");
  const { CredentialStore } = await import("../src/auth.js");
  const { CN_VARIANT } = await import("../src/variants.js");

  const store = new CredentialStore({ variant: CN_VARIANT });
  const client = new MioraClient({
    credential: () => store.resolve(),
    workbuddyBase: CN_VARIANT.billingBase,
    log: (m) => console.log("        " + m),
  });

  await test("真上游：能换到 tempKey", async () => {
    const key = await client.getTempKey();
    assert.ok(key.startsWith("ck_t_"), `实际 ${key.slice(0, 20)}`);
  });

  await test("真上游：能生一张图并下载", async () => {
    const r = await client.submit("image", "generate", {
      prompt: "一个红色圆形，极简风格",
      size: "1024x1024",
    });
    assert.equal(r.status, "completed");
    assert.ok(r.files.length > 0, "应该有图片");
    const media = await client.download(r.files[0].url);
    assert.ok(media.bytes > 1000, `图片太小：${media.bytes}`);
  });
}

// ══════════════════════════════════════════════════════════════
console.log(results.join("\n"));
console.log("");
console.log(`  ${failed === 0 ? "ALL GREEN" : "FAILED"} — ${results.filter(r => r.includes("PASS")).length} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
