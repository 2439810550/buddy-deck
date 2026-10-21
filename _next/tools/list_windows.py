"""列出某个进程的所有顶层窗口及其**窗口类名**。

## 为什么需要它

`NotifyIcon` 没有可见窗口 —— 它靠一个**隐藏的消息窗口**接收托盘事件。
判断"托盘图标真的建起来了"没法看窗口标题，要看：

    WinForms 的 NotifyIcon 会创建窗口类为 `WindowsForms10.Window.8.app.0.*`
    的隐藏窗口（不同 .NET 版本后缀不同），这就是它存在的证据。

比"点一下托盘菜单看有没有反应"更可靠 —— 后者要人来点，前者可重跑。

用法：
    python tools/list_windows.py <进程名不含.exe>
"""

import ctypes
import ctypes.wintypes as wt
import subprocess
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8")
except (AttributeError, OSError):
    pass

user32 = ctypes.windll.user32


def pids_of(name):
    ps = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         f"(Get-Process -Name '{name}' -ErrorAction SilentlyContinue).Id -join ','"],
        capture_output=True, text=True)
    return {int(x) for x in ps.stdout.strip().split(",") if x.strip().isdigit()}


def main():
    name = (sys.argv[1] if len(sys.argv) > 1 else "BuddyDeck.App").replace(".exe", "")
    pids = pids_of(name)
    if not pids:
        print(f"没找到进程 {name}")
        return 2

    print(f"进程 {name} PID = {sorted(pids)}")
    rows = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value not in pids:
            return True

        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls, 256)

        n = user32.GetWindowTextLengthW(hwnd)
        title = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, title, n + 1)

        visible = bool(user32.IsWindowVisible(hwnd))
        rows.append((hwnd, cls.value, title.value, visible))
        return True

    user32.EnumWindows(cb, 0)

    if not rows:
        print("该进程没有任何顶层窗口 —— 窗口可能还没建或已经退出")
        return 1

    has_winforms = False
    print()
    for hwnd, cls, title, visible in rows:
        mark = "可见" if visible else "隐藏"
        print(f"  [{mark}] 0x{hwnd:X}  class={cls!r}  title={title!r}")
        if cls.startswith("WindowsForms10."):
            has_winforms = True

    print()
    if has_winforms:
        print("✅ 发现 WindowsForms 窗口类 —— NotifyIcon 的消息窗口在，托盘图标已创建")
        return 0
    print("⚠ 未发现 WindowsForms 窗口类 —— 可能托盘没建（或进程还没起完）")
    return 1


if __name__ == "__main__":
    sys.exit(main())
