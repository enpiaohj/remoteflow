#!/usr/bin/env bash
# build-appicon.sh —— 程序图标统一入口，转调 build-appicon.py。
# 一次生成 Windows（RemoteFlow.ico / RemoteFlow-logo.png）与 macOS（AppIcon.iconset / AppIcon.icns），
# 两端共用「端点连接流」同一套图形。依赖：Python 3 + Pillow。
set -euo pipefail
cd "$(dirname "$0")/.."
python3 scripts/build-appicon.py "$@"
