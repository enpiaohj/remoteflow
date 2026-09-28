#!/usr/bin/env python3
"""build-ui-icons.py —— 从微软 Fluent UI System Icons（MIT）生成 WPF 图标几何资源。

生成 src/RemoteFlow.App/Themes/UiIcons.xaml：每个图标一个 Geometry 资源（键 Ui.*），
由统一的图标样式按文字前景色着色。只取本应用用到的图标；同一图标的多条 path 合并为一个几何，
以非零环绕规则（F1）填充，与 SVG 默认 fill-rule 一致。

用法：
  python scripts/build-ui-icons.py          # 从 GitHub 拉取并生成
  python scripts/build-ui-icons.py --check  # 只校验已生成文件覆盖全部键

来源：https://github.com/microsoft/fluentui-system-icons （MIT License，见 THIRD-PARTY-NOTICES.md）
"""

from __future__ import annotations

import argparse
import re
import sys
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "RemoteFlow.App" / "Themes" / "UiIcons.xaml"
BASE = "https://raw.githubusercontent.com/microsoft/fluentui-system-icons/main/assets"
SIZE = 20

# 资源键 → 候选图标名（按顺序尝试，取第一个存在的）。filled=True 时额外生成 <键>.Filled。
ICONS: list[tuple[str, list[str], bool]] = [
    # 导航（线性，选中时切换为实心）
    ("Ui.Home", ["Home"], True),
    ("Ui.Connections", ["Desktop"], True),
    ("Ui.Credential", ["Key"], True),
    ("Ui.Settings", ["Settings"], True),
    ("Ui.Help", ["Question Circle"], True),
    ("Ui.Info", ["Info"], True),
    # 通用操作
    ("Ui.Add", ["Add"], False),
    ("Ui.Edit", ["Edit"], False),
    ("Ui.Copy", ["Copy"], False),
    ("Ui.Delete", ["Delete"], False),
    ("Ui.Close", ["Dismiss"], False),
    ("Ui.More", ["More Horizontal"], False),
    ("Ui.Refresh", ["Arrow Clockwise"], False),
    ("Ui.Search", ["Search"], False),
    ("Ui.Checklist", ["Task List Ltr", "Checkbox Checked"], False),
    ("Ui.Favorite", ["Star"], True),
    ("Ui.Recent", ["History"], False),
    ("Ui.Reveal", ["Eye"], False),
    ("Ui.Hide", ["Eye Off"], False),
    ("Ui.Download", ["Arrow Download"], False),
    ("Ui.Upload", ["Arrow Upload"], False),
    ("Ui.Database", ["Database"], False),
    ("Ui.Lock", ["Lock Closed"], False),
    ("Ui.Appearance", ["Paint Brush"], False),
    ("Ui.Power", ["Power"], False),
    ("Ui.Language", ["Local Language"], False),
    ("Ui.FullScreen", ["Full Screen Maximize"], False),
    ("Ui.ExitFullScreen", ["Full Screen Minimize"], False),
    ("Ui.Scale", ["Arrow Fit", "Scale Fit", "Arrow Maximize"], False),
    ("Ui.Clipboard", ["Clipboard"], False),
    ("Ui.Keyboard", ["Keyboard"], False),
    ("Ui.TaskManager", ["Data Histogram", "Pulse"], False),
    ("Ui.Paste", ["Clipboard Paste"], False),
    ("Ui.Clear", ["Broom", "Eraser"], False),
    ("Ui.Pin", ["Pin"], False),
    ("Ui.Unpin", ["Pin Off"], False),
    ("Ui.Sessions", ["Window Multiple", "Tab Desktop Multiple"], False),
    ("Ui.Folder", ["Folder"], False),
    ("Ui.ImportExport", ["Arrow Swap"], False),
    ("Ui.Connect", ["Plug Connected"], False),
    ("Ui.Save", ["Save"], False),
    ("Ui.Accept", ["Checkmark"], False),
    ("Ui.Tag", ["Tag"], False),
    # 状态（行内小图标）
    ("Ui.Warning", ["Warning"], True),
    ("Ui.Shield", ["Shield Checkmark", "Shield"], False),
    ("Ui.Error", ["Error Circle"], True),
    ("Ui.Success", ["Checkmark Circle"], True),
    ("Ui.Pending", ["Circle"], False),
    ("Ui.Disconnected", ["Plug Disconnected"], False),
    # 主题分段按钮
    ("Ui.ThemeSystem", ["Laptop"], False),
    ("Ui.ThemeLight", ["Weather Sunny"], False),
    ("Ui.ThemeDark", ["Weather Moon"], False),
    # 协议（会话选择器、协议选择等单色场景；彩色协议徽章仍用 DeviceIcons.xaml 的 ProtocolIcon.*）
    ("Ui.ProtocolRdp", ["Desktop"], False),
    ("Ui.ProtocolSsh", ["Window Console"], False),
    ("Ui.ProtocolVnc", ["Desktop Cursor", "Desktop Mac"], False),
]

PATH_RE = re.compile(r'<path[^>]*?\sd="([^"]+)"[^>]*?>', re.S)


def fetch_svg(name: str, style: str) -> str | None:
    stem = name.lower().replace(" ", "_")
    url = f"{BASE}/{urllib.parse.quote(name)}/SVG/ic_fluent_{stem}_{SIZE}_{style}.svg"
    try:
        with urllib.request.urlopen(url, timeout=30) as resp:
            return resp.read().decode("utf-8")
    except Exception:
        return None


def to_geometry(svg: str) -> str:
    paths = PATH_RE.findall(svg)
    if not paths:
        raise ValueError("SVG 中没有 path")
    # 非零环绕填充；多条 path 直接拼接（每条都以 M 起笔，互不影响）。
    # 开头两个空 MoveTo 把几何边界锚定为完整 20×20 画布：Path 按 Stretch=Uniform 缩放时
    # 保留图标在画布内的留白与位置，省略号、箭头等扁平图形不会被拉满、各图标视觉大小一致。
    return "F1 M0,0 M20,20 " + " ".join(p.strip() for p in paths)


def build() -> int:
    lines = [
        '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
        "",
        "    <!--",
        "        界面线性图标（Ui.*）。由 scripts/build-ui-icons.py 生成，请勿手改。",
        f"        来源：Microsoft Fluent UI System Icons（{SIZE}px，regular / filled），MIT License，",
        "        https://github.com/microsoft/fluentui-system-icons ，许可证全文见 THIRD-PARTY-NOTICES.md。",
        "        单色几何，由 UiIcon 样式按文字前景色着色；身份类彩色图标见 IdentityIcons.xaml。",
        "    -->",
        "",
    ]
    missing = []
    for key, names, filled in ICONS:
        variants = [("regular", key)] + ([("filled", key + ".Filled")] if filled else [])
        for style, res_key in variants:
            svg = None
            used = None
            for name in names:
                svg = fetch_svg(name, style)
                if svg:
                    used = name
                    break
            if not svg:
                missing.append(f"{res_key}（{' / '.join(names)}）")
                continue
            geometry = to_geometry(svg)
            lines.append(f'    <!-- {used} · {style} -->')
            lines.append(f'    <Geometry x:Key="{res_key}">{geometry}</Geometry>')
    lines += ["", "</ResourceDictionary>", ""]

    if missing:
        print("未找到：", *missing, sep="\n  ")
        return 1

    OUT.write_text("\n".join(lines), encoding="utf-8", newline="\n")  # 仓库统一 LF（.gitattributes）
    print(f"已生成 {OUT.relative_to(ROOT)}，共 {sum(2 if f else 1 for _, _, f in ICONS)} 个图标")
    return 0


def check() -> int:
    text = OUT.read_text(encoding="utf-8")
    expected = [k for k, _, f in ICONS for k in ([k, k + ".Filled"] if f else [k])]
    missing = [k for k in expected if f'x:Key="{k}"' not in text]
    for k in missing:
        print("缺少", k)
    print("校验通过" if not missing else f"校验失败：缺少 {len(missing)} 个")
    return 1 if missing else 0


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    sys.exit(check() if parser.parse_args().check else build())
