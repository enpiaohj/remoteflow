#!/usr/bin/env bash
# build-freerdp.sh —— 自建带「通道插件」的 FreeRDP，供内嵌 RDP 使用。
#
# 为什么自建（而不是只依赖 brew install freerdp）：
#   brew 的 freerdp3 其实**是带通道的**（disp / drdynvc 都编进了 libfreerdp-client3，
#   只是入口是 local 符号，用 nm -g 查会被过滤掉，别被误导）。自建的价值在于：
#   版本可钉、可裁剪（关掉服务端 / 代理 / X11 / 音视频），且发布时不依赖用户机器上的 brew。
#   注意：通道能不能真正加载还取决于 rf_pre_connect 里的设置 ——
#   NetworkAutoDetect / SupportHeartbeatPdu / SupportMultitransport 默认开着会强制
#   拉起 rdpdr，rdpdr 加载失败会让 load_addins 整体失败，drdynvc / disp 跟着遭殃。
#
# 本脚本用 BUILTIN_CHANNELS=ON 把通道**编进** libfreerdp-client3.dylib，
# 因此不产生额外的通道 dylib，scripts/bundle-freerdp.sh 的传递依赖收集逻辑无需改动。
#
# 用法：
#   native/rdp/build-freerdp.sh            # 首次：克隆 + 配置 + 编译 + 安装到 dist
#   native/rdp/build-freerdp.sh --rebuild  # 只重编（源码已在）
#   native/rdp/build-freerdp.sh --clean    # 清掉 build 与 dist 重来
#
# 产物：native/rdp/freerdp-dist/{include,lib}
# 之后 build.sh 会优先用它（没有则回落 brew）。
set -euo pipefail
cd "$(dirname "$0")"

FREERDP_TAG="${FREERDP_TAG:-3.31.1}"   # 与此前 brew 版本对齐，保持 API 一致
SRC="freerdp-src"
BUILD="freerdp-build"
DIST="$PWD/freerdp-dist"

case "${1:-}" in
  --clean) echo "==> 清理 $BUILD 与 $DIST"; rm -rf "$BUILD" "$DIST" ;;
  --rebuild) ;;
esac

command -v cmake >/dev/null || { echo "需要 cmake：brew install cmake" >&2; exit 1; }

OPENSSL_PREFIX="$(brew --prefix openssl@3 2>/dev/null || brew --prefix openssl 2>/dev/null || true)"
[ -n "$OPENSSL_PREFIX" ] || { echo "需要 OpenSSL：brew install openssl@3" >&2; exit 1; }

if [ ! -d "$SRC" ]; then
  echo "==> 克隆 FreeRDP $FREERDP_TAG"
  git clone --depth 1 --branch "$FREERDP_TAG" https://github.com/FreeRDP/FreeRDP.git "$SRC"
fi

echo "==> 配置（通道内建；服务端 / 代理 / X11 / 音视频等一律关掉，只要客户端核心）"
cmake -S "$SRC" -B "$BUILD" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX="$DIST" \
  -DCMAKE_INSTALL_RPATH="@loader_path" \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0 \
  -DBUILD_SHARED_LIBS=ON \
  -DBUILD_TESTING=OFF \
  -DOPENSSL_ROOT_DIR="$OPENSSL_PREFIX" \
  -DWITH_CHANNELS=ON \
  -DBUILTIN_CHANNELS=ON \
  -DCHANNEL_DISP=ON -DCHANNEL_DISP_CLIENT=ON \
  -DCHANNEL_DRDYNVC=ON -DCHANNEL_DRDYNVC_CLIENT=ON \
  -DCHANNEL_CLIPRDR=ON -DCHANNEL_CLIPRDR_CLIENT=ON \
  -DWITH_CLIENT=ON -DWITH_CLIENT_COMMON=ON \
  -DWITH_CLIENT_SDL=OFF -DWITH_CLIENT_SDL2=OFF -DWITH_CLIENT_SDL3=OFF \
  -DWITH_X11=OFF -DWITH_WAYLAND=OFF \
  -DWITH_SERVER=OFF -DWITH_PROXY=OFF -DWITH_SHADOW=OFF -DWITH_SAMPLE=OFF \
  -DWITH_MANPAGES=OFF -DWITH_WINPR_TOOLS=OFF \
  -DWITH_FFMPEG=OFF -DWITH_SWSCALE=OFF -DWITH_DSP_FFMPEG=OFF \
  -DWITH_CUPS=OFF -DWITH_PCSC=OFF -DWITH_LIBUSB=OFF \
  -DWITH_PULSE=OFF -DWITH_ALSA=OFF -DWITH_OSS=OFF -DWITH_MACAUDIO=OFF \
  -DWITH_KRB5=OFF -DWITH_AAD=OFF -DWITH_WEBVIEW=OFF \
  -DWITH_FUSE=OFF -DWITH_URIPARSER=OFF

echo "==> 编译（多核，耐心等几分钟）"
cmake --build "$BUILD" --parallel "$(sysctl -n hw.ncpu)"

echo "==> 安装到 $DIST"
cmake --install "$BUILD"

echo "==> 校验：通道是否真的编进了 libfreerdp-client3"
CLIENT_LIB="$(find "$DIST/lib" -name 'libfreerdp-client3*.dylib' | head -1)"
if nm "$CLIENT_LIB" 2>/dev/null | grep -q "disp_DVCPluginEntry"; then
  echo "    OK：找到 disp_DVCPluginEntry —— Display Control 可用，动态分辨率生效。"
else
  echo "    !! 没找到 disp_DVCPluginEntry，通道未内建，请检查 CMake 配置。" >&2
  nm "$CLIENT_LIB" 2>/dev/null | grep -iE "DVCPluginEntry|VirtualChannelEntry" | head >&2
  exit 1
fi

echo "完成。接着跑 native/rdp/build.sh 重编 shim（会自动优先用 $DIST）。"
