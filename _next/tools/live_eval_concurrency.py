#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
并发分散真机验证：N 个并发请求是否落到不同账号上。

背景：e2e 的 t61 用真机证明了一个核心缺陷 —— 19 个号可用时，
      6 个并发请求全部落在同一个号。根因有二：
        ① AccountPool 排序主键是 ExpiresAtMs（负载只是次键，永远轮不到）
        ② GatewayApp.PickAccountWithAnyUsableModel 自己另写了一份同样的错误排序，
           完全绕过池子的 InFlight 计数
      本脚本在修复后做对照验证。

判据：N 个并发请求应落到 >= 2 个不同账号上（理想是尽量分散）。

用法：
  python tools/live_eval_concurrency.py --base http://127.0.0.1:8795 --key sk-conc --n 6 --rounds 3

---

## t68 的补充（e2e 加的，2026-10-07）

原脚本是 captain 为 t66 写的，只覆盖「并发分散」。t68 要求补两个方向：

    ③ 全冷却时仍返回明确 429 soft_rate
    ④ 会话粘性 vs 负载均衡（同一会话是否仍粘同一个号）

同时补一条原脚本的**判据强度**问题：

    ❗ 原判据是 `best >= 2`（**至少一轮**用到 ≥2 个号）。
      那太弱 —— 3 轮里只要 1 轮侥幸分散就过，另 2 轮全挤一个号也算通过。
      而「偶发分散」与「稳定分散」是两回事。
      ⇒ 新增 `--strict`：要求**每一轮**都 ≥ 2（修复后的预期是稳定分散）。
      默认仍用原判据（保持向后兼容），但**结论里会把两种都算出来**。
"""

import argparse
import json
import sys
import threading
import time
import urllib.error
import urllib.request

# Windows 控制台默认 GBK —— 中文/符号打不出来时会抛 UnicodeEncodeError，
# 而那是**环境问题不是被测对象的问题**（同 verify_gateway.py 的处置）。
# 强制 UTF-8 输出，让脚本在任何终端上都能跑完。
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

FORBIDDEN_PORTS = {8787, 3080}


def post_chat(base, key, model, tag, content=None):
    """发一次聊天请求，返回 (http_code, payload, 耗时)。"""
    body = json.dumps({
        "model": model,
        "messages": [{"role": "user", "content": content or f"只回复：{tag}"}],
        "max_tokens": 20,
        "stream": False,
    }, ensure_ascii=False).encode("utf-8")

    req = urllib.request.Request(
        f"{base}/v1/chat/completions", data=body, method="POST",
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            payload = json.loads(r.read().decode("utf-8"))
            return r.status, payload, time.time() - t0
    except urllib.error.HTTPError as e:
        text = e.read().decode("utf-8", errors="replace")
        try:
            payload = json.loads(text)
        except json.JSONDecodeError:
            payload = {"raw": text[:300]}
        return e.code, payload, time.time() - t0
    except Exception as e:
        return 0, {"error": f"{type(e).__name__}: {e}"}, time.time() - t0


def account_of(payload):
    """
    从响应里取「这次走了哪个号」。

    优先读网关加的归属头（若有），否则从 requestId / id 里取不出账号，
    就退回到用 /metrics 的 recent 判断 —— 但那需要串行，破坏并发。
    所以这里先试几个可能的位置。
    """
    if not isinstance(payload, dict):
        return None
    # 网关若把账号标签放进 usage 或顶层，这里能取到
    for k in ("account", "accountLabel", "accountTag", "usedAccount"):
        if isinstance(payload.get(k), str):
            return payload[k]
    return None


def read_metrics(base, key):
    with urllib.request.urlopen(
            urllib.request.Request(f"{base}/metrics",
                                   headers={"Authorization": f"Bearer {key}"}), timeout=15) as r:
        return json.loads(r.read().decode("utf-8"))


def account_labels(base, key):
    """accountTag → 人名，供报告里读得懂。"""
    try:
        with urllib.request.urlopen(
                urllib.request.Request(f"{base}/admin/accounts",
                                       headers={"Authorization": f"Bearer {key}"}), timeout=15) as r:
            acc = json.loads(r.read().decode("utf-8"))
    except Exception:  # noqa: BLE001
        return {}
    idx = {}
    if isinstance(acc, list):
        for a in acc:
            uid = str(a.get("uid") or "")
            head = uid if len(uid) <= 8 else uid[:8]
            idx[f"{head}:{a.get('enterpriseId') or ''}"] = a.get("label") or head
    return idx


def parse_recent(recent, n):
    """从 /metrics 的 recent 里取账号分布。

    ⚠ 字段名是 `accountTag`（实测 /metrics 的 recent[0] 形状），
      不是 account / accountLabel —— 早期版本读错名字，
      于是每轮都报"用到 0 个不同账号"，看着像"完全没分散"，
      实际是**根本没读到**。读不到和没分散长得一样，必须显式区分。

    @returns (distinct_list, missing_count, total)
    """
    accts = [r_.get("accountTag") or "" for r_ in recent[:n]]
    missing = sum(1 for a in accts if a == "")
    distinct = sorted(set(a for a in accts if a != ""))
    return distinct, missing, len(recent[:n])


# ── ④ 会话粘性（t68 新增）───────────────────────────────────────

def scenario_sticky(base, key, model, times, labels):
    """同一会话的连续请求是否仍粘在同一个号上。

    构造：**完全相同的 messages**（⇒ 同一个 ConversationKey）。
    期望：全程只出现 1 个账号 —— 粘性是特性，不该被负载均衡破坏。
    """
    print()
    print("=" * 72)
    print(f"④ 会话粘性 vs 负载均衡（同一段 messages 连发 {times} 次）")
    print("=" * 72)

    seen = []
    for i in range(times):
        code, payload, _ = post_chat(base, key, model, "sticky", content="粘性观测固定内容")
        try:
            m = read_metrics(base, key)
            distinct, missing, total = parse_recent(m.get("recent", []), 1)
        except Exception as e:  # noqa: BLE001
            print(f"  （读 /metrics 失败：{e}）")
            return None
        tag = distinct[0] if distinct else "（读不到）"
        seen.append(tag)
        print(f"  第{i+1}次：HTTP {code}  账号={tag} ({labels.get(tag, '?')})  累计序列={seen}")

    uniq = sorted(set(t for t in seen if t and t != "（读不到）"))
    ok = len(uniq) == 1 and seen and seen[0] != "（读不到）"
    verdict = "✅ 通过" if ok else "❌ 未通过"
    print()
    print(f"  判据（全程只出现 1 个账号）：{verdict}   实际={uniq}")
    if not ok:
        print("  ⚠ 粘性被破坏 —— 这正是 captain 担心的副作用（负载均衡压过了粘性）")
    return ok


# ── ③ 全冷却（t68 新增，如实说明限制）──────────────────────────

def scenario_all_cooled(base, key, model):
    print()
    print("=" * 72)
    print("③ 全冷却时是否仍返回明确 429 soft_rate")
    print("=" * 72)
    print("  ⚠ **如实说明限制**：真机上**没有**「把一个号标记冷却」的公开端点，")
    print("     所以无法从外部构造「全部号冷却」这个状态。")
    print("     真要把号打满配额去触发 ⇒ 消耗用户真实额度且不可控。")
    print()
    print("  ⇒ **本脚本不测这一维。** 该场景已由离线 E2E **精确覆盖**（能构造状态）：")
    print("       tests/BuddyDeck.E2E.Tests/PoolAndRetryE2ETests.cs")
    print("         · 网关层_所有号都在冷却时返回429错误对象_不崩不返回_undefined")
    print("         · 网关层_所有号都在冷却时流式请求也返回429错误对象")
    print("     （t13 修复后全绿）")
    print()
    print("  真机**能**做的等价观察：保险库为空 ⇒ 池里没有号 ⇒ 走同一个「没号可发」分支。")
    print("     见下方 ③b。")

    # ③b：用一个空保险库起网关不现实（本脚本不负责起网关）。
    # 改为直接打一个「池里没号」的探测：用错 key 会 401，用对 key 但不存在的模型……
    # 都不是「没号」。所以如实标记未测，不硬凑。
    print()
    print("  ③b：**未测** —— 本脚本连的是别人起好的网关，无法替换成空保险库。")
    return None


def main():
    ap = argparse.ArgumentParser(
        description="并发分散 + 粘性 + 全冷却 的真机验证（t68 扩展版）")
    ap.add_argument("--base", default="http://127.0.0.1:8795")
    ap.add_argument("--key", default="sk-conc")
    ap.add_argument("--model", default="deepseek-v4.1-flash")
    ap.add_argument("--n", type=int, default=6, help="并发数")
    ap.add_argument("--rounds", type=int, default=3, help="轮数")
    ap.add_argument("--settle", type=float, default=0.0,
                    help="每轮跑完后等几秒再跑下一轮（默认 0）")
    ap.add_argument("--strict", action="store_true",
                    help="判据收紧：要求**每一轮**都 ≥2 个号（默认只要求至少一轮）")
    ap.add_argument("--skip-sticky", action="store_true", help="跳过会话粘性场景")
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()
    base, key = args.base.rstrip("/"), args.key

    port = 0
    try:
        port = int(base.rsplit(":", 1)[1].split("/")[0])
    except (ValueError, IndexError):
        pass
    if port in FORBIDDEN_PORTS:
        print(f"!! 拒绝执行：{base} 指向保留端口 {port}（8787=全队模型来源，3080=DSH Web）")
        return 2

    print(f"目标 {base}  并发 {args.n}  轮数 {args.rounds}  模型 {args.model}"
          f"{'  [严格判据]' if args.strict else ''}")
    print("=" * 66)

    labels = account_labels(base, key)
    print(f"已加载 {len(labels)} 个号（accountTag → 名字）")

    # 先看基线：在途快照（起跑前应该全 0）
    try:
        m = read_metrics(base, key)
        print(f"起跑前 /metrics：totalRequests={m.get('totalRequests')} "
              f"attemptCount={m.get('attemptCount')}")
    except Exception as e:
        print(f"（读 /metrics 失败：{e}）")

    all_rounds, all_missing = [], []
    for rd in range(1, args.rounds + 1):
        results = [None] * args.n
        # 用 Barrier 对齐，避免"其实是串行"造成的假分散
        barrier = threading.Barrier(args.n)

        def worker(i):
            barrier.wait()                      # 所有线程在此对齐，然后同时放行
            code, payload, dt = post_chat(base, key, args.model, f"c{i}")
            results[i] = (code, payload, dt)

        threads = [threading.Thread(target=worker, args=(i,)) for i in range(args.n)]
        t0 = time.time()
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        wall = time.time() - t0

        codes = [c for c, _, _ in results]
        print(f"\n第 {rd} 轮  总墙钟 {wall:.2f}s")
        print(f"  HTTP: {codes}")

        # 账号归属：从 /metrics 的 recent 里读（它记了每次请求的账号）
        time.sleep(0.4)
        try:
            m = read_metrics(base, key)
            distinct, missing, total = parse_recent(m.get("recent", []), args.n)
            print(f"  最近 {total} 次请求的账号："
                  f"{[r_.get('accountTag') for r_ in m.get('recent', [])[:args.n]]}")
            if missing:
                print(f"  ⚠ 其中 {missing} 次读不到账号 —— 这是『读不到』，不是『没分散』")
            for d in distinct:
                print(f"       {d:<14} {labels.get(d, '?')}")
            print(f"  => 用到 {len(distinct)} 个不同账号：{distinct}")
            all_rounds.append(len(distinct))
            all_missing.append(missing)
        except Exception as e:
            print(f"  （读 /metrics 失败：{e}）")

        if args.settle:
            time.sleep(args.settle)

    print("\n" + "=" * 66)
    summary = {}
    if all_rounds:
        best, worst = max(all_rounds), min(all_rounds)
        print(f"各轮用到的不同账号数：{all_rounds}")
        print(f"最好 {best} / 最差 {worst}")
        # 原判据：至少有一轮用到 >= 2 个号（说明分散机制真的生效）
        ok_loose = best >= 2
        # 严格判据：每一轮都 >= 2（稳定分散）
        ok_strict = all(c >= 2 for c in all_rounds)
        print(f"判据（宽松：至少一轮 ≥2）：{'通过' if ok_loose else '失败'}")
        print(f"判据（严格：每轮都 ≥2）：{'通过' if ok_strict else '失败'}")
        summary["分散_宽松"] = ok_loose
        summary["分散_严格"] = ok_strict
        if not ok_loose:
            print("  ⇒ 仍然全部落在同一个号上 —— 分散未生效")
        if sum(all_missing):
            print(f"  ⚠ 有 {sum(all_missing)} 条记录读不到 accountTag —— 先查字段名，别急着下结论")
    else:
        print("没有得到可判读的轮次结果")
        summary["分散_宽松"] = False

    # ④ 会话粘性（t68 新增）
    if not args.skip_sticky:
        st = scenario_sticky(base, key, args.model, 4, labels)
        summary["会话粘性"] = bool(st)

    # ③ 全冷却（t68 新增，如实标未测）
    scenario_all_cooled(base, key, args.model)

    print()
    print("=" * 72)
    print("小结")
    print("=" * 72)
    for k, v in summary.items():
        print(f"  {k:<12} {'✅ 通过' if v else '❌ 未通过'}")
    print("  ③ 全冷却       ⏸ 未测（真机无法构造；已由离线 E2E 覆盖）")

    if args.json:
        print()
        print(json.dumps({"summary": summary, "perRound": all_rounds},
                         ensure_ascii=False, indent=2))

    return 0 if all(summary.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
