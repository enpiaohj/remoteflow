# RemoteFlow macOS 版技术方案

> **产品名称：** RemoteFlow
> **文档类型：** 技术方案 / 实施路线
> **文档版本：** V1.2.1
> **日期：** 2026-09-06
> **状态：** Phase 0 门禁 POC 已完成并通过 / 可进入 Phase 1
> **关联基线：** `docs/01-产品设计/2026-09-03-RemoteFlow产品设计文档-v1.1.md`

---

## 0. 修订记录

| 版本 | 日期 | 主要变更 |
|---|---|---|
| V1.0 | 2026-09-06 | 初版（commit `3a3ff58`，原文可从 Git 历史检出） |
| V1.2.1 | 2026-09-06 | Phase 1 实施中的实测更正：`LocalApplicationData` 在 macOS 上已正确解析到 `~/Library/Application Support`，原「需平台分支」判断错误，§6.3 相应收敛为加回归测试 |
| V1.2 | 2026-09-06 | **Phase 0 门禁 POC 实测完成，G1 / G2 均通过**，据实测结果修订。① 目标架构改为 universal（开发机为 Intel，见 §2）；② G2 前提修正：Avalonia 有一方 MIT WebView，原「无一方 WebView」判断错误；③ airspace 结论细化：RDP / VNC 已消除，**SSH 终端仍存在**（WebView 为 NativeControlHost）；④ §5 改为实测结果记录；⑤ §7.1 补 FreeRDP 精确构建配置（不可用 brew formula）；⑥ §7.5 按实测更新音频 / 色深行；⑦ 新增 §5.5 已知问题（WKWebView 延迟挂载、`chrome.webview` shim）；⑧ §11 补 universal 双架构打包；⑨ 里程碑扣除已完成的 Phase 0 |
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
- 分发：Developer ID 签名 + 公证的 `.dmg`，**universal（x86_64 + arm64）**。

### 1.2 首版非目标

- 云同步 / 团队 Vault（与 Windows 版同步节奏，属后续版本）。
- Mac App Store 上架（首版走 Developer ID 直分发，沙盒化留待评估）。
- Windows 版迁移到 Avalonia（本方案不触碰 WPF 工程）。
- **RDP 多显示器**（`UseMultimon`），原因见 §7.5。

---

## 2. 决策记录

| 决策项 | 选择 | 理由 |
|---|---|---|
| UI 技术 | **Avalonia + 复用 .NET 后端** | Core / Application / SSH 可原样复用；ViewModel 层（CommunityToolkit.Mvvm）大部分可共享；SwiftUI 原生会作废大半 C# 资产 |
| RDP 是否进首版 | **进首版** | Mac 用户存在真实 RDP 需求（管理 Windows Server / 跳板场景），不做等于半成品 |
| 仓库结构 | **同一 `remoteflow` 仓库多项目** | 符合「一个产品一个仓库」现状；共享层改一次两端受益；避免 submodule / 私有 NuGet 的同步成本 |
| RDP 画面承载 | **帧回调 + 托管位图渲染**（V1.1 修订） | 与 VNC 同一条渲染路径，规避 airspace；并使 RDP 适配层可脱离 Avalonia 独立验收，见 §3.2 |
| 目标架构 | **universal（x86_64 + arm64）**（V1.2 修订） | 开发机为 Intel（MacBookAir9,1 / i5-1030NG7），无法原生构建或运行 arm64；而用户侧多为 Apple Silicon。双架构最贴合现实。代价见 §11.2 |

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

1. **RDP / VNC 两条会话路径上的 airspace 问题消失**，其上的 Avalonia 浮层天然可用。
2. Phase 1 抽出的渲染面抽象可由 RDP / VNC **共用**（`IFrameRenderSurface`）。
3. **RDP 适配层可脱离 Avalonia 独立验收**——帧 dump 成图即可做无头验证，从而解除原 V1.0 中「Phase 1 验收依赖 Phase 2 才建的 Avalonia 壳」的依赖倒置。

代价是像素搬运的 CPU 开销。Phase 0 实测给出了强支持：**脏区仅占整屏 4.1%**，配合脏矩形增量更新可省去 95.9% 的拷贝量（详见 §5.2）。

> **⚠️ V1.2 重要修正 —— airspace 并未被完全消除。**
> Phase 0 实测发现 Avalonia 的 WebView（`NativeWebView`）在 macOS 上报告
> `SupportedScenarios = NativeControlHost`，即它**是原生控件宿主**。
> 因此结论应分协议表述：
>
> | 会话类型 | 画面承载 | airspace |
> |---|---|---|
> | RDP | 托管位图（本节方案） | ✅ 已消除 |
> | VNC | 托管位图（既有方案） | ✅ 已消除 |
> | **SSH 终端** | **WebView / WKWebView** | ❌ **仍存在** |
>
> 也就是说：SSH 会话 Tab 之上的会话常驻条、全屏药丸、连接质量浮层，
> **仍需沿用 Windows 版那套「独立顶层窗口」策略**（对应
> `SessionHostView.xaml:206` 的 `Popup` 做法）。Phase 3 必须验证
> Avalonia 的 popup 在 macOS 上能稳定盖住 WKWebView。

### 3.3 两个门禁 —— Phase 0 实测结论

原定两项假设不成立即须改变整个方案。Phase 0 已实测完成，**均通过**：

| 门禁 | 原假设 | 实测结论 |
|---|---|---|
| **G1 FreeRDP** | 可在 macOS 构建 FreeRDP 3.x，并经 C ABI 拿到帧 / 注入输入 / 拦截证书 | ✅ **通过**。FreeRDP 3.31.0 构建成功，NLA 认证、帧交付、键鼠注入、证书拦截全部验证。详见 §5.2 |
| **G2 Avalonia WebView** | 存在可用的 Avalonia WebView（WKWebView）承载 xterm.js | ✅ **通过（附一个必须遵守的绕法）**。仓库现有 `terminal.html` + `xterm.js` 未经改动即可在 WKWebView 中正确渲染。详见 §5.3 |

**G2 的原风险判断是错的，须更正：** V1.1 称「Avalonia 无一方 WebView，社区方案成熟度参差」。
实际情况是 **`Avalonia.Controls.WebView` 12.1.0 由 AvaloniaUI OÜ 官方发布，MIT 许可，原生 `net10.0` target**。
该项风险等级由「高」降至「低」。

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

## 5. Phase 0 — 门禁 POC（已完成）

**执行日期：** 2026-09-06　**结论：G1 / G2 均通过，可进入 Phase 1。**

### 5.0 验证环境

| 项 | 值 |
|---|---|
| 开发机 | MacBookAir9,1（2020 Intel）/ Core i5-1030NG7 @ 1.10GHz / **x86_64，无 arm64** |
| 系统 | macOS 15.7.9 (24G830) |
| .NET SDK | **10.0.400**（与 `global.json` 精确匹配），装于 `~/.dotnet`，免 sudo |
| 工具链 | Xcode CLT / Apple clang 17.0.0、CMake 4.4.3、pkgconf 3.0.7、OpenSSL 3.6.4 |
| 测试主机 | RDP：Windows DC `192.0.2.11`（域 CYGDI）；SSH：Ubuntu `192.0.2.20` |

> 开发机为 Intel 是本阶段最重要的环境发现，直接导致目标架构改为 universal（§2）。
> 好处是：**在现代 Mac 中最慢的一档硬件上测出的性能数据构成可靠下界**——这里跑得动，
> Apple Silicon 上必然更好。

### 5.1 附带验证：共享层可复用性（超出门禁范围）

在正式跑门禁前，先实测了 §3.1 中「零改动可复用」的断言：

| 项目 | 结果 |
|---|---|
| `RemoteFlow.Core` | ✅ 编译通过，0 警告 0 错误 |
| `RemoteFlow.Application` | ✅ 编译通过，0 警告 0 错误 |
| `RemoteFlow.Protocol.Ssh` | ✅ 编译通过，0 警告 0 错误 |

更进一步，用一个控制台探针驱动 `SshConnectionProvider` 连接真实 Ubuntu 主机，**SSH 协议层端到端可用**：

```
[HostKey] Lookup #1  → 未知 → 中止握手
[HostKey] 确认 #1    首次连接=True → 接受并记录
[HostKey] Lookup #2  → 已记录 → 静默放行
指纹 ssh-ed25519 4XEIfpP9+aUJ0zLAzIOpFHQcGNYDQx70Zqdcv/0VGdc

状态轨迹：Idle→Connecting | Connecting→Connected | Connected→Disconnected | Disconnected→Closed
Shell 流 / Resize / 命令回显 / 状态机收敛：全部正常
```

**结论：** SSH 协议层与 Host Key 的「同步查询 + 异步确认」两步拆分在 macOS 上行为与 Windows 完全一致，**零代码改动**。这比原计划提前锁定了一大块确定性。

### 5.2 G1 —— FreeRDP：通过

**构建。** 关键发现：**不能使用 Homebrew 的 `freerdp` formula**——那是 X11 实现，带 19 个依赖（libx11、libxcursor、sdl3、ffmpeg…）。必须从源码构建并关闭全部图形前端，精确配置见 §7.1。

产物干净：

| 库 | 体积 |
|---|---|
| `libfreerdp3.dylib` | 2.5 MB |
| `libwinpr3.dylib` | 977 KB |
| `libfreerdp-client3.dylib` | 612 KB |
| **合计** | **≈ 4.1 MB** |

外部依赖**仅 OpenSSL 一项**（Homebrew 路径，打包时需 `install_name_tool` 重定向），其余全部为系统框架。`otool -L` 确认无任何 X11 / SDL / FFmpeg 残留。

**六个验证点实测：**

| # | 验证点 | 结果 |
|---|---|---|
| 1 | TLS / NLA 认证 | ✅ `Logon Info V2 [CYGDI\testadmin]`，连接耗时 2.98 秒 |
| 2 | 帧完整可读 | ✅ 导出 4 帧，转 PNG 后为清晰的 Windows 登录界面，色彩 / stride 均正确 |
| 3 | 键鼠注入 | ✅ 鼠标移动在远端对应坐标产生 64×64 脏区，证明远端确实响应 |
| 4 | 分辨率协商 | ⚠️ **未测**。`disp` 动态通道已加载，但本次未主动触发 resize |
| 5 | 证书回调可拦截 | ✅ `VerifyCertificateEx` 触发 1 次，可拿到 CN / subject / issuer / 指纹 |
| 6 | 帧率与脏区 | ✅ 212 帧；**脏区占比仅 4.1%**（8,929,280 / 217,088,000 px） |

**验证点 6 是本阶段最有价值的数据**：脏区占比 4.1% 意味着配合脏矩形增量更新，托管位图渲染只需搬运约 4% 的像素量。**§3.2 的托管渲染决策由此获得实测支撑**，且这还是在最慢的 Intel 硬件上测得。

**附带发现：**
- 已启用的通道：`cliprdr`（剪贴板）、`disp`（分辨率）、`drive`、`printer`、`audin`（麦克风）、`rdpei`、`ainput`——正好覆盖 §7.5 所需。
- dylib 链接了 `CoreAudio` / `AVFoundation` / `AudioToolbox`，**确认音频与麦克风后端已编译进去**（§7.5 相应行据此更新）。
- 请求 `ColorDepth=32`，服务端实际协商到 **16bpp**（`GFX=0`，图形管线未启用）；FreeRDP 本地仍统一转成 BGRA32，不影响渲染路径。

### 5.3 G2 —— Avalonia WebView：通过（附必守绕法）

**选型更正。** `Avalonia.Controls.WebView` **12.1.0**，作者 **AvaloniaUI OÜ（官方）**，**MIT** 许可，原生 `net10.0` target，依赖 Avalonia 12.0.0。Avalonia 当前版本为 **12.1.2**（V1.1 假设的 11.x 已过时）。macOS 后端为 **WKWebView**。

**实测结果：** 加载仓库**未经任何改动**的 `src/RemoteFlow.App/Assets/Terminal/terminal.html`（含 xterm.js / addon-fit / addon-webgl / addon-search）：

```
[探测] {"rfTerminal":"object","xterm":"function","cols":"ready","chromeWebView":false}
[宿主→JS] InvokeScript 调用成功 3 次（setTheme / write / fit）
[回读] {"hasXtermDom":true,"text":"RemoteFlow — Phase 0 / G2 验证\n  ✓ Avalonia 12.1.2 + Avalonia…"}
```

经 `PrintToPdfStreamAsync()` 导出渲染结果，肉眼确认：ANSI 色彩、256 色块、粗体、下划线、反显、**中文渲染**全部正确，深色主题经 `setTheme` 生效。

**可用 API：** `Navigate` / `NavigateToString` / `InvokeScript` → `Task<string>` / `WebMessageReceived`（`Body` 字符串）/ `NavigationCompleted` / `PrintToPdfStreamAsync`，另有 `NativeWebViewCommandManager` 提供 Copy / Cut / Paste / SelectAll——正是终端剪贴板所需。

### 5.4 Phase 0 产出的两个必须遵守的约束

**① WKWebView 必须延迟到布局完成后创建（否则进程 SIGILL）**

现象：`NativeWebView` 一旦在宿主尚无确定尺寸时进入视觉树，进程立即以 `SIGILL`（exit 132）崩溃，无任何托管异常。崩溃栈：

```
AppKit  _NSViewValidateGeometry      ← 几何校验失败，AppKit 硬陷阱
AppKit  -[NSView initWithFrame:]
WebKit  -[WKWebView initWithFrame:configuration:]
```

隔离诊断已确认：纯 Avalonia 12.1.2 在同机器完全正常；崩溃仅发生在 WKWebView 实体化那一刻；显式设置 `Width` / `Height` **无效**。

**绕法（已验证有效）：** 容器先入视觉树，待其 `Bounds` 取得确定值后再挂载 WebView。

```csharp
// 宿主容器先入树
Opened += (_, _) => DispatcherTimer.RunOnce(() =>
{
    // 此时 host.Bounds 已是确定值，再创建 / 挂载 WebView
    host.Child = webView;
}, TimeSpan.FromMilliseconds(600));
```

Phase 3 实现 SSH 终端宿主时**必须遵守**此约束，并封装成一个统一的延迟挂载宿主控件，避免散落各处。

**② `terminal.html` 的 JS→宿主 通道需要抽象**

`terminal.html:332` 使用 `window.chrome.webview.postMessage`——**WebView2 专有 API**。实测 `chromeWebView:false`，确认其在 WKWebView 下不存在。现有代码有 `if` 守卫故不会报错，但**消息会被静默丢弃**。

Phase 1 需把该通道抽象为宿主无关的形式（宿主侧注入适配层，页面只调用统一入口），使同一份 `terminal.html` 同时适配 WebView2 与 WKWebView。

### 5.5 Phase 0 未覆盖项（转入后续阶段）

| 项 | 说明 | 归属 |
|---|---|---|
| RDP 分辨率协商 | `disp` 通道已加载但未主动触发 resize | Phase 2 |
| arm64 构建与运行 | 开发机为 Intel，无法原生验证 | 需 Apple Silicon 机器 |
| 终端键盘 / IME / 剪贴板交互 | 本次仅验证渲染与单向写入，未做真人交互 | Phase 3 |
| Avalonia popup 能否盖住 WKWebView | SSH 会话浮层策略依赖此结论（§3.2） | Phase 3 |
| Keychain 凭据保险库 | 不属门禁范围 | Phase 1 |
| 屏幕录制权限 | 本机未授权，GUI 截图不可用；已改用 `PrintToPdf` 绕开 | 运维事项 |

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
  - ✅ **实测更正（V1.2.1）：无需平台分支。** V1.0–V1.2 曾断言 .NET 在 macOS 上会把
    `Environment.SpecialFolder.LocalApplicationData` 解析到 XDG 的 `~/.local/share`。
    **该断言错误**——那是 Linux 行为。在 .NET 10 / macOS 15 上实测结果为：

    ```
    LocalApplicationData = /Users/<user>/Library/Application Support
    ```

    即已正确落在 macOS 惯例位置，`AppPaths` **零改动**即可产出
    `~/Library/Application Support/RemoteFlow`。
  - 因此本项收敛为：抽出不产生副作用的 `AppPaths.ResolveDefaultDataDirectory()`
    并补 `AppPathsTests` 锁定各平台落点，防止运行时行为变化导致用户数据悄悄换位置。
  - `TerminalAssetStore` 同样使用 `LocalApplicationData`，同理**无需**平台分支；
    它在 §6.5 中的迁移原因仅是「`internal` 且绑死 `RemoteFlow.App` 的嵌入资源名」。
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

**版本：** FreeRDP **3.31.0**（Phase 0 已验证），经 git submodule 纳入并固定 tag，产物纳入 `.app` bundle（见 Phase 4），**不入 Git 历史**。

**⚠️ 不可使用 Homebrew 的 `freerdp` formula**——那是 X11 实现，依赖 libx11 / libxcursor / sdl3 / ffmpeg 等 19 个包。必须从源码构建并关闭全部图形前端。Phase 0 验证通过的配置：

```bash
cmake -S . -B build \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_OSX_ARCHITECTURES="x86_64;arm64" \   # universal，见 §11.2
  -DBUILD_SHARED_LIBS=ON \
  -DOPENSSL_ROOT_DIR="$(brew --prefix openssl@3)" \
  -DWITH_X11=OFF -DWITH_SDL=OFF -DWITH_CLIENT_SDL=OFF \
  -DWITH_FFMPEG=OFF -DWITH_SWSCALE=OFF -DWITH_SERVER=OFF \
  -DWITH_SAMPLE=OFF -DBUILD_TESTING=OFF -DWITH_MANPAGES=OFF \
  -DWITH_KRB5=OFF -DWITH_PKCS11=OFF -DWITH_WINPR_TOOLS=OFF \
  -DWITH_CUPS=OFF -DWITH_PCSC=OFF -DCHANNEL_URBDRC=OFF
```

产物 ≈ 4.1 MB（`libfreerdp3` 2.5M + `libwinpr3` 977K + `libfreerdp-client3` 612K），外部依赖仅 OpenSSL。

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
| `ColorDepth` | `ColorDepth` | `/bpp:` | ✅ 对齐（Phase 0 实测：请求 32 时服务端可能协商到 16bpp，FreeRDP 本地统一转 BGRA32，不影响渲染） |
| `RedirectClipboard` | `RedirectClipboard` | `+clipboard` | ✅ 对齐（需实现 CLIPRDR ↔ `NSPasteboard` 桥接） |
| `RedirectAudio` | `AudioRedirectionMode` | `/sound:sys:` | ✅ **后端已确认**（Phase 0：dylib 已链接 CoreAudio / AudioToolbox / AVFoundation），待功能实测 |
| `RedirectMicrophone` | `AudioCaptureRedirectionMode` | `/microphone:sys:` | ✅ **后端已确认**（`audin` 通道已启用），需 `NSMicrophoneUsageDescription` 权限声明，待功能实测 |
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
- **浮层策略（V1.2 修正，按协议区分）**：
  - **RDP / VNC 会话**：托管位图渲染，无 airspace，浮层用普通 Avalonia 控件即可。
  - **SSH 会话**：WebView 为 `NativeControlHost`，**airspace 依然存在**，浮层仍需独立顶层窗口（比照 `SessionHostView.xaml:206` 的 WPF `Popup` 做法）。**必须在本阶段早期验证 Avalonia popup 能否稳定盖住 WKWebView**，这是 Phase 3 的头号未知项。

### 8.3 平台服务在 Mac 侧的实现

| 服务 | Windows（现有） | macOS（新实现） |
|---|---|---|
| `IUiDispatcher` | `Dispatcher` | `Avalonia.Threading.Dispatcher.UIThread` |
| 托盘 | `TrayService`（WinForms NotifyIcon） | Avalonia `TrayIcon` + `NativeMenu`（macOS 状态栏项） |
| 单实例 | `Local\` 命名 Mutex | Unix domain socket / `NSRunningApplication` 检测 + 唤醒已有实例 |
| `IDialogService` | WPF 无边框对话框 | Avalonia 窗口 / `Window.ShowDialog` |
| SSH 终端宿主 | WebView2 + xterm.js | `Avalonia.Controls.WebView` 12.1.x（WKWebView）+ 同一套 xterm.js 资产。**必须封装延迟挂载宿主控件**，见 §5.4① |
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

- 分别 `dotnet publish -r osx-x64` 与 `-r osx-arm64`，再合并为 universal（见 §11.2）。
- 组 `.app` bundle：`Info.plist`（`CFBundleIdentifier`、`LSMinimumSystemVersion`、`NSHighResolutionCapable`、按需 `NSMicrophoneUsageDescription`）、`.icns` 图标。
- FreeRDP / OpenSSL dylib 放 `Contents/Frameworks/`，`install_name_tool` + `@rpath` 修正加载路径（Phase 0 已确认 FreeRDP 三个库外部依赖仅 OpenSSL）。
- 签名：`codesign` Developer ID Application + Hardened Runtime + entitlements（`com.apple.security.cs.allow-jit` 视 .NET 运行时需要、`com.apple.security.network.client`）；**所有 dylib / 可执行文件逐个签名**。
- 公证：`notarytool submit` + `stapler staple`。
- 打 `.dmg`，按项目命名规范：`RemoteFlow-v<MAJOR.MINOR.PATCH>-macos-universal.dmg`。
- `scripts/` 增加 `build-macos.sh` / `sign-notarize.sh`；后续接 macOS CI runner。
- `releases/` 大文件继续走 `.gitignore` 排除 + GitHub Releases 上传；`source/` 与 `CHANGELOG.md` 入库。

### 11.2 universal 双架构的额外成本

**Phase 0 发现：Homebrew 只提供与宿主同架构的单架构库。** 本机 `openssl@3` 仅 `x86_64`。
因此 universal 产物不能直接依赖 brew 的 OpenSSL，需二选一：

1. **自建双架构 OpenSSL**：分别为 x86_64 / arm64 从源码构建，再 `lipo -create` 合并。可控但增加构建脚本复杂度。
2. **vcpkg 双 triplet**：`x64-osx` + `arm64-osx` 分别构建后合并。

FreeRDP 自身可用 `-DCMAKE_OSX_ARCHITECTURES="x86_64;arm64"` 一次产出 universal，**前提是其依赖（OpenSSL）也是 universal**。

.NET 侧需分别 publish 两个 RID 再合并可执行文件与原生库。签名必须在 `lipo` 合并**之后**执行。

建议把这条链路做成 `scripts/build-macos.sh` 的固定流程，并在 Phase 2 结束时就先跑通一次，不要拖到 Phase 4。

### 11.1 许可合规

| 组件 | 许可 | 结论 |
|---|---|---|
| FreeRDP 3.31.0 | Apache-2.0 | ✅ 动态链接分发于闭源产品可行；需在应用「关于」或随包文档中保留归属与许可声明 |
| OpenSSL 3.x | Apache-2.0 | ✅ 同上 |
| Avalonia 12.1.x | MIT | ✅ |
| Avalonia.Controls.WebView 12.1.x | MIT（AvaloniaUI OÜ 官方） | ✅ Phase 0 已核 nuspec |
| Community.MarcusW.VncClient | MIT | ✅（现已在用） |
| SSH.NET | MIT | ✅（现已在用） |

需在 Phase 4 前确认 FreeRDP 构建时启用的可选模块 / 编解码器无额外许可约束。

---

## 12. 里程碑与预估

| Phase | 内容 | 预估 | 关键产出 |
|---|---|---|---|
| 0 | 可行性门禁 POC（G1 / G2） | ~~2 周~~ **已完成** | ✅ 两项门禁均通过；版本选型锁定；附带验证 SSH 层端到端可用 |
| 1 | 共享层去平台化 + 回归网 | ~1.5 周 | 双平台可编译可测试；Windows 版零回归 |
| 2 | macOS RDP 适配层 | ~4–6 周 | 可无头验收的 `MacRdpSession` + 选项映射实测结论（Phase 0 已消化主要不确定性） |
| 3 | Avalonia UI 层 | ~6–8 周 | 功能对齐的 macOS 客户端 |
| 4 | 打包分发 | ~1–1.5 周 | 签名公证的 `.dmg` |
| | **剩余（单人）** | **~4–6 个月** | macOS v0.1 |

> **估算说明（V1.1 修订）：** V1.0 给出的 2–3 个月是「一切顺利」的**下界**，不适合作为对外承诺。FreeRDP 从零集成（构建依赖 + 掌握 client API + 帧/输入/剪贴板/证书 + 接状态机）对不熟悉该库者更接近 5–8 周；Avalonia 侧需重画 14 个 ViewModel 对应的约 20 个视图、全套对话框、主题与托盘，6–8 周为务实值。**对外按 4–6 个月表述。**

---

## 13. 风险登记

| 风险 | 等级 | 缓解 |
|---|---|---|
| ~~**G1** FreeRDP 集成~~ | ~~高~~ → **低** | ✅ Phase 0 已验证：构建、认证、帧、输入、证书全通。剩余为工程量而非可行性 |
| ~~**G2** Avalonia WebView 成熟度~~ | ~~高~~ → **低** | ✅ 系官方 MIT 组件；仓库现有 xterm.js 前端未改动即可正确渲染 |
| 托管位图渲染的 RDP 帧率 / CPU | 中 → **低** | ✅ Phase 0 实测脏区仅 4.1%，且系在最慢 Intel 硬件上测得，构成可靠下界 |
| **SSH 会话浮层被 WKWebView 遮挡**（airspace 仍存在） | **中** | Phase 3 早期验证 Avalonia popup 能否盖住 WKWebView；不行则浮层改独立窗口或降级方案 |
| **universal 双架构依赖链**（brew 仅提供单架构库） | **中** | 自建双架构 OpenSSL 或 vcpkg 双 triplet，见 §11.2；Phase 2 末即跑通，勿拖到 Phase 4 |
| **arm64 未经任何实机验证**（开发机为 Intel） | **中** | 尽早取得 Apple Silicon 机器补验构建 / 签名 / 性能 |
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

Phase 0 已完成，门禁通过。后续顺序：

1. 开 `feature/macos-phase1` 分支，从 **§6.1 测试工程拆分**开始——这是后续所有重构的安全网。
2. 依次推进 §6.2 Infrastructure 拆分 → §6.3 AppPaths → §6.4 Keychain → §6.5 渲染面抽象与终端资产下沉（含 §5.4② 的 JS 通道抽象）→ §6.6 Presentation 抽取。
3. Phase 2 起把 §11.2 的 universal 构建链路一并跑通，不留到 Phase 4。
4. 尽早取得一台 Apple Silicon 机器，补验 arm64 构建、签名与性能。
