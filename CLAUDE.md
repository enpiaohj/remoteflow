# CLAUDE.md — RemoteFlow

面向 Claude Code 的项目级规则。优先级：用户当前指令 > 本文件 > 用户级全局规则 > 默认行为。

## 产品与仓库

| 项 | 值 |
| --- | --- |
| 产品名 | RemoteFlow |
| 产品定位 | 统一远程连接工作台（Unified Remote Connection Workspace） |
| 仓库名 | `remoteflow`（lowercase-kebab-case） |
| 可见性 | Private |
| 默认分支 | `main` |
| 本地目录 | `D:\AIProjects\RemoteFlow` |
| 设计基线文档 | `docs/01-产品设计/2026-09-03-RemoteFlow产品设计文档-v1.1.md` |

## 技术基线（不可随意更改）

- 目标平台 Windows 11；开发语言 C#；Runtime **.NET 10 LTS**（`global.json` 锁定 `10.0.400`）。
- UI 为 **WPF + MVVM**，不引入其他 UI 框架。
- 协议不自研：RDP 用系统 `mstscax.dll` ActiveX；SSH 用 SSH.NET + xterm.js/WebView2；VNC 用 Community.MarcusW.VncClient。
- 本地数据库 SQLite；凭据保护 Windows DPAPI；日志 Serilog。
- 集中式包版本管理（`Directory.Packages.props`），新增依赖须说明原因并在此处/PR 记录。

## 架构约束

- 依赖方向：`UI → Application → Core ← Infrastructure / Protocol.*`。UI 不直接引用协议实现。
- `RemoteFlow.Core` 保持平台无关（`net10.0`，不引用 WPF/WinForms/Windows 专属 API）。
- View 不操作协议实现；ViewModel 不保存 Secret；`SessionManager` 统一管理会话生命周期。
- 数据库访问统一走 Repository，不散落 SQL。
- 新增协议 = 新增一个 `IConnectionProvider` 实现 + 在 `App.xaml.cs` 注册，不改动既有架构。

## 安全红线

- Secret（密码 / 私钥 / Passphrase / Token）**不得**明文写入 SQLite、日志、导出文件、异常消息、`ToString()`。
- 只有 `DpapiCredentialVault` 接触明文 Secret；`ResolvedCredential` 生命周期极短，用后 `Dispose`。
- SSH Host Key：首次记录、变化强警告，绝不静默接受。
- 连接历史只写标准化 `ConnectionErrorCode`，不落底层异常原文。

## 命名规范

- 源代码：遵循 C# / WPF 惯例（`QuotaService.cs`、`MainWindow.xaml`），不加日期版本号。
- 发布产物：`RemoteFlow-v<MAJOR.MINOR.PATCH>-win-x64.exe` / `.zip`。
- 文档：`YYYY-MM-DD-内容-v<文档版本>.<ext>`，放 `docs/`。
- 禁止 `final` / `latest` / `new` / `最终版` 等模糊后缀。

## 版本与发布

- Semantic Versioning。版本号统一在 `Directory.Build.props` 的 `VersionPrefix` / `AssemblyVersion` / `FileVersion` 维护。
- 「测试 / 编译 / 运行 / 看看效果」不等于正式发布。
- 仅当用户明确说「发布 / Release / 打版本」时，才创建 `releases/v<X.Y.Z>/`（含 `source/`、`CHANGELOG.md`、规范命名产物），并执行 `git tag vX.Y.Z`。
- **平台版本线相互独立**：Windows 与 macOS 各自按需发布，不强制同步。
  - 版本来源：Windows = `Directory.Build.props` 的 `VersionPrefix`；macOS = `RemoteFlow.App.Mac.csproj` 的 `ApplicationDisplayVersion`。
  - Tag：Windows 沿用 `v<X.Y.Z>`（历史如此）；macOS 用 `macos-v<X.Y.Z>`。
  - Release Snapshot：`releases/v<X.Y.Z>/`（Windows）/ `releases/macos-v<X.Y.Z>/`（macOS）。
  - 每个版本内部（csproj / 关于页 / CHANGELOG / Tag / Release）保持一致；跨平台不要求一致。
- `releases/` 内的大文件用 `.gitignore` 排除，改上传 GitHub Releases；`source/` 与 `CHANGELOG.md` 入库。

## Commit 规范

Conventional Commits：`feat:` `fix:` `docs:` `refactor:` `perf:` `test:` `build:` `ci:` `chore:` `release:`。
Release Commit 格式：`release: RemoteFlow vX.Y.Z`。
提交前先看 `git status` / `git diff`，不混入无关文件。

## 构建 / 测试命令（来自本项目）

```
dotnet build                                    # 全解决方案
dotnet test tests/RemoteFlow.Core.Tests         # 单元测试
dotnet run --project src/RemoteFlow.App         # 运行
dotnet publish src/RemoteFlow.App -c Release    # 发布 win-x64 单文件
```

RDP ActiveX 的运行时行为无法用单元测试覆盖，需实机验证（scratchpad 中有 Sprint 0 POC 参考）。

## 已知环境注意事项

- `MsTscAx.MsTscAx.13` 在 Windows 11 上是注册残留（类工厂返回 `CLASS_E_CLASSNOTAVAILABLE`）。
  `RdpControlLocator` 会逐版本**实际实例化**验证后再选用，切勿改回只查注册表。
- `IMsTscAxEvents` 的正确 IID 是 `{336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6}`（经实机枚举连接点确认）。
- `RemoteFlow.Application` 命名空间会遮蔽 `System.Windows.Application`，UI 层代码用完全限定名 `System.Windows.Application.Current`。
