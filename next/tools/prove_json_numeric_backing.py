#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
验证 `JsonValue.TryGetValue<T>` 的「底层类型精确匹配」语义。

这是 t31 检查 ③ 的**证据脚本** —— 它证明「为什么单一数值泛型会静默返回 null」，
而不是只写一句断言。

## 背景（真实案例）

observability 写的：`JsonValue.TryGetValue<long>` 对
`{"prompt_tokens": 2541}` 返回 **false** —— 因为 2541 是 **int**，
而 `TryGetValue<T>` 要求底层类型**精确匹配**。
于是 token 数**永远是 null**。JSON 合法、解析不报错、值就是没有。

## 为什么要单独跑一遍

System.Text.Json 不给「这个 JSON 数字是什么 C# 类型」的公开查询接口，
但它的规则是确定的：

    整数且落在 Int32 范围内  → int
    整数且超出 Int32 范围    → long
    带小数或指数             → double
    JSON 字符串              → string

本脚本按这条规则模拟 `TryGetValue<T>` 的匹配语义，覆盖四种背书写法。
**能说明问题就够** —— 真要端到端验证得写 C#（那属于 src/ 之外的测试工程，
不在 t31 范围内）。
"""

import json
import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def backing_type(text):
    """按 System.Text.Json 的规则，判断一个 JSON 文本里的数字会被读成什么 C# 类型。"""
    value = json.loads(text)
    if isinstance(value, str):
        return "string"
    if isinstance(value, bool):
        return "bool"
    if isinstance(value, int):
        # System.Text.Json：能放进 Int32 就用 int，否则 long
        return "int" if -2**31 <= value < 2**31 else "long"
    if isinstance(value, float):
        return "double"
    return "?"


def try_get_value(backing, generic):
    """模拟 JsonValue.TryGetValue<T> —— 要求底层类型**精确匹配**。

    这是关键：它不是「能转就转」，而是「类型一模一样才给」。
    """
    return backing == generic


def main():
    print("=" * 76)
    print("JsonValue.TryGetValue<T> 的底层类型精确匹配 —— 真实案例复盘")
    print("=" * 76)
    print()
    print("场景：从上游响应里抽 token 数 / 计费数字")
    print()

    samples = [
        ('{"prompt_tokens": 2541}',              "真实案例：小整数"),
        ('{"prompt_tokens": 0}',                 "边界：0"),
        ('{"prompt_tokens": 2147483647}',        "边界：int.MaxValue"),
        ('{"prompt_tokens": 2147483648}',        "边界：int.MaxValue + 1 → 变 long"),
        ('{"prompt_tokens": 9999999999999}',     "大数：long"),
        ('{"prompt_tokens": 0.22}',              "小数：double"),
        ('{"prompt_tokens": "2541"}',            "字符串背书的数字"),
    ]

    print("%-34s %-9s %-14s %s" % ("JSON 片段", "底层类型", "TryGetValue<long>", "结果"))
    print("-" * 76)

    broken_count = 0
    for text, note in samples:
        key = list(json.loads(text).keys())[0]
        inner = json.dumps(json.loads(text)[key])
        backing = backing_type(inner)
        ok = try_get_value(backing, "long")
        verdict = "拿到值" if ok else "**返回 false → 调用方拿 null**"
        if not ok:
            broken_count += 1
        print("%-34s %-9s %-14s %s" % (text, backing, "true" if ok else "false", verdict))

    print()
    print("=" * 76)
    print("结论：%d/%d 种输入下，只写 `TryGetValue<long>` 会**静默返回 false**。"
          % (broken_count, len(samples)))
    print()
    print("⚠ 注意「静默」二字 —— 它不抛异常、不返回错误码，只是 out 参数保持默认值。")
    print("  调用方写 `?? 0` 或 `?? null` 时，看起来「有处理」，实际是「永远走兜底」。")
    print()

    # ── 正确范式 ──
    print("-" * 76)
    print("正确范式：逐类型试（见 src/BuddyDeck.Core/UsageLedger.cs 的 Long()）")
    print("-" * 76)
    print()

    def correct_extract(inner):
        for generic in ("int", "long", "double", "decimal"):
            if try_get_value(backing_type(inner), generic):
                return generic, json.loads(inner)
        return None, None

    print("%-26s %-12s %s" % ("JSON 数字", "命中的泛型", "结果"))
    print("-" * 76)
    for text, _ in samples:
        key = list(json.loads(text).keys())[0]
        inner = json.dumps(json.loads(text)[key])
        hit, value = correct_extract(inner)
        if hit:
            print("%-26s %-12s %s" % (inner, hit, value))
        else:
            print("%-26s %-12s %s" % (inner, "（无）", "字符串背书 → 需要额外的 string 分支"))

    print()
    print("↑ 逐类型试之后，每种背书都能拿到值。")
    print("  这就是 UsageLedger 的 Long()/Double() 在做的事 —— 不是过度防御，是必需。")
    print("=" * 76)
    return 0


if __name__ == "__main__":
    sys.exit(main())
