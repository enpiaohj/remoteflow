# RemoteFlow v0.26.3 — 2026-10-16

修复 Windows RDP 在高 DPI 场景下字体、图标过小、看起来分辨率异常偏高的问题。
本版本仅含 Windows（win-x64）；macOS 未改动、未含产物。

## 修复

- 首次连接前按本机 DPI 设置远端 DesktopScaleFactor / DeviceScaleFactor，动态调整不再强制 100% 缩放。
- 分别计算像素与毫米尺寸，修正动态显示参数的单位混用。
- 登录完成、自动重连、跨屏 DPI 变化及隐藏会话 Tab 切回时补发显示更新。
- 适应窗口模式遵循动态分辨率协议限制；手动固定分辨率保持配置值。
- 释放视图时退订新增事件并停止延迟更新，避免迟到回调重启显示调整。

## 安装与升级

- Windows 11 x64，下载 exe 直接运行，或解压 zip 后运行 `RemoteFlow.exe`；均为自包含产物，无需安装 .NET。
- 退出旧程序后运行新版本，并重新建立 RDP 连接使首次握手的 DPI 设置生效。
- 没有数据库迁移，不修改保存的连接配置与凭据。

## 已知问题

- 缩放与动态调整取决于服务器及 ActiveX 支持；不支持动态调整时保留 SmartSizing 回退。
- macOS 构建与实机验证未执行，本版本未含 macOS 产物。
- 发布产物未做代码签名。

## 验证

- Release 构建 0 警告 / 0 错误，使用 `--artifacts-path artifacts/release-v0.26.3` 独立输出目录。
- Core 151 通过；共享集成测试 455 通过、5 项预设跳过；Windows 201 通过。
  未跟踪、会写入备份目录的临时 `ZzCtxProbe` 已排除，不进入源码快照。
- Mac 专属测试在 Windows 上 10 项预期跳过，不代表完成 macOS 验证。
- 新增 18 项定向回归，含真实 ActiveX 的连接前 DPI 属性读写与 WPF 生命周期测试。
- 用户已在开发构建上确认修复效果。
- 正式产物 FileVersion `0.26.3.0`；用户已确认主界面、界面版本与设置页正常。
- 正常退出后 `CleanExit=true`，无 `remoteflow.db-wal` / `-shm` 残留；监测未返回进程退出码数值。

## Artifacts

- `RemoteFlow-v0.26.3-win-x64.exe`
  - Size：75,231,975 bytes
  - SHA-256：`041116DFC894899A60A84163D689897B11D566FB5689DA7A6EBA9F0267406300`
- `RemoteFlow-v0.26.3-win-x64.zip`
  - Size：69,765,878 bytes
  - SHA-256：`C398D9FB51BAA81139E2961305BA6F6E6185AF2ACE484C1C7BE3E15D47BEEBA9`

## 源码与追溯

- 修复提交：`a4104b9ffa20ce1b3a906910c7cb0e1dfbf2ce20`。
- 源码与版本提交：`cbffc28dba5b527dac5b656b72a3647bfb49f6c4`；快照由该提交的白名单归档导出。
- 正式版本 Tag：`v0.26.3`，对应 `release: RemoteFlow v0.26.3` 发布提交。
- 本地源码快照：`releases/v0.26.3/source/`，排除 Git 元数据、历史版本、构建输出、用户数据及未跟踪文件。
- GitHub Release：https://github.com/enpiaohj/remoteflow/releases/tag/v0.26.3 。
