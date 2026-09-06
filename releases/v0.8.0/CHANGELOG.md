# RemoteFlow v0.8.0

发布日期：2026-09-06

上一正式版本为 v0.7.0。本版新增统一的会话连接质量详情，并完成 RDP 工具条远端任务管理器动作与输入焦点链路的实机修复。

## Added

- 会话常驻条与全屏药丸新增统一状态入口，可打开连接质量详情。
- RDP、SSH、VNC 会话统一展示连接状态、会话时长、重连次数与质量指标，并支持重新检测。
- RDP 工具条新增“启动任务管理器”，优先调用 mstsc ActiveX 的 `SendRemoteAction(TaskManager)` 官方远端语义动作。

## Changed

- RDP 的 Ctrl+Alt+Del 与远端任务管理器动作共用焦点准备流程：先恢复主窗口与 ActiveX 输入焦点，等待 WPF/WinForms 焦点交接，再触发远端动作。
- 对不支持 `SendRemoteAction` 的旧客户端保留 `IMsRdpClientNonScriptable.SendKeys` 协议级 Ctrl+Shift+Esc 回退；显式按 `VARIANT_BOOL` 与 Win32 `LONG` ABI 传递扫描码序列。
- 连接质量详情移除“参考”等说明性措辞，保持界面信息直接、简洁。

## Fixed

- 修复从常驻条直接点击“启动任务管理器”无响应的问题，无需先手动点击远端桌面。
- 修复任务管理器快捷键曾可能被本机输入管线截获、打开本机 Task Manager 的问题。
- 修复工具条夺取焦点后 RDP 动作被 ActiveX 以 `E_FAIL` 拒绝的问题。
- 移除无法可靠进入 RDP 协议输入层的宿主侧 `PostMessage` 按键模拟路径。

## Known Issues

- 无已知阻塞项。
- 本轮已实机确认常驻条可直接启动远端 Task Manager；全屏药丸使用同一动作入口，但封版后未单独再次人工复测。

## Verification

- Build：`dotnet build -c Release --nologo` → 0 错误 / 0 警告。
- Targeted Tests：`RdpKeyboardSequenceTests` → 2 / 2 通过。
- Tests：Core → 42 / 42 通过；Integration → 94 / 94 通过。
- Runtime：真实 RDP 会话中，无需预先点击远端桌面即可从常驻条启动远端 Task Manager；日志连续 3 次记录官方语义动作发送成功。
- Platform / Architecture：Windows 11 · win-x64（.NET 10 self-contained single-file）。
- Schema：数据库 schema 保持 3，无数据迁移。
- Secret Scan：未发现真实 Secret；3 个 Private Key 标记命中均已确认是测试夹具中的 `fake` 数据。

## Artifacts

- `RemoteFlow-v0.8.0-win-x64.exe`
  - Size：74,704,167 bytes
  - SHA-256：`E6C4EB29CBB6D47EF5D85174D9DF06A64A8A429AFE32A13D466A416E2ADE0F66`
- `RemoteFlow-v0.8.0-win-x64.zip`
  - Size：69,023,455 bytes
  - SHA-256：`DF19EDEE2B9CDCE0F13E2D30A0A2BCBEC1B26F3F873A5B6F39082E7704D979AB`

## Git

- Source Snapshot Commit：`d0e9e32`（`chore: 版本号提升至 0.8.0`）。
- Tag：`v0.8.0`（指向 `release: RemoteFlow v0.8.0`）。
