# RemoteFlow v0.11.2

发布日期：2026-09-10

上一正式版本为 v0.11.1（Windows）。纯 Bug Fix：继续修复云同步在「第二台设备」上的
阻塞——v0.11.1 修了陈旧 VMK 缓存，本版修更常见、更根本的两个成因，外加设备重复注册。
macOS 侧同批走 `macos-v0.5.2`，详见 [`releases/macos-v0.5.2/CHANGELOG.md`](../macos-v0.5.2/CHANGELOG.md)。

## Fixed

### 1. 第二台设备一直「同步出错，将自动重试」——拉取时父实体未到

- **成因**：AppsCloud `GET /sync/pull` 按 `Revision` 单调排序返回每个变更过的实体的当前状态。
  `credential-secret` 与它的 `credential` 是父子关系（`EntityId` 相同），但 `credential` 元数据
  被另一台设备改过、`credential-secret` 没动时，`credential` 的 `Revision` 会被推到
  `credential-secret` 之上。于是一台干净设备拉取时，`credential-secret` 先到、父 `credential`
  还排在后面没落地。
- **后果**：`CredentialSecretSyncSource.ApplyAsync` 找不到父 `credential`，直接
  `throw new InvalidOperationException(...)` → `SyncCoordinator.RunOnceAsync` 的兜底 `catch`
  捕获 → `SyncStatus.Error`；这一页游标没推进（异常发生在 `SetCursorAsync` 之前）→
  下一轮从同一位置再拉、再抛，**面板永远停在「同步出错」，重启应用也一样**。
- **修复**：
  - `CredentialSecretSyncSource.ApplyAsync` 改抛 `SyncDependencyNotReadyException`（可延后信号）。
  - `SyncCoordinator.PullAsync`：
    - 同一页内按依赖顺序应用 —— `DependencyRank`：`credential-secret` 排最后，父实体先落地。
    - 跨页时把 `SyncDependencyNotReadyException` 的变更收进 `deferred` 列表，**不使整轮失败、
      正常推进游标**；整轮拉完（父实体此时都已落地）后统一重试 `deferred`。
    - 重试仍找不到父实体 → 记为孤儿 Secret，`LogWarning` 跳过，不再卡死。

### 2. 两台设备版本号无限互相推高 / 反复「刷新后好多冲突」

- **成因**：`SqliteConnectionRepository.UpdateAsync` / `SqliteCredentialRepository.UpdateAsync`
  在写库时强制 `UpdatedAt = DateTimeOffset.Now`。`SyncCoordinator` 拉取落地后，用「刚解密的
  明文」算内容哈希存起来；但下一轮对账时 `GetPlaintextAsync` 读到的是仓储改过 `UpdatedAt`
  的版本 → 哈希对不上 → 对账把这条正常拉下来的实体当成「本地漂移」又 enqueue 推上去。
  两台设备互相把对方的更新当漂移推回去，版本号一路涨（这也正是上面 §1 里 `credential`
  Revision 被推高、超过 `credential-secret` 的直接来源）。
- **修复**：`PullAsync` 落地一个实体后，内容哈希改用 `source.GetPlaintextAsync` **重新读取的
  实际持久化状态**计算，而非拉下来的明文。仓储写入时怎么规范化（改 `UpdatedAt`、字段重排…），
  记录的哈希都与「下一轮对账会算出的哈希」一致 → 对账稳定，不再假漂移。

### 3. 注册 2 台设备，账号里显示 5 台

- **成因**：`CloudSyncService.SignOutAsync(wipeLocalCloudData: true)`（「清除此设备云数据」）
  会 `settings.CloudDeviceId = string.Empty`。下次登录 `EnsureDeviceId()` 生成一个新 GUID →
  服务端按 `ClientDeviceId` 注册一个**新的** Device 行。每「清除 + 重新登录」一次多一个。
- **修复**：`SignOutAsync` 的 wipe 分支不再清空 `CloudDeviceId`。设备标识是这台机器的稳定身份，
  「清除此设备云数据」清的是 VMK 缓存 + 本地同步表 + 记住的邮箱，不动机器身份。

## Changed

- 新增 `RemoteFlow.Core.Cloud.SyncDependencyNotReadyException`。
- `SyncCoordinator.ApplyChangeAsync` 不再返回明文（改由 `PullAsync` 重新读取实际状态算哈希）。

## Known Issues

- `credential-secret` 的 `EntityId` 与其 `credential` 相同、依赖它先落地——目前只处理这一对
  依赖关系（其余实体互不依赖）。
- 已经产生的历史 Revision 倒挂、版本号虚高不会自动回收，但不再影响同步（延后重试兜住）。
- 两台物理机的完整同步 / 冲突 / 恢复人工演练封版时仍未做。
- v0.11.0 / v0.11.1 的已知项（MFA、首次同步统计确认框等）不变。

## Verification

- Build：`dotnet build RemoteFlow.slnx -c Release --nologo` → 0 错误（1 处 pre-existing 测试工程 `CS0067` 警告）。
- Tests：`dotnet test -c Release` → Core.Tests 42 / 42；IntegrationTests 159 / 159
  （新增 `SyncDependencyOrderingTests.Child_that_arrives_before_its_parent_is_deferred_and_retried_not_fatal`：
  设备 A 建父+子再改父使 Revision 倒挂；设备 B 一次拉取 → 结果 `Synced` 而非 `Error`、
  父子都落地、再跑一轮 0 推送）；IntegrationTests.Windows 9 / 9；IntegrationTests.Mac 10 skipped。
- 端到端：`APPSCLOUD_BASE_URL=https://sync.appscloud.cn/` 下 `CloudRoundTripTests` /
  `CloudSyncFacadeTests` 3 / 3 通过。
- Runtime：`dotnet publish src/RemoteFlow.App -c Release -r win-x64` 单文件 exe 启动正常。
- 未执行：完整 UI 手动走查、实机 RDP / SSH / VNC 会话回归、多台真实设备演练。
- Platform / Architecture：Windows 11 · win-x64（.NET 10 self-contained single-file）。
- Schema：无本地 schema 变更，无服务端变更（AppsCloud 0.11.1 即可）。

## Artifacts

由 `.github/workflows/release-windows.yml` 在 `v0.11.2` tag 推送后构建并上传到 GitHub Release：

- `RemoteFlow-v0.11.2-win-x64.exe` —— Size / SHA-256：见发布后补录
- `RemoteFlow-v0.11.2-win-x64.zip`（含 `Assets/Terminal/`）—— Size / SHA-256：见发布后补录

## Git

- Source Snapshot：`release: RemoteFlow v0.11.2`，`source/` = HEAD 树（排除 `releases/`），
  含拉取依赖顺序 / 对账假漂移 / 设备重复修复 + 版本号提升（Win 0.11.2 / Mac 0.5.2）。
- 提交范围 `v0.11.1..`：`94a44cf` fix(cloud) · `chore` 版本号 0.11.2 · `chore(release)` macOS 0.5.2 · 快照封版。
- Tag：`v0.11.2`。分支：`main`。
