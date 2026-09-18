# RemoteFlow

> **统一远程连接工作台 / Unified Remote Connection Workspace**
> 让 RDP、SSH、VNC 等连接进入同一个工作流。

RemoteFlow 是面向 IT 运维、系统管理员、开发与技术支持人员的开源桌面客户端，
提供 **Windows 版（WPF）** 与 **macOS 原生版（AppKit）** —— 两端共享同一套领域模型与
ViewModel（`RemoteFlow.Core` + `RemoteFlow.Presentation`），各自用平台原生 UI 呈现。

它把分散的 RDP 文件、PuTTY/终端、VNC 客户端和记录在 Excel 里的服务器清单，
收敛到一个现代化、克制、启动快、连接路径短的工作台里。

**不是** TeamViewer / AnyDesk 类的公网穿透远控产品，
而是解决「主机多、工具散、凭据重复配置、临时记录到处飞」的高频痛点。

- 许可证：**GPL-3.0**（见 [LICENSE](LICENSE)）；第三方组件清单见
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
- 两平台版本线相互独立，按 Semantic Versioning 各自递增

---

## 功能

| 能力 | 说明 |
| --- | --- |
| 多协议会话 | RDP / SSH / VNC（含 macOS 屏幕共享），统一 Tab 外壳、多会话并行，切 Tab 不断开 |
| 三档全屏 | 常规 → 窗口最大化（保留标题栏）→ 完全全屏，F11 逐档循环；可拖动 / 可固定 / 自动隐藏的悬浮药丸工具条盖在原生画面之上 |
| 会话生命周期 | 统一状态机与资源清理，断线可见状态，重连不残留；关闭主窗口且有活动会话时先确认 |
| 实时状态同步 | SessionManager 唯一状态源，首页/列表/详情/托盘/状态栏随会话实时一致 |
| 连接质量详情 | 常驻条 / 药丸的状态入口打开：连接状态、会话时长、重连次数、质量指标，可重新检测 |
| RDP 工具 | 发送 `Ctrl+Alt+Del`、启动远端任务管理器（mstsc 官方远端语义动作 + 协议级回退）、缩放/适应窗口 |
| SSH 终端 | xterm.js（WebView2 / WKWebView 承载）：WebGL 渲染、六套主题、字体/字号、粘贴安全、清屏、输出内搜索、中文输入法直输 |
| VNC 画面 | 三档缩放（等比 / 拉伸 / 1:1）、本地零延迟光标、剪贴板远端→本机 |
| 连接资产管理 | 新建 / 编辑 / 复制 / 删除，多级分组（含受保护默认组）、跨分组标签、收藏、连接历史 |
| 全局搜索 | 按名称、IP、分组、标签、备注即时过滤，`Ctrl+K` 聚焦 |
| Credential Vault | 密码 / 私钥由平台密钥库加密单独保存（Windows DPAPI / macOS Keychain），连接库只存引用 |
| SSH Host Key 校验 | 首次连接记录指纹（TOFU），指纹变化时强警告，杜绝无感中间人 |
| 连接测试 | 统一对话框：DNS / Ping / TCP 三步诊断，失败给原因与详情 |
| 托盘控制 | 结构化右键菜单：新建连接（预选协议）、最近/活动会话、开机启动、退出 |
| 导入 / 导出 | 连接 CSV（不含 Secret）、凭据加密备份 `.rfbackup`（AES-256-GCM + PBKDF2） |
| 深浅主题 | 默认浅色，可跟随系统 |
| 本地优先 | 不强制登录、不依赖云服务即可完整使用 |

---

## 云同步（可选）

RemoteFlow 本地优先，所有功能无需联网即可使用。可选启用 **AppsCloud 云同步**
（端到端加密），在多台设备间同步连接与凭据：

- 内置的**体验服务器**为 `https://sync.appscloud.cn/`（应用默认值），开箱即可注册试用；
- 也可以自建 AppsCloud 服务端，在「设置 → 云同步 → 使用自定义服务地址」中指向自己的部署；
- 除你发起的远程连接与显式启用的云同步外，应用不向任何第三方发送数据，亦不含遥测。

### 加密与同步原理

同步内容在**客户端加密后才上传**，服务器自始至终只持有密文与元数据（ID、版本号、时间戳），
无法读取任何连接配置或凭据：

- **密钥层级**：数据密钥（VMK，256-bit 随机生成，与你的口令无函数关系）→
  由口令或 Recovery Key 经 *PBKDF2-SHA256 → KEK → AES-256-GCM* 包装成「信封」存储。
  口令只用于解开信封，**不是**加密数据的钥匙——改口令无需重加密数据，丢口令可用 Recovery Key 恢复。
- **认证与解密分离**：登录时客户端自行把口令派生为认证密钥（PBKDF2-SHA256，盐 = 账号）后再提交，
  服务器拿不到明文口令，因此**服务器自身也无法解开你的 Vault**——不存在「平台恢复」后门。
- **字段级加密**：每条实体（连接 / 凭据 / 分组 / 标签）由 HKDF-SHA256 从 VMK 派生独立密钥，
  以 AES-256-GCM 加密；Nonce 每次随机生成，并绑定实体上下文作为 AAD，防止密文被挪用或拼接。
  凭据 Secret（密码 / 私钥）本体同样以密文形式同步，服务器与中间网络均不可见。
- **本地密钥保护**：VMK 在内存中使用后立即清零；持久缓存经平台密钥库保护
  （Windows DPAPI / macOS Keychain），只在本机、本账户下可解。
- **新设备接入**：需已有设备批准，或使用 Recovery Key 解锁——没有绕过这两个入口的途径。

**同步逻辑**：本地变更先进入 Outbox 队列（只记实体引用，不含明文），推送时才从业务存储
即时读取并加密上传；按游标增量拉取。冲突采用**乐观并发 + 字段级 Last-Write-Win**：
每个实体带版本号，版本不匹配时按字段时间戳取较新值，避免整条覆盖。
各端可离线工作，联网后自动补齐。

---

## 技术栈

| 层 | Windows | macOS |
| --- | --- | --- |
| 平台 | Windows 11（兼容 Windows Server 管理场景） | macOS 13+ |
| Runtime | .NET 10 LTS | .NET 10 LTS（`net10.0-macos`） |
| UI | WPF + MVVM（CommunityToolkit.Mvvm） | 原生 AppKit（.NET for macOS），共用同一套 ViewModel |
| 凭据保护 | Windows DPAPI（`ProtectedData`） | macOS Keychain |
| RDP | 系统 `mstscax.dll` RDP Client ActiveX | 应用内嵌 FreeRDP（自建带 Display Control 通道） |
| 终端 | WebView2 + xterm.js | WKWebView + xterm.js |
| 发布形态 | win-x64 Self-contained 单文件 | arm64 / x64 DMG |

**跨平台共用**：.NET 10 · SQLite（Microsoft.Data.Sqlite）· Serilog（结构化 + Secret 脱敏 Enricher）·
SSH.NET（SSH 协议层）· xterm.js（终端渲染）· Community.MarcusW.VncClient（纯托管 RFB）。

---

## 解决方案结构

```
RemoteFlow.slnx  （不含 RemoteFlow.App.Mac —— net10.0-macos 无法跨平台构建，作为独立叶子）
├─ src/
│  ├─ RemoteFlow.Core/                   # 领域模型 + 抽象接口（平台无关，net10.0）
│  ├─ RemoteFlow.Application/            # 应用服务 / Use Case（SessionManager、搜索、导入导出、分组）
│  ├─ RemoteFlow.Presentation/           # 平台无关的 ViewModel + 视图无关服务 —— 两端 UI 都绑这一层
│  ├─ RemoteFlow.Infrastructure/         # 跨平台基础设施基座
│  ├─ RemoteFlow.Infrastructure.Windows/ # SQLite 仓储、DPAPI Vault、开机启动（Windows）
│  ├─ RemoteFlow.Infrastructure.Mac/     # Keychain Vault、LaunchAgent（macOS）
│  ├─ RemoteFlow.Protocol.Rdp/           # RDP ActiveX 互操作与会话（Windows）
│  ├─ RemoteFlow.Protocol.Rdp.Mac/       # FreeRDP 封装与会话（macOS）
│  ├─ RemoteFlow.Protocol.Ssh/           # SSH 协议层（协议与终端渲染分离，跨平台）
│  ├─ RemoteFlow.Protocol.Vnc/           # VNC / RFB 会话与渲染目标（跨平台）
│  ├─ RemoteFlow.App/                    # WPF UI / Startup / Composition Root（Windows）
│  └─ RemoteFlow.App.Mac/                # AppKit UI / Composition Root（macOS）
├─ native/rdp/                           # FreeRDP 自建脚本 + C ABI 封装 shim（macOS RDP）
└─ tests/
   ├─ RemoteFlow.Core.Tests/             # 领域 + 搜索 + CSV 解析单元测试
   ├─ RemoteFlow.IntegrationTests/       # 共享层集成测试（SQLite / Vault / 服务 / VM）
   ├─ RemoteFlow.IntegrationTests.Windows/ # Windows 专属集成测试
   └─ RemoteFlow.IntegrationTests.Mac/     # macOS 专属集成测试
```

架构依赖方向始终为 `UI(App / App.Mac) → Presentation → Application → Core ← Infrastructure.* / Protocol.*`。
UI 只做视图构造 + 平台互操作，逻辑一律下沉到 ViewModel。
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

应用数据默认位于 `%LOCALAPPDATA%\RemoteFlow\`：

| 文件 | 内容 |
| --- | --- |
| `remoteflow.db` | 连接、分组、标签、历史、凭据**元数据**（SQLite，WAL） |
| `vault.dat` | Secret 密文（Windows DPAPI），与数据库分离 |
| `settings.json` | 应用设置（原子写入） |
| `logs/` | 按天滚动的结构化日志（不含任何 Secret） |

> 备份时请同时保留 `remoteflow.db` 与 Secret 存储；后者只能在**同一账户 / 同一台机器**下解密。

## 构建与运行（macOS）

需要 macOS 13+ 与 .NET 10 SDK（含 macOS workload）。

```bash
# 一次性：自建带 Display Control 通道的 FreeRDP（动态分辨率靠它），约 5 分钟
native/rdp/build-freerdp.sh          # 本机架构；或 build-freerdp.sh arm64
native/rdp/build.sh                  # 编 C ABI 封装 shim

# 构建 / 运行
dotnet build src/RemoteFlow.App.Mac
open src/RemoteFlow.App.Mac/bin/Debug/net10.0-macos/RemoteFlow.app
```

macOS 数据目录：`~/Library/Application Support/RemoteFlow/`（Secret 存于 Keychain）。

---

## 安全模型

- Secret 不明文持久化，不进普通日志，不随导出文件导出；UI 默认不显示密码。
- `Credential` 只保存 `SecretReference` 引用键；明文由平台 Vault 在建立连接的瞬间解析，用后立即释放。
- 日志脱敏有两道防线：调用方不传 Secret + `SecretRedactingEnricher` 兜底遮蔽疑似敏感属性。
- SSH Host Key 首次提示（TOFU）、变化强警告；证书/Host Key 异常必须可见，不静默忽略。
- 安全问题的负责任披露见 [SECURITY.md](SECURITY.md)。

---

## License

本应用依据 **GPL-3.0** 许可证发布，不附带任何担保；随源码分发与运行时引用的
第三方组件及其许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
