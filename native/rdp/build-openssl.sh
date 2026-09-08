#!/usr/bin/env bash
# build-openssl.sh —— 从源码编一份指定架构的 OpenSSL 3.x（共享库）。
#
# 用途：GitHub 的 Intel macOS runner 已退役，只剩 arm64。要出 x86_64 的 FreeRDP，
# 就得在 arm64 runner 上交叉编译，而 brew 只装得了本机架构的 openssl —— 这里补上
# 缺的那一份，产物喂给 build-freerdp.sh 的 OPENSSL_ROOT_DIR。
#
# 用法：
#   native/rdp/build-openssl.sh <x64|arm64> <版本> [安装前缀]
#   native/rdp/build-openssl.sh x64 3.6.4 "$HOME/openssl-x64"
#
# 产物：<前缀>/{include,lib}（libssl.3.dylib / libcrypto.3.dylib，install_name 为 @rpath/…）
set -euo pipefail

ARCH_IN="${1:?用法: build-openssl.sh <x64|arm64> <版本> [前缀]}"
VER="${2:?缺 OpenSSL 版本，如 3.6.4}"
case "$ARCH_IN" in
  x64|x86_64)   OSX_ARCH=x86_64; SSL_TARGET=darwin64-x86_64-cc ;;
  arm64|aarch64) OSX_ARCH=arm64;  SSL_TARGET=darwin64-arm64-cc  ;;
  *) echo "未知架构：$ARCH_IN" >&2; exit 1 ;;
esac
PREFIX="${3:-$HOME/openssl-$OSX_ARCH}"

if [ -f "$PREFIX/lib/libssl.3.dylib" ] && [ -d "$PREFIX/include/openssl" ]; then
  got="$(lipo -archs "$PREFIX/lib/libssl.3.dylib" 2>/dev/null || true)"
  if [ "$got" = "$OSX_ARCH" ]; then
    echo "==> 已存在且架构匹配（${OSX_ARCH}），跳过：$PREFIX"
    exit 0
  fi
  echo "==> $PREFIX 存在但架构为 [$got]，重建"
  rm -rf "$PREFIX"
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"

URL="https://github.com/openssl/openssl/releases/download/openssl-$VER/openssl-$VER.tar.gz"
echo "==> 下载 $URL"
curl -fsSL "$URL" | tar xz
cd "openssl-$VER"

echo "==> Configure ${SSL_TARGET}（shared，min macOS 13.0）"
./Configure "$SSL_TARGET" shared no-tests \
  --prefix="$PREFIX" \
  --openssldir="$PREFIX/ssl" \
  -mmacosx-version-min=13.0

echo "==> make（$(sysctl -n hw.ncpu) 核）"
make -j"$(sysctl -n hw.ncpu)" >/dev/null

echo "==> make install_sw"
make install_sw >/dev/null

# 刻意保留 dylib 默认的绝对 install_name（$PREFIX/lib/libssl.3.dylib）：
# FreeRDP 会按绝对路径链接，随后 scripts/bundle-freerdp.sh 的 BFS 沿绝对路径
# 把它收进 Contents/Frameworks 并统一改 @rpath —— 与 arm64 走 brew 的路径一致。

echo "==> 完成：$PREFIX"
lipo -archs "$PREFIX/lib/libssl.3.dylib"
