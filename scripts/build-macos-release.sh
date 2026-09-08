#!/usr/bin/env bash
# build-macos-release.sh —— RemoteFlow macOS 发布产物打包
#
# 产出：dist/RemoteFlow.app（ad-hoc 签名，架构见 --arch）
#       dist/RemoteFlow-v<版本>-macos-<arch>.dmg
#
# 用法：scripts/build-macos-release.sh [x64|arm64|universal]   默认 x64（本机 Intel）
#
# 前置：dotnet（含 macos workload）。本机 Xcode 26.3 下 Release 走
#       LinkMode=None + Registrar=dynamic（见 csproj 注释 / 方案 §8.F）。
#
# universal：lipo 合并后需逐层自底向上 ad-hoc 重签（--deep 不可靠，会导致
#            "Microsoft.macOS: Failed to initialize the VM"）。当前脚本已按此顺序处理；
#            仍建议正式发布时用 Developer ID 单独签每个 Mach-O。
#
# 未做（需 Apple Developer 账号，见文末模板）：
#   - Developer ID Application 正式签名 + Hardened Runtime + entitlements
#   - notarytool 公证 + staple
set -euo pipefail

cd "$(dirname "$0")/.."
ARCH="${1:-x64}"
PROJ="src/RemoteFlow.App.Mac/RemoteFlow.App.Mac.csproj"
# macOS 版有独立版本线（见 csproj 注释）——版本以它的 ApplicationDisplayVersion 为准。
VERSION="$(sed -n 's/.*<ApplicationDisplayVersion>\(.*\)<\/ApplicationDisplayVersion>.*/\1/p' "$PROJ")"
[ -n "$VERSION" ] || { echo "未能从 $PROJ 读到 ApplicationDisplayVersion" >&2; exit 1; }
echo "==> macOS 版本：${VERSION}（对应 tag: macos-v${VERSION}）"

OUT="src/RemoteFlow.App.Mac/bin/Release/net10.0-macos"
DIST="dist"
APP="$DIST/RemoteFlow.app"

echo "==> 清理"
rm -rf "$DIST" "$OUT"
mkdir -p "$DIST"

# ── native RDP 前置：内嵌 FreeRDP（带 Display Control 通道）+ C ABI 封装 shim ──
# 缺失时应用仍能启动，RDP 回落外部客户端；但正式发布应带内嵌 RDP。
ensure_native() { # $1 = x64 | arm64
  local a="$1" osx
  case "$a" in x64) osx=x86_64;; arm64) osx=arm64;; esac
  if [ ! -d "native/rdp/freerdp-dist/$osx/include/freerdp3" ]; then
    echo "==> 自建 FreeRDP（${osx}）——首次约 5 分钟"
    native/rdp/build-freerdp.sh "$a"
  fi
  echo "==> 编译 RDP shim（${osx}）"
  native/rdp/build.sh "$a"
  [ -f "src/RemoteFlow.Protocol.Rdp.Mac/runtimes/osx-$a/native/libremoteflow_rdp.dylib" ] \
    || { echo "shim 未产出：src/.../runtimes/osx-$a/native/" >&2; exit 1; }
}

# 优先用本机 Apple Development 证书（稳定身份 → 钥匙串 ACL 跨构建不失效）；
# 没有则回落 ad-hoc（"-"）。正式发布传 RF_SIGN_IDENTITY="Developer ID Application: ..."。
# 末尾 `|| true`：CI runner 无签名身份时 grep 无匹配退出 1，配合 set -euo pipefail
# 会误杀整个脚本；这里本就是"取不到就回落 ad-hoc"，失败即空字符串。
detect_sign_id() {
  security find-identity -v -p codesigning 2>/dev/null \
    | grep -m1 -oE '"Apple Development:[^"]+"' | tr -d '"' || true
}
SIGN_ID="${RF_SIGN_IDENTITY:-$(detect_sign_id)}"
SIGN_ID="${SIGN_ID:--}"
ENTITLEMENTS="src/RemoteFlow.App.Mac/Entitlements.plist"

sign_bundle() { # 自底向上签名（先所有嵌套 Mach-O，再 bundle 本体）
  local app="$1"
  echo "   身份：$SIGN_ID"
  local ent=()
  [ "$SIGN_ID" != "-" ] && ent=(--options runtime --entitlements "$ENTITLEMENTS")
  # ${ent[@]+"${ent[@]}"}：bash 3.2（CI runner 的 /bin/bash）下 set -u 遇空数组
  # 展开 "${ent[@]}" 会报 unbound variable；ad-hoc（SIGN_ID=-）时 ent 恰为空。
  find "$app/Contents" -type f -name "*.dylib" -exec codesign --force --timestamp=none --sign "$SIGN_ID" {} \;
  codesign --force --timestamp=none ${ent[@]+"${ent[@]}"} --sign "$SIGN_ID" "$app/Contents/MacOS/RemoteFlow.App.Mac"
  codesign --force --timestamp=none ${ent[@]+"${ent[@]}"} --sign "$SIGN_ID" "$app"
  codesign --verify --verbose "$app"
}

case "$ARCH" in
  x64|arm64)
    RID="osx-$ARCH"
    ensure_native "$ARCH"
    echo "==> 发布 $RID"
    dotnet publish "$PROJ" -c Release -r "$RID" --nologo
    cp -R "$OUT/$RID/RemoteFlow.app" "$APP"
    ARCHTAG="$ARCH"
    ;;
  universal)
    ensure_native x64
    ensure_native arm64
    # FreeRDP 依赖 openssl；brew 单机通常只有本机架构那一份，universal 会做出跑不起来的
    # arm64 RDP 分支。这里预检：两份 FreeRDP dylib 的非 @rpath 依赖是否都能配对。
    for f in native/rdp/freerdp-dist/x86_64/lib/libfreerdp-client3*.dylib; do
      otool -L "$f" | awk 'NR>1{print $1}' | grep -E '^/usr/local|^/opt/homebrew' | while IFS= read -r d; do
        b="$(basename "$d")"
        alt="$(printf '%s' "$d" | sed 's#/usr/local#/opt/homebrew#; s#/opt/homebrew#/usr/local#')"
        [ -f "$d" ] && [ -f "$alt" ] || { echo "  ✗ universal 需要 $b 的两个架构，但只找到一份（${d}）。" >&2
          echo "    改用单架构发布：scripts/build-macos-release.sh x64（或 arm64）。" >&2; exit 1; }
      done
    done
    echo "==> 发布 osx-x64 / osx-arm64"
    dotnet publish "$PROJ" -c Release -r osx-x64  --nologo
    dotnet publish "$PROJ" -c Release -r osx-arm64 --nologo
    echo "==> lipo 合并为 universal（arm64 包为基底）"
    cp -R "$OUT/osx-arm64/RemoteFlow.app" "$APP"
    merge() {
      local rel="$1"
      lipo -create "$OUT/osx-arm64/RemoteFlow.app/$rel" "$OUT/osx-x64/RemoteFlow.app/$rel" \
        -output "$APP/$rel" 2>/dev/null && echo "   + $rel" || true
    }
    merge "Contents/MacOS/RemoteFlow.App.Mac"
    while IFS= read -r d; do merge "${d#"$OUT/osx-arm64/RemoteFlow.app/"}"; done \
      < <(find "$OUT/osx-arm64/RemoteFlow.app" -type f -name "*.dylib")

    # FreeRDP dylib 合出一份 universal，供随后的 bundle-freerdp.sh 收集
    UNIDIR="$(mktemp -d)/lib"; mkdir -p "$UNIDIR"
    for f in native/rdp/freerdp-dist/arm64/lib/*.dylib; do
      b="$(basename "$f")"
      lipo -create "native/rdp/freerdp-dist/arm64/lib/$b" "native/rdp/freerdp-dist/x86_64/lib/$b" \
        -output "$UNIDIR/$b" 2>/dev/null && echo "   + freerdp/$b" \
        || cp -f "$f" "$UNIDIR/$b"
    done
    export FREERDP_LIB_DIR="$UNIDIR"
    ARCHTAG="universal"
    ;;
  *)
    echo "未知架构：${ARCH}（x64 | arm64 | universal）" >&2; exit 1;;
esac

echo "==> 收 FreeRDP 依赖进 Contents/Frameworks（内嵌 RDP 脱离 brew）"
if find "$APP/Contents" -name libremoteflow_rdp.dylib | grep -q .; then
  scripts/bundle-freerdp.sh "$APP"
else
  echo "   （未见 libremoteflow_rdp.dylib，跳过——RDP 将回落外部客户端）"
fi

echo "==> ad-hoc 签名（开发用；正式发布替换为 Developer ID）"
sign_bundle "$APP"

echo "==> 校验：主可执行文件"
file "$APP/Contents/MacOS/RemoteFlow.App.Mac"

echo "==> 校验：所有 Mach-O 的架构一致"
want="$([ "$ARCHTAG" = universal ] && echo 'x86_64 arm64' || ([ "$ARCHTAG" = x64 ] && echo x86_64 || echo arm64))"
bad=0
while IFS= read -r m; do
  got="$(lipo -archs "$m" 2>/dev/null | tr ' ' '\n' | sort | tr '\n' ' ' | sed 's/ $//')"
  exp="$(printf '%s' "$want" | tr ' ' '\n' | sort | tr '\n' ' ' | sed 's/ $//')"
  [ "$got" = "$exp" ] || { echo "  ✗ $(basename "$m")：架构 [$got]，期望 [$exp]"; bad=1; }
done < <(find "$APP/Contents" -type f \( -name '*.dylib' -o -path '*/MacOS/*' \) )
[ "$bad" -eq 0 ] && echo "  ✓ 架构一致（${want}）" || { echo "架构不一致，终止" >&2; exit 1; }

DMG="$DIST/RemoteFlow-v${VERSION}-macos-${ARCHTAG}.dmg"
echo "==> 打 dmg：$DMG"
STAGE="$(mktemp -d)"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "RemoteFlow $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
rm -rf "$STAGE"

echo
echo "完成： $APP  /  $DMG"
cat <<'NOTARIZE'

── 正式发布（需 Apple「Developer ID Application」证书；本机现有的是「Apple Development」）─
# 1) 在 Apple Developer 后台创建 Developer ID Application 证书并下载到钥匙串
# 2) 存一次公证凭据（Team ID 4HFW56UM65）：
xcrun notarytool store-credentials rf-notary \
  --apple-id <apple-id> --team-id 4HFW56UM65 --password <App 专用密码>
# 3) 重签 + 公证 + 装订：
RF_SIGN_IDENTITY="Developer ID Application: <名字> (4HFW56UM65)" \
  scripts/build-macos-release.sh x64
xcrun notarytool submit dist/RemoteFlow-v*-macos-x64.dmg --keychain-profile rf-notary --wait
xcrun stapler staple dist/RemoteFlow.app
xcrun stapler staple dist/RemoteFlow-v*-macos-x64.dmg
────────────────────────────────────────────────────────────
NOTARIZE
