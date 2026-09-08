# RemoteFlow

> **统一远程连接工作台 / Unified Remote Connection Workspace**
> 让 RDP、SSH、Mac/VNC 等连接进入同一个工作流。

RemoteFlow 是面向 IT 运维、系统管理员、开发与技术支持人员的 Windows 桌面客户端。
它把分散的 RDP 文件、PuTTY/终端、VNC 客户端和记录在 Excel 里的服务器清单，
收敛到一个现代化、克制、启动快、连接路径短的工作台里。

首版**不是** TeamViewer / AnyDesk 类的公网穿透远控产品，
而是解决「主机多、工具散、凭据重复配置、临时记录到处飞」的高频痛点。

---

## 功能（当前 v0.7.0）

| 能力 | 说明 |
| --- | --- |
| 多协议会话 | RDP / SSH / VNC（含 macOS 屏幕共享），统一 Tab 外壳、多会话并行 |
| 会话生命周期 | 统一状态机与资源清理，断线可见状态，重连不残留 |
| 实时状态同步 | SessionManager 唯一状态源，首页/列表/详情/托盘/状态栏随会话实时一致 |
| 连接资产管理 | 新建 / 编辑 / 复制（自动命名并打开编辑）/ 删除，多级分组、标签、收藏、连接历史 |
| 右键上下文 | 连接行与首页按页面提供 连接 / 编辑 / 测试连接 / 收藏 / 管理 或 复制 / 删除 |
| 全局搜索 | 按名称、IP、分组、标签、备注即时过滤，`Ctrl+K` 聚焦 |
| Credential Vault | 密码 / 私钥由 Windows DPAPI 加密单独保存，连接库只存引用 |
| SSH Host Key 校验 | 首次连接记录指纹，指纹变化时强警告，杜绝无感中间人 |
| SSH 终端 | xterm.js 终端：六套主题、字体/字号、粘贴安全（多行/大文本确认）、清屏、搜索 |
| 连接测试 | 统一对话框：DNS / Ping / TCP 三步诊断，失败给原因与详情 |
| 托盘控制 | 结构化右键菜单：新建连接（预选协议）、最近/活动会话、开机启动、正式退出 |
| 导入 / 导出 | 连接 CSV（不含 Secret）与凭据加密备份 `.rfbackup` |
| 深浅主题 | 默认浅色，可跟随系统 |
| 本地优先 | 不强制登录、不依赖云服务即可完整使用 |

---

## 技术栈

| 层 | 选型 |
| --- | --- |
| 平台 | Windows 11（兼容 Windows Server 管理场景） |
| Runtime | .NET 10 LTS |
| UI | WPF + MVVM（CommunityToolkit.Mvvm） |
| 本地数据库 | SQLite（Microsoft.Data.Sqlite） |
| 凭据保护 | Windows DPAPI（`System.Security.Cryptography.ProtectedData`） |
| 日志 | Serilog（结构化 + Secret 脱敏 Enricher） |
| RDP | 系统自带 `mstscax.dll` RDP Client ActiveX（不自研协议栈） |
| SSH | SSH.NET（协议层）+ WebView2 承载 xterm.js（终端渲染层） |
| VNC | Community.MarcusW.VncClient（纯托管 RFB 实现，MIT） |
| 发布 | win-x64 Self-contained 单文件 |

---

## 解决方案结构

```
RemoteFlow.slnx
├─ src/
│  ├─ RemoteFlow.Core/              # 领域模型 + 抽象接口（平台无关，net10.0）
│  ├─ RemoteFlow.Application/       # 应用服务 / Use Case（SessionManager、搜索、导入导出）
│  ├─ RemoteFlow.Infrastructure/    # SQLite 仓储、DPAPI Vault、Serilog（net10.0-windows）
│  ├─ RemoteFlow.Protocol.Rdp/      # RDP ActiveX 互操作与会话
│  ├─ RemoteFlow.Protocol.Ssh/      # SSH 协议层（协议与终端渲染分离）
│  ├─ RemoteFlow.Protocol.Vnc/      # VNC / RFB 会话与 WriteableBitmap 渲染目标
│  └─ RemoteFlow.App/               # WPF UI / Startup / Composition Root
└─ tests/
   ├─ RemoteFlow.Core.Tests/        # 领域 + 搜索 + CSV 解析单元测试
   └─ RemoteFlow.IntegrationTests/  # SQLite / Vault 集成测试
```

架构依赖方向始终为 `UI → Application → Core ← Infrastructure / Protocol.*`。
UI 不直接引用任何协议实现细节；协议 Provider 通过 `IConnectionProvider` 抽象注册，
后续新增 SFTP / Web / PowerShell 只需追加一个实现。

---

## 构建与运行

前置：Windows 11 + [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。
SSH 终端需要 [Microsoft Edge WebView2 运行时](https://developer.microsoft.com/microsoft-edge/webview2/)
（Windows 11 通常已内置）。

```powershell
# 还原 + 构建
dotnet build

# 运行
dotnet run --project src/RemoteFlow.App

# 单元测试
dotnet test tests/RemoteFlow.Core.Tests

# 发布 win-x64 单文件
dotnet publish src/RemoteFlow.App -c Release
```

应用数据默认位于 `%LOCALAPPDATA%\RemoteFlow\`：

| 文件 | 内容 |
| --- | --- |
| `remoteflow.db` | 连接、分组、标签、历史、凭据**元数据**（SQLite，WAL） |
| `vault.dat` | DPAPI 加密后的 Secret 密文，与数据库分离 |
| `settings.json` | 应用设置（原子写入） |
| `logs/` | 按天滚动的结构化日志（不含任何 Secret） |

> 备份时请同时保留 `remoteflow.db` 与 `vault.dat`；
> 后者只能在**同一 Windows 账户**下解密。

### macOS 构建

```bash
# 一次性：自建带 Display Control 通道的 FreeRDP（动态分辨率靠它），约 5 分钟
native/rdp/build-freerdp.sh          # 本机架构；或 build-freerdp.sh arm64
native/rdp/build.sh                  # 编 C ABI 封装 shim

# 构建 / 运行
dotnet build src/RemoteFlow.App.Mac
open src/RemoteFlow.App.Mac/bin/Debug/net10.0-macos/RemoteFlow.app

# 打 DMG（ad-hoc 签名）
scripts/build-macos-release.sh arm64   # 或 x64 / universal
```

---

## CI / 发布

`.github/workflows/` 三个流水线：

| 文件 | 触发 | 做什么 |
| --- | --- | --- |
| `ci.yml` | push 到 `main` / `feature/**`、PR、手动 | ubuntu 跑共享测试（主门禁）；windows / macos 各验平台 head 能否构建 + 平台专属集成测试 |
| `release-windows.yml` | tag `v<X.Y.Z>`、手动 | win-x64 单文件 → GitHub Releases 草稿 |
| `release-macos.yml` | tag `macos-v<X.Y.Z>`、手动 | arm64 DMG → GitHub Releases 草稿 |

两平台版本线相互独立：Windows 版本取 `Directory.Build.props` 的 `VersionPrefix`（tag `v*`），
macOS 版本取 `src/RemoteFlow.App.Mac/*.csproj` 的 `ApplicationDisplayVersion`（tag `macos-v*`）。

**签名 / 公证**默认不做（产物 ad-hoc 签名，仅供内部）。配好对应 Secret 后自动启用：

| Secret | 用途 |
| --- | --- |
| `MACOS_CERT_P12_BASE64` / `MACOS_CERT_PASSWORD` | Developer ID Application 证书（.p12 的 base64） |
| `MACOS_NOTARY_APPLE_ID` / `MACOS_NOTARY_TEAM_ID` / `MACOS_NOTARY_PASSWORD` | notarytool 公证凭据 |
| `WINDOWS_CERT_PFX_BASE64` / `WINDOWS_CERT_PASSWORD` | Windows 代码签名证书 |

> macOS release 目前只出 arm64（CI runner 是 Apple Silicon，Intel slice 需另一台 Intel runner
> 交叉编译 —— 见 `release-macos.yml` 文末 TODO）。

---

## 安全模型

- Secret 不明文持久化，不进普通日志，不随导出文件导出；UI 默认不显示密码。
- `Credential` 只保存 `SecretReference` 引用键；明文由 `DpapiCredentialVault` 在建立连接的瞬间解析，用后立即释放。
- 日志脱敏有两道防线：调用方不传 Secret + `SecretRedactingEnricher` 兜底遮蔽疑似敏感属性。
- SSH Host Key 首次提示、变化强警告；证书/Host Key 异常必须可见，不静默忽略。

---

## Repository Rule

| 项 | 值 |
| --- | --- |
| 产品名 | RemoteFlow |
| 仓库名 | `remoteflow`（lowercase-kebab-case） |
| 可见性 | Private（默认） |
| 默认分支 | `main` |
| 版本规范 | Semantic Versioning `vMAJOR.MINOR.PATCH` |
| Tag | `vX.Y.Z`，指向已完成 Build/Test 的 Release Commit |
| Commit | Conventional Commits（`feat:` `fix:` `docs:` `refactor:` `build:` `release:` …） |
| Artifact 策略 | 安装包/大型构建输出上传 GitHub Releases，不长期入 Git History |

---

## 版本路线

- **V0.1** — Unified Connection MVP：RDP / SSH / VNC + 资产管理 + Vault + 搜索 + 多 Tab + 历史 + CSV（本版本）
- **V0.2** — Operations Toolkit：SFTP、SSH Jump Host / Tunnel、RDP Gateway、Ping/Port Check、Web 连接、Excel 导入
- **V0.3** — Asset Workspace：主机状态、基础资产信息、批量标签、项目/客户视图
- **V1.0** — Enterprise Foundation：Team Vault、RBAC、审计、共享连接
- **V1.x** — AI Assisted Operations：自然语言搜索、命令建议、错误解释（生成与执行分离，凭据永不发送给模型）

---

## 许可

内部项目，暂未开源。
