# RemoteFlow v0.23.0

发布日期：2026-09-27 · 上一版本 v0.22.0

图标体系：除设备 / 协议 / 分组 / 标签外的其余图标全部换新——操作与状态统一为 Microsoft Fluent 线性图标
（导航选中为实心），凭据类型、设置分区、页面分区、对话框与空状态使用原创彩色身份图标。
本版本仅发布 Windows（win-x64）；macOS 界面未改动、未含产物。
详细说明见 `docs/2026-09-27-RemoteFlow-v0.23.0-图标体系说明-v1.0.md`。

### 新增
- **彩色身份图标（25 枚，原创矢量）**：凭据类型 5 枚（Windows 域账号 / 本地账号 / SSH 口令 / SSH 私钥 / VNC 口令）；
  设置分区 10 枚（外观与行为、日期与时间、启动与托盘、全屏悬浮工具条、语言、连接数据、凭据备份、应用数据、
  数据安全提示、加密保护）；收藏、历史、快速上手、会话、云同步、快捷键、信息、文件夹、标签、凭据。
  用于凭据列表、设置页、首页区块标题与安全提示、帮助 / 关于分区、分组 / 标签 / 口令对话框标题、空状态。
- **线性操作图标（65 个几何，含 10 个实心变体）**：来自 Microsoft Fluent UI System Icons（MIT，20px），由 `scripts/build-ui-icons.py`
  生成 `Themes/UiIcons.xaml`；颜色跟随按钮文字，悬停 / 禁用 / 选中自动变色。
- 导航栏与页面标签：常态线性、当前页换实心图标并用品牌色。
- 会话选择器与会话切换菜单按协议显示图标（RDP / SSH / VNC）。

### 变更
- 全部操作按钮（新建、编辑、复制、删除、刷新、搜索、批量、收藏、更多、导入导出、显示 / 隐藏密码等）与
  会话工具条、全屏药丸改用 Fluent 线性图标。
- 状态图标统一为实心：已连接（对勾圆）/ 断开或网络波动（警告三角）/ 失败（错误圆）/ 连接中（空心圆），
  覆盖会话常驻条、药丸、连接质量浮层、断线状态层、测试连接步骤与结论；消息与主机密钥对话框的级别图标同步更换。
- 帮助 / 关于分区不再铺品牌淡色底块，彩色图标自带层次。
- 窗口标题栏按钮与折叠箭头保留系统字形（原生观感）。
- 共享层仅新增只读图标键属性（`PageTabViewModel.IconKey`、`CredentialItemViewModel.TypeIconKey`、
  `SessionTabViewModel.StateIconKey` / `FlyoutStateIconKey`），原字形属性保留供 macOS 使用。
- `THIRD-PARTY-NOTICES.md` 登记 Fluent UI System Icons 并附 MIT 许可证全文。

### 已知问题
- 会话内工具条、全屏药丸、连接质量浮层的新图标已由「真实加载全部视图」集成测试覆盖，
  但未在实际远程会话中做实机目视验证。
- 页面切换后新页面内容不在 UI 自动化（辅助功能）树中（既有问题，非本版引入）。
- macOS 界面未改动（仍为 SF Symbols），未在 Mac 上编译验证。

### Artifacts
- `RemoteFlow-v0.23.0-win-x64.exe`
  - Size：75,085,182 bytes
  - SHA-256：`AC524E1E567EB9E0D21C711B9819F136D1008AB73133B21DEAEE46CE1BABE9CC`
- `RemoteFlow-v0.23.0-win-x64.zip`
  - Size：69,618,987 bytes
  - SHA-256：`3BA62FC0C89E7C92074F0EE03ED33621FC6955504E23969B0F21C8ADAE487DA0`

### Verification
- Build：`dotnet build RemoteFlow.slnx -c Release` —— 0 警告 / 0 错误
- Tests：RemoteFlow.Core.Tests 46/46 通过；RemoteFlow.IntegrationTests 249 通过、5 跳过（Mac 专属用例在 Windows 上跳过，属预期）；
  RemoteFlow.IntegrationTests.Windows 57/57 通过（含图标体系回归 6 项：键存在、20×20 画布锚定、视图无旧字形、
  许可证披露、真实加载应用资源后实例化 19 个页面 / 对话框 / 会话视图不缺资源）
- 线性图标：`python scripts/build-ui-icons.py --check` 通过
- 实机走查（发布产物 `RemoteFlow-v0.23.0-win-x64.exe`）：启动正常、标题栏版本 v0.23.0、设置页可进入、
  正常退出（exit 0）、无 `remoteflow.db-wal` / `-shm` 残留
- 开发期实机验证（同一套图标改动）：浅色 + 深色主题下首页、连接工作台、凭据、设置、帮助、关于 6 个页面截图目视检查；
  导航栏选中实心、页面标签、凭据类型图标、设置分区图标、主题分段图标、工作台工具栏与详情面板操作图标
- 未执行：实际远程会话中的工具条 / 全屏药丸 / 连接质量浮层目视验证（由视图加载集成测试覆盖）；
  对话框（编辑器、测试连接、消息、主机密钥）的实机目视验证（由视图加载集成测试覆盖）；macOS 构建与验证
- Platform：Windows 11（win-x64）

### Git
- 源码提交：`707094a`（chore: 版本号提升至 0.23.0）；Tag `v0.23.0` 指向随后的 `release: RemoteFlow v0.23.0` 提交
- 功能提交：`3e9ede3`（feat(ui): 图标体系）、`51a7614`（build: 生成脚本输出 LF）、`afdda87`（docs: v0.23.0 图标体系说明）
- 2026-09-27 发布：推送 main 与 Tag，创建 GitHub Release（Latest）