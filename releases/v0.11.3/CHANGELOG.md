# RemoteFlow v0.11.3

发布日期：2026-09-10

上一正式版本为 v0.11.2（Windows）。纯 Bug Fix：修复「两台设备都显示已同步、连接数却不一致」。
macOS 侧同批走 `macos-v0.5.3`，详见 [`releases/macos-v0.5.3/CHANGELOG.md`](../macos-v0.5.3/CHANGELOG.md)。

## Fixed

### 干净设备拉取时 `connection` 落地外键失败 → 整轮同步失败、数据永不收敛

- **成因（现场日志实锤）**：
  - 首次同步时 `SyncCoordinator.ReconcileAsync` 遍历 `_sources`（注册顺序
    `[Connection, Credential, CredentialSecret, Group, Tag]`）把本地既有数据入队 Outbox。
    `connection` 排在 `credential` / `group` 之前 → Push 时先推 → 在服务端拿到**更小的 `Revision`**。
  - 一台干净设备 `GET /sync/pull` 按 `Revision` 递增返回。`connection` 先到，它引用的
    `credential` / `group` 还排在后面没落地 → `ConnectionSyncSource.ApplyAsync` →
    `SqliteConnectionRepository.AddAsync` → SQLite `FOREIGN KEY constraint failed`
    （`connections.credential_id → credentials(id)`、`connections.group_id → connection_groups(id)`）。
  - 这个 `SqliteException` **不在 v0.11.2 的 `SyncDependencyNotReadyException` 延后捕获范围内**
    → `SyncCoordinator.RunOnceAsync` 的兜底 `catch` 捕获 → `SyncStatus.Error`；游标不推进
    （异常发生在 `SetCursorAsync` 之前）→ 下一轮同一批再拉、再失败。
  - 界面可能停在**上一次成功时**的「已同步」，用户看到两台机器连接数不一致却都显示已同步。

- **修复 —— 按设计文档《RemoteFlow 同步协议、数据迁移与冲突处理设计》§6 / §12 / §17 重构 `PullAsync`**：
  - **§6「一批 Changes 全部成功应用后才能推进 Cursor」**：先把所有页的变更收进内存
    （跨页按 `(EntityType, EntityId)` 去重，保留最高 `Revision` 的那条），逐页**不再**推进游标；
    整批成功后一次性推进。
  - **§12「按依赖顺序落地」**：`DependencyRank` —— `group` / `tag` / `credential` 无依赖（rank 0）→
    `credential-secret` 依赖 `credential`（rank 1）→ `connection` 依赖 `group` + `credential`
    + `connection_tags` 依赖 `tag`（rank 2）。按 rank 再按 `Revision` 排序后应用。
  - **两轮重试**：落不下去的（`SyncDependencyNotReadyException` 或 `SqliteException` 外键失败）
    收集起来，第二轮重试 —— 跨页导致父实体在后一页的场景，第二轮父实体已落地即可通过。
  - **§17「失败隔离」**：两轮后仍失败的实体（多为引用了已被删除的实体，或密文损坏）——
    **不推进游标、标记 `Error`、保留已落地的本地数据**，`LogError` 出 `EntityType`/`EntityId`
    （不打印明文）；下一轮整批重试（已落地的实体靠 `server_version` 检查跳过），不造成永久缺口。
  - 解密失败（`CryptographicException`）仍照旧：立即 `Error`、不推进游标。

## Changed

- `SyncCoordinator.PullAsync` 由「逐页流式应用」改为「整批收集 + 排序 + 应用」。
  内存占用与本批变更数成正比（RemoteFlow 量级下可忽略）。
- `PullOutcome.DependencyDeferred` 更名 `Deferred`，捕获范围从
  `SyncDependencyNotReadyException` 扩到「`SyncDependencyNotReadyException` 或 `SqliteException`」。

## Known Issues

- 只按已知的依赖关系排序（`credential-secret`→`credential`，`connection`→`group`/`credential`/`tag`）。
  未来若新增有依赖的实体类型需同步更新 `DependencyRank`。
- 服务端数据被重置过、而本机同步指针（`sync_entity_state` / cursor）还指向旧数据时，
  需一次「云同步 → 清除此设备云数据」+ 重新登录来对齐（见「升级说明」）。
- 两台物理机的完整同步 / 冲突 / 恢复人工演练封版时仍未做。
- v0.11.0–v0.11.2 的已知项（MFA、首次同步统计确认框等）不变。

## Verification

- Build：`dotnet build RemoteFlow.slnx -c Release --nologo` → 0 错误（1 处 pre-existing 测试工程 `CS0067` 警告）。
- Tests：`dotnet test -c Release` → Core.Tests 42 / 42；IntegrationTests 159 / 159
  （`SyncDependencyOrderingTests` 重写为**真实仓储**：设备 A 在启用云同步前建 group + credential
  + 引用它们的 connection → 首次同步使 connection `Revision` 小于 credential / group → 断言这一倒挂
  → 设备 B 干净、一次 `RunOnceAsync` 即 `Synced`（非 `Error`）、connection 的 `CredentialId` /
  `GroupId` 正确落地 → 再跑一轮 `Pushed == 0`）；IntegrationTests.Windows 9 / 9；
  IntegrationTests.Mac 10 skipped。
- 端到端：`APPSCLOUD_BASE_URL=https://sync.appscloud.cn/` 下 `CloudRoundTripTests` /
  `CloudSyncFacadeTests` 3 / 3 通过。
- 未执行：完整 UI 手动走查、实机会话回归、两台物理机的同步演练。
- Platform / Architecture：Windows 11 · win-x64（.NET 10 self-contained single-file）。
- Schema：无本地 schema 变更，无服务端变更（AppsCloud 0.11.1 即可）。

## Artifacts

由 `.github/workflows/release-windows.yml` 在 `v0.11.3` tag 推送后构建并上传到 GitHub Release：

- `RemoteFlow-v0.11.3-win-x64.exe`
  - Size：74,845,142 bytes
  - SHA-256：`2CE5D10E1FEE90514D10A6EACC4A179757A9B7E18CB83DC9B148F737061C9268`
- `RemoteFlow-v0.11.3-win-x64.zip`（含 `Assets/Terminal/`）
  - Size：69,372,050 bytes
  - SHA-256：`EBBE439F2B0137ED94DC65AB8238C85119F68C1CCA543A475B707D521105CF0A`

## Git

- Source Snapshot：`release: RemoteFlow v0.11.3`，`source/` = HEAD 树（排除 `releases/`），
  含拉取重构 + 版本号提升（Win 0.11.3 / Mac 0.5.3）。
- 提交范围 `v0.11.2..`：`ba695cd` fix(cloud) · `chore` 版本号 0.11.3 · `chore(release)` macOS 0.5.3 · 快照封版。
- Tag：`v0.11.3`。分支：`main`。
