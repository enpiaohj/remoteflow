# RemoteFlow macOS v0.7.3

发布日期：2026-09-10 · 上一版本 macos-v0.7.2

计数修正 + 危险操作防误触（共享层，与 Windows v0.13.3 同批）。

## 修复 / 变更

- 「云端已存」不再把已删除（墓碑）的条目计入。
- 清除类按钮移入折叠的「危险操作」卡片，需输入「清除」解锁，避免误点。

## 验证

- CI 三 job 全绿；`dotnet test -c Release` —— Core.Tests 42、IntegrationTests 176、
  IntegrationTests.Windows 9 全绿；IntegrationTests.Mac 在非 macOS 上 skip。
- 本版未做 macOS 实机走查。
- 平台：macOS 13+ · 架构：arm64 + x64（分别打包）

## Git

- Tag：`macos-v0.7.3` · 提交范围 `macos-v0.7.2..`
- 共享层改动同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.13.3]`）。

## 产物

- `RemoteFlow-v0.7.3-macos-arm64.dmg` —— Size / SHA-256：见发布后补录
- `RemoteFlow-v0.7.3-macos-x64.dmg` —— Size / SHA-256：见发布后补录
