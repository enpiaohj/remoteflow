# RemoteFlow macOS v0.9.0

发布日期：2026-09-28 · 上一版本 macos-v0.8.3

首页整体视觉重做 + 仓库开源后的代码库迁移。改动集中在 `RemoteFlow.App.Mac`；
共享层（`Core` / `Presentation`）未做 macOS 专属改动，Windows 端不受影响。

## 新增

- **首页视觉重做**，对齐最新设计稿：浅灰白底 + 纯白卡片（圆角 12、轻投影），
  层次交给发丝线和淡投影而不是靠底色染灰。
- 侧栏顶部加**品牌标识**（强调色图标块 + 「RemoteFlow」）。
- 「最近连接」「收藏」行新增「…」更多操作按钮（弹出上下文菜单）。
- 区块底部的「管理收藏」「查看所有活动」从裸文字链接改为浅灰圆角动作条。
- 首页右下角显示 `RemoteFlow v{版本号}` 水印，一眼能看出当前跑的是哪个版本。

## 改进

- 「测试连接」对话框按 Windows 版结构重写：DNS / Ping / TCP 三步诊断行、结论、
  可能原因列表、默认折叠的详细信息、「取消 / 重新测试 / 关闭」按钮，配色改用
  macOS 原生系统色。
- 「我的连接」列表列宽从 240 加宽到 300，修复长连接名 / 备注被截断的问题。
- 去除首页问候区的波浪装饰，视觉更克制。

## 变更

- **移除设置页「分组」卡片里的「保护默认分组」开关**，以及连接列表右键菜单的
  「设为默认分组」——这两项依赖的旧接口在共享层已不存在。默认分组现在由
  `DefaultGroupResolver` 自动解析，保护状态跟随「是否内置分组」自动判定，
  不再需要用户手动设置（Windows 端此前已是这个设计，此次是 macOS 跟进对齐）。

## 内部

- **代码库随产品开源迁移到新的仓库历史**：远端仓库开源时把提交历史重写为
  全新起点（无法与本地历史三方合并），本次把 macOS 最新代码及全部历史发布
  快照（`macos-v0.2.0` – `v0.8.3`）移植到新历史上；本地旧历史已用 `git bundle`
  完整备份，未丢失任何提交。

## 验证

- `dotnet build -c Release`（解决方案，含 Windows 侧编译校验）—— 0 错误；
- `dotnet build src/RemoteFlow.App.Mac -c Release` —— 0 错误；
- `RemoteFlow.Core.Tests` 46 通过；
- `RemoteFlow.IntegrationTests.Mac` 10 通过；
- `RemoteFlow.IntegrationTests.Windows` 需 Windows 运行时，**本机未执行**（由 Windows 侧覆盖）。

## 已知问题

- **未签名 / 未公证**：本机无 Developer ID 证书，产物为 ad-hoc / Apple Development
  签名，其它机器首次打开会被 Gatekeeper 拦（需右键「打开」或到「系统设置 →
  隐私与安全性」放行）。

## Git

- Tag：`macos-v0.9.0` · 提交范围 `macos-v0.8.3..`

## 产物

- `RemoteFlow-v0.9.0-macos-arm64.dmg`
- `RemoteFlow-v0.9.0-macos-x64.dmg`
