"""
手写 ICO 打包器 —— 为了「每个尺寸用不同的图」。

## 为什么不能用 Pillow 的 save(sizes=[...])

Pillow 那个参数的实际行为是：拿**同一张图**缩成各个尺寸。
而我们要的是：

    256 / 128 / 64 / 48  →  鲸鱼娘立绘（清晰）
    32 / 24 / 16         →  简化版（模糊 + 提对比，小尺寸才认得出）

试过 `append_images=[...]`，实测**不生效** —— 存出来的 ico 里
只有第一档（812 字节，只有一个 16x16）。所以自己打包。

## ICO 格式（比想象中简单）

    ┌─ ICONDIR (6 字节) ────────────────────────┐
    │  u16 reserved = 0                          │
    │  u16 type     = 1   (1=图标, 2=光标)        │
    │  u16 count    = 有几张图                    │
    ├─ ICONDIRENTRY × count (每张 16 字节) ───────┤
    │  u8  width        (0 表示 256)              │
    │  u8  height       (0 表示 256)              │
    │  u8  colorCount   (0 = 真彩)                │
    │  u8  reserved     = 0                       │
    │  u16 planes       = 1                       │
    │  u16 bitCount     = 32                      │
    │  u32 bytesInRes   = 这张图的数据长度         │
    │  u32 imageOffset  = 数据从文件头算起的偏移    │
    ├─ 数据区 ────────────────────────────────────┤
    │  每张图：PNG 字节流（Vista+ 支持 PNG 压缩）   │
    └────────────────────────────────────────────┘

## 为什么每张用 PNG 而不是 BMP

BMP 格式的 ico 帧要写 AND 掩码和倒序的行数据，麻烦且容易错。
PNG 压缩的帧体积小得多，Vista 以后所有 Windows 都支持。
**唯一注意**：256x256 那一档必须是 PNG（BMP 存不下），其余档
用 PNG 也完全没问题。
"""

import struct
from PIL import Image
import io


def pack_ico(images, out_path):
    """
    把一批 PIL Image 打包成一个 .ico。

    @param images  [(size, Image), ...]  尺寸从大到小或任意顺序都行
    @param out_path  输出路径
    """
    frames = []
    for size, im in images:
        buf = io.BytesIO()
        # 确保是 RGBA（写进 ico 的 PNG 必须带 alpha）
        im.convert("RGBA").save(buf, format="PNG", optimize=True)
        frames.append((size, buf.getvalue()))

    count = len(frames)

    # ── 文件头 ──
    header = struct.pack("<HHH", 0, 1, count)

    # ── 目录表 + 数据 ──
    #
    # 数据区从 header(6) + count*16 之后开始
    data_offset = 6 + count * 16
    directory = b""
    payload = b""

    for size, data in frames:
        # 宽高是 u8；256 要写成 0（这是 ico 的约定）
        w = 0 if size >= 256 else size
        h = 0 if size >= 256 else size

        directory += struct.pack(
            "<BBBBHHII",
            w, h,
            0,        # colorCount：真彩填 0
            0,        # reserved
            1,        # planes
            32,       # bitCount
            len(data),
            data_offset,
        )
        payload += data
        data_offset += len(data)

    with open(out_path, "wb") as f:
        f.write(header)
        f.write(directory)
        f.write(payload)

    return out_path


def verify_ico(path):
    """读回来确认每档都在 —— 写错了要能发现，不能只信写的过程。"""
    with open(path, "rb") as f:
        raw = f.read()

    reserved, kind, count = struct.unpack("<HHH", raw[:6])
    if reserved != 0 or kind != 1:
        raise ValueError(f"ico 头不对：reserved={reserved} kind={kind}")

    entries = []
    for i in range(count):
        off = 6 + i * 16
        w, h, _, _, planes, bits, size, img_off = struct.unpack("<BBBBHHII", raw[off:off + 16])
        entries.append({
            "size": 256 if w == 0 else w,
            "bytes": size,
            "offset": img_off,
            # 抽查这张图的数据确实在那里、且是 PNG
            "is_png": raw[img_off:img_off + 8] == b"\x89PNG\r\n\x1a\n",
        })
    return entries
