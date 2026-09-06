#!/usr/bin/env bash
# 编译 libremoteflow_rdp.dylib（FreeRDP C ABI 封装）。
# 依赖：brew install freerdp（提供 freerdp3 / winpr3 头文件与 dylib）。
# 产出放到 RemoteFlow.Protocol.Rdp.Mac/runtimes/osx-<arch>/native/。
set -euo pipefail
cd "$(dirname "$0")"

FP="$(brew --prefix freerdp 2>/dev/null || echo /usr/local/opt/freerdp)"
[ -d "$FP/include/freerdp3" ] || { echo "找不到 FreeRDP，请先 brew install freerdp" >&2; exit 1; }

ARCH="${1:-$(uname -m)}"   # x86_64 | arm64
case "$ARCH" in
  x64|x86_64) ARCH=x86_64; RID=osx-x64 ;;
  arm64|aarch64) ARCH=arm64; RID=osx-arm64 ;;
esac

OUT_DIR="../../src/RemoteFlow.Protocol.Rdp.Mac/runtimes/$RID/native"
mkdir -p "$OUT_DIR"

echo "==> 编译 $ARCH → $OUT_DIR/libremoteflow_rdp.dylib"
cc -O2 -fPIC -shared -arch "$ARCH" \
  -o "$OUT_DIR/libremoteflow_rdp.dylib" \
  -install_name @rpath/libremoteflow_rdp.dylib \
  remoteflow_rdp.c \
  -I"$FP/include/freerdp3" -I"$FP/include/winpr3" \
  -L"$FP/lib" -lfreerdp3 -lfreerdp-client3 -lwinpr3 \
  -Wl,-rpath,@loader_path/../Frameworks \
  -Wl,-rpath,@loader_path \
  -Wl,-rpath,"$FP/lib"
# rpath 顺序即 dyld 搜索顺序：优先 .app 内 Contents/Frameworks（scripts/bundle-freerdp.sh
# 收进的随包副本）→ 同目录 → 最后才回落 brew（仅未打包的 Debug 内循环用）。

echo "==> 依赖："
otool -L "$OUT_DIR/libremoteflow_rdp.dylib" | sed -n '2,6p'
echo "完成。运行时需 FreeRDP dylib 在 rpath 或打进 .app/Contents/Frameworks（见方案 §8.F）。"
