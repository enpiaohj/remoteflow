# RemoteFlow macOS 版技术方案

> **产品名称：** RemoteFlow
> **文档类型：** 技术方案 / 实施路线
> **文档版本：** V1.1
> **日期：** 2026-09-06
> **状态：** 待评审 / 可进入 Phase 0 门禁 POC
> **关联基线：** `docs/01-产品设计/2026-09-03-RemoteFlow产品设计文档-v1.1.md`

---

## 0. 修订记录

| 版本 | 日期 | 主要变更 |
|---|---|---|
| V1.0 | 2026-09-06 | 初版（commit `3a3ff58`，原文可从 Git 历史检出） |
| V1.1 | 2026-09-06 | 代码级复核后修订。① 阶段重排：新增 Phase 0 可行性门禁 POC，解决原 Phase 1 对 Avalonia 壳的依赖倒置；② RDP 承载方式由「原生 NSView + NativeControlHost」改为「帧回调 + 托管位图渲染」，规避 airspace；③ Avalonia WebView 从普通风险升级为门禁项；④ 新增 §8 数据迁移、§9 测试策略、§7.5 RDP 选项映射表；⑤ 估算由 2–3 个月上调为 4–6 个月；⑥ 补充 ICMP 退化、`TerminalAssetStore` 下沉、许可合规、SDK 前置条件 |

---

## 1. 目标与范围

在不牺牲现有 Windows 版（WPF，当前 v0.8.0）的前提下，交付一个 **macOS 原生分发的 RemoteFlow 桌面客户端**，保持产品定位一致：统一管理并使用 RDP / SSH / VNC 连接，多 Tab 会话，本地优先，凭据与资产分离。

### 1.1 首版范围（macOS v0.1）

- 协议：**RDP + SSH + VNC** 全部进首版。
- 资产管理：分组 / 标签 / 收藏 / 搜索 / 连接历史，与 Windows 版一致。
- 凭据库：Local / Domain / SSH Password / SSH Key / VNC Password，Secret 由 macOS Keychain 保护。
- **从 Windows 版迁移数据**（连接 CSV + 凭据 `.rfbackup`），见 §8。
- 多 Tab 会话外壳、会话生命周期与实时状态同步。
- 深浅主题、系统跟随。
- 分发：Developer ID 签名 + 公证的 `.dmg`（先 `osx-arm64`）。

### 1.2 首版非目标

- 云同步 / 团队 Vault（与 Windows 版同步节奏，属后续版本）。
- Mac App Store 上架（首版走 Developer ID 直分发，沙盒化留待评估）。
- Windows 版迁移到 Avalonia（本方案不触碰 WPF 工程）。
- `universal` 二进制（首版仅 arm64，Intel 支持视需求再评估）。
- **RDP 多显示器**（`UseMultimon`），原因见 §7.5。

---

## 2. 决策记录

| 决策项 | 选择 | 理由 |
|---|---|---|
| UI 技术 | **Avalonia + 复用 .NET 后端** | Core / Application / SSH 可原样复用；ViewModel 层（CommunityToolkit.Mvvm）大部分可共享；SwiftUI 原生会作废大半 C# 资产 |
| RDP 是否进首版 | **进首版** | Mac 用户存在真实 RDP 需求（管理 Windows Server / 跳板场景），不做等于半成品 |
| 仓库结构 | **同一 `remoteflow` 仓库多项目** | 符合「一个产品一个仓库」现状；共享层改一次两端受益；避免 submodule / 私有 NuGet 的同步成本 |
| RDP 画面承载 | **帧回调 + 托管位图渲染**（V1.1 修订） | 与 VNC 同一条渲染路径，彻底规避 airspace；并使 RDP 适配层可脱离 Avalonia 独立验收，见 §3.2 |

---

## 3. 关键技术判断

### 3.1 现状盘点：各层 macOS 复用前景

依赖方向不变：`UI → Application → Core ← Infrastructure / Protocol.*`。

| 项目 | 当前 Target | 平台耦合 | macOS 处置 |
|---|---|---|---|
| `RemoteFlow.Core` | `net10.0` | 无 | ✅ 直接复用，零改动 |
| `RemoteFlow.Application` | `net10.0` | 无 | ✅ 直接复用，零改动 |
| `RemoteFlow.Protocol.Ssh` | `net10.0` | 无（SSH.NET 纯托管） | ✅ 直接复用 |
| `RemoteFlow.Protocol.Vnc` | `net10.0-windows` | 轻（仅 `VncRenderTarget` 依赖 `WriteableBitmap`） | 🟡 渲染目标抽象化后共享 |
| `RemoteFlow.Infrastructure` | `net10.0-windows` | 中（SQLite / Serilog 跨平台；`DpapiCredentialVault` 与 `AppPaths` 是 Windows 语义） | 🟡 拆分 + 新增 Keychain 实现 |
| `RemoteFlow.App/ViewModels/*` | WPF 内 | 中（`Dispatcher`、`DispatcherTimer`、一处 `CollectionView`） | 🟡 抽 `RemoteFlow.Presentation` 共享 |
| `RemoteFlow.App/Services/TerminalAssetStore` | WPF 内 `internal static` | 中（嵌入资源 LogicalName 绑死 `RemoteFlow.App`） | 🟡 连同 xterm.js 资产下沉共享 |
| `RemoteFlow.Protocol.Rdp` | `net10.0-windows` | 高（`mstscax.dll` ActiveX + WinForms `AxHost` + COM） | ❌ Windows 专属，保留；Mac 另起实现 |
| `RemoteFlow.App`（WPF 视图 / 主题 / 托盘 / 单实例 / Win32 P/Invoke） | WPF | 全部 | ❌ Mac 侧用 Avalonia 重写 |

设计基线文档 §10.2 已预判此路径：「后续如需 macOS/Linux 客户端，应优先抽取 `RemoteFlow.Core`，再评估独立 UI 技术。」

### 3.2 RDP 画面承载方式（V1.1 关键修订）

**问题：** 若沿用「原生 NSView + Avalonia `NativeControlHost`」，会重演 airspace 问题。该坑现有代码已踩过并留有注释：

```
src/RemoteFlow.App/Views/Sessions/SessionHostView.xaml:206-208
「RDP 会话内嵌原生 ActiveX（airspace），普通 WPF 浮层会被它盖住、
  也收不到其上的鼠标事件；用 Popup（独立顶层窗口）承载」
```

v0.8.0 的会话常驻条、全屏药丸、连接质量详情浮层全部依赖这条绕法。而「Avalonia Popup 能否稳定盖住 `NativeControlHost` 的 NSView」在 macOS 上是未验证假设——Avalonia 的 popup 可能落到原生 NSWindow，也可能落到 `OverlayPopupHost`，两者行为不同。

**决策：** FreeRDP 本身就以 framebuffer 更新为核心输出，改为 **帧回调 → 托管 `WriteableBitmap` 渲染**，与 VNC 走完全同一条路径（项目已有 `VncRenderTarget` 成熟实现）。

收益有三：

1. **airspace 整类问题消失**，所有 Avalonia 浮层天然可用，无需 popup 绕法。
2. Phase 1 抽出的渲染面抽象可由 RDP / VNC **共用**（`IFrameRenderSurface`）。
3. **RDP 适配层可脱离 Avalonia 独立验收**——帧 dump 成 PNG 即可做无头验证，从而解除原 V1.0 中「Phase 1 验收依赖 Phase 2 才建的 Avalonia 壳」的依赖倒置。

代价是像素搬运的 CPU 开销。这是可测量、可优化的已知量（对齐 stride、脏矩形增量更新、必要时降到 `SKBitmap`），比 airspace 类交互疑难可控得多。若实测帧率不达标，再回退到原生视图方案并同步重做浮层策略。

### 3.3 两个门禁假设

以下两项不成立会**改变整个方案**，故提到 Phase 0 先验：

| 门禁 | 假设 | 不成立的后果 |
|---|---|---|
| **G1 FreeRDP** | 可在 macOS arm64 构建 FreeRDP 3.x，并经 C ABI 稳定拿到帧 / 注入输入 / 拦截证书 | RDP 无法进首版，需退回「外部客户端兜底」或推迟 |
| **G2 Avalonia WebView** | 存在可用的 Avalonia WebView（WKWebView）承载 xterm.js，且剪贴板 / 焦点 / IME / DPI 行为可接受 | SSH 终端需自绘终端控件，工作量为另一数量级 |

G2 的风险常被低估：Avalonia **无一方 WebView**，社区方案（`WebViewControl-Avalonia`、`Avalonia.WebView`）成熟度参差，而 SSH 终端 100% 依赖它。

---

## 4. 目标工程结构

```
remoteflow/
├─ src/
│  ├─ RemoteFlow.Core/                    (net10.0)              不动
│  ├─ RemoteFlow.Application/             (net10.0)              不动
│  ├─ RemoteFlow.Presentation/            (net10.0)        新增  共享 ViewModel + 终端资产
│  ├─ RemoteFlow.Infrastructure/          (net10.0)        改造  去 Windows 化
│  ├─ RemoteFlow.Infrastructure.Windows/  (net10.0-windows) 新增  DpapiCredentialVault
│  ├─ RemoteFlow.Infrastructure.Mac/      (net10.0)        新增  KeychainCredentialVault
│  ├─ RemoteFlow.Protocol.Ssh/            (net10.0)              不动
│  ├─ RemoteFlow.Protocol.Vnc/            (net10.0)        改造  渲染面抽象
│  ├─ RemoteFlow.Protocol.Rdp/            (net10.0-windows)      不动（Windows）
│  ├─ RemoteFlow.Protocol.Rdp.Mac/        (net10.0)        新增  FreeRDP 封装（托管侧）
│  ├─ RemoteFlow.App/                     (WPF)                  不动
│  └─ RemoteFlow.App.Mac/                 (Avalonia)       新增  macOS UI / Composition Root
├─ native/
│  └─ remoteflow-rdp/                                     新增  FreeRDP C 封装 → libremoteflow-rdp.dylib
├─ scripts/                                               新增  macOS 构建 / 签名 / 公证脚本
└─ tests/
   ├─ RemoteFlow.Core.Tests/              (net10.0)              不动
   ├─ RemoteFlow.IntegrationTests/        (net10.0)        改造  跨平台部分（见 §9）
   └─ RemoteFlow.IntegrationTests.Windows/(net10.0-windows) 新增  DPAPI / RDP ActiveX 等
```

### 4.1 架构约束（延续现有红线）

- UI 不直接引用协议实现，只经 `IConnectionProvider` / `IRemoteSession`。
- `RemoteFlow.Core` 保持平台无关，不引用 Avalonia / WPF / Windows / AppKit。
- ViewModel 不保存 Secret；只有 Credential Vault 实现接触明文。
- 新增协议 / 新增平台 Vault = 新增一个实现 + 在对应 Composition Root 注册，不改既有架构。
- 数据库访问统一走 Repository。

---

## 5. Phase 0 — 可行性门禁 POC

**目标：** 用最小代价验证 §3.3 的两个门禁假设。**不触碰现有 Windows 工程。**
**预估：** 约 2 周（两个 spike 可并行）。
**门禁语义：** 两项均通过才进入 Phase 1；任一不通过，回到 §2 重新决策。

### 5.0 前置条件

- ⚠️ **当前开发机未安装 .NET SDK**（`dotnet` 命令不存在）。需先装 .NET 10 SDK（`global.json` 锁 `10.0.400`，`rollForward: latestFeature`）。
- Xcode Command Line Tools、Homebrew、CMake（FreeRDP 构建需要）。

### 5.1 G1 — FreeRDP spike

- `native/remoteflow-rdp/` 之外先做一个**独立的一次性验证程序**（不进主仓库结构，放 scratchpad）。
- 构建 FreeRDP 3.x for `osx-arm64`（含 OpenSSL 等依赖），固定版本 tag。
- 验证点：
  1. TLS / NLA 认证成功连上一台 Windows 主机。
  2. 位图更新回调能拿到完整帧，**dump 成 PNG 肉眼可读**。
  3. 键盘 / 鼠标注入生效（远端可见响应）。
  4. 分辨率协商 / resize 生效。
  5. 服务器证书回调可拦截（为后续「首次信任 + 变化强警告」铺路）。
  6. 粗测帧率与 CPU 占用，判断 §3.2 的托管位图渲染是否可行。

### 5.2 G2 — Avalonia WebView + xterm.js spike

- 最小 Avalonia 应用 + 候选 WebView 控件，加载现有 `src/RemoteFlow.App/Assets/Terminal/` 下的 xterm.js 前端。
- 验证点：控件可用性与维护状态、终端渲染正常、键盘输入与 IME、剪贴板复制粘贴、焦点行为、Retina DPI 清晰度、随窗口 resize。
- 同时确认 Avalonia 版本与 .NET 10 的兼容组合，锁定后写入 `Directory.Packages.props`。

### 5.3 Phase 0 验收

- G1、G2 各产出一份结论备忘（通过 / 不通过 / 有条件通过 + 已知限制）。
- 锁定 FreeRDP 版本、Avalonia 版本、WebView 控件选型。
- 若 G1 判定托管位图渲染帧率不可接受，**在此处**决定改回原生视图方案并同步调整 §3.2 与浮层策略。

---

## 6. Phase 1 — 共享层去平台化 + 回归网

**目标：** 让 Core / Application / SSH / VNC / ViewModel 五块在 `net10.0`（非 `-windows`）下编译通过，Windows 版行为零变化。
**预估：** 约 1.5 周。
**对 Windows 版影响：** 仅工程拆分与 TFM 调整，运行时行为不变，需回归编译 + 冒烟。

> **顺序要求：** §6.1 测试拆分必须**最先做**。Phase 1 后续各项是大范围重构，没有可在 macOS 上运行的回归网就等于裸奔。

### 6.1 测试工程拆分（第一项）

现状问题：`tests/RemoteFlow.IntegrationTests` 是 `net10.0-windows`，且**引用了 WPF 的 `RemoteFlow.App`**，导致以下本来完全跨平台的测试在 macOS 上一个都跑不了：

`SqliteRepositoryTests`、`CredentialBackupTests`、`GroupServiceTests`、`ImportExportServiceTests`、`ConnectionServiceTagTests`、`SessionManagerLifecycleTests`、`QualityGradeEvaluatorTests`、`DateTimeDisplayTests`

处置：

- `RemoteFlow.IntegrationTests` 降为 `net10.0`，去掉对 `RemoteFlow.App` / `RemoteFlow.Protocol.Rdp` 的引用，承载上述跨平台测试（被测类型随 §6.5 一并下沉到 `Presentation` / `Infrastructure`）。
- 新建 `RemoteFlow.IntegrationTests.Windows`（`net10.0-windows`），承载 `DpapiCredentialVaultTests`、`RdpKeyboardSequenceTests`、`ConnectionSelectionViewModelTests`、`TerminalAssetStoreTests` 中确属 Windows 的部分。
- 验收：两个测试工程在各自平台全绿；跨平台工程在 macOS 上可执行。

### 6.2 Infrastructure 拆分

- `RemoteFlow.Infrastructure` 降为 `net10.0`，保留：`SqliteConnectionRepository`、`SqliteGroupTagRepositories`、`SqliteCredentialHistoryRepositories`、`RemoteFlowDatabase`、`JsonSettingsStore`、`SessionStateStore`、`LoggingSetup`、`Data/LocalBackupService`。这些本身跨平台，只是被 `-windows` TFM 拖住。
- `Microsoft.Data.Sqlite` 在 `osx-arm64` 依赖 `SQLitePCLRaw` 自带原生库，正常工作，无需系统 SQLite。
- 新增 `RemoteFlow.Infrastructure.Windows`（`net10.0-windows`）承载 `DpapiCredentialVault` 与 `System.Security.Cryptography.ProtectedData` 依赖。
- `RemoteFlow.App`（WPF）改为引用 `Infrastructure` + `Infrastructure.Windows`。

### 6.3 AppPaths 平台感知

- Windows：`%LOCALAPPDATA%\RemoteFlow`（不变）。
- macOS：`~/Library/Application Support/RemoteFlow`。
  - ⚠️ .NET 在 macOS 上 `Environment.SpecialFolder.LocalApplicationData` 解析为 `~/.local/share`（遵循 XDG），不符合 macOS 惯例，**必须显式处理**。
  - 同一坑存在于 `TerminalAssetStore`（也用了 `LocalApplicationData`），随 §6.5 一并修正。
- 日志目录统一放数据目录下 `logs/`（与 Windows 版一致，简化备份说明）。
- `DataDirectory` 自定义覆盖逻辑保留。

### 6.4 Keychain 凭据保险库

- 新增 `RemoteFlow.Infrastructure.Mac/Security/KeychainCredentialVault : ICredentialVault`。
- P/Invoke `Security.framework`：`SecItemAdd` / `SecItemCopyMatching` / `SecItemUpdate` / `SecItemDelete`，`kSecClassGenericPassword`，`kSecAttrService = "RemoteFlow"`，`kSecAttrAccount = <引用键>`。
- 存储模型对齐 DPAPI 版：SQLite 存 `Credential` 元数据 + 引用键；Keychain 存密文条目。把 `remoteflow.db` 单独拷走同样得不到 Secret。
- `ResolvedCredential` 生命周期语义不变：解析即用、用后立即 `Dispose`、`CryptographicOperations.ZeroMemory` 清零明文缓冲。
- 非沙盒 Developer ID 分发下的 Keychain 访问提示行为需实测（首次访问系统弹窗授权、后续静默）。
- 需补一套与 `DpapiCredentialVaultTests` 对等的 `KeychainCredentialVaultTests`（放 Mac 专属测试工程或加平台守卫）。

### 6.5 渲染面抽象 + 终端资产下沉

**渲染面：**

- `Community.MarcusW.VncClient` 已提供 `IRenderTarget` / `IFramebufferReference` 抽象。
- 将 RFB 会话逻辑（`VncSession`、`VncKeyMapper`、`VncConnectionProvider`）降到 `net10.0`。
- `VncRenderTarget` 当前直接依赖 `System.Windows.Media.Imaging.WriteableBitmap` → 拆为：
  - **`IFrameRenderSurface`**（暴露像素缓冲写入 + 尺寸变更事件）——按 §3.2，此抽象由 **VNC 与 macOS RDP 共用**。
  - WPF 实现（`WriteableBitmap`，留在 `RemoteFlow.App`）。
  - Avalonia 实现（`Avalonia.Media.Imaging.WriteableBitmap`，放在 `RemoteFlow.App.Mac`）。
  - 无头实现（dump PNG），供 Phase 2 的 RDP 无头验收使用。
- 渲染面由 UI 层注入会话，协议项目不再引用任何 UI 框架。

**终端资产：**

- `TerminalAssetStore` 目前是 `RemoteFlow.App` 里的 `internal static`，且按 `RemoteFlow.App.Assets.Terminal.*` 的 LogicalName 读嵌入资源——Mac 工程既访问不到也读不出来，**"复用"不成立**。
- 将 `Assets/Terminal/**`（xterm.js 前端）与 `TerminalAssetStore` 一并移入 `RemoteFlow.Presentation`，改为 `public`，资源 LogicalName 随之调整；两端 App 各自引用。

### 6.6 抽出 RemoteFlow.Presentation

- 迁移 `src/RemoteFlow.App/ViewModels/*`（约 6000 行，14 个文件）到新 `RemoteFlow.Presentation`（`net10.0`）。
- 现存 WPF 耦合点（需抽象）：
  - `System.Windows.Application.Current.Dispatcher`（`HomePageViewModel`、`MainViewModel` 多处、`SessionTabViewModel`、`ConnectionsPageViewModel`）→ `IUiDispatcher { void Post(Action); Task InvokeAsync(Func<Task>); }`。
  - `System.Windows.Threading.DispatcherTimer`（`SessionTabViewModel` 会话时长计时）→ `IUiTimer` 或改用 `PeriodicTimer` + 调度器封送。
  - `System.Windows.Data`（`ConnectionsPageViewModel` 一处 `CollectionView`）→ 用抽象包装或改为 VM 内维护过滤后集合。
- `DialogService` / `ThemeService` / `TrayService` / `SessionViewFactory` / `Converters` 保留在各 UI 侧，接口下沉到 `Presentation`。
- Windows 版注册各抽象的 WPF 实现，行为不变。

### 6.7 Phase 1 验收

- `dotnet build` 全解决方案通过（含新项目）。
- `dotnet test tests/RemoteFlow.Core.Tests` 与拆分后的 `RemoteFlow.IntegrationTests` 在 **Windows 与 macOS 双平台**全绿。
- `RemoteFlow.IntegrationTests.Windows` 在 Windows 上全绿。
- Windows 版冒烟：连接管理、SSH 连一台、VNC 连一台、RDP 连一台、凭据增删、主题切换、托盘退出，行为与 v0.8.0 一致。

---

## 7. Phase 2 — macOS RDP 适配层

**目标：** 产出一个可无头验收的 `MacRdpSession`：帧输出到 `IFrameRenderSurface`、键鼠可注入、状态机收敛、证书可拦截。
**预估：** 约 4–6 周。
**风险等级：** 最高（但 Phase 0 的 G1 已消化主要不确定性）。
**依赖：** Phase 0 G1 通过、Phase 1 §6.5 渲染面抽象就位。**不依赖 Avalonia。**

### 7.1 FreeRDP 依赖管理

- FreeRDP 3.x 经 git submodule 或 vcpkg 纳入，构建 `osx-arm64` 动态库（`libfreerdp`、`libfreerdp-client`、`libwinpr` + OpenSSL）。
- 构建脚本固定 Phase 0 锁定的版本 tag，产物纳入 `.app` bundle（见 Phase 4），**不入 Git 历史**。

### 7.2 原生 helper C ABI（`libremoteflow-rdp.dylib`）

暴露最小 C 接口，协议与解码留在原生侧，**帧以缓冲区回调交给托管侧渲染**（§3.2）：

```c
rf_rdp_session*  rf_rdp_create(const rf_rdp_params*);
int              rf_rdp_connect(rf_rdp_session*, rf_rdp_callbacks*);
                 // callbacks: on_frame(buf, w, h, stride, dirty_rect)
                 //            on_state / on_certificate / on_clipboard
void             rf_rdp_send_pointer(rf_rdp_session*, int x, int y, uint32_t flags);
void             rf_rdp_send_scancode(rf_rdp_session*, uint16_t code, bool down);
void             rf_rdp_resize(rf_rdp_session*, int w, int h);
void             rf_rdp_set_clipboard(rf_rdp_session*, const char* utf8);
void             rf_rdp_disconnect(rf_rdp_session*);
void             rf_rdp_destroy(rf_rdp_session*);
```

- 脏矩形随帧回调上报，托管侧按需增量更新，避免整屏拷贝。
- 回调跨线程，托管侧需按现有 `VncRenderTarget` 的做法做好线程封送与缓冲区生命周期管理。

### 7.3 托管侧 `RemoteFlow.Protocol.Rdp.Mac`

- `MacRdpConnectionProvider : IConnectionProvider`（`Protocol => ProtocolType.Rdp`，`DefaultPort => 3389`，`IsAvailable` 检查 dylib 是否随包存在并可加载）。
- `MacRdpSession : IRemoteSession`：P/Invoke 上述 ABI，映射到既有会话状态机（`Idle → Connecting → Connected → Reconnecting → Disconnecting → Disconnected → Closed`，失败进 `Failed`），错误统一落 `ConnectionErrorCode`，**不落底层异常原文**。
- 帧写入 Phase 1 抽出的 `IFrameRenderSurface`。

### 7.4 服务器证书信任

- RDP 服务器证书：首次记录指纹、变化强警告、**绝不静默接受**（比照 SSH Host Key 的既有交互与落库思路）。
- 可复用 `IHostKeyRepository` 的模式新建 cert store 表，或扩展现有结构；由 UI 层实现确认策略（比照 `ISshHostKeyPolicy` 的「同步查询 + 异步确认」两步拆分）。

### 7.5 RdpOptions 选项映射表

现有 `RdpOptions` 映射的是 mstsc ActiveX 语义，换 FreeRDP 需逐项重映射。**首版结论如下**，Phase 2 需逐项实测确认：

| `RdpOptions` 字段 | Windows（mstsc ActiveX） | FreeRDP 对应 | macOS 首版 |
|---|---|---|---|
| `Domain` | `UserName` / `Domain` | `/d:` | ✅ 对齐 |
| `DisplayMode` | `SmartSizing` / `DesktopWidth&Height` | `/smart-sizing`、`/w: /h:` | ✅ 对齐 |
| `DesktopWidth` / `DesktopHeight` | `DesktopWidth` / `DesktopHeight` | `/w: /h:` | ✅ 对齐 |
| `ColorDepth` | `ColorDepth` | `/bpp:` | ✅ 对齐 |
| `RedirectClipboard` | `RedirectClipboard` | `+clipboard` | ✅ 对齐（需实现 CLIPRDR ↔ `NSPasteboard` 桥接） |
| `RedirectAudio` | `AudioRedirectionMode` | `/sound:sys:` | 🟡 取决于 FreeRDP 构建是否含 CoreAudio 后端 |
| `RedirectMicrophone` | `AudioCaptureRedirectionMode` | `/microphone:sys:` | 🟡 同上，且需 `NSMicrophoneUsageDescription` 权限声明 |
| `RedirectPrinters` | `RedirectPrinters` | `/printer` | 🟡 依赖 CUPS 后端，语义与 Windows 不同 |
| `RedirectDrives` | `RedirectDrives` | `/drive:` | ✅ 可对齐（默认仍关闭，避免无意暴露本机文件） |
| `UseMultimon` | `UseMultimon` | `/multimon` | ❌ **首版不支持**——托管单 surface 位图渲染下不可行 |
| `EnableNla` | `EnableCredSspSupport` | `/sec:nla` | ✅ 对齐 |
| `ConnectionQuality` | `NetworkConnectionType` | `/network:` | ✅ 对齐 |
| `StartFullScreen` | UI 层 | UI 层 | ✅ 对齐（Avalonia 侧实现） |

不支持项在 macOS 版连接编辑器中应**明确隐藏或置灰并说明**，不得静默忽略用户设置。

### 7.6 Phase 2 验收（无头，不需 Avalonia）

- 连上一台 RDP：帧序列 dump 成 PNG 可读、连续刷新、脏矩形正确。
- 脚本化键鼠注入在远端可见响应；resize 触发远端分辨率协商。
- 剪贴板文本双向。
- 断线 / 主动断开 / 取消 / 进程退出四条路径都收敛到 `Closed`，无残留进程 / 线程 / 内存增长。
- 证书首次信任 + 变化强警告可复现。
- §7.5 表中每一项有实测结论。

---

## 8. Phase 3 — Avalonia UI 层

**目标：** `RemoteFlow.App.Mac` 达到与 Windows 版对齐的功能面。
**预估：** 约 6–8 周。

### 8.1 工程

- `src/RemoteFlow.App.Mac`，Avalonia（`net10.0`），`CommunityToolkit.Mvvm`（已在用，复用）。
- Composition Root 对齐 `App.xaml.cs` 的装配：Repository / Vault（Keychain）/ Application 服务 / 三个 `IConnectionProvider`（Ssh / Vnc / Rdp.Mac）/ UI 服务 / ViewModel。
- ⚠️ `RemoteFlow.Application` 命名空间会遮蔽 `Avalonia.Application`（与 WPF 下遮蔽 `System.Windows.Application` 同理）→ UI 代码用完全限定名。

### 8.2 视图迁移

- WPF XAML → Avalonia XAML 差异：`Trigger` / `DataTrigger` → `Style` selector + 伪类；`x:Name` 作用域；`Visibility` → `IsVisible`；`Style.Resources` 与 `ResourceDictionary` 合并语义；附加属性写法。
- 布局与信息架构照搬设计稿（`docs/01-产品设计/UI/`）与现有 WPF 视图。
- 主题：`Theme.Light.xaml` / `Theme.Dark.xaml` → Avalonia `ThemeVariant` + `ResourceDictionary`；`ThemeService` 改用 `RequestedThemeVariant` + 系统跟随（`PlatformSettings.ColorValuesChanged`）。
- 图标资源 `Icons.xaml` 迁移为 Avalonia `StreamGeometry` / `PathIcon` 资源。
- **浮层策略**：因 §3.2 已消除 airspace，会话常驻条 / 全屏药丸 / 质量详情可用普通 Avalonia 浮层实现，**不必照搬 WPF 的 `Popup` 独立顶层窗口绕法**。

### 8.3 平台服务在 Mac 侧的实现

| 服务 | Windows（现有） | macOS（新实现） |
|---|---|---|
| `IUiDispatcher` | `Dispatcher` | `Avalonia.Threading.Dispatcher.UIThread` |
| 托盘 | `TrayService`（WinForms NotifyIcon） | Avalonia `TrayIcon` + `NativeMenu`（macOS 状态栏项） |
| 单实例 | `Local\` 命名 Mutex | Unix domain socket / `NSRunningApplication` 检测 + 唤醒已有实例 |
| `IDialogService` | WPF 无边框对话框 | Avalonia 窗口 / `Window.ShowDialog` |
| SSH 终端宿主 | WebView2 + xterm.js | Phase 0 G2 选定的 Avalonia WebView + 同一套 xterm.js 资产 |
| 会话画面宿主 | `WindowsFormsHost`（RDP）/ WPF `Image`（VNC） | Avalonia `Image` + `WriteableBitmap`，**RDP / VNC 统一** |

### 8.4 ICMP 与连接质量

- `ConnectionQualityProbe` / `ConnectionTestService` 使用 `System.Net.NetworkInformation.Ping`。
- ⚠️ .NET 在 Unix 上因权限限制会**退化为调用 `/sbin/ping` 子进程**，保真度下降（部分 `PingOptions` 不生效），且 hardened runtime / 未来沙盒下的可用性需实测。
- 若不可用，需给出降级策略：仅 TCP 连通探测 + UI 明确标注「本机无 ICMP，抖动 / 丢包无参考值」（现有 `ConnectionQualityProbe` 已有 `IcmpAvailable` 字段可承载该语义）。

### 8.5 Phase 3 验收

- 功能面对齐 Windows v0.8.0 的 macOS 可达子集，**其中 RDP 以 §7.5 表为准**。
- SSH 终端六套主题 / 粘贴安全 / 清屏 / 搜索可用。
- VNC 画面 + 输入 + 剪贴板接收可用。
- 数据迁移（§9）端到端可用。

---

## 9. 数据迁移：Windows → macOS

**这是用户第一天就会问的问题，列为首版明确需求。**

现有代码已具备跨平台迁移能力，无需新造格式：

| 数据 | 载体 | 跨平台性 |
|---|---|---|
| 连接 / 分组 / 标签 | CSV 导出导入（`ImportExportService`） | ✅ 纯文本，天然跨平台；导入已按「名称 + 主机 + 端口 + 协议」去重 |
| 凭据（含密码 / 私钥） | `.rfbackup`（`CredentialBackup`） | ✅ **不依赖 DPAPI**：PBKDF2-HMAC-SHA256（600,000 次迭代）+ AES-256-GCM + 用户口令，任意平台可解 |
| 连接历史 | 无导出 | ❌ 首版不迁移（可接受，属低价值数据） |
| 应用设置 | `settings.json` | 🟡 可手工拷贝，但含路径类设置，首版不承诺 |

**首版验收项：** 在 Windows 版导出 CSV + `.rfbackup` → 在 macOS 版导入 → 连接与凭据完整可用、可成功建立三种协议会话。

**文档产出：** 需在 README / 用户文档中给出明确迁移步骤。注意提示用户 `vault.dat` 本身**不可**直接拷贝到 Mac（DPAPI 绑定 Windows 账户），必须走 `.rfbackup`。

---

## 10. 测试策略

| 层 | 工程 | 平台 | 覆盖 |
|---|---|---|---|
| 领域 / 搜索 / CSV | `RemoteFlow.Core.Tests`（`net10.0`） | Win + Mac | 现有 4 个测试文件，不动 |
| 跨平台集成 | `RemoteFlow.IntegrationTests`（`net10.0`，改造） | Win + Mac | SQLite 仓储、凭据备份、分组、导入导出、标签、会话生命周期、质量评级、时间显示 |
| Windows 专属 | `RemoteFlow.IntegrationTests.Windows`（新增） | Win | DPAPI Vault、RDP 键盘序列、WPF 相关 |
| macOS 专属 | `RemoteFlow.IntegrationTests.Mac`（新增） | Mac | Keychain Vault、`AppPaths` macOS 语义、FreeRDP dylib 可加载性 |
| 协议运行时 | 无自动化 | 实机 | RDP / SSH / VNC 的真实连接行为，比照现有做法靠实机验证 |

---

## 11. Phase 4 — 打包与分发

**预估：** 约 1–1.5 周。

- `dotnet publish src/RemoteFlow.App.Mac -c Release -r osx-arm64`（先自包含单目录，评估是否 trim / AOT）。
- 组 `.app` bundle：`Info.plist`（`CFBundleIdentifier`、`LSMinimumSystemVersion`、`NSHighResolutionCapable`、按需 `NSMicrophoneUsageDescription`）、`.icns` 图标。
- FreeRDP / OpenSSL dylib 放 `Contents/Frameworks/`，`install_name_tool` + `@rpath` 修正加载路径。
- 签名：`codesign` Developer ID Application + Hardened Runtime + entitlements（`com.apple.security.cs.allow-jit` 视 .NET 运行时需要、`com.apple.security.network.client`）；**所有 dylib / 可执行文件逐个签名**。
- 公证：`notarytool submit` + `stapler staple`。
- 打 `.dmg`，按项目命名规范：`RemoteFlow-v<MAJOR.MINOR.PATCH>-macos-arm64.dmg`。
- `scripts/` 增加 `build-macos.sh` / `sign-notarize.sh`；后续接 macOS CI runner。
- `releases/` 大文件继续走 `.gitignore` 排除 + GitHub Releases 上传；`source/` 与 `CHANGELOG.md` 入库。

### 11.1 许可合规

| 组件 | 许可 | 结论 |
|---|---|---|
| FreeRDP 3.x | Apache-2.0 | ✅ 动态链接分发于闭源产品可行；需在应用「关于」或随包文档中保留归属与许可声明 |
| OpenSSL 3.x | Apache-2.0 | ✅ 同上 |
| Avalonia | MIT | ✅ |
| Community.MarcusW.VncClient | MIT | ✅（现已在用） |
| SSH.NET | MIT | ✅（现已在用） |

需在 Phase 4 前确认 FreeRDP 构建时启用的可选模块 / 编解码器无额外许可约束。

---

## 12. 里程碑与预估

| Phase | 内容 | 预估 | 关键产出 |
|---|---|---|---|
| 0 | 可行性门禁 POC（G1 FreeRDP / G2 WebView） | ~2 周 | 两份门禁结论 + 版本选型锁定 |
| 1 | 共享层去平台化 + 回归网 | ~1.5 周 | 双平台可编译可测试；Windows 版零回归 |
| 2 | macOS RDP 适配层 | ~4–6 周 | 可无头验收的 `MacRdpSession` + 选项映射实测结论 |
| 3 | Avalonia UI 层 | ~6–8 周 | 功能对齐的 macOS 客户端 |
| 4 | 打包分发 | ~1–1.5 周 | 签名公证的 `.dmg` |
| | **合计（单人）** | **~4–6 个月** | macOS v0.1 |

> **估算说明（V1.1 修订）：** V1.0 给出的 2–3 个月是「一切顺利」的**下界**，不适合作为对外承诺。FreeRDP 从零集成（构建依赖 + 掌握 client API + 帧/输入/剪贴板/证书 + 接状态机）对不熟悉该库者更接近 5–8 周；Avalonia 侧需重画 14 个 ViewModel 对应的约 20 个视图、全套对话框、主题与托盘，6–8 周为务实值。**对外按 4–6 个月表述。**

---

## 13. 风险登记

| 风险 | 等级 | 缓解 |
|---|---|---|
| **G1** FreeRDP 集成（无官方 .NET 绑定，帧/输入循环需原生封装） | 高 | Phase 0 门禁 POC；不通过则重新决策 RDP 方案 |
| **G2** Avalonia WebView 成熟度（SSH 终端 100% 依赖） | 高 | Phase 0 门禁 POC；不通过则 SSH 终端需自绘控件（工作量另一数量级） |
| 托管位图渲染的 RDP 帧率 / CPU | 中 | Phase 0 G1 粗测；脏矩形增量更新；不达标则回退原生视图 + 重做浮层策略 |
| Avalonia XAML 迁移工作量（样式 / 触发器 / 资源体系逐屏重写） | 中 | 布局照搬设计稿；先搭导航骨架再逐页填充 |
| Keychain 非沙盒行为（访问授权提示、access group） | 中 | Phase 1 用最小样例实测 + 对等单测 |
| ICMP 在 macOS 退化为子进程调用，影响连接质量功能 | 中 | Phase 3 实测；备降级策略（仅 TCP + UI 标注无 ICMP 参考值） |
| RDP 高级选项无法全量对齐（多显示器等） | 中 | §7.5 映射表逐项实测；不支持项在 UI 明确隐藏/置灰，不静默忽略 |
| Phase 1 大范围重构引入 Windows 版回归 | 中 | §6.1 测试拆分置于第一项；Windows 版冒烟清单 |
| `Microsoft.Data.Sqlite` / `SQLitePCLRaw` 在 `osx-arm64` 的原生库打包 | 低 | 官方支持，Phase 1 验证 publish 产物 |
| `.NET 10` + Avalonia 版本兼容 | 低 | Phase 0 锁定并写入 `Directory.Packages.props` |
| 命名空间遮蔽（`RemoteFlow.Application` vs `Avalonia.Application`） | 低 | UI 层统一完全限定名（已有先例） |

---

## 14. 与现有规则的衔接

- **版本**：macOS 版与 Windows 版共用同一 `Directory.Build.props` 版本号，还是独立版本线，需在进入 Phase 4 前决策（建议共用主版本、以平台后缀区分产物）。
- **发布**：仅当明确「发布 / Release / 打版本」时才建 `releases/v<X.Y.Z>/`；macOS 产物命名 `RemoteFlow-v<X.Y.Z>-macos-arm64.dmg`。
- **依赖**：Avalonia、WebView 控件等新增包在 `Directory.Packages.props` 集中登记并在 PR 说明原因；FreeRDP 属原生依赖，在 `native/` 与构建脚本中固定版本。
- **安全红线**：Keychain 实现同样受「只有 Vault 接触明文 Secret」「不写日志 / 导出 / 异常消息」约束；RDP 服务器证书信任比照 SSH Host Key，绝不静默接受。
- **分支**：大功能开发，建议 `feature/macos-<phase>` 分支推进，不直接对 `main`。

---

## 15. 下一步

1. 评审本方案，确认门禁标准与里程碑。
2. 在开发机安装 .NET 10 SDK 与 FreeRDP 构建依赖（§5.0）。
3. 启动 **Phase 0 两个门禁 POC**（G1 FreeRDP / G2 Avalonia WebView），**并行推进，产出结论后再决定是否进入 Phase 1**。
4. Phase 0 通过后开 `feature/macos-phase1` 分支，从 §6.1 测试工程拆分开始——这是后续所有重构的安全网。
