#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
对 BuddyDeck 新网关跑真实账号端到端测评。

用途：验证【新版 .NET 网关】能否用真实账号跑通全流程 ——
      不是验证账号本身有效，那是旧版网关就能证的。

用法：
  python tools/live_eval.py --base http://127.0.0.1:8795 --key sk-local-probe
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request

RESULTS = []


def call(method, url, key, body=None, timeout=120, raw=False):
    """发一个请求，返回 (http_code, 解析后的内容 或 原始文本)。"""
    data = None
    headers = {"Authorization": f"Bearer {key}"}
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            text = r.read().decode("utf-8", errors="replace")
            dt = time.time() - t0
            if raw:
                return r.status, text, dt
            try:
                return r.status, json.loads(text), dt
            except json.JSONDecodeError:
                return r.status, text, dt
    except urllib.error.HTTPError as e:
        text = e.read().decode("utf-8", errors="replace")
        dt = time.time() - t0
        try:
            return e.code, json.loads(text), dt
        except json.JSONDecodeError:
            return e.code, text, dt
    except Exception as e:  # 连接失败等
        return 0, f"{type(e).__name__}: {e}", time.time() - t0


def record(name, ok, detail):
    RESULTS.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}")
    print(f"         {detail}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://127.0.0.1:8795")
    ap.add_argument("--key", default="sk-local-probe")
    ap.add_argument("--model", default="deepseek-v4.1-flash")
    args = ap.parse_args()
    base, key = args.base.rstrip("/"), args.key

    print(f"目标网关: {base}\n模型: {args.model}\n" + "=" * 62)

    # ── 1. 健康与模型目录 ────────────────────────────────
    print("\n【1】基础可用性")
    code, body, dt = call("GET", f"{base}/healthz", key)
    record("healthz", code == 200 and isinstance(body, dict) and body.get("ok"),
           f"HTTP {code}, {dt:.2f}s, {json.dumps(body, ensure_ascii=False)[:100] if isinstance(body, dict) else str(body)[:100]}")

    code, body, dt = call("GET", f"{base}/v1/models", key)
    n = len(body.get("data", [])) if isinstance(body, dict) else 0
    record("GET /v1/models", code == 200 and n > 0, f"HTTP {code}, {n} 个模型, {dt:.2f}s")

    # ── 2. 非流式聊天 ────────────────────────────────────
    print("\n【2】非流式聊天")
    code, body, dt = call("POST", f"{base}/v1/chat/completions", key, {
        "model": args.model,
        "messages": [{"role": "user", "content": "用一句话说明你是什么模型。只回一句话。"}],
        "max_tokens": 100,
        "stream": False,
    })
    ok = code == 200 and isinstance(body, dict) and body.get("choices")
    content = ""
    usage = {}
    if ok:
        content = body["choices"][0]["message"]["content"]
        usage = body.get("usage", {})
    record("POST /v1/chat/completions", ok,
           f"HTTP {code}, {dt:.2f}s\n         回复: {content[:120]}\n"
           f"         usage: {json.dumps(usage, ensure_ascii=False)[:160]}")

    # ── 3. 流式聊天（SSE）────────────────────────────────
    print("\n【3】流式聊天（SSE）")
    code, text, dt = call("POST", f"{base}/v1/chat/completions", key, {
        "model": args.model,
        "messages": [{"role": "user", "content": "从1数到5，只回数字。"}],
        "max_tokens": 60,
        "stream": True,
    }, raw=True)
    nchunks = text.count("data:") if isinstance(text, str) else 0
    done = "[DONE]" in text if isinstance(text, str) else False
    record("流式 SSE", code == 200 and nchunks > 1 and done,
           f"HTTP {code}, {dt:.2f}s, {nchunks} 个 data 块, 有[DONE]={done}")

    # ── 4. 多轮 + 会话粘性 ───────────────────────────────
    print("\n【4】多轮对话")
    msgs = [{"role": "user", "content": "记住数字 4271，只回『已记住』。"},
            {"role": "assistant", "content": "已记住"},
            {"role": "user", "content": "我刚才让你记的数字是多少？只回数字。"}]
    code, body, dt = call("POST", f"{base}/v1/chat/completions", key, {
        "model": args.model, "messages": msgs, "max_tokens": 40, "stream": False,
    })
    ok = code == 200 and isinstance(body, dict) and body.get("choices")
    reply = body["choices"][0]["message"]["content"] if ok else ""
    record("多轮上下文", ok and "4271" in reply, f"HTTP {code}, 回复: {reply[:80]}")

    # ── 5. 错误处理 ──────────────────────────────────────
    print("\n【5】错误处理与模型回退")
    # 未知模型【不是】错误：ModelCatalog.Resolve 会回退到默认模型（与旧版一致）。
    # 早期版本这里断言「必须返回 4xx」，那是把设计行为当 bug 报 —— 断言写错了。
    code, body, dt = call("POST", f"{base}/v1/chat/completions", key, {
        "model": "nonexistent-model-xyz",
        "messages": [{"role": "user", "content": "只回：ok"}],
        "max_tokens": 10,
    })
    used = body.get("model", "") if isinstance(body, dict) else ""
    ok = code == 200 and used and used != "nonexistent-model-xyz"
    record("未知模型优雅回退到默认模型", ok,
           f"HTTP {code}, 请求=nonexistent-model-xyz → 实际使用={used}")

    code, body, dt = call("POST", f"{base}/v1/chat/completions", "wrong-key", {
        "model": args.model, "messages": [{"role": "user", "content": "hi"}], "max_tokens": 5,
    })
    record("错误 API key 返回 401", code == 401, f"HTTP {code}")

    # ── 6. 指标 ──────────────────────────────────────────
    print("\n【6】指标可观测")
    code, body, dt = call("GET", f"{base}/metrics", key)
    ok = code == 200 and isinstance(body, dict)
    keys = list(body.keys())[:8] if ok else []
    record("GET /metrics", ok, f"HTTP {code}, 字段: {keys}")

    # ── 7. Anthropic 翻译层 ──────────────────────────────
    print("\n【7】Anthropic 翻译层")
    code, body, dt = call("POST", f"{base}/v1/messages", key, {
        "model": args.model,
        "max_tokens": 60,
        "messages": [{"role": "user", "content": "只回复两个字：收到"}],
    })
    ok = code == 200 and isinstance(body, dict) and body.get("content")
    txt = ""
    if ok:
        for blk in body.get("content", []):
            if blk.get("type") == "text":
                txt += blk.get("text", "")
    record("POST /v1/messages", ok, f"HTTP {code}, {dt:.2f}s, 回复: {txt[:100]}")

    # ── 汇总 ─────────────────────────────────────────────
    print("\n" + "=" * 62)
    passed = sum(1 for _, ok, _ in RESULTS if ok)
    total = len(RESULTS)
    print(f"结果: {passed}/{total} 通过")
    for name, ok, _ in RESULTS:
        if not ok:
            print(f"  FAILED: {name}")
    return 0 if passed == total else 1


if __name__ == "__main__":
    sys.exit(main())
