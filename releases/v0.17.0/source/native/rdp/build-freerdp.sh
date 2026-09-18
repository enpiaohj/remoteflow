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
#   native/rdp/build-freerdp.sh [x64|arm64]          # 默认本机架构；克隆+配置+编译+安装
#   native/rdp/build-freerdp.sh [x64|arm64] --clean  # 清掉该架构的 build/dist 重来
#
# 产物：native/rdp/freerdp-dist/<arch>/{include,lib}   （arch = x86_64 | arm64）
# 之后 build.sh 会优先用它（没有则回落 brew）。
#
# 交叉编译到 arm64 需要 arm64 的 openssl（brew 在 Apple Silicon 上是 /opt/homebrew）。
# Intel 机器上大概率没有 arm64 openssl，arm64 构建会失败——那就只发 x64。
set -euo pipefail
cd "$(dirname "$0")"

FREERDP_TAG="${FREERDP_TAG:-3.31.1}"   # 与此前 brew 版本对齐，保持 API 一致

ARCH_IN="${1:-$(uname -m)}"
case "$ARCH_IN" in
  x64|x86_64) OSX_ARCH=x86_64 ;;
  arm64|aarch64) OSX_ARCH=arm64 ;;
  --clean) OSX_ARCH="$(uname -m)"; set -- --clean ;;
  *) echo "未知架构：${ARCH_IN}（x64 | arm64）" >&2; exit 1 ;;
esac

SRC="freerdp-src"                       # 源码单份，多架构共用
BUILD="freerdp-build-$OSX_ARCH"
DIST="$PWD/freerdp-dist/$OSX_ARCH"

case "${2:-${1:-}}" in
  --clean) echo "==> 清理 $BUILD 与 $DIST"; rm -rf "$BUILD" "$DIST" ;;
esac

command -v cmake >/dev/null || { echo "需要 cmake：brew install cmake" >&2; exit 1; }

# OpenSSL：优先用外部传入的 OPENSSL_ROOT_DIR（CI 在 arm64 runner 上交叉编译 x86_64
# 时，brew 只有本机架构那份，需自己从源码编一份对应架构的 openssl 再从这里传进来），
# 否则回落 brew。
OPENSSL_PREFIX="${OPENSSL_ROOT_DIR:-$(brew --prefix openssl@3 2>/dev/null || brew --prefix openssl 2>/dev/null || true)}"
[ -n "$OPENSSL_PREFIX" ] || { echo "需要 OpenSSL：brew install openssl@3，或设 OPENSSL_ROOT_DIR" >&2; exit 1; }
[ -d "$OPENSSL_PREFIX/include/openssl" ] || { echo "OPENSSL 前缀无效（缺 include/openssl）：$OPENSSL_PREFIX" >&2; exit 1; }

if [ ! -d "$SRC" ]; then
  echo "==> 克隆 FreeRDP $FREERDP_TAG"
  git clone --depth 1 --branch "$FREERDP_TAG" https://github.com/FreeRDP/FreeRDP.git "$SRC"
fi

# 交叉编译专属开关：宿主 x86_64 → 目标 arm64 时有两个只存在于该方向的坑，
# 都源于「按宿主取到的东西直接喂给 arm64 目标」。仅在宿主 x86_64 且目标是 arm64 时启用；
# 宿主 arm64 反向交叉编 x64（CI 的做法）与原生构建都不受影响，保持原样。
#   1) SIMD：DetectIntrinsicSupport.cmake 只看 CMAKE_SYSTEM_PROCESSOR（= 宿主），不看
#      CMAKE_OSX_ARCHITECTURES。宿主命中 SSE_LIST → 给 sse/*.c 加 -msse3，目标是 arm64 →
#      clang "unsupported option '-msse3'"（flag 校验在预处理前，那些文件虽有
#      SSE_AVX_INTRINSICS_ENABLED 守卫也救不了）。NEON 在 arm64 是基线指令，关掉无影响。
#   2) Opus：-DWITH_DSP_FFMPEG=OFF 后 FreeRDP 会去 find_package(Opus) 并在找到时默认打开
#      WITH_OPUS；宿主 brew 的 libopus 是 x86_64，链进 arm64 dylib 会报一堆
#      _opus_* undefined symbols。arm64 侧没有可用的 Opus，只能关掉（本客户端的音频通道
#      本就未启用，WITH_MACAUDIO=OFF）。
CROSS_FLAG=()
if [ "$(uname -m)" = "x86_64" ] && [ "$OSX_ARCH" = "arm64" ]; then
  echo "==> 宿主 x86_64 → 目标 arm64：关闭 SIMD 标记与 Opus 以规避 -msse3 / _opus_* 报错"
  CROSS_FLAG=(-DWITH_SIMD=OFF -DWITH_OPUS=OFF)
fi

echo "==> 配置（通道内建；服务端 / 代理 / X11 / 音视频等一律关掉，只要客户端核心）"
cmake -S "$SRC" -B "$BUILD" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX="$DIST" \
  -DCMAKE_INSTALL_RPATH="@loader_path" \
  -DCMAKE_OSX_ARCHITECTURES="$OSX_ARCH" \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0 \
  -DBUILD_SHARED_LIBS=ON \
  -DBUILD_TESTING=OFF \
  -DOPENSSL_ROOT_DIR="$OPENSSL_PREFIX" \
  -DWITH_CHANNELS=ON \
  -DBUILTIN_CHANNELS=ON \
  -DCHANNEL_DISP=ON -DCHANNEL_DISP_CLIENT=ON \
  -DCHANNEL_DRDYNVC=ON -DCHANNEL_DRDYNVC_CLIENT=ON \
  -DCHANNEL_CLIPRDR=ON -DCHANNEL_CLIPRDR_CLIENT=ON \
  -DCHANNEL_URBDRC=OFF -DCHANNEL_URBDRC_CLIENT=OFF \
  -DWITH_LIBUSB=OFF \
  -DWITH_CLIENT=ON -DWITH_CLIENT_COMMON=ON \
  -DWITH_CLIENT_SDL=OFF -DWITH_CLIENT_SDL2=OFF -DWITH_CLIENT_SDL3=OFF \
  -DWITH_X11=OFF -DWITH_WAYLAND=OFF \
  -DWITH_SERVER=OFF -DWITH_PROXY=OFF -DWITH_SHADOW=OFF -DWITH_SAMPLE=OFF \
  -DWITH_MANPAGES=OFF -DWITH_WINPR_TOOLS=OFF \
  -DWITH_FFMPEG=OFF -DWITH_SWSCALE=OFF -DWITH_DSP_FFMPEG=OFF \
  -DWITH_CUPS=OFF -DWITH_PCSC=OFF -DWITH_LIBUSB=OFF \
  -DWITH_PULSE=OFF -DWITH_ALSA=OFF -DWITH_OSS=OFF -DWITH_MACAUDIO=OFF \
  -DWITH_KRB5=OFF -DWITH_AAD=OFF -DWITH_WEBVIEW=OFF \
  -DWITH_FUSE=OFF -DWITH_URIPARSER=OFF \
  -DWITH_JSON_DISABLED=ON \
  ${CROSS_FLAG[@]+"${CROSS_FLAG[@]}"}

echo "==> 编译 ${OSX_ARCH}（多核，耐心等几分钟）"
cmake --build "$BUILD" --parallel "$(sysctl -n hw.ncpu)"

echo "==> 安装到 $DIST"
cmake --install "$BUILD"

echo "==> 校验：通道是否真的编进了 libfreerdp-client3"
FOUND_DISP=0
for lib in "$DIST"/lib/libfreerdp-client3*.dylib; do
  [ -e "$lib" ] || continue
  if nm "$lib" 2>/dev/null | grep -q "_disp_DVCPluginEntry$"; then FOUND_DISP=1; break; fi
done
if [ "$FOUND_DISP" = 1 ]; then
  echo "    OK：找到 disp_DVCPluginEntry —— Display Control 已内建，动态分辨率可用。"
else
  # 非致命：真正能不能加载在运行时看日志 [RDP/native] LoadChannels: load=1。
  echo "    !! 未在 libfreerdp-client3 里搜到 disp_DVCPluginEntry —— 若运行时 RDP 黑边再查 CMake。" >&2
fi

echo "完成（${OSX_ARCH}）。接着跑 native/rdp/build.sh $ARCH_IN 重编 shim（会自动优先用 ${DIST}）。"
