#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
真机观测：池 → 号 → 模型 的可见性、并发分流、限额自动降级（t61）。

## 用户诉求（原话）

> 可以看请求走的哪个号池、走的号池里哪个号，然后并发走，
> 这样就不会出现一个号限额还得手动切换。

## 这个脚本回答四个问题

    ① 池 → 号归属：这次请求走了哪个池、池里哪个号、用了哪个模型
    ② 并发分布：N 个请求同时发出时，账号怎么分布
    ③ 限额自动降级：不手动切换的情况下，请求是否仍然成功
    ④ 全部冷却时：返回明确 429，还是崩溃

## 判据从哪里取（**这一条最重要**）

`docs/concurrency-verification-howto.md` §0①：

> 要验证 A 对 B 做了什么，就去 **B 那边**取证，**不要在 A 内部找计数器**。

所以本脚本**不读** `AccountPool.SelectionCount` 之类的内部累计值（它回答的是
「历史上被选过几次」，不是「此刻这个请求用了哪个号」）。

**用了三条外部证据，互相印证**：

    ① `/metrics` 的 `recent[]` → 每条记录带 `accountTag`（= uid 前 8 位 + ":"）
       —— 这是**网关自己记账时写下的**「这个请求用了哪个号」
    ② `/admin/accounts` → `uid` / `label`
       —— 用来把 `accountTag` 反查回**人能看懂的名字**（池 → 号）
    ③ 响应体里的 `model` / `usedModel`
       —— 模型这一维

⚠ **本脚本打的是真实上游、消耗真实配额。** 每次 `chat` 请求 = 一次真实调用。
请勿滥用：默认每个场景只发必要的少数几次。

## 用法

    # 网关需先起好（本脚本**不自己起**，避免与别人的实例抢端口）
    src\\BuddyDeck.Gateway\\bin\\Debug\\net8.0\\BuddyDeck.Gateway.exe ^
        --port 8795 --api-key sk-local-e2e --vault <绝对路径>

    python tools/live_eval_pool.py --help
    python tools/live_eval_pool.py                      # 默认连 127.0.0.1:8795
    python tools/live_eval_pool.py --concurrency 4      # 并发场景发 4 个
    python tools/live_eval_pool.py --json               # 机器可读输出

⚠ **端口安全**：默认 8795。**绝不要**指向 8787（那是全队的模型来源）。
脚本会对 8787/3080 直接拒绝执行。
"""

import argparse
import json
import sys
import threading
import time
import urllib.error
import urllib.request

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

FORBIDDEN_PORTS = {8787, 3080}

# 观测到的原始证据都收在这里，最后一起打印（便于「异常项给原始输出」）
RAW_EVIDENCE = []


# ── HTTP ──────────────────────────────────────────────────────────

def call(base, method, path, key=None, body=None, timeout=180, headers=None):
    """发一个请求，返回 (status, parsed_or_text, elapsed_s)。不抛。"""
    data = None
    hdrs = dict(headers or {})
    if key:
        hdrs["Authorization"] = "Bearer " + key
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        hdrs["Content-Type"] = "application/json"

    req = urllib.request.Request(base + path, data=data, headers=hdrs, method=method)
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            text = resp.read().decode("utf-8", errors="replace")
            return resp.status, _maybe_json(text), time.time() - t0
    except urllib.error.HTTPError as e:
        text = e.read().decode("utf-8", errors="replace")
        return e.code, _maybe_json(text), time.time() - t0
    except Exception as e:  # noqa: BLE001
        return 0, "%s: %s" % (type(e).__name__, e), time.time() - t0


def _maybe_json(text):
    try:
        return json.loads(text)
    except (json.JSONDecodeError, TypeError):
        return text


def evidence(label, payload):
    """记一条原始证据（打印时原样给出，不做描述性转述）。"""
    RAW_EVIDENCE.append((label, payload))


# ── 观测面 ────────────────────────────────────────────────────────

def fetch_metrics(base, key):
    status, body, _ = call(base, "GET", "/metrics", key)
    return status, (body if isinstance(body, dict) else {})


def fetch_accounts(base, key):
    status, body, _ = call(base, "GET", "/admin/accounts", key)
    return status, (body if isinstance(body, list) else [])


def fetch_pools(base, key):
    status, body, _ = call(base, "GET", "/admin/pools", key)
    return status, (body if isinstance(body, list) else [])


def build_tag_index(accounts):
    """accountTag（uid 前 8 位 + ":"） → 人能看懂的名字。

    与 `GatewayApp.AccountTagOf` 的实现对齐：
        head = uid.Length <= 8 ? uid : uid[..8]
        tag  = $"{head}:{enterpriseId ?? ""}"
    """
    index = {}
    for a in accounts:
        uid = str(a.get("uid") or "")
        head = uid if len(uid) <= 8 else uid[:8]
        tag = "%s:%s" % (head, a.get("enterpriseId") or "")
        index[tag] = a.get("label") or head
    return index


def recent_after(base, key, since_iso):
    """取 /metrics 里 at > since_iso 的那些 recent 记录。"""
    _, m = fetch_metrics(base, key)
    recs = m.get("recent") or []
    return [r for r in recs if str(r.get("at") or "") > since_iso]


# ── 四个维度 ──────────────────────────────────────────────────────

def dim1_visibility(base, key, model, tag_index, label):
    """① 池 → 号 → 模型：一次真实请求，三个维度都要有证据。"""
    print()
    print("=" * 72)
    print("① 池 → 号 → 模型 的可见性")
    print("=" * 72)

    t_start = _now_iso()
    status, pools = fetch_pools(base, key)
    accounts_status, accounts = fetch_accounts(base, key)

    print("  /admin/pools    → HTTP %s，%d 个池" % (status, len(pools)))
    for p in pools:
        print("     %s" % json.dumps(p, ensure_ascii=False))
    evidence("GET /admin/pools", json.dumps(pools, ensure_ascii=False))

    print("  /admin/accounts → HTTP %s，%d 个号" % (accounts_status, len(accounts)))
    for a in accounts:
        print("     id=%s label=%s uid=%s" % (
            str(a.get("id"))[:28], a.get("label"), str(a.get("uid"))[:8] + "…"))
    evidence("GET /admin/accounts", json.dumps(accounts, ensure_ascii=False))

    # ★★ 交叉断言：池的 accountCount 必须等于账号列表长度。
    #
    # 为什么这条必须有（教训来自 t61 第一次跑）：
    #   当时 `/admin/pools` 报 `accountCount: 0`，而 `/admin/accounts` 有 19 个号 ——
    #   **一个明显错误的数字，却让三项判据全绿**，因为那些判据问的是
    #   「请求成不成功」，不是「显示的号数对不对」。
    #   ⇒ **判据要覆盖「我声称要验的那件事」**：这里声称验的是**可见性**，
    #     所以必须断言「看到的数字是对的」，而不只是「能看到数字」。
    #
    # 参考：该缺陷已由 gateway 修复（默认池改用 ResolveVaultPath）；本断言是它的回归钉子。
    counts_ok = True
    for p in pools:
        shown = p.get("accountCount")
        if shown is not None and shown != len(accounts):
            counts_ok = False
            print()
            print("  ❌ /admin/pools 的 accountCount=%s，但 /admin/accounts 有 %d 个"
                  % (shown, len(accounts)))
            print("     （-1 表示「读不到」，那是**另一个**语义，不是「有 -1 个号」）")
    if counts_ok and pools:
        print("     ✓ accountCount 与账号列表长度一致")

    # 发一次真实请求
    body = {
        "model": model,
        "stream": False,
        "messages": [{"role": "user", "content": "回一个字"}],
    }
    st, resp, dt = call(base, "POST", "/v1/chat/completions", key, body)
    print()
    print("  POST /v1/chat/completions → HTTP %s（%.1fs）" % (st, dt))
    if isinstance(resp, dict):
        print("     响应 model=%s finish=%s" % (
            resp.get("model"), (resp.get("choices") or [{}])[0].get("finish_reason")))
    evidence("POST /v1/chat/completions 原始响应", json.dumps(resp, ensure_ascii=False)[:600])

    # 从 /metrics 取这次请求的归属
    recs = recent_after(base, key, t_start)
    print("     本次请求在 /metrics.recent 里：%d 条" % len(recs))
    for r in recs:
        tag = str(r.get("accountTag") or "")
        name = tag_index.get(tag, "（未映射）")
        print("       requestId=%s 池=%s 号=%s(%s) 模型=%s→%s status=%s %sms" % (
            str(r.get("requestId"))[:12], _pool_of(pools, key),
            tag or "（空）", name, r.get("model"), r.get("usedModel"),
            r.get("status"), r.get("durationMs")))
    evidence("/metrics.recent（本次）", json.dumps(recs, ensure_ascii=False)[:800])

    ok = st == 200 and len(recs) >= 1 and bool(str(recs[0].get("accountTag") or "")) and counts_ok
    return ok, recs


def dim2_concurrency(base, key, model, concurrency, tag_index, pools):
    """② 并发分布：同时发 N 个，看账号怎么分布。"""
    print()
    print("=" * 72)
    print("② 并发分布（同时发 %d 个请求）" % concurrency)
    print("=" * 72)

    # ⚠ 用 threading.Barrier 让它们**真的同时**出发 —— 不用 sleep 猜时间
    barrier = threading.Barrier(concurrency)
    results = [None] * concurrency
    t_start = _now_iso()

    def worker(i):
        barrier.wait()          # 所有线程在这里对齐，然后一起冲
        body = {
            "model": model,
            "stream": False,
            "messages": [{"role": "user", "content": "并发观测 %d" % i}],
        }
        st, resp, dt = call(base, "POST", "/v1/chat/completions", key, body)
        results[i] = (st, dt)

    threads = [threading.Thread(target=worker, args=(i,)) for i in range(concurrency)]
    t0 = time.time()
    for t in threads:
        t.start()
    for t in threads:
        t.join(timeout=300)
    wall = time.time() - t0

    codes = [r[0] if r else None for r in results]
    print("  HTTP 状态码：%s" % codes)
    print("  墙钟耗时：%.1fs（%d 个并发）" % (wall, concurrency))

    recs = recent_after(base, key, t_start)
    by_tag = {}
    for r in recs:
        tag = str(r.get("accountTag") or "")
        by_tag[tag] = by_tag.get(tag, 0) + 1

    print("  账号分布（来自 /metrics.recent 的 accountTag）：")
    for tag, n in sorted(by_tag.items(), key=lambda kv: -kv[1]):
        print("     %-14s %-10s %d 次" % (tag or "（空）", tag_index.get(tag, "?"), n))
    evidence("并发 HTTP 码", json.dumps(codes))
    evidence("并发 /metrics.recent", json.dumps(recs, ensure_ascii=False)[:1500])

    # ⚠ 「峰值并发」需要**同一时刻**的在途数 —— 而 /metrics 只有 durationMs，
    #    **没有开始时刻**（本脚本 t67 才用 `at - durationMs` 反推出区间）。
    #    早期版本因此**不声称**能算峰值并发。
    #    ⚠ t67 更新：现在**能**反推了（见 dim5）—— 但那依赖 durationMs 准确，
    #       且只覆盖「已完成的请求」，所以在飞的请求仍看不到。保留下面的提示。
    print()
    print("  ⚠ 「峰值并发」的反推见 ⑤（用 at - durationMs 重建区间）—— 但有局限，见那节说明。")

    ok = all(c == 200 for c in codes) and len(by_tag) >= 1
    return ok, by_tag


def dim5_inflight_cap(base, key, model, tag_index, accounts_count):
    """⑤ 在途上限是否生效（t67 新增）。

    ## 判据怎么设计（这一步最容易自欺）

    配置里 `InFlightOptions.MaxPerAccount = 2`（`InFlightLease.cs:137`）。
    但**「每号最多 2 个」这个观察本身无法证明上限生效** ——
    因为 N 个请求 / M 个号时，纯轮询的预期最大值也是 `ceil(N/M)`：

        N=12, M=8 → ceil(1.5)=2   ← 与「上限=2」**巧合一致**，测不出区别

    ⇒ 必须选一个**两者会分岔**的场景：**N > M × 上限**。

        N=24, M=8, 上限=2 → 上限生效时最多同时 16 个，多的 8 个必须等
                           → 单号同时最多 **2**
        若上限未生效（纯轮询）→ 单号承担 **3**（24/8）

    ## 还要区分「同时」与「先后」

    「单号 3 个请求」可能是**先后完成**的（串行复用），那不算违反上限。
    所以本函数用 `at - durationMs` **反推每个请求的区间**，再算**最大时间重叠**。
    只有**重叠 ≥ 3** 才证明「3 个真的同时在跑」。

    ⚠ 局限：`durationMs` 是网关自报的整数毫秒，`at` 是完成时刻 ——
       反推出的区间是**近似**，且只覆盖已完成请求（在飞的看不到）。
       所以这个数是**下界**，不是精确峰值。
    """
    print()
    print("=" * 72)
    print("⑤ 在途上限是否生效（单号同时并发 ≤ MaxPerAccount）")
    print("=" * 72)

    limit = 2   # InFlightOptions.MaxPerAccount 的默认值（InFlightLease.cs:137）
    m = accounts_count if accounts_count > 0 else 8
    n = m * 3          # ★ 故意 > 号数 × 上限，让「上限生效」与「纯轮询」分岔
    print("  配置上限 MaxPerAccount = %d；号数 = %d" % (limit, m))
    print("  发 %d 个并发（= 号数 × 3）—— 纯轮询预期单号 3 个；上限生效则单号应 ≤ 2" % n)
    print()

    barrier = threading.Barrier(n)
    codes = [None] * n

    def worker(i):
        barrier.wait()
        body = {"model": model, "stream": False,
                "messages": [{"role": "user", "content": "上限观测 %d" % i}]}
        codes[i] = call(base, "POST", "/v1/chat/completions", key, body)[0]

    threads = [threading.Thread(target=worker, args=(i,)) for i in range(n)]
    # ⚠ 必须是 **ISO 字符串**（`recent_after` 按字符串比大小）——
    #   我第一版在这里传了 `time.time()` 的 float，导致
    #   `TypeError: '>' not supported between 'str' and 'float'`，
    #   脚本在 ⑤ 之后崩掉（小结打不出来）。修：发请求前先取 ISO。
    t_start = _now_iso()
    wall_start = time.time()
    for t in threads:
        t.start()
    for t in threads:
        t.join(timeout=900)
    wall = time.time() - wall_start

    print("  HTTP 状态码：%s" % codes)
    print("  墙钟耗时：%.1fs" % wall)

    recs = recent_after(base, key, t_start)
    by_tag = {}
    for r in recs:
        by_tag.setdefault(str(r.get("accountTag") or ""), []).append(r)

    print("  每号承担（总数）：")
    for tag, rs in sorted(by_tag.items(), key=lambda kv: -len(kv[1])):
        print("     %-14s %-10s %d 个" % (tag or "（空）", tag_index.get(tag, "?"), len(rs)))

    # ★ 反推时间重叠
    max_overlap = 0
    for tag, rs in by_tag.items():
        ivs = []
        for r in rs:
            try:
                from datetime import datetime, timedelta
                end = datetime.fromisoformat(str(r.get("at")))
                start = end - timedelta(milliseconds=int(r.get("durationMs") or 0))
                ivs.append((start, end))
            except (ValueError, TypeError):
                continue
        for s, _e in ivs:
            c = sum(1 for s2, e2 in ivs if s2 <= _e and e2 >= s)
            max_overlap = max(max_overlap, c)

    print()
    print("  ★ 单号最大**时间重叠** = %d（用 at - durationMs 反推区间算的）" % max_overlap)
    print("     判据：上限生效 ⇒ 应 ≤ %d；观察到 ≥ %d ⇒ **上限未生效**" % (limit, limit + 1))

    evidence("⑤ 上限观测 HTTP 码", json.dumps(codes))
    evidence("⑤ 上限观测 每号承担", json.dumps(
        {k: len(v) for k, v in by_tag.items()}, ensure_ascii=False))
    evidence("⑤ 单号最大时间重叠", str(max_overlap))

    cap_ok = max_overlap <= limit
    if not cap_ok:
        print()
        print("  ⚠ **在途上限未生效** —— 观察到 %d 个请求同时压在同一号上。" % max_overlap)
        print("     这**不是本次修复的回归**：修复改的是排序（分散），")
        print("     而上限要靠 `Acquire` 维护 InFlight。实测 `grep \".Acquire(\"")
        print("     src/BuddyDeck.Gateway/` → 0 命中 ⇒ **Acquire 从未接线** ⇒ InFlight 恒为 0。")
    return cap_ok, max_overlap


def dim3_fallback(base, key, model, tag_index):
    """③ 限额自动降级：用不存在的模型触发上游拒绝，看是否自动降级或明确报错。"""
    print()
    print("=" * 72)
    print("③ 限额 / 上游异常时的自动降级")
    print("=" * 72)
    print("  ⚠ 说明：真实限额**不可控**（不能为了让某个号被限就去打满配额）。")
    print("    所以本节验的是**降级链本身是否工作**，触发方式用「不可用的模型名」——")
    print("    它会让上游拒绝，从而走到与限流相同的重试/降级分支。")

    t_start = _now_iso()
    body = {
        "model": "no-such-model-xyz",       # 上游会拒
        "stream": False,
        "messages": [{"role": "user", "content": "降级观测"}],
    }
    st, resp, dt = call(base, "POST", "/v1/chat/completions", key, body)
    print()
    print("  用不可用模型 → HTTP %s（%.1fs）" % (st, dt))
    print("  原始响应：%s" % json.dumps(resp, ensure_ascii=False)[:400])
    evidence("降级观测 原始响应", json.dumps(resp, ensure_ascii=False)[:600])

    recs = recent_after(base, key, t_start)
    _, metrics = fetch_metrics(base, key)
    print("  /metrics.recent：")
    for r in recs:
        tag = str(r.get("accountTag") or "")
        print("     status=%s attempts=%s kind=%s 号=%s 模型=%s→%s" % (
            r.get("status"), r.get("attempts"),
            r.get("errorKind"), tag_index.get(tag, tag),
            r.get("model"), r.get("usedModel")))

    # ⚠ `attemptCount` 是 /metrics 的**顶层**字段（= 上游总尝试数），**不在** recent[] 里。
    #   recent[] 里那条叫 `attempts`（这一次客户端请求试了几枪）。我第一版写错了改这里查。
    print("     /metrics 顶层：totalRequests=%s attemptCount=%s httpSuccessRate=%s" % (
        metrics.get("totalRequests"), metrics.get("attemptCount"),
        metrics.get("httpSuccessRate")))
    evidence("降级观测 /metrics.recent", json.dumps(recs, ensure_ascii=False)[:800])
    evidence("降级观测 /metrics 顶层",
             json.dumps({k: v for k, v in metrics.items() if k != "recent"},
                        ensure_ascii=False)[:400])

    # 判据（宽松但有意义）：**不崩**（不是 500）、且给出明确状态
    not_crashed = st != 0 and st != 500
    print()
    print("  判据：不崩（HTTP≠500 且非连接失败）→ %s" % ("通过" if not_crashed else "失败"))
    return not_crashed, recs


def dim4_all_cooled(base, key, model):
    """④ 全部冷却时的行为 —— 通过 /admin/* 把号全部标冷却做不到（无此端点），
    所以这一维改为**如实说明无法从外部制造**，而不是假装测了。"""
    print()
    print("=" * 72)
    print("④ 全部号冷却时的行为")
    print("=" * 72)
    print("  ⚠ **本脚本无法从外部制造「全部号冷却」** —— 逐个说明原因：")
    print("     · 没有公开端点可以「把一个号标冷却」（`/admin/*` 里没有这个动作）")
    print("     · 靠真打满配额去触发 ⇒ 消耗用户真实额度，且不可控")
    print("     · 该场景**已在离线夹具里覆盖**（`tests/BuddyDeck.E2E.Tests`")
    print("       的 `网关层_所有号都在冷却时返回429错误对象_不崩不返回_undefined`，")
    print("       以及它的流式版本）—— 那里能精确构造状态")
    print()
    print("  ⇒ **如实标记：这一维本脚本不测。** 不代表它没被验证，")
    print("     而是**由离线 E2E 覆盖、本真机脚本不重复**。")
    return None, []


# ── 辅助 ──────────────────────────────────────────────────────────

def _now_iso():
    from datetime import datetime, timezone
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")


def _pool_of(pools, key):
    for p in pools:
        if p.get("isDefault"):
            return p.get("id")
    return "?"


# ── 主流程 ────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(
        description="真机观测：池→号→模型 可见性 / 并发分布 / 限额降级（t61）",
        epilog="⚠ 打真实上游，消耗真实配额。默认连 127.0.0.1:8795，拒绝 8787/3080。")
    ap.add_argument("--base", default="http://127.0.0.1:8795", help="网关地址")
    ap.add_argument("--key", default="sk-local-e2e", help="网关 API key")
    ap.add_argument("--model", default="hy3", help="用的模型")
    ap.add_argument("--concurrency", type=int, default=3, help="并发场景的请求数")
    ap.add_argument("--skip-live", action="store_true", help="只做只读探测，不发 chat 请求")
    ap.add_argument("--skip-cap", action="store_true",
                    help="跳过 ⑤ 在途上限观测（它会发 号数×3 个请求，最耗配额）")
    ap.add_argument("--json", action="store_true", help="输出 JSON")
    args = ap.parse_args()

    base = args.base.rstrip("/")

    # ⚠ 端口安全：绝不指向 8787（全队模型来源）/ 3080（DSH Web）
    port = 0
    try:
        port = int(base.rsplit(":", 1)[1].split("/")[0])
    except (ValueError, IndexError):
        pass
    if port in FORBIDDEN_PORTS:
        print("!! 拒绝执行：%s 指向保留端口 %d（8787=全队模型来源，3080=DSH Web）" % (base, port))
        return 2

    print("真机观测（t61）—— 池 → 号 → 模型 / 并发 / 降级")
    print("网关：%s" % base)
    print("⚠ 本脚本打**真实上游**，消耗真实配额。")

    # 探活
    st, health, _ = call(base, "GET", "/healthz", None)
    print()
    print("GET /healthz → HTTP %s" % st)
    print("   %s" % json.dumps(health, ensure_ascii=False)[:200] if isinstance(health, dict) else health)
    if st != 200:
        print("!! 网关不可达 —— 先按文档起网关")
        return 2

    _, accounts = fetch_accounts(base, args.key)
    _, pools = fetch_pools(base, args.key)
    tag_index = build_tag_index(accounts)
    print("   已加载 %d 个号、%d 个池（用于把 accountTag 反查成名字）" % (len(accounts), len(pools)))

    summary = {}
    if args.skip_live:
        print()
        print("(--skip-live：只做只读探测，不发 chat 请求)")
    else:
        ok1, r1 = dim1_visibility(base, args.key, args.model, tag_index, "d1")
        summary["视角可见性"] = ok1
        ok2, dist2 = dim2_concurrency(base, args.key, args.model, args.concurrency,
                                      tag_index, pools)
        summary["并发分布"] = ok2
        ok3, r3 = dim3_fallback(base, args.key, args.model, tag_index)
        summary["降级不崩"] = ok3
        dim4_all_cooled(base, args.key, args.model)
        if not args.skip_cap:
            ok5, overlap = dim5_inflight_cap(base, args.key, args.model, tag_index, len(accounts))
            summary["在途上限"] = ok5

    print()
    print("=" * 72)
    print("原始证据汇总（异常项以此为准，不做描述性转述）")
    print("=" * 72)
    for label, payload in RAW_EVIDENCE:
        print()
        print("  ── %s ──" % label)
        print("  %s" % str(payload)[:1200])

    print()
    print("=" * 72)
    print("小结")
    print("=" * 72)
    for k, v in summary.items():
        print("  %-12s %s" % (k, "✅ 通过" if v else "❌ 未通过"))

    if args.json:
        print()
        print(json.dumps({"summary": summary,
                          "evidence": [{"label": l, "payload": p} for l, p in RAW_EVIDENCE]},
                         ensure_ascii=False, indent=2))

    return 0 if all(summary.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
