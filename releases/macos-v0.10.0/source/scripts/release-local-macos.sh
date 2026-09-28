#!/usr/bin/env bash
# release-local-macos.sh —— RemoteFlow macOS 版本地发布（不依赖 GitHub Actions）。
#
# 与 Windows 的 scripts/release-local.ps1 对称：GitHub Actions 不可用（故障 / 配额 /
# 计费）时的兜底通道，两个平台各自在本地完成构建与发布，互不阻塞。
#
# 负责：测试门禁 → 构建 arm64 + x64 两个 DMG → 拷进 releases/macos-v<版本>/ →
#       算 SHA-256 / 大小并回写 CHANGELOG → **本地实机验证**（宿主架构那份 DMG 必须能起来）
#       → （--publish）创建 / 发布 GitHub Release。
# 不负责：源码快照 / release 提交 / tag —— 那是 scripts/seal-release-macos.sh 的事，
#         本脚本要求它先跑完（快照 = 产物对应源码，见发布规范 §5「Snapshot 与 Binary 一致」）。
#
# 用法：
#   scripts/release-local-macos.sh                    # 版本取 csproj，只构建不发布
#   scripts/release-local-macos.sh --publish          # 构建并创建 / 发布 GitHub Release
#   scripts/release-local-macos.sh --skip-tests       # 跳过测试门禁（不推荐）
#   scripts/release-local-macos.sh --skip-run-check   # 跳过实机验证（无 GUI 会话时；不推荐）
#   scripts/release-local-macos.sh 0.8.0 --backfill   # 补发旧版本：在 tag 的源码树上构建
#
# 前置：
#   - 已跑 scripts/seal-release-macos.sh（有 release 提交 + macos-v<版本> tag + 快照）。
#   - Xcode / dotnet(含 macos workload) / gh（已登录）就绪。
#   - dotnet 常装在 ~/.dotnet 而不在登录 shell 的 PATH，本脚本自行补上。
#
# 架构说明（重要）：
#   本脚本始终产 arm64 + x64 两份独立 DMG（不融合 universal——融合后重签会破坏
#   CoreCLR VM 初始化，见 build-macos-release.sh 注释）。
#   宿主架构那一份原生编译；另一份交叉编译，需要对应架构的 OpenSSL：
#     - 本机 x86_64（Intel）   → arm64 需先源码编一份 arm64 OpenSSL
#     - 本机 arm64（Apple Silicon）→ x64 需先源码编一份 x86_64 OpenSSL
#   缺的那份由 native/rdp/build-openssl.sh 自动补（首次联网下载源码，约 2-3 分钟），
#   缓存到 ~/openssl-<arch>，之后复用。
#   宿主架构那一份用 brew 的 openssl。
#
# 签名 / 公证：
#   - 默认 ad-hoc（"_"）或本机 Apple Development 证书——仅本机可用，别的机器会被
#     Gatekeeper 拦。正式对外分发需 Developer ID Application 证书 + 公证。
#   - 设了 RF_SIGN_IDENTITY="Developer ID Application: ..." 且
#     RF_NOTARY_PROFILE=<notarytool profile> 时，本脚本会签名 → 公证 → 装订。
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"

# ── 环境：补 dotnet 与 brew 到 PATH ──────────────────────────────────────────
# dotnet 装在 ~/.dotnet 时默认不在 PATH；Intel brew=/usr/local，Apple Silicon=/opt/homebrew。
export PATH="$HOME/.dotnet:/usr/local/bin:/opt/homebrew/bin:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

command -v dotnet >/dev/null || { echo "找不到 dotnet（找过 ~/.dotnet 与 PATH）。" >&2; exit 1; }
command -v gh     >/dev/null || { echo "找不到 gh（GitHub CLI）。" >&2; exit 1; }

PROJ="src/RemoteFlow.App.Mac/RemoteFlow.App.Mac.csproj"
OPENSSL_VER="${OPENSSL_VER:-3.6.4}"   # 与 .github/workflows/release-macos.yml 的 env 保持一致

# ── 参数 ────────────────────────────────────────────────────────────────────
PUBLISH=0
SKIP_TESTS=0
BACKFILL=0
RUN_CHECK=1
VERSION=""
for arg in "$@"; do
  case "$arg" in
    --publish)         PUBLISH=1 ;;
    --skip-tests)      SKIP_TESTS=1 ;;
    --backfill)        BACKFILL=1 ;;
    --skip-run-check)  RUN_CHECK=0 ;;
    -h|--help)    sed -n '2,40p' "$0"; exit 0 ;;
    -*)           echo "未知参数：${arg}（可用：--publish --skip-tests --backfill --skip-run-check）" >&2; exit 1 ;;
    *)            VERSION="$arg" ;;
  esac
done

# 实机验证的存活观察时长（秒）。太短会把「启动慢」误判成崩溃。
RUN_CHECK_SECONDS="${RUN_CHECK_SECONDS:-8}"

CSPROJ_V="$(sed -n 's#.*<ApplicationDisplayVersion>\(.*\)</ApplicationDisplayVersion>.*#\1#p' "$PROJ")"
[ -n "$CSPROJ_V" ] || { echo "未能从 $PROJ 读到 ApplicationDisplayVersion。" >&2; exit 1; }
VERSION="${VERSION:-$CSPROJ_V}"

case "$VERSION" in
  [0-9]*.[0-9]*.[0-9]*) : ;;
  *) echo "版本号格式应为 X.Y.Z：'$VERSION'" >&2; exit 1 ;;
esac

TAG="macos-v$VERSION"
REL="$ROOT/releases/$TAG"   # 绝对路径：backfill 模式会 cd 到 worktree

echo "== 1/7 前置检查 =="
if [ "$VERSION" != "$CSPROJ_V" ] && [ "$BACKFILL" != 1 ]; then
  echo "指定版本 $VERSION 与 csproj ($CSPROJ_V) 不一致。" >&2
  echo "补发旧版本请加 --backfill（会在该 tag 的源码树上构建，而非当前工作树）。" >&2
  exit 1
fi
[ -f "$REL/CHANGELOG.md" ] || {
  echo "缺 $REL/CHANGELOG.md —— 先跑 scripts/seal-release-macos.sh 完成封版。" >&2; exit 1; }
git rev-parse -q --verify "refs/tags/$TAG" >/dev/null || {
  echo "tag $TAG 不存在 —— 先跑 scripts/seal-release-macos.sh 完成封版。" >&2; exit 1; }
echo "  版本 $VERSION · tag $TAG"

# ── 构建树：默认当前工作树；--backfill 用 tag 的临时 worktree ────────────────
BUILD_ROOT="$ROOT"
WORKTREE=""
cleanup() {
  cd "$ROOT" 2>/dev/null || true   # 先离开 worktree，否则 remove 时 cwd 悬空
  [ -n "$WORKTREE" ] && git worktree remove --force "$WORKTREE" 2>/dev/null || true
}
trap cleanup EXIT

if [ "$BACKFILL" = 1 ]; then
  echo "== 2/7 补发模式：检出 $TAG 的源码树 =="
  WORKTREE="$(mktemp -d)"
  git worktree add --detach --quiet "$WORKTREE" "$TAG"
  BUILD_ROOT="$WORKTREE"
  # native 构建缓存属 .gitignore（不进快照），软链回主工作树复用，避免重复克隆/编译 FreeRDP。
  for d in freerdp-dist freerdp-src; do
    if [ -d "$ROOT/native/rdp/$d" ]; then
      rm -rf "$BUILD_ROOT/native/rdp/$d"
      ln -s "$ROOT/native/rdp/$d" "$BUILD_ROOT/native/rdp/$d"
    fi
  done
  echo "  构建树：${BUILD_ROOT}（源码 = ${TAG}）"
  cd "$BUILD_ROOT"
else
  echo "== 2/7 校验工作树与 $TAG 的构建路径一致 =="
  DRIFT="$(git diff --stat "$TAG" -- src tests native scripts .github \
             Directory.Build.props Directory.Packages.props global.json \
             RemoteFlow.slnx .editorconfig .gitattributes)"
  if [ -n "$DRIFT" ]; then
    echo "当前工作树相对 $TAG 有构建相关改动，产物将与快照不符：" >&2
    echo "$DRIFT" >&2
    echo "先提交并重新封版，或用 --backfill 在 $TAG 的源码树上构建。" >&2
    exit 1
  fi
  echo "  一致 ✓"
fi

# ── OpenSSL 准备：返回指定架构可用的前缀（本机架构返回空 = 走 brew） ─────────
HOST_OSX="$(uname -m)"   # x86_64 | arm64
# 用法：PREFIX="$(prepare_openssl x64)"  —— 日志走 stderr，stdout 只回前缀
prepare_openssl() {
  local a="$1" osx
  case "$a" in
    x64)   osx=x86_64 ;;
    arm64) osx=arm64 ;;
  esac
  if [ "$osx" = "$HOST_OSX" ]; then
    echo ""    # 宿主架构：brew 的 openssl 即可
    return 0
  fi
  local prefix="$HOME/openssl-$osx"
  if [ ! -f "$prefix/lib/libssl.3.dylib" ]; then
    echo "==> 交叉编译 $osx OpenSSL（首次，需联网）" >&2
    native/rdp/build-openssl.sh "$a" "$OPENSSL_VER" "$prefix" >&2
  else
    echo "==> 复用已有 $osx OpenSSL：$prefix" >&2
  fi
  echo "$prefix"
}

# ── 测试门禁 ────────────────────────────────────────────────────────────────
if [ "$SKIP_TESTS" = 0 ]; then
  echo "== 3/7 测试门禁 =="
  # 逐项目跑（与 ci.yml 的分平台做法一致）：解决方案级 `dotnet test` 会连带拉起
  # tests/RemoteFlow.IntegrationTests.Windows —— 它要 WindowsDesktop 运行时，在 macOS
  # 上必然启动失败，把整个门禁误判成红。
  for p in tests/RemoteFlow.Core.Tests tests/RemoteFlow.IntegrationTests tests/RemoteFlow.IntegrationTests.Mac; do
    echo "  → $p"
    dotnet test "$p" -c Release --nologo | tail -3
  done
  echo "  注：tests/RemoteFlow.IntegrationTests.Windows 需 Windows 运行时，本机未运行（由 Windows 侧覆盖）。"
  echo "  测试通过 ✓"
else
  echo "== 3/7 测试门禁 —— 已 --skip-tests 跳过 =="
fi

# ── 构建两个架构的 DMG ───────────────────────────────────────────────────────
echo "== 4/7 构建 DMG =="
STAGE="$(mktemp -d)"
DMGS=""
for a in arm64 x64; do
  ssl="$(prepare_openssl "$a")"
  if [ -n "$ssl" ]; then
    echo "==> 构建 ${a}（OPENSSL_ROOT_DIR=${ssl}）"
    OPENSSL_ROOT_DIR="$ssl" scripts/build-macos-release.sh "$a"
  else
    echo "==> 构建 ${a}（brew openssl）"
    scripts/build-macos-release.sh "$a"
  fi
  # build-macos-release.sh 每次会 rm -rf dist，必须立即把这份 DMG 取走。
  dmg="$(ls "$BUILD_ROOT"/dist/RemoteFlow-v*-macos-$a.dmg)"
  cp "$dmg" "$STAGE/"
  DMGS="$DMGS $STAGE/$(basename "$dmg")"
done

# ── 公证 + 装订（可选，需 Developer ID + notarytool profile） ────────────────
if [ -n "${RF_NOTARY_PROFILE:-}" ]; then
  echo "==> 公证 + 装订（profile: ${RF_NOTARY_PROFILE}）"
  for dmg in $DMGS; do
    xcrun notarytool submit "$dmg" --keychain-profile "$RF_NOTARY_PROFILE" --wait
    xcrun stapler staple "$dmg"
  done
else
  echo "==> 跳过公证（未设 RF_NOTARY_PROFILE）—— ad-hoc / Apple Development 签名，"
  echo "    别的机器首次打开需右键「打开」，或到「系统设置 → 隐私与安全性」放行。"
fi

# ── 落盘到 releases/ + 回写 CHANGELOG 的 SHA-256 / 大小 ──────────────────────
echo "== 5/7 落盘 + SHA-256 =="
CHANGELOG="$REL/CHANGELOG.md"
for dmg in $DMGS; do
  name="$(basename "$dmg")"
  cp "$dmg" "$REL/"
  hash="$(shasum -a 256 "$REL/$name" | awk '{print $1}')"
  size_mb="$(awk -v b="$(stat -f%z "$REL/$name")" 'BEGIN{printf "%.1f MB", b/1048576}')"
  echo "  $name  $size_mb  $hash"
  # 幂等回写：把 CHANGELOG 里该产物的整行替换为「大小 · SHA-256」，重跑不会叠加。
  RN="$name" RS="$size_mb" RH="$hash" perl -i -pe '
    my $n = quotemeta($ENV{RN});
    s{^- `$n`.*$}{- `$ENV{RN}` —— $ENV{RS} · SHA-256：`$ENV{RH}`};
  ' "$CHANGELOG"
done

# ── 本地实机验证：宿主架构那份 DMG 必须能起来 ────────────────────────────────
# 发布前必做的一步：把产物当用户那样打开一次。只挂载宿主架构那份（另一份是交叉编译
# 产物，本机根本跑不了，只能靠 build-macos-release.sh 的 Mach-O 架构校验兜底）。
# 验证失败即中止，且发生在创建 Release 之前 —— 坏产物不会被发出去。
if [ "$RUN_CHECK" = 1 ]; then
  echo "== 6/7 本地实机验证 =="
  case "$HOST_OSX" in
    x86_64) HOST_ARCH=x64 ;;
    arm64)  HOST_ARCH=arm64 ;;
    *)      HOST_ARCH="" ;;
  esac
  HOST_DMG="$REL/RemoteFlow-v$VERSION-macos-$HOST_ARCH.dmg"
  if [ -z "$HOST_ARCH" ]; then
    echo "  跳过：未知本机架构（$HOST_OSX），无对应产物可验。" >&2
  elif [ ! -f "$HOST_DMG" ]; then
    echo "  ✗ 缺宿主架构产物 $HOST_DMG —— 产物不齐，中止。" >&2
    exit 1
  else
    echo "  挂载并启动：$(basename "$HOST_DMG")（观察 ${RUN_CHECK_SECONDS}s）"
    MP="$(mktemp -d)"
    if ! hdiutil attach "$HOST_DMG" -nobrowse -readonly -mountpoint "$MP" >/dev/null 2>&1; then
      echo "  ✗ DMG 挂载失败 —— 产物有问题，中止。" >&2
      rmdir "$MP" 2>/dev/null || true
      exit 1
    fi
    EXE="$MP/RemoteFlow.app/Contents/MacOS/RemoteFlow.App.Mac"
    APP_PID=""
    if [ -x "$EXE" ]; then
      "$EXE" >/dev/null 2>&1 &
      APP_PID=$!
      disown "$APP_PID" 2>/dev/null || true   # 移出作业表，kill 时 shell 不再打印 "Terminated"
      sleep "$RUN_CHECK_SECONDS"
    fi
    if [ -n "$APP_PID" ] && kill -0 "$APP_PID" 2>/dev/null; then
      echo "  ✓ 启动成功，存活 ${RUN_CHECK_SECONDS}s 无异常"
      kill "$APP_PID" 2>/dev/null || true
      sleep 1
      kill -9 "$APP_PID" 2>/dev/null || true
      wait "$APP_PID" 2>/dev/null || true   # 回收后台作业，免得 shell 打印 "Terminated: 15"
    else
      echo "  ✗ app 启动后即退出（或可执行文件缺失）—— 中止，不发布。" >&2
      hdiutil detach "$MP" >/dev/null 2>&1 || true
      rmdir "$MP" 2>/dev/null || true
      exit 1
    fi
    hdiutil detach "$MP" >/dev/null 2>&1 || true
    rmdir "$MP" 2>/dev/null || true
  fi
  echo "  注：交叉编译那一份本机无法运行，CHANGELOG 的 Verification 需如实标注「未实机走查」。"
else
  echo "== 6/7 本地实机验证 —— 已 --skip-run-check 跳过（未实机走查）=="
fi

if [ "$PUBLISH" != 1 ]; then
  echo "== 7/7 已构建完成（未发布）。加 --publish 可创建 GitHub Release。 =="
  exit 0
fi

# ── 创建 / 发布 GitHub Release ──────────────────────────────────────────────
echo "== 7/7 创建 GitHub Release $TAG =="
# gh release create 要求 tag 已在**远端**存在（本地 tag 不算）。封版脚本按设计不 push，
# 所以这里先明确检查并给出指引，而不是让 gh 抛一句英文错误、产物白构建一场。
if ! git ls-remote --tags origin "refs/tags/$TAG" 2>/dev/null | grep -q "refs/tags/$TAG\$"; then
  echo "  ✗ 远端还没有 tag $TAG —— 先推送 tag 再发布：" >&2
  echo "      git push origin main --follow-tags" >&2
  echo "    产物已构建并落盘在 $REL/，推完可用下面任一方式建 Release：" >&2
  echo "      a) 重跑 scripts/release-local-macos.sh --publish（会重建 DMG）" >&2
  echo "      b) 直接用已落盘的产物：" >&2
  echo "         gh release create $TAG --draft --title \"RemoteFlow macOS v$VERSION\" \\" >&2
  echo "           --notes-file $CHANGELOG \\" >&2
  echo "           $REL/RemoteFlow-v$VERSION-macos-arm64.dmg $REL/RemoteFlow-v$VERSION-macos-x64.dmg" >&2
  echo "         gh release edit $TAG --draft=false" >&2
  echo "       （Latest 归 Windows 线，脚本会自动复位到最新 vX.Y.Z；见发布规范 §1）" >&2
  exit 1
fi
if gh release view "$TAG" >/dev/null 2>&1; then
  echo "  已存在 $TAG 的 Release —— 上传 / 覆盖产物并刷新说明"
  for dmg in $DMGS; do
    gh release upload "$TAG" "$REL/$(basename "$dmg")" --clobber
  done
  gh release edit "$TAG" --notes-file "$CHANGELOG"
else
  gh release create "$TAG" --draft --title "RemoteFlow macOS v$VERSION" \
    --notes-file "$CHANGELOG" $REL/RemoteFlow-v$VERSION-macos-arm64.dmg \
    $REL/RemoteFlow-v$VERSION-macos-x64.dmg
fi
gh release edit "$TAG" --draft=false
echo "已发布：https://github.com/enpiaohj/remoteflow/releases/tag/$TAG"

# Latest 归 Windows 线所有（见发布规范 §1「Latest 标记约定」）：GitHub 只有一个 Latest 槽位，
# 两条独立版本线不能都用它，否则每发一次 macOS 就把 Windows 的徽章顶掉。故这里发布后
# 把 Latest 复位到最新的 vX.Y.Z。找不到 Windows 版本时不硬设，交人工处理。
WIN_TAG=$(gh release list --limit 200 --json tagName,isDraft,isPrerelease \
  --jq '.[] | select(.isDraft == false and .isPrerelease == false) | .tagName' 2>/dev/null \
  | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sort -V | tail -1) || WIN_TAG=""
if [ -n "$WIN_TAG" ]; then
  gh release edit "$WIN_TAG" --latest >/dev/null
  echo "  Latest 已复位到 Windows 线：$WIN_TAG"
else
  echo "  ⚠ 未找到 Windows 版本 Release，Latest 暂留在 $TAG，请手工指定。" >&2
fi
