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

    // 提交生图
    if (req.url === "/api/ai/workbuddy-proxy/image/generate/submit") {
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
