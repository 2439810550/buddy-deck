"""检查 BuddyDeck.App 窗口的 Win32 样式 —— 判断原生标题栏是否真的存在。

## 为什么需要它

`--probe` 截图用 `RenderTargetBitmap` 渲染**客户区**，而 Windows 原生标题栏
（最小化/最大化/关闭）是**非客户区**，由系统画在窗口外面。
所以**截图里永远看不到标题栏** —— 用它验证"标题栏回来了"是做不到的。

必须直接查窗口样式位：
    WS_CAPTION      0x00C00000   ← 有标题栏（含 WS_BORDER | WS_DLGFRAME）
    WS_THICKFRAME   0x00040000   ← 可调整大小（有边框）
    WS_MINIMIZEBOX  0x00020000   ← 有最小化按钮
    WS_MAXIMIZEBOX  0x00010000   ← 有最大化按钮
    WS_SYSMENU      0x00080000   ← 有系统菜单（含关闭）

WPF 设 `ExtendsContentIntoTitleBar=true` 时会把这些位去掉（或改用
WS_POPUP 风格），所以「这几位还在不在」就是标题栏在不在的硬证据。

用法：
    python tools/check_window_chrome.py <进程名或 PID>
"""

import ctypes
import ctypes.wintypes as wt
import sys

# Windows 控制台默认 GBK，输出 ✅/❌ 会 UnicodeEncodeError。
# 显式切 stdout 到 UTF-8 —— 与团队那条「不要用 Get-Content 判断编码」的教训同源：
# **工具用法不能想当然**。
try:
    sys.stdout.reconfigure(encoding="utf-8")
except (AttributeError, OSError):
    pass

user32 = ctypes.windll.user32

WS_CAPTION = 0x00C00000
WS_THICKFRAME = 0x00040000
WS_MINIMIZEBOX = 0x00020000
WS_MAXIMIZEBOX = 0x00010000
WS_SYSMENU = 0x00080000

CHECKS = [
    ("标题栏 WS_CAPTION", WS_CAPTION),
    ("可缩放 WS_THICKFRAME", WS_THICKFRAME),
    ("最小化按钮 WS_MINIMIZEBOX", WS_MINIMIZEBOX),
    ("最大化按钮 WS_MAXIMIZEBOX", WS_MAXIMIZEBOX),
    ("系统菜单 WS_SYSMENU", WS_SYSMENU),
]


def enum_windows():
    """列出所有顶层窗口 (hwnd, title, pid)。"""
    result = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        n = user32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, buf, n + 1)
        result.append((hwnd, buf.value, pid.value))
        return True

    user32.EnumWindows(cb, 0)
    return result


def main():
    target = sys.argv[1] if len(sys.argv) > 1 else "BuddyDeck.App.exe"
    # 按可执行名找 pid
    import subprocess
    ps = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         f"(Get-Process -Name '{target.replace('.exe','')}' -ErrorAction SilentlyContinue).Id -join ','"],
        capture_output=True, text=True)
    pids = {int(x) for x in ps.stdout.strip().split(",") if x.strip().isdigit()}

    if not pids:
        print(f"没找到进程 {target}（先把它跑起来）")
        return 2

    print(f"目标进程 PID = {sorted(pids)}")
    found = False
    checked = 0
    for hwnd, title, pid in enum_windows():
        if pid not in pids:
            continue

        style = user32.GetWindowLongW(hwnd, -16)  # GWL_STYLE
        visible = bool(user32.IsWindowVisible(hwnd))

        # ⚠ 过滤条件必须**同时**要求「可见」和「有标题」，否则会误报。
        #
        # 旧版只查 `WS_CAPTION or WS_THICKFRAME` —— 而一个 WPF 进程有 16 个顶层
        # 窗口，其中 3 个**隐藏的内部窗口**（GDI+ / MediaContext 之类）也带
        # WS_CAPTION，于是它们被当成主窗口报了，结论是"缺最小化/最大化"。
        # **那是个假警报**：真正的主窗口按钮是齐的。
        #
        # ⇒ 判据改成：可见 + 标题非空。
        if not visible or not title:
            continue

        checked += 1
        found = True

        r = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        cr = wt.RECT()
        user32.GetClientRect(hwnd, ctypes.byref(cr))
        win_h = r.bottom - r.top
        nc_height = win_h - (cr.bottom - cr.top)

        print(f"\n窗口 hwnd=0x{hwnd:X} title='{title}' style=0x{style & 0xFFFFFFFF:08X}")
        print(f"  位置 L={r.left} T={r.top}  窗口 {r.right-r.left}x{win_h}  "
              f"客户区 {cr.right}x{cr.bottom}  非客户区 {nc_height}px")

        # ⚠ 最小化坐标检测：Windows 把最小化的窗口放在 (-32000, -32000) 一带。
        #   **这时看不到标题栏是正常的（窗口在任务栏里），不是按钮缺失。**
        #   这一条不加的话，会把"窗口被最小化了"误报成"没有标题栏按钮"。
        minimized = r.left <= -30000 and r.top <= -30000
        if minimized:
            print("  ⚠ 窗口处于**最小化**状态（坐标 -32000 一带）——")
            print("     此时看不到标题栏是正常的；还原：python tools/check_window_chrome.py --restore")

        ok = True
        for name, bit in CHECKS:
            has = bool(style & bit)
            print(f"  {'✅' if has else '❌'} {name}")
            ok = ok and has

        # ⚠ **非客户区高度才是"标题栏真的画出来了"的硬判据**。
        #
        # 光看样式位不够：设了 `ExtendsContentIntoTitleBar=true` 时，
        # 窗口**仍保留** WS_CAPTION / WS_MINIMIZEBOX / WS_MAXIMIZEBOX，
        # 但非客户区被压成 0 —— 按钮根本没地方画。
        # 实测过：样式位全绿，而非客户区 0px，用户就是看不到按钮。
        if nc_height <= 0 and not minimized:
            print("  ❌ 非客户区高度 = 0 ⇒ **标题栏被吃掉了**")
            print("     （样式位可能仍是齐的 —— 这正是只看样式位会误判的地方）")
            print("     查 MainWindow 是否设了 ExtendsContentIntoTitleBar = true")
            ok = False
        elif nc_height > 0:
            print(f"  ✅ 非客户区 {nc_height}px ⇒ 标题栏真的画出来了")

        print(f"\n结论：{'原生窗口边框/标题栏齐全 ✅' if ok else '有缺失 ❌'}")

    if not found:
        print("没找到「可见且有标题」的顶层窗口 —— 窗口可能没显示出来，或被最小化后隐藏")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
