#!/usr/bin/env python3
"""build-vector-icons-mac.py —— 把 Windows 版的彩色矢量图标转成 macOS 资源。

源（两批，同一种画法）：
  src/RemoteFlow.App/Themes/IdentityIcons.xaml —— 25 个 `Id.*`（凭据类型 / 设置分区 / 首页区块）
  src/RemoteFlow.App/Themes/DeviceIcons.xaml   —— 12 个 `DeviceIcon.*` + 3 个 `ResourceIcon.*`
    （设备类型 + 连接资源智能视图「所有设备/收藏/最近连接」）
  都是原创矢量：渐变主体 + RectangleGeometry / EllipseGeometry / 内联路径几何。
  WPF 的路径小语言与 SVG 路径命令同源（M/L/C/A/Z...），只需去掉 WPF 专属的
  `F1 `（非零环绕）前缀、把渐变的 0-1 相对坐标搬进 SVG 的 objectBoundingBox 渐变。
  不是重画，是坐标 / 语法的机械转换。

产出（每批一个输出目录）：
  <OutDir>/svg/<Key>.svg   —— 转换后的矢量源（可读可再编辑）
  <OutDir>/<Key>.png        —— 256×256 光栅（打进 App Bundle）

用法：
  python3 scripts/build-vector-icons-mac.py          # 生成两批
  python3 scripts/build-vector-icons-mac.py --check  # 只校验 PNG 是否覆盖全部键，不重新生成
依赖：本机 rsvg-convert（brew install librsvg）。
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CANVAS = 64
PNG_SIZE = 256

NS = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
XNS = "http://schemas.microsoft.com/winfx/2006/xaml"


@dataclass
class Batch:
    """一批图标：一个源 XAML + 一个输出目录。"""

    src: Path
    out_dir: Path
    label: str


BATCHES = [
    Batch(
        src=ROOT / "src" / "RemoteFlow.App" / "Themes" / "IdentityIcons.xaml",
        out_dir=ROOT / "src" / "RemoteFlow.App.Mac" / "Resources" / "IdentityIcons",
        label="身份图标",
    ),
    Batch(
        src=ROOT / "src" / "RemoteFlow.App" / "Themes" / "DeviceIcons.xaml",
        out_dir=ROOT / "src" / "RemoteFlow.App.Mac" / "Resources" / "DeviceIcons",
        label="设备 / 资源图标",
    ),
]


def tag(name: str) -> str:
    return f"{{{NS}}}{name}"


def parse_color(value: str) -> tuple[str, float]:
    """WPF #AARRGGBB / #RRGGBB → (SVG #RRGGBB, opacity 0-1)。"""
    h = value.lstrip("#")
    if len(h) == 8:
        a, r, g, b = h[0:2], h[2:4], h[4:6], h[6:8]
        return f"#{r}{g}{b}", int(a, 16) / 255
    if len(h) == 6:
        return f"#{h}", 1.0
    raise ValueError(f"无法识别的颜色：{value}")


def clean_path(d: str) -> str:
    """去掉 WPF 路径小语言里的 `F1 ` 非零环绕前缀，其余与 SVG path data 语法通用。"""
    d = d.strip()
    if d.startswith("F1 "):
        d = d[3:]
    return d


@dataclass
class Brushes:
    """收集顶层具名 LinearGradientBrush（Id.Blue / DeviceIcon.Blue ... 调色板），
    供图标按需引用；也接受内联（无 x:Key，直接写在 GeometryDrawing.Brush 里）的匿名渐变，
    按出现顺序生成一个稳定 id。"""

    defs: dict[str, ET.Element] = field(default_factory=dict)
    _anon_seq: int = 0

    @classmethod
    def collect(cls, root: ET.Element) -> "Brushes":
        b = cls()
        for el in root.findall(tag("LinearGradientBrush")):
            key = el.get(f"{{{XNS}}}Key")
            if key:
                b.defs[key] = el
        return b

    def register_anonymous(self, el: ET.Element, icon_name: str) -> str:
        self._anon_seq += 1
        key = f"anon.{icon_name}.{self._anon_seq}"
        self.defs[key] = el
        return key

    def svg_def(self, key: str) -> str:
        el = self.defs[key]
        start = el.get("StartPoint", "0,0").split(",")
        end = el.get("EndPoint", "1,1").split(",")
        stops = []
        for stop in el.findall(tag("GradientStop")):
            color, opacity = parse_color(stop.get("Color", "#000000"))
            offset = stop.get("Offset", "0")
            op_attr = f' stop-opacity="{opacity:.3f}"' if opacity < 1 else ""
            stops.append(f'    <stop offset="{offset}" stop-color="{color}"{op_attr} />')
        gid = key.replace(".", "-")
        return (
            f'  <linearGradient id="{gid}" x1="{start[0]}" y1="{start[1]}" '
            f'x2="{end[0]}" y2="{end[1]}">\n' + "\n".join(stops) + "\n  </linearGradient>"
        )


def brush_to_fill(brush: str | None, brushes: Brushes, used: set[str]) -> tuple[str, str]:
    """返回 (fill 属性值, fill-opacity 属性值或空串)。"""
    if not brush or brush == "{x:Null}":
        return "none", ""
    m = re.match(r"\{StaticResource ([A-Za-z]+\.[A-Za-z0-9]+)\}", brush)
    if m:
        key = m.group(1)
        used.add(key)
        return f"url(#{key.replace('.', '-')})", ""
    color, opacity = parse_color(brush)
    op_attr = f' fill-opacity="{opacity:.3f}"' if opacity < 1 else ""
    return color, op_attr


def shape_tag(el: ET.Element, extra: str = "") -> str:
    """单个 RectangleGeometry / EllipseGeometry → SVG 形状标签；extra 附加在属性末尾（如 fill）。"""
    if el.tag == tag("RectangleGeometry"):
        x, y, w, h = el.get("Rect", "0,0,0,0").split(",")
        rx = el.get("RadiusX", "0")
        ry = el.get("RadiusY", "0")
        return f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{rx}" ry="{ry}"{extra} />'
    if el.tag == tag("EllipseGeometry"):
        cx, cy = el.get("Center", "0,0").split(",")
        rx = el.get("RadiusX", "0")
        ry = el.get("RadiusY", "0")
        return f'<ellipse cx="{cx}" cy="{cy}" rx="{rx}" ry="{ry}"{extra} />'
    raise ValueError(f"未覆盖的几何元素：{el.tag}")


def geometry_drawing_to_svg(
    gd: ET.Element, brushes: Brushes, used: set[str], icon_name: str
) -> str:
    brush_attr = gd.get("Brush")
    geometry_attr = gd.get("Geometry")

    # 几何：内联 Geometry="..." 路径，或嵌套的 <GeometryDrawing.Geometry>
    # （单个 Rectangle/Ellipse，或 GeometryGroup 包多个同色形状的并集）。
    shapes: list[ET.Element] = []
    nested = gd.find(tag("GeometryDrawing.Geometry"))
    if nested is not None:
        group = nested.find(tag("GeometryGroup"))
        if group is not None:
            shapes = list(group)
        else:
            child = nested.find(tag("RectangleGeometry"))
            if child is None:
                child = nested.find(tag("EllipseGeometry"))
            if child is not None:
                shapes = [child]

    # 描边（可选）：<GeometryDrawing.Pen><Pen .../></GeometryDrawing.Pen>。
    stroke_attrs = ""
    pen_el = gd.find(tag("GeometryDrawing.Pen"))
    if pen_el is not None:
        pen = pen_el.find(tag("Pen"))
        if pen is not None:
            stroke, stroke_op_attr = brush_to_fill(pen.get("Brush", "#000000"), brushes, used)
            stroke_op_attr = stroke_op_attr.replace("fill-opacity", "stroke-opacity")
            cap = pen.get("StartLineCap", "Flat").lower()
            stroke_attrs = (
                f' stroke="{stroke}"{stroke_op_attr} stroke-width="{pen.get("Thickness", "1")}" '
                f'stroke-linecap="{cap}" stroke-linejoin="round"'
            )

    # 填充：属性形式 Brush="..."，或 <GeometryDrawing.Brush> 子元素内联定义（无 x:Key 的渐变）。
    brush_el = gd.find(tag("GeometryDrawing.Brush"))
    if brush_el is not None:
        inline_gradient = brush_el.find(tag("LinearGradientBrush"))
        if inline_gradient is not None:
            key = brushes.register_anonymous(inline_gradient, icon_name)
            used.add(key)
            fill, fill_op = f"url(#{key.replace('.', '-')})", ""
        else:
            fill, fill_op = "none", ""
    else:
        fill, fill_op = brush_to_fill(brush_attr, brushes, used)

    if stroke_attrs and not brush_attr and brush_el is None:
        fill = "none"
        fill_op = ""

    if shapes:
        fill_attr = f' fill="{fill}"{fill_op}'
        if len(shapes) == 1:
            return f"  {shape_tag(shapes[0], fill_attr)}"
        tags = "".join(shape_tag(s) for s in shapes)
        return f'  <g fill="{fill}"{fill_op}>{tags}</g>'
    if geometry_attr:
        d = clean_path(geometry_attr)
        return f'  <path d="{d}" fill="{fill}"{fill_op}{stroke_attrs} fill-rule="nonzero" />'

    raise ValueError("GeometryDrawing 既无 Rect/Ellipse 也无 Geometry，未覆盖的形状")


def convert_icon(drawing_image: ET.Element, brushes: Brushes, icon_name: str) -> str:
    group = drawing_image.find(f"{tag('DrawingImage.Drawing')}/{tag('DrawingGroup')}")
    if group is None:
        raise ValueError("DrawingImage 内没有 DrawingGroup")

    used: set[str] = set()
    body_lines = [
        geometry_drawing_to_svg(gd, brushes, used, icon_name)
        for gd in group.findall(tag("GeometryDrawing"))
    ]

    defs = "\n".join(brushes.svg_def(k) for k in used)
    defs_block = f"<defs>\n{defs}\n</defs>\n" if defs else ""

    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {CANVAS} {CANVAS}" '
        f'width="{CANVAS}" height="{CANVAS}">\n'
        f"{defs_block}"
        + "\n".join(body_lines)
        + "\n</svg>\n"
    )


def load_icons(src: Path) -> dict[str, ET.Element]:
    tree = ET.parse(src)
    root = tree.getroot()
    icons = {}
    for el in root.findall(tag("DrawingImage")):
        key = el.get(f"{{{XNS}}}Key")
        if key:
            icons[key] = el
    return icons


def run_batch(batch: Batch, check_only: bool) -> tuple[int, list[str]]:
    icons = load_icons(batch.src)

    if check_only:
        missing = [
            k for k in icons if not (batch.out_dir / f"{k.split('.', 1)[1]}.png").exists()
        ]
        return len(icons), missing

    tree = ET.parse(batch.src)
    brushes = Brushes.collect(tree.getroot())

    svg_out = batch.out_dir / "svg"
    svg_out.mkdir(parents=True, exist_ok=True)
    batch.out_dir.mkdir(parents=True, exist_ok=True)

    for key, el in icons.items():
        name = key.split(".", 1)[1]  # "Id.WindowsDomain" -> "WindowsDomain"
        svg = convert_icon(el, brushes, name)
        svg_path = svg_out / f"{name}.svg"
        svg_path.write_text(svg, encoding="utf-8")
        png_path = batch.out_dir / f"{name}.png"
        subprocess.run(
            ["rsvg-convert", "-w", str(PNG_SIZE), "-h", str(PNG_SIZE), str(svg_path), "-o", str(png_path)],
            check=True,
        )
        print(f"  [{batch.label}] {name}")

    return len(icons), []


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="只校验已生成的 PNG 覆盖全部键")
    args = parser.parse_args()

    if not args.check and subprocess.run(["which", "rsvg-convert"], capture_output=True).returncode != 0:
        print("找不到 rsvg-convert（brew install librsvg）。", file=sys.stderr)
        return 1

    total = 0
    all_missing: list[str] = []
    for batch in BATCHES:
        count, missing = run_batch(batch, args.check)
        total += count
        all_missing.extend(f"{batch.label}:{m}" for m in missing)

    if args.check:
        if all_missing:
            print(f"缺失 {len(all_missing)} 个 PNG：{', '.join(all_missing)}", file=sys.stderr)
            return 1
        print(f"{total} 个矢量图标 PNG 齐全（{len(BATCHES)} 批）。")
        return 0

    print(f"{total} 个矢量图标已生成（{len(BATCHES)} 批）。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
