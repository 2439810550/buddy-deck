#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
实测聊天页的媒体识别链路：生图/生视频的回复，界面能不能认出来。

做法：对真实网关发一次生图请求，拿到回复文本，
然后看那段文本里的 URL 是否满足界面的白名单判据。

为什么要单独验这一步：用户实测反馈「聊天测试生成图片和视频没法在回复框看见」。
可能断在三处，必须分开定位：
  ① 模型根本没返回 URL（那是提示词/工具注入问题）
  ② 返回了 URL，但界面的白名单把它拦了（MediaUrlGuard）
  ③ 识别出来了，但渲染/滚动没跟上

这个脚本只验 ① 和 ②。
"""

import json
import os
import pathlib
import sys
import urllib.error
import urllib.request

OUT = []


def say(s):
    OUT.append(str(s))


def post(base, key, path, body, timeout=300):
    req = urllib.request.Request(
        base + path,
        data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
        method="POST")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, json.loads(r.read().decode("utf-8", errors="replace"))
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")
    except Exception as e:
        return 0, f"{type(e).__name__}: {e}"


def main():
    port = os.environ.get("BUDDYDECK_PORT", "8795")
    key = os.environ.get("BUDDYDECK_API_KEY", "sk-demo")
    base = f"http://127.0.0.1:{port}"

    say(f"网关 {base}")
    say("")

    # ── ① 生图端点直接调（绕开聊天，先确认网关能出图）──
    say("① POST /v1/images/generations")
    code, body = post(base, key, "/v1/images/generations",
                      {"model": "miora-image", "prompt": "一只戴帽子的猫", "n": 1})
    say(f"   HTTP {code}")

    url = ""
    if isinstance(body, dict):
        data = body.get("data") or []
        if data:
            url = data[0].get("url", "")
            say(f"   返回 url: {url}")
        else:
            say(f"   body: {json.dumps(body, ensure_ascii=False)[:300]}")
    else:
        say(f"   body: {str(body)[:300]}")

    if not url:
        say("   ⇒ 生图本身没出 URL，聊天里更不可能有。**先修这里。**")
        return write()

    # ── ② 让聊天走 tool 调用（模型自己调生图工具）──
    say("")
    say("② POST /v1/chat/completions（让模型自己调生图工具）")
    code, body = post(base, key, "/v1/chat/completions", {
        "model": "deepseek-v4.1-flash",
        "messages": [{"role": "user", "content": "帮我生成一张图：一只戴帽子的猫"}],
        "max_tokens": 600,
        "stream": False,
    }, timeout=300)
    say(f"   HTTP {code}")

    text = ""
    if isinstance(body, dict) and body.get("choices"):
        text = body["choices"][0].get("message", {}).get("content", "") or ""
    else:
        text = str(body)[:400]

    say(f"   回复文本（前 400 字）:")
    for line in (text[:400] or "(空)").splitlines():
        say(f"     {line}")

    # ── ③ 判据：回复里有没有媒体 URL，以及它是否满足白名单形状 ──
    say("")
    say("③ 识别判据")
    import re
    found = re.findall(r"https?://[^\s\"'<>\)\]]+", text)
    say(f"   回复里的 URL 数: {len(found)}")
    for u in found[:5]:
        say(f"     {u}")

    expect_prefix = f"http://127.0.0.1:{port}/v1/images/file/"
    hit = [u for u in found if u.startswith(expect_prefix)]
    say(f"   匹配界面白名单前缀（{expect_prefix}）: {len(hit)}")

    if not found:
        say("   ⇒ 模型没在文本里给出 URL。")
        say("     可能原因：工具没注入（BUDDYDECK_MEDIA_TOOLS=0）、或模型选择不调工具。")
    elif not hit:
        say("   ⇒ 有 URL 但前缀不匹配 —— 界面会把它滤掉。")
        say("     核对 MediaUrlGuard.IsAllowedMediaUrl 的判据（host + port）。")
    else:
        say("   ⇒ 链路通：URL 形状正确，界面应当能渲染。")

    return write()


def write():
    pathlib.Path(r"D:\workfandaiapi\_next\.local\media-probe.txt").write_text(
        "\n".join(OUT), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
