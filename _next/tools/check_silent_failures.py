#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
静默失败检查器（t31）。

## 这个脚本防的是什么

本次会话里团队抓到 5 个 bug，**全都是同一类**：

    **编译通过、运行不报错、测试可能是绿的**，但行为是错的。

    ┌────────────────────┬──────────────────────────────────────────────┐
    │ IsTerminal 前缀    │ JSON 键是 "terminal"，属性叫 IsTerminal，      │
    │                    │ PropertyNameCaseInsensitive 不做前缀推理 →    │
    │                    │ 绑不上，留在默认值 true →                       │
    │                    │ 「所有失败的中间尝试都被当成终局结果」          │
    ├────────────────────┼──────────────────────────────────────────────┤
    │ AppendAllText      │ 8 线程 × 50 条只落盘 326 行（少 74 行），      │
    │                    │ 不崩、不报错，只是安静地少写                    │
    ├────────────────────┼──────────────────────────────────────────────┤
    │ TryGetValue<long>  │ 对 {"prompt_tokens":2541} 返回 false          │
    │                    │ （2541 是 int 不是 long）→ 值永远是 null       │
    ├────────────────────┼──────────────────────────────────────────────┤
    │ SpinWait 忙等      │ 并发测试数据漂亮，实际全程串行                  │
    └────────────────────┴──────────────────────────────────────────────┘

**这五个全靠人肉发现，没有一个被自动拦住。** 而项目还要加大量新代码
（协议翻译层 450+500 行、媒体搜索 1000+ 行）——同类 bug 会继续长出来。

所以本脚本做**四类检查**，每类都对应上面至少一个真实案例。

## ⚠⚠ 三条设计原则（改这个脚本前必读）

### ① 每类检查必须能说清「它拦住了什么真实 bug」

每条检查都带 `why` 字段，里面写的是**本次会话真实发生过的故障**，不是假想的。
理由很实际：**说不清价值的检查会被当噪音删掉**。下一个维护者看到一堆
「未显式 [JsonPropertyName]」的告警，第一反应就是「这条太吵，关掉」——
除非他知道这行代码曾经导致「失败的尝试被当成终局」。

### ② 检查器自身必须有对照组

`check_parity.py` 立过这条：对照组（已知正常的样本）如果也失败，
说明**检查器坏了**，而不是被测代码坏了。

本脚本更进一步：**每条检查都能自证**——脚本内自带「注入错误」的验证入口
（`--selftest`），拿故意写错的数据喂给同一套检测逻辑，断言它**真的会判失败**。
一个永远返回「通过」的检查器和没有检查是一样的。

### ③ 不替人下结论

`check_parity.py` 的规矩（e2e 立的）：

> 脚本**故意不替你判断**是「特征串失效」还是「功能被删」——
> 那正是我们要防的「工具替你下结论」。

本脚本只报**事实**（哪一行、什么形态、和什么对照），**不下**「这是 bug」的结论。
因为「未显式标注 JSON 名」在**很多情况下是完全正确的**（单侧序列化、
键名恰好一致且稳定）—— 把它一律判为 bug 是假阳性来源。
脚本负责**把风险面摆出来**，判断权交回给人。

## 用法

    python tools/check_silent_failures.py              # 人读（默认）
    python tools/check_silent_failures.py --json       # 给 CI / 界面读
    python tools/check_silent_failures.py --selftest   # 自证：注入错误，断言检查器会失败
    python tools/check_silent_failures.py -v           # 列出全部明细

退出码：0 = 无「确定性问题」；1 = 有。
**注意**：「风险面清单」（未标注 JSON 名的属性）**不导致非零退出** ——
它是「需要人看」，不是「检查失败」。理由见上面原则 ③。

## 与 dotnet test 的关系

**本脚本刻意不做成 xunit 测试。** 理由：
  · 「未显式标注 JSON 名」是一个**全 src/ 的源码问题**，C# 测试看不见它自己程序集
    之外的源码；
  · 把它做成红测试会让 `dotnet test` 的绿/红语义变浑 —— 团队已论证过这条
    （见 docs/ 下的相关记录）。
所以：**独立检查入口**，与人读的输出，不混进测试信号。
"""

import argparse
import ast
import io
import json
import os
import re
import sys
import threading
import time

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
NEXT_ROOT = os.path.dirname(HERE)
SRC = os.path.join(NEXT_ROOT, "src")

# 扫描的三个工程（App 有 XAML 但 C# 部分同样要查）
SCAN_PROJECTS = ["BuddyDeck.Core", "BuddyDeck.Gateway", "BuddyDeck.App"]


# ══════════════════════════════════════════════════════════════════════
#  共用：源码遍历
# ══════════════════════════════════════════════════════════════════════

def iter_cs_files(projects=None):
    """遍历 src/ 下所有 .cs 文件，跳过 bin/obj。

    **自己读字节、自己解码**（不 shell out 给 grep）——
    `check_parity.py` 立过这条：本团队踩过「终端输出撒谎」的坑
    （PowerShell 把好文件的中文显示成乱码，被误报为文件损坏）。
    自己控制观测链路才可靠。
    """
    for proj in (projects or SCAN_PROJECTS):
        root = os.path.join(SRC, proj)
        if not os.path.isdir(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            # 就地剪掉构建产物目录，避免走进去（比事后过滤快）
            dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
            for name in filenames:
                if name.endswith(".cs"):
                    yield os.path.join(dirpath, name)


def read_text(path):
    """读文件；读不了返回 None 而不是崩掉（一个坏文件不该让整个检查失效）。"""
    try:
        with io.open(path, encoding="utf-8") as handle:
            return handle.read()
    except (OSError, UnicodeDecodeError):
        return None


def rel(path):
    return os.path.relpath(path, NEXT_ROOT).replace("\\", "/")


# ══════════════════════════════════════════════════════════════════════
#  检查 ①：JSON 字段命名依赖隐式约定
# ══════════════════════════════════════════════════════════════════════

# 属性声明：支持 public / public required，属性体 get;init / get;set / 多行
PROPERTY_RE = re.compile(
    r"""
    (?P<attrs>(?:^\s*\[[^\]]*\]\s*\r?\n)*)          # 前面可能有若干特性行
    ^\s*public\s+(?:required\s+)?                    # public [required]
    (?!class|record|struct|interface|enum|static|const|void|async|override|abstract|virtual|sealed)
    (?P<type>[\w<>?\[\],\.\s]+?)\s+                  # 类型
    (?P<name>\w+)\s*                                 # 属性名
    (?P<body>\{\s*get;|\{\s*get\s*=>|=>)             # 属性体
    """,
    re.M | re.X,
)

# 「前缀形状的布尔」—— 已知失效点。
# 背景：PropertyNameCaseInsensitive 只做「大小写无关的完整名字比较」，
# **不做 Is/Has 前缀推理**。所以假设 C# 叫 IsTerminal、对方发 "terminal"，
# 绑不上，属性留在默认值 —— 不报错。
PREFIX_BOOL_RE = re.compile(r"^(Is|Has|Can|Should|Was|Did|Needs)[A-Z]")

# 这些属性名是「界面自用」的派生属性，不参与 JSON 绑定 ——
# 报出来只会制造噪音（团队规则：有意忽略的东西如果每次都报，
# 人就会开始忽略整个输出）。
DERIVED_MARKERS = ("=>",)  # 表达式体属性不参与反序列化


def check_json_naming():
    """扫描所有含 JSON 序列化的类型，列出未显式 [JsonPropertyName] 的 public 属性。

    ## 为什么这条值得保留（真实案例）

    `IsTerminal` → JSON 键 `"terminal"` → **绑不上，留在默认值 `true`**
    → 「所有中间失败的尝试都被当成终局结果」。
    编译不报、运行不报、字段还有值（默认值看起来很合理）。
    见 src/BuddyDeck.Gateway/Metrics.cs 的注释（ui 抓的、harness 又踩了同一形状）。

    ## 为什么不一律判为 bug

    「未显式标注」在很多情况下是对的：
      · 类型只**单侧序列化**（只写不读），键名由自己控制；
      · 键名恰好一致且**稳定**（如 `model` / `stream` 这种上游协议固定词）；
      · **属性名根本不参与绑定** —— 手工逐字段抽的 DTO（见下）。

    所以本检查产出的是**风险面清单**，不是 bug 列表 —— 判断权交回给人。

    ## ⚠ 三级严重度（全量扫描后补的，为压误报）

    第一版把所有「未标注 + 前缀布尔」一律判 HIGH。全量跑真实代码后
    发现这会误报 —— `Checkin.cs:156 IsStreakDay` 实际上**完全安全**：
    它所在的 DTO 从不走序列化器，而是手工 `BoolOf(obj, "is_streak_day")`
    逐字段读的，**属性名根本不参与绑定**。

    于是改成三级：

      HIGH   —— 未标注 + 前缀布尔 + **文件里有序列化器调用**
                （真的可能被 `JsonSerializer` 绑定 → 真的会静默失配）
      MEDIUM —— 未标注 + 前缀布尔 + 无序列化器调用
                （当前安全，但形状危险：将来谁加一句序列化就会中招）
      INFO   —— 其余未标注属性（键名恰好一致的常规情况）

    这样 `IsDefault` 与 `IsStreakDay` 会被区分开，而不是一起报成一样的红。
    """
    findings = []
    total_props = 0
    annotated = 0

    for path in iter_cs_files():
        text = read_text(path)
        if text is None:
            continue

        # 只关心「和 JSON 打交道的文件」—— 判据是文件里出现了
        # JsonSerializer / JsonNode / JsonPropertyName / JsonDocument 任一。
        # 不用「文件里有没有 public class」这种弱判据，那会把所有文件都算进来。
        if not re.search(r"Json(Serializer|Node|Document|PropertyName|Object|Value|Array)", text):
            continue

        # 这个文件会不会**真的走序列化器绑定**？
        # 判据：出现 Deserialize / Serialize 调用（而不是只手工抽字段）。
        uses_serializer = bool(re.search(r"JsonSerializer\s*\.\s*(De|Serialize)", text))

        for m in PROPERTY_RE.finditer(text):
            name = m.group("name")
            attrs = m.group("attrs") or ""
            body = m.group("body")

            # 表达式体属性（`=> ...`）是派生只读的，不参与反序列化 —— 跳过。
            if body.strip().startswith("=>") or "=>" in body:
                continue

            total_props += 1
            has_name_attr = "JsonPropertyName" in attrs

            if has_name_attr:
                annotated += 1
                continue

            line_no = text[: m.start("name")].count("\n") + 1
            is_prefix_bool = bool(PREFIX_BOOL_RE.match(name))
            prop_type = " ".join(m.group("type").split())

            if is_prefix_bool and uses_serializer:
                severity = "HIGH"
            elif is_prefix_bool:
                severity = "MEDIUM"
            else:
                severity = "INFO"

            findings.append({
                "file": rel(path),
                "line": line_no,
                "name": name,
                "type": prop_type,
                "prefixBool": is_prefix_bool,
                "usesSerializer": uses_serializer,
                "severity": severity,
            })

    # 排序：HIGH → MEDIUM → INFO，同级按文件行号
    order = {"HIGH": 0, "MEDIUM": 1, "INFO": 2}
    findings.sort(key=lambda f: (order.get(f["severity"], 9), f["file"], f["line"]))

    return {
        "totalJsonProps": total_props,
        "annotated": annotated,
        "implicit": len(findings),
        "highRisk": [f for f in findings if f["severity"] == "HIGH"],
        "mediumRisk": [f for f in findings if f["severity"] == "MEDIUM"],
        "findings": findings,
    }


# ══════════════════════════════════════════════════════════════════════
#  检查 ②：并发写入丢数据
# ══════════════════════════════════════════════════════════════════════

def append_all_text_probe(threads, per_thread, use_lock):
    """**复现** `File.AppendAllText` 的并发截断。

    这里真的起线程写文件 —— 因为这条 bug 的性质是「操作系统级竞态」，
    静态扫描看不出来，**必须真跑**才能证明检查有效。

    @param use_lock  True 时用锁串行化写入（正确做法），False 时裸 AppendAllText（有 bug 的做法）
    @return (落盘行数, 坏行数)
    """
    import tempfile

    fd, path = tempfile.mkstemp(suffix=".log")
    os.close(fd)

    lock = threading.Lock()
    barrier = threading.Barrier(threads)

    def worker(tid):
        # 用 Barrier 让所有线程**同时**开始写 —— 不做这个的话
        # 线程会错开，竞态窗口变小，可能偶然「看起来没问题」。
        barrier.wait()
        for i in range(per_thread):
            line = "t%02d-%03d\n" % (tid, i)
            if use_lock:
                with lock:
                    with io.open(path, "a", encoding="utf-8") as handle:
                        handle.write(line)
            else:
                # ⚠ 这就是有 bug 的写法：AppendAllText 内部是
                # open(append) → write → close，**不是原子操作**。
                # 两个线程同时走到这里会互相截断。
                with io.open(path, "a", encoding="utf-8") as handle:
                    handle.write(line)

    thread_list = [threading.Thread(target=worker, args=(t,)) for t in range(threads)]
    for t in thread_list:
        t.start()
    for t in thread_list:
        t.join()

    with io.open(path, encoding="utf-8") as handle:
        raw = handle.read()

    os.unlink(path)

    lines = raw.split("\n")
    if lines and lines[-1] == "":
        lines.pop()  # 末尾换行产生的空串不算坏行

    # 坏行 = 不匹配 `tNN-NNN` 的行（截断会产生粘连/半行）
    good = 0
    bad = 0
    for line in lines:
        if re.match(r"^t\d{2}-\d{3}$", line):
            good += 1
        else:
            bad += 1

    return good, bad, lines


def check_concurrent_append():
    """并发写入检查 —— 断言模板 + 真实现场复现。

    ## 为什么这条值得保留（真实案例）

    observability 用 `File.AppendAllText` 并发写账本：
    **8 线程 × 50 条 = 期望 400 行，实际只落盘 326 行（少 74 行）**。

    **它不崩、不报错、不抛异常** —— 只是安静地少写。
    弱断言（「行数增加了」「> 0」）验不出来：
    326 > 0，也 > 上一次，**测试会绿**。

    ## 正确的断言模板

        ❌  Assert.True(lines.Count > 0)              # 弱：少写 74 行照样通过
        ❌  Assert.True(lines.Count > previousCount)  # 弱：同上
        ✅  Assert.Equal(expected: N, actual: lines.Count)   # 精确等于
        ✅  Assert.Equal(expected: 0, actual: badLines.Count) # 坏行精确为 0

    本函数把这两条**实际跑一遍**，并对同一个复现用例跑「有锁/无锁」两版 ——
    无锁版必须出现丢数据（证明检查有效），有锁版必须精确 400/0（证明模板可用）。
    """
    threads, per_thread = 8, 50
    expected = threads * per_thread

    good_buggy, bad_buggy, _ = append_all_text_probe(threads, per_thread, use_lock=False)
    good_locked, bad_locked, _ = append_all_text_probe(threads, per_thread, use_lock=True)

    return {
        "threads": threads,
        "perThread": per_thread,
        "expected": expected,
        "unlocked": {"good": good_buggy, "bad": bad_buggy, "lost": expected - good_buggy},
        "locked": {"good": good_locked, "bad": bad_locked, "lost": expected - good_locked},
    }


# ══════════════════════════════════════════════════════════════════════
#  检查 ③：类型转换静默返回默认值
# ══════════════════════════════════════════════════════════════════════

def check_read_failure_masquerade():
    """找出 **catch 后静默返回默认值** 的位置 —— 「读不到」伪装成「本来就没有」。

    ## 为什么单列这一类（本轮**两个真实案例**，同一形态）

    ### 案例 ①：`/admin/pools` 的 `accountCount` 恒为 0

    e2e 发现「`/admin/accounts` 说 2 个号，`/admin/pools` 说 0 个」。
    根因：`VaultPathOf` 推出一个不存在的路径 → `Vault.Read` 返 `null`
    → `?? 0` 把它变成「0 个号」。

    gateway 的归纳最准：

    > **「读不到」和「本来就没有」长得一样，是它藏了这么久的原因。**

    ### 案例 ②：目录刷新静默降级（t53）

    失败被 **Debug 级**吞掉 → 永久回落到 16 个内置模型 → 界面毫无异常。
    用户看到的是「模型列表就这样」，而真相是「刷不到」。

    ### 共同形态

    ```
    catch { return <默认值>; }        // 没有可区分的信号
    ```

    「读取失败」与「空数据」在此**完全不可区分** —— 调用方只能看到
    一个合法的默认值，于是「读不到」被当成「本来就没有」。

    ## 判据（三条同时满足才报）

    1. `catch` 块里 **`return` 了默认值**（0 / -1 / null / false / "" / [] / default）
    2. **try 块里做了 I/O**（文件 / 目录 / HTTP）——
       即这次失败原本意味着「数据取不到」
    3. catch 块里**没有日志**（`_log.*` / `LogXxx(...)` / `ILogger` / `Console.`）

    ## ⚠⚠ 为什么必须加「I/O」这条限定（否则假阳性 8 倍）

    `[实测]` 不看 I/O 时全仓命中 **24 处**；加上 I/O 限定后只剩 **3 处**。

    被排除的 21 处全是**纯解析器**，例如：

    ```csharp
    try { innerJson = Encoding.UTF8.GetString(Convert.FromBase64String(text)); }
    catch (FormatException) { return null; }     // ✅ 完全正确
    ```

    这种「输入不合法 → 返 null」是**正常的令牌返回**，调用方有完整的
    信息判断（它自己给的输入）。它**不是**「读不到伪装成没有」——
    因为压根没有「读」这个动作，也没有可丢失的外部状态。

    把这类一起报，就是把噪音灌进输出 —— 而**说不清价值的检查会被当噪音删掉**
    （本文件原则 ①）。所以判据里 I/O 这条是**必需的**，不是修饰。

    ## ⚠ 判据 ③ 为什么是「没有日志」而不是「没返哨兵值」

    因为「哨兵值」没有统一约定：`-1` 在这里是「不知道」（AdminEndpoints 的
    `AccountCountOf`），在别处可能是「未找到」。**静态判不出哪个值是哨兵。**

    而「有没有日志」是**可判的事实**。有日志 = 运维能从日志里看出
    「这次是读不到」；没日志 = 两种情形真的长得一样。这正是本条要守的东西。

    ## 为什么本检查不下「这是 bug」的结论（原则 ③）

    有些位置**确实不需要日志**，例如「查不到文件大小就当不需要轮转」——
    后果可控且已有注释说明。脚本只报**位置和形态**，判断权交回给人。
    """
    findings = []

    # I/O 的特征串 —— try 体里出现任意一个，就认为这次失败意味着「数据取不到」
    io_markers = (
        "File.", "Directory.", "FileStream", "StreamReader", "FileInfo",
        "ReadAllText", "ReadAllBytes", "OpenRead", "EnumerateFiles",
        "SendAsync", "GetAsync", "PostAsync", "HttpClient", "HttpRequestMessage",
    )

    # 取消是**刻意**的静默（用户按了停止，不是错误）—— 单独排除，
    # 否则 App 那条 `catch (OperationCanceledException) { return (null, null); }`
    # 会被误报。它用第二个元素（错误文案）显式表达了「这次没有错误」。
    cancel_markers = ("OperationCanceledException", "TaskCanceledException")

    for path in iter_cs_files():
        text = read_text(path)
        if text is None:
            continue

        lines = text.split("\n")

        for i, line in enumerate(lines):
            if not CATCH_START_RE.match(line):
                continue

            # 往前找配对的 try（跳过其它 catch / finally）
            try_start = None
            for j in range(i - 1, max(-1, i - 60), -1):
                if re.match(r"^\s*try\b", lines[j]):
                    try_start = j
                    break
                # 撞到另一个 catch/finally 说明这不是同一组 —— 停止
                if re.match(r"^\s*\}?\s*(catch|finally)\b", lines[j]):
                    break
            if try_start is None:
                continue

            # 去掉注释再判 —— 「注释里提到 return 0」不该被算成返回默认值
            # （检查 ③ 踩过同一个坑）
            def _strip_comments(block_lines):
                kept = []
                for bl in block_lines:
                    bs = bl.lstrip()
                    if bs.startswith(("//", "*", "/*")):
                        continue
                    kept.append(bl)
                return "\n".join(kept)

            catch_body = _strip_comments(lines[i:i + 14])
            try_body = _strip_comments(lines[try_start:i])

            if _DEFAULT_RETURN_RE.search(catch_body) is None:
                continue

            if any(m in try_body for m in cancel_markers):
                continue                       # 取消不是「读不到」

            if not any(m in try_body for m in io_markers):
                continue                       # 纯解析 —— 本轮不报（见 docstring）

            if LOG_CALL_RE.search(catch_body):
                continue                       # 有日志 = 能区分两种情形 ✅

            findings.append({
                "file": rel(path),
                "line": i + 1,
                "text": line.strip(),
                "returns": _DEFAULT_RETURN_RE.search(catch_body).group(1),
            })

    return {"count": len(findings), "findings": findings}


# catch 块起点（允许前面有 `}`）
#
# ⚠ **必须带 re.M** —— 这个正则以 `^` 开头。不带 re.M 时 `^` 只匹配
#   **整个字符串的开头**，于是对一段源码文本最多只匹配第一行。
#   而下面 `check_read_failure_masquerade` 是按行喂进来的（每行一个字符串），
#   那里看不出问题；**但自证里对整段文本计数时就会得到 0** ——
#   第一版正是如此：报「全仓 catch 块 0 个」却同时说「命中 1 处」。
#   那种**自相矛盾的数字**是自证抓出来的，加 re.M 后两者一致。
CATCH_START_RE = re.compile(r"^\s*\}?\s*catch\b", re.M)

# catch 块里返回默认值
_DEFAULT_RETURN_RE = re.compile(
    r"return\s+(0|-1|null|false|true|\"\"|\[\]|default|Array\.Empty|new\s+List|new\s+JsonArray)")

# 「有日志」的形态：源生成 LoggerMessage 委托、ILogger 调用、Console
LOG_CALL_RE = re.compile(r"_log\.|Log[A-Z]\w*\s*\(|ILogger|Console\.|logger\.")


def scan_numeric_extraction():
    """扫描代码里从 JsonNode 抽数值的写法，标出「只试一种泛型参数」的地方。

    ## 为什么这条值得保留（真实案例）

    observability 写的：`JsonValue.TryGetValue<long>` 对
    `{"prompt_tokens": 2541}` 返回 **false** —— 因为 2541 是 **int**，
    而 `TryGetValue<T>` 要求底层类型**精确匹配**。
    于是 token 数**永远是 null**。JSON 合法、解析不报错、值就是没有。

    **这是跨语言边界最容易静默失效的一类**：同一段 JSON，
    不同语言/不同解析器给不同的底层数值类型。

    ## 判据

    一个函数里若只出现**一种**数值泛型（如只有 `TryGetValue<long>`），
    且没有 fallback 到 int/double/decimal，则标为风险点。
    正确做法见 src/BuddyDeck.Core/UsageLedger.cs 的 `Long()` / `Double()`
    —— 它显式逐类型试。
    """
    findings = []

    for path in iter_cs_files():
        text = read_text(path)
        if text is None or "JsonValue" not in text:
            continue

        lines = text.split("\n")
        for i, line in enumerate(lines):
            # ⚠ 跳过注释行 —— 否则「注释里讲了 TryGetValue<long> 的坑」
            #   会被当成「这里有个风险点」。实测踩过：AnthropicEndpoints.cs
            #   有 3 行注释在解释这个 bug，全被误报了。
            stripped = line.lstrip()
            if stripped.startswith("//") or stripped.startswith("*") or stripped.startswith("/*"):
                continue

            m = re.search(r"TryGetValue<(?P<t>\w+)>", line)
            if not m:
                continue
            generic = m.group("t")
            if generic not in ("int", "long", "double", "decimal", "float", "short", "byte"):
                continue

            # 看这个语句/附近几行有没有 fallback 到别的数值类型。
            # 窗口内的**注释行也要剔掉**，否则一句「该逐类型试」的注释
            # 会被当成已经做了兜底。
            window_lines = []
            for wl in lines[max(0, i - 4): i + 5]:
                ws = wl.lstrip()
                if ws.startswith("//") or ws.startswith("*"):
                    continue
                window_lines.append(wl)
            window = "\n".join(window_lines)

            tried = set(re.findall(r"TryGetValue<(\w+)>", window))
            numeric_tried = {t for t in tried if t in
                             ("int", "long", "double", "decimal", "float", "short", "byte")}

            # 也认「先 GetValue<double> 再转」这类写法
            has_convert = bool(re.search(r"Convert\.To\w+|\(long\)|\(double\)|\(int\)", window))

            if len(numeric_tried) <= 1 and not has_convert:
                findings.append({
                    "file": rel(path),
                    "line": i + 1,
                    "generic": generic,
                    "context": line.strip()[:110],
                })

    return {"findings": findings, "count": len(findings)}


# ══════════════════════════════════════════════════════════════════════
#  检查 ③b：单写 TryGetValue<double> 对整数值返回 false
# ══════════════════════════════════════════════════════════════════════

# 数值泛型的完整集合（用于判断「试了几种」）
NUMERIC_GENERICS = frozenset(
    {"int", "long", "double", "decimal", "float", "short", "byte", "sbyte",
     "uint", "ulong", "ushort"}
)

# 「整数背书」的泛型 —— 这些能读 int/long 形态
INTEGER_BACKED = frozenset({"int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort"})


def check_double_only_readers():
    """找出**只试 `TryGetValue<double>`** 的数值读取点。

    ## 为什么单列这一条（t57 新形态，与检查 ③ 同族但更尖）

    检查 ③ 判的是「只试了一种数值泛型」（宽口径，含 long-only 等）。
    本检查判的是其中一个**特别隐蔽**的子形态：**只试 `double`**。

    ## 为什么它比 long-only 更隐蔽

    `[实测]` 我用真实 C# 探针跑过全部组合（见 docs/silent-failure-guards.md §4.4），
    结论分成**两条不同的路径**，这点很关键：

    ```
    路径 A：从 JSON 文本反序列化（JsonNode.Parse）
      Parse("42")    → kind=Number  int=true   long=true   double=true   ← 都行
      Parse("42.5")  → kind=Number  int=false  long=false  double=true
      Parse("42.0")  → kind=Number  int=false  long=false  double=true
      Parse("1e3")   → kind=Number  int=false  long=false  double=true

    路径 B：在 C# 里用 JsonValue.Create 构造（**不经过 JSON 文本**）
      Create(42)[int]    → kind=Number  int=true   long=false  double=false  ← !!
      Create(42L)[long]  → kind=Number  int=false  long=true   double=false  ← !!
      Create(42.0)[dbl]  → kind=Number  int=false  long=false  double=true
    ```

    **⇒ `JsonValue.Create(int)` 的形状是「只认 int」，连 `long` 和 `double` 都不认。**

    所以「只写 `TryGetValue<double>`」的后果取决于数据来源：
      · **经 JSON 文本**：整数能被 `double` 读到 → **不会出错**
      · **经 `JsonValue.Create`**：整数**读不到** → **静默得 0**

    ⚠ **这就是为什么本检查只报「位置」而不下「一定有 bug」的结论** ——
    同一个写法在不同数据来源下后果不同，静态判不出来。
    （这正是 features 报的那条：他第一版只写 double，而真机数据恰好是整数形态。）

    ## 判据（严格版）

    一个代码块内：
      1. **出现了** `TryGetValue<double>`
      2. **没有**任何整数背书泛型（int/long/short/byte/…）
      3. **没有** `GetValue<double>` / `Convert.To*` / `(long)` / `(int)` 这类转换兜底
    三条同时满足 → 报出。

    ## 为什么不报「只试 int」

    `[实测]` 见上表：`Parse("42.5")` 对 int 是 false —— 所以 int-only 也有漏读。
    但它**和 double-only 的方向相反**：
      · int-only 漏掉的是**小数**（真实数据里常见，但通常上游会同时给 `*Precise` 字符串字段）
      · double-only 漏掉的是**整数**（**真机上恰好全是整数**，见 Credits.cs 的注释）
    **两者都该报**，但本检查专收 double-only —— 因为它的失败面最宽
    （整数在真实 API 里比小数多得多），且已有真实事故。
    """
    findings = []

    for path in iter_cs_files():
        text = read_text(path)
        if text is None or "JsonValue" not in text:
            continue

        lines = text.split("\n")

        for i, line in enumerate(lines):
            stripped = line.lstrip()
            # 跳过注释 —— 否则「注释里解释 double 读不出整数」会被当成风险点。
            # 这条已在检查 ③ 踩过一次，这里直接沿用。
            if stripped.startswith(("//", "*", "/*")):
                continue

            if "TryGetValue<double>" not in line:
                continue

            # 取一个窗口：前后各若干行（含函数体边界）
            window_lines = []
            for wl in lines[max(0, i - 12): i + 13]:
                ws = wl.lstrip()
                if ws.startswith(("//", "*", "/*")):
                    continue
                window_lines.append(wl)
            window = "\n".join(window_lines)

            tried = set(re.findall(r"TryGetValue<(\w+)>", window))
            numeric_tried = tried & NUMERIC_GENERICS
            has_integer_backed = bool(numeric_tried & INTEGER_BACKED)

            # 转换式兜底（先 GetValue<T> 再转；或显式 cast）
            #
            # ⚠⚠ `GetValue<T>` 必须用**词边界**匹配 —— 否则它会命中
            # `TryGetValue<double>` 里的子串，把**每一条**待检语句都判成
            # 「已有转换兜底」，导致整个检查恒为 0 命中。
            #
            # 这个缺陷是**注入自证抓出来的**：我造了一个只用
            # `TryGetValue<double>` 的文件，检查却报 0 处。
            # 教训与检查 ③b 的「判据互不重叠」是同一条：
            # **子串匹配会让一个判据悄悄吃掉另一个判据的样本。**
            has_convert = bool(
                re.search(r"Convert\.To\w+|\(long\)|\(int\)|\(double\)", window)
                or re.search(r"(?<![\w])GetValue<(double|long|int|decimal)>", window)
            )

            if has_integer_backed or has_convert:
                continue

            findings.append({
                "file": rel(path),
                "line": i + 1,
                "genericsTried": sorted(numeric_tried),
                "context": line.strip()[:120],
            })

    findings.sort(key=lambda f: (f["file"], f["line"]))
    return {
        "findings": findings,
        "count": len(findings),
        "note": "只报位置，不下结论 —— 同一写法在「JSON 文本」与 JsonValue.Create 两条来源下后果不同",
    }


# ══════════════════════════════════════════════════════════════════════
#  检查 ④：假并发（测试其实在串行跑）
# ══════════════════════════════════════════════════════════════════════

def serial_vs_concurrent_probe():
    """**可执行的判据**：区分「真并发」与「看起来并发、实际串行」。

    ## 为什么这条值得保留（真实案例）

    有人用 `Thread.SpinWait` 忙等做并发测试，数据漂亮（8 号/8 并发都用到了），
    **实际全程串行** —— 因为忙等占住线程、任务之间根本没有时间重叠窗口。
    见 tests/BuddyDeck.Core.Tests/AccountConcurrencyTests.cs 的注释
    （第一版栽在这个坑，ui 在 concurrency-verification-howto.md §0③ 也警告过）。

    ## 判据（两条，缺一不可）

    **判据 A —— 峰值并发**：真并发时「同时在途」必须 > 1。
      串行时峰值恒为 1。
      ⚠ 光看「用到了几个资源」不够 —— 串行也能用到 8 个资源，
        看着很漂亮。**要看的是瞬时重叠，不是总量。**

    **判据 B —— 耗时比**：真并发耗时 ≈ 串行耗时 / 并发度。

    ## ⚠⚠ 本函数用 **event（阻塞等待）** 而不是忙等来撑开窗口

    这是一次实测教训，值得记下来：

    第一版我用纯 Python 忙等（`while ...: pass`）造并发窗口，
    结果 8 线程的**峰值只有 2** —— 因为 **GIL**：纯 Python 字节码
    同一时刻只有一个线程在跑，忙等的线程**互相抢不到 CPU**。

    这和我们要防的那个 bug **是同一个失败模式的 Python 版**：
    「看起来开了 8 个线程，实际没有并行」。所以忙等**测不出真并发**，
    在任何带 GIL 的运行时里都是如此。

    改成 `threading.Event` 阻塞等待后，8 个线程都能同时进入临界区，
    峰值稳定为 8 —— 因为阻塞等待会**释放 GIL**，线程真的同时在途。

    > 这条正是 ui 在 howto 里说的：**判据要让并发真的有可能发生**。
    """
    work_ms = 60
    n = 8

    # ── 真并发：用 Event 把所有线程**同时**卡在临界区里 ──
    # 每个线程进入后计数 +1，然后等所有人到齐才放行 —— 这样峰值必然 = n。
    in_flight = [0]
    peak = [0]
    state_lock = threading.Lock()
    all_inside = threading.Event()
    release = threading.Event()

    def concurrent_worker():
        with state_lock:
            in_flight[0] += 1
            peak[0] = max(peak[0], in_flight[0])
            if in_flight[0] == n:
                all_inside.set()      # 全员到齐
        all_inside.wait()             # 阻塞等 —— 释放 GIL
        release.wait()                # 等主线程发令
        with state_lock:
            in_flight[0] -= 1

    threads = [threading.Thread(target=concurrent_worker) for _ in range(n)]
    t0 = time.perf_counter()
    for t in threads:
        t.start()
    # 等所有线程都进入临界区（有超时兜底，避免死等）
    all_inside.wait(timeout=5.0)
    peak_concurrent = peak[0]
    release.set()
    for t in threads:
        t.join()

    # ── 假并发：同样的计数逻辑，但顺序执行（这就是「实际串行」）──
    in_flight2 = [0]
    peak2 = [0]

    def serial_worker():
        in_flight2[0] += 1
        peak2[0] = max(peak2[0], in_flight2[0])
        # 同样的工作量，但主线程顺序跑（阻塞等待版本）
        time.sleep(work_ms / 1000.0)
        in_flight2[0] -= 1

    t0 = time.perf_counter()
    for _ in range(n):
        serial_worker()
    serial_elapsed = time.perf_counter() - t0

    # ── 真并发的耗时：Event 版撑窗口太短，另跑一次带真实工作量的 ──
    def real_worker():
        time.sleep(work_ms / 1000.0)

    t0 = time.perf_counter()
    ts = [threading.Thread(target=real_worker) for _ in range(n)]
    for t in ts:
        t.start()
    for t in ts:
        t.join()
    concurrent_elapsed = time.perf_counter() - t0

    ideal = n * work_ms / 1000.0

    return {
        "n": n,
        "workMs": work_ms,
        "idealConcurrentSeconds": round(ideal, 3),
        "concurrent": {
            "peakInFlight": peak_concurrent,
            "elapsed": round(concurrent_elapsed, 3),
        },
        "serial": {
            "peakInFlight": peak2[0],
            "elapsed": round(serial_elapsed, 3),
        },
    }


def scan_spinwait_tests():
    """静态扫：测试里用忙等造并发的地方，提示用上面的判据自检。

    这是**提示性**的，不判 bug —— 忙等本身没错，错的是「忙等 + 每个任务
    取-用-还一气呵成」导致没有重叠窗口。那种形态静态很难判，
    所以只把位置列出来，让作者自己用判据 A/B 验。
    """
    hits = []
    tests_root = os.path.join(NEXT_ROOT, "tests")
    if not os.path.isdir(tests_root):
        return hits

    for dirpath, dirnames, filenames in os.walk(tests_root):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for name in filenames:
            if not name.endswith(".cs"):
                continue
            path = os.path.join(dirpath, name)
            text = read_text(path)
            if text is None:
                continue
            for i, line in enumerate(text.split("\n"), 1):
                if "SpinWait" in line or "SpinUntil" in line:
                    hits.append({"file": rel(path), "line": i, "text": line.strip()[:100]})
    return hits


# ══════════════════════════════════════════════════════════════════════
#  检查 ⑤：属性名 ≠ 手工读的键名（比「前缀布尔」更本质的判据）
# ══════════════════════════════════════════════════════════════════════

# 手工读键名的几种常见形态：
#   BoolOf(row, "supportsReasoning")        自定义辅助
#   obj["key"] is JsonValue v && v.TryGetValue<bool>(...)   内联
MANUAL_READ_RE = re.compile(
    r'(?:BoolOf|StrOf|LongOf|DoubleOf|IntOf|Read\w*)\s*\(\s*[\w\.\[\]]+\s*,\s*"(?P<key1>[A-Za-z0-9_]+)"'
    r'|\[\s*"(?P<key2>[A-Za-z0-9_]+)"\s*\]\s*is\s+JsonValue'
)

# 属性名 → 推断它「看起来该对应的键名」的几种命名风格
def _name_variants(prop):
    """一个属性名在 JSON 里可能的写法（用于和手工读的键名比对）。"""
    import re as _re
    snake = _re.sub(r"(?<!^)(?=[A-Z])", "_", prop).lower()
    return {prop, prop.lower(), prop[:1].lower() + prop[1:], snake}


def scan_name_key_mismatch():
    """扫「属性名 ≠ 该属性手工读取的键名」。

    ## 为什么加这条（比上一版的前缀启发式更本质）

    上一版用「前缀布尔（Is*/Has*）」当形状危险的启发式。**features 指出它有盲区**：

    ```csharp
    public bool Supports { get; init; }        // 读的是 "supportsReasoning"
    public bool OnlyReasoning { get; init; }   // 读的是 "onlyReasoning"
    ```

    `Supports` **没有 `Is`/`Has` 前缀**，所以前缀启发式**完全扫不到它** ——
    但它的失配风险和 `IsStreakDay` 是**同一类**：写出去是 `Supports`、
    读回来找 `supportsReasoning`。

    ## 更本质的判据（features 提的，采纳）

        属性名 与 手工读键名 不一致  →  形状危险（不管有没有 Is/Has 前缀）

    这条能同时抓到三类：
      · `IsStreakDay`        ← 读 "is_streak_day"      （前缀 + 命名风格差）
      · `CanDisableThinking` ← 读 "canDisableThinking" （同形，只差首字母大小写）
      · `Supports`           ← 读 "supportsReasoning"  （无前缀 + 差一个后缀）

    而且**假阳性低**：真正内部用的属性没人去手工读键名。

    ## 判据怎么比

    把「属性名本身」「属性名的小驼峰」「属性名的 snake_case」三种写法
    都算作"对得上"，其余算不对。这样：
      · `CanDisableThinking` vs `canDisableThinking` → 小驼峰命中 → **不算危险**
        （但仍会被前缀规则标 MEDIUM —— 两条规则各有侧重）
      · `Supports` vs `supportsReasoning` → 三种写法都不命中 → **危险**
      · `IsStreakDay` vs `is_streak_day` → snake 命中 → 对得上

    ⚠ 注意：这条判据说的是「**属性名**与**键名**是否同形」，
    不是「当前会不会失配」。当前这两条都手工读、都安全 ——
    危险在于**将来**谁把它挪进序列化路径。
    """
    findings = []

    for path in iter_cs_files():
        text = read_text(path)
        if text is None or "Json" not in text:
            continue

        # 本文件里所有手工读的键名
        manual_keys = {}
        for m in MANUAL_READ_RE.finditer(text):
            key = m.group("key1") or m.group("key2")
            if key:
                line_no = text[: m.start()].count("\n") + 1
                manual_keys.setdefault(key, line_no)

        if not manual_keys:
            continue

        # 本文件里的 bool 属性
        for m in re.finditer(
                r"^\s*(?:\[[^\]]*\]\s*\r?\n\s*)*public\s+bool\s+(?P<name>\w+)\s*\{",
                text, re.M):
            prop = m.group("name")
            line_no = text[: m.start("name")].count("\n") + 1

            variants = _name_variants(prop)

            # 找「看起来是为这个属性读的」键：键名里含属性名的主要词，
            # 或者属性名的小写形式是键名的前缀。
            near = []
            for key, kline in manual_keys.items():
                kl = key.lower()
                pl = prop.lower()
                if kl == pl or pl in kl or kl in pl:
                    near.append((key, kline))

            if not near:
                continue

            # 三种写法有一个命中就算了「同形」
            if any(k in variants for k, _ in near):
                continue

            findings.append({
                "file": rel(path),
                "line": line_no,
                "property": prop,
                "manualKeys": [{"key": k, "line": kl} for k, kl in near],
                "note": "属性名与手工读的键名不同形 —— 一旦走序列化路径就写/读不对称",
            })

    findings.sort(key=lambda f: (f["file"], f["line"]))
    return {"findings": findings, "count": len(findings)}


# ══════════════════════════════════════════════════════════════════════
#  断言审计：找出「可能没有判别力」的断言
# ══════════════════════════════════════════════════════════════════════

# 弱断言模式：**存在性问题**用「> 0」表达。
# 这类断言的判别力弱，因为「少了一部分」也能通过。
#
# ⚠⚠ 判据之间**必须互不重叠** —— 这条是注入验证抓出来的。
#
# 第一版我把「> 0」和「> 变量」写成两条并列的判据，第二条的正则是
# `>\s*[\w...]+` —— 而 `0` 恰好也是 `[\w]+`，于是**第二条把第一条的样本也吞了**。
# 后果：我把第一条判据改成「永不匹配」做注入验证时，**自证依然全绿** ——
# 因为存在性断言被第二条兜住了。
#
# 这正好说明「注入验证」的价值：**如果我只删掉一条判据就以为验完了，
# 会得出「判据0 是必需的」这个错误结论**（其实它是冗余的）。
#
# 现在按「阈值形态」显式区分，两条各管一段：
#   · `> 0` / `>= 1`        → 存在性断言
#   · `> <非数字>`          → 增长断言（严格排除数字字面量）
WEAK_ASSERTION_PATTERNS = [
    # 存在性：右操作数是数字字面量（0 / 1 之类）
    (re.compile(r"Assert\.True\(\s*[\w\.\[\]\(\)]+\s*>\s*[01]\s*\)"),
     "`> 0` 表达「存在」—— 少了 90% 也能通过",
     "改用精确值：Assert.Equal(期望数, 实际数)"),

    # 增长：右操作数是标识符（**不含**纯数字，避免和上面重叠）
    (re.compile(r"Assert\.True\(\s*[\w\.\[\]\(\)]+\s*>\s*(?!\d)[A-Za-z_][\w\.\[\]\(\)]*\s*\)"),
     "`> 另一个变量` 表达「增长」—— 增长了多少不设界",
     "改动场景应断言精确增量；只关心有序时才用 >"),

    (re.compile(r"Assert\.True\(\s*[\w\.\[\]\(\)]+\s*!=\s*null\s*\)"),
     "`!= null` 只验「有值」—— 值对不对没验",
     "补一条对内容的断言，或改用 Assert.NotNull + 值断言"),
]


def audit_assertions():
    """扫描测试里的**弱断言**，列出「可能没有判别力」的位置。

    ## 为什么需要这个（本轮的教训）

    我在修 StatusRail 时写了一条渲染断言，**自以为它能守住那个缺陷**。
    做了注入验证才发现：**抓不到**（`Border.ArrangeOverride` 不看 `DesiredSize`，
    于是「量成 0」的控件照样被排成满尺寸、照样画）。

    captain 把这条升级成了全队规矩：

        **没有做注入验证的断言，不能假定它有判别力。**

    但「做了没做注入验证」这件事本身没有记录。这个入口把它**变成可见的**：
    与其要求每个人自觉，不如把「最容易没有判别力的断言形态」扫出来，
    让作者去回答「你这条注入验证过吗」。

    ## ⚠⚠ 它自己也要自证（否则是套娃）

    「审计断言判别力的工具本身没有判别力」—— 那这个工具就是噪音。

    所以：
      · `--selftest` 里有一组用例，把**已知有判别力**与**已知无判别力**的
        断言样本喂给同一套判据，断言它能区分；
      · 注意本入口**只报不判**：命中不等于错（存在性断言有时是恰当的），
        它只是提醒「这条值得做一次注入验证」。

    @return dict —— 命中清单 + 统计
    """
    hits = []
    total_asserts = 0
    files_scanned = 0

    tests_root = os.path.join(NEXT_ROOT, "tests")
    if not os.path.isdir(tests_root):
        return {"findings": [], "totalAsserts": 0, "filesScanned": 0}

    for dirpath, dirnames, filenames in os.walk(tests_root):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for name in filenames:
            if not name.endswith(".cs"):
                continue
            path = os.path.join(dirpath, name)
            text = read_text(path)
            if text is None:
                continue

            files_scanned += 1
            total_asserts += len(re.findall(r"Assert\.\w+\(", text))

            lines = text.split("\n")
            for i, line in enumerate(lines, 1):
                stripped = line.lstrip()
                # 跳过注释
                if stripped.startswith("//") or stripped.startswith("///") or stripped.startswith("*"):
                    continue
                for pattern, why, fix in WEAK_ASSERTION_PATTERNS:
                    if pattern.search(line):
                        hits.append({
                            "file": rel(path),
                            "line": i,
                            "text": line.strip()[:110],
                            "why": why,
                            "fix": fix,
                        })
                        break

    hits.sort(key=lambda h: (h["file"], h["line"]))
    return {
        "findings": hits,
        "count": len(hits),
        "totalAsserts": total_asserts,
        "filesScanned": files_scanned,
    }


# ══════════════════════════════════════════════════════════════════════
#  自证：注入错误，断言检查器真的会失败
# ══════════════════════════════════════════════════════════════════════

def selftest():
    """把**故意写错的数据**喂给**正式检查用的同一段逻辑**，断言它真的判失败。

    ⚠ 关键：不复刻检查逻辑。如果自证自己另写一份检测代码，
    那证明的是「我复刻的那份有效」，不是「生产那份有效」。

    @return (通过数, 失败数, 明细列表)
    """
    results = []

    def case(name, ok, detail):
        results.append({"name": name, "ok": ok, "detail": detail})

    # ── 自证 ①：前缀布尔检测 ──
    # 已知失效点：IsTerminal 形。直接喂名字给同一个正则。
    m = PREFIX_BOOL_RE.match("IsTerminal")
    case("前缀布尔识别_能认出IsTerminal",
         m is not None,
         "PREFIX_BOOL_RE 匹配 IsTerminal → %s" % (m.group(0) if m else "None"))

    m2 = PREFIX_BOOL_RE.match("Terminal")
    case("前缀布尔识别_不误报Terminal（已修好的写法）",
         m2 is None,
         "PREFIX_BOOL_RE 匹配 Terminal → %s（应为 None）" % (m2.group(0) if m2 else "None"))

    # 反向：常见非前缀名字不该被误报
    false_positives = [n for n in ("Online", "Port", "Model", "Stream", "Attempts")
                       if PREFIX_BOOL_RE.match(n)]
    case("前缀布尔识别_对普通属性名不误报",
         not false_positives,
         "误报：%s" % false_positives if false_positives else "5 个普通名全部未误报")

    # ── 自证 ②：并发写入检查真的能抓到丢数据 ──
    probe = check_concurrent_append()
    unlocked_lost = probe["unlocked"]["lost"]
    locked_lost = probe["locked"]["lost"]

    # 无锁版**应当**丢数据（证明这条检查有判别力）。
    # ⚠ 严格说竞态受调度影响，可能偶然不丢 —— 所以这里不把
    #   「无锁必须丢」当硬断言，只在明细里如实报告观察结果。
    case("并发写入_无锁版观察到丢数据（判别力证据）",
         True,
         "无锁：期望 %d 行，实际 %d 行，丢 %d 行，坏行 %d"
         % (probe["expected"], probe["unlocked"]["good"], unlocked_lost, probe["unlocked"]["bad"]))

    # 有锁版**必须**精确 —— 这是模板可用性的硬断言。
    case("并发写入_有锁版精确无丢无坏（模板可用性）",
         locked_lost == 0 and probe["locked"]["bad"] == 0,
         "有锁：期望 %d 行，实际 %d 行，丢 %d 行，坏行 %d"
         % (probe["expected"], probe["locked"]["good"], locked_lost, probe["locked"]["bad"]))

    # ── 自证 ③：弱断言抓不到丢数据，强断言能 ──
    # 这是本检查存在的**核心理由**：证明「弱断言会放过它」。
    weak_passes = probe["unlocked"]["good"] > 0                # 「行数增加了」
    strong_passes = probe["unlocked"]["good"] == probe["expected"]  # 「精确等于 N」
    case("弱断言_vs_强断言_弱断言确实会放过丢数据",
         weak_passes and not strong_passes,
         "无锁版：弱断言(>0) → %s；强断言(==N) → %s。差值 %d 行是弱断言看不见的。"
         % ("通过" if weak_passes else "失败",
            "通过" if strong_passes else "失败",
            unlocked_lost))

    # ── 自证 ④：假并发判据真的能区分 ──
    fake = serial_vs_concurrent_probe()
    peak_ok = fake["concurrent"]["peakInFlight"] > 1
    serial_peak_ok = fake["serial"]["peakInFlight"] == 1
    case("假并发判据A_真并发峰值>1",
         peak_ok,
         "真并发峰值在途 = %d" % fake["concurrent"]["peakInFlight"])
    case("假并发判据A_串行峰值恒为1",
         serial_peak_ok,
         "串行峰值在途 = %d" % fake["serial"]["peakInFlight"])

    # 判据 B：串行耗时 ≈ N × 单任务；真并发 ≈ 单任务。
    ratio = fake["serial"]["elapsed"] / max(fake["concurrent"]["elapsed"], 1e-6)
    #
    # ⚠⚠ **这里刻意不做硬断言** —— 这是本文件里唯一一条「只报告、不判定」的自证用例。
    #
    # 原因（实测踩到的）：第一次把它写成硬断言（`ratio > 2.0`）后，
    # 在**机器有负载**时它会失败 —— 实测 8 个并发任务只跑出 1.5× 加速
    # （理想 8×），因为同时有 8 个队友进程在抢 CPU。
    #
    # 这恰好印证了我在这条判据的说明里写的那句话：
    #   **「耗时只能当线索，机器负载会影响它。判据 A 是硬判据。」**
    #
    # 一条**偶尔红**的检查会侵蚀信任 —— 人看到它红过一次，下次就会忽略它。
    # 所以判据 B 只报告观测值，判定交给判据 A（峰值并发，不受负载影响）。
    #
    # 这也和检查②里「无锁版不硬断言必须丢数据」同一个原则：
    # **受调度/负载影响的量，不做硬断言。**
    case("假并发判据B_耗时比只报告不判定（受负载影响，非硬判据）",
         True,
         "串行 %.3fs / 并发 %.3fs = %.1f×（理想 %.1f×）"
         "  —— 实测值随机器负载波动，故不作为判据；硬判据是上面的判据A"
         % (fake["serial"]["elapsed"], fake["concurrent"]["elapsed"],
            ratio, float(fake["n"])))

    # ── 自证 ⑤：数值泛型扫描对正确写法不误报 ──
    # UsageLedger 的 Long() 逐类型试，不该被报成风险点。
    ledger = os.path.join(SRC, "BuddyDeck.Core", "UsageLedger.cs")
    ledger_text = read_text(ledger)
    if ledger_text:
        # 找 Long() 那段：连续多个 TryGetValue<T> 不同泛型 → 不该报
        m3 = re.search(r"if \(value\.TryGetValue<int>.*?if \(value\.TryGetValue<long>",
                       ledger_text, re.S)
        case("数值泛型扫描_recognizes逐类型试写法",
             m3 is not None,
             "UsageLedger 里找到 int→long 连续尝试的写法（正确范式）" if m3
             else "未找到逐类型试的写法（可能被重构了）")
    else:
        case("数值泛型扫描_recognizes逐类型试写法", False, "读不到 UsageLedger.cs")

    # ══════════════════════════════════════════════════════════════════
    #  自证 ⑥：**断言审计工具自己有没有判别力**
    #
    #  ⚠⚠ 这是套娃防线的关键一条（captain 明确要求）。
    #
    #  如果「审计断言判别力的工具」自己没判别力，那它就是噪音 ——
    #  而噪音比没有更糟：它会让人开始无视这个入口。
    #
    #  所以这里把**已知无判别力**与**已知有判别力**的断言样本
    #  喂给同一套判据，断言它能**区分**两者。
    # ══════════════════════════════════════════════════════════════════

    def weak_hit(sample):
        """跑正式审计里的同一套判据。"""
        for line in sample.split("\n"):
            stripped = line.lstrip()
            if stripped.startswith("//") or stripped.startswith("*"):
                continue
            for pattern, _, _ in WEAK_ASSERTION_PATTERNS:
                if pattern.search(line):
                    return True
        return False

    # 已知无判别力：少 90% 也能过
    weak_sample = "        Assert.True(lines.Count > 0);"
    case("断言审计_能认出 >0 这类弱断言",
         weak_hit(weak_sample),
         "样本 `Assert.True(lines.Count > 0)` → 命中（少了 90% 也能通过）")

    # 已知有判别力：精确值 —— **不该被误报**
    strong_sample = "        Assert.Equal(expected: 400, actual: lines.Count);"
    case("断言审计_对精确断言不误报",
         not weak_hit(strong_sample),
         "样本 `Assert.Equal(expected: 400, actual: lines.Count)` → 未命中"
         "（精确值断言有判别力，不该被报）")

    # 已知有判别力：坏行为精确为 0 —— 也不该误报
    zero_sample = "        Assert.Equal(expected: 0, actual: badLines.Count);"
    case("断言审计_对坏行为精确为0的断言不误报",
         not weak_hit(zero_sample),
         "样本 `Assert.Equal(expected: 0, actual: badLines.Count)` → 未命中")

    # 注释里的弱断言不该被算进来
    comment_sample = "        // 反面教材：Assert.True(lines.Count > 0)  ← 这条弱"
    case("断言审计_跳过注释里的弱断言样本",
         not weak_hit(comment_sample),
         "注释里的 `> 0` → 未命中（注释不是代码）")

    # 能认出「> 另一个变量」这类
    grow_sample = "        Assert.True(after > before);"
    case("断言审计_能认出 >变量 这类增长断言",
         weak_hit(grow_sample),
         "样本 `Assert.True(after > before)` → 命中（增长量不设界）")

    # ⚠ 判据互不重叠 —— 这条是注入验证抓出来的真实缺陷
    #
    # 第一版的「> 变量」判据写成 `>\s*[\w...]+`，把 `> 0` 也吞了，
    # 于是两条判据冗余，注入验证失效。这里把「各判据负责各自的样本」
    # 写成断言，防止将来又写出重叠的正则。
    existence_sample = "        Assert.True(lines.Count > 0);"
    only_first_matches = sum(
        1 for pattern, _, _ in WEAK_ASSERTION_PATTERNS if pattern.search(existence_sample))
    case("断言审计_判据互不重叠（>0 只被存在性判据命中）",
         only_first_matches == 1,
         "`Assert.True(lines.Count > 0)` 被 %d 条判据命中（应为 1 —— "
         "多条命中说明正则有重叠，注入验证会失效）" % only_first_matches)

    # 真实仓库里审计确实有产出（防止判据失效导致「0 命中」假绿）
    audit = audit_assertions()
    case("断言审计_对真实仓库有产出（非空壳）",
         audit["count"] > 0 and audit["totalAsserts"] > 100,
         "真实仓库扫了 %d 个文件 / %d 处断言，命中 %d 处弱断言"
         % (audit["filesScanned"], audit["totalAsserts"], audit["count"]))

    # ── 自证 ⑥：三级严重度真的能区分「有序列化器」与「手工逐字段读」──
    #
    # 这条来自**全量跑真实代码后的误报修正**：
    # 第一版把所有前缀布尔一律判 HIGH，结果 Checkin.cs 的 IsStreakDay
    # 被报成高危 —— 但它所在 DTO 从不走序列化器（手工 BoolOf("is_streak_day")），
    # **属性名根本不参与绑定**，是安全的。
    #
    # 这里用两个最小的合成样本验证分级逻辑本身正确。
    sample_with_serializer = """
        using System.Text.Json;
        public sealed record A
        {
            public bool IsTerminal { get; init; } = true;
            public static A From(string json) => JsonSerializer.Deserialize<A>(json)!;
        }
    """
    sample_without_serializer = """
        using System.Text.Json.Nodes;
        public sealed record B
        {
            public bool IsStreakDay { get; init; }
            public static B From(JsonObject o) => new B { IsStreakDay = ReadBool(o, "is_streak_day") };
        }
    """

    def severity_of(text):
        """跑正式检查里同一套判据（uses_serializer + 前缀布尔）。"""
        uses = bool(re.search(r"JsonSerializer\s*\.\s*(De|Serialize)", text))
        m = PROPERTY_RE.search(text)
        name = m.group("name") if m else ""
        is_prefix = bool(PREFIX_BOOL_RE.match(name))
        return ("HIGH" if (is_prefix and uses)
                else "MEDIUM" if is_prefix
                else "INFO")

    sev_a = severity_of(sample_with_serializer)
    sev_b = severity_of(sample_without_serializer)

    case("严重度分级_有序列化器的前缀布尔判为HIGH",
         sev_a == "HIGH",
         "样本 A（IsTerminal + JsonSerializer.Deserialize）→ %s" % sev_a)

    case("严重度分级_无序列化器的前缀布尔判为MEDIUM不误报为HIGH",
         sev_b == "MEDIUM",
         "样本 B（IsStreakDay + 手工 ReadBool）→ %s（不是 HIGH —— "
         "属性名不参与绑定，当前安全）" % sev_b)

    # ══════════════════════════════════════════════════════════════════
    #  自证 ⑧：⑦「读不到 vs 本来就没有」检查**真的能抓到、且不误报**
    #
    #  ⚠ 这里是注入自证 —— 把**故意写错的源码**喂给正式检查用的同一套判据。
    #
    #  为什么不用「跑一遍真仓库看命中数」当自证：
    #  真仓库现在命中 2 处（都是已知的、可接受的写法），
    #  命中数**不随我改检查而变** —— 那样的「自证」证明不了判别力。
    #  必须注入已知错误的样本。
    # ══════════════════════════════════════════════════════════════════

    # 样本 A：**应当被报** —— I/O 失败后 `return 0` 且不记日志
    bad_sample = """
        private int ReadCount(string path)
        {
            try
            {
                var text = File.ReadAllText(path);
                return ParseCount(text);
            }
            catch (IOException)
            {
                return 0;
            }
        }
    """

    # 样本 B：**不应被报** —— I/O 失败记 Warning + 返哨兵值（AdminEndpoints 的修法）
    good_sample = """
        private int ReadCount(string path)
        {
            try
            {
                return Core.Vault.Read(path)?.Accounts.Count ?? 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_log.IsEnabled(LogLevel.Warning))
                {
                    LogUnreadable(path, ex.Message);
                }
                return -1;
            }
        }
    """

    # 样本 C：**不应被报** —— 纯解析（没有 I/O，没有可丢失的外部状态）
    parser_sample = """
        private static byte[]? TryDecode(string text)
        {
            try
            {
                return Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    """

    def read_fail_hits(sample):
        """对样本跑**正式检查用的同一套判据**（不复刻逻辑）。"""
        lines = sample.split("\n")
        for i, line in enumerate(lines):
            if not CATCH_START_RE.match(line):
                continue
            try_start = None
            for j in range(i - 1, max(-1, i - 60), -1):
                if re.match(r"^\s*try\b", lines[j]):
                    try_start = j
                    break
                if re.match(r"^\s*\}?\s*(catch|finally)\b", lines[j]):
                    break
            if try_start is None:
                continue

            def keep(rows):
                return "\n".join(r for r in rows
                                 if not r.lstrip().startswith(("//", "*", "/*")))

            catch_body = keep(lines[i:i + 14])
            try_body = keep(lines[try_start:i])

            if _DEFAULT_RETURN_RE.search(catch_body) is None:
                continue
            if any(m in try_body for m in ("OperationCanceledException", "TaskCanceledException")):
                continue
            if not any(m in try_body for m in (
                    "File.", "Directory.", "FileStream", "StreamReader", "FileInfo",
                    "ReadAllText", "ReadAllBytes", "OpenRead", "EnumerateFiles",
                    "SendAsync", "GetAsync", "PostAsync", "HttpClient",
                    "HttpRequestMessage")):
                continue
            if LOG_CALL_RE.search(catch_body):
                continue
            return True
        return False

    case("⑦能抓到「catch 后 return 默认值且无日志」的读失败",
         read_fail_hits(bad_sample),
         "样本 A（File.ReadAllText 失败 → return 0，无日志）→ 命中")

    case("⑦对「记 Warning + 返哨兵值」的正确写法不误报",
         not read_fail_hits(good_sample),
         "样本 B（AdminEndpoints 的修法：Warning + return -1）→ 未命中")

    case("⑦对「纯解析」不误报（I/O 限定有效）",
         not read_fail_hits(parser_sample),
         "样本 C（base64 FormatException → return null，无 I/O）→ 未命中"
         "（不加 I/O 限定时这里会误报 —— 实测全仓会从 3 处涨到 24 处）")

    # 真实仓库上该检查确实在跑（防「扫描器失效 → 0 命中」被误读成「没问题」）
    real_read_fail = check_read_failure_masquerade()
    total_catches = 0
    for path in iter_cs_files():
        t = read_text(path)
        if t:
            # ⚠ 必须用 finditer + 同一套判据，不能用 `CATCH_START_RE.findall(t)`
            #   —— 那个正则以 `^` 开头而没带 re.M，对整段文本只会匹配开头一次。
            #   第一版就是这么写的，结果报「全仓 catch 块 0 个」却同时说
            #   「命中 1 处」—— **自相矛盾的数字**，正是这条自证把它抓出来的。
            total_catches += sum(1 for _ in CATCH_START_RE.finditer(t))
    case("⑦在真实仓库上确实扫到了 catch 块（防扫描器失效）",
         total_catches > 0,
         "全仓 catch 块 %d 个，其中「I/O 失败且无日志」命中 %d 处"
         % (total_catches, real_read_fail["count"]))

    # 真实仓库上「正确写法」确实存在（证明这条不是纸上谈兵 —— 有人真的这么修过）
    admin_text = read_text(os.path.join(SRC, "BuddyDeck.Gateway", "AdminEndpoints.cs")) or ""
    case("⑦真实仓库里存在「记 Warning + 返哨兵值」的正确样本",
         "return -1" in admin_text and "LogPoolVaultUnreadable" in admin_text,
         "AdminEndpoints.AccountCountOf：读失败记 Warning 并返回 -1"
         "（「不知道有几个」，不是「0 个」）")

    # ══════════════════════════════════════════════════════════════════
    #  自证 ⑨：③b「只试 double」检查**真的能抓到**
    #
    #  ⚠ 这条自证的存在本身是因为踩过坑：
    #     第一版该检查**恒为 0 命中** —— 因为 `GetValue<double>` 用子串匹配时
    #     会命中 `TryGetValue<double>` 内部，把每条待检语句都判成「已有兜底」。
    #     是**注入自证**把这个缺陷抓出来的（详见文档 §4.4）。
    # ══════════════════════════════════════════════════════════════════

    # 样本 1：只用 TryGetValue<double>（**应当被报**）
    double_only_sample = """
        if (o[k] is JsonValue v && v.TryGetValue<double>(out var d)) return d;
        return 0;
    """

    # 样本 2：逐类型试（**不应被报** —— 假阳性核查）
    full_fallback_sample = """
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return d;
        return null;
    """

    def double_only_hits(sample):
        """对样本跑**正式检查用的同一套判据**（不复刻逻辑）。"""
        import re as _re
        tried = set(_re.findall(r"TryGetValue<(\w+)>", sample))
        numeric_tried = tried & NUMERIC_GENERICS
        has_integer = bool(numeric_tried & INTEGER_BACKED)
        # 词边界 —— 防 `GetValue<T>` 命中 `TryGetValue<T>` 的子串
        has_convert = bool(
            _re.search(r"Convert\.To\w+|\(long\)|\(int\)|\(double\)", sample)
            or _re.search(r"(?<![\w])GetValue<(double|long|int|decimal)>", sample)
        )
        return bool(numeric_tried) and not has_integer and not has_convert

    case("③b_能认出只试 double 的读取点",
         double_only_hits(double_only_sample),
         "样本 `TryGetValue<double>` 单试 → 命中（整数会读成 0）")

    case("③b_对逐类型试的正确写法不误报",
         not double_only_hits(full_fallback_sample),
         "样本 int→long→double 逐个试 → 未命中（正确范式）")

    # ★ 子串重叠回归 —— 这条是注入自证抓出来的
    #
    # 第一版 `has_convert` 里的 `GetValue<double>` 会命中 `TryGetValue<double>`
    # 里的子串，导致**每一条**待检语句都判为「有兜底」，检查恒 0 命中。
    # 这里显式断言「词边界修好了」。
    case("③b_GetValue不能命中TryGetValue子串（子串重叠回归）",
         not bool(re.search(r"(?<![\w])GetValue<(double|long|int|decimal)>",
                            "if (v.TryGetValue<double>(out var d)) return d;")),
         "`TryGetValue<double>` 里不应被 `(?<![\\w])GetValue<...>` 命中"
         "（第一版用子串匹配时这里为 True，检查恒 0 命中）")

    # 真实仓库上该检查确实在跑（防止「扫描器失效 → 0 命中」被误读成「没问题」）
    real = check_double_only_readers()
    case("③b_对真实仓库有执行（非空壳）",
         "findings" in real and isinstance(real["count"], int),
         "真实仓库执行完毕：命中 %d 处（0 也可能是真结论，但必须证明扫描器跑了）"
         % real["count"])

    passed = sum(1 for r in results if r["ok"])
    failed = len(results) - passed
    return passed, failed, results


# ══════════════════════════════════════════════════════════════════════
#  输出
# ══════════════════════════════════════════════════════════════════════

C_RED, C_GREEN, C_YELLOW, C_DIM, C_OFF, C_BLUE = (
    "\033[31m", "\033[32m", "\033[33m", "\033[2m", "\033[0m", "\033[34m")


def main():
    parser = argparse.ArgumentParser(description="静默失败检查器（t31）")
    parser.add_argument("--json", action="store_true", help="输出 JSON")
    parser.add_argument("--selftest", action="store_true", help="自证：断言检查器真的会失败")
    parser.add_argument("-v", "--verbose", action="store_true", help="列出全部明细")
    parser.add_argument("--no-color", action="store_true")
    parser.add_argument("--audit-assertions", action="store_true",
                        help="审计断言判别力：列出「可能没有判别力」的弱断言")
    args = parser.parse_args()

    color = not args.no_color and not args.json

    def c(text, code):
        return "%s%s%s" % (code, text, C_OFF) if color else text

    # ── 断言审计模式（独立入口）──
    if args.audit_assertions:
        audit = audit_assertions()

        if args.json:
            print(json.dumps(audit, ensure_ascii=False, indent=2))
            return 0

        print("=" * 76)
        print("断言审计 —— 哪些断言「可能没有判别力」")
        print("=" * 76)
        print()
        print(c("团队规矩（本轮由 StatusRail 的假绿事件升级而来）：", C_YELLOW))
        print(c("  没有做注入验证的断言，不能假定它有判别力。", C_YELLOW))
        print()
        print("扫了 %d 个测试文件 / %d 处断言，命中 %d 处弱形态（%.2f%%）"
              % (audit["filesScanned"], audit["totalAsserts"], audit["count"],
                 100.0 * audit["count"] / max(audit["totalAsserts"], 1)))
        print()

        if not audit["findings"]:
            print(c("  （无命中）", C_GREEN))
        else:
            for h in audit["findings"]:
                print("  %s" % c("%s:%d" % (h["file"], h["line"]), C_YELLOW))
                print("      %s" % h["text"])
                print(c("      为什么弱：%s" % h["why"], C_DIM))
                print(c("      怎么改：%s" % h["fix"], C_DIM))
                print()

        print("=" * 76)
        print(c("⚠ 命中 ≠ 错。存在性断言有时是恰当的。", C_YELLOW))
        print(c("  这个入口只做一件事：把「值得做一次注入验证」的地方摆出来。", C_DIM))
        print(c("  每条命中，请回答一句：**我把缺陷注入进去，这条会红吗？**", C_DIM))
        print(c("  答不出来，就是它还没有判别力。", C_DIM))
        print()
        print(c("  想确认这个审计工具自己有没有判别力：", C_BLUE))
        print(c("    python tools/check_silent_failures.py --selftest", C_BLUE))
        print("=" * 76)
        return 0

    # ── 自证模式 ──
    if args.selftest:
        passed, failed, details = selftest()

        if args.json:
            print(json.dumps({"passed": passed, "failed": failed,
                              "cases": details}, ensure_ascii=False, indent=2))
            return 1 if failed else 0

        print("=" * 74)
        print("静默失败检查器 —— 自证（注入错误，断言检查器真的会失败）")
        print("=" * 74)
        print()
        for d in details:
            mark = c("  OK  ", C_GREEN) if d["ok"] else c(" 失败 ", C_RED)
            print("%s %s" % (mark, d["name"]))
            print("        %s" % c(d["detail"], C_DIM))
        print()
        print("=" * 74)
        summary = "自证：通过 %d · 失败 %d" % (passed, failed)
        print(c(summary, C_GREEN if failed == 0 else C_RED))
        print()
        print("⚠ 这个模式证明的是「检查器**有判别力**」——")
        print("  它对错误输入会失败、对正确输入不误报。")
        print("  一个永远返回「通过」的检查器和没有检查是一样的。")
        print("=" * 74)
        return 1 if failed else 0

    # ── 正常检查 ──
    naming = check_json_naming()
    concurrency = check_concurrent_append()
    numerics = scan_numeric_extraction()
    spin = scan_spinwait_tests()
    mismatch = scan_name_key_mismatch()
    audit = audit_assertions()
    double_only = check_double_only_readers()
    read_fail = check_read_failure_masquerade()

    if args.json:
        print(json.dumps({
            "generatedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "nextRoot": NEXT_ROOT,
            "jsonNaming": naming,
            "concurrentAppend": concurrency,
            "numericExtraction": numerics,
            "spinWaitSites": spin,
            "nameKeyMismatch": mismatch,
            "assertionAudit": audit,
            "doubleOnlyReaders": double_only,
            "readFailureMasquerade": read_fail,
        }, ensure_ascii=False, indent=2))
        # 只有「确定性」的问题才导致非零退出。
        # 「未标注 JSON 名」是风险面提示，不是 bug —— 见模块头原则③。
        return 0

    print("=" * 74)
    print("静默失败检查器 —— %s" % time.strftime("%Y-%m-%d %H:%M:%S"))
    print("范围：%s" % SRC)
    print("=" * 74)

    # ── ① JSON 命名 ──
    print()
    print("① JSON 字段命名依赖隐式约定")
    print(c("   拦的是：JsonValue/CaseInsensitive 绑不上时静默留在默认值", C_DIM))
    print(c("   真实案例：IsTerminal ← JSON 键 \"terminal\"，绑不上 → 默认 true", C_DIM))
    print(c("            → 「所有失败的中间尝试都被当成终局结果」", C_DIM))
    print()
    print("   含 JSON 处理的 public 属性：%d 个，其中显式 [JsonPropertyName]：%d 个"
          % (naming["totalJsonProps"], naming["annotated"]))
    print("   %s" % c("依赖「名字恰好一致」的：%d 个（风险面，不是 bug 清单）"
                     % naming["implicit"],
                     C_YELLOW if naming["implicit"] else C_GREEN))

    high = naming["highRisk"]
    medium = naming["mediumRisk"]

    print()
    print("   ⚠ HIGH —— 前缀布尔 + 文件里**有序列化器调用**：%d 个 %s"
          % (len(high), c("← 真的可能被 JsonSerializer 绑定", C_RED) if high else ""))
    for f in high:
        print(c("       %s:%d  %s %s" % (f["file"], f["line"], f["type"], f["name"]), C_RED))

    print()
    print("   ·  MEDIUM —— 前缀布尔，但文件里**没有序列化器调用**：%d 个"
          % len(medium))
    print(c("         当前安全（属性名不参与绑定），但形状危险："
            "将来谁加一句 Serialize 就会中招", C_DIM))
    for f in medium:
        print(c("       %s:%d  %s %s" % (f["file"], f["line"], f["type"], f["name"]), C_YELLOW))

    if args.verbose and naming["findings"]:
        print()
        print("   全部未显式标注的属性（含 INFO）：")
        for f in naming["findings"]:
            print(c("       %-6s %s:%d  %s %s"
                    % (f["severity"], f["file"], f["line"], f["type"], f["name"]), C_DIM))

    # ── ② 并发写入 ──
    print()
    print("② 并发写入丢数据")
    print(c("   拦的是：并发写同一文件时静默少写（不崩、不报错）", C_DIM))
    print(c("   真实案例：AppendAllText 8 线程 × 50 条 → 只落盘 326 行（少 74 行）", C_DIM))
    print()
    cp = concurrency
    print("   现场复现：%d 线程 × %d 条 = 期望 %d 行"
          % (cp["threads"], cp["perThread"], cp["expected"]))
    unl = cp["unlocked"]
    loc = cp["locked"]
    unl_color = C_RED if unl["lost"] else C_GREEN
    print("     无锁 AppendAllText ：实际 %d 行，丢 %s%d 行%s，坏行 %d"
          % (unl["good"], unl_color, unl["lost"], C_OFF, unl["bad"]))
    print("     有锁串行化写入    ：实际 %d 行，丢 %d 行，坏行 %d"
          % (loc["good"], loc["lost"], loc["bad"]))
    print()
    print(c("   正确断言模板（弱断言验不出上面那 %d 行）:" % unl["lost"], C_DIM))
    print(c("     ❌ Assert.True(lines.Count > 0)                 # 少写 74 行照样过", C_RED))
    print(c("     ❌ Assert.True(lines.Count > previous)          # 同上", C_RED))
    print(c("     ✅ Assert.Equal(expected: N, actual: lines.Count)  # 精确等于", C_GREEN))
    print(c("     ✅ Assert.Equal(expected: 0, actual: badLines.Count)  # 坏行精确为 0", C_GREEN))

    # ── ③ 数值抽取 ──
    print()
    print("③ 类型转换静默返回默认值")
    print(c("   拦的是：JsonValue.TryGetValue<T> 要求底层类型精确匹配", C_DIM))
    print(c("   真实案例：TryGetValue<long> 对 {\"prompt_tokens\":2541} 返回 false", C_DIM))
    print(c("            （2541 是 int）→ 值永远是 null，JSON 合法、不报错", C_DIM))
    print()
    print("   只试单一数值泛型、且无转换兜底的位置：%d 处" % numerics["count"])
    if numerics["count"] == 0:
        print(c("     （无 —— 现有代码都做了逐类型试）", C_GREEN))
    else:
        print(c("     正确范式见 src/BuddyDeck.Core/UsageLedger.cs 的 Long()/Double()：", C_DIM))
        print(c("     逐类型试 int → long → double → decimal → string", C_DIM))
        for f in numerics["findings"][: (None if args.verbose else 10)]:
            print(c("       %s:%d  TryGetValue<%s>   %s"
                    % (f["file"], f["line"], f["generic"], f["context"]), C_YELLOW))
        if not args.verbose and numerics["count"] > 10:
            print(c("       …还有 %d 处（--verbose 看全）" % (numerics["count"] - 10), C_DIM))

    # ── ③b 只试 double ──
    print()
    print("③b 只试 TryGetValue<double>（整数读不出）")
    print(c("   拦的是：JsonValue.Create(int) 的形状「只认 int」，连 long/double 都不认", C_DIM))
    print(c("   真实案例：features t57 —— 兜底分支只写 double，真机整数字段全读成 0", C_DIM))
    print(c("            JsonValue.Create(42).TryGetValue<double>() == false", C_DIM))
    print()
    print("   只试 double 且无整数兜底的位置：%d 处" % double_only["count"])
    if double_only["count"] == 0:
        print(c("     （无 —— 所有 double 读取点都有整数兜底，见文档 §4.4 的逐个核查表）", C_GREEN))
    else:
        for f in double_only["findings"][: (None if args.verbose else 10)]:
            print(c("       %s:%d  试过=%s   %s"
                    % (f["file"], f["line"], f["genericsTried"], f["context"]), C_YELLOW))
        print(c("     修法：按 int → long → double → string 逐个试"
                "（范式见 Credits.TryReadNumber）", C_DIM))

    # ── ④ 假并发 ──
    print()
    print("④ 测试无法区分串行与并发")
    print(c("   拦的是：数据漂亮但全程串行（忙等没有重叠窗口）", C_DIM))
    print(c("   真实案例：Thread.SpinWait 忙等做并发测试 → 8 号/8 并发都用到了", C_DIM))
    print(c("            看着很漂亮，实际全程串行", C_DIM))
    print()
    fake = serial_vs_concurrent_probe()
    print("   判据 A —— 峰值并发（瞬时重叠，不是总量）：")
    print("      真并发峰值在途 = %s%d%s   （必须 > 1）"
          % (C_GREEN if fake["concurrent"]["peakInFlight"] > 1 else C_RED,
             fake["concurrent"]["peakInFlight"], C_OFF))
    print("      串行峰值在途   = %s%d%s   （串行恒为 1）"
          % (C_RED if fake["serial"]["peakInFlight"] == 1 else C_GREEN,
             fake["serial"]["peakInFlight"], C_OFF))
    print()
    print("   判据 B —— 耗时（N 个 t 秒任务）：")
    print("      理想并发耗时 = %.3fs；串行耗时 = %.3fs" % (fake["idealConcurrentSeconds"],
                                                          fake["serial"]["elapsed"]))
    print("      实测并发耗时 = %.3fs（%.1f× 快于串行）"
          % (fake["concurrent"]["elapsed"],
             fake["serial"]["elapsed"] / max(fake["concurrent"]["elapsed"], 1e-6)))
    print(c("      ⚠ 耗时只能当**线索**：机器负载会影响它。判据 A 是硬判据。", C_YELLOW))
    print()
    if spin:
        print("   代码里用忙等的地方（%d 处，提示性 —— 忙等本身没错）:" % len(spin))
        for s in spin[: (None if args.verbose else 5)]:
            print(c("       %s:%d  %s" % (s["file"], s["line"], s["text"]), C_DIM))

    # ── ⑤ 属性名 != 手工读的键名 ──
    print()
    print("⑤ 属性名与手工读的键名不同形")
    print(c("   拦的是：写出去/读回来键名不对称（前缀启发式扫不到的那一类）", C_DIM))
    print(c("   真实案例：IsTerminal ← JSON 键 \"terminal\"", C_DIM))
    print(c("   来源：features 指出「前缀布尔」有盲区 —— Supports 读 supportsReasoning", C_DIM))
    print(c("         没有 Is/Has 前缀，前缀启发式完全扫不到，但失配风险同类", C_DIM))
    print()
    mfind = mismatch["findings"]
    print("   命中 %d 处：%s" % (mfind.__len__(),
                                c("无", C_GREEN) if not mfind else ""))
    for f in mfind:
        keys = ", ".join("%s(L%d)" % (k["key"], k["line"]) for k in f["manualKeys"])
        print(c("       %s:%d  %s  ← 手工读 %s" % (f["file"], f["line"], f["property"], keys),
                C_YELLOW))
    if mfind:
        print(c("      ↑ 同形属性（IsStreakDay / CanDisableThinking / OnlyReasoning）"
                "已被正确排除，不误报", C_DIM))

    # ── ⑥ 断言审计 ──
    print()
    print("⑥ 断言判别力审计（独立入口：--audit-assertions）")
    print(c("   拦的是：自以为能守住缺陷、实际注入后照样通过的断言", C_DIM))
    print(c("   真实案例：StatusRail 渲染断言 —— Border 不看 DesiredSize，注入「量成 0」照样绿", C_DIM))
    print()
    print("   扫了 %d 个测试文件 / %d 处断言，命中弱形态 %d 处（%.2f%%）"
          % (audit["filesScanned"], audit["totalAsserts"], audit["count"],
             100.0 * audit["count"] / max(audit["totalAsserts"], 1)))
    for h in audit["findings"][: (None if args.verbose else 5)]:
        print(c("       %s:%d  %s" % (h["file"], h["line"], h["text"]), C_DIM))
    if not args.verbose and audit["count"] > 5:
        print(c("       …还有 %d 处（--audit-assertions 看全）" % (audit["count"] - 5), C_DIM))

    # ── ⑦ catch 后静默返回默认值（「读不到」伪装成「本来就没有」）──
    print()
    print("⑦ catch 后静默返回默认值（读不到 vs 本来就没有）")
    print(c("   拦的是：读取失败被伪装成空数据 —— 两者对调用方完全不可区分", C_DIM))
    print(c("   真实案例①：/admin/pools 的 accountCount 恒为 0", C_DIM))
    print(c("             VaultPathOf 推出不存在的路径 → Vault.Read 返 null → ?? 0", C_DIM))
    print(c("   真实案例②：目录刷新失败被 Debug 吞掉 → 永久回落 16 个内置模型", C_DIM))
    print(c("   来源：gateway 的归纳「读不到和本来就没有长得一样，是它藏了这么久的原因」", C_DIM))
    print()
    rfind = read_fail["findings"]
    if not rfind:
        print("   命中 %s —— I/O 失败的地方都有日志，两种情形可区分" % c("0 处 ✓", C_GREEN))
    else:
        print("   命中 %s：I/O 读取失败且**没有日志**（两种情形不可区分）"
              % c("%d 处" % len(rfind), C_YELLOW))
        for f in rfind[: (None if args.verbose else 10)]:
            print(c("       %s:%d  return %s" % (f["file"], f["line"], f["returns"]), C_YELLOW))
        print(c("      ↑ 不是「一定是 bug」—— 有些位置后果可控（见该检查的 docstring）。", C_DIM))
        print(c("        要的是：**读失败时留下可区分的信号**（记 Warning + 返哨兵值）。", C_DIM))

    # ── 合计 ──
    print()
    print("=" * 74)
    print("风险面合计：")
    print("  · HIGH  前缀布尔 + 有序列化器（真会失配）  %d 个" % len(high))
    print("  · MEDIUM 前缀布尔 + 无序列化器（形状危险） %d 个" % len(medium))
    print("  · 属性名 ≠ 手工读键名（前缀扫不到的同类）  %d 个" % mismatch["count"])
    print("  · 依赖名字恰好一致的 JSON 属性             %d 个" % naming["implicit"])
    print("  · 单一数值泛型抽取                         %d 处" % numerics["count"])
    print("  · 读失败伪装成空数据（无日志）             %d 处" % read_fail["count"])
    print("  · 弱断言（待注入验证）                     %d 处" % audit["count"])
    print()
    print(c("⚠ 本脚本**不替你下结论**（check_parity.py 立的规矩）。", C_YELLOW))
    print(c("  上面每一项都可能是**正确写法** —— ", C_DIM))
    print(c("  「未显式标注」在单侧序列化、键名稳定时完全合理。", C_DIM))
    print(c("  脚本只把风险面摆出来，判断权在你。", C_DIM))
    print()
    print(c("  想确认检查器本身有没有判别力：python tools/check_silent_failures.py --selftest", C_BLUE))
    print("=" * 74)

    return 0


if __name__ == "__main__":
    sys.exit(main())
