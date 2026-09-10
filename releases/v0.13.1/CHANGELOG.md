# RemoteFlow v0.13.1

发布日期：2026-09-10

纯安全修复：收紧同步的**删除语义**。上一正式版本为 v0.13.0。

## Fixed

- **删除只由显式记录传播，绝不从「本地不存在」推断。**
  - 旧行为：`ReconcileAsync`（崩溃兜底对账）会把「sync 状态里记得、本地已不存在」的实体
    自动补一条 `Delete` 并推送 → 一次误删（本地文件缺失 / 数据库被部分还原 / 程序异常）
    会被传播到云端与所有设备，**不可逆**。
  - 新行为：对账不再补 `Delete`，仅 `LogWarning` 供排查；删除的唯一传播途径是服务层删除时
    登记的 Outbox 墓碑（`ConnectionService` / `CredentialService` / `GroupService` 的删除都走这条路），
    依旧受 LWW 与冲突保护；「删除 vs 他人修改」仍留给用户选择。
  - 取舍：极小概率（删除瞬间崩溃、Outbox 未登记）下云端会保留该条并在下次同步回到本地 ——
    相比误删扩散，这是安全的一侧。

## Verification

- Build：`dotnet build RemoteFlow.slnx -c Release --nologo` → 0 错误。
- Tests：`dotnet test -c Release` → Core.Tests 42；IntegrationTests 168（新增
  `Reconcile_does_not_propagate_a_delete_that_was_not_recorded` /
  `An_explicitly_recorded_delete_is_still_propagated`）；IntegrationTests.Windows 9；
  IntegrationTests.Mac 10 skipped。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。
- Platform / Architecture：Windows 11 · win-x64。
- Schema：无变更。

## Artifacts

- `RemoteFlow-v0.13.1-win-x64.exe` —— Size / SHA-256：见发布后补录
- `RemoteFlow-v0.13.1-win-x64.zip` —— Size / SHA-256：见发布后补录

## Git

- 提交范围 `v0.13.0..`：`f6dad29` fix(cloud) · 版本号 / 快照封版。
- Tag：`v0.13.1`。分支：`main`。
