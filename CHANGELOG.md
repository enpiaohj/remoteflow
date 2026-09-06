# 更新日志

本文件记录 RemoteFlow 的版本变更。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

每个正式版本的完整变更详情见 `releases/v<X.Y.Z>/CHANGELOG.md`。

---

## [0.7.0] — 2026-09-06

### 新增 / 变更
- 协议功能增强：RDP 高级设置收口（显示/本地资源/连接与安全/体验 Section、分辨率预设、多显示器↔启动全屏联动、麦克风与连接质量真实映射）；SSH 终端主题与实时预览、粘贴安全、清屏、搜索输出；VNC 剪贴板远端→本机接收。
- 托盘右键结构化菜单：打开/隐藏、新建连接（预选协议）、最近连接、活动会话 + 断开全部、设置、开机启动（双向同步）、正式退出。
- 会话 Tab「关闭右侧会话」。

详见 [`releases/v0.7.0/CHANGELOG.md`](releases/v0.7.0/CHANGELOG.md)。

## [0.6.0] — 2026-09-06

### 新增 / 变更
- Session 生命周期统一与资源清理（RemoteSessionBase/Tracker、统一关闭编排、RDP/SSH/VNC 清理加固、WebView2 共享环境、有序退出与崩溃标记）。
- 会话实时状态同步（SessionManager 唯一状态源、聚合通知、首页/列表/详情/托盘实时、右键 连接/切换/断开）。
- 首页时间显示与顺序预设、已连接口径、托盘与会话切换、右键菜单分组、复制自动命名、统一「测试连接」对话框。

详见 [`releases/v0.6.0/CHANGELOG.md`](releases/v0.6.0/CHANGELOG.md)。

## [0.5.0] — 2026-09-05

### 新增 / 变更
- 默认分组保护与规则（分组 is_default/is_protected、schema v3、删除默认选新默认、设为默认分组、删光不复活）。
- VNC 输入发送链路修复与会话三档缩放。

详见 [`releases/v0.5.0/CHANGELOG.md`](releases/v0.5.0/CHANGELOG.md)。

## [0.4.0] — 2026-09-05

### 新增 / 变更
- 统一连接链路与首页收藏连接；我的连接多选批量；凭据批量与筛选；右侧详情分区完善；首页密度与统计；图标全应用统一；导航键盘可达性。

详见 [`releases/v0.4.0/CHANGELOG.md`](releases/v0.4.0/CHANGELOG.md)。

## [0.3.2] — 2026-09-05

### 修复
- 单文件发布包缺失终端资源导致 SSH 无法连接——SSH 终端资源（xterm.js 前端）
  现随 exe 内嵌并在运行时释放，不再依赖 exe 旁存在 `Assets/Terminal/`。
  已实机验证。

详见 [`releases/v0.3.2/CHANGELOG.md`](releases/v0.3.2/CHANGELOG.md)。

## [0.3.1] — 2026-09-05

### 修复
- SSH 主机密钥信任弹窗仍会被残留鼠标输入瞬间关闭——无边框窗口的拖动处理器
  未被输入宽限期覆盖，现已一并纳入，并为 `DragMove()` 增加防御性异常处理。
  已实机验证。

详见 [`releases/v0.3.1/CHANGELOG.md`](releases/v0.3.1/CHANGELOG.md)。

## [0.1.1] — 2026-09-04

### 修复
- SSH 主机密钥确认对话框被握手超时吞掉——弹窗改到握手中止后的异步流程里，
  用户接受则记录指纹并自动重试；指纹变化仍是强警告。
- 连接 CSV 导入不去重——以名称 + 主机 + 端口 + 协议为身份，重复导入不再产生副本。

详见 [`releases/v0.1.1/CHANGELOG.md`](releases/v0.1.1/CHANGELOG.md)。

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

[0.3.1]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.3.1
[0.3.2]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.3.2
[0.3.1]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.3.1
[0.1.1]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.1.1
[0.1.0]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.1.0
