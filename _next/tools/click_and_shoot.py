#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
点击 BuddyDeck 窗口左侧导航的某一项，并把窗口内容抓成 PNG。

## 为什么需要它（而不是 --probe）

`--probe` 是"另起一个进程 → 切页 → 立即截图"。
而账号页有三块**异步拉取**的数据（账号/积分/模型矩阵），
其中积分要逐个账号向上游查 —— **慢**。

探针截到的是中间态，看起来像"功能没做"。
这个脚本对**用户正在看的那个真实窗口**操作：
点导航 → 等 → 抓图，拍到的是**用户实际看到的画面**。

用法：
  python tools/click_and_shoot.py --nav 账号 --wait 30 --out shot.png
"""

import argparse
import ctypes
import ctypes.wintypes as wt
import sys
import time

u = ctypes.windll.user32

# 左侧导航各项相对窗口左上角的 y 偏移（实测自 ui-accounts.png）
NAV_Y = {
    "总览": 103,
    "账号": 222,
    "接入池": 270,
    "聊天测试": 319,
    "用量统计": 367,
    "日志": 416,
    "设置": 465,
}
NAV_X = 55   # 侧栏中心

PW_RENDERFULLCONTENT = 0x00000002


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def find_window(title="BuddyDeck"):
    """找可见的、标题匹配的顶层窗口。返回 (hwnd, rect) 或 None。"""
    result = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        if not u.IsWindowVisible(hwnd):
            return True
        n = u.GetWindowTextLengthW(hwnd)
        if n == 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 1)
        u.GetWindowTextW(hwnd, buf, n + 1)
        if title in buf.value:
            r = RECT()
            u.GetWindowRect(hwnd, ctypes.byref(r))
            result.append((hwnd, r))
            return False
        return True

    u.EnumWindows(cb, 0)
    return result[0] if result else None


def click(hwnd, x, y):
    u.SetForegroundWindow(hwnd)
    time.sleep(0.4)
    u.SetCursorPos(x, y)
    time.sleep(0.15)
    u.mouse_event(0x0002, 0, 0, 0, 0)   # LEFTDOWN
    time.sleep(0.06)
    u.mouse_event(0x0004, 0, 0, 0, 0)   # LEFTUP


def grab(hwnd, rect, path):
    """
    用 PrintWindow 抓窗口内容。

    ⚠ 用 GDI 的 BitBlt 从窗口 DC 抓（而不是 PrintWindow）：
      PrintWindow(2) 对 WPF 窗口常抓到**黑图或残缺**（WPF 走 DirectX 合成，
      不画到窗口 DC）。BitBlt 从屏幕 DC 抓是唯一可靠的方式 ——
      但它要求窗口**没有被遮挡**。
    """
    w = rect.right - rect.left
    h = rect.bottom - rect.top

    hdc_screen = u.GetDC(0)
    hdc_mem = ctypes.windll.gdi32.CreateCompatibleDC(hdc_screen)
    hbm = ctypes.windll.gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    ctypes.windll.gdi32.SelectObject(hdc_mem, hbm)

    # SRCCOPY = 0x00CC0020
    ctypes.windll.gdi32.BitBlt(hdc_mem, 0, 0, w, h,
                               hdc_screen, rect.left, rect.top, 0x00CC0020)

    # 把 HBITMAP 写进 BMP 文件（手写文件头，避免依赖 PIL）
    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", ctypes.c_long),
                    ("biHeight", ctypes.c_long), ("biPlanes", wt.WORD),
                    ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                    ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                    ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", wt.DWORD),
                    ("biClrImportant", wt.DWORD)]

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = -h          # 负数 = 自上而下
    bi.biPlanes = 1
    bi.biBitCount = 24
    bi.biCompression = 0

    row_size = ((w * 3 + 3) // 4) * 4
    img_size = row_size * h

    buf = ctypes.create_string_buffer(img_size)
    ctypes.windll.gdi32.GetDIBits(hdc_mem, hbm, 0, h, buf,
                                  ctypes.byref(bi), 0)

    # BMP 文件头
    file_header = bytearray(b"BM")
    file_header += (14 + 40 + img_size).to_bytes(4, "little")
    file_header += (0).to_bytes(4, "little")
    file_header += (54).to_bytes(4, "little")

    hdr = bytearray()
    hdr += (40).to_bytes(4, "little")
    hdr += w.to_bytes(4, "little", signed=True)
    hdr += h.to_bytes(4, "little", signed=True)
    hdr += (1).to_bytes(2, "little")
    hdr += (24).to_bytes(2, "little")
    hdr += (0).to_bytes(4, "little")
    hdr += img_size.to_bytes(4, "little")
    hdr += (0).to_bytes(4, "little") * 4

    bmp_path = path.replace(".png", ".bmp")
    with open(bmp_path, "wb") as f:
        f.write(bytes(file_header) + bytes(hdr) + buf.raw)

    ctypes.windll.gdi32.DeleteObject(hbm)
    ctypes.windll.gdi32.DeleteDC(hdc_mem)
    u.ReleaseDC(0, hdc_screen)

    return bmp_path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--nav", default="账号", help="要点的左侧导航项名")
    ap.add_argument("--wait", type=float, default=30, help="点完后等几秒再抓")
    ap.add_argument("--out", default="shot.bmp")
    ap.add_argument("--title", default="BuddyDeck")
    args = ap.parse_args()

    found = find_window(args.title)
    if not found:
        print(f"没找到可见窗口 {args.title!r}（先用非 probe 模式把界面跑起来）")
        return 1

    hwnd, rect = found
    print(f"窗口 hwnd={hex(hwnd)} pos=({rect.left},{rect.top}) "
          f"size={rect.right-rect.left}x{rect.bottom-rect.top}")

    y = NAV_Y.get(args.nav)
    if y is None:
        print(f"未知导航项 {args.nav!r}，可选：{list(NAV_Y)}")
        return 1

    x = rect.left + NAV_X
    cy = rect.top + y
    print(f"点击「{args.nav}」于 ({x},{cy})")
    click(hwnd, x, cy)

    print(f"等 {args.wait} 秒让异步数据落定...")
    time.sleep(args.wait)

    bmp = grab(hwnd, rect, args.out)
    print(f"已抓取 -> {bmp}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
