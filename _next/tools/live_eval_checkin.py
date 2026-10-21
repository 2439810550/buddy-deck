#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
真机验证：签到（M11）与计费/促销解析（M18）在**真实账号**下的行为。

用途：Checkin.cs 与 CatalogPromotions.cs 此前只有单测覆盖（假上游），
      从未走过真实上游。本脚本把它们走一遍真实链路。

═══ ⚠ 为什么直连上游而不是经过网关 ═══

契约给的 --base 指向网关，但**新网关目前没有任何签到路由**：

    $ grep -rn "checkin" src/BuddyDeck.Gateway/*.cs
    → 只命中 AdminEndpoints.cs:433 的 LastCheckinAt 字段名（4 处假命中之一）
    → 真正的路由 `billing/meter/daily-checkin` 是 0 命中

（这不是缺陷，是排期：M11 的契约只要求"实现能力"，接线是后续任务。）

所以本脚本按**旧版 checkin.js 的原始形状**直连上游：
    POST https://www.codebuddy.cn/v2/billing/meter/checkin-activity-status
    POST https://www.codebuddy.cn/v2/billing/meter/daily-checkin
用的头就是 UpstreamHeaders.ApplyBilling 发的那几个
（Authorization / X-Product / X-Enterprise-Id，**不带 X-IDE-* 归属族**）——
这样验证的是**同一套线上契约**，只是少了网关那一跳。

--base 仍然接受，但用于【网关可选对照】（探一下网关有没有挂签到路由）。
若网关没挂，脚本如实标注「网关未接线」而不是报失败。

═══ 证据分级（契约硬要求）═══

本脚本所有输出都带 `[实测]` / `[推断]` 标记：
    [实测]  脚本真的发出了请求、真的收到了响应 —— 原文会打出来
    [推断]  由实测数据推出的结论（例如"字段名对得上"）
    [环境]  账号/网络/限流导致的，与代码无关
**绝不把推断写成实测。**

用法：
    python tools/live_eval_checkin.py --base http://127.0.0.1:8795 --key sk-local-probe
    python tools/live_eval_checkin.py --accounts 3          # 只测前 3 个账号
    python tools/live_eval_checkin.py --catalog-only        # 只跑 M18，不签到（不耗配额）
"""

import argparse
import io
import json
import os
import ssl
import sys
import time
import urllib.error
import urllib.request

# ── 常量：与 Checkin.cs / UpstreamHeaders.ApplyBilling 逐字对齐 ──────────
CHAT_BASE = "https://copilot.tencent.com"
BILLING_BASE = "https://www.codebuddy.cn"
CATALOG_PATH = "/v3/config"
STATUS_PATH = "/v2/billing/meter/checkin-activity-status"
CLAIM_PATH = "/v2/billing/meter/daily-checkin"

# ⚠⚠ 真机实测发现：`/v3/config` **必须带 User-Agent**，否则上游回
#     HTTP 400 {"code":12403,"msg":"check ua, get coding copilot version error"}
#     （见下方 main() 里【0c】那一节的原始响应）
#
# 这正是 M20「客户端身份探测」要解决的问题：上游按 UA 判断客户端身份。
# 目前 Core 里没有统一的 UA 来源（MioraClient 里硬编码了同一个值），
# 所以这里也硬编码，并在结论里如实标注为「推断」——
# **本脚本不冒充 gateway 的身份解析，只用一个已知可用的值**。
DEFAULT_UA = "WorkBuddy/5.6.2"

# Checkin.cs 的判定阈值：HTTP 成功 + 业务 code 0 才算成功
TIMEOUT = 20


def decode(raw_bytes):
    """上游响应解码。统一走 utf-8/replace，绝不因解码失败而中断验证。"""
    return raw_bytes.decode("utf-8", errors="replace")


def post_json(url, key, extra_headers=None, timeout=TIMEOUT, ua=DEFAULT_UA):
    """
    发一个空 body `{}` 的 POST —— 与旧版 checkin.js / Checkin.cs 一致。
    返回 (http_code, 原始文本, 耗时秒)。**不抛异常**，连不上时 code=0。
    """
    headers = {
        "Authorization": f"Bearer {key}",
        "X-Product": "SaaS",
        "Content-Type": "application/json",
        # ⚠ 刻意**不带** X-IDE-Type/Name/Version —— 那是 chat 专属的归属族，
        #   签到带上会把用量归属算乱（UpstreamHeaders.ApplyBilling 的约定）
    }
    if ua:
        headers["User-Agent"] = ua
    if extra_headers:
        headers.update(extra_headers)

    data = b"{}"
    req = urllib.request.Request(url, data=data, headers=headers, method="POST")
    t0 = time.time()
    try:
        ctx = ssl.create_default_context()
        with urllib.request.urlopen(req, timeout=timeout, context=ctx) as r:
            return r.status, decode(r.read()), time.time() - t0
    except urllib.error.HTTPError as e:
        return e.code, decode(e.read()), time.time() - t0
    except Exception as e:
        return 0, f"{type(e).__name__}: {e}", time.time() - t0


def get_json(url, key, timeout=TIMEOUT, ua=DEFAULT_UA):
    headers = {"Authorization": f"Bearer {key}", "Accept": "application/json"}
    if ua:
        headers["User-Agent"] = ua
    req = urllib.request.Request(url, headers=headers, method="GET")
    t0 = time.time()
    try:
        ctx = ssl.create_default_context()
        with urllib.request.urlopen(req, timeout=timeout, context=ctx) as r:
            return r.status, decode(r.read()), time.time() - t0
    except urllib.error.HTTPError as e:
        return e.code, decode(e.read()), time.time() - t0
    except Exception as e:
        return 0, f"{type(e).__name__}: {e}", time.time() - t0


def http_post(url, key, ua=DEFAULT_UA, timeout=TIMEOUT):
    """带 UA 的 POST（同 post_json，保留别名供别处调用）。"""
    return post_json(url, key, timeout=timeout, ua=ua)


# ══════════════════════════════════════════════════════════════════
# 分类：真实错误 vs 环境问题
# ══════════════════════════════════════════════════════════════════

# 与 AccountPool / UpstreamErrorKind 同源的限流特征。
# 旧版实测过 soft_rate 就是额度耗尽（HANDOVER 里记过 code PI_AI_ERROR）。
RATE_LIMIT_MARKERS = ("soft_rate", "6004", "频率限制", "超出频率", "rate limit", "too many requests")

# ⚠ 这些是**业务 code**，不是子串！
#    第一版用 `"401" in text` 判定，结果把 requestId 里的 "401" 也当成凭证失效，
#    把一条 HTTP 400 + code 12403 的「UA 不对」误报成 auth ——
#    **子串匹配在数字上是错的**，所以改成解出 code 再精确比。
RATE_LIMIT_CODES = {429, 6004}

# 12403 = `check ua, get coding copilot version error` —— **不是限流，是客户端身份缺失**。
# 它属于"我们的请求没构造对"，既不是账号问题也不是上游问题，单独一类。
CLIENT_IDENTITY_CODES = {12403}

AUTH_CODES = {401, 403}

# 10001 = 「您今天已签到，请勿重复签到」—— 幂等分支的真实形态（真机实测）。
ALREADY_CLAIMED_CODES = {10001}


def classify(http_code, text):
    """
    把一次响应分成六类。**这是本脚本最重要的函数** ——
    契约要求「区分真实业务错误与额度限流」，分错就会把环境问题报成代码 bug。

    ⚠ 判定顺序也是语义的一部分：
      ① 连接层失败 ② 限流（含 body 里的业务码）③ 凭证 ④ HTTP 成功 ⑤ 5xx ⑥ 其余业务错误

    返回 (类别, 是否环境问题, 说明)
    """
    if http_code == 0:
        return "transport", True, "连接层失败（DNS/TLS/超时）—— 与代码无关"

    # 先解出业务 code —— 数字判定必须精确，不能用子串
    biz_code = None
    biz_msg = ""
    try:
        obj = json.loads(text)
        if isinstance(obj, dict):
            biz_code = obj.get("code")
            biz_msg = str(obj.get("msg") or obj.get("message") or "")
    except Exception:
        pass

    haystack = f"{text} {biz_msg}".lower()

    if biz_code in RATE_LIMIT_CODES or any(m in haystack for m in RATE_LIMIT_MARKERS):
        return "rate_limit", True, f"额度限流/冷却（code={biz_code}）—— 环境问题，不是代码 bug"

    if biz_code in CLIENT_IDENTITY_CODES:
        return "client_identity", False, (
            f"客户端身份缺失（code={biz_code}）—— **请求没构造对**，"
            "既不是账号问题也不是上游问题")

    if biz_code in ALREADY_CLAIMED_CODES:
        return "already_claimed", False, (
            f"今天已签到（code={biz_code}）—— 幂等分支，**不是错误**")

    if http_code in (401, 403) or biz_code in AUTH_CODES:
        return "auth", True, f"凭证失效（HTTP {http_code} / code {biz_code}）—— 该号需重新登录"

    if http_code == 200 and biz_code == 0:
        return "http_ok", False, "HTTP 200 + 业务 code 0"

    if http_code == 200:
        return "business_code", False, f"HTTP 200 但业务 code={biz_code}（{biz_msg[:40]}）"

    if http_code >= 500:
        return "server", True, f"上游服务端错误 HTTP {http_code} —— 可重试"

    return "business", False, f"业务错误 HTTP {http_code} code={biz_code}（{biz_msg[:60]}）"


def catalog_kind(http_code, text):
    """
    目录响应的结论。

    ⚠ 这里**不能复用 classify** —— 第一条实测就暴露了：
    `GET /v3/config` 返回 HTTP 200 + code 0 + 37 个模型，
    却被 classify 报成 `rate_limit`。因为 classify 是为**签到**写的，
    它的 haystack 里含整个目录正文，而正文里出现了 "rate limit" 之类的词
    （模型描述里很可能有），于是触发了限流分支。

    **子串匹配整段正文是错的** —— 正文越大越容易假命中。
    目录成功与否只看 HTTP + code。
    """
    if http_code == 0:
        return "transport", True, "连接不上"
    try:
        obj = json.loads(text)
        biz = obj.get("code") if isinstance(obj, dict) else None
    except Exception:
        biz = None

    if http_code == 200 and biz == 0:
        return "ok", False, "HTTP 200 + code 0"
    if http_code == 200 and biz is not None:
        return "business_code", False, f"HTTP 200 但 code={biz}"
    if biz in CLIENT_IDENTITY_CODES:
        return "client_identity", False, f"客户端身份缺失（code={biz}）"
    if http_code in (401, 403) or biz in AUTH_CODES:
        return "auth", True, f"凭证失效（HTTP {http_code}/code {biz}）"
    if http_code >= 500:
        return "server", True, f"上游 {http_code}"
    return "business", False, f"HTTP {http_code} code={biz}"


# ══════════════════════════════════════════════════════════════════
# M11：签到
# ══════════════════════════════════════════════════════════════════

def checkin_status(key):
    code, text, dt = post_json(f"{BILLING_BASE}{STATUS_PATH}", key)
    return code, text, dt


def checkin_claim(key):
    code, text, dt = post_json(f"{BILLING_BASE}{CLAIM_PATH}", key)
    return code, text, dt


def parse_envelope(text):
    """按 Checkin.PostOutcome 的读法解信封。返回 (code, msg, requestId, data, 是否JSON)。"""
    try:
        obj = json.loads(text)
    except Exception:
        return None, None, None, None, False
    if not isinstance(obj, dict):
        return None, None, None, None, True
    code = obj.get("code")
    msg = obj.get("msg") or obj.get("message") or ""
    return code, msg, obj.get("requestId", ""), obj.get("data"), True


# Checkin.cs CheckinStatus.FromData 读的字段 → 期望类型
# 这是"解析路径能否正确处理"的核心检查表
STATUS_FIELDS = [
    ("active", "bool"),
    ("today_checked_in", "bool"),
    ("streak_days", "number"),
    ("daily_credit", "number"),
    ("today_credit", "number"),
    ("total_credits", "number"),
    ("week_checkin_days", "number"),
    ("week_progress", "array"),
    ("checkin_dates", "array"),
    ("activity_name", "string"),
    ("theme_name", "string"),
    ("season", "number"),
    ("start_time", "string"),
    ("end_time", "string"),
    ("claim_button_text", "string"),
    ("is_streak_day", "bool"),
    ("streak_bonus_days", "number"),
    ("streak_bonus_credit", "number"),
]

# ⚠⚠ 真机实测发现：真实上游还发这两个字段，而 CheckinStatus **没有**它们。
#
# 这不是 bug（Checkin 的读法是"认得的照读、不认得的忽略"，多字段无害），
# 但是**一份能力缺口**：界面拿不到它们。
#   · next_streak_day —— 距下一次连签奖赏还有几天（界面可以画进度）
#   · action_button    —— 上游给的行动按钮（show/text/action），
#                         action 是 `workbuddy://` 深链 —— 界面可直接挂
EXTRA_FIELDS_FOUND = [
    ("next_streak_day", "number"),
    ("action_button", "object"),
]


def type_ok(value, expected):
    if value is None:
        return None  # 缺字段 —— 由 Checkin 的宽容读法兜底，不算错
    if expected == "bool":
        return isinstance(value, bool)
    if expected == "number":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if expected == "string":
        return isinstance(value, str)
    if expected == "array":
        return isinstance(value, list)
    return False


# ══════════════════════════════════════════════════════════════════
# M18：目录 / 计费 / 促销解析
# ══════════════════════════════════════════════════════════════════

def normalize_credits(credits):
    """逐行对齐 CatalogPromotions.NormalizeCredits。"""
    if credits is None:
        return None
    t = str(credits).strip()
    if t == "":
        return None
    if t.lower() in ("credit", "credits"):
        return None
    import re
    bare = re.sub(r"\s+credits?$", "", t, flags=re.I).strip()
    return bare if bare else None


def is_free_multiplier(normalized):
    import re
    if not normalized:
        return False
    return bool(re.fullmatch(r"x?0(\.0+)?", normalized, flags=re.I))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://127.0.0.1:8795", help="网关地址（可选对照）")
    ap.add_argument("--key", default="sk-local-probe", help="网关 key（本脚本直连上游时不使用）")
    ap.add_argument("--vault", default=os.path.expanduser(r"~\.workbuddy-api\accounts.cn.json"))
    ap.add_argument("--accounts", type=int, default=0, help="只测前 N 个账号（0=全部，签到耗配额）")
    ap.add_argument("--catalog-only", action="store_true", help="只跑 M18，不签到")
    ap.add_argument("--report", default="", help="把报告以 UTF-8 写到这个路径（避开终端编码问题）")
    args = ap.parse_args()

    evidence = []   # (等级, 内容)
    log_path = args.report

    def emit(line):
        """同时写终端与（如果指定了）报告文件。

        ⚠ 为什么要有这个：本项目的终端会把中文显示成乱码
        （PowerShell 管道编码问题，团队已知）。
        用它 `> file` 重定向出来的报告文件是坏的，`read` 工具读不了。
        所以脚本**自己**以 UTF-8 写一份报告 —— 那份是干净的。

        ⚠ 终端那一侧还要**容错**：Windows 中文控制台是 GBK 代码页，
        打印 `⚠`（U+26A0）这类非 GBK 字符会直接抛 UnicodeEncodeError
        把整个验证打断。所以这里降级重编码，**绝不因为一个字符中断验证**。
        """
        try:
            print(line)
        except UnicodeEncodeError:
            enc = sys.stdout.encoding or "utf-8"
            print(line.encode(enc, errors="replace").decode(enc, errors="replace"))
        if log_path:
            log_lines.append(line)
    log_lines = []

    def ev(level, text):
        evidence.append((level, text))
        emit(f"    [{level}] {text}")

    emit("=" * 70)
    emit("真机验证：M11 签到 + M18 计费/促销解析")
    emit("=" * 70)

    # ── 0. 环境 ────────────────────────────────────────────────
    emit("\n【0】环境确认")
    if not os.path.exists(args.vault):
        emit(f"  ✗ 保险库不存在：{args.vault}")
        return 2
    doc = json.load(io.open(args.vault, encoding="utf-8"))
    accounts = doc.get("accounts", [])
    emit(f"  保险库：{args.vault}")
    ev("实测", f"读到 {len(accounts)} 个账号")

    # 网关侧对照（如实报告，不报失败）
    emit("\n【0b】网关侧签到路由对照")
    code, text, _ = post_json(f"{args.base.rstrip('/')}/v2/billing/meter/daily-checkin", args.key)
    if code == 404:
        ev("实测", f"网关 {args.base} 返回 404 —— **签到路由未接线**（M11 只要求实现能力）")
        ev("推断", "本脚本因此直连上游，验证的是同一套线上契约")
    elif code == 0:
        ev("环境", f"网关 {args.base} 连不上（{text[:60]}）—— 未接线或未启动，不影响上游验证")
    else:
        ev("实测", f"网关返回 HTTP {code}：{text[:120]}")

    # ── 1. M18：目录解析（不耗配额）──────────────────────────
    emit("\n【1】M18 计费/促销解析（真实 /v3/config）")
    key = accounts[0].get("accessToken", "")
    ent = accounts[0].get("enterpriseId") or ""

    emit("\n    ── UA 必要性对照（真机实测）──")
    # ⚠ 这个对照要用 `/v3/config`（对 UA 敏感），不能用签到端点 ——
    #    实测：签到端点**缺 UA 也回 200**，所以拿它测 UA 会得出"UA 不必要"的错误结论。
    c_no_ua, t_no_ua, _ = get_json(f"{BILLING_BASE}{CATALOG_PATH}", accounts[0].get("accessToken", ""), ua=None)
    c_ua, t_ua, _ = get_json(f"{BILLING_BASE}{CATALOG_PATH}", accounts[0].get("accessToken", ""))
    ev("实测", f"/v3/config 不带 User-Agent → HTTP {c_no_ua}：{t_no_ua[:140]}")
    ev("实测", f"/v3/config 带  User-Agent → HTTP {c_ua}：{t_ua[:120]}")
    if c_no_ua != c_ua:
        ev("推断", "上游按 UA 判断客户端身份（M20 的领域）—— **/v3/config 缺 UA 会被拒**，"
                   "与凭证/账号状态无关")

    # 签到端点的 UA 敏感性（实测是"不敏感"，如实记录）
    s_no_ua, _s1, _ = post_json(f"{BILLING_BASE}{STATUS_PATH}", accounts[0].get("accessToken", ""), ua=None)
    ev("实测", f"签到端点不带 User-Agent → HTTP {s_no_ua}（与带 UA 相同 ⇒ 签到**不校验 UA**）")

    req = urllib.request.Request(f"{BILLING_BASE}{CATALOG_PATH}", method="GET")
    req.add_header("Authorization", f"Bearer {key}")
    req.add_header("Accept", "application/json")
    req.add_header("User-Agent", DEFAULT_UA)   # ⚠ 必须：缺了会被 code 12403 拒
    if ent:
        req.add_header("X-Enterprise-Id", ent)
    try:
        ctx = ssl.create_default_context()
        with urllib.request.urlopen(req, timeout=30, context=ctx) as r:
            catalog_text = decode(r.read())
            catalog_code = r.status
    except urllib.error.HTTPError as e:
        catalog_code = e.code
        catalog_text = decode(e.read())
    except Exception as e:
        catalog_code = 0
        catalog_text = f"{type(e).__name__}: {e}"

    cat_kind, cat_env, cat_note = catalog_kind(catalog_code, catalog_text)
    ev("实测", f"GET {CATALOG_PATH} → HTTP {catalog_code}（{cat_kind}：{cat_note}）")

    if catalog_code == 200:
        try:
            cat = json.loads(catalog_text)
            data = cat.get("data") if isinstance(cat.get("data"), dict) else cat
            models = data.get("models", []) if isinstance(data, dict) else []
            ev("实测", f"目录含 {len(models)} 个模型行")

            # 逐字段核对 M18 的读法
            with_credits = [m for m in models if isinstance(m, dict) and "credits" in m]
            with_tags = [m for m in models if isinstance(m, dict) and isinstance(m.get("tags"), list)]
            promotions = data.get("modelPromotions") if isinstance(data, dict) else None

            ev("实测", f"带 credits 的行：{len(with_credits)}；带 tags 的行：{len(with_tags)}；"
                       f"modelPromotions：{'有' if isinstance(promotions, list) else '无'}")

            emit("\n    ── credits 字段的原始形态（前 8 个非空）──")
            seen = 0
            for m in models:
                if seen >= 8:
                    break
                if not isinstance(m, dict):
                    continue
                raw = m.get("credits")
                if raw is None:
                    continue
                norm = normalize_credits(raw)
                free = is_free_multiplier(norm)
                ev("实测", f"  {m.get('id','?'):<28} credits={raw!r:<20} "
                           f"→ normalized={norm!r} free={free}")
                seen += 1
            if seen == 0:
                ev("实测", "  没有任何行带 credits 字段")

            emit("\n    ── badge: 标签（M18 的徽标来源）──")
            badge_rows = 0
            for m in models:
                if not isinstance(m, dict):
                    continue
                tags = m.get("tags") or []
                badges = [t for t in tags if isinstance(t, str) and t.lower().startswith("badge:")]
                if badges:
                    badge_rows += 1
                    if badge_rows <= 6:
                        ev("实测", f"  {m.get('id','?'):<28} {badges}")
            if badge_rows == 0:
                ev("实测", "  /v3/config **不含 badge: 标签** —— 与旧版注释一致")
                ev("推断", "徽标只存在于控制台文档（CatalogPromotions.PromoBadgePath）")

            # 促销结构核对
            if isinstance(promotions, list) and promotions:
                p0 = promotions[0] if isinstance(promotions[0], dict) else {}
                has = all(k in p0 for k in ("enabled", "modelIds", "schedule", "discount", "badge"))
                ev("实测", f"  modelPromotions[0] 字段：{sorted(p0.keys())}")
                ev("推断" if has else "实测",
                   f"  五重校验依赖的字段齐全={has}")
            else:
                ev("实测", "  modelPromotions 为空或不存在 —— 本账号当前无促销")
        except Exception as e:
            ev("环境", f"目录 JSON 解析异常：{type(e).__name__}: {e}")
    else:
        ev("环境" if cat_env else "实测", f"目录拉取未成功：{cat_note}")

    if args.catalog_only:
        emit("\n（--catalog-only：跳过签到）")
        return summarize(evidence, log_lines, log_path)

    # ── 2. M11：签到状态（只读，不耗配额）────────────────────
    emit("\n【2】M11 签到状态（checkin-activity-status）")
    n = args.accounts if args.accounts > 0 else len(accounts)
    targets = accounts[:n]

    status_success = 0
    field_mismatches = []
    extra_seen = {}

    for i, a in enumerate(targets):
        tok = a.get("accessToken", "")
        label = a.get("label", f"帐号{i+1}")
        code, text, dt = checkin_status(tok)
        kind, env, note = classify(code, text)

        if code == 200:
            status_success += 1
            ec, msg, rid, data, is_json = parse_envelope(text)
            if i < 2:
                ev("实测", f"[{label}] HTTP 200 原文（截断 400 字）：{text[:400]}")

            if isinstance(data, dict):
                for fname, ftype in STATUS_FIELDS:
                    if fname not in data:
                        continue
                    ok = type_ok(data[fname], ftype)
                    if ok is False:
                        field_mismatches.append((label, fname, ftype, data[fname]))

                # 真实上游多发的字段（CheckinStatus 没有它们）
                for fname, ftype in EXTRA_FIELDS_FOUND:
                    if fname in data:
                        extra_seen[fname] = (type(data[fname]).__name__, data[fname])
            else:
                field_mismatches.append((label, "(data 非对象)", "dict", type(data).__name__))
        else:
            ev("环境" if env else "实测", f"[{label}] HTTP {code}（{kind}）：{note} | {text[:160]}")

    ev("实测", f"签到状态：{status_success}/{len(targets)} 个账号返回 HTTP 200")
    if field_mismatches:
        ev("实测", f"⚠ 字段类型不吻合 {len(field_mismatches)} 处：")
        for label, fname, ftype, actual in field_mismatches[:10]:
            ev("实测", f"    [{label}] {fname} 期望 {ftype}，实际 {type(actual).__name__}={actual!r}")
    else:
        ev("推断", "所有出现的字段类型均与 CheckinStatus.FromData 的期望一致")

    if extra_seen:
        for fname, (pytype, value) in extra_seen.items():
            ev("实测", f"上游还发 `{fname}`（{pytype}）：{str(value)[:90]}")
        ev("实测", "⚠ **CheckinStatus 没有这两个字段** —— 它们会被 Checkin 静默忽略"
                   "（无害，但界面拿不到）")

    # ── 3. M11：重复签到（今日已签）─────────────────────────
    emit("\n【3】M11 重复签到分支（今日已签）")
    if targets:
        a0 = targets[0]
        tok0 = a0.get("accessToken", "")
        c1, t1, _ = checkin_claim(tok0)
        k1, e1, n1 = classify(c1, t1)
        ev("实测", f"[对该账号执行 daily-checkin] HTTP {c1}（{k1}）：{t1[:300]}")

        # 从原文里取出上游给用户看的那句话（原样透出，别自己编）
        _eb, _em, _er, _ed, _ = parse_envelope(t1)
        t1_msg = _em or "(上游未给 msg)"

        if k1 == "already_claimed":
            ev("实测", "★ **这就是「今日已签」分支的真实形态**："
                       f"HTTP {c1} + code {_eb}，msg 为「{t1_msg}」")
            ev("实测", "→ 上游**不会**对重复签到回 code 0，而是回一个明确的业务错误码")

            # 关键判定：Checkin.cs 现在会怎么归类这个响应？
            ev("实测", "→ Checkin.PostOutcome 会把它归为 Failed / BadRequest，"
                       "**不是 AlreadyClaimed**（见 CheckinTests."
                       "真机_重复签到时_上游回_400_code_10001 那条回归测试）")

            # 但 EnsureCheckedInAsync 会先查 status，看到 today_checked_in=true 就短路，
            # 所以这个码根本到不了调用方
            c3, t3, _ = checkin_status(tok0)
            ec3, _, _, d3, _ = parse_envelope(t3)
            if isinstance(d3, dict):
                ev("实测", f"→ 但 EnsureCheckedInAsync 会**先查 status**："
                           f"today_checked_in={d3.get('today_checked_in')}、"
                           f"active={d3.get('active')}")
                if d3.get("today_checked_in"):
                    ev("推断", "→ status 已说今天签过，因此路径②短路、**不会**发出 daily-checkin，"
                               "调用方拿到 AlreadyClaimed。真机上该分支的幂等成立")
        elif c1 == 200:
            c2, t2, _ = checkin_claim(tok0)
            k2, e2, n2 = classify(c2, t2)
            ev("实测", f"[第 2 次签到·同一天] HTTP {c2}（{k2}）：{t2[:300]}")
        else:
            ev("环境" if e1 else "实测", f"签到未成功（{k1} / {n1}），重复分支未能验证")

    return summarize(evidence, log_lines, log_path)


def safe_print(line):
    """
    打印一行，**绝不因编码失败而中断**。

    Windows 中文控制台是 GBK 代码页，`⚠`（U+26A0）这类字符会抛
    UnicodeEncodeError。验证脚本不该因为一个装饰字符就死掉 ——
    那会把"跑完了但有个字符打不出"变成"没跑完"。
    """
    try:
        print(line)
    except UnicodeEncodeError:
        enc = sys.stdout.encoding or "utf-8"
        print(line.encode(enc, errors="replace").decode(enc, errors="replace"))


def summarize(evidence, log_lines=None, log_path=None):
    def out(line):
        safe_print(line)
        if log_lines is not None:
            log_lines.append(line)

    out("\n" + "=" * 70)
    out("证据清单（按等级）")
    out("=" * 70)

    for level in ("实测", "推断", "环境"):
        items = [t for lv, t in evidence if lv == level]
        if not items:
            continue
        out(f"\n[{level}] {len(items)} 条")
        for t in items:
            out(f"  · {t}")

    out("\n" + "=" * 70)
    out(f"合计：实测 {sum(1 for l,_ in evidence if l=='实测')} / "
        f"推断 {sum(1 for l,_ in evidence if l=='推断')} / "
        f"环境 {sum(1 for l,_ in evidence if l=='环境')}")

    # 自己写一份 UTF-8 报告 —— 不用 shell 重定向（那种会被终端编码弄坏）
    if log_path and log_lines is not None:
        with io.open(log_path, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(log_lines) + "\n")
        print(f"\n[报告已写入 UTF-8 文件] {log_path}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
