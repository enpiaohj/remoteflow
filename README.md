# RemoteFlow

> **统一远程连接工作台 / Unified Remote Connection Workspace**
> 让 RDP、SSH、Mac/VNC 等连接进入同一个工作流。

RemoteFlow 是面向 IT 运维、系统管理员、开发与技术支持人员的 Windows 桌面客户端。
它把分散的 RDP 文件、PuTTY/终端、VNC 客户端和记录在 Excel 里的服务器清单，
收敛到一个现代化、克制、启动快、连接路径短的工作台里。

首版**不是** TeamViewer / AnyDesk 类的公网穿透远控产品，
而是解决「主机多、工具散、凭据重复配置、临时记录到处飞」的高频痛点。

---

## 功能（V0.1）

| 能力 | 说明 |
| --- | --- |
| 多协议会话 | RDP / SSH / VNC（含 macOS 屏幕共享），统一 Tab 外壳、多会话并行 |
| 连接资产管理 | 新建 / 编辑 / 复制 / 删除，分组、标签、收藏、连接历史 |
| 全局搜索 | 按名称、IP、分组、标签、备注即时过滤，`Ctrl+K` 聚焦 |
| Credential Vault | 密码 / 私钥由 Windows DPAPI 加密单独保存，连接库只存引用 |
| SSH Host Key 校验 | 首次连接记录指纹，指纹变化时强警告，杜绝无感中间人 |
| CSV 导入 / 导出 | 导出文件不含任何密码或私钥 |
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
