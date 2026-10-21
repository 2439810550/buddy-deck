#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
M20 User-Agent 要求验证（t51 决策材料）。

## 这个脚本回答什么

「哪些端点校验 User-Agent、哪些不校验」，以及**带什么 UA 才能拿到模型目录**。

## ⚠ 重要：它默认**不打真实上游**

真实上游需要**真实账号凭据**，而且会消耗配额。所以脚本分两种模式：

    # ① 默认：只打印**当前代码实际会发什么头**（离线，不联网）
    python tools/check_ua_requirement.py

    # ② 真机：拿真实凭据打真实上游（**会消耗配额**，需显式开启）
    python tools/check_ua_requirement.py --live --credential <accessToken> [--base https://...]

**② 只在你要复核 t48 的结论时用**，且要明白代价：每个 UA 变体 = 一次真实请求。

## t48 已实测的结论（我未复现，标注来源）

    不带 User-Agent → 400 code 12403「check ua, get coding copilot version error」
    带 User-Agent   → 200 + 37 个模型
    签到端点        → 两种 UA 都 200（**两端点行为不同**）

本脚本的作用是**让这些结论可复核**，而不是替代它们。
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.request

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

# ── 代码侧事实（离线可查，逐条附出处）────────────────────────────
#
# 这些是**读代码**得出的事实，不是实测。脚本把它们打出来，
# 是为了让「新版当前发什么」这件事可被任何人一眼复核。

CODE_FACTS = [
    {
        "what": "/v3/config（模型目录）不带 User-Agent",
        "where": "src/BuddyDeck.Core/UpstreamClient.cs:182-185",
        "detail": "只有 userAgent 非空才加这个头；调用方传 null 时**一个 UA 都没有**",
        "caller": "src/BuddyDeck.Gateway/GatewayApp.cs:405 —— FetchCatalogAsync(..., null, ...)",
        "impact": "按 t48 实测，无 UA ⇒ 400 code 12403 ⇒ 目录刷新失败",
        "mitigation": "GatewayApp.cs:409-418 把失败**静默**吞掉（只 Debug 级打日志）"
                      "⇒ 回落到 ModelCatalog.BuiltIn 的 16 个编译内置模型",
    },
    {
        "what": "MioraClient 的 User-Agent",
        "where": "src/BuddyDeck.Core/MioraClient.cs:224",
        "detail": 'request.Headers.TryAddWithoutValidation("User-Agent", "WorkBuddy/5.6.2")',
        "caller": "它只用于 Miora 的临时 key 路径（TempKeyPath），**不是 /v3/config**",
        "impact": "所以「新版有 UA」这个印象是错的 —— 有 UA 的是 Miora 那条线，不是目录那条线",
    },
    {
        "what": "聊天请求的 UA",
        "where": "src/BuddyDeck.Core/UpstreamProtocol.cs:57-60",
        "detail": "同样只在 userAgent 非空时加；网关转发聊天时传的是 null",
        "caller": "src/BuddyDeck.Gateway/GatewayApp.cs（RunWithRetryAsync → ChatStreamAsync(..., null, null, ...)）",
        "impact": "聊天当前**不带 UA**。t48 未说聊天被拒（可能不校验，或另有隐情）—— 见「未验证」一节",
    },
]

# ── 旧版怎么决定 UA（读旧版源码得出，非实测）────────────────────
LEGACY_FACTS = [
    {
        "what": "目录请求的 UA 是 **CLI 形态**，不是 WorkBuddy 形态",
        "where": "workbuddy_api/src/upstream.js:26",
        "detail": 'const CLIENT_UA = "CLI/2.63.2 CodeBuddy/2.63.2"',
        "why": "上游按 UA **分流花名册**：CLI 形态给 CLI 的模型集（即本工具能用的那些），"
               "App 形态给 App 内部花名册（upstream.js:639-645 注释）",
        "impact": "⇒ 不只是「有没有 UA」，**UA 的形态决定拿到哪份目录**",
    },
    {
        "what": "版本号的来源：优先探测已装 App，失败才用编译内置常量",
        "where": "workbuddy_api/src/client-identity.js:189-213",
        "detail": "macOS 读 bundle、Windows 读注册表；都拿不到时用 "
                  "FALLBACK_CN_APP_VERSION=5.5.6 / FALLBACK_AI_APP_VERSION=5.5.2",
        "impact": "⇒ 版本号**有来源逻辑**，但**探测失败也能用**（常量兜底）",
    },
    {
        "what": "旧版注释自称「版本号不参与路由」",
        "where": "workbuddy_api/src/client-identity.js:13-15",
        "detail": '"The gateway ignores the version numbers for routing, '
                  'so these are shape requirements, not currency claims."',
        "impact": "⇒ 旧版作者的判断是「**形态是关键，具体版本号不是**」。"
                  "但这是**旧版注释**，不是对当前上游的实测 —— 见「未验证」一节",
    },
]

# ── UA 变体（真机模式用）──────────────────────────────────────────
UA_VARIANTS = [
    ("（不设 UA）", None),
    ("WorkBuddy/5.6.2", "WorkBuddy/5.6.2"),                    # 新版 MioraClient 的值
    ("CLI/2.63.2 CodeBuddy/2.63.2", "CLI/2.63.2 CodeBuddy/2.63.2"),  # 旧版目录用的值
    ("WorkBuddy/0.0.1", "WorkBuddy/0.0.1"),                    # 探版本号是否被校验
]


def live_probe(base, credential, path, variants, extra_headers=None):
    """对真实上游打一轮 UA 变体。**会消耗配额。**"""
    results = []
    for label, ua in variants:
        headers = {
            "Authorization": "Bearer " + credential,
            "Accept": "application/json",
        }
        headers.update(extra_headers or {})
        if ua is not None:
            headers["User-Agent"] = ua

        req = urllib.request.Request(base + path, headers=headers, method="GET")
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                text = resp.read().decode("utf-8", errors="replace")
                status = resp.status
        except urllib.error.HTTPError as e:
            status, text = e.code, e.read().decode("utf-8", errors="replace")
        except Exception as e:  # noqa: BLE001
            status, text = 0, str(e)

        results.append({"ua": label, "status": status, "body": text[:600]})
    return results


def print_code_facts():
    print("=" * 72)
    print("① 代码侧事实（读源码得出，**非实测**）")
    print("=" * 72)
    for i, fact in enumerate(CODE_FACTS, 1):
        print()
        print("  [%d] %s" % (i, fact["what"]))
        print("      出处：%s" % fact["where"])
        print("      内容：%s" % fact["detail"])
        print("      调用：%s" % fact["caller"])
        print("      影响：%s" % fact["impact"])
        if fact.get("mitigation"):
            print("      缓解：%s" % fact["mitigation"])


def print_legacy_facts():
    print()
    print("=" * 72)
    print("② 旧版如何决定 UA（读 workbuddy_api 源码得出，**非实测**）")
    print("=" * 72)
    for i, fact in enumerate(LEGACY_FACTS, 1):
        print()
        print("  [%d] %s" % (i, fact["what"]))
        print("      出处：%s" % fact["where"])
        print("      内容：%s" % fact["detail"])
        if fact.get("why"):
            print("      说明：%s" % fact["why"])
        print("      影响：%s" % fact["impact"])


def print_matrix(live_results_get, live_results_checkin):
    print()
    print("=" * 72)
    print("③ 实测证据矩阵")
    print("=" * 72)
    print()
    print("  %-34s %-10s %-10s" % ("UA 变体", "/v3/config", "签到端点"))
    print("  " + "-" * 56)

    for i, (label, _ua) in enumerate(UA_VARIANTS):
        cfg = live_results_get[i] if live_results_get else None
        chk = live_results_checkin[i] if live_results_checkin else None
        cfg_s = ("%s" % cfg["status"]) if cfg else "（未跑真机）"
        chk_s = ("%s" % chk["status"]) if chk else "（未跑真机）"
        print("  %-34s %-10s %-10s" % (label, cfg_s, chk_s))

    if live_results_get:
        print()
        print("  原始响应（/v3/config）：")
        for r in live_results_get:
            print("    · %-34s → %s  %s" % (r["ua"], r["status"], r["body"][:200].replace("\n", " ")))
    if live_results_checkin:
        print()
        print("  原始响应（签到端点）：")
        for r in live_results_checkin:
            print("    · %-34s → %s  %s" % (r["ua"], r["status"], r["body"][:200].replace("\n", " ")))


def main():
    parser = argparse.ArgumentParser(description="M20 User-Agent 要求验证（t51）")
    parser.add_argument("--live", action="store_true",
                        help="打真实上游（⚠ 会消耗真实配额，需要 --credential）")
    parser.add_argument("--credential", default="", help="真实 accessToken（只在 --live 时用）")
    parser.add_argument("--base", default="https://www.codebuddy.cn", help="目录所在的基地址")
    parser.add_argument("--json", action="store_true", help="输出 JSON")
    args = parser.parse_args()

    if args.live and not args.credential:
        print("!! --live 需要 --credential（真实 accessToken）")
        return 2

    live_get = live_checkin = None
    if args.live:
        print("⚠⚠ 真机模式：将对真实上游发 %d × 2 次请求，**消耗真实账号配额**"
              % len(UA_VARIANTS))
        live_get = live_probe(args.base, args.credential, "/v3/config", UA_VARIANTS)
        # 签到端点：两种 UA 都 200（t48 实测）—— 这里用 POST 复核
        live_checkin = None  # 签到是 POST，且本脚本聚焦目录；留位给后续扩展

    if args.json:
        print(json.dumps({
            "codeFacts": CODE_FACTS,
            "legacyFacts": LEGACY_FACTS,
            "liveCatalog": live_get,
        }, ensure_ascii=False, indent=2))
        return 0

    print("M20 User-Agent 要求 —— 决策材料（t51）")
    print("⚠ 本脚本区分「读代码得出」与「实测」两类结论，请按标注引用。")

    print_code_facts()
    print_legacy_facts()
    print_matrix(live_get, live_checkin)

    print()
    print("=" * 72)
    print("④ 未验证 / 存疑（**不要当成实测**）")
    print("=" * 72)
    print("""
  · t48 的「不带 UA → 400 code 12403」「带 UA → 200 + 37 模型」
    —— 我**未复现**（需要真实凭据与配额）。本文引用时一律标来源为 t48。
  · 「同一份 UA 换不同版本号（如 WorkBuddy/0.0.1）是否仍 200」
    —— **未验证**。这正是判定「版本号有意义 vs 只是形态」的关键实验，
    见 §③ 矩阵里的 WorkBuddy/0.0.1 行（需 --live 才有数）。
  · 聊天端点是否校验 UA —— **未验证**。t48 未提；代码里聊天也不带 UA。
  · 签到端点「两种 UA 都 200」—— 引自 t48，我未复现。
""")
    print("=" * 72)
    return 0


if __name__ == "__main__":
    sys.exit(main())
