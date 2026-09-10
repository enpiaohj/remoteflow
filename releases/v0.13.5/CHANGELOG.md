# RemoteFlow v0.13.5

发布日期：2026-09-10

同步状态自愈（计数修正的补完）。上一正式版本为 v0.13.4。

## Fixed

- 删除过的条目仍被计入「云端已存」（标签显示 6 而实际只剩 3）：v0.13.4 只对以后发生的删除
  清状态行，修复前已删除实体的状态行仍在。现在对账会清理「云端有、本地已无」实体的本地同步
  状态行 —— 只忘记版本、不产生上行操作，不传播删除；统计随下一次对账自愈。

## Verification

- Build：`dotnet build RemoteFlow.slnx -c Release --nologo` → 0 错误。
- Tests：`dotnet test -c Release` → Core.Tests 42；IntegrationTests 177；IntegrationTests.Windows 9。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。
- Platform / Architecture：Windows 11 · win-x64。Schema：无变更。

## Artifacts

- `RemoteFlow-v0.13.5-win-x64.exe`
  - Size：74,869,924 bytes
  - SHA-256：`D189955EA695297F574DEEC2B90C5A2E69DE8B4C23DE2516DADF2634879767CF`
- `RemoteFlow-v0.13.5-win-x64.zip`
  - Size：69,396,682 bytes
  - SHA-256：`041FE524A7DDB3C75BE53630728C3FDC38502B74585EE7898E9F8F451F9C23EB`

## Git

- 提交范围 `v0.13.4..`：`719aa8b` fix(cloud) · 版本号 / 快照封版。
- Tag：`v0.13.5`。分支：`main`。
