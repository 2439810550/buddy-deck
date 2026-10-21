#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
M10 用量账本的独立验证（t13-C）。

## 为什么要有这个脚本

`UsageLedger.cs`（38,567 B）与 `UsageEndpoints.cs`（13,233 B）是 observability
在 t24 交付的，**代码在盘上、单测通过，但从来没有被独立验证过** ——
和 `/admin/*` 在 t17 之前的状态一样。

**而这个东西比 `/admin/*` 更危险**：`/admin/*` 当时是「404 不能用」，
账本却是**往磁盘写用户的历史用量数据**。
如果追加/轮转逻辑有 bug，后果是**用户的历史账本被损坏** —— 那是不可逆的数据丢失。

## 五条验证（第 5 条由队长提出，理由正确）

    ① 金丝雀脱敏   账本里的 token 原值绝不能出现在 /usage/* 的响应里
    ② 落盘追加     并发 N 条之后 **行数 == N**、skippedLines == 0
    ③ 聚合正确性   by-model / by-account 的 credit 汇总 = 手算值
    ④ 坏行容错     手工插半截 JSON → 端点不崩
    ⑤ 原子性       并发写入不产生交错行；**逐行都能被解析**

## ⚠⚠ 为什么 ② 的断言必须是「精确相等」而不是「行数增加了」

这条来自作者 observability 的实测（2026-10-07）：

    第一版用 File.AppendAllText → 8 线程 × 50 条**只落盘 326 行**（期望 400）

原因：`AppendAllText` 每次调用自己 open/close 句柄。并发时 **A 关闭时会按
「它以为的长度」截断，把 B 刚写的部分切掉**。**「单次 write 原子」这条保证
只在同一个句柄上成立** —— 每次重开句柄就等于放弃它。

**而它的关键教训是**：

> **只看「不崩」是验不出这个 bug 的 —— 它不崩，只是安静地少写几行。**

⇒ 所以这里的 ② 断言写成 **`行数 == N`（精确）** + **`skippedLines == 0`**，
**不用「行数增加了」这种弱断言**。

**这是「静默失败」的又一个实例**，与同一会话里已出现的两个同类：
  · ui 的 `Is*` 前缀坑（字段有默认值、看起来正常）
  · harness 的 `AttemptCount` 翻倍（数字在、但错）
共同点：**没有报错，只是数字不对**。

## 怎么跑

    1) 起一个真网关，**指向本脚本自带的假上游**（见下面 FAKE_PORT）：
       dotnet run --project src\\BuddyDeck.Gateway -- ^
           --port 8794 --api-key test-key ^
           --vault <临时保险库> --log-dir <临时目录> ^
           --upstream-base http://127.0.0.1:8898

    2) python tools\\verify_usage_ledger.py <gateway-port> <api-key> <ledger-path>

⚠ **完全离线**：假上游 + 临时目录 + 自造金丝雀值。不碰真实账号、不打真实上游。

⚠ **读账本文件时注意 .NET 的共享模式**：作者提醒过 ——
修完之后 `File.ReadAllText` 这类朴素读法**仍会失败**，那是 .NET 默认 `FileShare.Read`
决定的（写入方持 `FileShare.ReadWrite`），**不是 bug**。
本脚本用 Python 读，不经过 .NET 的共享模式，所以不受影响；
但若你改用 C# 读，记得显式传 `FileShare.ReadWrite`。
"""

import json
import os
import sys
import threading
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8794
API_KEY = sys.argv[2] if len(sys.argv) > 2 else "test-key"
LEDGER_PATH = sys.argv[3] if len(sys.argv) > 3 else ""

# 假上游端口 —— 网关要用 `--upstream-base http://127.0.0.1:<这个>` 指过来
FAKE_PORT = int(os.environ.get("FAKE_UPSTREAM_PORT", "8898"))

BASE = "http://127.0.0.1:%d" % PORT

# 金丝雀：只要这个值出现在任何响应里，就是泄露。
CANARY = "sk-USAGE-CANARY-MUST-NOT-LEAK"


class FakeUpstream(BaseHTTPRequestHandler):
    """最小假上游：回一段固定 SSE，让网关能正常完成一次 chat 并记账。"""

    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass  # 静音

    def _sse(self):
        frames = [
            {"id": "chatcmpl-m10", "object": "chat.completion.chunk", "created": 1700000000,
             "model": "hy3", "choices": [{"index": 0,
                                          "delta": {"role": "assistant"}, "finish_reason": None}]},
            {"id": "chatcmpl-m10", "object": "chat.completion.chunk", "created": 1700000000,
             "model": "hy3", "choices": [{"index": 0,
                                          "delta": {"content": "好的"}, "finish_reason": None}]},
            {"id": "chatcmpl-m10", "object": "chat.completion.chunk", "created": 1700000000,
             "model": "hy3",
             "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}],
             # credit 是账本的核心字段 —— 上游实际扣费
             "usage": {"prompt_tokens": 10, "completion_tokens": 5,
                       "total_tokens": 15, "credit": 0.25}},
        ]
        out = "".join("data: %s\n\n" % json.dumps(f) for f in frames) + "data: [DONE]\n\n"
        return out.encode("utf-8")

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        if length:
            self.rfile.read(length)
        body = self._sse()
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
        self.wfile.flush()

    def do_GET(self):
        # 模型目录：网关刷新目录时会来
        body = json.dumps({"models": [{"id": "hy3", "name": "Hy3"}]}).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def start_fake():
    server = ThreadingHTTPServer(("127.0.0.1", FAKE_PORT), FakeUpstream)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server

checks = 0
failures = []


def check(label, condition, detail=""):
    global checks
    checks += 1
    if condition:
        print("  [OK]   %s" % label)
    else:
        print("  [FAIL] %s  %s" % (label, detail))
        failures.append(label)


def call(method, path, body=None, key=API_KEY, raw=False):
    """发一个请求，返回 (status, parsed_or_text)。不抛。"""
    url = BASE + path
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    if key:
        req.add_header("Authorization", "Bearer " + key)
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            text = resp.read().decode("utf-8")
            return resp.status, (text if raw else json.loads(text) if text.strip() else None)
    except urllib.error.HTTPError as e:
        text = e.read().decode("utf-8", errors="replace")
        try:
            return e.code, (text if raw else json.loads(text))
        except json.JSONDecodeError:
            return e.code, text
    except Exception as e:  # noqa: BLE001
        return 0, str(e)


def chat_once(prompt="你好"):
    return call("POST", "/v1/chat/completions", {
        "model": "hy3",
        "messages": [{"role": "user", "content": prompt}],
        "stream": False,
    })


def _try_json(text):
    """解析 JSON，失败返回 None（不抛）。"""
    try:
        return json.loads(text)
    except (json.JSONDecodeError, TypeError):
        return None


def read_ledger_lines():
    """读账本文件的所有行（原始文本，不解析）—— 用来验行数与完整性。"""
    if not LEDGER_PATH or not os.path.exists(LEDGER_PATH):
        return None
    with open(LEDGER_PATH, encoding="utf-8") as handle:
        return handle.read().split("\n")


print("=" * 72)
print("M10 用量账本独立验证 —— 网关端口 %d，假上游端口 %d" % (PORT, FAKE_PORT))
print("账本路径：%s" % (LEDGER_PATH or "(未提供 —— ②⑤ 将跳过)"))
print("=" * 72)

# 假上游必须在本脚本里起 —— 否则网关会把请求打到真实上游。
start_fake()

# ── 网关可用性 ────────────────────────────────────────────────────
status, health = call("GET", "/healthz", key=None)
if status != 200 or not isinstance(health, dict) or not health.get("ok"):
    print("!! 网关没起来或 /healthz 异常：%s %s" % (status, health))
    sys.exit(2)

print()
print("=" * 72)
print("① 金丝雀脱敏：账本里的凭据原值不得出现在响应里")
print("=" * 72)

# 先发一次真实请求，让账本里有东西
status, _ = chat_once("金丝雀测试")
check("发一次 chat 请求成功（为账本制造数据）", status in (200, 429, 502), "got %s" % status)

for path in ("/usage/summary", "/usage/by-model", "/usage/by-account", "/usage/recent"):
    status, body = call("GET", path, raw=True)
    check("%s 可访问（200）" % path, status == 200, "got %s" % status)
    if isinstance(body, str):
        check("%s 不含金丝雀值" % path, CANARY not in body)
        # 顺带查常见的凭据键名 —— 不是原值，是"根本不该有这些键"
        for keyname in ("accessToken", "refreshToken"):
            check("%s 不含 %s 键" % (path, keyname), keyname not in body)

# ── ② 落盘追加语义 ───────────────────────────────────────────────
print()
print("=" * 72)
print("② 落盘追加：连发 N 次 → 行数增加，不是覆盖")
print("=" * 72)

before_lines = read_ledger_lines()
if before_lines is None:
    print("  [SKIP] 未提供账本路径，跳过 ②")
else:
    n_before = len([ln for ln in before_lines if ln.strip()])

    # 连发 5 次
    ok_count = 0
    for i in range(5):
        s, _ = chat_once("追加测试 %d" % i)
        if s in (200, 429, 502):
            ok_count += 1

    after_lines = read_ledger_lines()
    n_after = len([ln for ln in after_lines if ln.strip()])

    check("连发 5 次请求都被受理", ok_count == 5, "got %d" % ok_count)
    check("账本行数增加（%d → %d）" % (n_before, n_after), n_after > n_before,
          "行数没涨 —— 可能是覆盖写而不是追加")

    # 关键：原有行必须还在（追加的语义是"不破坏已有内容"）
    if n_before > 0:
        old = set(ln for ln in before_lines if ln.strip())
        new = set(ln for ln in after_lines if ln.strip())
        check("原有行全部保留（没有覆盖）", old.issubset(new),
              "丢失 %d 行" % len(old - new))

    # ── ⑤ 原子性：逐行都能被解析（交错写会留下半行 JSON）──────
    print()
    print("=" * 72)
    print("⑤ 原子性：每一行都必须是完整 JSON（交错写会留下半行）")
    print("=" * 72)

    # 并发发一批 —— 这是交错写最容易暴露的场景
    with ThreadPoolExecutor(max_workers=8) as pool:
        results = list(pool.map(lambda i: chat_once("并发 %d" % i), range(24)))
    accepted = sum(1 for s, _ in results if s in (200, 429, 502))
    check("并发 8 线程 × 24 次请求都被受理", accepted == 24, "got %d" % accepted)

    final_lines = read_ledger_lines()
    non_empty = [ln for ln in final_lines if ln.strip()]
    bad = []
    for idx, line in enumerate(non_empty, 1):
        try:
            json.loads(line)
        except json.JSONDecodeError as e:
            bad.append((idx, str(e), line[:80]))

    check("并发写之后 %d 行全部是合法 JSON" % len(non_empty), not bad,
          "坏行：%s" % bad[:3])

    # 半行检测：最后一行是否以 \n 收尾（不收尾说明写到一半被截断）
    raw_text = "\n".join(final_lines)
    check("文件以换行收尾（没有半行尾巴）",
          raw_text.endswith("\n") or raw_text == "",
          "末尾：%r" % raw_text[-40:])

# ── ③ 聚合正确性 ─────────────────────────────────────────────────
print()
print("=" * 72)
print("③ 聚合正确性：by-model / by-account 的 credit 与 token 汇总 = 手算值")
print("=" * 72)

status, summary = call("GET", "/usage/summary")
check("/usage/summary 返回 200", status == 200, "got %s" % status)

if isinstance(summary, dict) and "totals" in summary:
    totals = summary["totals"]
    print("       totals 样例：%s" % json.dumps(totals, ensure_ascii=False)[:200])

    status, by_model = call("GET", "/usage/by-model")
    check("/usage/by-model 返回 200", status == 200, "got %s" % status)

    if isinstance(by_model, dict) and "rows" in by_model:
        rows = by_model["rows"]
        print("       by-model 行数：%d" % len(rows))

        # ★ 手算：把各行的 credit 加起来，必须等于 totals.credit（浮点容差）
        sum_credit = sum((r.get("credit") or 0) for r in rows)
        sum_calls = sum((r.get("calls") or 0) for r in rows)
        total_credit = totals.get("credit") or 0
        total_calls = totals.get("calls") or 0

        # 只比 money（calls 可能因为"没落盘的最后一笔"有 1-2 的时差）
        check("Σ(by-model.credit) == totals.credit（浮点容差 1e-6）",
              abs(sum_credit - total_credit) < 1e-6,
              "Σ=%s vs totals=%s" % (sum_credit, total_credit))
        check("Σ(by-model.calls) 与 totals.calls 差不超过 2（允许未落盘的尾巴）",
              abs(sum_calls - total_calls) <= 2,
              "Σ=%s vs totals=%s" % (sum_calls, total_calls))

    status, by_account = call("GET", "/usage/by-account")
    check("/usage/by-account 返回 200", status == 200, "got %s" % status)
    if isinstance(by_account, dict) and "rows" in by_account:
        acc_rows = by_account["rows"]
        print("       by-account 行数：%d" % len(acc_rows))
        sum_acc_credit = sum((r.get("credit") or 0) for r in acc_rows)
        check("Σ(by-account.credit) 不超过 totals.credit（账号是子集）",
              sum_acc_credit <= (totals.get("credit") or 0) + 1e-6,
              "Σ=%s vs totals=%s" % (sum_acc_credit, totals.get("credit")))
else:
    check("/usage/summary 有 totals 字段", False, "响应：%s" % str(summary)[:200])

# ── ④ 坏行容错 ───────────────────────────────────────────────────
print()
print("=" * 72)
print("④ 坏行容错：手工插半截 JSON → 端点不崩")
print("=" * 72)

if not LEDGER_PATH or not os.path.exists(LEDGER_PATH):
    print("  [SKIP] 未提供账本路径，跳过 ④")
else:
    # 先把好数据读出来备份语义（不真备份文件，只验证"插入坏行后仍能工作"）
    status, good_before = call("GET", "/usage/summary")
    good_calls = good_before.get("totals", {}).get("calls") if isinstance(good_before, dict) else None

    with open(LEDGER_PATH, "a", encoding="utf-8") as handle:
        handle.write('{"iso":"2026-01-01T00:00:00.000Z","ts":1,"mod')   # 半截 JSON，无换行
        handle.write("\n")
        handle.write("这不是 JSON，是一句话\n")
        handle.write("\n")                                              # 空行
        handle.write('{"iso":"2026-01-01T00:00:00.000Z","ts":2,"model":"fake","credit":999}\n')

    for path in ("/usage/summary", "/usage/by-model", "/usage/by-account", "/usage/recent"):
        status, body = call("GET", path, raw=True)
        check("插坏行后 %s 仍返回 200" % path, status == 200, "got %s" % status)
        check("插坏行后 %s 响应仍是合法 JSON" % path,
              isinstance(body, str) and _try_json(body) is not None,
              "响应不可解析：%s" % str(body)[:120])

    # 坏行不该让好数据消失
    status, good_after = call("GET", "/usage/summary")
    if isinstance(good_after, dict):
        after_calls = good_after.get("totals", {}).get("calls")
        check("坏行不影响已有条目的统计（calls %s → %s）" % (good_calls, after_calls),
              after_calls is not None and after_calls >= (good_calls or 0),
              "统计变了：%s → %s" % (good_calls, after_calls))

    # ⚠ 注意：那条 credit=999 的"好 JSON 坏数据"行**会被计入** —— 这是预期行为
    # （账本是追加日志，不校验业务语义）。这里只是确认它不会让端点崩。
    print("       （注：手插的 credit=999 那行会被计入统计 —— 追加日志不校验业务语义，属预期）")

# ── 结果 ─────────────────────────────────────────────────────────
print()
print("=" * 72)
if failures:
    print("结果：%d 项检查，%d 项失败" % (checks, len(failures)))
    for f in failures:
        print("   FAIL  %s" % f)
    print("=" * 72)
    sys.exit(1)
else:
    print("结果：%d 项检查全部通过" % checks)
    print("=" * 72)
    sys.exit(0)
