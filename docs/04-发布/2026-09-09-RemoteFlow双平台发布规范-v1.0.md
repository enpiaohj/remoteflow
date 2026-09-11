# RemoteFlow 双平台发布规范 v1.0

发布日期：2026-09-09
适用范围：`enpiaohj/remoteflow`（Private，默认分支 `main`），Windows(WPF) + macOS(AppKit) 双平台

> 本文是发布相关的**唯一权威流程**。与仓库 `CLAUDE.md`、用户级 `~/.claude/rules/release.md` 冲突时，
> 以更具体、风险更低的一方为准，并在 PR / 提交说明里指出冲突。

---

## 0. 一句话

`main` 是双平台统一树。**Windows 打 `vX.Y.Z` tag、macOS 打 `macos-vX.Y.Z` tag**。

产物构建与发布**只有一条路径：本地构建**（GitHub Actions 已弃用，见 §4）：

- **本地发布**：各平台在自己的机器上构建产物并创建 GitHub Release，互不依赖、互不阻塞 ——
  Windows 用 `scripts/release-local.ps1`，macOS 用 `scripts/release-local-macos.sh`。
  本机架构那一份原生编译，另一份交叉编译（见 §5.6）。
  产物名：`RemoteFlow-vX.Y.Z-win-x64.exe` / `RemoteFlow-vX.Y.Z-macos-arm64.dmg` / `-x64.dmg`。
- **GitHub Actions 已弃用**：`release-*.yml` 的触发器已停用，不再产生产物。
  文件保留在仓库里，正文注释说明恢复方法（见 §4）。

共享层（`RemoteFlow.Core` / `RemoteFlow.Presentation`）的改动**同时影响两端**，
要么是加法，要么在同一个改动里把两端视图一起改到、两端都能编译。

---

## 1. 仓库与版本线

| 项 | Windows | macOS |
| --- | --- | --- |
| 版本号来源 | `Directory.Build.props` 的 `<VersionPrefix>` | `src/RemoteFlow.App.Mac/RemoteFlow.App.Mac.csproj` 的 `<ApplicationDisplayVersion>`（`<ApplicationVersion>` 单调 +1） |
| Tag | `vX.Y.Z`（如 `v0.9.0`） | `macos-vX.Y.Z`（如 `macos-v0.3.0`） |
| 封版脚本 | `scripts/seal-release.ps1` | `scripts/seal-release-macos.sh` |
| 本地发布脚本 | `scripts/release-local.ps1`（需 pwsh） | `scripts/release-local-macos.sh`（bash） |
| Release workflow（备用） | `.github/workflows/release-windows.yml`（触发 `tags: v[0-9]*`） | `.github/workflows/release-macos.yml`（触发 `tags: macos-v*`） |
| 本地快照 | `releases/vX.Y.Z/`（`source/` + `CHANGELOG.md`） | `releases/macos-vX.Y.Z/`（同上） |
| 产物 | `RemoteFlow-vX.Y.Z-win-x64.exe` / `.zip` | `RemoteFlow-vX.Y.Z-macos-arm64.dmg` / `-x64.dmg` |
| 根 `CHANGELOG.md` | Windows 版本条目 | 走 `releases/macos-*/CHANGELOG.md`；根 `CHANGELOG.md` 是否收 macOS 条目按当时约定 |

- 两条版本线**独立递增**，互不牵制。Windows 修一个 bug 发 `v0.9.1` 不需要 macOS 也动。
- `Directory.Build.props` 的 `AssemblyVersion` / `FileVersion` 是全解决方案共享的——macOS 程序集
  也会带上 Windows 版本号做内部元数据，这是**已知的无害副作用**（macOS bundle 版本以 csproj 为准）。
- 语义化版本：`MAJOR.MINOR.PATCH`。新功能 / 能力增强 = MINOR；纯 Bug Fix / 稳定性 = PATCH；
  Breaking Change = MAJOR。已发布的 tag / 产物不覆盖、不倒退。

### Latest 标记约定

GitHub Releases 页只有**一个** Latest 槽位，而本项目有两条**独立递增**的版本线 ——
谁最后发布谁就自动占住 Latest，会把另一条线的徽章顶掉。约定：

- **Latest 固定归 Windows 线**（最新的 `vX.Y.Z`）。Releases 页最显眼的位置给主线产品，
  macOS 用户按 `macos-v*` 的 tag 名自行辨认。
- `scripts/release-local.ps1` 发布时照常 `--latest`；`scripts/release-local-macos.sh`
  发布后把 Latest **复位**到最新的 `vX.Y.Z`（已内建在脚本的 7/7 步，无需手工干预）。
- 手工修正（如 macOS 发布后脚本没复位）：`gh release edit vX.Y.Z --latest`。
- 这是**有意的不对称**，不是 bug —— 不要为了「对称」让 macOS 也去设 Latest。

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

## 4. 门禁（GitHub Actions 已弃用，改本地）

**现状：GitHub Actions 已弃用（2026-09-10）。** 账户计费问题导致所有 job 在启动前即被拒，
`ci.yml` / `release-windows.yml` / `release-macos.yml` 的触发器已停用（文件保留，正文注释里有恢复方法）。
因此 **`main` 目前没有自动门禁**——不再有「CI 两端都绿」这个信号可看。

**替代门禁（合共享层改动前必须本地跑，两端都要能编译）**：

```
dotnet build -c Release                                   # 共享层 + Windows 侧编译校验
dotnet build src/RemoteFlow.App.Mac -c Release            # macOS 侧编译校验（在 mac 上跑）
# 测试逐项目跑 —— 解决方案级 `dotnet test` 会拉起跑不了的 IntegrationTests.Windows
dotnet test tests/RemoteFlow.Core.Tests              -c Release
dotnet test tests/RemoteFlow.IntegrationTests        -c Release
dotnet test tests/RemoteFlow.IntegrationTests.Windows -c Release   # 仅 Windows 机器
dotnet test tests/RemoteFlow.IntegrationTests.Mac    -c Release    # 仅 macOS 机器
```

判据同过去：**改 `Core` / `Presentation` = 同时影响两端**，加法优先；必须删改签名时，
同一个改动里把 `App`(WPF) + `App.Mac`(AppKit) 一起改到，**两端本地编译都过**才算完成。

**如实标注**：本地跑绿不等于 CI 跑绿，**不得据此把「未执行」写成「已通过」**；
CHANGELOG 的 Verification 要写明是在哪台机器、跑了哪些项目、哪些没跑。

**恢复 Actions 的条件**：账户计费恢复正常后，取消三个 workflow 里 `on:` 块的注释即可。
在那之前 `releases/` 的产物一律走 §5.6 通道 A（本地发布）。

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

### 5.2 本地回归 + 实机验证（不得跳过、不得编造结果）

**编译与测试**：

```
# Windows
dotnet build -c Release --nologo                    # 0 错误
dotnet test  -c Release --nologo                    # Core / Integration / *.Windows 全绿；*.Mac skip 属正常

# macOS（逐项目跑，见 §5.6 —— 解决方案级 dotnet test 会拉起跑不了的 *.Windows）
dotnet test tests/RemoteFlow.Core.Tests              -c Release
dotnet test tests/RemoteFlow.IntegrationTests        -c Release
dotnet test tests/RemoteFlow.IntegrationTests.Mac    -c Release
```

**实机验证（这一条最容易漏，必须做）**：光「编译通过 + 测试通过」不算数，还要**把产物当用户那样打开一次**，
确认能起、能进主界面、能进设置，退出干净。

```
# Windows
dotnet publish src/RemoteFlow.App -c Release -r win-x64 --nologo -o <tmp>/publish
<启动 publish 出的单文件 exe，确认能起能退>

# macOS —— 脚本已内置为强制关卡（见 §5.6 通道 A）
scripts/release-local-macos.sh --publish     # 第 6 步会挂载宿主架构那份 DMG 启动 app，起不来即中止
```

- macOS 侧只验**宿主架构**那一份：另一份是交叉编译产物，本机根本跑不了 ——
  CHANGELOG 的 Verification 要如实写「该架构未实机走查」。
- **未执行的验证**（如 RDP / SSH / VNC 真实会话、另一架构的实机走查）**在 CHANGELOG 里如实写「未执行」**，
  不得写成「通过」。

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

### 5.6 构建产物 + 发布 Release

**发布前必须确认源码快照 = 产物对应源码**：若 `seal` 之后源码又改过（尤其影响构建的
`src/` `native/` `scripts/`），必须重新封版再构建 —— 不得「改完源码直接复制旧快照」。

#### 通道 A：本地发布（唯一路径）

```
# Windows（需 pwsh；先跑过 seal-release.ps1 封版）
pwsh scripts/release-local.ps1 -Version X.Y.Z            # 只构建，产物落 releases/vX.Y.Z/
pwsh scripts/release-local.ps1 -Version X.Y.Z -Publish   # 构建 + 创建并发布 GitHub Release

# macOS（先跑过 seal-release-macos.sh 封版）
scripts/release-local-macos.sh                           # 版本取 csproj，只构建
scripts/release-local-macos.sh --publish                 # 构建 + 创建并发布 GitHub Release
scripts/release-local-macos.sh X.Y.Z --backfill --publish # 补发旧版本（在该 tag 的源码树上构建）
```

- 两个脚本都会：跑测试门禁 → 构建 → 产物拷进 `releases/<tag>/` → 算 SHA-256 / 大小 →
  回写该版本 CHANGELOG 的「产物」节 → **实机验证** →（加 `--publish` / `-Publish`）建 Release
  （草稿 → 发布 → 设 Latest）。
- **实机验证是发布前的强制关卡**：macOS 侧脚本会挂载宿主架构那份 DMG、直接启动 app 并观察数秒，
  起不来就中止，**坏产物不会走到创建 Release 那一步**；`--skip-run-check` 仅供无 GUI 会话时使用，
  用了就等于「未实机走查」，必须在 CHANGELOG 里写明。Windows 侧脚本暂未内建这一步，
  按 §5.2 手工启动 publish 出的 exe 验证。
- macOS 侧只验宿主架构那一份；交叉编译的另一份本机跑不了，CHANGELOG 要如实标注。
- **macOS 的产物永远两份**：`-arm64.dmg` + `-x64.dmg`，分别打包，**不融合 universal**
  （融合后重签会破坏 CoreCLR VM 初始化）。宿主架构那份原生编译，另一份交叉编译 ——
  缺的 OpenSSL 由 `native/rdp/build-openssl.sh` 自动补（首次联网下载源码）。
- **签名**：本地无 Developer ID 证书时用 ad-hoc / Apple Development 签名，**别的机器首次打开
  会被 Gatekeeper 拦**（右键「打开」或到「系统设置 → 隐私与安全性」放行）。正式对外签名 + 公证
  需 Developer ID Application 证书，并在 macOS 侧设 `RF_SIGN_IDENTITY` + `RF_NOTARY_PROFILE`。
- **补发旧版本**：macOS 用 `--backfill`（在 tag 的临时 worktree 里构建，保证产物对应快照源码；
  当前工作树若与该 tag 的构建路径有差异，非 backfill 模式会直接报错拒绝）。
- **已决定不补发的版本**：`macos-v0.8.0` —— 封版时 Actions 计费故障导致无产物，
  功能已由 `macos-v0.8.1` 覆盖，决定不补发（详见 `releases/macos-v0.8.0/CHANGELOG.md`）。
  tag 保留但不对应 Release，这是有意为之，不是遗漏。
- **已删除 Release 的版本**：`macos-v0.2.0` / `macos-v0.3.0` —— 当初走 Actions 通道时建了
  Release **草稿**但始终没人点 Publish，此后 v0.4.0 起均直接发布，两个草稿就此搁置。
  2026-09-10 经用户确认**删除这两个草稿 Release**：`gh release delete <tag> --yes`
  （**不带** `--cleanup-tag`，**tag 保留**）。
  注意：那两个版本的双架构 DMG **只存在于这两个草稿里**，本地 `releases/macos-v0.2.0/`、
  `releases/macos-v0.3.0/` 只有 `source/` + `CHANGELOG.md`，故删除后该两版的二进制**不再可得**；
  这是已知且接受的结果。tag 保留、快照保留，历史可追溯。

#### ~~通道 B：GitHub Actions~~ —— 已弃用（2026-09-10）

保留本节仅作历史参考与恢复指引。触发器已停用，`release-windows.yml` / `release-macos.yml`
当前不会运行；本文件正文注释里有恢复方法。下面描述的是**恢复之后**的行为：

- push tag 后自动跑：build + test + 打包 + `upload-artifact` + **创建 GitHub Release 草稿**（`draft: true`）。
- `gh run list --workflow=release-macos.yml --limit 1` 看结果；`gh release view <tag>` 看草稿与产物。
- **人工审核草稿**：产物名 / 大小对；Release body（`--notes-file releases/<tag>/CHANGELOG.md`）；
  确认后 `gh release edit <tag> --draft=false --latest`（或网页 Publish）。设 Latest 会自动取消其它版本的 Latest。
- 弃用原因：账户计费问题使 job 在启动前即被拒（`The job was not started because recent account
  payments have failed...`），重跑、官方状态页恢复都无效（已实测），属账户侧而非平台侧。

#### 产物的两个去处

1. **GitHub Release 页** = 用户下载渠道。
2. **本地 `releases/<tag>/` 目录** = 磁盘上的不可变快照，用于快速回退 / 现场保留
   （`.gitignore` 排除 `*.exe|*.zip|*.dmg`，不入库）。
   → 最终 `releases/<tag>/` 里应有 `source/` + `CHANGELOG.md`（入库）+ 产物（本地，gitignore）。

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
- **本地构建发布时**，Verification 里还要写清：
  - **实机启动验证的结果**：宿主架构那份 DMG 是否实际启动过、观察到什么
    （如「挂载 x64 DMG 启动 app，存活 8s 无异常」）；跳过没做的就写「未实机走查」，不得含糊。
  - 构建机架构与「哪份原生 / 哪份交叉」——如「Intel 主机：x64 原生 + arm64 交叉编译」；
    交叉编出来的那份**无法在本机实机走查**，必须如实写「未实机走查」。
  - 签名类型（ad-hoc / Apple Development / Developer ID + 公证），以及未公证时
    「其它机器首次打开需放行」的提示。

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

- 原做法（基于 Actions）：合并 `release-windows.yml` + `release-macos.yml` 为一个 `release.yml`。
  但 Actions 已弃用（§4），这条路目前不通；若日后本地双平台都要发，更现实的形态是
  **一个版本号、两个平台各自本地构建、汇总到同一个 GitHub Release**（`gh release upload --clobber` 追加产物）。
- **启用时机**：等 macOS 版到一个够稳的基线（多轮实机验证通过、迭代节奏放缓）。
  在那之前保持双线——锁步会让稳定的 Windows 被快速迭代的 macOS 卡住。
- 统一发布**不能替代**分支纪律 + 本地门禁——那两条才是防「双端发散」的根本。

---

## 9. 安全红线（发布同样适用）

- Secret（密码 / 私钥 / Passphrase / Token / 证书私钥）**不进** `source/` 快照、产物、日志、CHANGELOG、异常消息。
  需要配置模板给 `.env.example` / `config.example.*`。
- 不删未知用户文件、不覆盖已有 Backup / Release Snapshot。
- 数据库 schema 变更：`releases/*/CHANGELOG.md` 注明迁移路径；优先向后兼容 / 可回滚。
- 高风险 Git 操作（历史重写、大规模目录移动、schema 迁移）前先 `git bundle create` 备份到
  `D:\AIProjects\_Backup\YYYY-MM-DD-RemoteFlow-操作前备份-vX.Y.bundle` 并 `git bundle verify`。
