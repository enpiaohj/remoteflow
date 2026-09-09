# RemoteFlow v0.11.1

发布日期：2026-09-09

上一正式版本为 v0.11.0（Windows）。本版是纯 Bug Fix：修复 v0.11.0 引入的一个
云同步阻塞性缺陷。macOS 侧同批修复走 `macos-v0.5.1`，详见
[`releases/macos-v0.5.1/CHANGELOG.md`](../macos-v0.5.1/CHANGELOG.md)。

## Fixed

- **第二台设备一直「同步出错，将自动重试」，重开也不恢复。**
  - 成因：VMK（Vault 主密钥）的本地缓存（`cloud:vault-master-key`，经平台密钥库保护）
    是**单一全局槽，不带任何账号 / Vault 归属信息**。`VaultMasterKeyService.TryUnlockAsync`
    一旦发现有缓存就无条件返回 `Unlocked`，从不与服务端核对。
  - 触发：Vault 被**重建**（从 v0.10.0 的旧「设备信封」模型升级后必须重新初始化 Vault，
    新 Vault = 新 VMK）或本设备**切换过账号**时，缓存里还是旧 VMK。
  - 后果：`SyncCoordinator` 用旧 VMK 派生实体密钥去解密拉取到的密文 → 每条都
    `CryptographicException` → `PullAsync` 返回 `SyncStatus.Error` → 面板显示
    「同步出错，将自动重试」；游标不推进、缓存不失效，**重启应用也一样**。
    唯一出路是手动「云同步 → 清除此设备云数据」再重新登录。
  - 修复：
    - 缓存 VMK 时一并写入其所属 Vault 的标识 —— `CloudVaultStatus.VaultId`
      （AppsCloud `GET /api/v1/vault/status` 响应新增 `vaultId`，需服务端 ≥ 0.11.1）。
      存储格式 `base64(vmk)|vaultId`。
    - `TryUnlockAsync`：先取 `/vault/status`。
      缓存的 `VaultTag` 与当前 `VaultId` **一致** → 复用缓存（不再取信封，快路径）；
      **不一致 / 当前无 Vault** → 清掉旧缓存，按正常流程走到「输入主口令」/「初始化」/「待批准」。
    - **离线兜底**：拿不到 `/vault/status`（网络异常）时，若有本地缓存仍直接 `Unlocked`，
      不因离线而误判缓存陈旧——保留离线解锁能力。
    - `BootstrapAsync` / `RecoverAsync` / 口令解锁三条写缓存的路径统一经 `CacheAsync` 打上 VaultId。
  - 旧格式缓存（无 `|` 分隔的 v0.11.0 缓存）读出时 `VaultTag` 为空串，必然与任何 `VaultId`
    不匹配 → 首次解锁即被清理并要求重新输入主口令，之后恢复正常。

## Changed

- `IVaultKeyStore` 移除口令派生模型下不再使用的设备私钥方法
  （`GetDevicePrivateKeyAsync` / `SetDevicePrivateKeyAsync`）；`GetCachedMasterKeyAsync`
  返回类型改为 `CachedMasterKey?`（VMK + VaultTag），`SetCachedMasterKeyAsync` 增 `vaultTag` 参数。

## Known Issues

- 依赖 AppsCloud ≥ 0.11.1（`vaultId` 字段）。已随本次一并部署到 `https://sync.appscloud.cn/`。
  对旧服务端，客户端拿不到 `vaultId` → 不缓存 VMK，每次解锁都要重输主口令（降级但不阻塞）。
- 两台物理机的完整同步 / 冲突 / 恢复人工演练封版时仍未做。
- v0.11.0 遗留的已知项（MFA、首次同步统计确认框等）不变。

## Verification

- Build：`dotnet build RemoteFlow.slnx -c Release --nologo` → 0 错误（1 处 pre-existing 测试工程 `CS0067` 警告）。
- Tests：`dotnet test -c Release` → Core.Tests 42 / 42；IntegrationTests 158 / 158
  （新增 `VaultMasterKeyServiceTests` 4 项：陈旧缓存被丢弃并可用当前 Vault 口令重新解锁 /
  归属一致时复用缓存不取信封 / 离线回退信任缓存 / 口令解锁后按当前 VaultId 打 tag）；
  IntegrationTests.Windows 9 / 9；IntegrationTests.Mac 10 skipped（Windows 上不可运行）。
- 端到端：`APPSCLOUD_BASE_URL=https://sync.appscloud.cn/`（AppsCloud 0.11.1）下
  `CloudRoundTripTests` / `CloudSyncFacadeTests` 3 / 3 通过。
- Runtime：`dotnet publish src/RemoteFlow.App -c Release -r win-x64` 单文件 exe 启动正常。
- 未执行：完整 UI 手动走查、实机 RDP / SSH / VNC 会话回归、多台真实设备演练。
- Platform / Architecture：Windows 11 · win-x64（.NET 10 self-contained single-file）。
- Schema：无本地 schema 变更。服务端仅 `VaultStatusResponse` 增字段，无 migration。

## Artifacts

由 `.github/workflows/release-windows.yml` 在 `v0.11.1` tag 推送后构建并上传到 GitHub Release：

- `RemoteFlow-v0.11.1-win-x64.exe`
  - Size：74,843,694 bytes
  - SHA-256：`595F97B264140124D2A1BFD38941CFD7AFEAA3303E447F38601B13EA7896E50D`
- `RemoteFlow-v0.11.1-win-x64.zip`（含 `Assets/Terminal/`）
  - Size：69,370,758 bytes
  - SHA-256：`684F3EA9333CBF8ECFCEA98D9CB1A81454065AD50F31F31243ACFE34E93DEC33`

## Git

- Source Snapshot：`release: RemoteFlow v0.11.1`，`source/` = 含缓存修复 + 版本号提升
  （Win 0.11.1 / Mac 0.5.1）的 HEAD 树（排除 `releases/`）。
- 提交范围 `v0.11.0..`：`c00ec53` fix(cloud) 陈旧 VMK 缓存 · `chore` 版本号 0.11.1 ·
  `chore(release)` macOS 0.5.1 · 快照封版。配套服务端 `enpiaohj/appscloud` `8de0f1c`。
- Tag：`v0.11.1`。分支：`main`。
