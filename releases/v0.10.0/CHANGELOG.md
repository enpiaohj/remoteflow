# RemoteFlow v0.10.0

发布日期：2026-09-09

上一正式版本为 v0.9.0（Windows）。本版落地**云同步（Cloud Sync）**：连接、分组、标签、
凭据元数据与密码经端到端加密后同步到自建的 AppsCloud 服务，多台设备之间保持一致。
服务端只存密文，看不到明文、主密钥（VMK）或 Recovery Key。

macOS 原生版同一功能、同一批提交，走 `macos-v0.4.0`（AppKit 界面），详见
[`releases/macos-v0.4.0/CHANGELOG.md`](../macos-v0.4.0/CHANGELOG.md)。加密栈与同步引擎在
`RemoteFlow.Core` / `RemoteFlow.Infrastructure`，双端共享；只有设置面板是各自的原生实现。

无破坏性变更。不登录云账号，RemoteFlow 与之前完全一致地本地运行。

## 架构

- **端到端加密同步栈**（新增，双端共享）：
  - `RemoteFlow.Core.Cloud`：契约、`RecoveryKey`（RFC 4648 Base32）、`CloudEndpoint` / `CloudSyncGate` /
    `ISyncChangeTracker` / `CloudSignInException` 等。
  - `RemoteFlow.Infrastructure.Security`：`VaultCryptography`（ECDH-P256 + HKDF-SHA256 + AES-256-GCM）、
    设备信封（临时 ECDH）、Recovery 信封（Recovery Key = 256-bit CSPRNG）、载荷加密
    （实体密钥 = `HKDF(VMK, salt=EntityId)`，AAD 绑定 `UserId | AppId | EntityType | EntityId | SchemaVersion | KeyVersion`）；
    `VaultMasterKeyService`（解锁 / 初始化 / 恢复 / 批准设备）、`VaultSession`、`DeviceKeyService` / `RecoveryKeyService`。
  - `RemoteFlow.Infrastructure.Sync`：`AppsCloudClient`（REST + Access Token 内存持有 + 401 自动 refresh）、
    `JwtReader`、`CredentialVaultTokenStore` / `CredentialVaultKeyStore`（DPAPI / Keychain）、
    `SqliteSyncStore`（Outbox + 每流单调游标 + 冲突表 + 内容哈希）、
    `SyncCoordinator`（Reconcile → Push → Pull，自推回声跳过）、
    `ConflictService`（检测非合并：保留本机 / 使用云端）、
    `SyncSerializer`、5 个 `ISyncEntitySource`（连接 / 分组 / 标签 / 凭据元数据 / 凭据 Secret）、
    `OutboxSyncChangeTracker`、`CloudSyncAutoRunner`（周期同步）。
  - `CloudSyncService`（UI 唯一入口门面）、`CloudSyncViewModel`（面板状态机，共享）。
- **本地 SQLite 迁移** `user_version` 4 → 5：`sync_entity_state` / `sync_outbox` / `sync_stream_cursor` /
  `sync_conflict` 四张表 + `sync_entity_state.content_hash`。只加不改，旧库自动升级、可回退到不启用同步。
- `Application/Services` 的 `ConnectionService` / `CredentialService` / `GroupService` 增加可选
  `ISyncChangeTracker` 构造参数，每次写库后登记待同步操作；未启用云同步时是 No-Op。
- 依赖后端 **AppsCloud**（独立仓库）的 G3 E2EE Vault 元数据 / 信封分发 + G4 Sync Push/Pull 服务。

## Added

- **设置 → 云同步** 分页（WPF），按状态分区：
  - **未登录**：邮箱 + 密码登录 AppsCloud。默认服务地址 `https://sync.appscloud.cn/`、默认只读；
    勾选「使用自定义服务地址（一般无需修改）」解锁编辑，连自建 / 内网 AppsCloud。
  - **需初始化 Vault**：本账号第一台设备，本机生成 VMK 与 Recovery Key（**只显示一次**，一次性横幅）。
  - **需要授权**：已有 Vault、本设备未授权 —— 在另一台已登录设备上批准，或输入 Recovery Key 恢复。
  - **已就绪**：同步状态 / 待上传数、立即同步 / 刷新、待批准设备列表（逐个批准）、
    冲突列表（保留本机 / 使用云端）、退出云账号 / 清除此设备云数据。
- **首次登录自动开户**：邮箱在 AppsCloud 尚无账号时，自动创建后登录（设计文档「注册 / 登录」为同一步）。
- **本地优先**：未登录或云端不可达时，连接 CRUD、RDP / SSH / VNC 会话、密码读取均正常，
  改动在本地 Outbox 累积，恢复后由后台 `CloudSyncAutoRunner` 补传。
- 密码 / 私钥仍只经 Windows Credential Vault（DPAPI）接触明文；同步时用 VMK 派生的实体密钥
  AES-256-GCM 加密，服务端只拿到密文。

## Changed

- 登录 / 同步失败提示改为人读中文：
  - `AppsCloudClient` 解析 RFC 7807 ProblemDetails（优先首条字段校验错误，其次 `detail` / `title`），
    不再把原始 JSON 抛给 UI。
  - `CloudSyncViewModel.Describe(ex)` 按异常类型与状态码分类（401 凭据错 / 403 未授权 / 409 已注册 /
    5xx 服务不可用 / `HttpRequestException` 网络 / `TaskCanceledException` 超时）。
  - `LoginOrRegisterAsync`：登录失败且密码 < 12 位时直接给准话，不再空跑一次注册撞 400；
    账号已存在但密码不符抛 `CloudSignInException("邮箱或密码不正确。")`，**绝不覆盖既有账号**。
- 设置页（WPF）首次显示时调用一次 `CloudSyncViewModel.InitializeAsync()`，
  让面板反映后台已恢复的登录态（此前该方法从未被调用）。
- `AppSettings` 新增 `CloudBaseUrl`（默认 `https://sync.appscloud.cn/`）/ `CloudSyncEnabled` / `CloudDeviceId`。
- 组合根 `App.xaml.cs`：`AddCloudSync()` + 启动 / 停止 `CloudSyncAutoRunner`。

## Fixed

- `CloudSyncAutoRunner.Dispose` 幂等：`_disposed` 守卫 + 吞 `ObjectDisposedException`——
  修复 `App.OnExit` 显式 `Dispose` 与容器 `DisposeAsync` 双重释放时
  `CancellationTokenSource has been disposed` 崩溃（`/run` 冒烟发现）。

## Known Issues

- **`sync.appscloud.cn` 正式部署尚未上线**：默认地址已指向该域名，但 AppsCloud 服务尚未部署到那里；
  端到端验证跑在内网测试环境。部署上线前，全新安装点「登录」会连不上，需解锁字段填可达的 AppsCloud 地址。
- **未做多真实设备演练**：加密栈与同步引擎有对真机 AppsCloud 测试环境的端到端集成测试
  （`CloudRoundTripTests` / `CloudSyncFacadeTests`），但「两台物理 Windows 机各自登录 → 批准 →
  改同一条目 → 冲突 → 解决 → 恢复」的完整人工演练封版时未做。
- **首次同步的对象统计确认对话框**（设计文档「将同步 N 条 … 可取消」）尚未实现，后续版本补。
- `AppSettings` 的 User / Device / Session 作用域区分、后端 G3.2 Key Rotation 的客户端接入 —— 后续版本。
- 面板 UI（各状态分区切换、Recovery Key 横幅、冲突 / 设备列表模板、深浅主题排版）未做实机截图走查。

## Verification

- Build：`dotnet build RemoteFlow.slnx -c Release --nologo` → 0 错误（1 处 pre-existing 测试工程
  `CS0067` 警告，非本版引入）。
- Tests：`dotnet test -c Release`（`APPSCLOUD_BASE_URL` 指向真机 AppsCloud 测试环境）→
  Core.Tests 42 / 42；IntegrationTests 154 / 154（含 `CloudRoundTripTests`、`CloudSyncFacadeTests`
  两项真机端到端：设备 A 登录 → 初始化 Vault → 造连接 / 凭据 / 密码 → push；干净设备 B
  登录 → Recovery Key → pull → 解密 → 解析出可连接的明文密码）；IntegrationTests.Windows 9 / 9；
  IntegrationTests.Mac 10 skipped（Windows 上不可运行，预期）。
- CI：push `main`（`57078fa`）触发 `ci.yml`，shared（Ubuntu）/ windows / **macos** 三 job 全绿。
- Runtime：`dotnet publish src/RemoteFlow.App -c Release -r win-x64` → 单文件 `RemoteFlow.exe`
  （约 74.8 MB）启动正常，运行 12s 无异常退出。
- 未执行：完整 UI 手动走查、实机 RDP / SSH / VNC 会话回归、多台真实设备的同步 / 冲突 / 恢复演练。
- Platform / Architecture：Windows 11 · win-x64（.NET 10 self-contained single-file）。
- Schema：本地 SQLite `user_version` 4 → 5（只加表 + 一列，旧库自动升级）；`settings.json` 新增
  云同步字段，缺失时回退默认，向后兼容。

## Artifacts

由 `.github/workflows/release-windows.yml` 在 `v0.10.0` tag 推送后构建并上传到 GitHub Release：

- `RemoteFlow-v0.10.0-win-x64.exe` —— Size / SHA-256 待 CI 产物核对后补录本节。
- `RemoteFlow-v0.10.0-win-x64.zip` —— 含 `Assets/Terminal/`（单文件 exe 的 SSH 终端需同目录资源）。

产物入库策略：`releases/v0.10.0/` 里 `source/` + 本 CHANGELOG **入 Git**；exe / zip 由 `.gitignore`
排除，发布后 `gh release download v0.10.0` 下载一份到本地 `releases/v0.10.0/` 做磁盘快照，
SHA-256 与 Release 页 / CI 日志核对一致。用户下载走 GitHub Release 页。

## Git

- Source Snapshot：`release: RemoteFlow v0.10.0`，`source/` = 含云同步 + 默认地址 + 版本号提升的
  HEAD 树（排除 `releases/`）。
- 本版提交范围 `v0.9.0..`：云同步 11 个提交 `9f2d995 … 57078fa`（G5 加密栈 / 客户端 / 同步引擎 /
  实体源 / `CloudSyncService` 门面 / `CloudSyncViewModel` + WPF 面板 / `AutoRunner` 幂等修复 /
  预配置地址 / AppKit 分页 / 自动开户 / 错误提示）+ `chore(cloud): 默认服务地址改为 sync.appscloud.cn`
  + `chore: 版本号提升至 0.10.0` + 快照封版提交。另含合入的 macOS 侧提交 `134ccaa … 873aace`
  （macOS 设置页对齐 v0.9.0，Windows 不受影响）。
- Tag：`v0.10.0`（指向 `release: RemoteFlow v0.10.0`）。
- 分支：`main`（双平台统一树）。CI 两端 + 共享全绿。
