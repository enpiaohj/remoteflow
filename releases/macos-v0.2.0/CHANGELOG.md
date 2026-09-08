# RemoteFlow macOS v0.2.0

发布日期：2026-09-08

RemoteFlow 的**首个 macOS 原生版**。基于 `net10.0-macos` + 原生 AppKit（C#），
复用与 Windows 版同一套 .NET 后端（Core / Application / Infrastructure / Protocol.*）。

macOS 与 Windows 为相互独立的版本线，本版本号（0.2.0）不与 Windows 版对齐。

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

- **仅 Apple Silicon（arm64）**。Intel Mac 未覆盖（universal 包需 Intel runner 交叉编译，待办）。
- **Ad-hoc 签名，未公证**：首次打开需在 Finder 里**右键 →「打开」**，或执行
  `xattr -dr com.apple.quarantine /Applications/RemoteFlow.app`。
  配置 Apple「Developer ID Application」证书 + 公证凭据后，后续版本将自动带完整签名。
- RDP 需服务端为 Windows 8 / Server 2012 及以上才支持动态分辨率；更低版本回落固定分辨率。

## Verification

- Build：GitHub Actions `Release · macOS`（run 34180050362，arm64）成功；CI 三 job（shared / windows / macos）+ actionlint 通过。
- Tests：`RemoteFlow.Core.Tests`、`RemoteFlow.IntegrationTests`（Linux）、`IntegrationTests.Windows`、`IntegrationTests.Mac` 通过。
- Runtime：macOS 端多轮实机验证（RDP 动态分辨率、VNC 显示、UI）；Windows 端本轮未做运行时冒烟。
- Platform：macOS 13+（构建 deployment target 13.0）
- Architecture：arm64

## Git

- Commit：e6319f6
- Tag：macos-v0.2.0
- PR：#1（feature/macos-phase1 → main，merge 17405eb）

## 产物

- `RemoteFlow-v0.2.0-macos-arm64.dmg`（约 55 MiB，ad-hoc 签名）
