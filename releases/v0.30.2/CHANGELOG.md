# RemoteFlow v0.30.2

发布日期：2026-11-19

优化会话标签拖动体验，折叠状态下也能调整标签顺序。
本版本仅发布 Windows（win-x64）；未含 macOS 产物。

### Changed
- 标题栏标签拖动：被拖标签跟随鼠标并带抬起效果，邻居标签平滑让位，越过邻居一半宽度才换位；拖动期间捕获鼠标，拖出标签条不中断，靠近左右边缘自动滚动，松手后平滑落位并选中该标签。

### Added
- 折叠会话列表：每行新增拖动手柄，上下拖动调整顺序；列表换位只移动对应一项，不整表重建，滚动位置和选中状态不丢。
- 会话与文件传输标签的右键菜单新增「上移 / 下移」（标题栏标签显示为「左移 / 右移」），已在边界时置灰；折叠列表中选中某行后可用 Alt+↑ / Alt+↓ 移动。

### Upgrade / Rollback
- 从 v0.30.1 升级：先正常退出旧版，再运行本版 EXE；ZIP 解压后包含同一份 EXE。本版未修改 SQLite Schema、配置字段及凭据保险库格式。
- 回退：正常退出本版，运行保留的 v0.30.1 EXE；升级前建议在程序完全退出后备份 `%LOCALAPPDATA%\RemoteFlow`（使用 `REMOTEFLOW_DATA_DIR` 时备份对应目录）。

### Known Issues
- 拖动的跟手、动画、自动滚动以及折叠列表手柄拖动依赖真实鼠标操作，无自动化覆盖；换位规则、菜单可用状态与列表同步由测试覆盖。
- 沿用 v0.30.0：远程关机 / 重启未在真实远程设备执行；macOS 未构建、未验证；Windows 产物未签名。

### Verification
- `dotnet build RemoteFlow.slnx -c Release --nologo`：通过，0 警告、0 错误。
- Release 测试：Core 151 项通过；Integration 685 项通过、5 项外部云服务测试跳过；Windows 280 项通过。合计 1,116 项通过，0 失败，5 项跳过。
- `dotnet publish src/RemoteFlow.App -c Release -r win-x64 --nologo`：通过，重新构建正式产物。
- 最终正式 EXE：隔离数据目录启动、窗口正常、退出 code 0，未残留 SQLite WAL / SHM 文件；程序版本 0.30.2。
- 未执行：真实鼠标拖动手感的人工走查（提供过开发构建供体验，此处未记录结果）；升级 / 回退启动验证（本版未改数据格式）。

### Artifacts
- `RemoteFlow-v0.30.2-win-x64.exe`
  - Size：75,382,008 bytes
  - SHA-256：`5CB911BE1FE87EBF773F0A2C2D6BDCD30440A0A254F339BBA95034DEA6832815`
- `RemoteFlow-v0.30.2-win-x64.zip`
  - Size：69,782,083 bytes
  - SHA-256：`92B4C9239FBAEB49AF41A7F375DB6196CD545BBEF74FB961D8B20B7ABE679FCE`
- `RemoteFlow-v0.30.2-source.zip`：由 Tag `v0.30.2` 经 `git archive` 导出的源码压缩包（SHA-256 见 GitHub Release 页面）。
