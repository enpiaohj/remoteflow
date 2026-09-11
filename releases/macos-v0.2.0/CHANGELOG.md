# RemoteFlow macOS v0.2.0

发布日期：2026-09-08

RemoteFlow 的**首个 macOS 原生版**。基于 `net10.0-macos` + 原生 AppKit（C#），
复用与 Windows 版同一套 .NET 后端（Core / Application / Infrastructure / Protocol.*）。

macOS 与 Windows 为相互独立的版本线，本版本号（0.2.0）不与 Windows 版对齐。

> ## ⚠ 本版未发布（Release 草稿已删除）
>
> 本版当初走 GitHub Actions 通道，只建了 Release **草稿**、从未 Publish；此后
> `macos-v0.4.0` 起改为直接发布，该草稿长期搁置。2026-09-10 经确认**删除该草稿 Release**
> （tag `macos-v0.2.0` 保留，本地快照保留）。
>
> 注意：本版双架构 DMG **只存在于那个草稿里**，本地快照目录并无产物，故删除后
> **已不可下载**。本文件保留作历史记录与源码追溯；需要可用版本请使用最新的 `macos-v*`。
> 决策记录见 `docs/04-发布/2026-09-09-RemoteFlow双平台发布规范-v1.0.md` §5.6。

## Added

- macOS 原生客户端：主窗口、会话标签栏、首页（收藏 / 最近连接）、连接、凭据、设置、连接详情。
- RDP：内嵌自建 FreeRDP 3.31.1（`BUILTIN_CHANNELS=ON`），带 Display Control 动态分辨率通道
  —— 窗口 Resize / 进入全屏时远端桌面分辨率跟随，无黑边。
- SSH：复用现有 SSH.NET + xterm.js 终端实现。
- VNC：复用现有 Community.MarcusW.VncClient 实现。
- 凭据保险库走 macOS Keychain（`KeychainCredentialVault`）。
- 明暗两套配色（`Palette`），跟随系统外观切换。
- CI（GitHub Actions）：push / PR 门禁三平台构建 + 测试；`Release · macOS` 流水线（tag `macos-v*` 触发）。

## Changed

- 共享层重构：抽出 `RemoteFlow.Presentation`（`net10.0`，平台无关的 ViewModel 与服务）；
  `RemoteFlow.Infrastructure` 拆出 `.Windows`（DPAPI）与 `.Mac`（Keychain）。
- Windows 版功能与行为不变，版本号与发布节奏独立。

## Known Issues

- 提供 **arm64（Apple Silicon）** 与 **x64（Intel）** 两个独立 DMG；不提供融合 universal 二进制
  （融合后重签会破坏 CoreCLR VM 初始化）。x64 那份在 arm64 CI runner 上交叉编译。
- **Ad-hoc 签名，未公证**：首次打开需在 Finder 里**右键 →「打开」**，或执行
  `xattr -dr com.apple.quarantine /Applications/RemoteFlow.app`。
  配置 Apple「Developer ID Application」证书 + 公证凭据后，后续版本将自动带完整签名。
- RDP 需服务端为 Windows 8 / Server 2012 及以上才支持动态分辨率；更低版本回落固定分辨率。

## Verification

- Build：GitHub Actions `Release · macOS`（arm64 原生 + x64 交叉编译两条 matrix）成功；CI 三 job（shared / windows / macos）+ actionlint 通过。
- Tests：`RemoteFlow.Core.Tests`、`RemoteFlow.IntegrationTests`（Linux）、`IntegrationTests.Windows`、`IntegrationTests.Mac` 通过。
- Runtime：macOS 端多轮实机验证（RDP 动态分辨率、VNC 显示、UI）；Windows 端本轮未做运行时冒烟。
- Platform：macOS 13+（构建 deployment target 13.0）
- Architecture：arm64 + x64（分别打包）

## Git

- Commit：e6319f6
- Tag：macos-v0.2.0
- PR：#1（feature/macos-phase1 → main，merge 17405eb）

## 产物

- **已不可得**。本版 DMG 当初只挂在 Release 草稿上，2026-09-10 随草稿一并删除（见顶部说明）。
  当时构建的是：
  - `RemoteFlow-v0.2.0-macos-arm64.dmg`（Apple Silicon，约 55 MiB，ad-hoc 签名）
  - `RemoteFlow-v0.2.0-macos-x64.dmg`（Intel，ad-hoc 签名）
