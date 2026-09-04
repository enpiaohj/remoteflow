# 更新日志

本文件记录 RemoteFlow 的版本变更。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

每个正式版本的完整变更详情见 `releases/v<X.Y.Z>/CHANGELOG.md`。

---

## [0.1.0] — 2026-09-04

首个正式版本（Unified Connection MVP）。先把「连接」这件事做扎实。

> 开发期间曾迭代打过内部标签（v0.1.0 早版、v0.1.1），均未推送、未对外分发，
> 已合并为单一的正式首版 v0.1.0。

### 新增
- 连接资产管理：CRUD、多级分组、跨分组标签、收藏、连接历史。
- 全局搜索：按名称 / Host/IP / 分组 / 标签 / 备注即时过滤，相关度排序。
- 连接列表行 / 凭据列表行 / 会话 Tab 均有右键菜单。
- 多协议会话：RDP（系统 ActiveX）、SSH（SSH.NET + xterm.js/WebView2）、
  VNC（纯托管 RFB）；统一 Tab 外壳、多会话并行、切 Tab 不断开、断线内部重连。
- 会话工具条：非全屏为顶部常驻长条，全屏为可拖动 / 可固定 / 自动隐藏的悬浮药丸
  （独立 Popup，盖在 RDP 原生画面之上，贴齐屏幕上边缘，首次进全屏给一次性提示）。
- 会话全屏：按显示器完整边界铺满、无溢出；`F11` / `Esc` 经低级键盘钩子响应。
- Credential Vault：密码 / 私钥经 Windows DPAPI 加密单独存储，连接库只存引用键；
  凭据编辑框与备份口令框均可「眼睛」显隐，明文不进 ViewModel。
- 连接 CSV 导入 / 导出（不含 Secret）。
- 凭据加密备份 `.rfbackup`：口令加密的凭据导入 / 导出，AES-256-GCM + PBKDF2 600k 迭代；
  明文密钥只在内存、绝不落盘；导入按名称跳过已存在。
- 启动 DB 自愈：残留的 `-wal` / `-shm` 与主库不一致且无人占用时自动清理重开。
- 页面数据加载失败改为顶部可重试横幅，不再静默。
- 深浅双主题、系统托盘、响应式窗口、单实例、i18n 基础设施。

### 已知问题
- RDP 证书对话框是系统 `mstsc` 的，「不再询问」在 Windows 层持久化。
- 单文件 exe 的 SSH 终端需同目录 `Assets/Terminal/`，用 `.zip` 包。
- SSH / VNC 未做真实设备深度联调，人工测试清单已随快照归档。

详见 [`releases/v0.1.0/CHANGELOG.md`](releases/v0.1.0/CHANGELOG.md)。

[0.1.0]: https://example.invalid/remoteflow/releases/tag/v0.1.0
