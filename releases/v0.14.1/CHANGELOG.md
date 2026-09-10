# RemoteFlow v0.14.1

发布日期：2026-09-10

系统信息采集修正。上一正式版本为 v0.14.0。

## Fixed

- 后台「系统」列显示不准（如「Windows 25H2 (X64)」）：
  - 版本族与 SKU 改用 EditionID 判定（不随系统语言变）+ 构建号 ≥22000 判 Windows 11
    → 「Windows 11 专业版」（Win11 的 ProductName 常误写 Windows 10 Pro）；
  - UBR 是 REG_DWORD，此前读到 null 导致构建号缺修订 → 「25H2 (26200.9445)」；
  - 架构由枚举 X64 规范化成 x64；macOS 取 kern.osproductversion，Linux 取 /etc/os-release。
  - 管理台展示改为「名称 版本 · 架构」。

## Verification

- Build：`dotnet build RemoteFlow.slnx -c Release --nologo` → 0 错误。
- Tests：`dotnet test -c Release` → Core.Tests 42；IntegrationTests 179；IntegrationTests.Windows 9。
- 端到端：真实上报后服务端存储核验 `Windows 11 专业版 / 25H2 (26200.9445) / x64`。
- Platform / Architecture：Windows 11 · win-x64。Schema：无变更。

## Artifacts

- `RemoteFlow-v0.14.1-win-x64.exe` —— Size / SHA-256：见发布后补录
- `RemoteFlow-v0.14.1-win-x64.zip` —— Size / SHA-256：见发布后补录

## Git

- 提交范围 `v0.14.0..`：`af29243` fix(assets) · 版本号 / 快照封版。
- Tag：`v0.14.1`。分支：`main`。
