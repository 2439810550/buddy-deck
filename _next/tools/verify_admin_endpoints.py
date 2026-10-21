#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
/admin/* 端点族的端到端契约验证（t17 收口用）。

为什么要有这个脚本（而不是只靠单元测试）：
  单元测试用的是测试夹具起的宿主。队长指出的真实问题是 ——
  「ui 的验收只验到『网关回了 401』，t18 补了授权头，
   但从来没人验证过 401 变成 200、以及返回的数据能不能被界面真正渲染」。
  所以这里起**真的网关进程**，用**真的 HTTP** 打全部端点，
  并逐一核对响应形状与 BuddyDeck.App 的 DTO 对齐。

它验四件事：
  ① 每个端点都返回 200 + 可解析 JSON（不是 404 / 401）
  ② 键名与 IGatewayClient.cs 里的 DTO 逐字对齐
  ③ /admin/accounts 不含 accessToken / refreshToken 原值（金丝雀值）
  ④ 无 key / 错 key 一律 401

用法：python tools/verify_admin_endpoints.py <port> <api-key>
"""

import json
import sys
import urllib.error
import urllib.request

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8792
API_KEY = sys.argv[2] if len(sys.argv) > 2 else "test-key"

BASE = "http://127.0.0.1:%d" % PORT

# 金丝雀：只要这两个值出现在任何响应里，就是 token 泄露。
CANARY_ACCESS = "sk-CURL-CANARY-MUST-NOT-LEAK"
CANARY_REFRESH = "rt-CURL-CANARY"

failures = []
checks = 0


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
        with urllib.request.urlopen(req, timeout=15) as resp:
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


print("=" * 72)
print("① 鉴权：无 key / 错 key 必须 401")
print("=" * 72)
for path in ["/admin/accounts", "/admin/settings", "/admin/pools", "/admin/logs?lines=5"]:
    st, _ = call("GET", path, key=None)
    check("无 key %s → 401" % path, st == 401, "实际 %s" % st)
    st, _ = call("GET", path, key="wrong-key")
    check("错 key %s → 401" % path, st == 401, "实际 %s" % st)

st, _ = call("GET", "/healthz", key=None)
check("/healthz 仍然免鉴权 → 200", st == 200, "实际 %s" % st)

print()
print("=" * 72)
print("② 读端点：200 + 形状与 App 的 DTO 对齐")
print("=" * 72)

# ── /admin/accounts：App 的 AccountSummary ────────────────
st, accounts = call("GET", "/admin/accounts")
check("GET /admin/accounts → 200", st == 200, "实际 %s" % st)
check("响应是数组", isinstance(accounts, list), type(accounts).__name__)
if isinstance(accounts, list) and accounts:
    first = accounts[0]
    for field in ["id", "label", "uid", "enterpriseId", "domain",
                  "expiresAtMs", "refreshExpiresAtMs", "active"]:
        check("AccountSummary.%s 存在" % field, field in first,
              "实际键：%s" % sorted(first.keys()))
    check("active 是布尔", isinstance(first.get("active"), bool))
    check("expiresAtMs 是数字", isinstance(first.get("expiresAtMs"), int))
    print("       样例：%s" % json.dumps(first, ensure_ascii=False)[:160])
else:
    print("       （保险库为空，跳过字段断言）")

# ── ③ 脱敏 ────────────────────────────────────────────────
print()
print("=" * 72)
print("③ 脱敏：响应里绝不能出现 token 原值")
print("=" * 72)
st, raw_accounts = call("GET", "/admin/accounts", raw=True)
check("/admin/accounts 不含 accessToken 原值",
      CANARY_ACCESS not in raw_accounts, "泄露了！")
check("/admin/accounts 不含 refreshToken 原值",
      CANARY_REFRESH not in raw_accounts, "泄露了！")
check("/admin/accounts 不含 accessToken 这个键",
      "accessToken" not in raw_accounts)
check("/admin/accounts 不含 refreshToken 这个键",
      "refreshToken" not in raw_accounts)

# ── /admin/settings：App 的 GatewaySettings ───────────────
st, settings = call("GET", "/admin/settings")
check("GET /admin/settings → 200", st == 200, "实际 %s" % st)
if isinstance(settings, dict):
    for field in ["port", "apiKey", "variantId", "vaultPath", "logPath"]:
        check("GatewaySettings.%s 存在" % field, field in settings,
              "实际键：%s" % sorted(settings.keys()))
    print("       样例：%s" % json.dumps(settings, ensure_ascii=False)[:200])

# ── /admin/pools：App 的 PoolSummary ─────────────────────
st, pools = call("GET", "/admin/pools")
check("GET /admin/pools → 200", st == 200, "实际 %s" % st)
check("响应是数组", isinstance(pools, list))
if isinstance(pools, list) and pools:
    first = pools[0]
    for field in ["id", "displayName", "port", "apiKey", "running",
                  "accountCount", "isDefault"]:
        check("PoolSummary.%s 存在" % field, field in first,
              "实际键：%s" % sorted(first.keys()))
    print("       样例：%s" % json.dumps(first, ensure_ascii=False)[:200])

# ── /admin/logs：App 的 LogListDto { lines: [{at,level,message}] } ──
st, logs = call("GET", "/admin/logs?lines=20")
check("GET /admin/logs → 200", st == 200, "实际 %s" % st)
check("响应是对象且含 lines", isinstance(logs, dict) and "lines" in logs,
      "实际：%s" % type(logs).__name__)
if isinstance(logs, dict) and logs.get("lines"):
    line = logs["lines"][0]
    for field in ["at", "level", "message"]:
        check("LogLine.%s 存在" % field, field in line,
              "实际键：%s" % sorted(line.keys()))
    print("       样例：%s" % json.dumps(line, ensure_ascii=False)[:200])
else:
    print("       （日志为空，跳过字段断言 —— 端点形状已验）")

print()
print("=" * 72)
print("④ 写端点：能改写状态且不 404 / 不 500")
print("=" * 72)

# 取一个真实 id 来测激活
account_id = None
if isinstance(accounts, list) and accounts:
    account_id = accounts[0]["id"]

if account_id:
    from urllib.parse import quote
    enc = quote(account_id, safe="")

    st, _ = call("POST", "/admin/accounts/%s/activate" % enc, {})
    check("POST activate → 200", st == 200, "实际 %s" % st)

    st, _ = call("POST", "/admin/accounts/%s/rename" % enc, {"label": "e2e 改的名"})
    check("POST rename → 200", st == 200, "实际 %s" % st)

    # 改名要真的落盘
    st, after = call("GET", "/admin/accounts")
    renamed = any(a.get("label") == "e2e 改的名" for a in after) if isinstance(after, list) else False
    check("rename 真的改了标签（落盘生效）", renamed)

    st, _ = call("POST", "/admin/accounts/%s/refresh" % enc, {})
    check("POST refresh 如实回 501（未实现，不假装成功）", st == 501, "实际 %s" % st)

st, _ = call("POST", "/admin/accounts/import", {"payload": "{不是 JSON"})
check("POST import 畸形 JSON → 400", st == 400, "实际 %s" % st)

st, _ = call("POST", "/admin/settings", {"port": 99999})
check("POST settings 非法端口 → 400", st == 400, "实际 %s" % st)

st, _ = call("POST", "/admin/settings", {"variantId": "不存在的变体"})
check("POST settings 未知变体 → 400", st == 400, "实际 %s" % st)

st, _ = call("POST", "/admin/settings", {"port": PORT})
check("POST settings 合法值 → 200", st == 200, "实际 %s" % st)

st, _ = call("POST", "/admin/pools", {"id": "new"})
check("POST pools 新增 → 501（不假装支持多池）", st == 501, "实际 %s" % st)

st, _ = call("POST", "/admin/pools/default/state", {"running": True})
check("POST pools state → 200", st == 200, "实际 %s" % st)

print()
print("=" * 72)
if failures:
    print("结果：%d 项检查，%d 项失败" % (checks, len(failures)))
    for f in failures:
        print("  ✗ %s" % f)
    sys.exit(1)
else:
    print("结果：%d 项检查全部通过" % checks)
print("=" * 72)
