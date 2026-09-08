# RemoteFlow v0.9.0

发布日期：2026-09-09

上一正式版本为 v0.8.1（Windows）。本版是 Windows 平台版本：一轮 UI 精修 + 会话工具条修复，并随共享层重构带入若干协议侧改进。**macOS 原生版走独立的 `macos-v*` 版本线，不在本次发布范围内**（`RemoteFlow.App.Mac` / FreeRDP 相关内容与本 CHANGELOG 无关）。

## 架构

- **抽出 `RemoteFlow.Presentation` 工程**：ViewModel（`MainViewModel` / `ConnectionsPageViewModel` / `HomePageViewModel` / `SettingsPageViewModel` / `SessionTabViewModel` 等）与视图无关服务（`DateTimeDisplay` / `ConnectionTestService` / `ConnectionQualityProbe` / `IDialogService` / 终端资产）从 `RemoteFlow.App` 下沉到平台无关层，Windows(WPF) 与 macOS(AppKit) 双端共用同一套 VM 与格式化逻辑。UI 依赖抽象化（`IUiDispatcher` / `IUiTimer` / `IThemeService` / `ILaunchOnStartupService`）。
- `RemoteFlow.Infrastructure` 拆出 `RemoteFlow.Infrastructure.Windows` / `.Mac`；集成测试拆出 `.Windows` / `.Mac`。
- **GitHub Actions**：每次 push / PR 在 Ubuntu 跑共享测试（Core + Integration），Windows / macOS 各验「能否构建 + 平台专属集成测试」；打 `v[0-9]*` tag 自动构建 win-x64 self-contained 单文件、（有证书则签名）、打包并创建 GitHub Release 草稿。
- `Directory.Build.props` 加 `EnableWindowsTargeting=true`，使 macOS 开发机也能编译校验 Windows 侧，避免共享层改动把 Windows 版改坏而不自知。

## Added

- **会话状态灯**。连接列表、首页「最近连接」大卡、收藏行的图标右下角新增一个状态点：**空闲不显示**，连接中显琥珀点、已连接显绿点。数据源是既有的会话状态（`IsConnected` / `HasActiveSession`），纯 XAML（`SessionStatusDot` 样式 + `DataTrigger`），不新增 ViewModel 属性。
- **悬停就地连接**。「我的连接」列表行、首页收藏行鼠标移上去即在行内浮出「连接」按钮；首页「最近连接」大卡（整卡本就是连接热区）悬停时右侧浮出「连接 →」提示。
- 详情面板「协议」字段改用协议色小徽章，与列表 / 首页同一套视觉语言。
- **SSH 终端启用 xterm.js WebGL 渲染器**（共享 `terminal.html`，Windows / macOS 同源）。
- **VNC 画质旋钮**：内网高画质 + 低压缩（共享 `Protocol.Vnc/VncSession.cs`）。

## Changed

- 全屏胶囊工具条进全屏后的默认停留时间 2.5s → **3s**（与其它平台错开，给足看清工具条的时间）；单击远端画面仍由低级鼠标钩子立即收起，不受此值影响。
- 常驻条 / 全屏药丸的状态入口：只有「状态图标（+ 状态文字）」是按钮（打开连接质量详情），主机 IP 拆成旁边的静态文字，不再是点击热区——那截长、在窄药丸上极易误触。
- 首页问候区收紧：无已连接会话时不再显示「0 个会话已连接」；问候块与统计行间距、最近连接卡片最小高度下调。
- 凭据列表瘦身：「保险库」列 Secret 已保存时只显示锁图标不再每行重复「已保存密码」，仅缺失时显示「未保存…」警示；未被任何连接引用的凭据用静音警告色；列宽 132 → 92，表头「保险库状态」→「保险库」。
- 分组：老用户升级也新建「我的设备」，不再借用现有分组顶默认（共享 `Application/Services/GroupService.cs`）。

## Fixed

- **多会话全屏下切换会话，胶囊工具条丢失**。`SessionHostView` 原来只在「首次可见」时装配全屏悬浮工具条（一次性事件）。多会话切 Tab 靠显隐、视图实例不销毁，切走再切回不会再装配 → 停在全屏却没有工具条。改为常驻监听可见性：切回且处于全屏就重新装配（药丸 / 计时器 / 低级鼠标钩子），切走则拆掉交给下一个可见视图。
- **关闭会话后仍停在全屏**。多会话时关掉一个会话会跳到另一个会话或回工作区——这是一次打断性的上下文切换，却停在没有标题栏 / 导航的沉浸全屏里。改为：`MainWindow` 监听 Tab 集合，有会话 Tab 被移除且处于全屏时退出全屏，回到常驻条（主动切换会话不移除 Tab，保持全屏）。
- **双击常驻条状态入口会把窗口最小化**。状态入口单击打开的质量详情 Flyout 是 owned、无任务栏按钮的无边框置顶窗；双击走「开 → 第二击激活主窗令 Flyout 失活自关 → 重开」的反复激活，触发已知 WPF 问题最小化宿主窗。修复：状态入口加 250ms 双击去抖 + Flyout 关闭后兜底检测宿主窗口被最小化则还原。
- **Windows VNC 会话看不到远端光标**（回归）。共享 `Protocol.Vnc/VncSession.cs` 现在无条件挂 `CursorHandler`（随本地光标 / 画质旋钮改动进来），库因此不再把光标合成进帧缓冲，改为把光标伪编码交给宿主。macOS 的 `VncScreenView` 订阅了 `CursorChanged` 自己画（`NSCursor`）；WPF 的 `VncSessionView` 没订阅 → 光标消失。修复：`VncSessionView` 订阅 `CursorChanged`，把 `VncCursorShape`（RGBA + 热点）经 `CreateIconIndirect(fIcon=FALSE)` 转成原生 `HCURSOR` 再包成 WPF `Cursor` 套到画面（同 macOS 用 `NSCursor` 思路，交给 OS 按热点渲染 / 定位）。
- **SSH 中文输入法快速输入掉字**（`keyCode=229`）+ 出向批量泵（共享 `Protocol.Ssh/SshSession.cs`）。
- 稳定性打磨：堵住若干 `async void` 崩溃口，连接错误提示由错误码转成「人话」（共享 `Presentation/ConnectionErrorText.cs`）。

## Known Issues

- **P4（设置页「首页时间显示 5 控件合并为 1 个下拉」）本版未做**。该改动要删 `AppSettings` 的 `ShowHomeTime` / `ShowHomeSeconds` / `ShowWeekday` / `ShowHomeWeekNumber` / `ShowHomeTimeOrder` 并重构 `HomePageViewModel.RefreshHeader`，但 macOS 的 `DetailView.cs` 直接消费 `DateLine` / `ClockLine` / `HasClockLine`——是一次跨平台「同步删除」，须排一个协调窗口一起做。
- 会话状态灯的「连接中 / 已连接」两态、多会话全屏切换 / 关会话行为、双击最小化修复、悬停连接按钮手感、**VNC 远端光标（形状 / 热点 / 隐藏切换）**、SSH WebGL 渲染器与批量泵、SSH 输入法修复——均需实机会话验证，封版时未在真实 RDP / SSH / VNC 会话上跑（本机无可连服务器凭据）。
- 设置页的日期时间相关配置本版沿用旧的 5 控件形态（见上）。

## Verification

- Build：`dotnet build -c Release --nologo` → 0 错误（1 处 pre-existing 测试工程 `CS0067` 警告，非本版引入）。
- Tests：`dotnet test -c Release` → Core 42 / 42；Integration（共享）92 / 92；Integration.Windows 9 / 9；Integration.Mac 10 skipped（Windows 上不可运行，预期）。
- Runtime：`dotnet publish -c Release -r win-x64` 单文件启动 / 退出正常；首页、我的连接、凭据页 PrintWindow 截图确认静态呈现（角标空闲不显示、悬停连接、凭据列瘦身、协议徽章、首页统计行去噪均符合预期）。
- Platform / Architecture：Windows 11 · win-x64（.NET 10 self-contained single-file）。
- Schema：数据库 schema 保持 3，无数据迁移；`settings.json` 无字段变更，向后兼容。
- 改动范围：全部在 `src/RemoteFlow.App/`（WPF）+ `src/RemoteFlow.App/Views/Sessions/VncCursorImage.cs`（新增）；未改动 `RemoteFlow.Core` / `RemoteFlow.Presentation` / 任何 macOS 工程——macOS 版构建不受本版 UI 改动影响。

## Artifacts

由 `release-windows.yml` 在 tag 推送后构建并上传到 GitHub Release：

- `RemoteFlow-v0.9.0-win-x64.exe`（self-contained 单文件）
- `RemoteFlow-v0.9.0-win-x64.zip`（含运行时资源目录）

SHA-256 见 GitHub Release 页 / CI 日志。本地 `releases/v0.9.0/` 只保留 `source/` 与本 CHANGELOG。

## Git

- Source Snapshot Commit：`de16225`（`chore: 版本号提升至 0.9.0`）。
- 本版含自 v0.8.1 以来的 Windows 相关提交：`e471584`（会话工具条精修）、`b5aa543`（会话状态灯 / 悬停连接 / 协议徽章 / 首页收紧 / 凭据列瘦身）、`17607fa`（VNC WPF 远端光标）；以及共享层带入的 `09496b3`（SSH 输入法 + 批量泵）、`6db1166`（VNC 画质）、`e7d9258`（SSH WebGL）、`1d69b9b`（分组种子）、`82d8ff7`（稳定性 + 错误文案）、Presentation 层抽取（`23b8d4f` / `05a36eb` / `13c7bcf` / `fefb274` / `d7e6ce2`）。
- Tag：`v0.9.0`（指向 `release: RemoteFlow v0.9.0`）。
- 分支：基于 `main`（含 macOS 原生版 PR #1 合并后的统一 `main`）；本版仅在 `src/RemoteFlow.App/` 增改，不含 P4，不动共享层与 macOS 工程。
