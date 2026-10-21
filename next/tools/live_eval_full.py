#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
BuddyDeck 新网关 —— 标准范围真实账号测评（扩展版）。

覆盖：聊天 / 流式 / 多轮 / Anthropic / 生图 / 改图 / 生视频 / 搜索 / 抓取 /
      读图 / 签到状态 / 媒体回读 / 错误处理 / 指标

用法：
  python tools/live_eval_full.py --base http://127.0.0.1:8795 --key sk-local-probe
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request

RESULTS = []


def call(method, url, key, body=None, timeout=300, raw=False):
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
    except Exception as e:
        return 0, f"{type(e).__name__}: {e}", time.time() - t0


def record(name, ok, detail=""):
    RESULTS.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}")
    if detail:
        for line in str(detail).splitlines():
            # Windows 控制台默认 GBK，某些字符（阿拉伯文、部分符号）编不出去。
            # 用 backslashreplace 兜底：宁可显示成转义，也不要让测评脚本自己崩掉。
            safe = line.encode("gbk", errors="backslashreplace").decode("gbk")
            print(f"         {safe}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://127.0.0.1:8795")
    ap.add_argument("--key", default="sk-local-probe")
    ap.add_argument("--model", default="deepseek-v4.1-flash")
    ap.add_argument("--skip-media", action="store_true", help="跳过生图/生视频（省配额）")
    args = ap.parse_args()
    base, key = args.base.rstrip("/"), args.key

    print(f"目标: {base}   模型: {args.model}")
    print("=" * 64)

    # 1 基础
    print("\n【1】基础可用性")
    code, b, dt = call("GET", f"{base}/healthz", key)
    record("healthz", code == 200 and b.get("ok"), f"HTTP {code}  {b.get('models')} 模型")
    code, b, dt = call("GET", f"{base}/v1/models", key)
    record("GET /v1/models", code == 200, f"HTTP {code}  {len(b.get('data', []))} 个")

    # 2 聊天
    print("\n【2】聊天")
    code, b, dt = call("POST", f"{base}/v1/chat/completions", key, {
        "model": args.model,
        "messages": [{"role": "user", "content": "用一句话说明你是什么模型。"}],
        "max_tokens": 100, "stream": False})
    txt = b["choices"][0]["message"]["content"] if code == 200 and b.get("choices") else ""
    record("非流式", code == 200 and bool(txt), f"HTTP {code}  {dt:.2f}s  {txt[:80]}")

    code, text, dt = call("POST", f"{base}/v1/chat/completions", key, {
        "model": args.model,
        "messages": [{"role": "user", "content": "从1数到5，只回数字。"}],
        "max_tokens": 60, "stream": True}, raw=True)
    n = text.count("data:") if isinstance(text, str) else 0
    record("流式 SSE", code == 200 and n > 1 and "[DONE]" in text, f"HTTP {code}  {n} 块")

    # 3 Anthropic
    print("\n【3】Anthropic 翻译层")
    code, b, dt = call("POST", f"{base}/v1/messages", key, {
        "model": args.model, "max_tokens": 60,
        "messages": [{"role": "user", "content": "只回复两个字：收到"}]})
    atxt = ""
    if code == 200 and isinstance(b, dict):
        atxt = "".join(x.get("text", "") for x in b.get("content", []) if x.get("type") == "text")
    record("POST /v1/messages", code == 200 and bool(atxt), f"HTTP {code}  {atxt[:60]}")

    # 4 搜索
    # ⚠ 这两个端点返回的是【Markdown 文本】，不是 JSON —— 与聊天端点形状不同。
    #   它们是给模型当 tool 结果用的，人/模型直接读文本，所以没有 JSON 包裹层。
    #   早期版本断言 `isinstance(b, dict)`，那是按聊天端点的形状想当然。
    print("\n【4】联网搜索")
    code, body, dt = call("POST", f"{base}/v1/search", key, {
        "query": "DeepSeek 最新模型", "max_results": 3}, timeout=180, raw=True)
    text = body if isinstance(body, str) else json.dumps(body, ensure_ascii=False)
    has_results = ("http" in text) and len(text) > 80
    record("POST /v1/search", code == 200 and has_results,
           f"HTTP {code}  {dt:.2f}s  返回 {len(text)} 字符（Markdown 文本）\n{text[:240]}")

    # 5 抓取
    print("\n【5】网页抓取")
    code, body, dt = call("POST", f"{base}/v1/webfetch", key, {
        "url": "https://example.com", "max_length": 2000}, timeout=180, raw=True)
    text = body if isinstance(body, str) else json.dumps(body, ensure_ascii=False)
    record("POST /v1/webfetch", code == 200 and "Example Domain" in text,
           f"HTTP {code}  {dt:.2f}s  返回 {len(text)} 字符（Markdown 文本）\n{text[:240]}")

    # 6 生图
    img_url = None
    if not args.skip_media:
        print("\n【6】生图（真实配额）")
        code, b, dt = call("POST", f"{base}/v1/images/generations", key, {
            "model": "miora-image", "prompt": "一只戴墨镜的橘猫，扁平插画风格",
            "n": 1, "size": "1024x1024"}, timeout=300)
        data = b.get("data", []) if isinstance(b, dict) else []
        if data:
            img_url = data[0].get("url") or data[0].get("b64_json")
        record("POST /v1/images/generations", code == 200 and bool(data),
               f"HTTP {code}  {dt:.2f}s\n{json.dumps(b, ensure_ascii=False)[:300] if isinstance(b,dict) else str(b)[:300]}")

        # 7 媒体回读（签名 URL）
        if img_url:
            print("\n【7】媒体文件回读")
            full = img_url if img_url.startswith("http") else f"{base}{img_url}"
            code, text, dt = call("GET", full, key, raw=True)
            record("GET 图片 URL", code == 200 and len(text) > 100,
                   f"HTTP {code}  {len(text)} 字节  {dt:.2f}s")

        # 8 生视频
        print("\n【8】生视频（真实配额）")
        code, b, dt = call("POST", f"{base}/v1/video/generations", key, {
            "model": "miora-video", "prompt": "一只猫在草地上奔跑", "duration": 5}, timeout=300)
        record("POST /v1/video/generations", code in (200, 202) and isinstance(b, dict),
               f"HTTP {code}  {dt:.2f}s\n{json.dumps(b, ensure_ascii=False)[:260] if isinstance(b,dict) else str(b)[:260]}")
    else:
        print("\n【6-8】生图/回读/生视频 —— 按要求跳过")

    # 9 错误处理
    print("\n【9】错误与鉴权")
    code, b, dt = call("POST", f"{base}/v1/chat/completions", key, {
        "model": "nonexistent-xyz", "messages": [{"role": "user", "content": "ok"}], "max_tokens": 10})
    used = b.get("model", "") if isinstance(b, dict) else ""
    record("未知模型回退默认", code == 200 and used and used != "nonexistent-xyz",
           f"xyz → {used}")
    code, b, dt = call("POST", f"{base}/v1/chat/completions", "bad-key", {
        "model": args.model, "messages": [{"role": "user", "content": "hi"}], "max_tokens": 5})
    record("错误 key → 401", code == 401, f"HTTP {code}")
    code, b, dt = call("GET", f"{base}/v1/images/file/..%2Fappsettings.json", key)
    record("路径穿越被拦", code in (400, 401, 404), f"HTTP {code}")

    # 10 指标
    print("\n【10】指标")
    code, b, dt = call("GET", f"{base}/metrics", key)
    record("GET /metrics", code == 200 and isinstance(b, dict),
           f"HTTP {code}  请求数={b.get('totalRequests')}  "
           f"成功率={b.get('httpSuccessRate')}  完成率={b.get('completionRate')}" if isinstance(b, dict) else f"HTTP {code}")

    print("\n" + "=" * 64)
    passed = sum(1 for _, ok, _ in RESULTS if ok)
    print(f"结果: {passed}/{len(RESULTS)} 通过")
    for name, ok, _ in RESULTS:
        if not ok:
            print(f"  FAILED: {name}")
    return 0 if passed == len(RESULTS) else 1


if __name__ == "__main__":
    sys.exit(main())
