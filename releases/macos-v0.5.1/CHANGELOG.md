# RemoteFlow macOS v0.5.1

发布日期：2026-09-09 · 上一版本 macos-v0.5.0

修复 macos-v0.5.0 的一个云同步阻塞性缺陷。与 Windows v0.11.1 同一批修复
（共享层 `RemoteFlow.Core` / `RemoteFlow.Infrastructure`）。

无破坏性变更，直接覆盖安装。

---

## 安装

与上一版一致：DMG 拖入「应用程序」；首次启动右键「打开」，或
`xattr -dr com.apple.quarantine /Applications/RemoteFlow.app`。

---

## 修复

- **第二台设备一直「同步出错，将自动重试」，重开也不恢复**：VMK（Vault 主密钥）
  本地缓存是单一全局槽、不区分账号 / Vault。当 Vault 被重建（如从 macos-v0.4.0 升级后
  重新初始化）或切换过账号时，解锁逻辑会无条件复用旧 VMK → 每次拉取的密文都解密失败
  → 同步停在出错状态，且缓存不失效、重启也一样。
  - 现在缓存 VMK 时附带其所属 Vault 的标识（`vaultId`，需 AppsCloud ≥ 0.11.1）。
    解锁时先取 `/vault/status` 比对：不一致就清掉旧缓存、回到「输入主口令」，一致才复用。
    离线时仍信任本地缓存，不影响离线解锁。
  - 覆盖 bootstrap / Recovery Key 恢复 / 口令解锁三条路径。

## 变更

- 服务端 `GET /api/v1/vault/status` 响应新增 `vaultId` 字段（AppsCloud 0.11.1）。
- 移除 `IVaultKeyStore` 中口令派生模型不再使用的设备私钥方法。

---

## 升级说明

已卡在「同步出错」的设备：升级到本版后会自动检测到缓存陈旧，提示重新输入主口令即可恢复；
或手动「云同步 → 清除此设备云数据」后重新登录。

---

## 验证

- **构建**：CI 三 job（shared / windows / macos）全绿；macos job `dotnet build src/RemoteFlow.App.Mac` 成功。
- **测试**：`RemoteFlow.Core.Tests` 42、`RemoteFlow.IntegrationTests` 158
  （+4 `VaultMasterKeyServiceTests`）、`IntegrationTests.Windows` 9 全绿；`IntegrationTests.Mac` 在非 macOS 上 skip。
- **端到端**：`CloudRoundTripTests` / `CloudSyncFacadeTests` 对已升级的 `https://sync.appscloud.cn/`（AppsCloud 0.11.1）3/3 通过。
- **实机**：本版未做 macOS 实机走查。
- **平台**：macOS 13 及以上 · **架构**：arm64 + x64（分别打包）

---

## Git

- Tag：`macos-v0.5.1`
- 提交范围：`macos-v0.5.0..`（`fix(cloud)` 陈旧 VMK 缓存 + 版本号 / 快照封版）
- 共享层修复同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.11.1]`）。

## 产物

由 `.github/workflows/release-macos.yml` 在 `macos-v0.5.1` tag 推送后构建并上传到 GitHub Release：

- `RemoteFlow-v0.5.1-macos-arm64.dmg` —— Apple Silicon，ad-hoc 签名 —— Size / SHA-256：见发布后补录
- `RemoteFlow-v0.5.1-macos-x64.dmg` —— Intel（arm64 runner 交叉编译），ad-hoc 签名 —— Size / SHA-256：见发布后补录
