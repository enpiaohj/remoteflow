# RemoteFlow v0.3.2

发布日期：2026-09-05

本版本修复了「SSH 连接打开即失败/一闪即断」的真正根因：**正式发布的单文件
exe 旁边没有 `Assets/Terminal/` 资源目录，SSH 终端在发起连接前就初始化失败**。
此前 v0.3.1 针对信任弹窗输入逻辑的修复（残留鼠标输入宽限期）只是加固，并非
本次问题的原因——真正的故障发生在弹窗出现之前。上一个正式版本为 v0.3.1。

## Fixed

- **单文件发布包缺失终端资源导致 SSH 无法连接**：`SshSessionView` 原先硬编码
  `AppContext.BaseDirectory\Assets\Terminal` 作为 WebView2 虚拟主机目录。在
  .NET 5+ 单文件发布下，`AppContext.BaseDirectory` 始终是 exe 所在目录（不会
  变成内容解压目录），因此单独下载 `RemoteFlow.exe`（不带旁置的
  `Assets/Terminal/`）时，`SetVirtualHostNameToFolderMapping` 会抛
  `DirectoryNotFoundException`，会话在真正连接前就被终端初始化失败中断——用户
  看到的现象是连接立即失败，日志中完全没有主机密钥校验流程。现已把五个终端
  前端文件（`terminal.html` / `xterm.js` / `xterm.css` / `addon-fit.js` /
  `addon-webgl.js`）作为内嵌资源随 exe 携带，运行时由新增的
  `TerminalAssetStore` 释放到 `%LOCALAPPDATA%\RemoteFlow\TerminalAssets\<版本>`，
  再交给 WebView2 映射。单文件 exe 与 zip 包现在对 SSH 终端等效，不再依赖
  exe 旁恰好存在资源目录。**已在只有单个 exe、无任何旁置资源的目录下实机
  连接 SSH 成功验证。**

## Changed

- 长期记入「已知问题」的「单文件 exe 的 SSH 终端需同目录 `Assets/Terminal/`，
  用 .zip 包」已消除——SSH 终端资源现已内嵌，.exe 单独运行即可。

## Verification

- Build：`dotnet build -c Release` — 0 警告 / 0 错误
- Tests：`dotnet test` — 82 项全部通过（32 Core + 50 Integration，新增
  `TerminalAssetStoreTests`）
- Manual：仅含单 exe 的隔离目录启动，打开 SSH 连接成功、终端正常渲染
- Platform：Windows 11
- Architecture：x64（win-x64，self-contained，单文件）

## Git

- Commit：见下方 Release Commit
- Tag：`v0.3.2`
- 上一版本：`v0.3.1`（`0f51472`）
