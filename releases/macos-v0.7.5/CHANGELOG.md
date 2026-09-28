# RemoteFlow macOS v0.7.5

发布日期：2026-09-10 · 上一版本 macos-v0.7.4

同步状态自愈（共享层，与 Windows v0.13.5 同批）。

## 修复

- 「云端已存」不再计入修复之前删除过的实体：对账会清理「云端有、本地已无」的本地同步状态行
  （只忘记版本、不传播删除）。

## 验证

- CI 三 job 全绿；`dotnet test -c Release` —— Core.Tests 42、IntegrationTests 177、
  IntegrationTests.Windows 9 全绿；IntegrationTests.Mac 在非 macOS 上 skip。
- 本版未做 macOS 实机走查。
- 平台：macOS 13+ · 架构：arm64 + x64（分别打包）

## Git

- Tag：`macos-v0.7.5` · 提交范围 `macos-v0.7.4..`
- 共享层改动同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.13.5]`）。

## 产物

- `RemoteFlow-v0.7.5-macos-arm64.dmg` —— Apple Silicon，ad-hoc 签名
  - Size：57,366,113 bytes
  - SHA-256：`7BB05C27B3408842FE82081DD1F905358D0CC9D75599613DC673F6903BFA7DFE`
- `RemoteFlow-v0.7.5-macos-x64.dmg` —— Intel，ad-hoc 签名
  - Size：60,008,920 bytes
  - SHA-256：`0D1672BE32E43F80992E746119842781A9F72F97C89DCBE0518B20FAFCCE269E`

> 本次 macOS 的 `release` 汇总 job 因 GitHub Actions 瞬时故障（启动即失败、0 个 step）未产出发布，
> 已用 workflow build job 的产物手动建立并发布同一 tag 的 Release。
