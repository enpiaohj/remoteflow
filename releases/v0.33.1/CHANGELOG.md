# RemoteFlow v0.33.1

发布日期：2026-11-27

本版本提供 Windows x64 产物。未含 macOS 产物。

### Fixed
- PowerShell 会话套用终端外观后，连接会使用页面最近一次上报的行列数，修复 `clear` 清屏后光标错位和多余滚动。Windows SSH 会话也使用更新后的尺寸同步逻辑。

### 升级与回退
- 升级时正常退出 v0.33.0，再运行本版 EXE。ZIP 解压后包含同一份 EXE。
- 回退时正常退出本版，再运行保留的 v0.33.0 EXE。此次修复未涉及数据库、凭据保险库或设置格式。

### 验证
- Windows Release 构建通过，0 警告、0 错误。Core 151 项、Integration 713 项、Windows 351 项通过，常规测试共 1,215 项通过、12 项跳过。
- 本机 WinRM 回环首轮有 1 项异常断开测试失败，原因未确定。后续两轮各 13 项全部通过。
- 开发构建中用实际 WinRM、ConPTY 与 WebView2 复现并验证了 `clear` 后的光标和滚动位置。正式 EXE 使用隔离数据启动，显示 `v0.33.1`，可进入设置，退出码为 0，无 SQLite WAL 或 SHM 残留。
- 未执行独立远程服务器上的 `clear`、Windows SSH 终端实测、运行中切换全屏后的清屏走查及 macOS 构建验证。

### 已知问题
- 上一版记录的凭据保险库偶发保存失败仍待排查。Windows EXE 未签名。

### Artifacts
- `RemoteFlow-v0.33.1-win-x64.exe`
  - Size：75,461,513 bytes
  - SHA-256：`899249FC71A1DBACF8AA40E1E7E95522145731223FA7C0188B825F56EEAC328E`
- `RemoteFlow-v0.33.1-win-x64.zip`
  - Size：69,862,434 bytes
  - SHA-256：`6293D2566BE1F92B4404F684E6A61C1FCCA024CF719876366829B0B7795CF902`
