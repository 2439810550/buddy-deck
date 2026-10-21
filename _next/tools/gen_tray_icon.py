"""生成 src/BuddyDeck.App/Assets/tray.ico —— 纯 Python、零依赖。

为什么自己生成而不是找个图标文件：
  · 仓库里没有现成的 .ico，而托盘图标**必须有**
  · 装 Pillow 只为画一个图标不划算（而且这是别人的构建环境）
  · ICO 格式本身很简单：一个头 + 若干张 BMP/PNG 条目

设计：与 BuddyDeck 的视觉语言一致 —— 深底 + 琥珀（#D2A24C，design 的强调色）。
图形是一个「汉堡式三横」的简化卡组意象 + 右上角一个琥珀点（表示"有内容在跑"）。
刻意做得**几何、可辨**：16px 下细节全糊，所以只保留最强的形状对比。

多尺寸（16/24/32/48）打进一个 .ico —— Windows 会按 DPI 挑合适的那个。
"""

import os
import struct
import zlib

# ---- 调色（与 design 的 Colors.xaml 一致）----
INK = (0x0E, 0x11, 0x14)      # 背景（深）
AMBER = (0xD2, 0xA2, 0x4C)    # 强调（琥珀）
CHALK = (0xE6, 0xED, 0xF3)    # 前景文字色

SIZES = [16, 24, 32, 48]


def render(size):
    """把图标画成 RGBA 像素 —— 手写光栅，不依赖任何图形库。"""
    px = [[(0, 0, 0, 0) for _ in range(size)] for _ in range(size)]
    s = size / 16.0  # 按 16 的逻辑网格缩放

    def put(x, y, color, a=255):
        if 0 <= x < size and 0 <= y < size:
            px[y][x] = (color[0], color[1], color[2], a)

    def rect(x0, y0, x1, y1, color):
        for y in range(int(y0 * s), int(y1 * s)):
            for x in range(int(x0 * s), int(x1 * s)):
                put(x, y, color)

    # 底板：圆角方（用四角切掉来近似）
    rect(1, 1, 15, 15, INK)
    for cx, cy in ((0, 0), (15, 0), (0, 15), (15, 15)):
        for dx in (0, 1):
            for dy in (0, 1):
                x = int((cx + dx) * s)
                y = int((cy + dy) * s)
                if 0 <= x < size and 0 <= y < size:
                    px[y][x] = (0, 0, 0, 0)

    # 三条横杠（卡组 / 列表意象），颜色用冷白
    for i, y0 in enumerate((4.0, 7.0, 10.0)):
        rect(3, y0, 13 if i == 0 else 11, y0 + 1.2, CHALK)

    # 右上角琥珀点 —— 一眼看到"这软件是活的"
    for y in range(int(10.5 * s), int(13.5 * s)):
        for x in range(int(10.5 * s), int(13.5 * s)):
            put(x, y, AMBER)

    return px


def to_png(px, size):
    """RGBA 像素 → PNG 字节（ico 允许内嵌 PNG，Vista+ 支持）。"""
    raw = b""
    for y in range(size):
        raw += b"\x00"  # filter type 0
        for x in range(size):
            r, g, b, a = px[y][x]
            raw += bytes((r, g, b, a))

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out = os.path.join(root, "src", "BuddyDeck.App", "Assets", "tray.ico")
    os.makedirs(os.path.dirname(out), exist_ok=True)

    images = [to_png(render(sz), sz) for sz in SIZES]

    # ICONDIR: reserved=0, type=1, count
    ico = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for sz, data in zip(SIZES, images):
        # 宽高 0 表示 256；这里都 < 256 所以直接写
        ico += struct.pack("<BBBBHHII", sz, sz, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for data in images:
        ico += data

    with open(out, "wb") as f:
        f.write(ico)
    print("wrote", out, len(ico), "bytes")


if __name__ == "__main__":
    main()
