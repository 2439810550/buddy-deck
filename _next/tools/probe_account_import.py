#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
直接调 BuddyDeck.App 的账号导入逻辑，验证「从旧版导入」能不能真的读到账号。

为什么要绕开界面：界面点按钮走的是 WPF 事件链，
出问题时看不出是「文件读不到」「解析失败」还是「网关拒绝」。
这个脚本把三段分开测：
  ① 旧版文件是否可读、结构对不对
  ② 解析出的账号数与标签
  ③ 网关的 /admin/accounts/import 接受什么形状
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


def main():
    legacy = pathlib.Path(os.path.expanduser("~/.workbuddy-api/accounts.cn.json"))
    say(f"① 旧版账号库: {legacy}")
    say(f"   存在: {legacy.exists()}")
    if not legacy.exists():
        say("   ⇒ 结论：文件不存在，导入必然失败")
        return write()

    raw = legacy.read_text(encoding="utf-8")
    doc = json.loads(raw)
    accounts = doc.get("accounts", [])
    say(f"   大小: {len(raw)} 字节   账号数: {len(accounts)}")
    say(f"   activeId: {doc.get('activeId', '(无)')}")

    # 逐条看必需字段是否齐全
    required = ["id", "uid", "domain", "accessToken", "expiresAtMs"]
    bad = []
    for i, a in enumerate(accounts):
        missing = [k for k in required if not a.get(k)]
        if missing:
            bad.append((i, a.get("label", "?"), missing))
    say(f"   字段齐全: {'是' if not bad else f'否，{len(bad)} 条缺字段'}")
    for i, label, missing in bad[:5]:
        say(f"     #{i} [{label}] 缺: {missing}")

    labels = [a.get("label", "") for a in accounts]
    say(f"② 解析标签: {', '.join(labels[:8])}{' …' if len(labels) > 8 else ''}")

    # ③ 网关接受什么形状 —— 试 import 端点
    site = os.environ.get("BUDDYDECK_PORT", "8795")
    key = os.environ.get("BUDDYDECK_API_KEY", "sk-demo")
    base = f"http://127.0.0.1:{site}"

    say(f"③ 网关 {base}/admin/accounts/import")

    # 先看现有账号数
    try:
        req = urllib.request.Request(f"{base}/admin/accounts", headers={"Authorization": f"Bearer {key}"})
        with urllib.request.urlopen(req, timeout=10) as r:
            cur = json.loads(r.read().decode("utf-8"))
        say(f"   导入前账号数: {len(cur.get('accounts', []))}")
    except Exception as e:
        say(f"   读 /admin/accounts 失败: {type(e).__name__}: {e}")

    # 试两种载荷形状（JSON 字符串 / 对象）
    payloads = [
        ("整份文件 JSON 字符串", json.dumps({"payload": raw})),
        ("整份文件对象", json.dumps({"document": doc}, ensure_ascii=False)),
        ("Vault 文档形状", json.dumps({"accounts": accounts, "activeId": doc.get("activeId", "")}, ensure_ascii=False)),
    ]

    for name, body in payloads:
        try:
            req = urllib.request.Request(
                f"{base}/admin/accounts/import",
                data=body.encode("utf-8"),
                headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
                method="POST")
            with urllib.request.urlopen(req, timeout=20) as r:
                text = r.read().decode("utf-8", errors="replace")
                say(f"   [{name}] HTTP {r.status}  {text[:200]}")
        except urllib.error.HTTPError as e:
            text = e.read().decode("utf-8", errors="replace")
            say(f"   [{name}] HTTP {e.code}  {text[:200]}")
        except Exception as e:
            say(f"   [{name}] {type(e).__name__}: {e}")

    return write()


def write():
    p = pathlib.Path(r"D:\workfandaiapi\_next\.local\import-probe.txt")
    p.write_text("\n".join(OUT), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
