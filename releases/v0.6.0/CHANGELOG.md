# RemoteFlow v0.6.0

发布日期：2026-09-06

上一正式版本为 v0.5.0。本版以稳定性为主，完成 Session 生命周期统一、会话实时状态同步，
并附带一批首页/菜单/托盘等 UI 增强。

> **已知说明**：以下多项（会话清理、实时状态、测试连接对话框、托盘与会话切换、首页
> 时间显示等）均通过编译与自动化测试（Release 0/0；Core 42 / Integration 85）；真实
> RDP/SSH/VNC 会话与压测仍属实机范畴，发布前已完成真机基本使用，压力回归 runbook
> 建议在真机继续覆盖（connect/close 循环、断线/重连、带会话退出）。

## Added（Session 生命周期与资源清理）

- 统一会话生命周期状态：新增 `Reconnecting` / `Closed`（`Disconnecting` 真正启用）；
  UI 会话状态层覆盖「重新连接中… / 已关闭」。
- 共享生命周期基座 `RemoteSessionBase`（线程安全状态机 / 幂等关闭 / 会话级 CTS /
  `SessionResourceTracker`）与 `IRemoteSession` 契约收敛（单次使用、幂等清理到 Closed）。
- `SessionManager` 统一关闭编排：幂等认领、两阶段有界等待（总超时）、移除后 `MarkClosed`、
  历史按会话终态记 Result 并原子防悬空/双写。
- RDP / SSH / VNC 清理加固：
  - RDP：失败路径统一退订 COM 事件与 SafeDisconnect、Subscribe/Unsubscribe 幂等、连接可取消、
    AutoReconnect 映射 `Reconnecting`、迟到回调门控。
  - SSH：`CleanupAsync` 幂等（互斥体 try/finally）、远端 EOF 即清理、连接可取消、收尾不误标 Failed。
  - VNC：连接可取消且不复活已释放会话（Interlocked 认领）、库 Closed/Interrupted 即释放、幂等空安全、
    视图全量退订；保留 v0.5.0 的输入发送修复与三档缩放。
- WebView2：共享 `CoreWebView2Environment`（引用计数），会话只释放自有 WebView；不按进程名 Kill。
- App 退出顺序：Graceful Close All →（超时 force）→ 共享资源 → SQLite/Logging/Mutex；
  `session-state.json` 崩溃标记 + 启动 recovery 日志；不跨重启恢复会话。

## Added / Changed（会话实时状态同步）

- `SessionManager` 成为唯一实时状态事实来源：`HasActiveSession` / `HasConnectedSession` /
  `GetSessionState`（可空区分“无会话”）快照查询。
- 统一聚合事件 `SessionsChanged`（快照重算信号，创建/状态跳变/移除后广播）与
  `SessionStateChanged`（携带 Session/Profile 身份）；各页面/托盘不再各自轮询或缓存 IsConnected。
- 首页实时：停留首页时「N 个会话已连接」统计与最近/收藏卡片随会话状态即时变化
  （150ms 去抖全量刷新作保险），关闭 Session 无需切页立即回落。
- 连接列表实时：我的连接 / 收藏 / 最近连接行“最近连接”列显示实时绿点「已连接」，否则恢复时间；
  收藏/最近/分组复用同一批对象全同步。
- 右键按状态动态：无会话「连接」；有活动「切换到会话 / 断开连接」（断开经 SessionManager 统一关闭）。
- 收藏/最近/首页等快捷视图状态文案统一（连接/编辑 ─ 测试连接/收藏切换 ─ 管理连接；
  复制/删除等管理操作仅在「我的连接」页提供）。

## Added / Changed（UI 与工具）

- 首页标题可选显示当前时间 / 秒，且「显示顺序」提供四种即见即得的整行预设
  （日期·星期·周数·时间等）；默认开启时间与秒。
- 「已连接」口径：状态栏 / 首页统计 / 托盘只计 `State==Connected`，随会话状态跳变即时刷新。
- 托盘右键菜单补全：打开 / 已连接会话切换（动态）/ 退出；会话切换聚焦对应 Tab。
- 首页最近连接 / 收藏行右键对齐“我的连接”（连接/编辑/测试连接/收藏切换/管理连接），右键即选中。
- 统一「测试连接」对话框：DNS / Ping / TCP 自动三步、逐行状态、结论与“可能原因”、
  折叠“查看详细信息”（完整异常写日志）、取消/重新测试、三协议复用；入口在连接右键。
- 复制连接：自动生成不冲突副本名（如 `名称 (2)`）并创建后打开编辑。
- 右侧详情去掉“测试连接”次级按钮（保留右键入口）。

## Fixed

- 关闭 Session 后首页“已连接”与计数可能滞后/不一致（改为关闭期即广播回落）。
- 连接/详情历史加载切换竞态导致详情显示错行历史。
- 复制连接在筛选/搜索视图下找不到副本导致不打开编辑。
- 会话计数在 Connecting→Connected / 断开等状态跳变时状态栏/托盘不即时刷新。
- 右键菜单在快捷页出现不应出现的复制/删除，及空/悬空分隔线问题。

## Known Issues

- 无已知阻塞项。真实协议压力回归（RDP/SSH/VNC 循环、断线重连、带会话退出）建议真机覆盖；
  主页“显示顺序-单独一行”、托盘与会话切换等视觉交互建议实机复核。

## Verification

- Build：`dotnet build -c Release` → 0 错误 / 0 警告。
- Tests：Core 单元测试 42 通过；Integration 测试 85 通过（含 SessionManager 簿记/事件、
  ConnectionTestService 等新增用例）。
- Platform / Architecture：Windows 11 · win-x64（.NET 10 单文件 self-contained）。
- Schema：数据库 schema 保持 3（与 v0.5.0 一致，无新迁移）。

## Git

- 快照源码（本版本二进制对应提交）：`ce198b8`（chore: 版本号提升至 0.6.0）
- Tag：`v0.6.0`（release: RemoteFlow v0.6.0）
