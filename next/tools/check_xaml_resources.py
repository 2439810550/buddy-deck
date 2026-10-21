"""检查 XAML 里 `StaticResource` 引用的 key 是否真的存在。

## 为什么需要这个检查

`StaticResource` 缺失 **编译期是绿的**（XAML 编译不解析资源字典），
运行时才抛 `XamlParseException`，而且**整个窗口起不来**。

这与团队已经归档的两类静默失败同源：
  · `RelativeSource=AncestorType=Window` 找不到 → 绑定不生效（静默）
  · `const` 字段无法绑定 → 绑定不生效（静默）
  · **`StaticResource` 缺失 → 这次是炸，但炸在运行时** ← 本脚本守的

（`DynamicResource` 缺失是另一种表现：不炸，只是那个属性没上色。）

用法：
    python tools/check_xaml_resources.py
退出码 0 = 全部存在；1 = 有缺失（并列出文件:行号）。
"""

import re
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
APP = ROOT / "src" / "BuddyDeck.App"
THEMES = APP / "Themes"

# WPF-UI 自带的、以及 WPF 框架内置的 key —— 不在我们的字典里但合法
EXTERNAL_OK = {
    "FontWeightEmphasis",
    "FontUi",
    "FontNumeric",
}

# 只扫这些目录里的使用点（Themes 自己是被引用的定义处，不查）
SCAN_DIRS = [APP]


def defined_keys():
    """收集所有**可用**的资源 key。

    ⚠ 之前我只扫了 Themes/*.xaml，结果误报 34 条 —— 因为转换器实例
    （BoolToVisibility / InverseBoolToVisibility / StringToVisibility /
    AccountStateBrush …）是**在 App.xaml 里声明**的，不在 Themes 里。

    一个只能扫一半来源的检查器没用：它会一直喊狼来了，然后没人看它。
    所以这里把 App.xaml 也纳入。
    """
    keys = set()

    # Themes/*.xaml —— design 的视觉 token
    for f in THEMES.glob("*.xaml"):
        keys |= set(re.findall(r'x:Key="([^"]+)"', f.read_text(encoding="utf-8")))

    # App.xaml —— 转换器实例（XAML 层唯一必须由界面作者声明的那一段）
    app_xaml = APP / "App.xaml"
    if app_xaml.exists():
        keys |= set(re.findall(r'x:Key="([^"]+)"', app_xaml.read_text(encoding="utf-8")))

    # 页面自己 <UserControl.Resources> 里声明的（页面内局部资源）
    for f in APP.rglob("*.xaml"):
        if THEMES in f.parents:
            continue
        keys |= set(re.findall(r'x:Key="([^"]+)"', f.read_text(encoding="utf-8")))

    return keys


def main():
    defined = defined_keys()

    problems = []
    for f in sorted(APP.rglob("*.xaml")):
        if THEMES in f.parents:
            continue
        text = f.read_text(encoding="utf-8")
        for lineno, line in enumerate(text.splitlines(), 1):
            for key in re.findall(r"StaticResource\s+([A-Za-z][\w]*)", line):
                if key in defined or key in EXTERNAL_OK:
                    continue
                problems.append((f.relative_to(ROOT), lineno, key))

    if not problems:
        print(f"OK: 所有 StaticResource 引用都能在 {len(defined)} 个已定义 key 里找到")
        return 0

    print(f"发现 {len(problems)} 处缺失的 StaticResource key（运行时会让窗口起不来）：")
    for path, lineno, key in problems:
        print(f"  {path}:{lineno}  ->  {key}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
