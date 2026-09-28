#!/usr/bin/env bash
# seal-release-macos.sh —— 封版 RemoteFlow macOS 版：创建不可变源码快照 → Commit → Tag。
#
# 对应 Windows 版的 scripts/seal-release.ps1，差别：
#   - 版本取自 src/RemoteFlow.App.Mac/RemoteFlow.App.Mac.csproj 的 <ApplicationDisplayVersion>
#   - Tag 用 macos-v<X.Y.Z>（Windows 是 v<X.Y.Z>）
#   - 快照目录 releases/macos-v<X.Y.Z>/
#   - 只做「可追溯的源码快照 + release 提交 + tag」这部分；构建 / 打包 / DMG 由
#     scripts/release-local-macos.sh 在本机完成（不再依赖 GitHub Actions）。
#     两条命令的顺序是：先本脚本封版，再 release-local-macos.sh 出产物 + 建 Release。
#
# 用法：
#   scripts/seal-release-macos.sh                 # 版本取自 csproj
#   scripts/seal-release-macos.sh 0.2.0           # 显式指定，需与 csproj 一致
#   scripts/seal-release-macos.sh 0.2.0 --allow-retag   # tag 已存在时允许移动（仅草稿未发布时用）
#
# 产出：releases/macos-v<X.Y.Z>/source/  + 一个 `release: RemoteFlow macos-v<X.Y.Z>` 提交
#       + 注解 tag macos-v<X.Y.Z>（指向该提交）。脚本不 push，请自行 `git push origin main --follow-tags`。
set -euo pipefail
cd "$(dirname "$0")/.."

PROJ="src/RemoteFlow.App.Mac/RemoteFlow.App.Mac.csproj"
CSPROJ_V="$(sed -n 's#.*<ApplicationDisplayVersion>\(.*\)</ApplicationDisplayVersion>.*#\1#p' "$PROJ")"
VERSION="${1:-$CSPROJ_V}"
ALLOW_RETAG=0
[ "${2:-}" = "--allow-retag" ] && ALLOW_RETAG=1
[ "${1:-}" = "--allow-retag" ] && { ALLOW_RETAG=1; VERSION="$CSPROJ_V"; }

case "$VERSION" in
  [0-9]*.[0-9]*.[0-9]*) : ;;
  *) echo "版本号格式应为 X.Y.Z：'$VERSION'" >&2; exit 1 ;;
esac

TAG="macos-v$VERSION"
REL="releases/$TAG"

echo "== 1/6 前置检查 =="
[ "$VERSION" = "$CSPROJ_V" ] || { echo "指定版本 $VERSION 与 csproj ($CSPROJ_V) 不一致，先统一。" >&2; exit 1; }
[ "$(git branch --show-current)" = "main" ] || { echo "请在 main 分支封版（当前 $(git branch --show-current)）。" >&2; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "工作树有未提交改动，先提交或清理。" >&2; git status --short; exit 1; }
if git rev-parse -q --verify "refs/tags/$TAG" >/dev/null; then
  [ "$ALLOW_RETAG" = 1 ] || { echo "$TAG 已存在。若该版本仍是未发布草稿要重封，加 --allow-retag。" >&2; exit 1; }
  echo "  （$TAG 已存在，--allow-retag：将移动到新的 release 提交）"
fi

echo "== 2/6 CHANGELOG =="
[ -f "$REL/CHANGELOG.md" ] || {
  echo "缺 $REL/CHANGELOG.md —— 先按真实改动写好（版本概述 / Added-Changed-Fixed /" >&2
  echo "Known Issues / Verification / Git），再回来封版。" >&2; exit 1; }
head -1 "$REL/CHANGELOG.md" | grep -q "RemoteFlow macOS v$VERSION" \
  || { echo "$REL/CHANGELOG.md 首行应为 '# RemoteFlow macOS v$VERSION'。" >&2; exit 1; }

echo "== 3/6 解决方案可还原（快速健全性检查）=="
dotnet restore RemoteFlow.slnx --nologo -v q

echo "== 4/6 创建源码快照 → $REL/source/ =="
rm -rf "$REL/source"
mkdir -p "$REL/source"
# 快照 = 当前 HEAD 的精确跟踪状态；排除 releases/ 防止快照里再套一层历史发布目录。
git archive --format=tar HEAD -- ':(exclude)releases' | tar -x -C "$REL/source"
SNAP_N="$(find "$REL/source" -type f | wc -l | tr -d ' ')"
echo "  $SNAP_N 个文件"
# 冗余保险：快照里不应出现构建产物。
find "$REL/source" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true

echo "== 5/6 Commit =="
git add "$REL/source" "$REL/CHANGELOG.md"
git commit -q -m "release: RemoteFlow $TAG"
echo "  $(git log --oneline -1)"

echo "== 6/6 Tag =="
git tag -d "$TAG" 2>/dev/null || true
git tag -a "$TAG" -m "RemoteFlow $TAG"
echo "  $TAG -> $(git rev-parse --short "$TAG^{commit}")"

cat <<EOF

封版完成（本地）。接着：
  scripts/release-local-macos.sh --publish   # 本机构建 arm64 + x64 DMG 并创建 / 发布 Release
  git push origin main --follow-tags         # 推 release 提交 + tag
$([ "$ALLOW_RETAG" = 1 ] && echo "  # tag 是移动的，若远端已有：git push origin main && git push origin $TAG --force")

注：GitHub Actions 已弃用（计费问题导致 job 被拒），\`.github/workflows/release-macos.yml\`
的触发器已停用、push tag 不会再触发 —— 产物一律走 scripts/release-local-macos.sh。
EOF
