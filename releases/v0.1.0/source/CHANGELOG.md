# 更新日志

本文件记录 RemoteFlow 的版本变更。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

每个正式版本的完整变更详情见 `releases/v<X.Y.Z>/CHANGELOG.md`。

---

## [0.1.0] — 2026-09-03

Unified Connection MVP。首版目标：先把「连接」做好。

### 新增
- 连接资产管理：CRUD、多级分组、跨分组标签、收藏、连接历史。
- 全局搜索：按名称 / Host/IP / 分组 / 标签 / 备注即时过滤，相关度排序。
- 多协议会话：RDP（系统 ActiveX）、SSH（SSH.NET + xterm.js/WebView2）、
  VNC（纯托管 RFB）；统一 Tab 外壳、多会话并行、切 Tab 不断开、断线内部重连。
- Credential Vault：密码 / 私钥经 Windows DPAPI 加密单独存储，连接库只存引用键。
- SSH 主机密钥校验：首次记录、指纹变化强警告。
- CSV 导入 / 导出：导出不含任何 Secret。
- 深浅双主题、系统托盘、响应式窗口、键盘快捷键。

详见 [`releases/v0.1.0/CHANGELOG.md`](releases/v0.1.0/CHANGELOG.md)。

[0.1.0]: https://example.invalid/remoteflow/releases/tag/v0.1.0
