#!/usr/bin/env bash
# 编译 libremoteflow_rdp.dylib（FreeRDP C ABI 封装）。
# 依赖：brew install freerdp（提供 freerdp3 / winpr3 头文件与 dylib）。
# 产出放到 RemoteFlow.Protocol.Rdp.Mac/runtimes/osx-<arch>/native/。
set -euo pipefail
cd "$(dirname "$0")"

# 优先用自建的带通道 FreeRDP（native/rdp/build-freerdp.sh 产出）。
# brew 的 freerdp3 不带通道插件 → 没有 disp 通道 → 动态分辨率发不出去 → RDP 黑边。
DIST="$PWD/freerdp-dist"
if [ -d "$DIST/include/freerdp3" ]; then
  FP="$DIST"
  echo "==> 使用自建 FreeRDP：$FP"
else
  FP="$(brew --prefix freerdp 2>/dev/null || echo /usr/local/opt/freerdp)"
  echo "==> 使用 brew FreeRDP：$FP（注意：不带通道插件，动态分辨率不可用；"
  echo "    跑 native/rdp/build-freerdp.sh 自建一份即可启用）"
fi
[ -d "$FP/include/freerdp3" ] || { echo "找不到 FreeRDP，请先跑 build-freerdp.sh 或 brew install freerdp" >&2; exit 1; }

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
