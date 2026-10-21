#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
验证界面侧的积分反序列化：网关真实响应 → 界面 DTO 的字段是否对得上。

为什么要单独验这一步：
  网关回的是 `{accountTag, label, totalCount, totalDosage, packages:[…]}`，
  而界面 DTO 期望 `{id, totalRemaining, checkinRemaining, …}` ——
  **字段名对不上时 System.Text.Json 默默忽略，不抛异常**，
  结果就是"界面显示 0 积分，日志里什么都没有"。

这个脚本把两边的字段名摆在一起比对，不依赖"跑起来看看"。
"""

import json
import os
import pathlib
import sys
import urllib.request

OUT = []


def say(s):
    OUT.append(str(s))


# 界面 DTO 声明期望的 JSON 键名（来自 IGatewayClient.cs 的 JsonPropertyName）
GUI_EXPECTS = {
    "AccountCredits": ["accountTag", "label", "totalDosage", "totalCount", "packages"],
    "CreditPackageInfo": ["packageName", "remain", "size", "cycleEndTime", "grantSource"],
}


def main():
    port = os.environ.get("BUDDYDECK_PORT", "8795")
    key = os.environ.get("BUDDYDECK_API_KEY", "sk-demo")
    base = f"http://127.0.0.1:{port}"

    say(f"网关 {base}")
    say("")

    try:
        req = urllib.request.Request(f"{base}/admin/credits",
                                     headers={"Authorization": f"Bearer {key}"})
        with urllib.request.urlopen(req, timeout=60) as r:
            body = json.loads(r.read().decode("utf-8"))
    except Exception as e:
        say(f"请求失败：{type(e).__name__}: {e}")
        return write()

    say("① 网关顶层键：")
    for k in body.keys():
        v = body[k]
        say(f"     {k} = {'[数组 %d]' % len(v) if isinstance(v, list) else v}")

    say("")
    say("② 界面 DTO 期望的键：")
    missing = []
    for k in GUI_EXPECTS["AccountCredits"]:
        hit = k in body
        say(f"     {'✅' if hit else '❌'} {k}")
        if not hit:
            missing.append(k)

    if body.get("packages"):
        say("")
        say("③ 积分包的键（第一个包）：")
        pkg = body["packages"][0]
        for k in GUI_EXPECTS["CreditPackageInfo"]:
            hit = k in pkg
            say(f"     {'✅' if hit else '❌'} {k}")
            if not hit:
                missing.append(f"package.{k}")

    say("")
    if missing:
        say(f"⇒ 有 {len(missing)} 个键对不上：{missing}")
        say("   后果：那些字段在界面上是默认值（0 / 空串），**且不报错**。")
    else:
        say("⇒ 全部对得上，界面能正确反序列化。")

    return write()


def write():
    pathlib.Path(r"D:\workfandaiapi\_next\.local\credits-shape.txt").write_text(
        "\n".join(OUT), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
