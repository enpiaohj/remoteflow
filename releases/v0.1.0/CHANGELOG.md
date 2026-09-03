# RemoteFlow v0.1.0

发布日期：2026-09-04

首个正式版本。目标：先把「连接」这件事做扎实——连接资产的组织、检索，以及
RDP / SSH / VNC 三种协议的会话体验与凭据安全。

## Added

### 连接资产管理
- 连接的增删改查；多级分组树；跨分组的标签；收藏。
- 连接历史：记录每次连接的时间、时长、结果（标准化错误码，不落底层异常原文）。
- 全局搜索：按名称 / Host / IP / 分组 / 标签 / 备注即时过滤，按相关度排序，`Ctrl+K` 聚焦。
- CSV 导入 / 导出：导出文件不含任何 Secret（仅连接元数据）。

### 多协议会话
- **RDP**：宿主系统自带的 `mstscax` ActiveX 控件。
  `RdpControlLocator` 逐版本实际实例化验证后再选用，规避 Windows 11 上的注册残留。
  支持 FitToWindow / 固定分辨率、剪贴板 / 驱动器 / 打印机 / 音频重定向、NLA 开关、
  跨 DPI 分辨率跟随、`Ctrl+Alt+End → 远端 Ctrl+Alt+Del`。
- **SSH**：SSH.NET 承载协议，xterm.js + WebView2 渲染终端（xterm 资源随包离线分发）。
  主机密钥首次记录、指纹变化时强警告，绝不静默接受。
- **VNC**：纯托管 RFB 客户端（Community.MarcusW.VncClient）。非托管帧缓冲直接写
  `WriteableBitmap`；适应窗口缩放，1:1 模式下画面超出可视区时提供滚动条。
- 统一会话外壳：多会话并行、切 Tab 不断开会话、断线在会话内部提示并可重连，
  不弹模态错误框。
- 会话工具条：非全屏为顶部常驻长条；全屏为悬浮药丸（独立 Popup，可盖在 RDP
  原生画面之上），支持会话切换、窗口最小化 / 关闭、退出全屏、拖动、固定，
  未固定时自动隐藏——鼠标移到屏幕顶沿唤出、单击远端画面即收起。
- 会话全屏：按当前显示器完整物理边界铺满（不使用最大化，避免无边框窗口
  向四周溢出裁掉远端任务栏）；`F11` / `Esc` 经低级键盘钩子响应，键盘焦点在
  内嵌原生画面里时同样有效。

### 安全
- Credential Vault：密码 / 私钥 / Passphrase 经 Windows DPAPI（`CurrentUser` +
  应用专属 entropy）加密，存于独立的 `vault.dat`，连接库只保存引用键。
- 明文 Secret 仅在建立连接的瞬间接触，用后 `CryptographicOperations.ZeroMemory` 清零。
- 日志经 `SecretRedactingEnricher` 脱敏（password / token / privatekey / passphrase
  等字段）；Secret 不进日志、不进 SQLite、不进导出文件、不进异常消息、不进 `ToString()`。
- UI 默认不显示密码；`PasswordBox` 不参与数据绑定。

### 平台与体验
- 深 / 浅双主题，可跟随系统。
- 系统托盘：关闭按钮可配置为最小化到托盘以保留后台会话。
- 响应式窗口：窄屏自动收起右侧详情面板。
- 单实例运行：再次启动时激活已有窗口。
- 启动 / 退出健壮性：SQLite 打开带退避重试；优雅退出关闭全部会话、
  释放服务、checkpoint WAL。
- i18n 基础设施：`Strings.resx` 资源与文化切换入口就位（v0.1 内置简体中文）。

## Fixed

- 会话工具条「全屏」按钮与 `F11` 之前无响应。
- 全屏后左侧留白、远端画面缩在左上角、远端任务栏 / 右侧被裁。
- 全屏悬浮工具条不自动隐藏（顶沿轮询把自动隐藏计时器反复清零）。
- 启动时 SQLite `disk I/O error` / 退出时 `IAsyncDisposable` 异常 / 关闭过程弹框。

## Known Issues

- RDP 服务器证书告警对话框由系统 `mstsc` 控件弹出（非本应用 UI）；用户勾选
  「不再询问」后由 Windows 记住该服务器，证书变化时 `mstsc` 仍会重新告警。
- 单文件 `RemoteFlow.exe` 单独运行时 RDP / VNC 可用；SSH 终端需要同目录下的
  `Assets/Terminal/`，请使用 `.zip` 包（含该目录）。
- 强制结束进程会残留 SQLite `-wal` / `-shm`；下次启动由退避重试自动接管，
  正常退出则会 checkpoint 清理。
- SSH / VNC 未在本轮做真实设备的深度联调，人工测试清单见
  `docs/02-测试/2026-09-03-RemoteFlow-V0.1.0-SSH-VNC人工测试清单-v1.0.md`。

## Verification

- Build：`dotnet build -c Release` → 0 警告 0 错误
- Tests：47 通过 / 0 失败（32 单元 + 15 集成）
- Publish：`dotnet publish -c Release`（win-x64、self-contained、单文件、压缩）
- Runtime：RDP 实机连接 POC DC（`cygdi\piaohj`）验证——连接、渲染、
  非全屏常驻长条、`F11` 进/退全屏、全屏边到边、药丸自动隐藏 / 顶沿唤出、
  优雅退出（exit 0、WAL 清零、日志无 ERR/FTL）
- Platform：Windows 11 Pro（10.0.26200）
- Architecture：x64

## Artifacts

| 文件 | 说明 | SHA256 |
| --- | --- | --- |
| `RemoteFlow-v0.1.0-win-x64.zip` | 完整发布包（含 `Assets/Terminal/`），**推荐** | `8ceb99e8134aba5a77f0e559aacc4f8ec663bae0ad2059690a900e43607109f2` |
| `RemoteFlow-v0.1.0-win-x64.exe` | 单文件（RDP / VNC 可用，SSH 终端需 zip 包） | `7679983a62581399a37727f31411c9a590656829062a165eee8b8aa52423223a` |

产物为本地磁盘不可变快照，不入库；`source/` 与本文件入库。

## Git

- Commit：`release: RemoteFlow v0.1.0`
- Tag：`v0.1.0`
- 快照对应源码：`edfbc8f`（发布二进制 ProductVersion 内嵌同一 commit）
