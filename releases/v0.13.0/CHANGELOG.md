# RemoteFlow v0.13.0

发布日期：2026-09-10

上一正式版本为 v0.12.0（Windows）。本版：客户端资产上报 + 冲突可读化 + 关联清理。
macOS 侧同批走 `macos-v0.7.0`。

## Added

- **基础系统信息 / 资产信息上报**（`RemoteFlow.Core.Diagnostics` + `Infrastructure.Diagnostics.SystemInfoCollector`）：
  主机名、操作系统(名称/版本/架构)、CPU(型号/物理核/逻辑核/主频)、内存(总量/可用)、
  磁盘(卷明细 + 汇总)、主 IPv4、当前用户、时区、运行时长、屏幕、客户端版本、.NET 运行时版本。
  - Windows：注册表（OS 版本 / CPU 型号与主频）+ kernel32（`GlobalMemoryStatusEx` 内存、
    `GetLogicalProcessorInformationEx` 物理核、`GetSystemMetrics` 屏幕）；macOS/Linux：`sysctl` / `/proc`。
  - 采集为尽力而为，任何异常都降级留空；上报失败仅记 Warning，不影响登录 / 同步。
  - `PUT /api/v1/devices/system-info`（AppsCloud 0.12.2 起支持；管理台设备页与用户详情展示）。
- **冲突可读化**：`SyncEntityLabeler` 把实体翻成名称（连接「名称（host）」、凭据名、分组名、标签名、
  密码「XX 的密码」；已删除的明确标注）；`ICloudSyncService.GetConflictsAsync` 返回 `CloudConflictInfo`
  （含 Label / Kind，密码冲突标注「两边都改了，需你选择」）。
- **「云端已存」汇总**：`GetSyncedCountsAsync`（按 server_version>0 统计），双端同步状态卡片展示。

## Fixed

- 删除连接未清理 `connection_history`（最近活动）→ 孤儿记录。改为事务内先删历史再删连接
  （`connection_tags` 仍由外键级联）；新增仓储测试。
- **从未上云又删除的记录不再产生假冲突**：创建后、推送前删除时 Outbox 合并为 `Delete (baseVersion=0)`，
  云端没有该实体，旧逻辑会拿它去推并记一条冲突。现在「Delete 且 baseVersion=0」直接丢弃 Outbox 条目，
  删除无需传播。新增测试（静默 + 墓碑传播两条）。

## Verification

- Build：`dotnet build RemoteFlow.slnx -c Release --nologo` → 0 错误。
- Tests：`dotnet test -c Release` → Core.Tests 42 / 42；IntegrationTests 165 / 165；
  IntegrationTests.Windows 9 / 9；IntegrationTests.Mac 10 skipped。
- 端到端（对 `https://sync.appscloud.cn/`）：`ViewerParityFixtureTests`（为网页自查页造对照数据）、
  `SystemInfoUploadTests`（上报后 Windows 上断言 CPU/内存/磁盘非空）、`CloudSyncFacadeTests` 通过；
  服务端 `Devices` 表核验到真实采集值（PHJ-HOMEPC / Windows 25H2 / i7-1370… / 14 核 20 线程 / 32GB）。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。
- Platform / Architecture：Windows 11 · win-x64。
- Schema：无本地 SQLite schema 变更（服务端新增 Devices 资产列，migration `DeviceSystemInfo`）。

## Artifacts

- `RemoteFlow-v0.13.0-win-x64.exe` —— Size / SHA-256：见发布后补录
- `RemoteFlow-v0.13.0-win-x64.zip` —— Size / SHA-256：见发布后补录

## Git

- 提交范围 `v0.12.0..`：系统信息采集上报（`ce0f308`）· 冲突可读化 + 云端已存汇总（`61e8a12`）· 版本号 / 快照封版。
- Tag：`v0.13.0`。分支：`main`。
