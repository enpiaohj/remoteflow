# RemoteFlow

> **统一远程连接工作台 / Unified Remote Connection Workspace**
> 让 RDP、SSH、Mac/VNC 等连接进入同一个工作流。

RemoteFlow 是面向 IT 运维、系统管理员、开发与技术支持人员的桌面客户端，
提供 **Windows 版（WPF）** 与 **macOS 原生版（AppKit）** —— 两端共享同一套领域模型与
ViewModel（`RemoteFlow.Core` + `RemoteFlow.Presentation`），各自用平台原生 UI 呈现。

它把分散的 RDP 文件、PuTTY/终端、VNC 客户端和记录在 Excel 里的服务器清单，
收敛到一个现代化、克制、启动快、连接路径短的工作台里。

**不是** TeamViewer / AnyDesk 类的公网穿透远控产品，
而是解决「主机多、工具散、凭据重复配置、临时记录到处飞」的高频痛点。

- 当前正式版：**Windows v0.9.0**（GitHub Releases · Latest）/ macOS `macos-v*` 独立版本线
- 两平台版本线相互独立，各自发布；共享层改动随下一个发布的平台带出，详见
  [`docs/04-发布/2026-09-09-RemoteFlow双平台发布规范-v1.0.md`](docs/04-发布/2026-09-09-RemoteFlow双平台发布规范-v1.0.md)

---

## 功能

| 能力 | 说明 |
| --- | --- |
| 多协议会话 | RDP / SSH / VNC（含 macOS 屏幕共享），统一 Tab 外壳、多会话并行，切 Tab 不断开 |
| 会话生命周期 | 统一状态机与资源清理，断线可见状态，重连不残留 |
| 实时状态同步 | SessionManager 唯一状态源，首页/列表/详情/托盘/状态栏随会话实时一致 |
| 会话状态灯 | 列表 / 首页图标右下角：连接中琥珀、已连接绿，空闲不显示 |
| 全屏与工具条 | 沉浸全屏 + 可拖动 / 可固定 / 自动隐藏的悬浮胶囊工具条（盖在 RDP 原生画面之上）；切换会话保持全屏，关闭会话回常驻条 |
| 连接质量详情 | 常驻条 / 药丸的状态入口打开：连接状态、会话时长、重连次数、质量指标，可重新检测 |
| RDP 工具 | 发送 `Ctrl+Alt+Del`、启动远端任务管理器（mstsc 官方远端语义动作 + 协议级回退）、缩放/适应窗口、多显示器 |
| SSH 终端 | xterm.js（WebView2 承载）：WebGL 渲染、六套主题、字体/字号、粘贴安全（多行/大文本确认）、清屏、输出内搜索、中文输入法直输 |
| VNC 画面 | 三档缩放（等比 / 拉伸 / 1:1）、内网高画质低压缩、本地零延迟光标、剪贴板远端→本机 |
| 连接资产管理 | 新建 / 编辑 / 复制（自动命名并打开编辑）/ 删除，多级分组（含受保护默认组）、跨分组标签、收藏、连接历史与迷你图 |
| 悬停就地连接 | 列表行 / 首页收藏行悬停浮出「连接」按钮，省去双击 / 走详情面板 |
| 右键上下文 | 连接行 / 首页行 / 会话 Tab 按场景提供 连接 / 切换 / 断开 / 编辑 / 测试 / 收藏 / 移动分组 / 删除 等 |
| 全局搜索 | 按名称、IP、分组、标签、备注即时过滤，`Ctrl+K` 聚焦 |
| Credential Vault | 密码 / 私钥由平台密钥库加密单独保存（Windows DPAPI / macOS Keychain），连接库只存引用 |
| SSH Host Key 校验 | 首次连接记录指纹，指纹变化时强警告，杜绝无感中间人 |
| RDP 证书 TOFU | 首次接受、变化强警告（macOS 版；Windows 版沿用系统 `mstsc` 对话框） |
| 连接测试 | 统一对话框：DNS / Ping / TCP 三步诊断，失败给原因与详情 |
| 托盘控制 | 结构化右键菜单：新建连接（预选协议）、最近/活动会话、开机启动、正式退出 |
| 导入 / 导出 | 连接 CSV（不含 Secret）、凭据加密备份 `.rfbackup`（AES-256-GCM + PBKDF2）、应用数据完整备份 |
| 设置 | 外观 / 首页时间行（一个下拉即预览）/ 日期时间格式 / 默认落地页 / RDP·SSH·VNC 默认项 / 启动与托盘 |
| 深浅主题 | 默认浅色，可跟随系统 |
| 本地优先 | 不强制登录、不依赖云服务即可完整使用 |

> 逐版本变更见 [`CHANGELOG.md`](CHANGELOG.md) 与 `releases/vX.Y.Z/CHANGELOG.md`。

---

## 技术栈

| 层 | Windows | macOS |
| --- | --- | --- |
| 平台 | Windows 11（兼容 Windows Server 管理场景） | macOS 13+ |
| Runtime | .NET 10 LTS | .NET 10 LTS（`net10.0-macos`） |
| UI | WPF + MVVM（CommunityToolkit.Mvvm） | 原生 AppKit（.NET for macOS），共用同一套 ViewModel |
| 凭据保护 | Windows DPAPI（`ProtectedData`） | macOS Keychain |
| RDP | 系统 `mstscax.dll` RDP Client ActiveX | 应用内嵌 FreeRDP（自建带 Display Control 通道）|
| VNC 光标 | 原生 `HCURSOR`（`CreateIconIndirect`）| `NSCursor` |
| 发布 | win-x64 Self-contained 单文件 | arm64 / x64 DMG（ad-hoc 或 Developer ID + 公证）|

**跨平台共用**：.NET 10 · SQLite（Microsoft.Data.Sqlite）· Serilog（结构化 + Secret 脱敏 Enricher）·
SSH.NET（SSH 协议层）· xterm.js（终端渲染，WebView2 / WKWebView 承载）·
Community.MarcusW.VncClient（纯托管 RFB，MIT）。

---

## 解决方案结构

```
RemoteFlow.slnx  （不含 RemoteFlow.App.Mac —— net10.0-macos 无法跨平台构建，作为独立叶子）
├─ src/
│  ├─ RemoteFlow.Core/                  # 领域模型 + 抽象接口（平台无关，net10.0）
│  ├─ RemoteFlow.Application/           # 应用服务 / Use Case（SessionManager、搜索、导入导出、分组）
│  ├─ RemoteFlow.Presentation/          # 平台无关的 ViewModel + 视图无关服务
│  │                                    #   （格式化、探测、对话框抽象、终端资产）—— 两端 UI 都绑这一层
│  ├─ RemoteFlow.Infrastructure/        # 跨平台基础设施基座
│  ├─ RemoteFlow.Infrastructure.Windows/ # SQLite 仓储、DPAPI Vault、开机启动（Windows）
│  ├─ RemoteFlow.Infrastructure.Mac/     # Keychain Vault、LaunchAgent（macOS）
│  ├─ RemoteFlow.Protocol.Rdp/          # RDP ActiveX 互操作与会话（Windows）
│  ├─ RemoteFlow.Protocol.Rdp.Mac/      # FreeRDP 封装与会话（macOS）
│  ├─ RemoteFlow.Protocol.Ssh/          # SSH 协议层（协议与终端渲染分离，跨平台）
│  ├─ RemoteFlow.Protocol.Vnc/          # VNC / RFB 会话与渲染目标（跨平台）
│  ├─ RemoteFlow.App/                   # WPF UI / Startup / Composition Root（Windows）
│  └─ RemoteFlow.App.Mac/               # AppKit UI / Composition Root（macOS）
├─ native/rdp/                          # FreeRDP 自建脚本 + C ABI 封装 shim（macOS RDP）
└─ tests/
   ├─ RemoteFlow.Core.Tests/            # 领域 + 搜索 + CSV 解析单元测试
   ├─ RemoteFlow.IntegrationTests/      # 共享层集成测试（SQLite / Vault / 服务 / VM）
   ├─ RemoteFlow.IntegrationTests.Windows/ # Windows 专属集成测试
   └─ RemoteFlow.IntegrationTests.Mac/     # macOS 专属集成测试
```

架构依赖方向始终为 `UI(App / App.Mac) → Presentation → Application → Core ← Infrastructure.* / Protocol.*`。
UI 只做视图构造 + 平台互操作，逻辑一律下沉到 ViewModel；改动 `Core` / `Presentation`
即同时影响两端，按 API 变更对待（加法优先，或在同一改动里把两端视图一起改到、两端 CI 都绿）。
协议 Provider 通过 `IConnectionProvider` 抽象注册，后续新增 SFTP / Web / PowerShell 只需追加一个实现。

---

## 构建与运行（Windows）

前置：Windows 11 + [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。
SSH 终端需要 [Microsoft Edge WebView2 运行时](https://developer.microsoft.com/microsoft-edge/webview2/)
（Windows 11 通常已内置）。

```powershell
# 还原 + 构建全解决方案（slnx 不含 App.Mac）
dotnet build

# 运行 Windows 版
dotnet run --project src/RemoteFlow.App

# 测试（共享 + Windows 专属；App.Mac 相关测试在 Windows 上会 skip）
dotnet test tests/RemoteFlow.Core.Tests
dotnet test tests/RemoteFlow.IntegrationTests
dotnet test tests/RemoteFlow.IntegrationTests.Windows

# 发布 win-x64 单文件
dotnet publish src/RemoteFlow.App -c Release -r win-x64
```

> `Directory.Build.props` 里 `EnableWindowsTargeting=true`：macOS 开发机也能
> `dotnet build`（slnx）做 Windows 侧编译校验，避免共享层改动把 Windows 版改坏。

应用数据默认位于 `%LOCALAPPDATA%\RemoteFlow\`（macOS 版在 `~/Library/Application Support/RemoteFlow/`）：

| 文件 | 内容 |
| --- | --- |
| `remoteflow.db` | 连接、分组、标签、历史、凭据**元数据**（SQLite，WAL） |
| `vault.dat` / Keychain | Secret 密文，与数据库分离（Windows DPAPI 文件 / macOS 钥匙串条目） |
| `settings.json` | 应用设置（原子写入） |
| `logs/` | 按天滚动的结构化日志（不含任何 Secret） |

> 备份时请同时保留 `remoteflow.db` 与 Secret 存储；后者只能在**同一账户 / 同一台机器**下解密。

## 构建与运行（macOS）

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
| `ci.yml` | push 到 `main` / `feature/**`、PR、手动 | ubuntu 跑共享测试（主门禁）；windows / macos 各验能否构建 + 平台专属集成测试。**`main` 因此永远不会「一端是坏的」** |
| `release-windows.yml` | tag `v[0-9]*`、手动 | win-x64 单文件 → build/test/publish/（签）→ 创建 GitHub Release **草稿** |
| `release-macos.yml` | tag `macos-v*`、手动 | arm64 / x64 DMG → 同上草稿 |

两平台版本线相互独立：Windows 版本取 `Directory.Build.props` 的 `VersionPrefix`（tag `v*`），
macOS 版本取 `src/RemoteFlow.App.Mac/*.csproj` 的 `ApplicationDisplayVersion`（tag `macos-v*`）。
CI 出的 Release 是**草稿**，人工审核后手动 Publish / 设 Latest。

**完整发布流程、分支纪律、共享层改动规则、草稿重来的授权边界、统一发布路线图**见
[`docs/04-发布/2026-09-09-RemoteFlow双平台发布规范-v1.0.md`](docs/04-发布/2026-09-09-RemoteFlow双平台发布规范-v1.0.md)。

**签名 / 公证**默认不做（产物 ad-hoc 签名，仅供内部）。配好对应 Secret（`MACOS_CERT_P12_BASE64` /
`MACOS_CERT_PASSWORD` / `MACOS_NOTARY_*` / `WINDOWS_CERT_PFX_BASE64` / `WINDOWS_CERT_PASSWORD`）后自动启用。

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
| 版本规范 | Semantic Versioning；Windows `vMAJOR.MINOR.PATCH`、macOS `macos-vMAJOR.MINOR.PATCH`（独立版本线）|
| Tag | 指向已完成 Build/Test 的 Release Commit；已发布 tag 不移动 / 不删除 |
| Commit | Conventional Commits（`feat:` `fix:` `docs:` `refactor:` `perf:` `build:` `ci:` `release:` …） |
| Artifact 策略 | 安装包/大型构建输出上传 GitHub Releases，不长期入 Git History；`releases/vX.Y.Z/` 只入库 `source/` + `CHANGELOG.md` |

---

## 版本路线

- **V0.1** — Unified Connection MVP：RDP / SSH / VNC + 资产管理 + Vault + 搜索 + 多 Tab + 历史 + CSV ✅
- **V0.2 – V0.9** — 分组模型、最近连接、会话生命周期与实时状态、连接质量详情、RDP 高级设置与远端任务管理器、
  协议性能优化、macOS 原生版 + 共享层重构、UI 精修（会话状态灯 / 悬停连接 / 首页时间行 / 会话工具条）✅
- **下一步（Operations Toolkit）** — SFTP、SSH Jump Host / Tunnel、RDP Gateway、Ping/Port Check、Web 连接、Excel 导入
- **Asset Workspace** — 主机状态、基础资产信息、批量标签、项目/客户视图
- **Enterprise Foundation** — Team Vault、RBAC、审计、共享连接
- **AI Assisted Operations** — 自然语言搜索、命令建议、错误解释（生成与执行分离，凭据永不发送给模型）

---

## 许可

内部项目，暂未开源。
