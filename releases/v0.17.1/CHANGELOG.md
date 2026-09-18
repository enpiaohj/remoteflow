# RemoteFlow v0.17.1

发布日期：2026-09-18 · 上一版本 v0.17.0


深色模式可读性修复。本版本仅发布 Windows（win-x64）。

### 修复
- **深色模式下对话框内裸文本显示为黑色**：自绘对话框的 Window 没有设置全局前景，
  无显式 Foreground 的 TextBlock（如新建连接左侧协议区的 SSH / VNC 项）继承 WPF 默认
  `SystemColors.ControlText`——浅色主题下恰好可用，深色主题下深底黑字几乎不可读。
  修复分两层：全部对话框 Window 与 MainWindow 统一设置
  `TextElement.Foreground={DynamicResource Text.Primary}` 兜底裸文本；
  `ProtocolOption`（协议选择项）样式显式 `Foreground`——RadioButton 控件自身的
  Foreground 默认值（系统黑）优先于窗口附加属性的继承，必须显式设置才生效。

### Artifacts
- `RemoteFlow-v0.17.1-win-x64.exe`
  - Size：74,889,688 bytes
  - SHA-256：`E558D5D439765171D0483DF34DC292DC45A1D0D5E504F47ABE5BAD8BD8883C8C`
- `RemoteFlow-v0.17.1-win-x64.zip`
  - Size：69,416,551 bytes
  - SHA-256：`40E2D27757B400545BE2571F351F407935EBDB343AB71F646440361A3D6957EF`
- 均附于 GitHub Release `v0.17.1`。

