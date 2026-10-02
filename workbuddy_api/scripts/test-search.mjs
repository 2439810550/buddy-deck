/**
 * 联网模块（搜索 / 抓取）的测试。
 *
 * 分两块，全部**离线**（假上游，零成本、可重复）：
 *   ① 纯逻辑：域名过滤语义、query 组装、参数校验、错误映射
 *   ② 集成：起一个假上游，验证请求头、凭据惰性获取、错误分支
 *
 * 为什么坚持假上游：搜索要花额度、慢、且结果不稳定（同样的词每次给的不一样），
 * 拿真上游做断言必然 flaky。真上游只适合人工 smoke。
 *
 * 跑法：node scripts/test-search.mjs
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

const { SearchClient, SearchError, formatSearchResults } = await import("../src/search.js");

/** 一个只认识「一个上游」的假 WorkBuddy。 */
async function startFake(handler) {
  const server = createServer((req, res) => {
    const chunks = [];
    req.on("data", (chunk) => chunks.push(chunk));
    req.on("end", () => {
      const body = Buffer.concat(chunks).toString("utf8");
      handler(req, res, body);
    });
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address();
  return {
    base: `http://127.0.0.1:${port}`,
    close: () => new Promise((resolve) => server.close(resolve)),
  };
}

/** 造一个指向假上游的 client。 */
function clientFor(base, extra = {}) {
  return new SearchClient({
    variant: { chatBase: base },
    credential: async () => ({ accessToken: "test-token" }),
    ...extra,
  });
}

/** 一个标准的搜索响应。 */
function searchPayload(items) {
  return JSON.stringify({
    query: "q",
    type: "text2text",
    provider: "0",
    results: items,
    total_results: items.length,
    response_time_ms: 12,
  });
}

// ══════════════════════════════════════════════════════════════
group("纯逻辑：参数校验");

await test("query 少于 2 字符被拒", async () => {
  const client = clientFor("http://127.0.0.1:1");
  await assert.rejects(() => client.search({ query: "a" }), (error) => {
    assert.equal(error.code, "INVALID_REQUEST");
    assert.equal(error.status, 400);
    return true;
  });
});

await test("query 全是空白被拒", async () => {
  const client = clientFor("http://127.0.0.1:1");
  await assert.rejects(() => client.search({ query: "   " }), /至少 2 个字符/u);
});

await test("maxResults 越界被拒（0 / 21 / 非整数）", async () => {
  const client = clientFor("http://127.0.0.1:1");
  for (const bad of [0, 21, 2.5]) {
    await assert.rejects(() => client.search({ query: "ok", maxResults: bad }), /1–20/u);
  }
});

await test("freshness 合法写法都通过校验", async () => {
  const fake = await startFake((req, res, body) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(searchPayload([]));
  });
  try {
    const client = clientFor(fake.base);
    for (const good of ["d", "d1", "d30", "m", "m12", "y", "y5"]) {
      await client.search({ query: "ok", freshness: good });
    }
  } finally {
    await fake.close();
  }
});

await test("freshness 非法被拒（d31 / m13 / y6 / 乱写）", async () => {
  const client = clientFor("http://127.0.0.1:1");
  for (const bad of ["d31", "m13", "y6", "zz", "1d"]) {
    await assert.rejects(() => client.search({ query: "ok", freshness: bad }), (error) => {
      assert.equal(error.code, "INVALID_REQUEST");
      return true;
    });
  }
});

await test("fetchPage 只接受 http/https", async () => {
  const client = clientFor("http://127.0.0.1:1");
  await assert.rejects(() => client.fetchPage({ url: "file:///etc/passwd" }), /只支持 http\/https/u);
  await assert.rejects(() => client.fetchPage({ url: "不是地址" }), /不是合法地址/u);
  await assert.rejects(() => client.fetchPage({ url: "" }), /url 不能为空/u);
});

// ══════════════════════════════════════════════════════════════
group("纯逻辑：域名过滤");

await test("includeDomains 过滤掉其他域名", async () => {
  const fake = await startFake((req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(searchPayload([
      { title: "官方", url: "https://docs.python.org/3/library/asyncio.html" },
      { title: "CSDN", url: "https://blog.csdn.net/x/article" },
      { title: "百度", url: "https://baidu.com/s?wd=x" },
    ]));
  });
  try {
    const client = clientFor(fake.base);
    const result = await client.search({ query: "asyncio", includeDomains: ["docs.python.org"] });
    assert.equal(result.results.length, 1);
    assert.equal(result.results[0].title, "官方");
  } finally {
    await fake.close();
  }
});

await test("子域算命中（docs.python.org 匹配 python.org）", async () => {
  const fake = await startFake((req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(searchPayload([{ title: "子域", url: "https://docs.python.org/3/" }]));
  });
  try {
    const client = clientFor(fake.base);
    const result = await client.search({ query: "asyncio", includeDomains: ["python.org"] });
    assert.equal(result.results.length, 1);
  } finally {
    await fake.close();
  }
});

await test("不误配：a.com 不该匹配 nota.com", async () => {
  const fake = await startFake((req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(searchPayload([{ title: "假货", url: "https://nota.com/x" }]));
  });
  try {
    const client = clientFor(fake.base);
    const result = await client.search({ query: "asyncio", includeDomains: ["a.com"] });
    assert.equal(result.results.length, 0);
  } finally {
    await fake.close();
  }
});

await test("有 include 过滤时向上游要满 max_results（否则筛完不够数）", async () => {
  let seen;
  const fake = await startFake((req, res, body) => {
    seen = JSON.parse(body);
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(searchPayload([]));
  });
  try {
    const client = clientFor(fake.base);
    await client.search({ query: "x y", maxResults: 2, includeDomains: ["a.com"] });
    assert.equal(seen.max_results, 20, "应拉满 20 再本地筛");
    await client.search({ query: "x y", maxResults: 2 });
    assert.equal(seen.max_results, 2, "没过滤时按请求量取");
  } finally {
    await fake.close();
  }
});

await test("excludeDomains 折进 query 的 -site: 语法", async () => {
  let seen;
  const fake = await startFake((req, res, body) => {
    seen = JSON.parse(body);
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(searchPayload([]));
  });
  try {
    const client = clientFor(fake.base);
    await client.search({ query: "话题", excludeDomains: ["csdn.net", "baidu.com"] });
    assert.equal(seen.query, "话题 (-site:csdn.net -site:baidu.com)");
  } finally {
    await fake.close();
  }
});

await test("没有 exclude 时 query 原样不动", async () => {
  let seen;
  const fake = await startFake((req, res, body) => {
    seen = JSON.parse(body);
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(searchPayload([]));
  });
  try {
    const client = clientFor(fake.base);
    await client.search({ query: "原样", excludeDomains: [] });
    assert.equal(seen.query, "原样");
  } finally {
    await fake.close();
  }
});

// ══════════════════════════════════════════════════════════════
group("集成：请求头与凭据");

await test("请求只带 Content-Type + Bearer（官方那堆头都可省）", async () => {
  let headers;
  const fake = await startFake((req, res) => {
    headers = req.headers;
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(searchPayload([]));
  });
  try {
    const client = clientFor(fake.base);
    await client.search({ query: "ok" });
    assert.equal(headers["authorization"], "Bearer test-token");
    assert.match(headers["content-type"], /application\/json/u);
    // 上游没有要求这些，带了反而是噪音
    assert.equal(headers["x-ide-type"], undefined);
    assert.equal(headers["x-user-id"], undefined);
  } finally {
    await fake.close();
  }
});

await test("打到的是 /agenttool/v1/search", async () => {
  let path;
  const fake = await startFake((req, res) => {
    path = req.url;
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(searchPayload([]));
  });
  try {
    await clientFor(fake.base).search({ query: "ok" });
    assert.equal(path, "/agenttool/v1/search");
  } finally {
    await fake.close();
  }
});

await test("抓取打到 /agenttool/v1/webfetch", async () => {
  let path;
  const fake = await startFake((req, res) => {
    path = req.url;
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ url: "https://a.com", title: "T", content: "正文" }));
  });
  try {
    await clientFor(fake.base).fetchPage({ url: "https://a.com" });
    assert.equal(path, "/agenttool/v1/webfetch");
  } finally {
    await fake.close();
  }
});

await test("凭据在每次调用时才取（换号/续期自动跟上）", async () => {
  let calls = 0;
  const tokens = [];
  const fake = await startFake((req, res) => {
    tokens.push(req.headers["authorization"]);
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(searchPayload([]));
  });
  try {
    const client = new SearchClient({
      variant: { chatBase: fake.base },
      credential: async () => ({ accessToken: `token-${(calls += 1)}` }),
    });
    await client.search({ query: "第一次" });
    await client.search({ query: "第二次" });
    assert.deepEqual(tokens, ["Bearer token-1", "Bearer token-2"], "第二次必须用新凭据");
  } finally {
    await fake.close();
  }
});

await test("拿不到凭据 → AUTH_MISSING_TOKEN / 401", async () => {
  const client = new SearchClient({
    variant: { chatBase: "http://127.0.0.1:1" },
    credential: async () => {
      throw new Error("not signed in");
    },
  });
  await assert.rejects(() => client.search({ query: "ok" }), (error) => {
    assert.equal(error.code, "AUTH_MISSING_TOKEN");
    assert.equal(error.status, 401);
    return true;
  });
});

await test("凭据里没有 accessToken → AUTH_MISSING_TOKEN", async () => {
  const client = new SearchClient({
    variant: { chatBase: "http://127.0.0.1:1" },
    credential: async () => ({}),
  });
  await assert.rejects(() => client.search({ query: "ok" }), /没有 accessToken/u);
});

// ══════════════════════════════════════════════════════════════
group("集成：上游错误映射");

await test("HTTP 401 → UNAUTHORIZED / 401（不静默降级成空结果）", async () => {
  const fake = await startFake((req, res) => {
    res.writeHead(401, { "Content-Type": "application/json" });
    res.end("{}");
  });
  try {
    await assert.rejects(() => clientFor(fake.base).search({ query: "ok" }), (error) => {
      assert.equal(error.code, "UNAUTHORIZED");
      assert.equal(error.status, 401);
      assert.equal(error.openaiType, "authentication_error");
      return true;
    });
  } finally {
    await fake.close();
  }
});

await test("HTTP 403 也算 UNAUTHORIZED", async () => {
  const fake = await startFake((req, res) => {
    res.writeHead(403);
    res.end("nope");
  });
  try {
    await assert.rejects(() => clientFor(fake.base).search({ query: "ok" }), (error) => {
      assert.equal(error.code, "UNAUTHORIZED");
      return true;
    });
  } finally {
    await fake.close();
  }
});

await test("业务 code 非 0 → UPSTREAM_ERROR 并带上 msg", async () => {
  const fake = await startFake((req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ code: 12345, msg: "额度不足" }));
  });
  try {
    await assert.rejects(() => clientFor(fake.base).search({ query: "ok" }), (error) => {
      assert.equal(error.code, "UPSTREAM_ERROR");
      assert.match(error.message, /额度不足/u);
      return true;
    });
  } finally {
    await fake.close();
  }
});

await test("非 JSON 响应 → UPSTREAM_ERROR（不是崩在 JSON.parse）", async () => {
  const fake = await startFake((req, res) => {
    res.writeHead(200, { "Content-Type": "text/html" });
    res.end("<html>502 Bad Gateway</html>");
  });
  try {
    await assert.rejects(() => clientFor(fake.base).search({ query: "ok" }), (error) => {
      assert.equal(error.code, "UPSTREAM_ERROR");
      return true;
    });
  } finally {
    await fake.close();
  }
});

await test("连不上 → NETWORK / 502", async () => {
  // 1 端口几乎不可能有人监听
  await assert.rejects(() => clientFor("http://127.0.0.1:1").search({ query: "ok" }), (error) => {
    assert.equal(error.code, "NETWORK");
    assert.equal(error.status, 502);
    return true;
  });
});

await test("超时 → TIMEOUT / 504", async () => {
  const fake = await startFake(() => {
    // 故意不回，让客户端超时
  });
  try {
    await assert.rejects(
      () => clientFor(fake.base).search({ query: "ok", timeoutMs: 300 }),
      (error) => {
        assert.equal(error.code, "TIMEOUT");
        assert.equal(error.status, 504);
        return true;
      },
    );
  } finally {
    await fake.close();
  }
});

// ══════════════════════════════════════════════════════════════
group("集成：响应归一化");

await test("缺字段的脏数据不会让结果带 undefined", async () => {
  const fake = await startFake((req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ results: [{ url: "https://a.com" }, null, { title: "有标题" }] }));
  });
  try {
    const result = await clientFor(fake.base).search({ query: "ok" });
    assert.equal(result.results.length, 3);
    assert.equal(result.results[0].title, "");
    assert.equal(result.results[0].snippet, "");
    // null 项也要变成结构完整的对象，否则下游 .title 会炸
    assert.equal(typeof result.results[1].url, "string");
    assert.equal(result.results[2].title, "有标题");
  } finally {
    await fake.close();
  }
});

await test("results 不是数组时当空处理", async () => {
  const fake = await startFake((req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ results: "oops" }));
  });
  try {
    const result = await clientFor(fake.base).search({ query: "ok" });
    assert.deepEqual(result.results, []);
    assert.equal(result.totalResults, 0);
  } finally {
    await fake.close();
  }
});

await test("抓取结果字段归一化（snake_case → camelCase）", async () => {
  const fake = await startFake((req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({
      url: "https://a.com",
      title: "标题",
      content: "正文",
      content_type: "text/markdown",
      content_length: 42,
      truncated: true,
      status_code: 200,
    }));
  });
  try {
    const page = await clientFor(fake.base).fetchPage({ url: "https://a.com" });
    assert.equal(page.title, "标题");
    assert.equal(page.contentType, "text/markdown");
    assert.equal(page.contentLength, 42);
    assert.equal(page.truncated, true);
    assert.equal(page.statusCode, 200);
  } finally {
    await fake.close();
  }
});

await test("抓取默认带上 prompt（否则上游不知道要提取什么）", async () => {
  let seen;
  const fake = await startFake((req, res, body) => {
    seen = JSON.parse(body);
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ content: "x" }));
  });
  try {
    const client = clientFor(fake.base);
    await client.fetchPage({ url: "https://a.com" });
    assert.equal(seen.prompt, "Summarize this page.");
    await client.fetchPage({ url: "https://a.com", prompt: "只要价格" });
    assert.equal(seen.prompt, "只要价格");
  } finally {
    await fake.close();
  }
});

// ══════════════════════════════════════════════════════════════
group("markdown 格式化");

await test("空结果给出明确说法（不是空字符串）", () => {
  const text = formatSearchResults({ query: "没结果", provider: "0", results: [] });
  assert.match(text, /No results found/u);
  assert.match(text, /没结果/u);
});

await test("结果渲染成标题 + 链接 + 摘要 + 来源", () => {
  const text = formatSearchResults({
    query: "q",
    provider: "0",
    results: [{ title: "标题A", url: "https://a.com", snippet: "摘要A", site: "a.com" }],
  });
  assert.match(text, /## 1\. \[标题A\]\(https:\/\/a\.com\)/u);
  assert.match(text, /摘要A/u);
  assert.match(text, /Found 1 result/u);
});

await test("复数正确（1 result vs 2 results）", () => {
  const one = formatSearchResults({ query: "q", provider: "0", results: [{ url: "a" }] });
  const two = formatSearchResults({ query: "q", provider: "0", results: [{ url: "a" }, { url: "b" }] });
  assert.match(one, /1 result\b/u);
  assert.match(two, /2 results/u);
});

// ══════════════════════════════════════════════════════════════
group("网关路径（真起一个网关，假上游）");

await test("POST /v1/search 默认回 markdown", async () => {
  const upstream = await startFake((req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(searchPayload([{ title: "T", url: "https://a.com", snippet: "S" }]));
  });
  try {
    const { createGateway } = await import("../src/gateway.js");
    const gateway = createGateway({
      variant: { id: "workbuddy", region: "cn", chatBase: upstream.base, billingBase: upstream.base, catalogPath: "/v3/config" },
      client: {},
      store: { resolve: async () => ({ accessToken: "t", uid: "u", domain: "copilot.tencent.com" }) },
      catalog: { current: () => [], base: () => [] },
      apiKey: "sk",
      log: () => {},
    });
    const listening = await gateway.listen(0, "127.0.0.1");
    const port = typeof listening === "object" && listening !== null ? listening.port : listening;
    try {
      const response = await fetch(`http://127.0.0.1:${port}/v1/search`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: "Bearer sk" },
        body: JSON.stringify({ query: "测试查询" }),
      });
      assert.equal(response.status, 200);
      assert.match(response.headers.get("content-type") ?? "", /text\/plain/u);
      const text = await response.text();
      assert.match(text, /## 1\. \[T\]/u);
    } finally {
      await gateway.close();
    }
  } finally {
    await upstream.close();
  }
});

await test("POST /v1/search format=json 回结构化", async () => {
  const upstream = await startFake((req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(searchPayload([{ title: "T", url: "https://a.com" }]));
  });
  try {
    const { createGateway } = await import("../src/gateway.js");
    const gateway = createGateway({
      variant: { id: "workbuddy", region: "cn", chatBase: upstream.base, billingBase: upstream.base, catalogPath: "/v3/config" },
      client: {},
      store: { resolve: async () => ({ accessToken: "t", uid: "u", domain: "copilot.tencent.com" }) },
      catalog: { current: () => [], base: () => [] },
      apiKey: "sk",
      log: () => {},
    });
    const listening = await gateway.listen(0, "127.0.0.1");
    const port = typeof listening === "object" && listening !== null ? listening.port : listening;
    try {
      const response = await fetch(`http://127.0.0.1:${port}/v1/search`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: "Bearer sk" },
        body: JSON.stringify({ query: "测试查询", format: "json" }),
      });
      const body = await response.json();
      assert.equal(body.object, "search_result");
      assert.equal(body.results[0].title, "T");
    } finally {
      await gateway.close();
    }
  } finally {
    await upstream.close();
  }
});

await test("参数校验失败回 400 且是 OpenAI 错误形状", async () => {
  const upstream = await startFake((req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(searchPayload([]));
  });
  try {
    const { createGateway } = await import("../src/gateway.js");
    const gateway = createGateway({
      variant: { id: "workbuddy", region: "cn", chatBase: upstream.base, billingBase: upstream.base, catalogPath: "/v3/config" },
      client: {},
      store: { resolve: async () => ({ accessToken: "t", uid: "u", domain: "copilot.tencent.com" }) },
      catalog: { current: () => [], base: () => [] },
      apiKey: "sk",
      log: () => {},
    });
    const listening = await gateway.listen(0, "127.0.0.1");
    const port = typeof listening === "object" && listening !== null ? listening.port : listening;
    try {
      const response = await fetch(`http://127.0.0.1:${port}/v1/search`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: "Bearer sk" },
        body: JSON.stringify({ query: "a" }),
      });
      assert.equal(response.status, 400);
      const body = await response.json();
      assert.equal(body.error.type, "invalid_request_error");
    } finally {
      await gateway.close();
    }
  } finally {
    await upstream.close();
  }
});

// ══════════════════════════════════════════════════════════════
process.stdout.write(`${results.join("\n")}\n`);
process.stdout.write(`\n${failed === 0 ? "ALL GREEN" : "FAILURES"} — ${results.filter((line) => line.includes("PASS")).length} passed, ${failed} failed\n`);
process.exit(failed === 0 ? 0 : 1);
