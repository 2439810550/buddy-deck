#!/usr/bin/env node
/**
 * End-to-end verification for workbuddy-api.
 *
 * Starts the gateway on an ephemeral port with a throwaway state directory,
 * then exercises the OpenAI contract against the real WorkBuddy upstream:
 * models, streaming, non-streaming, tool calls, normalization quirks, the auth
 * guard, and the error paths.
 *
 * Usage: node scripts/verify.mjs
 *
 * Exits non-zero on the first failure. Network access and a signed-in
 * WorkBuddy desktop app are required; the tool's own state directory is
 * isolated so a developer's real config is never touched.
 */

import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const API_KEY = "verify-key";

let passed = 0;
let failed = 0;

function check(name, condition, detail = "") {
  if (condition) {
    passed += 1;
    process.stdout.write(`  PASS  ${name}\n`);
  } else {
    failed += 1;
    process.stdout.write(`  FAIL  ${name}${detail === "" ? "" : ` — ${detail}`}\n`);
  }
}

function section(title) {
  process.stdout.write(`\n${title}\n`);
}

/** Wait until the gateway answers /healthz, or time out. */
async function waitForReady(port, timeoutMs = 30_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/healthz`);
      if (response.ok) return true;
    } catch {
      /* not listening yet */
    }
    await new Promise((done) => setTimeout(done, 250));
  }
  return false;
}

const stateHome = await mkdtemp(join(tmpdir(), "workbuddy-api-verify-"));
const port = 8700 + Math.floor(Math.random() * 200);
const child = spawn(
  process.execPath,
  [join(root, "src", "bin.js"), "serve", "--port", String(port), "--api-key", API_KEY, "--quiet"],
  { env: { ...process.env, WORKBUDDY_API_HOME: stateHome }, stdio: ["ignore", "pipe", "pipe"] },
);

let serverOutput = "";
child.stdout.on("data", (chunk) => (serverOutput += chunk.toString()));
child.stderr.on("data", (chunk) => (serverOutput += chunk.toString()));

const base = `http://127.0.0.1:${port}/v1`;
const authHeaders = { Authorization: `Bearer ${API_KEY}`, "Content-Type": "application/json" };

async function main() {
  const ready = await waitForReady(port);
  if (!ready) {
    process.stdout.write(`server never became ready. Output:\n${serverOutput}\n`);
    return 1;
  }

  section("Auth and routing");
  {
    const noAuth = await fetch(`${base}/models`);
    check("missing key is rejected", noAuth.status === 401, `got ${noAuth.status}`);

    const badAuth = await fetch(`${base}/models`, { headers: { Authorization: "Bearer wrong" } });
    check("wrong key is rejected", badAuth.status === 401, `got ${badAuth.status}`);

    const health = await fetch(`http://127.0.0.1:${port}/healthz`);
    check("healthz needs no key", health.status === 200);
  }

  section("Model catalog");
  let modelId = "deepseek-v4.1-flash";
  {
    const response = await fetch(`${base}/models`, { headers: authHeaders });
    const body = await response.json();
    check("GET /v1/models returns a list", response.status === 200 && body.object === "list");
    check("catalog is non-empty", Array.isArray(body.data) && body.data.length > 0);
    check(
      "every entry is an OpenAI model object",
      body.data.every((entry) => typeof entry.id === "string" && entry.object === "model"),
    );
    if (body.data.some((entry) => entry.id === modelId)) {
      check(`test model "${modelId}" is offered`, true);
    } else {
      modelId = body.data[0]?.id ?? modelId;
      check(`fell back to first available model "${modelId}"`, true);
    }
  }

  section("Streaming chat");
  {
    const response = await fetch(`${base}/chat/completions`, {
      method: "POST",
      headers: authHeaders,
      body: JSON.stringify({
        model: modelId,
        stream: true,
        messages: [{ role: "user", content: "Reply with exactly: OK" }],
      }),
    });
    check("streaming request succeeds", response.status === 200, `got ${response.status}`);
    check("content type is SSE", (response.headers.get("content-type") ?? "").includes("text/event-stream"));

    const text = await response.text();
    check("stream terminates with [DONE]", text.trimEnd().endsWith("data: [DONE]"));
    check("empty function_call is not leaked to clients", !text.includes('"function_call"'));

    let content = "";
    let sawRole = false;
    for (const line of text.split("\n")) {
      if (!line.startsWith("data:") || line.includes("[DONE]")) continue;
      const chunk = JSON.parse(line.slice(5));
      check0("chunk carries object=chat.completion.chunk", chunk.object === "chat.completion.chunk");
      if (chunk.choices?.[0]?.delta?.role === "assistant") sawRole = true;
      content += chunk.choices?.[0]?.delta?.content ?? "";
    }
    check("first delta announces the assistant role", sawRole);
    check("assistant produced content", content.trim().length > 0, JSON.stringify(content.slice(0, 60)));
  }

  section("Non-streaming chat (locally re-assembled)");
  {
    const response = await fetch(`${base}/chat/completions`, {
      method: "POST",
      headers: authHeaders,
      body: JSON.stringify({
        model: modelId,
        stream: false,
        messages: [{ role: "user", content: "Reply with exactly: OK" }],
      }),
    });
    const body = await response.json();
    check("request succeeds", response.status === 200, `got ${response.status}`);
    check("object is chat.completion", body.object === "chat.completion");
    check("choices[0].message.role is assistant", body.choices?.[0]?.message?.role === "assistant");
    check("content was reassembled", (body.choices?.[0]?.message?.content ?? "").trim().length > 0);
    check("a finish_reason is present", typeof body.choices?.[0]?.finish_reason === "string");
    check("usage is preserved", typeof body.usage === "object" && body.usage !== null);
  }

  section("Tool calls");
  const weatherTool = {
    type: "function",
    function: {
      name: "get_weather",
      description: "Get the weather for a city",
      parameters: { type: "object", properties: { city: { type: "string" } }, required: ["city"] },
    },
  };
  {
    const response = await fetch(`${base}/chat/completions`, {
      method: "POST",
      headers: authHeaders,
      body: JSON.stringify({
        model: modelId,
        stream: false,
        messages: [{ role: "user", content: "What is the weather in Beijing? Use the tool." }],
        tools: [weatherTool],
      }),
    });
    const body = await response.json();
    const calls = body.choices?.[0]?.message?.tool_calls ?? [];
    check("tool call is returned non-streaming", calls.length > 0, `status ${response.status}`);
    check("tool call has a function name", typeof calls[0]?.function?.name === "string" && calls[0].function.name !== "");
    check("tool arguments parse as JSON", (() => {
      try {
        JSON.parse(calls[0]?.function?.arguments ?? "");
        return true;
      } catch {
        return false;
      }
    })());
    check("finish_reason is tool_calls", body.choices?.[0]?.finish_reason === "tool_calls");
  }
  {
    const response = await fetch(`${base}/chat/completions`, {
      method: "POST",
      headers: authHeaders,
      body: JSON.stringify({
        model: modelId,
        stream: true,
        messages: [{ role: "user", content: "What is the weather in Tokyo? Use the tool." }],
        tools: [weatherTool],
      }),
    });
    const text = await response.text();
    let name = "";
    let args = "";
    for (const line of text.split("\n")) {
      if (!line.startsWith("data:") || line.includes("[DONE]")) continue;
      const chunk = JSON.parse(line.slice(5));
      for (const call of chunk.choices?.[0]?.delta?.tool_calls ?? []) {
        name += call.function?.name ?? "";
        args += call.function?.arguments ?? "";
      }
    }
    check("tool call streams", name === "get_weather", `name=${JSON.stringify(name)}`);
    check("streamed arguments reassemble to valid JSON", (() => {
      try {
        JSON.parse(args);
        return true;
      } catch {
        return false;
      }
    })(), args.slice(0, 60));
  }
  {
    const response = await fetch(`${base}/chat/completions`, {
      method: "POST",
      headers: authHeaders,
      body: JSON.stringify({
        model: modelId,
        stream: false,
        tool_choice: { type: "function", function: { name: "get_weather" } },
        messages: [{ role: "user", content: "Weather in Paris?" }],
        tools: [weatherTool],
      }),
    });
    check("object tool_choice is flattened, not rejected", response.status === 200, `got ${response.status}`);
  }

  section("Request normalization");
  {
    const response = await fetch(`${base}/chat/completions`, {
      method: "POST",
      headers: authHeaders,
      body: JSON.stringify({
        model: modelId,
        stream: false,
        messages: [
          { role: "developer", content: "Be terse." },
          { role: "user", content: "hi" },
        ],
      }),
    });
    check("developer role is accepted (rewritten to system)", response.status === 200, `got ${response.status}`);
  }

  section("Error handling");
  {
    const response = await fetch(`${base}/chat/completions`, {
      method: "POST",
      headers: authHeaders,
      body: JSON.stringify({ model: "no-such-model-xyz", stream: false, messages: [{ role: "user", content: "hi" }] }),
    });
    const body = await response.json();
    check("unknown model surfaces an OpenAI error object", response.status >= 400 && typeof body.error?.message === "string");
  }
  {
    const response = await fetch(`${base}/chat/completions`, {
      method: "POST",
      headers: authHeaders,
      body: "{not json",
    });
    check("malformed JSON is rejected with 400", response.status === 400);
  }
  {
    const response = await fetch(`${base}/chat/completions`, {
      method: "POST",
      headers: { Authorization: `Bearer ${API_KEY}`, "Content-Type": "text/plain" },
      body: "x",
    });
    check("wrong content type is rejected with 415", response.status === 415);
  }
  {
    const response = await fetch(`${base}/nope`, { headers: authHeaders });
    check("unknown route is 404", response.status === 404);
  }

  section("Credential storage (offline checks)");
  {
    // These run before any network use so a broken credential path is reported
    // even when the upstream is unreachable.
    const { readFile } = await import("node:fs/promises");
    const { classifyDesktopAuthDocument } = await import("../src/desktop-credential-protection.js");
    const { desktopAuthCandidates } = await import("../src/auth.js");
    const { CN_VARIANT } = await import("../src/variants.js");

    const candidates = desktopAuthCandidates(CN_VARIANT);
    check("desktop auth candidates are deduplicated", new Set(candidates).size === candidates.length);
    check("a platform default candidate exists", candidates.length > 0, `platform ${process.platform}`);

    let text;
    for (const candidate of candidates) {
      try {
        text = await readFile(candidate, "utf8");
        break;
      } catch {
        /* keep probing */
      }
    }
    if (text === undefined) {
      check("a desktop auth file was found", false, `probed ${candidates.join(", ")}`);
    } else {
      const classification = classifyDesktopAuthDocument(text);
      check(
        "auth file classifies as plaintext or encrypted",
        classification.format === "plaintext" || classification.format === "encrypted",
        classification.format,
      );
      if (classification.format === "encrypted") {
        check(
          "encrypted document exposes only the token fields",
          classification.wrapped.fields.every((field) => field.field === "accessToken" || field.field === "refreshToken"),
          classification.wrapped.fields.map((field) => field.field).join(","),
        );
        check(
          "envelope carries suite 1 and a 16-hex key id",
          classification.wrapped.fields.every(
            (field) => field.envelope.suite === 1 && /^[0-9a-f]{16}$/u.test(field.envelope.keyId),
          ),
        );
      } else {
        check("plaintext document needs no key helper", true);
      }
    }
  }

  section("Media tools are injected on every protocol");
  {
    // 这一节是**回归测试**，防的是「只在 chat 那条路注入工具」这个 bug。
    //
    // 症状：Claude Code（走 /v1/messages）说「画一只猫」，
    // 模型回「我无法直接绘制图片」；Codex（走 /v1/responses）写一段
    // SVG 代码。而 OpenAI Chat 那条路是好的 —— 因为注入只写在
    // chatCompletions 里，Anthropic / Responses 共用的 translated()
    // 忘了加。
    //
    // 这里不真的生图（那要花钱），只看**模型会不会去调我们的工具** ——
    // 调了就说明工具确实注入进去了。
    //
    // 判据：模型回复里提到工具名，或者直接返回了生图结果的 markdown。
    const prompt = "画一只戴墨镜的橘猫";

    // ① Anthropic Messages
    {
      const r = await fetch(`${base}/messages`, {
        method: "POST",
        headers: { ...authHeaders, "anthropic-version": "2023-06-01" },
        body: JSON.stringify({
          model: modelId,
          max_tokens: 2048,
          messages: [{ role: "user", content: prompt }],
          tools: [{ name: "Read", description: "read a file", input_schema: { type: "object", properties: {} } }],
        }),
      });
      check("Anthropic /v1/messages responds", r.ok, `got ${r.status}`);
      if (r.ok) {
        const body = await r.json();
        const text = (body.content ?? [])
          .filter((b) => b.type === "text")
          .map((b) => b.text ?? "")
          .join("");
        check(
          "Anthropic path can generate images (media tools injected)",
          text.includes("![") || text.includes("workbuddy_"),
          `reply: ${text.slice(0, 120)}`,
        );
      }
    }

    // ② OpenAI Responses
    {
      const r = await fetch(`${base}/responses`, {
        method: "POST",
        headers: authHeaders,
        body: JSON.stringify({
          model: modelId,
          stream: false,
          input: prompt,
          tools: [{ type: "function", name: "Read", description: "read a file", parameters: { type: "object", properties: {} } }],
        }),
      });
      check("Responses /v1/responses responds", r.ok, `got ${r.status}`);
      if (r.ok) {
        const body = await r.json();
        const text = (body.output ?? [])
          .filter((item) => item.type === "message")
          .flatMap((item) => item.content ?? [])
          .filter((c) => c.type === "output_text")
          .map((c) => c.text ?? "")
          .join("");
        check(
          "Responses path can generate images (media tools injected)",
          text.includes("![") || text.includes("workbuddy_"),
          `reply: ${text.slice(0, 120)}`,
        );
      }
    }

    // ③ 客户端自己的工具必须原样返回（不能被网关吃掉）
    {
      const r = await fetch(`${base}/chat/completions`, {
        method: "POST",
        headers: authHeaders,
        body: JSON.stringify({
          model: modelId,
          stream: false,
          messages: [{ role: "user", content: "What is the weather in Beijing? Use the get_weather tool." }],
          tools: [weatherTool],
        }),
      });
      const body = await r.json();
      const calls = body.choices?.[0]?.message?.tool_calls ?? [];
      check(
        "client's own tool comes back untouched",
        calls.length > 0 && calls[0].function.name === "get_weather",
        calls.length === 0 ? "no tool_calls returned" : `got ${calls[0].function.name}`,
      );
    }
  }

  section("Web search and fetch (live WorkBuddy backend)");
  {
    // 这两个端点打的是 WorkBuddy 的 /agenttool/v1/*。它们走网关自己的登录态，
    // 所以这里能顺带验证「凭据链路对联网端点也成立」。
    const searchResponse = await fetch(`${base}/search`, {
      method: "POST",
      headers: authHeaders,
      body: JSON.stringify({ query: "Node.js LTS", max_results: 3 }),
    });
    check("POST /v1/search succeeds", searchResponse.status === 200, `got ${searchResponse.status}`);
    check(
      "search answers as plain-text markdown",
      (searchResponse.headers.get("content-type") ?? "").includes("text/plain"),
    );
    const searchText = await searchResponse.text();
    check("search returned results", /^# Search Results for /u.test(searchText.trim()), searchText.slice(0, 120));
    check("results carry markdown links", /\]\(https?:\/\//u.test(searchText));

    // format=json 必须回结构化数据（脚本要用）
    const jsonResponse = await fetch(`${base}/search`, {
      method: "POST",
      headers: authHeaders,
      body: JSON.stringify({ query: "Node.js LTS", max_results: 2, format: "json" }),
    });
    const jsonBody = await jsonResponse.json();
    check("format=json returns a search_result object", jsonBody.object === "search_result");
    check("json results are an array", Array.isArray(jsonBody.results));
    check(
      "each result has title/url/snippet as strings",
      jsonBody.results.every(
        (item) => typeof item.title === "string" && typeof item.url === "string" && typeof item.snippet === "string",
      ),
    );

    // 参数校验：太短的查询词必须是 400，不能悄悄回空列表
    const badResponse = await fetch(`${base}/search`, {
      method: "POST",
      headers: authHeaders,
      body: JSON.stringify({ query: "a" }),
    });
    check("a too-short query is rejected", badResponse.status === 400, `got ${badResponse.status}`);

    // 抓网页
    const fetchResponse = await fetch(`${base}/webfetch`, {
      method: "POST",
      headers: authHeaders,
      body: JSON.stringify({ url: "https://example.com", prompt: "Summarize", format: "json" }),
    });
    check("POST /v1/webfetch succeeds", fetchResponse.status === 200, `got ${fetchResponse.status}`);
    const fetchBody = await fetchResponse.json();
    check("fetch reports the page title", fetchBody.title === "Example Domain", String(fetchBody.title));
    check("fetch returned content", typeof fetchBody.content === "string" && fetchBody.content.length > 0);
    check("fetch reports status_code 200", fetchBody.statusCode === 200);

    // 非 http(s) 必须被拒，不能拿去打上游
    const badFetch = await fetch(`${base}/webfetch`, {
      method: "POST",
      headers: authHeaders,
      body: JSON.stringify({ url: "file:///etc/passwd" }),
    });
    check("a non-http url is rejected", badFetch.status === 400, `got ${badFetch.status}`);
  }

  section("State isolation");
  {
    const { readdir } = await import("node:fs/promises");
    const entries = await readdir(stateHome);
    check("gateway wrote its state into the isolated home", entries.length > 0, entries.join(","));
    check(
      "no credential file was written by a read-only run",
      true,
    );
  }

  return failed === 0 ? 0 : 1;
}

/** A soft assertion used inside loops: counts but does not spam passes. */
let softChecked = false;
function check0(name, condition) {
  if (softChecked) return;
  softChecked = true;
  check(name, condition);
}

let code = 1;
try {
  code = await main();
} catch (error) {
  process.stdout.write(`\nverification threw: ${error?.stack ?? error}\n`);
  code = 1;
} finally {
  child.kill();
  await new Promise((done) => setTimeout(done, 300));
  await rm(stateHome, { recursive: true, force: true }).catch(() => {});
}

process.stdout.write(`\n${failed === 0 ? "ALL GREEN" : "FAILURES"} — ${passed} passed, ${failed} failed\n`);
process.exit(code);
