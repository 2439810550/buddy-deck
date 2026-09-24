# -*- coding: utf-8 -*-
"""
调用本机 workbuddy_api 网关的最小示例（零依赖，只用 Python 标准库）。

用法：
  1) 另开一个终端启动网关：
       cd D:\\workfandaiapi\\workbuddy_api
       node src/bin.js serve --port 8787 --api-key testkey
  2) 运行本文件：
       python D:\\workfandaiapi\\调用示例.py

说明：--api-key 用你自己启动时打印的那个值，改下面的 API_KEY 即可。
"""

import json
import sys
import urllib.request

# Windows 控制台默认用 GBK 输出，这里强制 UTF-8，避免中文变乱码
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

# 这两行是唯一需要改的地方
BASE_URL = "http://127.0.0.1:8787/v1"
API_KEY = "testkey"
MODEL = "deepseek-v4.1-flash"  # 可用模型见：node src/bin.js models


def chat(prompt: str, model: str = MODEL) -> str:
    """发一次非流式请求，返回整段回答文本。"""
    body = json.dumps(
        {
            "model": model,
            "messages": [{"role": "user", "content": prompt}],
            "stream": False,
        },
        ensure_ascii=False,
    ).encode("utf-8")

    request = urllib.request.Request(
        f"{BASE_URL}/chat/completions",
        data=body,
        headers={
            "Authorization": f"Bearer {API_KEY}",
            "Content-Type": "application/json; charset=utf-8",
        },
        method="POST",
    )

    with urllib.request.urlopen(request, timeout=300) as response:
        payload = json.loads(response.read().decode("utf-8"))

    return payload["choices"][0]["message"]["content"]


if __name__ == "__main__":
    answer = chat("用三句话解释什么是递归。")
    print(answer)
