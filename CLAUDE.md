# CLAUDE.md — RemoteFlow（开源仓）

面向 Claude Code 的项目级规则。优先级：用户当前指令 > 本文件 > 用户级全局规则 > 默认行为。

## 产品与仓库

| 项 | 值 |
| --- | --- |
| 产品名 | RemoteFlow |
| 产品定位 | 统一远程连接工作台（Unified Remote Connection Workspace） |
| 本仓库 | `enpiaohj/remoteflow`（Public，GPL-3.0）＝**唯一开发与发布位置** |
| 本地目录 | Windows：`D:\AIProjects\remoteflow` |
| 默认分支 | `main` |
| 许可证 | GPL-3.0（见 `LICENSE`）；第三方组件清单 `THIRD-PARTY-NOTICES.md`，新增依赖须同步更新 |

存在一个内部私有仓（开发历史归档，同名加 `-private` 后缀）。**不要在私有仓做任何修改**；
若需从它带改动过来，用白名单导出（见发布流程第 1 步）而非手工拷贝。

## 技术基线（不可随意更改）

- 双平台：**Windows = WPF（`RemoteFlow.App`）**，**macOS = 原生 AppKit（`RemoteFlow.App.Mac`）**；
  C#；Runtime **.NET 10 LTS**（`global.json` 锁定）。Windows 侧不引入 WPF 以外的 UI 框架。
- 两端共享 `RemoteFlow.Core` + `RemoteFlow.Presentation`（平台无关，不引用 UI 框架 API）。
- 协议不自研：RDP 用系统 `mstscax.dll`（Windows）/ 内嵌 FreeRDP（macOS，`native/rdp/`）；
  SSH 用 SSH.NET + xterm.js（WebView2 / WKWebView）；VNC 用 Community.MarcusW.VncClient。
- 本地 SQLite；凭据 DPAPI / Keychain；日志 Serilog；集中式包版本（`Directory.Packages.props`）。

## 架构约束

- 依赖方向：`UI(App / App.Mac) → Presentation → Application → Core ← Infrastructure.* / Protocol.*`。
- **UI 层极薄**：只做视图构造 + 平台互操作；逻辑一律下沉 ViewModel。
- 改 `Core` / `Presentation` = 同时影响两端，按 API 变更对待：优先加法。
- UI 主题约束：**任何 Window/UserControl 必须设置全局前景**（`TextElement.Foreground="{DynamicResource Text.Primary}"`），
  自绘 `ControlTemplate` 的控件（RadioButton / Button 等）必须在**控件样式上显式 Foreground**——
  控件自身 Foreground 默认是系统黑，会屏蔽窗口级继承（深色主题下黑底黑字，v0.17.1 教训）。
- View 不操作协议实现；ViewModel 不保存 Secret；数据库访问统一走 Repository。

## 发布流程（固化，每次发布按此执行，不要临场发挥）

发布前置条件：用户明确说「发布 / Release / 打版本」。「测试 / 编译 / 看看效果」不是发布。

1. **同步**（仅当私有仓有增量改动时）：
   ```
   git -C <私有仓> archive HEAD src tests native scripts RemoteFlow.slnx \
     Directory.Build.props Directory.Packages.props global.json .gitignore \
     .gitattributes .editorconfig SECURITY.md CHANGELOG.md | tar -x -C .
   ```
   白名单不含 `docs/ releases/ .github/ CLAUDE.md`（内部内容不入本仓）。
2. **敏感校验**（任何批量导入后必做，必须零命中才继续）：
   ```
   grep -rIn "192\.168\.\|cncyswfs\|cncyswdc\|cngdi\|cndsj\|上线发布回滚\|E2EE-Vault" --include="*.cs" --include="*.md" .
   ```
   `appscloud.cn` 是已批准公开的体验服务地址，允许存在。Secret 形态扫描（私钥块 / token 字面量）tests 之外零命中。
3. **版本号**：`Directory.Build.props` 的 `VersionPrefix` / `AssemblyVersion` / `FileVersion` 三处同步提升。
   Semantic Versioning：新功能 MINOR、修复 PATCH。
4. **根 `CHANGELOG.md`**：顶部加条目（基于真实 diff，不编造），含 `### Artifacts`（Size + SHA-256，构建后回填）。
5. **构建与测试**：`dotnet build -c Release`（0 错误）→ 逐项目 `dotnet test`
   （Core.Tests / IntegrationTests / IntegrationTests.Windows；Mac 测试在 Windows 上 skip 属预期）。
6. **产物 + 实机走查**（不得跳过、不得编造结果）：
   ```
   dotnet publish src/RemoteFlow.App -c Release -r win-x64 --nologo -o <tmp>
   ```
   启动产物确认：能起、主界面正常、状态栏版本 = 新版本号、能进设置、干净退出（exit 0）、
   无 `remoteflow.db-wal/-shm` 残留。
7. **本地留存** `releases/vX.Y.Z/`：
   - `source/`——`git archive HEAD <白名单> | tar -x`（**入库**；commit 前导出，tag 即此源码）。
     若快照含历史遗留的未脱敏内容，先在快照内修正并注明。
   - `CHANGELOG.md` 副本（**入库**）。
   - 产物 `RemoteFlow-vX.Y.Z-win-x64.exe` / `.zip`（`.gitignore` 排除，本地留存）。
8. **提交与推送**：
   - `chore: 版本号提升至 X.Y.Z`（props + 根 CHANGELOG）
   - `release: RemoteFlow vX.Y.Z`（releases/vX.Y.Z/ 的 source + CHANGELOG 副本）
   - `git tag -a vX.Y.Z` → `git push origin main` + `git push origin vX.Y.Z`
9. **GitHub Release**：
   ```
   gh release create vX.Y.Z --title "RemoteFlow vX.Y.Z" --notes-file releases/vX.Y.Z/CHANGELOG.md --latest <exe> <zip>
   ```
   创建后核对 `gh release view vX.Y.Z --json assets` 的 `digest` 与本地 SHA-256 **逐位一致**。
10. macOS 产物（DMG）需 Mac 构建环境，本机无法执行；未发布时在 CHANGELOG 如实标注「未含 macOS 产物」。

### 发布相关注意事项（踩过的坑）

- 单文件自包含发布**不可字节复现**：同源码两次构建哈希不同。CHANGELOG 记录的哈希必须是
  实际上传的那次构建的。
- **替换正在运行的 exe 会失败**（覆盖写被拒）；Windows 允许 rename 运行中的 exe——文件替换
  「成功」不代表用户重启前能吃到新版本，验证前必须重启应用。
- 单文件 exe 的**进程名 = 文件名**（如 `RemoteFlow-v0.17.1-win-x64`），`Get-Process -Name RemoteFlow`
  查不到，用 `-Name 'RemoteFlow*'` 或按路径过滤。
- tag 源码 = 产物源码：publish 后若源码再有改动，必须重新构建产物，不得沿用。

## Commit 规范

Conventional Commits：`feat:` `fix:` `docs:` `refactor:` `perf:` `test:` `build:` `ci:` `chore:` `release:`。
Release Commit 格式：`release: RemoteFlow vX.Y.Z`。提交前先看 `git status` / `git diff`，不混入无关文件。

## 安全红线

- Secret 不明文入库/日志/导出/异常；本仓为公开仓库，**内网 IP、主机名、内部运维文档永远不入库**。
- SSH Host Key：首次记录（TOFU）、变化强警告，绝不静默接受。

## 构建 / 测试命令

```
dotnet build RemoteFlow.slnx -c Release          # 全解决方案（slnx 不含 App.Mac）
dotnet test tests/RemoteFlow.Core.Tests -c Release
dotnet run --project src/RemoteFlow.App
dotnet publish src/RemoteFlow.App -c Release -r win-x64   # 发布 win-x64 单文件
```
