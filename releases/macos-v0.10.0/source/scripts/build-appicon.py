#!/usr/bin/env python3
"""build-appicon.py —— RemoteFlow 程序图标（「端点连接流」）统一生成器。

一次生成 Windows 与 macOS 两端的全部图标资源，两端不再各维护一套图形：
  * Windows：src/RemoteFlow.App/Assets/RemoteFlow.ico（16/24/32/48/64/128/256）
             src/RemoteFlow.App/Assets/RemoteFlow-logo.png（256，标题栏 / 侧栏 LOGO）
  * macOS  ：src/RemoteFlow.App.Mac/Resources/AppIcon.iconset/*.png + AppIcon.icns

设计：蓝 → 靛品牌渐变圆角底；左下、右上两个白色设备端点，由一条青蓝流线贯通，
流线末端为向前箭头，表达 Remote + Flow。大尺寸带克制高光、内描边与投影；
16 / 24 / 32px 使用单独简化的字形（去投影与高光、加粗主轮廓），不机械缩放。

用法：
  python scripts/build-appicon.py          生成全部资源
  python scripts/build-appicon.py --check  只校验已生成资源的尺寸 / 透明边缘 / 非空像素

依赖：Pillow（仅开发期生成用，不进入应用运行依赖）。
"""

from __future__ import annotations

import argparse
import math
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
WIN_ASSETS = ROOT / "src" / "RemoteFlow.App" / "Assets"
MAC_RESOURCES = ROOT / "src" / "RemoteFlow.App.Mac" / "Resources"

ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
SMALL_LIMIT = 32  # 不大于该尺寸使用简化字形

# macOS iconset 文件名 → 像素尺寸
ICONSET = {
    "icon_16x16.png": 16,
    "icon_16x16@2x.png": 32,
    "icon_32x32.png": 32,
    "icon_32x32@2x.png": 64,
    "icon_128x128.png": 128,
    "icon_128x128@2x.png": 256,
    "icon_256x256.png": 256,
    "icon_256x256@2x.png": 512,
    "icon_512x512.png": 512,
    "icon_512x512@2x.png": 1024,
}

# 品牌色（基于 #3352CE）
TOP = (92, 140, 255)
MID = (51, 82, 206)
BOTTOM = (33, 49, 160)
FLOW_A = (94, 227, 255)   # 青
FLOW_B = (255, 255, 255)  # 白
SCREEN = (40, 70, 196)


# ── 几何工具 ──────────────────────────────────────────────────────

def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def mix(c1: tuple[int, ...], c2: tuple[int, ...], t: float) -> tuple[int, ...]:
    return tuple(round(lerp(x, y, t)) for x, y in zip(c1, c2))


def bezier(p0, p1, p2, p3, steps: int) -> list[tuple[float, float]]:
    pts = []
    for i in range(steps + 1):
        t = i / steps
        u = 1 - t
        x = u ** 3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t ** 3 * p3[0]
        y = u ** 3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t ** 3 * p3[1]
        pts.append((x, y))
    return pts


def vertical_gradient(size: int, stops: list[tuple[float, tuple[int, int, int]]]) -> Image.Image:
    img = Image.new("RGBA", (1, size))
    for y in range(size):
        t = y / max(1, size - 1)
        for i in range(len(stops) - 1):
            (t0, c0), (t1, c1) = stops[i], stops[i + 1]
            if t0 <= t <= t1:
                img.putpixel((0, y), mix(c0, c1, (t - t0) / max(1e-6, t1 - t0)) + (255,))
                break
    return img.resize((size, size))


def rounded_mask(size: int, box: tuple[float, float, float, float], radius: float) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius=radius, fill=255)
    return mask


def stroke_path(draw: ImageDraw.ImageDraw, pts, width: float, color_at) -> None:
    """沿采样点画圆盘，得到圆头圆角的粗线；color_at(t) 支持沿线渐变。"""
    r = width / 2
    n = len(pts) - 1
    for i, (x, y) in enumerate(pts):
        draw.ellipse((x - r, y - r, x + r, y + r), fill=color_at(i / max(1, n)))


# ── 大尺寸：完整字形 ──────────────────────────────────────────────

def render_full(size: int, crop: int = 0) -> Image.Image:
    """crop：在 1024 设计网格四周裁掉的留白。macOS 保留 Apple 规范留白（0），
    Windows 图标习惯更满幅，裁掉 60 使底座约占画布 91%。"""
    ss = 4
    s = round(size * ss * 1024 / (1024 - 2 * crop))
    k = s / 1024  # 设计网格 1024

    canvas = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    box = (100 * k, 100 * k, 924 * k, 924 * k)
    radius = 185 * k

    # 底座投影
    shadow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        (box[0], box[1] + 14 * k, box[2], box[3] + 14 * k), radius=radius, fill=(10, 21, 80, 90))
    canvas.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(18 * k)))

    # 渐变底座
    face = vertical_gradient(s, [(0.0, TOP), (0.55, MID), (1.0, BOTTOM)])
    mask = rounded_mask(s, box, radius)
    base = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    base.paste(face, (0, 0), mask)

    # 顶部柔光 + 上半高光
    glow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse((150 * k, -160 * k, 874 * k, 520 * k), fill=(160, 195, 255, 70))
    glow = glow.filter(ImageFilter.GaussianBlur(90 * k))
    base.alpha_composite(Image.composite(glow, Image.new("RGBA", (s, s)), mask))

    sheen = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    sd = ImageDraw.Draw(sheen)
    for y in range(int(100 * k), int(520 * k)):
        a = round(46 * (1 - (y - 100 * k) / (420 * k)))
        sd.line((0, y, s, y), fill=(255, 255, 255, max(0, a)))
    base.alpha_composite(Image.composite(sheen, Image.new("RGBA", (s, s)), mask))
    canvas.alpha_composite(base)

    # 内描边
    ImageDraw.Draw(canvas).rounded_rectangle(
        (box[0] + 1 * k, box[1] + 1 * k, box[2] - 1 * k, box[3] - 1 * k),
        radius=radius, outline=(255, 255, 255, 46), width=max(1, round(3 * k)))

    glyph = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glyph)

    # 流线：从左下端点右侧出发，S 形上扬到右上端点左侧
    p0, p1, p2, p3 = (360 * k, 650 * k), (510 * k, 650 * k), (450 * k, 390 * k), (566 * k, 390 * k)
    pts = bezier(p0, p1, p2, p3, 360)
    stroke_path(gd, pts, 64 * k, lambda t: mix(FLOW_A, FLOW_B, t) + (255,))

    # 流动节拍：线上三颗高亮点
    for t in (0.28, 0.5, 0.72):
        x, y = pts[round(t * (len(pts) - 1))]
        r = 15 * k
        gd.ellipse((x - r, y - r, x + r, y + r), fill=(22, 60, 190, 255))

    # 端点设备：白色圆角机身 + 蓝色屏幕
    def endpoint(cx: float, cy: float) -> None:
        w, h = 210 * k, 170 * k
        gd.rounded_rectangle((cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2), radius=38 * k, fill=(255, 255, 255, 255))
        m = 30 * k
        gd.rounded_rectangle((cx - w / 2 + m, cy - h / 2 + m, cx + w / 2 - m, cy + h / 2 - m - 18 * k),
                             radius=16 * k, fill=SCREEN + (255,))
        gd.rounded_rectangle((cx - 36 * k, cy + h / 2 - 34 * k, cx + 36 * k, cy + h / 2 - 22 * k),
                             radius=6 * k, fill=SCREEN + (255,))

    endpoint(285 * k, 650 * k)
    endpoint(739 * k, 390 * k)

    # 流线在右上端点前的向前箭头
    ax, ay = 616 * k, 390 * k
    gd.polygon([(ax - 72 * k, ay - 76 * k), (ax, ay), (ax - 72 * k, ay + 76 * k)], fill=(255, 255, 255, 255))

    # 字形投影
    shadow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    shadow.putalpha(glyph.getchannel("A").point(lambda a: a * 0.30))
    shadow = ImageChops.offset(shadow, 0, round(10 * k)).filter(ImageFilter.GaussianBlur(14 * k))
    tint = Image.new("RGBA", (s, s), (10, 21, 80, 0))
    tint.putalpha(shadow.getchannel("A"))
    canvas.alpha_composite(Image.composite(tint, Image.new("RGBA", (s, s)), mask))
    canvas.alpha_composite(glyph)

    if crop:
        c = round(crop * k)
        canvas = canvas.crop((c, c, s - c, s - c))
    return canvas.resize((size, size), Image.LANCZOS)


# ── 小尺寸：简化字形 ──────────────────────────────────────────────

def render_small(size: int) -> Image.Image:
    """16/24/32px：满幅底座、无投影高光，两个实心端点 + 一条粗对角流线。"""
    ss = 8
    s = size * ss
    k = s / 32  # 设计网格 32

    canvas = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    box = (1 * k, 1 * k, 31 * k, 31 * k)
    face = vertical_gradient(s, [(0.0, TOP), (1.0, BOTTOM)])
    canvas.paste(face, (0, 0), rounded_mask(s, box, 7 * k))

    d = ImageDraw.Draw(canvas)
    pts = bezier((10 * k, 22 * k), (16 * k, 22 * k), (16 * k, 10 * k), (22 * k, 10 * k), 120)
    stroke_path(d, pts, 3.4 * k, lambda t: mix(FLOW_A, FLOW_B, t) + (255,))

    def endpoint(cx: float, cy: float) -> None:
        d.rounded_rectangle((cx - 5 * k, cy - 4 * k, cx + 5 * k, cy + 4 * k), radius=1.6 * k, fill=(255, 255, 255, 255))
        d.rectangle((cx - 3 * k, cy - 2 * k, cx + 3 * k, cy + 1.2 * k), fill=SCREEN + (255,))

    endpoint(8 * k, 22 * k)
    endpoint(24 * k, 10 * k)

    return canvas.resize((size, size), Image.LANCZOS)


WIN_CROP = 60


def render(size: int, crop: int = 0) -> Image.Image:
    return render_small(size) if size <= SMALL_LIMIT else render_full(size, crop)


# ── 输出与校验 ───────────────────────────────────────────────────

def build() -> None:
    frames = {size: render(size) for size in sorted(set(ICONSET.values()))}
    win = {size: render(size, WIN_CROP) for size in ICO_SIZES}

    WIN_ASSETS.mkdir(parents=True, exist_ok=True)
    ico = WIN_ASSETS / "RemoteFlow.ico"
    win[256].save(ico, format="ICO", sizes=[(n, n) for n in ICO_SIZES],
                  append_images=[win[n] for n in ICO_SIZES if n != 256])
    win[256].save(WIN_ASSETS / "RemoteFlow-logo.png")

    iconset = MAC_RESOURCES / "AppIcon.iconset"
    iconset.mkdir(parents=True, exist_ok=True)
    for name, n in ICONSET.items():
        frames[n].save(iconset / name)
    frames[1024].save(MAC_RESOURCES / "AppIcon.icns", format="ICNS",
                      append_images=[frames[n] for n in (16, 32, 64, 128, 256, 512)])

    print(f"已生成：{ico.relative_to(ROOT)}、RemoteFlow-logo.png、AppIcon.iconset（{len(ICONSET)} 张）、AppIcon.icns")


def check() -> int:
    errors: list[str] = []

    def verify(img: Image.Image, label: str, expect: int) -> None:
        img = img.convert("RGBA")
        if img.size != (expect, expect):
            errors.append(f"{label}: 尺寸 {img.size} ≠ {expect}")
            return
        alpha = img.getchannel("A")
        if alpha.getpixel((0, 0)) > 16:
            errors.append(f"{label}: 左上角不透明，圆角透明边缘缺失")
        if alpha.getbbox() is None or sum(alpha.histogram()[201:]) < expect * expect * 0.5:
            errors.append(f"{label}: 可见像素不足")

    ico = Image.open(WIN_ASSETS / "RemoteFlow.ico")
    sizes = sorted(w for w, _ in ico.info.get("sizes", set()))
    if sizes != ICO_SIZES:
        errors.append(f"RemoteFlow.ico 帧尺寸 {sizes} ≠ {ICO_SIZES}")
    for n in ICO_SIZES:
        ico.size = (n, n)
        verify(ico, f"RemoteFlow.ico@{n}", n)

    verify(Image.open(WIN_ASSETS / "RemoteFlow-logo.png"), "RemoteFlow-logo.png", 256)
    for name, n in ICONSET.items():
        verify(Image.open(MAC_RESOURCES / "AppIcon.iconset" / name), name, n)

    icns = Image.open(MAC_RESOURCES / "AppIcon.icns")
    icns.load()
    if max(icns.size) < 512:
        errors.append(f"AppIcon.icns 最大帧 {icns.size} 过小")

    for e in errors:
        print("✗", e)
    print("校验通过" if not errors else f"校验失败：{len(errors)} 项")
    return 1 if errors else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true", help="只校验已生成的资源")
    args = parser.parse_args()
    if args.check:
        return check()
    build()
    return check()


if __name__ == "__main__":
    sys.exit(main())
