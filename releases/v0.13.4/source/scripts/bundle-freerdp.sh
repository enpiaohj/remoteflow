#!/usr/bin/env bash
# bundle-freerdp.sh —— 把 FreeRDP 及其全部非系统依赖 dylib 收进 .app/Contents/Frameworks，
# 并把 libremoteflow_rdp.dylib + 所有被收进来的 dylib 的加载路径改写成 @rpath，
# 使 .app 脱离 `brew install freerdp` / 自建 dist 也能跑内嵌 RDP（方案 v1.3 §8.F）。
#
# 用法：scripts/bundle-freerdp.sh <path/to/RemoteFlow.app>
#
# 幂等：重复执行只会覆盖 Contents/Frameworks 里的同名 dylib。
# 依赖：otool / install_name_tool（Xcode command line tools 自带）。/bin/bash 3.2 兼容。
#
# libremoteflow_rdp.dylib 由 native/rdp/build.sh 产出，已带 LC_RPATH
# @loader_path/../Frameworks —— 它在 Contents/MonoBundle/，故指向 Contents/Frameworks。
#
# 自建 FreeRDP 的 dylib install_name 是 @rpath/libfreerdp*.dylib（非绝对路径），
# 光靠 otool -L 的字符串定位不到磁盘文件 —— 用 FREERDP_LIB_DIR 兜底解析。
# 默认按 .app 的架构取本仓库 native/rdp/freerdp-dist/<arch>/lib；可用环境变量覆盖。
set -euo pipefail

APP="${1:?用法: bundle-freerdp.sh <RemoteFlow.app>}"
[ -d "$APP/Contents" ] || { echo "不是有效的 .app：$APP" >&2; exit 1; }

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DETECTED_ARCH="$(lipo -archs "$APP/Contents/MacOS/RemoteFlow.App.Mac" 2>/dev/null | awk '{print $1}')"
case "${DETECTED_ARCH:-x86_64}" in x86_64) _rid=osx-x64 ;; arm64) _rid=osx-arm64 ;; *) _rid=osx-x64 ;; esac
# 默认取 shim 旁：native/rdp/build.sh 已把自建 FreeRDP dylib 复制到这里（按 RID 分架构）。
# universal 由 build-macos-release.sh 导出 FREERDP_LIB_DIR 指向 lipo 合过的一份。
FREERDP_LIB_DIR="${FREERDP_LIB_DIR:-$REPO_ROOT/src/RemoteFlow.Protocol.Rdp.Mac/runtimes/$_rid/native}"

FW="$APP/Contents/Frameworks"
mkdir -p "$FW"

SHIMS="$(find "$APP/Contents" -name libremoteflow_rdp.dylib || true)"
[ -n "$SHIMS" ] || { echo "未在 $APP 找到 libremoteflow_rdp.dylib（先 dotnet publish，确认 NativeReference 生效——检查 RID 是否匹配）" >&2; exit 1; }
SEED_SHIM="$(printf '%s\n' "$SHIMS" | head -1)"

# 是否为需要随包携带的非系统库
is_vendored() {
  case "$1" in
    /usr/lib/*|/System/*|@rpath/*|@loader_path/*|@executable_path/*) return 1 ;;
    "") return 1 ;;
    *) return 0 ;;
  esac
}

# @rpath/xxx.dylib → FREERDP_LIB_DIR 里的实际文件（找不到则回空串）
resolve_rpath() {
  local b
  b="$(basename "$1")"
  [ -f "$FREERDP_LIB_DIR/$b" ] && printf '%s' "$FREERDP_LIB_DIR/$b"
}

SEEN=""   # 换行分隔的 basename 集合

# 广度优先收集全部传递依赖，种子 = SHIM 的 otool -L
QUEUE="$(otool -L "$SEED_SHIM" | awk 'NR>1{print $1}')"
while [ -n "$QUEUE" ]; do
  src="$(printf '%s\n' "$QUEUE" | head -1)"
  QUEUE="$(printf '%s\n' "$QUEUE" | tail -n +2)"
  case "$src" in
    @rpath/*) r="$(resolve_rpath "$src")"; [ -n "$r" ] && src="$r" ;;
  esac
  is_vendored "$src" || continue
  base="$(basename "$src")"
  printf '%s\n' "$SEEN" | grep -qxF "$base" && continue
  [ -f "$src" ] || { echo "  ! 依赖不存在，跳过：$src" >&2; continue; }
  SEEN="$SEEN
$base"
  cp -f "$src" "$FW/$base"
  chmod u+w "$FW/$base"
  QUEUE="$QUEUE
$(otool -L "$src" | awk 'NR>1{print $1}')"
done
SEEN="$(printf '%s\n' "$SEEN" | grep -v '^$' || true)"

CNT="$(printf '%s\n' "$SEEN" | grep -c . || true)"
echo "==> 收进 Contents/Frameworks 的 dylib：$CNT 个"
if [ "$CNT" -eq 0 ]; then
  echo "  ✗ 一个都没收到 —— shim 对 FreeRDP 的引用都是 @rpath，且 FREERDP_LIB_DIR 里没有对应文件。" >&2
  echo "    FREERDP_LIB_DIR=$FREERDP_LIB_DIR" >&2
  echo "    请先：native/rdp/build-freerdp.sh <arch> && native/rdp/build.sh <arch>" >&2
  exit 1
fi
printf '%s\n' "$SEEN" | sed 's/^/     + /'

in_seen() { printf '%s\n' "$SEEN" | grep -qxF "$1"; }

# 改写每个被收进来的 dylib：自身 id + 对其它 vendored / @rpath 库的引用 → @rpath/<base>
printf '%s\n' "$SEEN" | while IFS= read -r base; do
  f="$FW/$base"
  install_name_tool -id "@rpath/$base" "$f"
  otool -L "$f" | awk 'NR>1{print $1}' | while IFS= read -r dep; do
    dbase="$(basename "$dep")"
    if is_vendored "$dep" && in_seen "$dbase"; then
      install_name_tool -change "$dep" "@rpath/$dbase" "$f" || true
    fi
  done
done

# 改写 shim 对 FreeRDP 的引用 + 剥掉指向真实文件系统的 LC_RPATH
printf '%s\n' "$SHIMS" | while IFS= read -r shim; do
  otool -L "$shim" | awk 'NR>1{print $1}' | while IFS= read -r dep; do
    dbase="$(basename "$dep")"
    case "$dep" in
      @rpath/*) in_seen "$dbase" && install_name_tool -change "$dep" "@rpath/$dbase" "$shim" || true ;;
      *) if is_vendored "$dep" && in_seen "$dbase"; then
           install_name_tool -change "$dep" "@rpath/$dbase" "$shim" || true
         fi ;;
    esac
  done
  install_name_tool -add_rpath "@loader_path/../Frameworks" "$shim" 2>/dev/null || true
  # dev 机器路径 / brew 路径不该烤进发布二进制
  otool -l "$shim" | awk '/LC_RPATH/{p=1;next} p&&/path /{print $2;p=0}' | while IFS= read -r rp; do
    case "$rp" in
      @*) : ;;
      *) install_name_tool -delete_rpath "$rp" "$shim" 2>/dev/null || true ;;
    esac
  done
done

echo "==> 校验：残留的绝对路径引用（应为空）"
LEFT=0
for f in "$FW"/*.dylib $SHIMS; do
  [ -f "$f" ] || continue
  while IFS= read -r dep; do
    if is_vendored "$dep"; then echo "  ✗ $(basename "$f") → $dep"; LEFT=1; fi
  done < <(otool -L "$f" | awk 'NR>1{print $1}')
  while IFS= read -r rp; do
    case "$rp" in @*) : ;; *) echo "  ✗ $(basename "$f") LC_RPATH → $rp"; LEFT=1 ;; esac
  done < <(otool -l "$f" | awk '/LC_RPATH/{p=1;next} p&&/path /{print $2;p=0}')
done
[ "$LEFT" -eq 0 ] && echo "  ✓ 全部为 @rpath / 系统库" || { echo "仍有未处理依赖" >&2; exit 1; }
