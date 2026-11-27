#!/usr/bin/env python3
"""build-appicon.py —— RemoteFlow 程序图标（「立体双箭头」）统一生成器。

一次生成 Windows 与 macOS 两端的全部图标资源，两端共用同一套图形：
  * Windows：src/RemoteFlow.App/Assets/RemoteFlow.ico（16/24/32/48/64/128/256）
             src/RemoteFlow.App/Assets/RemoteFlow-logo.png（256，关于页等大尺寸 LOGO）
             src/RemoteFlow.App/Assets/RemoteFlow-logo-small.png（64，标题栏 / 侧栏 / 设置页小 LOGO，简化字形）
  * macOS  ：src/RemoteFlow.App.Mac/Resources/AppIcon.iconset/*.png + AppIcon.icns

设计源是矢量文件 scripts/appicon/AppIcon.xaml（大尺寸：带厚度的蓝 → 靛底座，白色挤出前箭头 +
青蓝玻璃后箭头，表达「连上、流动」）与 AppIcon-small.xaml（16 / 24 / 32 px 的加粗简化字形）。
各尺寸由 scripts/render-appicon.ps1 用 WPF 直接从矢量渲染（每个尺寸单独渲染，不是缩放），
本脚本负责打包与校验。

用法（仅 Windows：渲染依赖 WPF）：
  python scripts/build-appicon.py          生成全部资源并自校验
  python scripts/build-appicon.py --check  只校验已生成资源的尺寸 / 透明边缘 / 非空像素

依赖：Python 3 + Pillow、PowerShell 7（pwsh）。均只在开发期使用，不进入应用运行依赖。
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
WIN_ASSETS = ROOT / "src" / "RemoteFlow.App" / "Assets"
MAC_RESOURCES = ROOT / "src" / "RemoteFlow.App.Mac" / "Resources"
RENDER_SCRIPT = ROOT / "scripts" / "render-appicon.ps1"

ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

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


def render_frames(out: Path) -> None:
    pwsh = shutil.which("pwsh")
    if pwsh is None:
        sys.exit("需要 PowerShell 7（pwsh）来渲染矢量图标。")
    subprocess.run([pwsh, "-NoProfile", "-STA", "-File", str(RENDER_SCRIPT), "-OutDir", str(out)], check=True)


def build() -> None:
    with tempfile.TemporaryDirectory() as tmp:
        frames = Path(tmp)
        render_frames(frames)
        mac = {n: Image.open(frames / f"mac-{n}.png").convert("RGBA") for n in sorted(set(ICONSET.values()))}
        win = {n: Image.open(frames / f"win-{n}.png").convert("RGBA") for n in ICO_SIZES}

        WIN_ASSETS.mkdir(parents=True, exist_ok=True)
        ico = WIN_ASSETS / "RemoteFlow.ico"
        # 每个尺寸用各自渲染的帧（小尺寸是简化字形），不要让 Pillow 从 256 缩放
        win[256].save(ico, format="ICO", sizes=[(n, n) for n in ICO_SIZES],
                      append_images=[win[n] for n in ICO_SIZES if n != 256])
        win[256].save(WIN_ASSETS / "RemoteFlow-logo.png")
        Image.open(frames / "logo-small-64.png").convert("RGBA").save(WIN_ASSETS / "RemoteFlow-logo-small.png")

        iconset = MAC_RESOURCES / "AppIcon.iconset"
        iconset.mkdir(parents=True, exist_ok=True)
        for name, n in ICONSET.items():
            mac[n].save(iconset / name)
        mac[1024].save(MAC_RESOURCES / "AppIcon.icns", format="ICNS",
                       append_images=[mac[n] for n in (16, 32, 64, 128, 256, 512)])

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
    verify(Image.open(WIN_ASSETS / "RemoteFlow-logo-small.png"), "RemoteFlow-logo-small.png", 64)
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
