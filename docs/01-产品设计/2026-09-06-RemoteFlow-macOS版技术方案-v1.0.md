# RemoteFlow macOS 版技术方案

> **产品名称：** RemoteFlow
> **文档类型：** 技术方案 / 实施路线
> **文档版本：** V1.0
> **日期：** 2026-09-06
> **状态：** 待评审 / 可进入 Phase 0
> **关联基线：** `docs/01-产品设计/2026-09-03-RemoteFlow产品设计文档-v1.1.md`

---

## 1. 目标与范围

在不牺牲现有 Windows 版（WPF，当前 v0.8.0）的前提下，交付一个 **macOS 原生分发的 RemoteFlow 桌面客户端**，保持产品定位一致：统一管理并使用 RDP / SSH / VNC 连接，多 Tab 会话，本地优先，凭据与资产分离。

### 1.1 首版范围（macOS v0.1）

- 协议：**RDP + SSH + VNC** 全部进首版。
- 资产管理：分组 / 标签 / 收藏 / 搜索 / 连接历史，与 Windows 版一致。
- 凭据库：Local / Domain / SSH Password / SSH Key / VNC Password，Secret 由 macOS Keychain 保护。
- 多 Tab 会话外壳、会话生命周期与实时状态同步。
- 深浅主题、系统跟随。
- 分发：Developer ID 签名 + 公证的 `.dmg`（先 `osx-arm64`）。

### 1.2 首版非目标

- 云同步 / 团队 Vault（与 Windows 版同步节奏，属后续版本）。
- Mac App Store 上架（首版走 Developer ID 直分发，沙盒化留待评估）。
- Windows 版迁移到 Avalonia（本方案不触碰 WPF 工程）。
- `universal` 二进制（首版仅 arm64，Intel 支持视需求再评估）。

---

## 2. 决策记录

| 决策项 | 选择 | 理由 |
|---|---|---|
| UI 技术 | **Avalonia + 复用 .NET 后端** | Core / Application / SSH 可原样复用；ViewModel 层（CommunityToolkit.Mvvm）大部分可共享；SwiftUI 原生会作废大半 C# 资产 |
| RDP 是否进首版 | **进首版** | Mac 用户存在真实 RDP 需求（管理 Windows Server / 跳板场景），不做等于半成品 |
| 仓库结构 | **同一 `remoteflow` 仓库多项目** | 符合「一个产品一个仓库」现状；共享层改一次两端受益；避免 submodule / 私有 NuGet 的同步成本 |

---

## 3. 现状盘点：各层 macOS 复用前景

依赖方向不变：`UI → Application → Core ← Infrastructure / Protocol.*`。

| 项目 | 当前 Target | 平台耦合 | macOS 处置 |
|---|---|---|---|
| `RemoteFlow.Core` | `net10.0` | 无 | ✅ 直接复用，零改动 |
| `RemoteFlow.Application` | `net10.0` | 无 | ✅ 直接复用，零改动 |
| `RemoteFlow.Protocol.Ssh` | `net10.0` | 无（SSH.NET 纯托管） | ✅ 直接复用 |
| `RemoteFlow.Protocol.Vnc` | `net10.0-windows` | 轻（仅 `VncRenderTarget` 依赖 `WriteableBitmap`） | 🟡 渲染目标抽象化后共享 |
| `RemoteFlow.Infrastructure` | `net10.0-windows` | 中（SQLite / Serilog 跨平台；`DpapiCredentialVault` 与 `AppPaths` 是 Windows 语义） | 🟡 拆分 + 新增 Keychain 实现 |
| `RemoteFlow.App/ViewModels/*` | WPF 内 | 中（`Dispatcher`、`DispatcherTimer`、一处 `CollectionView`） | 🟡 抽 `RemoteFlow.Presentation` 共享 |
| `RemoteFlow.Protocol.Rdp` | `net10.0-windows` | 高（`mstscax.dll` ActiveX + WinForms `AxHost` + COM） | ❌ Windows 专属，保留；Mac 另起实现 |
| `RemoteFlow.App`（WPF 视图 / 主题 / 托盘 / 单实例 / Win32 P/Invoke） | WPF | 全部 | ❌ Mac 侧用 Avalonia 重写 |

设计基线文档 §10.2 已预判此路径：「后续如需 macOS/Linux 客户端，应优先抽取 `RemoteFlow.Core`，再评估独立 UI 技术。」

---

## 4. 目标工程结构

```
remoteflow/
├─ src/
│  ├─ RemoteFlow.Core/                    (net10.0)              不动
│  ├─ RemoteFlow.Application/             (net10.0)              不动
│  ├─ RemoteFlow.Presentation/            (net10.0)        新增  共享 ViewModel
│  ├─ RemoteFlow.Infrastructure/          (net10.0)        改造  去 Windows 化
│  ├─ RemoteFlow.Infrastructure.Windows/  (net10.0-windows) 新增  DpapiCredentialVault
│  ├─ RemoteFlow.Infrastructure.Mac/      (net10.0)        新增  KeychainCredentialVault
│  ├─ RemoteFlow.Protocol.Ssh/            (net10.0)              不动
│  ├─ RemoteFlow.Protocol.Vnc/            (net10.0)        改造  渲染目标抽象
│  ├─ RemoteFlow.Protocol.Rdp/            (net10.0-windows)      不动（Windows）
│  ├─ RemoteFlow.Protocol.Rdp.Mac/        (net10.0)        新增  FreeRDP 封装（托管侧）
│  ├─ RemoteFlow.App/                     (WPF)                  不动
│  └─ RemoteFlow.App.Mac/                 (Avalonia)       新增  macOS UI / Composition Root
├─ native/
│  └─ remoteflow-rdp/                                     新增  FreeRDP C 封装 → libremoteflow-rdp.dylib
├─ scripts/                                               新增  macOS 打包 / 签名 / 公证脚本
└─ tests/
   ├─ RemoteFlow.Core.Tests/                                    扩充（平台无关）
   └─ RemoteFlow.IntegrationTests/                              Windows 专属部分加 TFM 守卫
```

### 4.1 架构约束（延续现有红线）

- UI 不直接引用协议实现，只经 `IConnectionProvider` / `IRemoteSession`。
- `RemoteFlow.Core` 保持平台无关，不引用 Avalonia / WPF / Windows / AppKit。
- ViewModel 不保存 Secret；只有 Credential Vault 实现接触明文。
- 新增协议 / 新增平台 Vault = 新增一个实现 + 在对应 Composition Root 注册，不改既有架构。
- 数据库访问统一走 Repository。

---

## 5. Phase 0 — 共享层去平台化

**目标：** 让 Core / Application / SSH / VNC / ViewModel 五块在 `net10.0`（非 `-windows`）下编译通过，Windows 版行为零变化。
**预估：** 约 1 周。
**对 Windows 版影响：** 仅工程拆分与 TFM 调整，运行时行为不变，需回归编译 + 冒烟。

### 5.1 Infrastructure 拆分

- `RemoteFlow.Infrastructure` 降为 `net10.0`，保留：`SqliteConnectionRepository`、`SqliteGroupTagRepositories`、`SqliteCredentialHistoryRepositories`、`RemoteFlowDatabase`、`JsonSettingsStore`、`SessionStateStore`、`LoggingSetup`、`Data/LocalBackupService`。这些本身跨平台，只是被 `-windows` TFM 拖住。
- `Microsoft.Data.Sqlite` 在 `osx-arm64` 依赖 `SQLitePCLRaw` 自带原生库，正常工作，无需系统 SQLite。
- 新增 `RemoteFlow.Infrastructure.Windows`（`net10.0-windows`）承载 `DpapiCredentialVault` 与 `System.Security.Cryptography.ProtectedData` 依赖。
- `RemoteFlow.App`（WPF）改为引用 `Infrastructure` + `Infrastructure.Windows`。

### 5.2 AppPaths 平台感知

- Windows：`%LOCALAPPDATA%\RemoteFlow`（不变）。
- macOS：`~/Library/Application Support/RemoteFlow`。
  - ⚠️ .NET 在 macOS 上 `Environment.SpecialFolder.LocalApplicationData` 解析为 `~/.local/share`，不符合 macOS 惯例，需显式用 `~/Library/Application Support`。
- 日志目录：macOS 可选 `~/Library/Logs/RemoteFlow`，或统一放数据目录下 `logs/`（与 Windows 版一致，推荐后者以简化备份说明）。
- `DataDirectory` 自定义覆盖逻辑保留。

### 5.3 Keychain 凭据保险库

- 新增 `RemoteFlow.Infrastructure.Mac/Security/KeychainCredentialVault : ICredentialVault`。
- P/Invoke `Security.framework`：`SecItemAdd` / `SecItemCopyMatching` / `SecItemUpdate` / `SecItemDelete`，`kSecClassGenericPassword`，`kSecAttrService = "RemoteFlow"`，`kSecAttrAccount = <引用键>`。
- 存储模型对齐 DPAPI 版：SQLite 存 `Credential` 元数据 + 引用键；Keychain 存密文条目。把 `remoteflow.db` 单独拷走同样得不到 Secret。
- `ResolvedCredential` 生命周期语义不变：解析即用、用后立即 `Dispose`、`CryptographicOperations.ZeroMemory` 清零明文缓冲。
- 非沙盒 Developer ID 分发下的 Keychain 访问提示行为需实测（首次访问系统弹窗授权、后续静默）。

### 5.4 VNC 渲染目标抽象

- `Community.MarcusW.VncClient` 已提供 `IRenderTarget` / `IFramebufferReference` 抽象。
- 将 RFB 会话逻辑（`VncSession`、`VncKeyMapper`、`VncConnectionProvider`）降到 `net10.0`。
- `VncRenderTarget` 当前直接依赖 `System.Windows.Media.Imaging.WriteableBitmap` → 拆为：
  - `IVncRenderSurface`（暴露像素缓冲写入 + 尺寸变更事件）。
  - WPF 实现（`WriteableBitmap`，留在 `RemoteFlow.App`）。
  - Avalonia 实现（`Avalonia.Media.Imaging.WriteableBitmap`，放在 `RemoteFlow.App.Mac`）。
- 渲染 surface 由 UI 层注入会话，协议项目不再引用任何 UI 框架。

### 5.5 抽出 RemoteFlow.Presentation

- 迁移 `src/RemoteFlow.App/ViewModels/*`（约 6000 行，14 个文件）到新 `RemoteFlow.Presentation`（`net10.0`）。
- 现存 WPF 耦合点（需抽象）：
  - `System.Windows.Application.Current.Dispatcher`（多处）→ `IUiDispatcher { void Post(Action); Task InvokeAsync(Func<Task>); }`。
  - `System.Windows.Threading.DispatcherTimer`（`SessionTabViewModel` 会话时长计时）→ `IUiTimer` 或改用 `PeriodicTimer` + 调度器封送。
  - `System.Windows.Data`（`ConnectionsPageViewModel` 一处 `CollectionView`）→ 用 `ICollectionView` 抽象或改为 VM 内维护过滤后集合。
- `DialogService` / `ThemeService` / `TrayService` / `SessionViewFactory` / `Converters` 保留在各 UI 侧，接口下沉到 `Presentation`。
- Windows 版注册各抽象的 WPF 实现，行为不变。

### 5.6 Phase 0 验收

- `dotnet build` 全解决方案通过（含新项目）。
- `dotnet test tests/RemoteFlow.Core.Tests` 通过。
- Windows 版 `dotnet run --project src/RemoteFlow.App` 冒烟：连接管理、SSH 连一台、VNC 连一台、RDP 连一台、凭据增删、主题切换、托盘退出。
- 在 macOS 上 `dotnet build` 共享层项目（`Core` / `Application` / `Presentation` / `Infrastructure` / `Infrastructure.Mac` / `Protocol.Ssh` / `Protocol.Vnc`）全部通过。

---

## 6. Phase 1 — macOS RDP 原生适配层（关键路径）

**目标：** 在 Avalonia 会话 Tab 内承载一个可用的 RDP 会话（画面 + 键鼠 + 基础剪贴板 + 分辨率协商 + 证书信任）。
**预估：** 约 3–4 周（含 POC）。
**风险等级：** 最高。FreeRDP 无官方 .NET 绑定，图形/输入循环需原生封装。

### 6.1 Sprint 0 POC（先做，不通不进 UI）

对齐现有仓库对 RDP ActiveX 的做法（`CLAUDE.md` 提到 scratchpad 有 Sprint 0 POC 参考）：

- `native/remoteflow-rdp/`：C / Objective-C，链接 FreeRDP 3.x。
- 命令行程序：连接一台 RDP 主机 → 每帧回调 dump 成 PNG → 注入一段键盘 / 鼠标脚本 → 断开。
- 验证点：TLS / NLA 认证、位图更新回调拿到完整帧、输入注入生效、分辨率协商、服务器证书回调可拦截。
- POC 通过后再评估把画面绘制路径改为持有 `NSView` / `CALayer`。

### 6.2 FreeRDP 依赖管理

- FreeRDP 3.x 经 git submodule 或 vcpkg 纳入，构建 `osx-arm64` 动态库（`libfreerdp`、`libfreerdp-client`、`libwinpr` + OpenSSL）。
- 构建脚本固定版本 tag，产物纳入 `.app` bundle（见 Phase 3），不入 Git 历史。

### 6.3 原生 helper C ABI（`libremoteflow-rdp.dylib`）

暴露最小 C 接口，图形 / 输入的重活留在原生侧（FreeRDP 官方示例 `sdl-freerdp` / `wlfreerdp` 有成熟范式）：

```
rf_rdp_session*  rf_rdp_create(const rf_rdp_params*);
int              rf_rdp_connect(rf_rdp_session*, rf_rdp_callbacks*);   // 帧 / 状态 / 证书 / 剪贴板回调
void             rf_rdp_send_pointer(rf_rdp_session*, int x, int y, uint32_t flags);
void             rf_rdp_send_scancode(rf_rdp_session*, uint16_t code, bool down);
void             rf_rdp_resize(rf_rdp_session*, int w, int h);
void             rf_rdp_set_clipboard(rf_rdp_session*, const char* utf8);
void*            rf_rdp_get_nsview(rf_rdp_session*);                   // 供 Avalonia NativeControlHost 嵌入
void             rf_rdp_disconnect(rf_rdp_session*);
void             rf_rdp_destroy(rf_rdp_session*);
```

### 6.4 托管侧 `RemoteFlow.Protocol.Rdp.Mac`

- `MacRdpConnectionProvider : IConnectionProvider`（`Protocol => ProtocolType.Rdp`，`DefaultPort => 3389`，`IsAvailable` 检查 dylib 是否随包存在）。
- `MacRdpSession : IRemoteSession`：P/Invoke 上述 ABI，映射到既有会话状态机（`Idle → Connecting → Connected → Reconnecting → Disconnecting → Disconnected → Closed`，失败进 `Failed`），错误统一落 `ConnectionErrorCode`，不落底层异常原文。
- `NSView` 经 Avalonia `NativeControlHost` 嵌入会话 Tab。
- 证书信任：RDP 服务器证书首次记录指纹、变化强警告、绝不静默接受（复用现有 Host Key 的交互与落库思路，或建独立 cert store 表）。

### 6.5 Phase 1 验收

- Avalonia 空壳窗口内连上一台 RDP：画面刷新流畅、键鼠可用、窗口 resize 触发远端分辨率协商、剪贴板文本双向。
- 断线 / 主动断开 / 取消 / App 退出四条路径都能收敛到 `Closed`，无残留进程 / 线程。
- 证书首次弹窗 + 变化强警告可复现。

---

## 7. Phase 2 — Avalonia UI 层

**目标：** `RemoteFlow.App.Mac` 达到与 Windows 版对齐的功能面。
**预估：** 约 3–5 周。

### 7.1 工程

- `src/RemoteFlow.App.Mac`，Avalonia（`net10.0`），`CommunityToolkit.Mvvm`（已在用，复用）。
- Composition Root 对齐 `App.xaml.cs` 的装配：Repository / Vault（Keychain）/ Application 服务 / 三个 `IConnectionProvider`（Ssh / Vnc / Rdp.Mac）/ UI 服务 / ViewModel。
- ⚠️ `RemoteFlow.Application` 命名空间会遮蔽 `Avalonia.Application`（与 WPF 下遮蔽 `System.Windows.Application` 同理）→ UI 代码用完全限定名。

### 7.2 视图迁移

- WPF XAML → Avalonia XAML 差异：`Trigger` / `DataTrigger` → `Style` selector + 伪类 / `:is()` ；`x:Name` 作用域；`Visibility` → `IsVisible`；`Style.Resources` 与 `ResourceDictionary` 合并语义；附加属性写法。
- 布局与信息架构照搬设计稿（`docs/01-产品设计/UI/`）与现有 WPF 视图。
- 主题：`Theme.Light.xaml` / `Theme.Dark.xaml` → Avalonia `ThemeVariant` + `ResourceDictionary`；`ThemeService` 改用 Avalonia 的 `RequestedThemeVariant` + 系统跟随（`PlatformSettings.ColorValuesChanged`）。
- 图标资源 `Icons.xaml` 迁移为 Avalonia `StreamGeometry` / `PathIcon` 资源。

### 7.3 平台服务在 Mac 侧的实现

| 服务 | Windows（现有） | macOS（新实现） |
|---|---|---|
| `IUiDispatcher` | `Dispatcher` | `Avalonia.Threading.Dispatcher.UIThread` |
| 托盘 | `TrayService`（WinForms NotifyIcon） | Avalonia `TrayIcon` + `NativeMenu`（macOS 状态栏项） |
| 单实例 | `Local\` 命名 Mutex | Unix domain socket / `NSRunningApplication` 检测 + 唤醒已有实例 |
| `IDialogService` | WPF 无边框对话框 | Avalonia 窗口 / `Window.ShowDialog` |
| SSH 终端宿主 | WebView2 + xterm.js | Avalonia WebView（WKWebView）+ 同一套 xterm.js 前端资源；`TerminalAssetStore` 释放逻辑复用 |
| 会话画面宿主 | `WindowsFormsHost` / WPF 控件 | `NativeControlHost`（RDP NSView）/ Avalonia `Image`+`WriteableBitmap`（VNC） |

### 7.4 Phase 2 验收

- 功能面对齐 Windows v0.8.0 的 macOS 可达子集：资产管理、三协议连接、多 Tab、实时状态、搜索、历史、凭据库、导入导出、主题、托盘。
- SSH 终端六套主题 / 粘贴安全 / 搜索可用。
- VNC 画面 + 输入 + 剪贴板接收可用。

---

## 8. Phase 3 — 打包与分发

**预估：** 约 1 周。

- `dotnet publish src/RemoteFlow.App.Mac -c Release -r osx-arm64`（先自包含单目录，评估是否 trim / AOT）。
- 组 `.app` bundle：`Info.plist`（`CFBundleIdentifier`、`LSMinimumSystemVersion`、`NSHighResolutionCapable`）、`.icns` 图标。
- FreeRDP / OpenSSL dylib 放 `Contents/Frameworks/`，`install_name_tool` + `@rpath` 修正加载路径。
- 签名：`codesign` Developer ID Application + Hardened Runtime + entitlements（`com.apple.security.cs.allow-jit` 视 .NET 运行时需要、`com.apple.security.network.client`）；所有 dylib / 可执行文件逐个签名。
- 公证：`notarytool submit` + `stapler staple`。
- 打 `.dmg`，按项目命名规范：`RemoteFlow-v<MAJOR.MINOR.PATCH>-macos-arm64.dmg`。
- `scripts/` 增加 `build-macos.sh` / `sign-notarize.sh`；后续接 macOS CI runner。
- `releases/` 大文件继续走 `.gitignore` 排除 + GitHub Releases 上传；`source/` 与 `CHANGELOG.md` 入库。

---

## 9. 里程碑与预估

| Phase | 内容 | 预估 | 关键产出 |
|---|---|---|---|
| 0 | 共享层去平台化 | ~1 周 | 共享层 macOS 可编译；Windows 版零回归 |
| 1 | macOS RDP 原生适配（含 POC） | ~3–4 周 | Avalonia 壳内可用 RDP 会话 |
| 2 | Avalonia UI 层 | ~3–5 周 | 功能对齐的 macOS 客户端 |
| 3 | 打包分发 | ~1 周 | 签名公证的 `.dmg` |
| | **合计（单人）** | **~2–3 个月** | macOS v0.1 |

FreeRDP POC（Phase 1 前段）是最大不确定项，建议作为独立门禁：POC 不通过则重新评估 RDP 方案（外部客户端兜底 / 延后）。

---

## 10. 风险登记

| 风险 | 等级 | 缓解 |
|---|---|---|
| FreeRDP 集成（无官方 .NET 绑定，图形/输入循环需原生封装） | 高 | Sprint 0 POC 先行，作为进入 UI 阶段的门禁 |
| Avalonia XAML 迁移工作量（样式 / 触发器 / 资源体系逐屏重写） | 中 | 布局照搬设计稿；先搭导航骨架再逐页填充 |
| Keychain 非沙盒行为（访问授权提示、access group） | 中 | Phase 0 用最小样例实测 |
| xterm.js 宿主从 WebView2 换 WKWebView 的行为差异（剪贴板 / 焦点 / DPI / IME） | 中 | Phase 2 早期打通终端最小闭环 |
| `Microsoft.Data.Sqlite` / `SQLitePCLRaw` 在 `osx-arm64` 的原生库打包 | 低 | 官方支持，Phase 0 验证 publish 产物 |
| `.NET 10` + Avalonia 版本兼容 | 低 | Phase 0 锁定 Avalonia 版本并纳入 `Directory.Packages.props` |
| 命名空间遮蔽（`RemoteFlow.Application` vs `Avalonia.Application`） | 低 | UI 层统一完全限定名（已有先例） |

---

## 11. 与现有规则的衔接

- **版本**：macOS 版与 Windows 版共用同一 `Directory.Build.props` 版本号，还是独立版本线，需在进入 Phase 3 前决策（建议共用主版本、以平台后缀区分产物）。
- **发布**：仅当明确「发布 / Release / 打版本」时才建 `releases/v<X.Y.Z>/`；macOS 产物命名 `RemoteFlow-v<X.Y.Z>-macos-arm64.dmg`。
- **依赖**：Avalonia、`Avalonia.WebView` 等新增包在 `Directory.Packages.props` 集中登记并在 PR 说明原因。
- **安全红线**：Keychain 实现同样受「只有 Vault 接触明文 Secret」「不写日志 / 导出 / 异常消息」约束；RDP 证书信任比照 SSH Host Key。
- **分支**：大功能开发，建议 `feature/macos-<phase>` 分支推进，不直接对 `main`。

---

## 12. 下一步

1. 评审本方案，确认里程碑与范围。
2. 开 `feature/macos-phase0` 分支，从风险最低、对 Windows 版零影响的项开工：`Infrastructure` 拆分 + `AppPaths` 平台感知 + `RemoteFlow.Presentation` 抽取。
3. 并行启动 FreeRDP Sprint 0 POC（独立于主线，验证 RDP 可行性）。
