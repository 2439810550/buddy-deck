#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
click_trace.py —— 界面动作到网关请求的可见性验证。

## 它回答的问题

> 「我在界面上点了这个按钮，请求到底有没有发到网关？」

这是一个**反复栽过**的问题（t18 的 Authorization 头、t47 的保险库路径、
t53 的目录刷新 —— 都是「界面看起来正常，但请求没发出去」）。
本工具把这类验证变成一条可重跑的命令，而不是每次靠肉眼盯界面。

## 方法（三选一里选了「假上游」）

契约允许「断点 / 日志 / 假上游」三选一。这里选**假上游**，理由：

```
断点        要 GUI 调试器，不可重跑、不可进 CI
日志        要真网关在跑，而且日志里只有「到达的」请求 ——
            看不到「本该发但没发」的那一半
假上游  ✅   可控、可重跑、可断网（验失败路径）、不碰真实账号
```

具体做法：**起一个「什么都记下来」的假网关**，让**真正的 App** 通过
环境变量指向它：

```
BUDDYDECK_PORT=<假网关端口>      ← App 读这个决定往哪发（App.xaml.cs:141）
BUDDYDECK_API_KEY=<任意>         ← App 读这个决定带不带 Authorization（:152）
```

**不需要改 src/ 任何代码** —— 这两个环境变量是 App 本来就支持的
（`BUDDYDECK_OFFLINE` 也是同一族，见 App.xaml.cs:15 的说明）。

## 为什么这是「真」验证而不是自证

App 发的是**真 HTTP**（真 HttpClient、真序列化、真鉴权头组装），
假网关收的是**真请求**。被替换掉的只有「网关那台机器」——
而那正是我们要观察的对象。所以「请求到达了吗」这个问题是真答案。

## 三类交互怎么触发

界面动作本身要人点，但**每个动作对应的请求是确定的**（见
`ACTION_PROBES` 表：每个动作的路径、方法都对着 `IGatewayClient` 的实现抄）。
所以工具分两种模式：

```
--probe-requests   不开 App：只把三类动作对应的请求发给假网关，验「通道通」
--with-app         开真 App：让它在假网关上看它启动时发了什么（需要 GUI 环境）
```

**默认走 --probe-requests** —— 因为本机常无人值守（GUI 起来了也没人点，
而 `--probe` 截图模式只能证明「渲染了」，证明不了「请求发了」）。

## 失败路径

`--negate` 让假网关**拒绝全部请求**（或在 --with-app 下干脆不起），
用来验「网关不可用时的表现」：App 必须拿到明确的连接失败，
而不是静默无反应。见 §3 的断言。

## 用法

    python tools/click_trace.py                    # 三类交互 + 失败路径，全跑
    python tools/click_trace.py --json             # 机器可读
    python tools/click_trace.py --run 聊天          # 只跑一类
    python tools/click_trace.py --with-app         # 另起真 App（需 GUI）

退出码：0 = 全部通过；1 = 有断言失败。
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

# ────────────────────────────────────────────────────────────────────
# 三类交互的探针表
#
# ⚠ 每条都对着 `src/BuddyDeck.App/Services/IGatewayClient.cs` 的**实现**抄
#   （行号在注释里）。界面改了实现，这张表要跟着改 ——
#   所以它放在脚本顶部显眼处，而不是散在函数里。
# ────────────────────────────────────────────────────────────────────

@dataclass(frozen=True)
class Probe:
    """一个可验证的「界面动作 → 网关请求」。"""

    action: str          # 界面上的动作名（人看的）
    method: str          # HTTP 方法
    path: str            # 请求路径
    body: Any            # 请求体（GET 用 None）
    why: str             # 为什么这个动作发这条请求（对着哪行抄的）


ACTION_PROBES: list[Probe] = [
    # ── ① 发起聊天 ──────────────────────────────────────────────
    Probe(
        action="发起聊天",
        method="POST",
        path="/v1/chat/completions",
        body={
            "model": "hy3",
            "stream": True,
            "messages": [{"role": "user", "content": "click_trace 探针"}],
        },
        why="IGatewayClient.cs:459 ChatStreamAsync → POST /v1/chat/completions",
    ),

    # ── ② 刷新账号 ──────────────────────────────────────────────
    # 界面上「账号」页一进去就调这两条：列账号 + 拿指标（仪表盘同款）
    Probe(
        action="刷新账号（列账号）",
        method="GET",
        path="/admin/accounts",
        body=None,
        why="IGatewayClient.cs:694 GetAccountsAsync → GET /admin/accounts",
    ),
    Probe(
        action="刷新账号（看指标）",
        method="GET",
        path="/metrics",
        body=None,
        why="IGatewayClient.cs:690 GetMetricsAsync → GET /metrics",
    ),

    # ── ③ 切换池 ────────────────────────────────────────────────
    Probe(
        action="切换池（列池）",
        method="GET",
        path="/admin/pools",
        body=None,
        why="IGatewayClient.cs:720 GetPoolsAsync → GET /admin/pools",
    ),
    Probe(
        action="切换池（启停）",
        method="POST",
        path="/admin/pools/pool-1/state",
        body={"running": True},
        why="IGatewayClient.cs:727 SetPoolRunningAsync → POST /admin/pools/{id}/state",
    ),
]

# ────────────────────────────────────────────────────────────────────
# 假网关：把收到的每个请求记下来，并按需回一个「形状对得上」的响应
# ────────────────────────────────────────────────────────────────────


@dataclass
class Recorded:
    """一个收到的请求。"""

    method: str
    path: str
    headers: dict[str, str]
    body: str
    at: float = field(default_factory=time.time)


class FakeGateway:
    """
    记录一切、并对已知路径回「够用」响应的假网关。

    ⚠ 响应**不追求真实**（那不是本工具的目的）——
    只要形状能让 App 的反序列化不抛就行。本工具观察的是**请求侧**。
    """

    def __init__(self, *, negate: bool = False) -> None:
        self.records: list[Recorded] = []
        self._lock = threading.Lock()
        self._server: ThreadingHTTPServer | None = None
        self._thread: threading.Thread | None = None
        self.port: int = 0
        self.negate = negate

    # ── 生命周期 ────────────────────────────────────────────────

    def start(self) -> int:
        outer = self

        class Handler(BaseHTTPRequestHandler):
            # 别把每个请求打到 stderr —— 输出要干净，记录在 self.records 里
            def log_message(self, *args: Any) -> None:  # noqa: D102
                pass

            def _handle(self) -> None:  # noqa: C901
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length) if length else b""

                with outer._lock:
                    outer.records.append(Recorded(
                        method=self.command,
                        path=self.path,
                        headers={k: v for k, v in self.headers.items()},
                        body=raw.decode("utf-8", errors="replace"),
                    ))

                # --negate：模拟「网关不可用」—— 直接断开连接
                if outer.negate:
                    self.close_connection = True
                    try:
                        self.connection.close()
                    except OSError:
                        pass
                    return

                status, payload = outer._response_for(self.command, self.path)
                data = json.dumps(payload).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            do_GET = _handle
            do_POST = _handle

        # 端口 0 —— 系统分配，绝不占 8787 / 3080
        self._server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self._server.server_address[1]

        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)
        self._thread.start()
        return self.port

    def stop(self) -> None:
        if self._server is not None:
            self._server.shutdown()
            self._server.server_close()
            self._server = None

    # ── 响应 ────────────────────────────────────────────────────

    def _response_for(self, method: str, path: str) -> tuple[int, Any]:
        """
        给一个「形状够用」的响应。

        ⚠ 这里**故意不模拟业务语义** —— 本工具验的是请求侧。
        真正的业务正确性由 Gateway.Tests / E2E.Tests 负责。
        """
        if path == "/healthz":
            return 200, {
                "ok": True, "provider": "click-trace-probe", "models": 1,
                "buildId": "clicktrace0000", "startedAt": "2026-10-07T00:00:00.000Z",
                "stale": False,
            }

        if path == "/metrics":
            return 200, {
                "totalRequests": 0, "attemptCount": 0, "httpSuccessRate": 0,
                "completionRate": 0, "averageLatencyMs": 0, "p95LatencyMs": 0,
                "maxLatencyMs": 0, "recent": [], "outcomes": {}, "errorKinds": {},
                "startedAtUnixMs": 0,
            }

        if path.startswith("/admin/accounts"):
            return 200, []
        if path.startswith("/admin/pools"):
            return 200, []
        if path == "/v1/models":
            return 200, {"object": "list", "data": []}

        if path == "/v1/chat/completions":
            # 聊天走 SSE；这里回一条最小可解析的流
            return 200, {"note": "click-trace probe：只验请求到达，不验聊天语义"}

        return 200, {}

    # ── 查询 ────────────────────────────────────────────────────

    def snapshot(self) -> list[Recorded]:
        with self._lock:
            return list(self.records)


def free_port() -> int:
    """拿一个当前空闲的端口（绑一下就放掉）。"""
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


# ────────────────────────────────────────────────────────────────────
# 发送一个探针请求（模拟界面发出的那一条）
# ────────────────────────────────────────────────────────────────────


def send_probe(port: int, probe: Probe, api_key: str) -> tuple[int, str]:
    """
    把探针请求发到假网关。

    ⚠ 这里**手写 HTTP 而不是调 IGatewayClient** —— 因为要验的正是
    「界面的请求长什么样」，而 `HttpGatewayClient` 需要 WPF 容器才能构造。
    路径/方法/鉴权头的组装方式对着它的实现抄（见 ACTION_PROBES.why）。

    返回 (状态码, 错误文本)；状态码 0 表示连接层失败。
    """
    url = f"http://127.0.0.1:{port}{probe.path}"
    data = None if probe.body is None else json.dumps(probe.body).encode("utf-8")

    req = urllib.request.Request(url, data=data, method=probe.method)
    req.add_header("Content-Type", "application/json")
    if api_key:
        # 与 HttpGatewayClient.ApplyAuth 一致：/healthz 跳过，其余带 Bearer
        if probe.path != "/healthz":
            req.add_header("Authorization", f"Bearer {api_key}")

    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            return resp.status, resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")
    except Exception as e:  # noqa: BLE001 —— 连接层失败有很多种，统一当「没到」
        return 0, f"{type(e).__name__}: {e}"


# ────────────────────────────────────────────────────────────────────
# 检查框架（和 tools/ 里其他脚本同形：PASS/FAIL + 计数 + 退出码）
# ────────────────────────────────────────────────────────────────────


class Checks:
    def __init__(self) -> None:
        self.passed = 0
        self.failed = 0
        self.rows: list[dict[str, str]] = []

    def check(self, name: str, ok: bool, detail: str = "") -> None:
        if ok:
            self.passed += 1
            print(f"  PASS  {name}")
        else:
            self.failed += 1
            print(f"  FAIL  {name}" + (f" — {detail}" if detail else ""))

        # ⚠ `detail` 只在**失败时**记进 rows。
        #
        # 它现在的语义是「为什么失败」的提示（比如「一条记录都没有 ——
        # 那说明请求压根没发出去」）。通过时留着它会出现在 --json 里，
        # 让读 JSON 的人以为出了问题 —— 那是**自己制造误导**，
        # 而本工具存在的意义正是消除误导（实测踩到：--json 的通过项里
        # 带着一条看起来像报错的 detail）。
        self.rows.append({
            "name": name,
            "status": "pass" if ok else "fail",
            "detail": "" if ok else detail,
        })

    def section(self, title: str) -> None:
        print(f"\n{title}")


# ────────────────────────────────────────────────────────────────────
# 主流程
# ────────────────────────────────────────────────────────────────────


def run_requests(checks: Checks, api_key: str, only: str | None) -> None:
    """§1 三类交互：每个动作的请求必须真的到达假网关。"""
    checks.section("§1 三类交互 → 请求到达假网关")

    gw = FakeGateway()
    port = gw.start()
    print(f"  （假网关起在 127.0.0.1:{port}，端口 0 由系统分配）")

    try:
        probes = [p for p in ACTION_PROBES if only is None or only in p.action]

        for probe in probes:
            before = len(gw.snapshot())
            status, _ = send_probe(port, probe, api_key)
            after = gw.snapshot()

            arrived = len(after) > before

            # ★ 核心断言：**请求到达了**
            checks.check(
                f"{probe.action} → {probe.method} {probe.path} 到达网关",
                arrived,
                "假网关没收到任何请求" if not arrived else "",
            )

            if not arrived:
                continue

            rec = after[-1]
            checks.check(
                f"{probe.action} 的路径与方法正确",
                rec.path == probe.path and rec.method == probe.method,
                f"收到 {rec.method} {rec.path}",
            )

            # 鉴权头：除 /healthz 外都要带
            has_auth = "Authorization" in rec.headers
            checks.check(
                f"{probe.action} {'带' if probe.path != '/healthz' else '不带'} Authorization",
                has_auth if probe.path != "/healthz" else not has_auth,
                f"实际头：{list(rec.headers)}",
            )

            if probe.body is not None:
                checks.check(
                    f"{probe.action} 的请求体是合法 JSON 且带 model/参数",
                    _body_ok(rec.body),
                    f"体：{rec.body[:120]}",
                )
    finally:
        gw.stop()


def _body_ok(body: str) -> bool:
    if not body:
        return False
    try:
        parsed = json.loads(body)
    except json.JSONDecodeError:
        return False
    return isinstance(parsed, dict) and len(parsed) > 0


def run_failure_path(checks: Checks, api_key: str) -> None:
    """§2 失败路径：网关不可用时要能看到明确失败，而不是静默。"""
    checks.section("§2 失败路径：网关未启动 / 拒绝连接")

    # ① 打一个**没人监听**的端口 —— 模拟网关没启动
    dead = free_port()
    probe = ACTION_PROBES[0]
    status, detail = send_probe(dead, probe, api_key)

    checks.check(
        "网关未启动时，请求得到明确的连接失败（状态码 0 + 非空错误）",
        status == 0 and detail != "",
        f"status={status} detail={detail[:80]}",
    )

    # ② 网关在但拒绝（--negate）：同样必须失败，不能静默成功
    gw = FakeGateway(negate=True)
    port = gw.start()
    try:
        status, detail = send_probe(port, probe, api_key)
        checks.check(
            "网关拒绝请求时，客户端拿到失败而不是静默成功",
            status == 0,
            f"status={status} detail={detail[:80]}",
        )

        # ★ 而且**请求确实到达过**（证明不是「没发出去」而是「被拒」）
        checks.check(
            "拒绝场景下请求仍留下了到达记录（区分「没发」与「被拒」）",
            len(gw.snapshot()) >= 1,
            "一条记录都没有 —— 那说明请求压根没发出去",
        )
    finally:
        gw.stop()


def run_app_probe(checks: Checks, api_key: str) -> None:
    """
    §3（可选）起真 App，看它在假网关上报了什么。

    ⚠ 需要 GUI 环境。**默认不跑** —— 本机常无人值守，GUI 起来了也
    没人点按钮，而 App 的 `--probe` 截图模式只能证明「渲染了」，
    证明不了「请求发了」。所以这条在无人值守时**如实标为 SKIP**，
    不伪装成通过。
    """
    checks.section("§3 真 App 挂到假网关（需 GUI，可能 SKIP）")

    exe = _find_app_exe()
    if exe is None:
        print("  SKIP  未找到 BuddyDeck.App 的可执行文件 —— 跳过（如实标记，不算通过）")
        checks.rows.append({"name": "真 App 挂假网关", "status": "skip", "detail": "未找到 exe"})
        return

    gw = FakeGateway()
    port = gw.start()
    env = dict(os.environ, BUDDYDECK_PORT=str(port), BUDDYDECK_API_KEY=api_key)
    env.pop("BUDDYDECK_OFFLINE", None)

    proc = None
    try:
        proc = subprocess.Popen(  # noqa: S603
            [exe, "--probe", os.path.join(os.environ.get("TEMP", "."), "clicktrace-shots")],
            env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )

        # 轮询等待 App 发出第一批请求（不 sleep 硬等）
        deadline = time.time() + 60
        while time.time() < deadline and len(gw.snapshot()) == 0 and proc.poll() is None:
            time.sleep(0.25)

        records = gw.snapshot()
        checks.check(
            f"真 App 启动后向假网关发出了请求（收到 {len(records)} 条）",
            len(records) > 0,
            "60 秒内一条都没收到",
        )

        if records:
            paths = sorted({r.path for r in records})
            print(f"  收到的路径：{', '.join(paths)}")
    finally:
        if proc is not None and proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
        gw.stop()


def _find_app_exe() -> str | None:
    """找编译好的 App 可执行文件（找不到返回 None）。"""
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    for rel in (
        r"src\BuddyDeck.App\bin\Debug\net8.0-windows\BuddyDeck.App.exe",
        r"src\BuddyDeck.App\bin\Release\net8.0-windows\BuddyDeck.App.exe",
    ):
        candidate = os.path.join(root, rel)
        if os.path.isfile(candidate):
            return candidate
    return None


def run_offline_app(checks: Checks, api_key: str) -> None:
    """
    §4 真 App + **网关确实没启动** —— 验「明确失败而非静默」。

    做法：把 App 指向一个**确认没人监听**的端口
    （不是 §2 那种「端口在但拒绝」—— 连不上 vs 连上了被拒是两种失败模式）。

    断言的是**硬事实**：离线状态下 App 不崩、不挂死、正常退出且仍产出截图。
    至于界面上那句「连不上网关」文案，由人工看一眼截图判读 ——
    **不把它做成自动断言**，因为那要靠 OCR / 像素比对，
    那种断言的假阳性率比它挡住的 bug 还多。
    如实标为「需人工看一眼」，比造一个看起来自动、实际不可靠的检查强。
    """
    checks.section("§4 真 App + 网关确实未启动（验明确失败而非静默）")

    exe = _find_app_exe()
    if exe is None:
        print("  SKIP  未找到 BuddyDeck.App 的可执行文件 —— 跳过（如实标记，不算通过）")
        checks.rows.append({"name": "离线 App 表现", "status": "skip", "detail": "未找到 exe"})
        return

    dead_port = free_port()          # 绑一下就放掉 —— 确认没人监听
    shots = os.path.join(os.environ.get("TEMP", "."), "clicktrace-offline")
    os.makedirs(shots, exist_ok=True)
    for stale in os.listdir(shots):
        try:
            os.remove(os.path.join(shots, stale))
        except OSError:
            pass

    env = dict(os.environ, BUDDYDECK_PORT=str(dead_port), BUDDYDECK_API_KEY=api_key)
    env.pop("BUDDYDECK_OFFLINE", None)   # ⚠ 留着它会绕过「连不上」这条路径

    proc = subprocess.Popen(  # noqa: S603
        [exe, "--probe", shots], env=env,
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )

    try:
        deadline = time.time() + 90
        while time.time() < deadline and proc.poll() is None:
            time.sleep(0.5)

        exited = proc.poll() is not None
        checks.check(
            "网关未启动时 App 仍能正常跑完（不崩、不挂死）",
            exited,
            "90 秒还没退出 —— 可能卡在重试循环里",
        )

        if not exited:
            proc.kill()
            return

        checks.check(
            f"离线状态下正常退出（退出码 {proc.returncode}）",
            proc.returncode == 0,
            f"非零退出码 {proc.returncode}",
        )

        pngs = [f for f in os.listdir(shots) if f.endswith(".png")]
        checks.check(
            f"离线截图已生成（{len(pngs)} 张）",
            len(pngs) > 0,
            f"目录 {shots} 里没有 png",
        )

        if pngs:
            print("  需人工看一眼「总览」截图里的「连不上网关」卡片是否在：")
            print(f"      {os.path.join(shots, 'ui-dashboard.png')}")
    finally:
        if proc.poll() is None:
            proc.kill()


def main() -> int:
    # ⚠ 先把 stdout 强制成 UTF-8。
    #
    # Windows 控制台默认是 GBK（cp936），而本脚本的输出里有 `✅`、`§`、
    # `→` 这类字符 —— 直接 print 会抛 UnicodeEncodeError 并**在打印文档头
    # 时就崩掉**（实测踩到）。reconfigure 是 3.7+ 的稳定做法，
    # 比「把字符全去掉」好：那些符号本身就是给人扫读用的。
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except (AttributeError, OSError):
            pass  # 老 Python / 被重定向的流 —— 后面 errors="replace" 兜住

    parser = argparse.ArgumentParser(
        description="界面动作到网关请求的可见性验证（假上游法）")
    parser.add_argument("--json", action="store_true", help="机器可读输出")
    parser.add_argument("--run", default=None, help="只跑某一类交互（按动作名模糊匹配）")
    parser.add_argument("--with-app", action="store_true", help="另起真 App（需 GUI）")
    parser.add_argument("--offline-app", action="store_true",
                        help="只跑 §4：真 App + 网关确实未启动（需 GUI）")
    parser.add_argument("--api-key", default="click-trace-key", help="让探针带的 Bearer key")
    args = parser.parse_args()

    if not args.json:
        print(__doc__.split("## 用法")[0].strip())
        print()

    checks = Checks()

    if args.with_app:
        run_app_probe(checks, args.api_key)
        run_offline_app(checks, args.api_key)
    elif args.offline_app:
        run_offline_app(checks, args.api_key)
    else:
        run_requests(checks, args.api_key, args.run)
        run_failure_path(checks, args.api_key)

    if args.json:
        print(json.dumps(
            {"passed": checks.passed, "failed": checks.failed, "checks": checks.rows},
            ensure_ascii=False, indent=2))
    else:
        total = checks.passed + checks.failed
        print(f"\n{'ALL GREEN' if checks.failed == 0 else 'FAILURES'} — "
              f"{checks.passed} passed, {checks.failed} failed, {total} total")

    return 0 if checks.failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
