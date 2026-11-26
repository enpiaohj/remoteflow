# RemoteFlow v0.31.0

发布日期：2026-11-26

修复在分组视图点刷新后跳回「所有设备」。
本版本仅发布 Windows（win-x64）；未含 macOS 产物。

### Fixed
- 停在某个分组（含「未分组」）时点刷新，会自动跳到「所有设备」：刷新重建左侧分组树时，清空节点会让树控件把「未选中」写回程序，随后恢复原分组时读到的已是空值。现在清空前先记住当前分组、重建期间忽略控件回写，重建后按分组恢复。新建 / 编辑连接等操作后的自动刷新走同一路径，一并修复。分组已被删除时仍回到「所有设备」。

### Upgrade / Rollback
- 从 v0.30.3 升级：先正常退出旧版，再运行本版 EXE；ZIP 解压后包含同一份 EXE。本版未修改 SQLite Schema、配置字段及凭据保险库格式。
- 回退：正常退出本版，运行保留的 v0.30.3 EXE；升级前建议在程序完全退出后备份 `%LOCALAPPDATA%\RemoteFlow`（使用 `REMOTEFLOW_DATA_DIR` 时备份对应目录）。

### Known Issues
- 用 VNC 连接 macOS 时在内网千兆下仍感觉较慢，本版未做改动，正在排查。
- 沿用此前版本：远程关机 / 重启未在真实远程设备执行；macOS 端未构建、未验证；Windows 产物未签名。

### Verification
- `dotnet build RemoteFlow.slnx -c Release --nologo`：通过，0 警告、0 错误。
- Release 测试：Core 151 项通过；Integration 687 项通过、5 项外部云服务测试跳过；Windows 287 项通过。合计 1,125 项通过，0 失败，5 项跳过。
- 分组刷新问题有真实分组树控件测试：修复前普通分组与「未分组」两种情况均失败，修复后通过。
- `dotnet publish src/RemoteFlow.App -c Release -r win-x64 --nologo`：通过，重新构建正式产物。
- 最终正式 EXE：隔离数据目录启动，主界面正常、版本显示 v0.31.0、可进入设置、退出 code 0，未残留 SQLite WAL / SHM 文件。
- 未执行：升级 / 回退启动验证（本版未改数据格式）。

### Artifacts
- `RemoteFlow-v0.31.0-win-x64.exe`
  - Size：75,383,502 bytes
  - SHA-256：`27D2A1CDC0C7B6939A966CE4A108E6A9219CB3EA67FFCEBACAE9CB31DF54CE23`
- `RemoteFlow-v0.31.0-win-x64.zip`
  - Size：69,784,133 bytes
  - SHA-256：`3BA0D2EAB2F44BA49E516CBF748C00B81A8F042A2F735DE2A5C0439F451EE763`
- `RemoteFlow-v0.31.0-source.zip`：由 Tag `v0.31.0` 经 `git archive` 导出的源码压缩包（SHA-256 见 GitHub Release 页面）。
