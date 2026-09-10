# RemoteFlow macOS v0.7.0

发布日期：2026-09-10 · 上一版本 macos-v0.6.0

客户端资产上报 + 冲突可读化 + 关联清理（共享层，与 Windows v0.13.0 同批）。无破坏性变更。

---

## 安装

DMG 拖入「应用程序」；首次启动右键「打开」，或 `xattr -dr com.apple.quarantine /Applications/RemoteFlow.app`。

---

## 新增

- **上报本机基础系统信息（资产信息）**：登录 / 恢复会话后自动上报——主机名、操作系统(名称/版本/架构)、
  CPU(型号/物理核/逻辑核/主频)、内存(总量/可用)、磁盘(卷明细 + 汇总)、主 IPv4、当前用户、时区、
  运行时长、屏幕、客户端与 .NET 版本。管理台「设备」「用户详情」可见。
  采集尽力而为，失败降级留空，绝不影响登录与同步。
- **冲突列表显示可读名称**：连接「名称（host）」、凭据/分组/标签各自名称、密码「XX 的密码」并标注需选择。
- **同步状态新增「云端已存」汇总**（连接 N · 凭据 N · …）。

## 修复

- 删除连接时未清理 `connection_history`（最近活动）造成的孤儿记录 —— 改为事务内先删历史再删连接。
- 从未上云又删除的记录不再产生假冲突（`Delete` 且 `baseVersion=0` 直接丢弃 Outbox 条目）。

---

## 验证

- CI 三 job 全绿；`dotnet test -c Release` —— Core.Tests 42、IntegrationTests 165、IntegrationTests.Windows 9 全绿；
  IntegrationTests.Mac 在非 macOS 上 skip。
- 端到端（对 `https://sync.appscloud.cn/`）：`SystemInfoUploadTests` / `ViewerParityFixtureTests` /
  `CloudSyncFacadeTests` 全通过。
- 本版未做 macOS 实机走查。
- 平台：macOS 13+ · 架构：arm64 + x64（分别打包）

---

## Git

- Tag：`macos-v0.7.0` · 提交范围 `macos-v0.6.0..`
- 共享层改动同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.13.0]`）。

## 产物

- `RemoteFlow-v0.7.0-macos-arm64.dmg` —— Apple Silicon，ad-hoc 签名 —— Size / SHA-256：见发布后补录
- `RemoteFlow-v0.7.0-macos-x64.dmg` —— Intel（arm64 runner 交叉编译），ad-hoc 签名 —— Size / SHA-256：见发布后补录
