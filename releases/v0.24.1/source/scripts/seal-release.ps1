<#
.SYNOPSIS
  封版 RemoteFlow v<X.Y.Z>：完整回归 → 生成发布产物 → 创建不可变快照 → Commit → Tag。
.DESCRIPTION
  仅在用户明确「测试通过、封版」后运行。
  会执行 clean/build/test/publish 全套回归，任一失败即中止，不会打 Tag。
.EXAMPLE
  pwsh scripts/seal-release.ps1 -Version 0.1.0
#>
param(
  [Parameter(Mandatory)] [string] $Version
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

$tag = "v$Version"
$rel = "releases/$tag"
$pub = "src/RemoteFlow.App/bin/Release/net10.0-windows/win-x64/publish"

Write-Host "== 1/8 检查工作树干净 ==" -ForegroundColor Cyan
if (git status --porcelain) { throw "工作树有未提交改动，请先提交或清理。" }
if (git tag -l $tag)        { throw "$tag 已存在。" }

Write-Host "== 2/8 Directory.Build.props 版本号 ==" -ForegroundColor Cyan
$props = Get-Content Directory.Build.props -Raw
if ($props -notmatch "<VersionPrefix>$([regex]::Escape($Version))</VersionPrefix>") {
  throw "Directory.Build.props 的 VersionPrefix 不是 $Version，请先统一。"
}

Write-Host "== 3/8 Clean ==" -ForegroundColor Cyan
dotnet clean -v q --nologo | Out-Null
Get-ChildItem src,tests -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force -EA SilentlyContinue

Write-Host "== 4/8 Build (Release) ==" -ForegroundColor Cyan
dotnet build -c Release -v q --nologo
if ($LASTEXITCODE) { throw "Release 构建失败。" }

Write-Host "== 5/8 Test ==" -ForegroundColor Cyan
dotnet test --nologo -v q
if ($LASTEXITCODE) { throw "测试未全部通过。" }

Write-Host "== 6/8 Publish (win-x64 self-contained single file) ==" -ForegroundColor Cyan
dotnet publish src/RemoteFlow.App -c Release -v q --nologo
if ($LASTEXITCODE) { throw "发布失败。" }

Write-Host "== 7/8 创建 Release Snapshot ==" -ForegroundColor Cyan
New-Item -ItemType Directory -Force "$rel/source" | Out-Null
# 源码快照 = 当前 HEAD 的精确跟踪状态；显式排除 releases/ 防止快照里再套一层
# 历史发布目录（递归嵌套），源码快照只放源码。
git archive --format=tar HEAD -- ':(exclude)releases' | tar -x -C "$rel/source"
# 规范命名的 Binary Artifact（本地磁盘保留，.gitignore 已排除大文件，改上传 GitHub Releases）
Copy-Item "$pub/RemoteFlow.exe" "$rel/RemoteFlow-$tag-win-x64.exe"
Compress-Archive -Path "$pub/*" -DestinationPath "$rel/RemoteFlow-$tag-win-x64.zip" -Force -CompressionLevel Optimal

$exeHash = (Get-FileHash "$rel/RemoteFlow-$tag-win-x64.exe" -Algorithm SHA256).Hash
$zipHash = (Get-FileHash "$rel/RemoteFlow-$tag-win-x64.zip" -Algorithm SHA256).Hash
$exeMB   = [math]::Round((Get-Item "$rel/RemoteFlow-$tag-win-x64.exe").Length/1MB, 2)

Write-Host "  exe  $exeMB MB  SHA256 $exeHash"
Write-Host "  zip             SHA256 $zipHash"
Write-Host ""
Write-Host "  >>> 现在请手工编辑 $rel/CHANGELOG.md（基于真实改动，勿凭空编造），"
Write-Host "      填入版本概述 / Added-Changed-Fixed / Known Issues / Verification / Git。"
Write-Host "      exe/zip 的 SHA256 见上。完成后回来按回车继续 Commit + Tag。"
Read-Host "  按回车继续"

if (-not (Test-Path "$rel/CHANGELOG.md")) { throw "$rel/CHANGELOG.md 不存在，已中止（不打 Tag）。" }

Write-Host "== 8/8 Commit + Tag ==" -ForegroundColor Cyan
# source/ 内的构建产物清理
Get-ChildItem "$rel/source" -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force -EA SilentlyContinue
git add "$rel/source" "$rel/CHANGELOG.md"
git commit -m "release: RemoteFlow $tag"
git tag -a $tag -m "RemoteFlow $tag"

Write-Host ""
Write-Host "封版完成：" -ForegroundColor Green
git log --oneline -1
Write-Host "Tag $tag -> $(git rev-parse "$tag^{commit}")"
Write-Host "本地发布产物：$rel/RemoteFlow-$tag-win-x64.exe / .zip（未入库，用于快速回退 / 上传 GitHub Releases）"
