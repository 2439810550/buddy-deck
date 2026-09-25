#!/usr/bin/env node
/**
 * 用量账本的单元测试。
 *
 * 为什么单独测这个模块，不并进 verify.mjs：
 *
 * verify.mjs 打的是真上游（要联网、要登录、会花钱），
 * 而这个模块是纯计算 + 本地写盘。混在一起的话，
 * 「账本逻辑坏了」和「网络不通了」会报同样的红，排查要绕一圈。
 *
 * 用法：node scripts/test-usage.mjs
 */
import { mkdtemp, rm, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

let passed = 0;
let failed = 0;

function check(name, ok, detail = "") {
  if (ok) {
    process.stdout.write(`  ✓ ${name}\n`);
    passed += 1;
  } else {
    process.stdout.write(`  ✗ ${name}  ${detail}\n`);
    failed += 1;
  }
}

// 用独立的 state 目录，绝不碰开发者的真账本
const home = await mkdtemp(join(tmpdir(), "wb-usage-test-"));
process.env["WORKBUDDY_API_HOME"] = home;

const { recordUsage, summarizeUsage, parseLedger, aggregate, ledgerPath } =
  await import("../src/usage.js");

// ── summarizeUsage ───────────────────────────────────────────
process.stdout.write("\nsummarizeUsage\n");

const full = summarizeUsage({
  prompt_tokens: 100, completion_tokens: 50, total_tokens: 150,
  credit: 0.05,
  prompt_cache_hit_tokens: 80, prompt_cache_miss_tokens: 20,
  prompt_cache_write_tokens: 5, cache_read_input_tokens: 80,
  completion_thinking_tokens: 12,
});

check("保留 credit（上游给的真实扣费）", full.credit === 0.05, `得到 ${full.credit}`);
check("保留 promptTokens", full.promptTokens === 100, `得到 ${full.promptTokens}`);
check("保留 completionTokens", full.completionTokens === 50, `得到 ${full.completionTokens}`);
check("保留缓存命中", full.cacheHit === 80, `得到 ${full.cacheHit}`);
check("保留缓存未命中", full.cacheMiss === 20, `得到 ${full.cacheMiss}`);
check("保留缓存写入", full.cacheWrite === 5, `得到 ${full.cacheWrite}`);
check("保留推理 token", full.reasoning === 12, `得到 ${full.reasoning}`);

check("容忍 undefined", typeof summarizeUsage(undefined) === "object", "");
check("容忍 null", typeof summarizeUsage(null) === "object", "");
// 字段集合是**固定**的（值为 undefined 也不省略）——
// 这样下游聚合时不用到处判 key 存不存在，直接读就行。
check("空对象返回固定字段集合",
  Object.keys(summarizeUsage({})).length === 9,
  `得到 ${Object.keys(summarizeUsage({})).length} 个键`);
check("空对象的字段值都是 undefined",
  Object.values(summarizeUsage({})).every((v) => v === undefined),
  "");
check("丢掉非数字的 credit",
  summarizeUsage({ credit: "abc" }).credit === undefined,
  `得到 ${summarizeUsage({ credit: "abc" }).credit}`);
check("丢掉 NaN",
  summarizeUsage({ credit: NaN }).credit === undefined,
  `得到 ${summarizeUsage({ credit: NaN }).credit}`);
check("0 是合法值（免费模型）",
  summarizeUsage({ credit: 0 }).credit === 0,
  `得到 ${summarizeUsage({ credit: 0 }).credit}`);

// ── parseLedger ──────────────────────────────────────────────
process.stdout.write("\nparseLedger\n");

check("解析正常行",
  parseLedger('{"model":"a","credit":1}\n{"model":"b","credit":2}\n').length === 2, "");
check("跳过空行",
  parseLedger('\n\n{"model":"a"}\n\n').length === 1, "");
check("跳过坏 JSON",
  parseLedger('{"model":"a"}\nnot json\n{"model":"b"}\n').length === 2, "");
check("跳过半截行（写入中）",
  parseLedger('{"model":"a"}\n{"model":"b",').length === 1, "");
check("空文本返回空数组", parseLedger("").length === 0, "");

// ── aggregate ────────────────────────────────────────────────
process.stdout.write("\naggregate\n");

const rows = [
  { model: "cheap", credit: 1, promptTokens: 1000, completionTokens: 0,
    cacheHit: 0, cacheMiss: 1000, account: "aaa", outcome: "ok" },
  { model: "cheap", credit: 3, promptTokens: 1000, completionTokens: 0,
    cacheHit: 1000, cacheMiss: 0, account: "aaa", outcome: "ok" },
  { model: "free", credit: 0, promptTokens: 500, completionTokens: 500,
    cacheHit: 0, cacheMiss: 0, account: "bbb", outcome: "ok" },
  { model: "cheap", credit: 0, promptTokens: 0, completionTokens: 0,
    cacheHit: 0, cacheMiss: 0, account: "aaa", outcome: "failed" },
];

const agg = aggregate(rows);

check("统计调用数", agg.totals.calls === 4, `得到 ${agg.totals.calls}`);
check("统计失败数", agg.totals.failures === 1, `得到 ${agg.totals.failures}`);
check("汇总积分", Math.abs(agg.totals.credit - 4) < 1e-9, `得到 ${agg.totals.credit}`);
check("汇总 tokens", agg.totals.tokens === 3000, `得到 ${agg.totals.tokens}`);
check("算缓存命中率", Math.abs(agg.totals.cacheHitRate - 0.5) < 1e-9,
  `得到 ${agg.totals.cacheHitRate}`);
check("算 tokens/积分", agg.totals.tokensPerCredit === 750,
  `得到 ${agg.totals.tokensPerCredit}`);
check("按模型分组", agg.byModel.length === 2, `得到 ${agg.byModel.length}`);
check("按账号分组", agg.byAccount.length === 2, `得到 ${agg.byAccount.length}`);
check("按积分降序",
  agg.byModel[0].model === "cheap", `第一个是 ${agg.byModel[0].model}`);
check("免费模型 tokensPerCredit 为 null（不是除零）",
  agg.byModel.find((m) => m.model === "free").tokensPerCredit === null, "");

// ── recordUsage 真写盘 ───────────────────────────────────────
process.stdout.write("\nrecordUsage 写盘\n");

check("账本路径在 state 目录下",
  ledgerPath().startsWith(home), ledgerPath());

await recordUsage({
  model: "test-model",
  usedModel: "test-model",
  kind: "chat",
  account: "abc12345",
  usage: {
    prompt_tokens: 10, completion_tokens: 20, total_tokens: 30,
    credit: 0.042,
    prompt_cache_hit_tokens: 5, prompt_cache_miss_tokens: 5,
  },
  outcome: "ok",
  attempts: 1,
  elapsedMs: 123,
});

const text = await readFile(ledgerPath(), "utf8");
const written = parseLedger(text);

check("写了一条", written.length === 1, `得到 ${written.length}`);
check("credit 写进去了", written[0]?.credit === 0.042, `得到 ${written[0]?.credit}`);
check("缓存命中写进去了", written[0]?.cacheHit === 5, `得到 ${written[0]?.cacheHit}`);
check("账号写进去了", written[0]?.account === "abc12345", `得到 ${written[0]?.account}`);
check("耗时写进去了", written[0]?.elapsedMs === 123, `得到 ${written[0]?.elapsedMs}`);
check("带时间戳", typeof written[0]?.ts === "number", "");

// 再写一条，确认是追加不是覆盖
await recordUsage({ model: "second", usage: { credit: 0.01 }, outcome: "ok" });
const text2 = await readFile(ledgerPath(), "utf8");
check("追加而不是覆盖",
  parseLedger(text2).length === 2, `得到 ${parseLedger(text2).length}`);

// 坏数据不该抛
let threw = false;
try {
  await recordUsage({ model: "x", usage: null, outcome: "ok" });
  await recordUsage({ model: "y", usage: "not-an-object", outcome: "ok" });
} catch {
  threw = true;
}
check("坏 usage 不抛异常", !threw, "");

// 写盘失败的路径不该抛（指向一个不存在的盘符）
const goodHome = process.env["WORKBUDDY_API_HOME"];
process.env["WORKBUDDY_API_HOME"] = "Z:\\definitely-not-a-real-drive\\x";
let threw2 = false;
try {
  await recordUsage({ model: "z", usage: { credit: 1 }, outcome: "ok" });
} catch {
  threw2 = true;
}
check("写盘失败不抛异常", !threw2, "");
process.env["WORKBUDDY_API_HOME"] = goodHome;

// ── 收尾 ─────────────────────────────────────────────────────
await rm(home, { recursive: true, force: true });

process.stdout.write(`\n通过 ${passed} / ${passed + failed}\n`);
if (failed > 0) process.exit(1);
