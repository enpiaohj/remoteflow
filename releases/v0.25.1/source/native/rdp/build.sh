#!/usr/bin/env bash
# 编译 libremoteflow_rdp.dylib（FreeRDP C ABI 封装）。
# 依赖：brew install freerdp（提供 freerdp3 / winpr3 头文件与 dylib）。
# 产出放到 RemoteFlow.Protocol.Rdp.Mac/runtimes/osx-<arch>/native/。
set -euo pipefail
cd "$(dirname "$0")"

ARCH="${1:-$(uname -m)}"   # x86_64 | arm64
case "$ARCH" in
  x64|x86_64) ARCH=x86_64; RID=osx-x64 ;;
  arm64|aarch64) ARCH=arm64; RID=osx-arm64 ;;
  *) echo "未知架构：${ARCH}（x64 | arm64）" >&2; exit 1 ;;
esac

# 优先用自建的带通道 FreeRDP（native/rdp/build-freerdp.sh <arch> 产出，按架构分目录）。
# brew 的 freerdp3 也带通道，但版本不可钉、发布时依赖用户机器上的 brew。
DIST="$PWD/freerdp-dist/$ARCH"
FREERDP_IS_VENDORED=0
if [ -d "$DIST/include/freerdp3" ]; then
  FP="$DIST"; FREERDP_IS_VENDORED=1
  echo "==> 使用自建 FreeRDP（${ARCH}）：$FP"
else
  FP="$(brew --prefix freerdp 2>/dev/null || echo /usr/local/opt/freerdp)"
  echo "==> 使用 brew FreeRDP：${FP}（发布前建议改用 build-freerdp.sh $1 自建）"
fi
[ -d "$FP/include/freerdp3" ] || { echo "找不到 FreeRDP，请先跑 build-freerdp.sh $1 或 brew install freerdp" >&2; exit 1; }

OUT_DIR="../../src/RemoteFlow.Protocol.Rdp.Mac/runtimes/$RID/native"
mkdir -p "$OUT_DIR"

echo "==> 编译 $ARCH → $OUT_DIR/libremoteflow_rdp.dylib"
# -headerpad_max_install_names：.NET macOS SDK 打包进 MonoBundle 时会用 install_name_tool
# 改写 install id / rpath 成更长的 @executable_path/... ，Mach-O 头没留白就报
#   "larger updated load commands do not fit"（CI arm64 就是栽在这）。CMake 建的
# FreeRDP dylib 默认带这个 flag，手写 cc 链接得自己加。
cc -O2 -fPIC -shared -arch "$ARCH" \
  -Wl,-headerpad_max_install_names \
  -o "$OUT_DIR/libremoteflow_rdp.dylib" \
  -install_name @rpath/libremoteflow_rdp.dylib \
  remoteflow_rdp.c \
  -I"$FP/include/freerdp3" -I"$FP/include/winpr3" \
  -L"$FP/lib" -lfreerdp3 -lfreerdp-client3 -lwinpr3 \
  -Wl,-rpath,@loader_path/../Frameworks \
  -Wl,-rpath,@loader_path \
  $([ "$FREERDP_IS_VENDORED" = 0 ] && printf -- '-Wl,-rpath,%s' "$FP/lib")
# rpath 顺序即 dyld 搜索顺序：.app 内 Contents/Frameworks（bundle-freerdp.sh 收进的随包副本）
# → shim 同目录（Debug 内循环：下面会把自建 FreeRDP dylib 复制到这里）
# → 仅 brew 兜底时追加 brew/lib（自建 FreeRDP 绝不把绝对路径烤进发布二进制）。

if [ "$FREERDP_IS_VENDORED" = 1 ]; then
  echo "==> 复制自建 FreeRDP dylib 到 shim 旁（供 Debug 的 @loader_path 解析；发布时 bundle-freerdp.sh 会移到 Frameworks）"
  cp -f "$FP"/lib/libfreerdp*.dylib "$FP"/lib/libwinpr*.dylib "$OUT_DIR/" 2>/dev/null || true
fi

echo "==> 依赖："
otool -L "$OUT_DIR/libremoteflow_rdp.dylib" | sed -n '2,6p'
echo "完成（${RID}）。发布：scripts/build-macos-release.sh 会调 bundle-freerdp.sh 把 FreeRDP 收进 .app/Contents/Frameworks。"
