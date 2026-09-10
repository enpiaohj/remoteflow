# RemoteFlow macOS v0.7.1

发布日期：2026-09-10 · 上一版本 macos-v0.7.0

纯安全修复：收紧同步的删除语义（共享层，与 Windows v0.13.1 同批）。

## 修复

- 删除只由显式记录传播，绝不从「本地不存在」推断：对账不再把「云端有、本地已无」自动补成
  `Delete`（避免误删被扩散到云端与所有设备），只记 Warning；删除的唯一传播途径仍是服务层
  删除时登记的 Outbox 墓碑，受 LWW 与冲突保护。

## 验证

- CI 三 job 全绿；`dotnet test -c Release` —— Core.Tests 42、IntegrationTests 168、
  IntegrationTests.Windows 9 全绿；IntegrationTests.Mac 在非 macOS 上 skip。
- 本版未做 macOS 实机走查。
- 平台：macOS 13+ · 架构：arm64 + x64（分别打包）

## Git

- Tag：`macos-v0.7.1` · 提交范围 `macos-v0.7.0..`
- 共享层改动同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.13.1]`）。

## 产物

- `RemoteFlow-v0.7.1-macos-arm64.dmg` —— Size / SHA-256：见发布后补录
- `RemoteFlow-v0.7.1-macos-x64.dmg` —— Size / SHA-256：见发布后补录
