# RemoteFlow macOS v0.8.1

发布日期：2026-09-10 · 上一版本 macos-v0.8.0

系统信息采集修正（共享层，与 Windows v0.14.1 同批）。

## 修复

- 系统信息采集：Windows 用 EditionID + 构建号判定版本族与 SKU（「Windows 11 专业版」）、
  版本串含构建修订（「25H2 (26200.9445)」）、架构规范化为 x64/arm64；
  macOS 取 kern.osproductversion + kern.osversion。

## 验证

- CI 三 job（shared / windows / macos）—— 注意：本次发布时 GitHub Actions 存在平台故障，
  macOS 产物待 Actions 恢复后由 `release-macos.yml` 重跑补发。
- `dotnet test -c Release` —— Core.Tests 42、IntegrationTests 179、IntegrationTests.Windows 9 全绿。
- 本版未做 macOS 实机走查。
- 平台：macOS 13+ · 架构：arm64 + x64（分别打包）

## Git

- Tag：`macos-v0.8.1` · 提交范围 `macos-v0.8.0..`
- 共享层改动同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.14.1]`）。

## 产物

- `RemoteFlow-v0.8.1-macos-arm64.dmg` —— 待 Actions 恢复后补发
- `RemoteFlow-v0.8.1-macos-x64.dmg` —— 待 Actions 恢复后补发
