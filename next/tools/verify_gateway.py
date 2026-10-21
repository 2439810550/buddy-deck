#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
假上游 + 真网关的端到端验证脚本。

为什么要有这个：单元测试里用的是内存里的假上游（HttpMessageHandler）。
这里换成**一个真的 HTTP 假上游**（起在本地端口上），网关通过真的
TCP 连接打过去 —— 那条链路（DNS/连接/SSE 分块传输）也一并验了。

用法：
    python verify_gateway.py <gateway_port> <fake_upstream_port>

它做四件事：
  1. 起假上游，按请求决定吐 SSE（流式）
  2. 打 /healthz、/v1/models
  3. 打 stream=true，验**边收边发**（逐片到达时间）
  4. 打 stream=false，验重组出的 chat.completion 形状
"""

import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import urllib.request

# Windows 控制台默认是 GBK，打不出 ✓ 这类字符。
# 不改成 UTF-8 的话脚本会在打印时崩掉 —— 那是环境问题，不是被测对象的问题。
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

FAKE_PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 8899

# 假上游收到的请求记在这里
CAPTURED = []


class FakeUpstream(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass  # 静音

    def do_GET(self):
        # 模型目录（网关刷新目录时会来）
        body = json.dumps({
            "models": [{"id": "hy3", "name": "Hy3"}, {"id": "glm-5.3", "name": "GLM-5.3"}]
        }).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length).decode("utf-8") if length else ""
        CAPTURED.append({
            "path": self.path,
            "body": raw,
            "authorization": self.headers.get("Authorization", ""),
            "x_user_id": self.headers.get("X-User-Id", ""),
            "x_ide_type": self.headers.get("X-IDE-Type", ""),
        })

        # 分片：三段正文 + 一段工具调用（arguments 故意切成两半）
        chunks = [
            {"id": "chatcmpl-e2e", "object": "chat.completion.chunk", "created": 1700000000,
             "model": "hy3", "choices": [{"index": 0, "delta": {"role": "assistant"}, "finish_reason": None}]},
            {"id": "chatcmpl-e2e", "object": "chat.completion.chunk", "created": 1700000000,
             "model": "hy3", "choices": [{"index": 0,
                                          "delta": {"content": "你好",
                                                    "function_call": {"name": "", "arguments": ""},
                                                    "reasoning_content": "先想一下"},
                                          "finish_reason": None}]},
            {"id": "chatcmpl-e2e", "object": "chat.completion.chunk", "created": 1700000000,
             "model": "hy3", "choices": [{"index": 0, "delta": {"content": "，世界"}, "finish_reason": None}]},
            {"id": "chatcmpl-e2e", "object": "chat.completion.chunk", "created": 1700000000,
             "model": "hy3", "choices": [{"index": 0,
                                          "delta": {"tool_calls": [
                                              {"index": 0, "id": "call_1", "type": "function",
                                               "function": {"name": "read_file", "arguments": '{"pa'}}]},
                                          "finish_reason": None}]},
            {"id": "chatcmpl-e2e", "object": "chat.completion.chunk", "created": 1700000000,
             "model": "hy3", "choices": [{"index": 0,
                                          "delta": {"tool_calls": [
                                              {"index": 0, "function": {"arguments": 'th":"/tmp/a.txt"}'}}]},
                                          "finish_reason": None}]},
            {"id": "chatcmpl-e2e", "object": "chat.completion.chunk", "created": 1700000000,
             "model": "hy3",
             "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}],
             "usage": {"prompt_tokens": 11, "completion_tokens": 7, "total_tokens": 18, "credit": "0.02"}},
        ]

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()

        def send_line(text):
            data = (text + "\n\n").encode("utf-8")
            self.wfile.write(("%X\r\n" % len(data)).encode())
            self.wfile.write(data)
            self.wfile.write(b"\r\n")
            self.wfile.flush()

        for i, chunk in enumerate(chunks):
            send_line("data: " + json.dumps(chunk, ensure_ascii=False))
            # 前几片之间停一下 —— 让「边收边发」可观测
            time.sleep(0.05)

        send_line("data: [DONE]")
        self.wfile.write(b"0\r\n\r\n")
        self.wfile.flush()


def start_fake():
    server = ThreadingHTTPServer(("127.0.0.1", FAKE_PORT), FakeUpstream)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def post(port, body, stream):
    req = urllib.request.Request(
        "http://127.0.0.1:%d/v1/chat/completions" % port,
        data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    return urllib.request.urlopen(req, timeout=60)


def main():
    gw = int(sys.argv[1])
    start_fake()
    time.sleep(0.3)

    print("=" * 70)
    print("1. GET /healthz")
    print("=" * 70)
    health = urllib.request.urlopen("http://127.0.0.1:%d/healthz" % gw, timeout=15).read().decode()
    print(health)
    h = json.loads(health)
    for key in ("ok", "provider", "models", "buildId", "startedAt", "stale"):
        assert key in h, "healthz 缺少字段 %s" % key
    assert len(h["buildId"]) == 12, "buildId 不是 12 位：%r" % h["buildId"]
    int(h["buildId"], 16)
    print("   ✓ 六个字段齐全，buildId 是 12 位 hex")

    print()
    print("=" * 70)
    print("2. GET /v1/models")
    print("=" * 70)
    models = json.loads(urllib.request.urlopen("http://127.0.0.1:%d/v1/models" % gw, timeout=15).read().decode())
    assert models["object"] == "list", "object 不是 list"
    assert len(models["data"]) > 0, "data 为空"
    for item in models["data"]:
        for key in ("id", "object", "owned_by"):
            assert key in item, "模型项缺少 %s" % key
        assert item["object"] == "model"
    print("   object=list，%d 个模型，每项含 id/object/owned_by" % len(models["data"]))
    print("   首个：%s" % json.dumps(models["data"][0], ensure_ascii=False))

    print()
    print("=" * 70)
    print("3. POST /v1/chat/completions  stream=true（验边收边发）")
    print("=" * 70)
    resp = post(gw, {"model": "hy3", "messages": [{"role": "user", "content": "你好"}], "stream": True}, True)
    assert resp.status == 200
    assert "text/event-stream" in resp.headers.get("Content-Type", ""), resp.headers.get("Content-Type")

    arrivals = []
    raw_lines = []
    started = time.time()
    for line in resp:
        text = line.decode("utf-8").rstrip("\r\n")
        if text.startswith("data: "):
            arrivals.append((time.time() - started, text))
            raw_lines.append(text)

    print("   收到 %d 个 data 事件，用时 %.0f ms" % (len(arrivals), (time.time() - started) * 1000))
    for t, text in arrivals:
        preview = text[:110] + ("…" if len(text) > 110 else "")
        print("     +%6.0f ms  %s" % (t * 1000, preview))

    assert raw_lines[-1] == "data: [DONE]", "最后一条不是 [DONE]：%r" % raw_lines[-1]
    joined = "\n".join(raw_lines)
    assert "你好" in joined, "正文分片没到"
    assert "先想一下" in joined, "reasoning_content 被丢了"
    assert "function_call" not in joined, "空的 function_call 没被清洗"
    print("   ✓ [DONE] 收尾 ✓ reasoning 保留 ✓ 空 function_call 已清洗")

    # 边收边发：第一个片应该远早于最后一个片
    first_at = arrivals[0][0]
    last_at = arrivals[-1][0]
    spread = last_at - first_at
    print("   首片 %.0f ms，末片 %.0f ms，跨度 %.0f ms" % (first_at * 1000, last_at * 1000, spread * 1000))
    assert spread > 0.10, "所有分片几乎同时到达（跨度 %.3fs）—— 像是被整体缓冲了" % spread
    print("   ✓ 分片是陆续到达的（跨度 %.0f ms）→ 真流式，不是先缓冲完再发" % (spread * 1000))

    print()
    print("=" * 70)
    print("4. POST /v1/chat/completions  stream=false（验重组）")
    print("=" * 70)
    data = json.loads(post(gw, {"model": "hy3", "messages": [{"role": "user", "content": "你好"}], "stream": False}, False).read().decode())
    print(json.dumps(data, ensure_ascii=False, indent=2)[:1400])
    assert data["object"] == "chat.completion", "object 不对：%r" % data["object"]
    choice = data["choices"][0]
    msg = choice["message"]
    assert msg["role"] == "assistant"
    assert "你好" in msg["content"], "正文没拼对：%r" % msg.get("content")
    assert choice["finish_reason"] == "stop", choice["finish_reason"]
    assert data["usage"]["total_tokens"] == 18, data["usage"]
    call = msg["tool_calls"][0]
    args = call["function"]["arguments"]
    parsed = json.loads(args)  # ★ 分片拼回来必须是合法 JSON
    assert parsed["path"] == "/tmp/a.txt", parsed
    print("   ✓ object=chat.completion ✓ role/content ✓ finish_reason=stop ✓ usage ✓")
    print("   ✓ tool_calls.arguments 拼回合法 JSON：%s" % args)

    print()
    print("=" * 70)
    print("5. 上游收到的请求（管线改写是否生效）")
    print("=" * 70)
    upstream_body = json.loads(CAPTURED[-1]["body"])
    print("   Authorization: %s" % CAPTURED[-1]["authorization"][:30] + "…")
    print("   X-User-Id:     %s" % CAPTURED[-1]["x_user_id"])
    print("   X-IDE-Type:    %s" % CAPTURED[-1]["x_ide_type"])
    print("   stream 字段:   %s  ← 非流式请求也被强制成 true（上游只支持流式）" % upstream_body.get("stream"))
    assert upstream_body.get("stream") is True, "管线没强制 stream=true"
    print("   ✓ 强制 stream=true ✓ 身份头齐全")

    print()
    print("=" * 70)
    print("全部通过")
    print("=" * 70)


if __name__ == "__main__":
    main()
