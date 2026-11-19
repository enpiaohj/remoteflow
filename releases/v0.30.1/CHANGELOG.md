# RemoteFlow v0.30.1

发布日期：2026-11-19

修复三个反馈问题并支持拖动调整会话标签顺序。
本版本仅发布 Windows（win-x64）；未含 macOS 产物。

### Fixed
- 主机已有会话时编辑并更换凭据：「保存并连接」不再只聚焦仍用旧配置的旧会话，而是确认后关闭旧会话并按新配置重连；会话标签的「重新连接」也改为读取最新保存的配置。
- 连接列表按「在线 / 离线」筛选后点击刷新，列表不再被清空：刷新沿用上次探测结果，探测范围覆盖除状态外全部筛选结果，探测完成后按新结果自动重新筛选。

### Added
- 会话与文件传输标签可在标题栏拖动调整顺序；工作台等页面标签固定在最前，点击关闭按钮不会触发拖动。

### Upgrade / Rollback
- 从 v0.30.0 升级：先正常退出旧版，再运行本版 EXE；ZIP 解压后包含同一份 EXE。本版未修改 SQLite Schema、配置字段及凭据保险库格式。
- 回退：正常退出本版，运行保留的 v0.30.0 EXE；升级前建议在程序完全退出后备份 `%LOCALAPPDATA%\RemoteFlow`（使用 `REMOTEFLOW_DATA_DIR` 时备份对应目录）。

### Known Issues
- 标签拖动手感、「保存并连接」重连确认流程以代码与状态测试覆盖为主，无自动化鼠标 / 对话框走查。
- 沿用 v0.30.0：远程关机 / 重启未在真实远程设备执行；macOS 未构建、未验证；Windows 产物未签名。

### Verification
- `dotnet build RemoteFlow.slnx -c Release --nologo`：通过，0 警告、0 错误。
- Release 测试：Core 151 项通过；Integration 684 项通过、5 项外部云服务测试跳过；Windows 279 项通过。合计 1,114 项通过，0 失败，5 项跳过。
- `dotnet publish src/RemoteFlow.App -c Release -r win-x64 --nologo`：通过，重新构建正式产物。
- 最终正式 EXE：隔离数据目录启动、窗口正常、退出 code 0，未残留 SQLite WAL / SHM 文件；程序版本 0.30.1。
- 未执行：真实鼠标拖动与「保存并连接」重连对话框的人工走查（已提供开发构建供体验，但此处未记录结果）；升级 / 回退启动验证（本版未改数据格式）。

### Artifacts
- `RemoteFlow-v0.30.1-win-x64.exe`
  - Size：75,379,156 bytes
  - SHA-256：`66BBF2ED0497CFEDF7D70A0BCFE4A84C8E0DF9E220D4F015AA6A825830AD6E0B`
- `RemoteFlow-v0.30.1-win-x64.zip`
  - Size：69,779,078 bytes
  - SHA-256：`7BDE7A42A2F39B7AEDD73270D530A53BA1B230E735C655DF0C46222741E8C2A1`
- `RemoteFlow-v0.30.1-source.zip`：由 Tag `v0.30.1` 经 `git archive` 导出的源码压缩包（SHA-256 见 GitHub Release 页面）。

