#!/usr/bin/env bash
# build-appicon.sh —— 从 AppIcon.svg 生成 AppIcon.iconset + AppIcon.icns。
# 依赖：rsvg-convert（brew install librsvg）、iconutil（macOS 自带）。
set -euo pipefail
cd "$(dirname "$0")/../src/RemoteFlow.App.Mac/Resources"

SET=AppIcon.iconset
rm -rf "$SET"; mkdir -p "$SET"

# 大尺寸用完整字形（双 chevron + 光影），16/32 用简化字形（单粗 chevron）避免糊。
big()   { rsvg-convert -w "$1" -h "$1" AppIcon.svg       -o "$SET/$2"; }
small() { rsvg-convert -w "$1" -h "$1" AppIcon-small.svg -o "$SET/$2"; }

small 16   icon_16x16.png
small 32   icon_16x16@2x.png
small 32   icon_32x32.png
small 64   icon_32x32@2x.png
big   128  icon_128x128.png
big   256  icon_128x128@2x.png
big   256  icon_256x256.png
big   512  icon_256x256@2x.png
big   512  icon_512x512.png
big   1024 icon_512x512@2x.png

iconutil -c icns "$SET" -o AppIcon.icns
echo "✅ AppIcon.icns ($(du -h AppIcon.icns | cut -f1))"
