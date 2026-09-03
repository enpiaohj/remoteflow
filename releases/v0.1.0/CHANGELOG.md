# RemoteFlow v0.1.0

发布日期：2026-09-03

RemoteFlow 首个版本。目标是先把「连接」这件事做好：在一个 WPF 客户端中
统一管理并使用 RDP、SSH、VNC/macOS Screen Sharing 连接，替代传统的
「RDP 文件 + PuTTY/终端 + VNC 客户端 + Excel」组合。

---

## Added

### 连接资产管理
- 新建 / 编辑 / 复制 / 删除连接，字段含名称、Host/IP、端口、协议、分组、标签、备注。
- 默认端口自动填充（RDP 3389 / SSH 22 / VNC 5900），支持手工指定非标准端口。
- 收藏 / 取消收藏；记录最近连接时间与连接历史。
- 多级分组（Group）+ 跨分组标签（Tag）；删除分组时组内连接迁移而非删除。

### 全局搜索
- 按名称、Host/IP、分组、标签、备注即时过滤，`Ctrl+K` 聚焦。
- 相关度排序：名称前缀 > 名称包含 > 主机 > 标签 > 分组 > 备注；同分下最近连接优先。

### 会话
- RDP：内嵌系统 Microsoft RDP Client ActiveX 控件，全屏 / 缩放适配 / 发送 Ctrl+Alt+Del /
  剪贴板与音频重定向开关 / 动态分辨率跟随窗口 / NLA 开关。
- SSH：SSH.NET 协议层 + WebView2 承载 xterm.js 终端，支持 Password 与 Private Key 登录、
  UTF-8 与中文、ANSI 色彩、终端 Resize、复制粘贴、可配置 KeepAlive、登录后自动执行命令。
- VNC：纯托管 RFB 实现，Password 认证、适应窗口 / 1:1 缩放、键鼠输入、只读模式、共享连接。
- 多 Tab：三协议统一 Tab 外壳，Tab 只显示协议图标 / 名称 / 状态 / 关闭按钮。
- 切换 Tab 不断开会话；断线在会话内部显示轻量状态层并提供「重新连接」，不弹模态错误框。
- 并发会话上限保护，防止异常情况下重复创建连接。

### Credential Vault
- 凭据类型：Windows 域账号、本地账号、SSH 口令、SSH 私钥、VNC 口令。
- 密码 / 私钥 / Passphrase 经 Windows DPAPI（CurrentUser）加密，存于独立的 `vault.dat`；
  连接数据库只保存引用键，被单独复制走无法得到明文。
- 界面在任何情况下不显示已保存的 Secret；编辑凭据时密码框留空表示保持原值不变。
- 删除凭据同步清除保险库密文；引用该凭据的连接变为「未指定凭据」并提示影响数量。

### SSH 主机密钥校验
- 首次连接提示并记录 SHA256 指纹。
- 指纹变化时中止连接并强警告，默认焦点在「取消连接」，信任按钮使用警示色。
- 设置页可查看与移除已信任的主机密钥。

### 导入 / 导出
- CSV 导入 / 导出（`Name,Host,Port,Protocol,Group,Tags,CredentialName,Notes`）。
- 缺失的分组与标签自动创建；单行解析失败不中断整体导入，错误逐行汇总。
- 导出文件不含任何密码或私钥；导入只按名称关联已存在的凭据。

### 界面
- 左侧轻导航（7 个一级入口）+ 顶部全局搜索与「新建连接」+ 中央连接/会话共享工作区 +
  可折叠右侧详情面板；无传统菜单栏，不堆仪表盘。
- 深 / 浅双主题（Windows 11 Fluent 风格），默认浅色，可跟随系统。
- 响应式窗口：窄窗口自动收起详情面板；全屏会话隐藏导航与详情。
- 键盘快捷键：`Ctrl+K` 搜索、`Ctrl+N` 新建、`Ctrl+W` 关闭会话、`Ctrl+Tab` 切换、`Esc` 退出全屏。
- 系统托盘：可选「关闭窗口时最小化到通知区域」，保持后台会话，从托盘退出。
- 首页：最近连接、收藏、连接历史，帮助用户快速回到工作状态。

### 设置
- 常规（开机启动、关闭行为、主题）、RDP 默认值、SSH 终端字体 / 字号 / 类型 / KeepAlive、
  安全（清理历史、主机密钥管理）、数据（数据目录、并发会话上限）。
- 任一设置变化即时原子写盘。

### 安全与稳定性
- 结构化日志（Serilog，按天滚动）；两道脱敏防线，日志不含 Secret / 完整认证报文 / 剪贴板内容。
- 连接历史只落标准化错误码，不写底层异常原文。
- SQLite 启用 WAL，异常退出后可恢复；Schema 通过 `user_version` 版本化迁移，事务化执行。
- 设置文件与保险库文件均采用「临时文件 + 原子替换」写入。
- 单个会话失败不影响其他会话与主程序；界面线程未处理异常记录后不退出应用。

---

## 技术栈

| 层 | 选型 |
| --- | --- |
| 平台 / Runtime | Windows 11 / .NET 10 LTS |
| UI | WPF + MVVM（CommunityToolkit.Mvvm） |
| 数据库 | SQLite（Microsoft.Data.Sqlite 10.0.11） |
| 凭据保护 | Windows DPAPI（System.Security.Cryptography.ProtectedData 10.0.11） |
| 日志 | Serilog 4.4.0 |
| RDP | 系统自带 mstscax.dll RDP Client ActiveX（不自研协议栈） |
| SSH | SSH.NET 2026.0.0 + xterm.js 5.5.0 / WebView2 |
| VNC | Community.MarcusW.VncClient 2.0.7（MIT） |

---

## Verification

- Build：`dotnet build -c Release` — 0 警告 0 错误（全 9 个项目）
- Tests：`dotnet test` — 47 项全部通过（32 单元 + 15 集成）
  - 单元：全局搜索匹配与排序、CSV 解析边界（引号 / 字段内逗号与换行 / BOM）、
    领域约定、错误码中文映射、`ResolvedCredential` 不泄露 Secret
  - 集成：SQLite 建库 / 幂等迁移 / CRUD 往返 / 关联表级联（删组迁移连接、删凭据 SET NULL）、
    DPAPI 保险库（**保险库文件字节级搜不到明文 Secret**、删除清理、并发写不覆盖、私钥+Passphrase 分离）
- Publish：`dotnet publish src/RemoteFlow.App -c Release` — win-x64 Self-contained 单文件，
  发布产物只含 `RemoteFlow.exe` 与终端资源（xterm.js），无调试符号
- Runtime：Debug、Release、发布版单文件 exe 均可独立启动，日志无 Error / Fatal
- RDP：Sprint 0 技术 POC 已实机验证控件探测 → 句柄就绪 → 属性配置 →
  连接点事件挂接 → `OnConnecting` / `OnDisconnected` 回调 → 资源释放全链路
- Platform：Windows 11 Pro 26200
- Architecture：win-x64

### 发布产物

| 文件 | 大小 | SHA256 |
| --- | --- | --- |
| `RemoteFlow-v0.1.0-win-x64.exe` | 70.97 MB | `6996b6f1d3e94099d98bbcefdcf2a3067355cbdd59ca9aa4b989b2f1b8a9b452` |
| `RemoteFlow-v0.1.0-win-x64.zip` | 65.53 MB | `9517804a9392c10fb8934ec76195e26c0d27cdeb2d7403308fd8d409ad4bc555` |

`.exe` 为 Self-contained 单文件，双击即可运行（首次运行需本机已安装
Microsoft Edge WebView2 运行时，Windows 11 通常已内置）。
`.zip` 为完整发布目录（exe + 终端资源），内容与单文件 exe 等价。

---

## Known Issues

- RDP 多显示器（UseMultimon）依赖控件版本，部分环境可能不生效，属增强项。
- SSH 终端首次加载依赖 WebView2 运行时；缺失时会话内给出明确提示。
- VNC 协议自身加密能力有限，建议仅在内网 / VPN / SSH 隧道中使用。
- 保险库文件（`vault.dat`）只能在同一 Windows 用户账户下解密，跨账户 / 跨机器迁移需重新录入凭据。

---

## Git

- 源码 Commit：`66c4fc5`（`feat: RemoteFlow V0.1 统一远程连接工作台首版实现`）——
  本次发布的 `source/` 快照与发布产物均基于此提交构建。
- Tag：`v0.1.0`，指向发布提交 `release: RemoteFlow v0.1.0`。
