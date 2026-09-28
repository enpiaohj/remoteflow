# RemoteFlow macOS v0.8.1

发布日期：2026-09-10 · 上一版本 macos-v0.8.0

系统信息采集修正（共享层，与 Windows v0.14.1 同批）。

## 修复

- 系统信息采集：Windows 用 EditionID + 构建号判定版本族与 SKU（「Windows 11 专业版」）、
  版本串含构建修订（「25H2 (26200.9445)」）、架构规范化为 x64/arm64；
  macOS 取 kern.osproductversion + kern.osversion。

## 验证

- 构建方式：**本地发布**（GitHub Actions 当时因账户计费故障全部 job 被拒，改走
  `scripts/release-local-macos.sh --backfill`，在 `macos-v0.8.1` tag 的源码树上构建）。
- 构建机：Intel（x86_64）· x64 原生编译 / arm64 交叉编译。
- 测试门禁（本机 `dotnet test -c Release`）：
  - `Core.Tests` 42 通过；
  - `IntegrationTests` 179 通过 / 5 跳过；
  - `IntegrationTests.Mac` 10 通过。
  - `IntegrationTests.Windows` 需 Windows 运行时，**未在本机执行**（由 Windows 侧覆盖）。
- 产物架构复核（挂载 DMG 后 `lipo -archs`）：arm64 包内 5 个 dylib 与主可执行均为 arm64；
  x64 包内 8 个 dylib 与主可执行均为 x86_64。
- **未做 macOS 实机走查**：arm64 包无法在 Intel 构建机上运行（交叉产出），x64 包未做运行验证。
- 平台：macOS 13+ · 架构：arm64 + x64（分别打包）

## 已知问题

- **未签名 / 未公证**：本机无 Developer ID 证书，产物为 ad-hoc / Apple Development 签名，
  其它机器首次打开会被 Gatekeeper 拦（需右键「打开」或到「系统设置 → 隐私与安全性」放行）。
- **arm64 包与 x64 包依赖不完全对等**：x64 包内含 `libopus` / `libjansson` / `libusb`，
  arm64 交叉编译时因宿主只有 x86_64 版依赖而关闭了对应编译开关（`WITH_OPUS=OFF`），
  arm64 包不含这几个库。本客户端的音频通道未启用（`WITH_MACAUDIO=OFF`），预期无功能影响。
- arm64 包未经实机验证。

## Git

- Tag：`macos-v0.8.1` · 提交范围 `macos-v0.8.0..`
- 共享层改动同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.14.1]`）。

## 产物

- `RemoteFlow-v0.8.1-macos-arm64.dmg` —— 55.5 MB · SHA-256：`ccedc188d3b1ea514b33c9ba68a4d1b1b7dac3557cd291ec70c10a3149d9888d`
- `RemoteFlow-v0.8.1-macos-x64.dmg` —— 58.6 MB · SHA-256：`e945a3426a9c23e0ac315fce74e5697eaebd7f593e43e5bc3f6b4e647cc5ccfb`
