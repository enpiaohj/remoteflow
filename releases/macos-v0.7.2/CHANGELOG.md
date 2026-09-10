# RemoteFlow macOS v0.7.2

发布日期：2026-09-10 · 上一版本 macos-v0.7.1

修复「永远显示已同步却不同步」+ 冲突标签取云端名称（共享层，与 Windows v0.13.2 同批）。

## 修复

- 服务端 Vault 被重建（账号清空 / 重新初始化）后，本地旧同步指针导致假「已同步」：
  新增 `VaultSwitchDetector`，解锁时比对 VaultId，不一致即清空同步表并重新全量同步。
- 冲突里本机已删除的实体改为解密云端密文显示名称，并写明「保留本机 / 使用云端」的处置后果。

## 验证

- CI 三 job 全绿；`dotnet test -c Release` —— Core.Tests 42、IntegrationTests 175、
  IntegrationTests.Windows 9 全绿；IntegrationTests.Mac 在非 macOS 上 skip。
- 本版未做 macOS 实机走查。
- 平台：macOS 13+ · 架构：arm64 + x64（分别打包）

## Git

- Tag：`macos-v0.7.2` · 提交范围 `macos-v0.7.1..`
- 共享层改动同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.13.2]`）。

## 产物

- `RemoteFlow-v0.7.2-macos-arm64.dmg` —— Apple Silicon，ad-hoc 签名
  - Size：58,742,535 bytes
  - SHA-256：`96E8A75E8913D593ECB39969BC3D8BF34259235452A6873A0CE3F948482BED6A`
- `RemoteFlow-v0.7.2-macos-x64.dmg` —— Intel，ad-hoc 签名
  - Size：60,093,067 bytes
  - SHA-256：`D11AD76DB925F2200CCAEBC87C1CFFE99D6490D36D019C4C090E0665122F5BEF`
