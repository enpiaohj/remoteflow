# RemoteFlow 双平台发布规范 v1.0

发布日期：2026-09-09
适用范围：`enpiaohj/remoteflow`（Private，默认分支 `main`），Windows(WPF) + macOS(AppKit) 双平台

> 本文是发布相关的**唯一权威流程**。与仓库 `CLAUDE.md`、用户级 `~/.claude/rules/release.md` 冲突时，
> 以更具体、风险更低的一方为准，并在 PR / 提交说明里指出冲突。

---

## 0. 一句话

`main` 是双平台统一树。**Windows 打 `vX.Y.Z` tag、macOS 打 `macos-vX.Y.Z` tag**，
各自的 GitHub Actions 自动构建产物并创建 **Release 草稿**；人工审核后手动发布。
共享层（`RemoteFlow.Core` / `RemoteFlow.Presentation`）的改动**同时影响两端**，
要么是加法，要么在同一个改动里把两端视图一起改到、两端 CI 都绿。

---

## 1. 仓库与版本线

| 项 | Windows | macOS |
| --- | --- | --- |
| 版本号来源 | `Directory.Build.props` 的 `<VersionPrefix>` | `src/RemoteFlow.App.Mac/RemoteFlow.App.Mac.csproj` 的 `<ApplicationDisplayVersion>`（`<ApplicationVersion>` 单调 +1） |
| Tag | `vX.Y.Z`（如 `v0.9.0`） | `macos-vX.Y.Z`（如 `macos-v0.3.0`） |
| Release workflow | `.github/workflows/release-windows.yml`（触发 `tags: v[0-9]*`） | `.github/workflows/release-macos.yml`（触发 `tags: macos-v*`） |
| 本地快照 | `releases/vX.Y.Z/`（`source/` + `CHANGELOG.md`） | `releases/macos-vX.Y.Z/`（同上） |
| 产物 | `RemoteFlow-vX.Y.Z-win-x64.exe` / `.zip` | `RemoteFlow-vX.Y.Z-macos-arm64.dmg` / `-x64.dmg` |
| 根 `CHANGELOG.md` | Windows 版本条目 | 走 `releases/macos-*/CHANGELOG.md`；根 `CHANGELOG.md` 是否收 macOS 条目按当时约定 |

- 两条版本线**独立递增**，互不牵制。Windows 修一个 bug 发 `v0.9.1` 不需要 macOS 也动。
- `Directory.Build.props` 的 `AssemblyVersion` / `FileVersion` 是全解决方案共享的——macOS 程序集
  也会带上 Windows 版本号做内部元数据，这是**已知的无害副作用**（macOS bundle 版本以 csproj 为准）。
- 语义化版本：`MAJOR.MINOR.PATCH`。新功能 / 能力增强 = MINOR；纯 Bug Fix / 稳定性 = PATCH；
  Breaking Change = MAJOR。已发布的 tag / 产物不覆盖、不倒退。

---

## 2. 分支纪律（这条最重要）

1. **每次开工前、每次发布前必做**：
   ```
   git fetch origin
   git log --oneline HEAD..origin/main     # 落后多少
   git log --oneline origin/main..HEAD     # 领先多少
   ```
2. 特性分支**从最新 `main` 拉**，命名 `feature/<name>` / `fix/<name>` / `refactor/<name>` / `docs/<name>` / `build/<name>` / `ci/<name>` / `hotfix/<name>`。
3. 干活期间**每天 rebase `main`**。分支别过夜太久——`main` 移动快时，一个长会话就可能撞上共享层重构。
4. 一端在做大重构（如 Presentation 抽取、工程拆分）时，另一端**暂缓结构性改动或高频 rebase**，
   并在群里 / commit 里说清「本周谁动共享层」。
5. 合并 `main` 用 **fast-forward**（`git merge --ff-only`）或 squash；不引入无意义 merge commit。
6. 未经明确授权不得：`push --force` / `push --force-with-lease` / `reset --hard`（对已推送分支）/
   `rebase` 已推送历史 / `branch -D` / `tag -d`（已发布 tag）/ 删除远程分支或 Release。

---

## 3. 共享层改动规则

分层：`Core`（模型，无 UI）← `Presentation`（ViewModel + 视图无关服务，两端绑同一套）
← `App`(WPF) / `App.Mac`(AppKit)（**只有视图构造 + 平台互操作**）。

- **判据**：同一个 `if` 写在 `SessionHostView.xaml.cs` 和 `MainWindowController.cs` 两处 → 它该在 `MainViewModel`。
- **改 `Presentation` / `Core` = 破坏两端**，按 API 变更对待：
  - **优先加法**：加属性 / 加方法，旧的标 `[Obsolete]`，下个版本再删。
  - **必须删除 / 改签名时**：一个分支里同时改 `Presentation` + `App`(WPF) + `App.Mac`(AppKit) 的相关视图，
    **两端 CI 都绿**才合。
  - 做不到同步（比如另一端本人不在）→ 要么拆成加法先行，要么排一个跨平台窗口，**不要单端硬删**。
- **纯视图 / 样式各做各的**：WPF XAML、AppKit 布局、间距、颜色、hover、状态点尺寸——按各平台 HIG，不共享。
- **发布前主动查共享层回归**：`grep` 一遍 `Protocol.*` / `Presentation` 里**新增的事件 / 接口 / 无条件挂的 handler**，
  确认两端视图都消费了。（v0.9.0 踩过：共享 `VncSession` 挂了 `CursorHandler`，macOS 接了、WPF 没接 → Windows VNC 光标没了。）
- `Directory.Build.props` 有 `EnableWindowsTargeting=true`：**在任一平台机器上都能 `dotnet build`（slnx，不含 App.Mac）**
  做 Windows 侧编译校验。提交共享层改动前跑一遍全解决方案 build + test。

---

## 4. CI 门禁（安全网）

`.github/workflows/ci.yml`：每次 push `main` / `feature/**` + 每个 PR：
- Ubuntu 跑共享测试（`Core.Tests` + `IntegrationTests`）——主门禁；
- Windows runner 验「能否构建 slnx + `IntegrationTests.Windows`」；
- macOS runner 验「能否构建 `App.Mac` + `IntegrationTests.Mac`」。

→ **`main` 因此永远不会「一端是坏的」**。fetch + rebase 拿到的基线可信。
→ **合共享层改动前，先看目标分支的 CI 两端都绿。**

---

## 5. 发布流程（以 Windows `vX.Y.Z` 为例；macOS 同构，换 tag 前缀 + 版本号来源）

### 5.1 准备

```
git fetch origin && git checkout main && git merge --ff-only <feature-branch>
git log --oneline vX.Y.(Z-1)..HEAD          # 本版包含哪些提交，是否有编造/无关项
```

- 清 `%LOCALAPPDATA%\RemoteFlow\remoteflow.db-wal` / `-shm`（否则 publish 阶段可能文件占用）。
- kill 本机残留的 `RemoteFlow.exe`。
- 判定版本号（见 §1）。

### 5.2 本地回归（不得跳过、不得编造结果）

```
dotnet build -c Release --nologo                    # 0 错误
dotnet test  -c Release --nologo                    # Core / Integration / *.Windows 全绿；*.Mac skip 属正常
dotnet publish src/RemoteFlow.App -c Release -r win-x64 --nologo -o <tmp>/publish
<启动 publish 出的单文件 exe，确认能起能退>
```

未执行的验证（如实机 RDP / SSH / VNC 会话）**在 CHANGELOG 里如实写「未执行」**，不得写成「通过」。

### 5.3 版本号 + 根 CHANGELOG

- `Directory.Build.props`：`VersionPrefix` / `AssemblyVersion` / `FileVersion` → `X.Y.Z`。
- 根 `CHANGELOG.md`：在顶部 `---` 下加 `## [X.Y.Z] — YYYY-MM-DD` 摘要（架构 / 新增 / 变更 / 修复 / 未含），
  末尾补 `[X.Y.Z]: https://github.com/enpiaohj/remoteflow/releases/tag/vX.Y.Z`。
- Commit：`chore: 版本号提升至 X.Y.Z`（含 `Directory.Build.props` + 根 `CHANGELOG.md`）。

### 5.4 版本快照

```
mkdir -p releases/vX.Y.Z/source
git archive --format=tar HEAD -- ':(exclude)releases' | tar -x -C releases/vX.Y.Z/source
# 校验：source 里无 bin/obj；Directory.Build.props / CHANGELOG.md 版本对；不含 Secret
```

- 写 `releases/vX.Y.Z/CHANGELOG.md`（详版，见 §6）。
- `git add releases/vX.Y.Z/source releases/vX.Y.Z/CHANGELOG.md`
- **`.gitignore` 排除 `releases/**/*.zip|*.exe|*.dmg` 等**——`source/` 里被 HEAD 跟踪的设计 zip 会被漏掉，
  需 `git add -f releases/vX.Y.Z/source/<那个 zip 路径>` 补回，保持 source 与 HEAD 一致。
- **`git status` 确认只暂存了 `releases/vX.Y.Z/` 下的文件**——不得把用户并行积累的未跟踪内容混进来。
- Commit：`release: RemoteFlow vX.Y.Z`。

### 5.5 Tag + Push

```
git tag -a vX.Y.Z -m "RemoteFlow vX.Y.Z"
git status && git branch --show-current && gh auth status
git push origin main
git push origin vX.Y.Z
```

- Tag 必须指向已完成 build/test 的 `release:` commit。
- **不得移动 / 删除已推送并已发布的 tag。** 草稿阶段的 tag（Release 还没手动发布）若确需重来，
  见 §7。

### 5.6 CI 出草稿 → 人工发布

- tag 推上去后 `release-windows.yml` 自动跑：build + test + publish + （有证书则 signtool 签）+ 打包 +
  `upload-artifact` + **创建 GitHub Release 草稿**（`draft: true`，作者 `github-actions[bot]`）。
- `gh run list --workflow=release-windows.yml --limit 1` 看结果；`gh release view vX.Y.Z` 看草稿与产物。
- **人工审核草稿**：产物名 / 大小对；Release body（可把 `releases/vX.Y.Z/CHANGELOG.md` 正文贴进去）；
  确认没问题再在网页上「Publish release」。
- 发布时若要设为 Latest，取消勾其它版本的 Latest。

### 5.7 发布报告（中文，至少含）

```
产品 / 版本 / Build / 平台 / 架构
构建结果 / 测试结果
Release Snapshot / Source Snapshot / CHANGELOG / 发布产物（+ SHA256）
Git Commit / Git Tag
已知问题 / 未执行项目（如实标注）
```

---

## 6. CHANGELOG 约定

- `releases/vX.Y.Z/CHANGELOG.md`（详版）章节：`架构 / Added / Changed / Fixed / Removed / Known Issues / Verification / Artifacts / Git`。
- **基于真实 diff / git log 写**，不写「优化部分功能」「修复若干问题」。
- 明确标出**哪些改动是共享层带入的**（如「SSH WebGL（共享 `terminal.html`）」），以及**哪些是本平台专属**。
- Known Issues 里如实列**未实机验证**的项、**跨平台待协调**的项（如某个 P4 式合并 macOS 还没跟上）。
- Verification 里写清 build / test 的真实数字，未跑的写「未执行」。

---

## 7. 草稿阶段重来 / 撤销（谨慎，需授权）

Release **还是草稿、从未 Publish、产物未对外分发** 时，可视为「未正式发布」：

- 删草稿 Release：`gh release delete vX.Y.Z`（只删草稿，不动 tag）。
- 若 tag 也要重指向新提交：`git tag -d vX.Y.Z && git push origin :refs/tags/vX.Y.Z`（删远端 tag）
  → 修正后重新 `git tag` + `git push`。**删远端 tag / force-push tag 需用户明确授权。**
- 若 `release:` / `chore:` commit 已在 `origin/main` 上，**优先用新提交往前修**（补一个 `fix` / 追加快照修正 commit + 新 tag），
  **不 force-push `main`**——除非用户明确要求且清楚影响（其它 clone / 另一平台）。
- 一旦 Publish（公开、可下载），就是 Immutable：发现问题发 `X.Y.(Z+1)`，不覆盖。

（v0.9.0 教训：误把 tag 推到一个没有 `.github/workflows/` 的孤儿提交 → 没触发 CI；
删 tag、`reset --hard origin/main`、在最新结构上重做，才恢复。）

---

## 8. 统一发布（路线图，尚未启用）

目标：一个 `vX.Y.Z` tag → 一个 GitHub Release，同时挂 `win-x64.exe/.zip` + `macos-arm64.dmg`/`-x64.dmg`，
一条版本线、一份 CHANGELOG。

- 做法：合并 `release-windows.yml` + `release-macos.yml` 为一个 `release.yml`（win job + mac job +
  「汇总成一个 Release」job），去掉 `macos-v` 前缀，`Directory.Build.props` + Mac csproj 版本对齐。
- **启用时机**：等 macOS 版到一个够稳的基线（多轮实机验证通过、迭代节奏放缓）。
  在那之前保持双线——锁步会让稳定的 Windows 被快速迭代的 macOS 卡住。
- 统一发布**不能替代**分支纪律 + CI 门禁——那两条才是防「双端发散」的根本。

---

## 9. 安全红线（发布同样适用）

- Secret（密码 / 私钥 / Passphrase / Token / 证书私钥）**不进** `source/` 快照、产物、日志、CHANGELOG、异常消息。
  需要配置模板给 `.env.example` / `config.example.*`。
- 不删未知用户文件、不覆盖已有 Backup / Release Snapshot。
- 数据库 schema 变更：`releases/*/CHANGELOG.md` 注明迁移路径；优先向后兼容 / 可回滚。
- 高风险 Git 操作（历史重写、大规模目录移动、schema 迁移）前先 `git bundle create` 备份到
  `D:\AIProjects\_Backup\YYYY-MM-DD-RemoteFlow-操作前备份-vX.Y.bundle` 并 `git bundle verify`。
