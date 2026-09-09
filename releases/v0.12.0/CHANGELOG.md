# RemoteFlow v0.12.0

发布日期：2026-09-10

上一正式版本为 v0.11.3（Windows）。本版是**多设备一致性的两个结构改进**：
「清除本地数据并从云端恢复」兜底 + 元数据冲突 Last-Writer-Wins 自动解决。
macOS 侧同批走 `macos-v0.6.0`，详见 [`releases/macos-v0.6.0/CHANGELOG.md`](../macos-v0.6.0/CHANGELOG.md)。

## Added

### 「清除本地数据并从云端恢复」（云同步 → 账号卡片，双端）

以云端为权威的兜底通道。新增 `ICloudSyncService.RestoreFromCloudAsync`：

- 新增 `LocalDataWiper`：事务性清空本机业务数据（连接 / 凭据及 Secret / 分组 / 标签 / 连接历史）
  + 四张同步表（`sync_outbox` / `sync_state` / `sync_entity_state` / `sync_conflict`）。
- 凭据的 Secret / 私钥引用先经平台密钥库（Windows DPAPI / macOS Keychain）删除，不写进 SQLite。
- 系统分组「未分组」保留（本机概念、不参与同步）；SSH 主机密钥是本地安全数据，不在清除范围。
- 保留云会话、设备身份、`CloudDeviceId`（不回退 v0.11.2 修掉的设备重复注册）。
- 清空后 `sync_state` 行删除 → 游标归 0 → 随后的整轮同步把云端作为权威完整拉取。
- UI：账号卡片新增危险按钮（WPF + AppKit），二次确认（警示色）后执行并刷新。
- 与「清除此设备云数据」（保留本地业务数据、清同步状态）语义相反，互相配合。

## Changed

### 元数据冲突 Last-Writer-Wins 自动解决

目标：同一条 `connection` / `credential` **元数据**在两台设备都被改过时不再弹窗打断，
按「最后一笔」自动收敛；密码 / 私钥绝不平滑覆盖。

- `ISyncEntitySource` 新增默认接口方法 `ReadContentModifiedAt(byte[], int)`：从 Payload 读内容
  最后修改时间（LWW 依据）；不支持的类型默认返回 null。
  - `ConnectionSyncSource` / `CredentialSyncSource` 已实现（读各自的 `UpdatedAt`）。
- `SyncCoordinator` **push-conflict** 分支：
  - 本地较新（`local.UpdatedAt > 云端快照.UpdatedAt`）→ **LocalWins**：以服务端当前版本为
    基线、新 OperationId 重推覆盖；若重推又竞态失败，取最新快照继续，最多两轮后采纳最新。
  - 云端较新 → **RemoteWins**：采纳云端快照（`source.ApplyAsync`）、丢弃本地较早一笔、
    更新 `server_version` / 内容哈希 / 清冲突态，并删除本地待推 Outbox。
  - 两笔相同 → 视作云端较新（RemoteWins），收敛到已上云的一份。
- `SyncCoordinator` **pull 撞上本地待推**：同样的 LWW —— 本地较新则保留本地与 Outbox
  （下一次 push 用 push 侧 LWW 顶上去）；云端较新则采纳云端并清掉本地待推。
- **保守回退（仍弹用户对话框）**：credential-secret（密码 / 私钥）；组 / 标签（尚无内容时间戳，
  本版不引入其 UpdatedAt 列）；删除类冲突；无法解密；两笔时间戳缺任一。

## Notes

- **已知边界**：两台机器在启用云同步之前各自手工建过同一台服务器 → 两条**不同 GUID** 的
  重复记录（引擎按 ID 认同一条，无法自行判断相等）。这类「首次加入时按
  host+port+protocol+name 认领云端 ID 自动合并」尚未实现（下一步 v0.13）。
  现阶段：
  - 以某一台为准：在要丢弃的那台上用「清除本地数据并从云端恢复」，得到另一台的完整副本；
  - 同 ID 的并发修改（两台各自编辑了已同步的同一条连接 / 凭据）：本版已自动取最新，不弹窗。
- `RemoteFlow.slnx`、App.Mac 的 AppDelegate / App.xaml.cs 组合根注册 `LocalDataWiper`（DI）。

## Verification

- Build：`dotnet build RemoteFlow.slnx -c Release --nologo` → 0 错误（1 处 pre-existing 测试工程 `CS0067` 警告）。
- Tests：`dotnet test -c Release` → Core.Tests 42 / 42；IntegrationTests 164 / 164
  （+`SyncLwwTests` 3：connection 双改较新者赢且无对话框 / 本地较新重推赢 / 密码双改仍留冲突；
  +`SyncRestoreFromCloudTests`：wipe 清空 + 重拉恢复云端副本、丢弃本地独有；+VM restore 命令测试）；
  IntegrationTests.Windows 9 / 9；IntegrationTests.Mac 10 skipped。
- 端到端：`APPSCLOUD_BASE_URL=https://sync.appscloud.cn/` 下 `CloudRoundTripTests` /
  `CloudSyncFacadeTests` 3 / 3 通过。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。
- Platform / Architecture：Windows 11 · win-x64（.NET 10 self-contained single-file）。
- Schema：无本地 SQLite schema 变更，无服务端变更。

## Artifacts

由 `.github/workflows/release-windows.yml` 在 `v0.12.0` tag 推送后构建并上传到 GitHub Release：

- `RemoteFlow-v0.12.0-win-x64.exe`
  - Size：74,849,991 bytes
  - SHA-256：`B4E51B21CCF4347CD47369358C10FF9DA43F8E2981A9ED2F67034396D53C191B`
- `RemoteFlow-v0.12.0-win-x64.zip`（含 `Assets/Terminal/`）
  - Size：69,376,764 bytes
  - SHA-256：`A967AB289965E3B157656C72E436E72792B3B55B8DBCC7150E33B63AE54D338C`

## Git

- Source Snapshot：`release: RemoteFlow v0.12.0`，`source/` = HEAD 树（排除 `releases/`），
  含 restore 兜底 + LWW + 版本号提升（Win 0.12.0 / Mac 0.6.0）。
- 提交范围 `v0.11.3..`：`4cfd9c0` feat(cloud) restore 兜底 · `afce27c` feat(cloud) LWW ·
  `adfd56a` chore 版本号 0.12.0 · `chore(release)` macOS 0.6.0 · 快照封版。
- Tag：`v0.12.0`。分支：`main`。
