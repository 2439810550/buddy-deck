#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
新旧功能对照的**可重跑检查器**（t27）。

## 为什么要有这个脚本

`docs/feature-parity.md` 是一份**一次性快照**。但 `src/` 一直在被并发修改 ——
本团队在**同一轮会话里**至少四次因为「记录与实现不同步」而误判：

    · 我把「M10 用量账本缺失」写进表 —— 它 07:17 就已落盘（我写表之前）
    · 我把「/admin/* 两头都不存在」写进表 —— 它 07:11 就已落盘
    · 队长基于同样过期的信息做过两次推演

**一份会过期的表，价值随并发写在衰减；一个能重跑的检查，价值不会衰减。**

所以：**表负责「解释」，脚本负责「当前事实」。** 有冲突时以脚本为准。

## ⚠⚠ 设计上最重要的一条：为什么不能 naive grep

这条必须写在最前面，因为**将来改这个脚本的人最容易在这里犯错**。

**「提到过这个词」≠「做过了」。**

本任务开工时的实测证据：

    $ grep "checkin" src/**/*.cs
    → 4 命中

如果据此报「checkin 已实现」，那是**假绿**。实际这 4 处是：

    Vault.cs:74            [JsonPropertyName("lastCheckinAt")]   ← 数据字段
    Vault.cs:10            注释里提了一句
    AdminEndpoints.cs:426  LastCheckinAt = ...                    ← 同上

**是字段名，不是签到功能。** 而真正的签到功能要调上游
`billing/meter/daily-checkin` —— grep 它得到 **0 命中**，即**确实仍缺失**。

⇒ 所以本脚本的**每条检测特征都必须是「它必须产生的外部效果」**：
**上游端点路径、路由字面量、类型定义**——这些**不可能偶然出现在注释或字段名里**。
每条特征下面的 `why` 字段就是在回答「为什么它不会被偶然命中」。

**这是同一类错误的第三个变体**（前两个：清单过期、终端撒谎）：
    · 清单过期  → 对世界的**记录**没被验证
    · 终端撒谎  → 对世界的**观测**没被验证
    · naive grep → 对世界的**检索**没被验证（把"提及"当"实现"）
**共同点：拿来当依据的东西，本身没被验证过。**

## ⚠⚠ 第二条：「沉默的判定 = 下一个假绿」

有些东西 **grep 根本判不出来** —— 比如「agent 循环有没有真的跑起来」
「设置 POST 有没有真的落盘」。这类项**必须显式出现在 `UNKNOWN` 那一档**，
**不许让它静默消失**。

理由：一个不报错的检查器，会让下一个人以为「没消息 = 没问题」。
**把「我判不了」说出来，比硬猜一个结论安全得多。**

所以本脚本是三态，不是两态：

    [已实现] IMPLEMENTED  —— 找到实现特征
    [仍缺失] MISSING      —— 没找到，且我们**知道该找什么**
    [待人工] UNKNOWN      —— **grep 判不了**，附「为什么判不了 + 该怎么做」

## 用法

    python tools/check_parity.py              # 人读（默认，彩色）
    python tools/check_parity.py --json       # 给 CI / 界面读
    python tools/check_parity.py -v           # 连已实现项也列出来

退出码：0 = 没有 MISSING（UNKNOWN 不算失败）；1 = 有 MISSING。
**注意**：UNKNOWN 不导致非零退出 —— 它是「需要人看」，不是「检查失败」。
"""

import argparse
import io
import json
import os
import re
import subprocess
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# ── 路径 ──────────────────────────────────────────────────────────
# 脚本放在 _next/tools/ 下，仓库根是上一级。
HERE = os.path.dirname(os.path.abspath(__file__))
NEXT_ROOT = os.path.dirname(HERE)
SRC = os.path.join(NEXT_ROOT, "src")
LEGACY_SRC = os.path.join(os.path.dirname(NEXT_ROOT), "workbuddy_api", "src")

# 扫描范围：新版自有的两个工程（App 是 UI，不参与协议对照）
SCAN_DIRS = [os.path.join(SRC, "BuddyDeck.Core"), os.path.join(SRC, "BuddyDeck.Gateway")]


# ── 检测基础设施 ──────────────────────────────────────────────────

def grep(pattern, dirs=None, literal=True, regex=False):
    """在 src/ 下找 pattern，返回 [(文件, 行号, 行内容)]。

    **故意用 Python 自己读文件**，不 shell out 到 grep / Select-String。
    理由：本团队刚踩过一次「终端输出撒谎」的坑（PowerShell 把好文件的中文
    显示成乱码，被误报为「文件编码损坏」）。
    **自己读字节、自己解码，才能控制观测链路。**
    """
    hits = []
    for root in (dirs or SCAN_DIRS):
        for dirpath, _dirnames, filenames in os.walk(root):
            for name in filenames:
                if not name.endswith(".cs"):
                    continue
                path = os.path.join(dirpath, name)
                try:
                    with io.open(path, encoding="utf-8") as handle:
                        for lineno, line in enumerate(handle, 1):
                            matched = (re.search(pattern, line) is not None
                                       if regex or not literal
                                       else pattern in line)
                            if matched:
                                rel = os.path.relpath(path, NEXT_ROOT).replace("\\", "/")
                                hits.append((rel, lineno, line.strip()))
                except (OSError, UnicodeDecodeError):
                    # 读不了的文件跳过而不是崩掉 —— 一个坏文件不该让整个检查失效
                    continue
    return hits


class Check:
    """一条检测项。

    @param ident   编号（M1 / R3 …），与 docs/feature-parity.md 对齐
    @param title   人读的名字
    @param pattern **行为特征**（见模块顶部：绝不能是模块名/类名）
    @param why     为什么这个特征**不可能偶然命中** —— 必填，这是防 naive grep 的关键
    @param note    补充说明（判 UNKNOWN 时写清"该怎么人工确认"）
    @param intentional  判为「有意不做」时给理由。**给了它就不再是缺口** ——
                        见 docs/feature-parity.md §3。这是为了防「检查器被噪音淹没」：
                        有意不做的东西如果每次都报，人就会开始忽略整个输出。
    """

    def __init__(self, ident, title, pattern=None, why="", note="", literal=True,
                 intentional=""):
        self.ident = ident
        self.title = title
        self.pattern = pattern
        self.why = why
        self.note = note
        self.literal = literal
        self.intentional = intentional

    def run(self):
        # 「有意不做」优先：这类项**不参与**缺口统计。
        # 但它仍然会被检测 —— 如果哪天它突然命中了（有人做了），
        # 状态会从 INTENTIONAL 变成 IMPLEMENTED，那是**有意义的变化**，会被看见。
        if self.intentional and self.pattern is not None:
            hits = grep(self.pattern, literal=self.literal)
            if hits:
                return "IMPLEMENTED", hits, "（原判为有意不做，现已被实现 —— 需更新 §3）"
            return "INTENTIONAL", [], self.intentional

        if self.pattern is None:
            return "UNKNOWN", [], self.note

        hits = grep(self.pattern, literal=self.literal)
        return ("IMPLEMENTED" if hits else "MISSING"), hits, ""


# ── 检测表 ────────────────────────────────────────────────────────
#
# 特征选取原则（每条 why 都在回答同一件事）：
#   选「只有真做了这件事才会出现」的字符串 —— 上游端点路径、路由字面量、类型定义。
#   绝不选模块名/文件名/类名的普通词 —— 那会被注释与字段名命中（见模块顶部 checkin 的例子）。

CHECKS = [
    # ── 已实现项：作为**对照组** ──────────────────────────────────
    # 这几条已知是好的。它们的作用是证明检测机制本身有效 ——
    # 如果它们也报 MISSING，说明**检查器坏了**，而不是功能没了。
    Check("OK-1", "聊天转发（对照组）", "/v2/chat/completions",
          why="上游聊天端点路径，只可能出现在真实转发代码里"),
    Check("OK-2", "token 刷新（对照组）", "/v2/plugin/auth/token/refresh",
          why="上游刷新端点路径"),
    Check("OK-3", "模型目录（对照组）", "/v3/config",
          why="上游目录端点路径"),
    Check("OK-4", "用量账本 M10（对照组）", "class UsageLedger",
          why="**类型定义**而非提及 —— 字段名/注释不会写成 `class X`"),

    # ── 缺失项：业务面路由 ────────────────────────────────────────
    Check("M1", "Anthropic Messages 翻译层", "/v1/messages",
          why="路由字面量；旧版在 gateway.js:587 注册，新版要接必须写这个字符串"),
    Check("M3", "count_tokens 端点", "count_tokens",
          why="Anthropic 特有的路径段，上游协议里没有这个词，不可能偶然出现"),
    Check("M2", "OpenAI Responses 翻译层", "/v1/responses",
          why="路由字面量；旧版 gateway.js:605"),
    Check("M5", "Miora 生图/生视频客户端", "/api/ai/workbuddy-proxy",
          why="旧版 miora.js:61 的 MIORA_API_BASE 常量值 —— **代理路径**，"
              "比 grep `miora` 可靠得多（后者会被注释里提到 Miora 命中）"),
    Check("M6", "媒体工具定义注入", "workbuddy_generate_image",
          why="工具名前缀+固定名，由 media-tools.js 的 TOOL_PREFIX 拼出，"
              "注释里不会写具体工具名"),
    Check("M8", "联网搜索", "/agenttool/v1/search",
          why="上游 agenttool 端点路径（search.js:38）"),
    Check("M8b", "网页抓取", "/agenttool/v1/webfetch",
          why="上游 agenttool 端点路径（search.js:41）"),
    Check("M9", "生成结果中转 + HMAC 签名", "/v1/images/file/",
          why="路由前缀字面量；旧版 gateway.js:666"),

    # ── 缺失项：模块 ──────────────────────────────────────────────
    Check("M11", "每日签到", "billing/meter/daily-checkin",
          why="**上游端点路径**。这条就是本脚本的设计动机 —— "
              "grep `checkin` 会命中 Vault.cs 的 [JsonPropertyName(\"lastCheckinAt\")] 字段名（4 处假命中），"
              "而 grep 这个路径得到 0 —— 才如实反映「签到功能没做」"),
    Check("M12", "账号加密导出/导入", "scrypt",
          why="transfer.js 的核心 KDF；.NET 对应 Rfc2898DeriveBytes/scrypt 调用。"
              "注：.NET 也可能用 AesGcm 而不用 scrypt —— 故此条命中与否都需人工确认",
          note="若报 MISSING，仍需人工确认是否换用了别的 KDF 方案"),
    Check("M13", "429 恢复时间的文案解析", "parseRateLimitReset",
          why="旧版 ratelimit.js:51 的函数名。⚠ 新版若实现会用不同命名 —— "
              "故此条 MISSING 时**需人工确认**是否以别的形式存在",
          note="旧版从文案里抠 `2026-09-25 17:07:48 UTC+8`；"
               "新版 GatewayApp 只有 JSON 的 resetAt，缺文案式解析。"
               "**人工确认方式**：grep 上游 429 文案里的 `UTC+` / `重置` 等中文字面量"),
    Check("M15", "CC Switch 集成", "cc-switch",
          why="该工具的数据目录名，只有真集成才会出现"),
    Check("M18", "套餐促销/计费解析", "promoBadgePath",
          why="旧版 variants.js:35 的字段名；新版即使实现也会叫别的 —— 需人工确认",
          note="人工确认：新版是否解析 upstream.js 的 parsePromotions/modelWithCurrentPromotion"),
    Check("M19", "模型目录按账号落盘缓存", "catalogPath",
          why="旧版 catalog.js:19 的函数名 —— 同样是命名相关的弱特征",
          note="人工确认：ModelCatalog 是否把目录写盘"),

    # ── 有意不做：**不参与缺口统计**（对应 docs/feature-parity.md §3）─────
    # 为什么要单列这一档：有意不做的东西如果每次都报成「缺失」，
    # 输出会被噪音淹没，最终没人看 —— 那检查器就白做了。
    # 但它们**仍被检测**：哪天突然命中，状态会跳到 IMPLEMENTED，提醒该去更新 §3。
    Check("X-1", "客户端身份探测（读 App 版本）→ 有意不做", "detectAppInstall",
          why="旧版 client-identity.js 的函数名",
          intentional="§3.3：UA 的**形态**是硬要求、**版本号**不是（旧版注释自证："
                      "gateway ignores the version numbers for routing）。"
                      "探测要起子进程、跨平台路径各异，收益仅「版本号真实一点」，性价比极低。"
                      "新版用写死的 X-IDE-Type/Name —— 保留常量即可。"),
    Check("X-2", "独立状态目录（paths.js）→ 有意不做", "workbuddy-api",
          why="旧版状态目录名 `.workbuddy-api`，只有真做了隔离目录才会出现",
          intentional="§3.1：新版**刻意与旧版共用同一保险库文件**（auth.cn.json），"
                      "因为要和正在运行的旧网关、桌面 App 读同一份凭据。"
                      "照搬隔离目录会让用户在新界面导入的账号**旧网关看不见** —— 那是倒退。"),
    Check("X-3", "库入口聚合（index.js）→ 不需要", "from \"./variants.js\"",
          why="旧版 index.js 的 re-export 语法，只在 JS 模块体系里有意义",
          intentional="§3.2：纯库入口，把 25 个模块的导出重新聚合。"
                      "新架构下由 MSBuild/程序集引用处理，**这个问题天然不存在**。"),

    # ── 判不了的：强制显式列出 ────────────────────────────────────
    # 这些**故意不给 pattern**。理由见模块顶部「沉默的判定 = 下一个假绿」。
    Check("M7", "Agent 循环（网关代执行工具）", None,
          why="无可靠字符串特征",
          note="**为什么 grep 判不了**：核心行为是「把上游的 tool_calls 拦下来、自己执行、"
               "把结果回填再发一轮」—— 这是**控制流**，没有独特的字符串常量。"
               "**人工/自动确认方式**：E2E 用例 —— 上游回 tool_calls，断言网关**没有**把它"
               "透传给客户端，而是自己发起了第二次上游请求。"),
    Check("M14", "设置持久化（落盘）", None,
          why="无可靠字符串特征",
          note="**为什么 grep 判不了**：`SaveSettings` 这种名字在界面侧也有，"
               "无法区分「界面调了」与「网关真写了文件」。"
               "**已实测确认（e2e 2026-10-07）**：POST /admin/settings 只改内存，"
               "运行目录下只有 .log、无设置文件 ⇒ 重启即丢。"
               "**确认方式**：起真网关 → POST → 看运行目录是否出现设置文件。"),
    Check("M4", "协议公共零件（SSE 命名事件等）", None,
          why="无可靠字符串特征",
          note="M1/M2 的公共依赖（beginSse/writeEvent/toAnthropicStopReason）。"
               "**确认方式**：M1 与 M2 都实现后，看是否共用了一个辅助类。"),
]


# ── 跨仓库：旧版有、新版没有的模块 ────────────────────────────────

def legacy_only_modules():
    """列出旧版存在的 .js 模块，以及新版是否有同名概念。

    ⚠ 这条**只能脚本做**：C# 测试项目无法列出另一个仓库的文件。
    这正是「脚本不可被 E2E 测试替代」的硬理由。

    注意：这里只做**提示**，不下「缺失」判断 —— 因为「旧版有 A.js」
    不代表「新版必须有 A」；上面 CHECKS 才是判断的地方。
    """
    if not os.path.isdir(LEGACY_SRC):
        return None, "旧版源码目录不存在：%s" % LEGACY_SRC
    modules = sorted(n for n in os.listdir(LEGACY_SRC) if n.endswith(".js"))
    return modules, None


# ── 输出 ──────────────────────────────────────────────────────────

C_GREEN, C_RED, C_YELLOW, C_DIM, C_OFF = "\033[32m", "\033[31m", "\033[33m", "\033[2m", "\033[0m"
C_BLUE = "\033[34m"

LABEL = {
    "IMPLEMENTED": (C_GREEN, "[已实现]"),
    "MISSING": (C_RED, "[仍缺失]"),
    "UNKNOWN": (C_YELLOW, "[待人工]"),
    "INTENTIONAL": (C_BLUE, "[有意不做]"),
}


def main():
    parser = argparse.ArgumentParser(description="新旧功能对照的可重跑检查器（t27）")
    parser.add_argument("--json", action="store_true", help="输出 JSON（给 CI / 界面读）")
    parser.add_argument("-v", "--verbose", action="store_true", help="连已实现项也列出来")
    parser.add_argument("--no-color", action="store_true", help="禁用 ANSI 颜色")
    args = parser.parse_args()

    color = not args.no_color and not args.json

    results = []
    for check in CHECKS:
        status, hits, note = check.run()
        results.append({
            "id": check.ident,
            "title": check.title,
            "status": status,
            "pattern": check.pattern,
            "why": check.why,
            "note": note,
            "hits": [{"file": f, "line": ln, "text": t} for f, ln, t in hits[:5]],
            "hitCount": len(hits),
        })

    modules, legacy_err = legacy_only_modules()

    counts = {"IMPLEMENTED": 0, "MISSING": 0, "UNKNOWN": 0, "INTENTIONAL": 0}
    for r in results:
        counts[r["status"]] += 1

    if args.json:
        print(json.dumps({
            "generatedAt": _now(),
            "nextRoot": NEXT_ROOT,
            "legacyRoot": LEGACY_SRC,
            "legacyModuleCount": len(modules) if modules else 0,
            "legacyModules": modules,
            "legacyError": legacy_err,
            "summary": counts,
            "checks": results,
        }, ensure_ascii=False, indent=2))
        return 1 if counts["MISSING"] else 0

    def c(text, code):
        return "%s%s%s" % (code, text, C_OFF) if color else text

    print("=" * 72)
    print("新旧功能对照检查器 —— %s" % _now())
    print("新版：%s" % NEXT_ROOT)
    print("旧版：%s（只读）" % LEGACY_SRC)
    print("=" * 72)

    # ── 对照组 ──
    controls = [r for r in results if r["id"].startswith("OK-")]
    broken = [r for r in controls if r["status"] != "IMPLEMENTED"]
    print()
    print("① 对照组（已知已实现 —— 它们也失败说明【检查器本身坏了】）")
    for r in controls:
        code, label = LABEL[r["status"]]
        print("  %s %-28s %s" % (c(label, code), r["title"],
                                 c("命中 %d 处" % r["hitCount"], C_DIM) if r["hitCount"] else ""))
    if broken:
        # ── 对照组失败时的**可操作**自诊断 ────────────────────────
        #
        # ⚠ 为什么这里**故意不自动判断**是「特征失效」还是「功能被删」：
        #
        # 启发式可以猜（比如"项目里还有别的相关命中 → 多半是特征失效"），
        # 但猜错时会给出一个**自信的错答案**，把维护者引向错误的地方改。
        # 本次会话里团队栽的每一个坑，根因都是「一个没被验证的自信结论」，
        # 所以这里只把**两条路的证据**摆出来，判断权交回给人。
        print()
        print("  %s" % c("⚠⚠ 对照组失败 —— 先别信下面②③④的结论！检测机制本身可疑。", C_RED))
        for r in broken:
            print("     失败项：%s  没命中 `%s`" % (r["title"], r["pattern"]))
        print()
        print("  %s" % c("怎么区分两种原因（脚本不替你判，因为猜错会更糟）：", C_YELLOW))
        print("     %s" % c("① 特征串失效：目标代码被重构/改名了 → 该改【脚本】的这条特征", C_DIM))
        print("        %s" % c('自查：grep -rn "<上面的特征串>" src/BuddyDeck.Core src/BuddyDeck.Gateway', C_DIM))
        print("        %s" % c("整个项目都没有该串 ⇒ 多半属于①（代码里就不该有，脚本的特征过时了）", C_DIM))
        print("     %s" % c("② 功能真被删/回退：代码确实没了 → 该改【代码】，脚本是对的", C_DIM))
        print("        %s" % c('自查：git log -S "<特征串>" --oneline -- src/', C_DIM))
        print("        %s" % c("有删除记录 ⇒ 多半属于②（曾经有、被人拿掉了）", C_DIM))
        print()
        print("     %s" % c("⚠ 修完对应的一侧后重跑本脚本；对照组全绿之前，"
                            "本脚本的任何「缺失」结论都不可信。", C_YELLOW))

    # ── 缺失 / 待人工 ──
    missing = [r for r in results if r["status"] == "MISSING"]
    unknown = [r for r in results if r["status"] == "UNKNOWN"]

    print()
    print("② 仍缺失（%d 项）" % len(missing))
    if not missing:
        print("  （无）")
    for r in missing:
        code, label = LABEL["MISSING"]
        print("  %s %-6s %s" % (c(label, code), r["id"], r["title"]))
        print("      %s" % c("检测：`%s`" % r["pattern"], C_DIM))
        print("      %s" % c("依据：%s" % r["why"], C_DIM))
        if r["note"]:
            print("      %s" % c("注意：%s" % r["note"], C_YELLOW))

    print()
    print("③ 待人工判定（%d 项 —— grep 判不了，**不是**没问题）" % len(unknown))
    if not unknown:
        print("  （无）")
    for r in unknown:
        code, label = LABEL["UNKNOWN"]
        print("  %s %-6s %s" % (c(label, code), r["id"], r["title"]))
        print("      %s" % c(r["note"], C_DIM))
    if unknown:
        print()
        print("  %s" % c("↑ 这一档刻意不做自动结论：**沉默的判定 = 下一个假绿**。",
                         C_YELLOW))

    # ── 有意不做 ──
    intentional = [r for r in results if r["status"] == "INTENTIONAL"]
    print()
    print("④ 有意不做（%d 项 —— **不是缺口**，列出来只为防止将来被误报）" % len(intentional))
    if not intentional:
        print("  （无）")
    for r in intentional:
        code, label = LABEL["INTENTIONAL"]
        print("  %s %-6s %s" % (c(label, code), r["id"], r["title"]))
        print("      %s" % c("理由：%s" % r["note"], C_DIM))
    if intentional:
        print()
        print("  %s" % c("↑ 这一档不参与缺口统计。有意不做的东西若每次都报，"
                         "人会开始忽略整个输出 —— 检查器就白做了。", C_DIM))

    # ── 跨仓库 ──
    print()
    print("⑤ 旧版模块清单（跨仓库对比 —— 这是脚本**不可被 C# 测试替代**的能力）")
    if legacy_err:
        print("  %s" % c(legacy_err, C_YELLOW))
    else:
        print("  旧版共 %d 个 .js 模块：%s" % (len(modules), ", ".join(modules)))
        print()
        print("  %s" % c("注：这里只列举，不下「缺失」判断 —— 「旧版有 A.js」不等于「新版必须有 A」。"
                         "判断在②③两档。", C_DIM))

    # ── 合计 ──
    print()
    print("=" * 72)
    print("合计：已实现 %d · 仍缺失 %d · 待人工 %d · 有意不做 %d" % (
        counts["IMPLEMENTED"], counts["MISSING"], counts["UNKNOWN"], counts["INTENTIONAL"]))
    print()
    print("⚠ 本脚本只检查**当前代码事实**。它不是 docs/feature-parity.md 的替代 ——")
    print("  表负责「为什么/怎么排版/依赖顺序」，脚本负责「此刻是什么状态」。")
    print("  **两者冲突时，以本脚本为准。**")
    print("=" * 72)

    return 1 if counts["MISSING"] else 0


def _now():
    from datetime import datetime, timezone
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


if __name__ == "__main__":
    sys.exit(main())
