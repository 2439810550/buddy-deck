"""
从鲸鱼娘立绘生成应用图标。

## 素材

    D:\\za\\weixinzhushou\\animations\\assets\\idle.png    （512x512，12 张动作帧之一）

## 为什么不能直接用原图

原图是**全身立绘**，人物只占画布中间约 54%。直接缩到 16px：

    16px → 一个小蓝点，五官完全糊掉

因为立绘有几十种颜色、大量渐变和细线，16px 只有 256 个像素，装不下。

## 分层做法（Windows 图标支持每个尺寸用不同的图）

    256 / 128 / 64 / 48   →  头肩立绘，清晰完整
    32 / 24 / 16          →  简化版：裁得更紧 + 模糊抹细节 + 提对比

小尺寸那几张不是「同一张缩下去」，而是**专门处理过**的 —— 把
细节抹平、让色块分明，缩下去才不会变成噪点。

这是专业图标的通用做法（Chrome、VSCode 都这么干）。

## 换表情 / 换裁切

只改下面两个常量就行。

## 跑法

    python tools/make-icon/make_whale_icon.py
"""

from PIL import Image, ImageDraw, ImageFilter, ImageFont
import numpy as np
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ico_pack

# ── 素材来源 ──────────────────────────────────────────────────
WHALE_DIR = r"D:\za\weixinzhushou\animations\assets"
SOURCE_FRAME = "idle.png"          # 可选：idle / happy / wave / proud / surprise …

# 头肩裁切（让脸占主导，但保留鲸鱼发饰和蕾丝边）
CROP_BIG = (85, 10, 425, 300)

# 小尺寸用的更紧的裁切（只有脸 + 发饰）
CROP_SMALL = (115, 25, 395, 235)

# 分层分界：>= 这个尺寸用立绘，< 用简化版
BIG_THRESHOLD = 48

# Windows 需要的全部尺寸
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def crop_square(im, box):
    """按 box 裁切，然后补成正方形（透明填充，人物居中）。"""
    c = im.crop(box)
    w, h = c.size
    side = max(w, h)
    sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    sq.paste(c, ((side - w) // 2, (side - h) // 2), c)
    return sq


def simplify(im, blur=3.0, contrast=1.5, sat=1.3):
    """
    简化处理：模糊抹掉细节 + 提对比让色块分明。

    为什么要先放大到 512 再处理：直接在原尺寸模糊会把轮廓也糊掉。
    先放大、模糊、再缩回去，等于「低通滤波」，细节没了但大形状还在。

    为什么提对比和饱和度：模糊之后颜色会互相渗，整体发灰。
    拉一下对比和饱和度，缩到 16px 时才不会是一团灰。
    """
    work = im.resize((512, 512), Image.LANCZOS)
    if blur:
        work = work.filter(ImageFilter.GaussianBlur(blur))

    arr = np.array(work).astype(np.float32)
    rgb = arr[:, :, :3]

    # 对比度：围绕均值拉伸
    mean = rgb.mean(axis=(0, 1), keepdims=True)
    rgb = np.clip((rgb - mean) * contrast + mean, 0, 255)

    # 饱和度：离灰度越远拉得越开
    gray = rgb.mean(axis=2, keepdims=True)
    rgb = np.clip(gray + (rgb - gray) * sat, 0, 255)

    arr[:, :, :3] = rgb
    return Image.fromarray(arr.astype(np.uint8))


def render(im_big, im_small, size):
    """按目标尺寸选素材。"""
    if size >= BIG_THRESHOLD:
        return im_big.resize((size, size), Image.LANCZOS)
    return im_small.resize((size, size), Image.LANCZOS)


def main():
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    out_dir = os.path.join(root, "Assets")
    os.makedirs(out_dir, exist_ok=True)

    src_path = os.path.join(WHALE_DIR, SOURCE_FRAME)
    if not os.path.exists(src_path):
        print(f"  找不到素材：{src_path}")
        return 1

    src = Image.open(src_path).convert("RGBA")
    print(f"  素材：{src_path}  ({src.width}x{src.height})")

    # ── 两套素材 ──
    big_src = crop_square(src, CROP_BIG)
    small_src = simplify(crop_square(src, CROP_SMALL))

    # ── 预览图：看清每个尺寸长什么样 ──
    preview_path = os.path.join(root, "Assets", "icon-preview.png")
    make_preview(big_src, small_src, preview_path)
    print(f"  ✓ {preview_path}")

    # ── .ico：每个尺寸单独渲染再打包 ──
    #
    # 为什么不用 Pillow 的 save(sizes=[...])：那个参数是拿**同一张图**
    # 缩成各个尺寸，小尺寸那几张就得不到简化处理。
    #
    # 更关键的是：试过 append_images=[...] 想自己提供每一帧，
    # **实测不生效** —— 存出来只有 812 字节、里面只有一档 16x16。
    # 所以用自己的打包器（ico_pack.py）。
    frames = [(s, render(big_src, small_src, s)) for s in SIZES]
    ico_path = os.path.join(out_dir, "app.ico")
    ico_pack.pack_ico(frames, ico_path)

    # 读回来验证每档都在 —— 不能只信写的过程
    entries = ico_pack.verify_ico(ico_path)
    print(f"  ✓ {ico_path}  ({os.path.getsize(ico_path):,} 字节)")
    print("      内含 %d 档：%s" % (
        len(entries),
        " ".join(f"{e['size']}({e['bytes']//1024}K)" for e in entries),
    ))
    bad = [e for e in entries if not e["is_png"]]
    if bad:
        print(f"      ⚠ 有 {len(bad)} 档不是 PNG 数据，可能坏了")

    # ── PNG：安装器、README、托盘备用 ──
    for size in (256, 128, 64, 48, 32, 16):
        p = os.path.join(out_dir, f"app-{size}.png")
        render(big_src, small_src, size).save(p, format="PNG")
    print("  ✓ app-256.png / 128 / 64 / 48 / 32 / 16")

    return 0


def make_preview(big_src, small_src, out_path):
    """
    生成一张「各尺寸实际长什么样」的对比图。

    为什么要生成它：图标是视觉的东西，光看 256 那张说明不了问题 ——
    真正会出丑的是 16px。这张图把每档都摆出来，一眼能看出哪档糊了。
    """
    sizes = [256, 128, 64, 48, 32, 24, 16]
    pad = 16
    label_h = 22
    W = pad + sum(max(s, 40) + pad for s in sizes) + pad
    # 高度：标题(30) + 256 主图行 + 尺寸标签 + 文件列表行(2 行 × 20)
    # 第一版这里算小了，文件列表那行被切掉 —— 预览图被切等于没预览
    H = 30 + 256 + label_h + 16 + 2 * 22 + pad

    canvas = Image.new("RGB", (W, H), (244, 245, 247))

    try:
        font = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 12)
        font_hi = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 14)
    except OSError:
        font = font_hi = ImageFont.load_default()

    draw = ImageDraw.Draw(canvas)
    draw.text((pad, 6), "鲸鱼娘图标 —— 各尺寸实际效果（下面这排才是任务栏里看到的）",
              font=font_hi, fill=(50, 55, 60))

    x = pad
    baseline = 40 + 256
    for s in sizes:
        im = render(big_src, small_src, s)
        y = baseline - s
        canvas.paste(im, (x, y), im)
        draw.text((x, baseline + 4), f"{s}px", font=font, fill=(140, 145, 150))
        x += max(s, 40) + pad

    # 再画一行模拟「文件列表里的真实观感」（白底、不放大）
    list_y = baseline + label_h + 14
    draw.text((pad, list_y), "文件列表里（白底、真实大小）：", font=font, fill=(140, 145, 150))

    row = list_y + 16
    for s, name in ((16, "WorkBuddy 控制台.exe"), (16, "另一个文件.txt")):
        im = render(big_src, small_src, s)
        canvas.paste(im, (pad + 8, row + (16 - s) // 2), im)
        draw.text((pad + 30, row + 1), name, font=font, fill=(31, 35, 40))
        row += 22

    canvas.save(out_path)


if __name__ == "__main__":
    sys.exit(main())
