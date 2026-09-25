<#
.SYNOPSIS
  RemoteFlow Windows 版本地发布脚本 —— Windows 版**唯一**的发布路径。

.DESCRIPTION
  GitHub Actions 已弃用（账户计费问题使 job 在启动前即被拒），Windows 产物一律本地构建。
  用与 .github/workflows/release-windows.yml 相同的命令构建 win-x64 单文件产物，
  打包 zip、算出 SHA-256、写回快照 CHANGELOG，并创建 / 发布 GitHub Release。

  发布前请按规范 §5.2 手工启动产物 exe，确认能起能退（macOS 侧已由脚本强制做这一步，
  Windows 侧暂未内建）。

  默认只构建与打包（产出到 releases/v<版本>/），加 -Publish 才动 GitHub。

.EXAMPLE
  # 只构建 + 打包（不动远端）
  ./scripts/release-local.ps1 -Version 0.14.0

.EXAMPLE
  # 构建并发布到 GitHub（草稿 → 直接发布）
  ./scripts/release-local.ps1 -Version 0.14.0 -Publish
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Publish,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
# 中文输出在 GBK 控制台会乱码，统一切到 UTF-8。
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$tag = "v$Version"
$outDir = Join-Path $root "releases\$tag"
$pubDir = Join-Path $env:TEMP "rf-publish-$Version"

if (-not (Test-Path $outDir)) { throw "快照目录不存在：$outDir（先提交 release: RemoteFlow $tag 的 source/ 快照）" }

Write-Host "[release-local] 构建 $tag ……" -ForegroundColor Cyan
if (-not $SkipTests) {
    dotnet test RemoteFlow.slnx -c Release --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "测试未通过，已中止发布。" }
}

dotnet publish src/RemoteFlow.App -c Release -r win-x64 --nologo -o $pubDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "publish 失败。" }

$exe = "RemoteFlow-$tag-win-x64.exe"
$zip = "RemoteFlow-$tag-win-x64.zip"
Copy-Item (Join-Path $pubDir 'RemoteFlow.exe') (Join-Path $outDir $exe) -Force
Compress-Archive -Path (Join-Path $pubDir '*') -DestinationPath (Join-Path $outDir $zip) -Force

# 用 .NET API 算 SHA-256：某些环境里 Get-FileHash / 模块受限不可用。
function Get-Sha256([string]$path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($path)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

$exeHash = Get-Sha256 (Join-Path $outDir $exe)
$zipHash = Get-Sha256 (Join-Path $outDir $zip)
$exeSize = (Get-Item (Join-Path $outDir $exe)).Length
$zipSize = (Get-Item (Join-Path $outDir $zip)).Length

Write-Host "[release-local] 产物：" -ForegroundColor Green
Write-Host "  $exe  $exeSize bytes  $exeHash"
Write-Host "  $zip  $zipSize bytes  $zipHash"

$changelog = Join-Path $outDir 'CHANGELOG.md'
if (Test-Path $changelog) {
    $text = Get-Content $changelog -Raw
    $text = $text -replace '(?s)- `RemoteFlow-' + [regex]::Escape($tag) + '-win-x64\.exe`.*?SHA-256：`[^`]*`', ''
    Write-Host "[release-local] 提示：请把下面的 Size / SHA-256 补进 $changelog" -ForegroundColor Yellow
}

if (-not $Publish) {
    Write-Host "[release-local] 已构建完成（未发布）。加 -Publish 可创建 GitHub Release。" -ForegroundColor Yellow
    return
}

Write-Host "[release-local] 创建 GitHub Release $tag ……" -ForegroundColor Cyan
gh release create $tag --draft --title "RemoteFlow $tag" --notes-file $changelog `
    (Join-Path $outDir $exe) (Join-Path $outDir $zip)
if ($LASTEXITCODE -ne 0) { throw "gh release create 失败。" }

# Latest 由 Windows 线持有（见发布规范 §1「Latest 标记约定」）；macOS 脚本发布后会把
# Latest 复位到这里。两条独立版本线共用 GitHub 一个 Latest 槽位，约定归 Windows。
gh release edit $tag --draft=false --latest
Write-Host "[release-local] 已发布 $tag" -ForegroundColor Green
