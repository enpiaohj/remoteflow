# RemoteFlow v0.13.2

发布日期：2026-09-10

修复「永远显示已同步、却什么都不推不拉」与冲突看不懂，外加冲突标签取云端名称。
上一正式版本为 v0.13.1。

## Fixed

- **假「已同步」**：服务端账号被清空 / Vault 重新初始化后 `VaultId` 变化，客户端仍保留旧 Vault
  的游标与各实体 `server_version` → 对账认为「都已同步」→ 客户端永不推拉。
  - 新增 `VaultSwitchDetector`：解锁后比对 `AppSettings.CloudVaultId` 与当前 `VaultId`；
    不一致即 `ResetAsync`（清 `sync_state` / `sync_entity_state` / `sync_outbox` / `sync_conflict`），
    下一次同步把本机数据作为新 Vault 的全量内容重推（同 Vault 与首次同步不重置）。
- **冲突说明看不懂**：本机已删除的实体改为解密云端密文取名称；并写明「本机已删除，云端仍存在 ——
  保留本机=仍删除；使用云端=恢复它」等处置后果。

## Verification

- Build：`dotnet build RemoteFlow.slnx -c Release --nologo` → 0 错误。
- Tests：`dotnet test -c Release` → Core.Tests 42；IntegrationTests 175；IntegrationTests.Windows 9。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。
- Platform / Architecture：Windows 11 · win-x64。Schema：本地无变更（`settings.json` 增 `CloudVaultId`）。

## Artifacts

- `RemoteFlow-v0.13.2-win-x64.exe`
  - Size：74,867,911 bytes
  - SHA-256：`4FE965E880FBF5EFC5AA8340F0605B608FE271220DB0D4E3C8B8BA41545D59CA`
- `RemoteFlow-v0.13.2-win-x64.zip`
  - Size：69,394,652 bytes
  - SHA-256：`FE056869FA27284D77F3680B07A8134F28BFB399F24571E7288917E9AAC4D9F7`

## Git

- 提交范围 `v0.13.1..`：`9a7fabf` fix(cloud) · 版本号 / 快照封版。
- Tag：`v0.13.2`。分支：`main`。
