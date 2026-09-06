#!/usr/bin/env bash
# bundle-freerdp.sh —— 把 FreeRDP 及其全部非系统依赖 dylib 收进 .app/Contents/Frameworks，
# 并把 libremoteflow_rdp.dylib + 所有被收进来的 dylib 的加载路径改写成 @rpath，
# 使 .app 脱离 `brew install freerdp` 也能跑内嵌 RDP（方案 v1.3 §8.F）。
#
# 用法：scripts/bundle-freerdp.sh <path/to/RemoteFlow.app>
#
# 幂等：重复执行只会覆盖 Contents/Frameworks 里的同名 dylib。
# 依赖：otool / install_name_tool（Xcode command line tools 自带）。/bin/bash 3.2 兼容。
#
# libremoteflow_rdp.dylib 由 native/rdp/build.sh 产出，已带 LC_RPATH
# @loader_path/../Frameworks —— 它在 Contents/MonoBundle/，故指向 Contents/Frameworks。
set -euo pipefail

APP="${1:?用法: bundle-freerdp.sh <RemoteFlow.app>}"
[ -d "$APP/Contents" ] || { echo "不是有效的 .app：$APP" >&2; exit 1; }

FW="$APP/Contents/Frameworks"
mkdir -p "$FW"

SHIMS="$(find "$APP/Contents" -name libremoteflow_rdp.dylib || true)"
[ -n "$SHIMS" ] || { echo "未在 $APP 找到 libremoteflow_rdp.dylib（先 dotnet build，确认 NativeReference 生效）" >&2; exit 1; }
SEED_SHIM="$(printf '%s\n' "$SHIMS" | head -1)"

# 是否为需要随包携带的非系统库
is_vendored() {
  case "$1" in
    /usr/lib/*|/System/*|@rpath/*|@loader_path/*|@executable_path/*) return 1 ;;
    "") return 1 ;;
    *) return 0 ;;
  esac
}

SEEN=""   # 换行分隔的 basename 集合

# 广度优先收集全部传递依赖，种子 = SHIM 当前引用的 FreeRDP 绝对路径
QUEUE="$(otool -L "$SEED_SHIM" | awk 'NR>1{print $1}')"
while [ -n "$QUEUE" ]; do
  src="$(printf '%s\n' "$QUEUE" | head -1)"
  QUEUE="$(printf '%s\n' "$QUEUE" | tail -n +2)"
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

echo "==> 收进 Contents/Frameworks 的 dylib：$(printf '%s\n' "$SEEN" | wc -l | tr -d ' ') 个"

in_seen() { printf '%s\n' "$SEEN" | grep -qxF "$1"; }

# 改写每个被收进来的 dylib：自身 id + 对其它 vendored 库的引用 → @rpath/<base>
printf '%s\n' "$SEEN" | while IFS= read -r base; do
  f="$FW/$base"
  install_name_tool -id "@rpath/$base" "$f"
  otool -L "$f" | awk 'NR>1{print $1}' | while IFS= read -r dep; do
    if is_vendored "$dep"; then
      dbase="$(basename "$dep")"
      in_seen "$dbase" && install_name_tool -change "$dep" "@rpath/$dbase" "$f" || true
    fi
  done
done

# 改写 shim 对 FreeRDP 的引用
printf '%s\n' "$SHIMS" | while IFS= read -r shim; do
  otool -L "$shim" | awk 'NR>1{print $1}' | while IFS= read -r dep; do
    if is_vendored "$dep"; then
      dbase="$(basename "$dep")"
      in_seen "$dbase" && install_name_tool -change "$dep" "@rpath/$dbase" "$shim" || true
    fi
  done
  install_name_tool -add_rpath "@loader_path/../Frameworks" "$shim" 2>/dev/null || true
done

echo "==> 校验：残留的绝对路径引用（应为空）"
LEFT=0
for f in "$FW"/*.dylib $SHIMS; do
  while IFS= read -r dep; do
    if is_vendored "$dep"; then echo "  ✗ $(basename "$f") → $dep"; LEFT=1; fi
  done < <(otool -L "$f" | awk 'NR>1{print $1}')
done
[ "$LEFT" -eq 0 ] && echo "  ✓ 全部为 @rpath / 系统库" || { echo "仍有未处理依赖" >&2; exit 1; }
