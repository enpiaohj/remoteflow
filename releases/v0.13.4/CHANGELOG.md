# RemoteFlow v0.13.4

发布日期：2026-09-10

计数修正 + 危险操作防误触。上一正式版本为 v0.13.2。

## Fixed

- 「云端已存」把已删除的条目也算进去了：统计取自 `sync_entity_state`（`server_version>0`），
  墓碑仍在表里 —— 标签实际剩 3 个却显示 6。现在删除推送成功与墓碑落地都清掉该实体的状态行。

## Changed

- 两个清除按钮移入独立「危险操作」卡片并默认折叠，需手动输入「清除」才启用（双端一致），
  卡片内写清各自后果；账号卡片只留非破坏性的「退出云账号」。

## Verification

- Build：`dotnet build RemoteFlow.slnx -c Release --nologo` → 0 错误。
- Tests：`dotnet test -c Release` → Core.Tests 42；IntegrationTests 176；IntegrationTests.Windows 9。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。
- Platform / Architecture：Windows 11 · win-x64。Schema：无变更。

## Artifacts

- `RemoteFlow-v0.13.4-win-x64.exe`
  - Size：74,869,907 bytes
  - SHA-256：`71969E1E98B711F0854A934FCD88ED2FE4E43B31CB424AC8E2786801D1E06EDF`
- `RemoteFlow-v0.13.4-win-x64.zip`
  - Size：69,396,673 bytes
  - SHA-256：`854036B6E326BEA6BC420CFD46B842E0B7CDEDD1C85E3B388623DE3FA8E4E78A`

## Git

- 提交范围 `v0.13.2..`：`2fb9a95` fix(cloud) · 版本号 / 快照封版。
- Tag：`v0.13.4`。分支：`main`。
