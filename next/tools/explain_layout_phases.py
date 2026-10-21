#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
验证「Border 会强制子元素敷满」这件事 —— 解释为什么渲染测试抓不到量成 0 的缺陷。

## 背景

`StatusRailLayoutTests` 里我原本写了一个「真渲染后必须有像素」的用例，
预期它能抓到「漏 MeasureOverride → 量成 0 → 什么都不画」这个缺陷。

**实测：抓不到。** 给 `MeasureOverride` 注入 `return new Size(0, 0)` 之后，
渲染用例照样通过。

## 为什么

WPF 的布局分两趟：

    Measure  —— 自下而上：子元素报告 DesiredSize
    Arrange  —— 自上而下：父元素**给定**子元素的最终矩形

`Border` 的 `ArrangeOverride` 会把**自己整个可用矩形**交给子元素 ——
它不看子元素的 `DesiredSize`。于是：

    DesiredSize      = 0×0        ← MeasureOverride 说的是这个
    ActualWidth/Height = 56×200   ← Arrange 实际给的是这个
    → OnRender 里 `if (width <= 0) return;` **不成立**，照样画

所以「渲染出像素」这件事，**不能**用来证明 `MeasureOverride` 是对的 ——
它证明的是另一件事（渲染管线接受这个控件）。

## 这个脚本做什么

用 CLI 复刻同样的判断，把「依赖 Arrange 还是依赖 Measure」的区别讲清楚。
纯 Python，不依赖 WPF —— 它模拟的是**布局语义**，不是真渲染。
"""

import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def measure_phase(child_wants_w, child_wants_h):
    """Measure：子元素报告自己要多大。"""
    return (child_wants_w, child_wants_h)


def arrange_border(desired, available):
    """Border.ArrangeOverride 的语义：**不看 child.DesiredSize**，直接给整个可用矩形。"""
    return available


def arrange_stackpanel(desired, available):
    """StackPanel 的语义：**看** DesiredSize，只给子元素要的量。"""
    return desired


def onrender_draws(given_w, given_h):
    """StatusRail.OnRender 开头的守卫：`if (width <= 0 || height <= 0) return;`"""
    return given_w > 0 and given_h > 0


def main():
    avail = (56.0, 200.0)

    print("=" * 74)
    print("为什么「渲染出像素」抓不到「量成 0」的缺陷")
    print("=" * 74)
    print()
    print("场景：StatusRail 的 MeasureOverride 返回 0×0（复刻缺陷）")
    print()

    desired = measure_phase(0.0, 0.0)
    print("  Measure 阶段：子元素报告 DesiredSize = %.0f×%.0f" % desired)
    print()

    for name, arrange in (("Border", arrange_border), ("StackPanel", arrange_stackpanel)):
        given = arrange(desired, avail)
        draws = onrender_draws(*given)
        print("  放进 %-11s → Arrange 给定 %.0f×%.0f → OnRender 会画吗？ %s"
              % (name, given[0], given[1], "**会**" if draws else "不会"))
        print("      %s" % ("（Border 强制敷满，绕过了 DesiredSize）" if name == "Border"
                            else "（StackPanel 尊重 DesiredSize，于是不画）"))
    print()
    print("=" * 74)
    print("结论")
    print("=" * 74)
    print()
    print("  · 渲染用例（Border + RenderTargetBitmap）**测的是 Arrange 后的尺寸**，")
    print("    所以它对「MeasureOverride 返回 0」**不敏感** —— 会假绿。")
    print()
    print("  · 真正能抓到这个缺陷的，是**直接断言 DesiredSize > 0** 的那几条用例。")
    print("    它们问的是 Measure 阶段的答案，而缺陷正好出在 Measure。")
    print()
    print("  · 所以两条断言各管一段，都不能删：")
    print("      DesiredSize > 0     → 防「量成 0」（缺陷本体）")
    print("      渲染出像素          → 防「渲染管线不接受这个控件」（另一类）")
    print()
    print("⚠ 这个脚本本身就是一次「检查器假绿」的复盘：")
    print("   我原以为渲染用例能守住这个缺陷，实测发现不能。")
    print("   **没有做注入验证的断言，不能假定它有判别力。**")
    print("=" * 74)
    return 0


if __name__ == "__main__":
    sys.exit(main())
