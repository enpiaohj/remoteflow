# RemoteFlow macOS v0.8.2

发布日期：2026-09-11 · 上一版本 macos-v0.8.1

macOS 端菜单与提示文案规范化（界面打磨；共享层未改动）。

## 变更

- **「显示」菜单标题随状态切换**（对齐 macOS HIG）：

  | 菜单项 | 状态 | 标题 |
  | --- | --- | --- |
  | 边栏 | 侧栏折叠 / 展开 | `显示边栏` / `隐藏边栏` |
  | 连接列表 | 列表列隐藏 / 显示 | `显示连接列表` / `隐藏连接列表` |
  | 全屏 | 非全屏 / 全屏中 | `进入全屏` / `退出全屏` |

  原来三项都是写死的「显示 / 隐藏 X」「进入 / 退出全屏」，不符合 HIG，也看不出当前
  处于哪一档。实现上在菜单打开前（`NSMenuDelegate.MenuWillOpen`）刷新标题，因此菜单、
  工具栏、系统 `toggleSidebar:` 三条路径都能拿到当前状态。连接列表项在首页 / 凭据页
  仍保持禁用，与原行为一致。

- **会话状态用词统一**：RDP / VNC / SSH 会话画面的「会话失败」改为「连接失败」，
  与窗口标题、会话状态机用语一致。

- **细节修正**：
  - 「正在连接 {name} …」去掉省略号前的多余空格；
  - 连接列表时间范围「7 天」改为「近 7 天」；
  - 连接右键菜单「复制」改为「复制连接」，避免与界面其它「复制」歧义。

## 范围说明

- 仅改 `src/RemoteFlow.App.Mac`（AppKit 视图与菜单）。
- **共享层（`Core` / `Presentation` / `Infrastructure`）未改动** —— Windows 端文案与
  行为零变化。
- 设置页正文、云同步文案本轮未动（属说明性文字，词义已清晰，改动收益低）。

## 验证

- 本地回归（本机 Intel，2026-09-11）：
  - `dotnet build -c Release`（解决方案）—— 0 错误（1 个既有警告）；
  - `Core.Tests` 42 通过；
  - `IntegrationTests` 179 通过 / 5 跳过；
  - `IntegrationTests.Mac` 10 通过；
  - `dotnet build src/RemoteFlow.App.Mac -c Release` —— 0 错误；
  - `IntegrationTests.Windows` 需 Windows 运行时，**本机未执行**（由 Windows 侧覆盖）。
- 启动冒烟：Debug app 启动后存活 9 秒，菜单构建无异常。
- **未实机走查**：菜单打开时标题的动态切换需在真实界面点开「显示」菜单确认；
  本轮未做自动点击验证（无辅助功能权限），请人工点开确认一次。
- 平台：macOS 13+ · 架构：arm64 + x64（分别打包）

## 已知问题

- **未签名 / 未公证**：本机无 Developer ID 证书，产物为 ad-hoc / Apple Development 签名，
  其它机器首次打开会被 Gatekeeper 拦（需右键「打开」或到「系统设置 → 隐私与安全性」放行）。
- 菜单动态标题未经实机点击验证（见「验证」）。

## Git

- Tag：`macos-v0.8.2` · 提交范围 `macos-v0.8.1..`

## 产物

- `RemoteFlow-v0.8.2-macos-arm64.dmg` —— 55.8 MB · SHA-256：`685ed37540721b2f3dca63b800d36b192d6d200db6d5c4de73753b4d643d80df`
- `RemoteFlow-v0.8.2-macos-x64.dmg` —— 57.0 MB · SHA-256：`4c11fee9af624a10b35f4edfbd8e341ac5bb0d9f157e00b30c2a0d22e10418bf`
