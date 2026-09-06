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
VERSION="$(sed -n 's/.*<ApplicationDisplayVersion>\(.*\)<\/ApplicationDisplayVersion>.*/\1/p' "$PROJ")"
[ -n "$VERSION" ] || VERSION="0.1.0"

OUT="src/RemoteFlow.App.Mac/bin/Release/net10.0-macos"
DIST="dist"
APP="$DIST/RemoteFlow.app"

echo "==> 清理"
rm -rf "$DIST" "$OUT"
mkdir -p "$DIST"

# 优先用本机 Apple Development 证书（稳定身份 → 钥匙串 ACL 跨构建不失效）；
# 没有则回落 ad-hoc（"-"）。正式发布传 RF_SIGN_IDENTITY="Developer ID Application: ..."。
SIGN_ID="${RF_SIGN_IDENTITY:-$(security find-identity -v -p codesigning 2>/dev/null \
  | grep -m1 -oE '"Apple Development:[^"]+"' | tr -d '"')}"
SIGN_ID="${SIGN_ID:--}"
ENTITLEMENTS="src/RemoteFlow.App.Mac/Entitlements.plist"

sign_bundle() { # 自底向上签名（先所有嵌套 Mach-O，再 bundle 本体）
  local app="$1"
  echo "   身份：$SIGN_ID"
  local ent=()
  [ "$SIGN_ID" != "-" ] && ent=(--options runtime --entitlements "$ENTITLEMENTS")
  find "$app/Contents" -type f -name "*.dylib" -exec codesign --force --timestamp=none --sign "$SIGN_ID" {} \;
  codesign --force --timestamp=none "${ent[@]}" --sign "$SIGN_ID" "$app/Contents/MacOS/RemoteFlow.App.Mac"
  codesign --force --timestamp=none "${ent[@]}" --sign "$SIGN_ID" "$app"
  codesign --verify --verbose "$app"
}

case "$ARCH" in
  x64|arm64)
    RID="osx-$ARCH"
    echo "==> 发布 $RID"
    dotnet publish "$PROJ" -c Release -r "$RID" --nologo
    cp -R "$OUT/$RID/RemoteFlow.app" "$APP"
    ARCHTAG="$ARCH"
    ;;
  universal)
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
    ARCHTAG="universal"
    ;;
  *)
    echo "未知架构：$ARCH（x64 | arm64 | universal）" >&2; exit 1;;
esac

echo "==> ad-hoc 签名（开发用；正式发布替换为 Developer ID）"
sign_bundle "$APP"

echo "==> 校验"
file "$APP/Contents/MacOS/RemoteFlow.App.Mac"

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
