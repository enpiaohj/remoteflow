# RemoteFlow v0.11.0

发布日期：2026-09-09

上一正式版本为 v0.10.0（Windows）。本版把云同步的**端到端加密模型改为「口令派生」**
（Bitwarden 式）：另一台设备只需**邮箱 + 主口令**即可解锁同步数据，不再必须走「设备批准」
或 Recovery Key。Recovery Key 回归本职 —— 只在忘记主口令时恢复访问权。同时补齐云同步的
账号入口与使用体验。

macOS 原生版同一功能、同一批提交，走 `macos-v0.5.0`（AppKit 界面），详见
[`releases/macos-v0.5.0/CHANGELOG.md`](../macos-v0.5.0/CHANGELOG.md)。加密栈与同步引擎在
`RemoteFlow.Core` / `RemoteFlow.Infrastructure`，双端共享；只有设置面板是各自的原生实现。

**破坏性变更**：加密模型不兼容 v0.10.0。云端 AppsCloud 需同步升级到 0.11（已上线
`https://sync.appscloud.cn/`）；升级后需在「云同步」里重新初始化 Vault ——
旧的「设备信封」数据无法迁移。不登录云账号则与 v0.10.0 完全一致地本地运行。

## 架构

- **VMK 加密改为口令派生**（双端共享）：主密钥（VMK）改由
  `PBKDF2-SHA256(主口令, 随机 salt, 600,000 迭代)` 得到的 KEK 用 AES-256-GCM 包装
  （`VaultAlgorithms.SecretEnvelope = "PBKDF2-SHA256_AES-256-GCM"`）。
  另一台设备**只需邮箱 + 主口令**即可解锁 —— 不再需要设备 ECDH 密钥对或 Recovery Key 走通。
- **客户端派生认证密钥**：`VaultCryptography.DeriveAuthKey(主口令, 邮箱)` =
  `Base64(PBKDF2-SHA256(主口令, "RemoteFlow/auth/v1:"+小写邮箱, 600k, 32B))`，
  作为 `password` 字段发给 AppsCloud 的注册 / 登录 / 改口令端点；服务端对它再做一次
  `PasswordHasher` —— **服务端全程拿不到主口令明文**。
- 统一信封类型 `VaultKeyEnvelope`（`Kind` = `password` / `recovery`，含 `Salt` / `Iterations`）。
  移除 `DeviceKeyService`、设备 ECDH 密钥对、`DeviceKeyEnvelope` / `RecoveryKeyEnvelope`。
  `RecoveryKeyService` 改为 `WrapWithSecret(Recovery, RecoveryKey.ToDisplayString())`。
- `IVaultSession.WrapWithSecret(kind, secret)` 取代 `WrapMasterKeyForDevice`（VMK 不出会话）。
- **设备批准降为可选安全层**：账号可开 `Vault.RequireDeviceApproval` 开关（默认关）——
  开启后 `GET /vault/envelope/password` 对未批准设备返回 403，新设备即使主口令正确也要
  一台已登录设备在 `DeviceVaultAccess` 里登记批准才能同步。
- `VaultMasterKeyService`：`TryUnlockAsync(password)` → 缓存命中直接解锁；否则按状态返回
  `NeedsBootstrap` / `NeedsApproval`（开了批准且本机未批）/ `NeedsPassword` / 取口令信封
  `UnwrapWithSecret` → `Unlocked`。`BootstrapAsync(password, requireDeviceApproval)` /
  `RecoverAsync` / `ReWrapPasswordEnvelopeAsync` / `ResetRecoveryKeyAsync`。
- `CloudSyncService`：`AuthenticateAsync`（登录 only，注册走独立路径）、`_pendingPassword`
  暂存口令供解锁、`UnlockWithPasswordAsync` / `BootstrapVaultAsync(requireDeviceApproval)` /
  `ChangePasswordAsync`（改认证密钥 → 用新会话重登 → 重新包装口令信封）/
  `ResetRecoveryKeyAsync` / `GetRequireApprovalAsync` / `SetRequireApprovalAsync` /
  `GetPendingDevicesAsync` / `ApproveDeviceAsync` / `RetryUnlockAsync`。
- 依赖后端 **AppsCloud 0.11**（G3.2）：口令 Vault + `POST /api/v1/auth/change-password` +
  `DeviceVaultAccess` 表 + `RequireDeviceApproval`。服务端 migration `PasswordVault`。
- `AppSettings` 新增 `CloudEmail`（记住上次登录邮箱）。

## Added

- **设置 → 云同步**：
  - **登录 / 注册模式切换**（`CloudAuthMode`）。注册表单：邮箱 + 主口令 + 二次确认（≥12 位提示）
    + 可选「新设备登录需在已有设备上批准」。
  - **「补输主口令」卡片**（`NeedsPassword` 状态）：本机已登录但无缓存 VMK 时输入主口令解锁，
    旁附 Recovery Key 恢复入口。
  - **更改主口令**入口（`dialogs.PromptPasswordAsync` 两步：当前口令 + 新口令确认，≥12 位）。
  - **重置 Recovery Key** 入口（生成新 Key、旧的立即失效、一次性展示）。
  - Recovery Key 一次性展示：**「复制」/「另存为文件」**按钮；必须先复制或另存
    （`RecoveryKeySecured` 门槛）才能点「我已妥善保存」。
  - 已就绪卡片：**上次同步条数摘要**（「上传 N · 下载 M」）、**「新设备需批准」开关**、
    待批准设备列表（`RequireApproval` 开启时才拉取 + 显示）。
  - 服务地址字段默认只读，**左键三击进入可编辑**（无 checkbox / 提示文字，`UnlockServerUrlField()`）。
  - 记住上次登录邮箱，`TryResumeAsync` 也带回。

## Changed

- 登录不再「首次自动注册」—— 改为明确的注册入口。登录失败（401）抛
  `CloudSignInException("邮箱或密码不正确。若还没有账号，请点「注册」。")`。
- `CloudSyncViewModel.Describe(ex)`：补 `CryptographicException`（「主口令或 Recovery Key 不正确」）；
  403 文案改为「本设备暂无访问权，请在已登录的设备上批准，或用 Recovery Key 恢复」。
- WPF `SettingsPage.xaml` / AppKit `SettingsPaneView.cs` 云同步整块按新状态机
  （`SignedOut` / `NeedsVaultSetup` / `NeedsPassword` / `NeedsApproval` / `Ready`）改造。

## Fixed

- **同步循环卡在「同步中」**：`SyncCoordinator.RunOnceAsync` 用 `try` / `catch`(全类型) / `finally`
  包裹，任何未预期异常都记 `SyncStatus.Error`、网络类记 `Offline`，`finally` 里保证状态落地，
  不再永久停在 `Syncing`（`1a0ee11`）。
- **「刷新后看到好多冲突」**：首次同步（游标 0 **且无待推变更**）先 `PullAsync` 一轮，
  认领服务端既有实体的版本 / 内容哈希，避免随后的对账（`ReconcileAsync`）把本地既有条目
  全部当「新建」推上去、与服务端逐条撞冲突。已有真实待推变更时不做这步，走正常 Push→冲突。

## Removed

- `DeviceKeyService` 及其测试、设备 ECDH 密钥对、`CloudSyncRegistration` 里的
  `AddSingleton<DeviceKeyService>()`。
- `ICloudClient` 的设备信封方法（`GetDeviceEnvelopeAsync` / `AddDeviceEnvelopeAsync` /
  `GetRecoveryEnvelopeAsync` / `PutRecoveryEnvelopeAsync` / `GetCurrentDeviceIdAsync`）。

## Known Issues

- **未做多真实设备演练**：加密栈与同步引擎有对**已上线的 `https://sync.appscloud.cn/`**
  （AppsCloud 0.11）的端到端集成测试（`CloudRoundTripTests` / `CloudSyncFacadeTests`），
  但「两台物理机各自登录 → 改同一条目 → 冲突 → 解决 → 恢复」的完整人工演练封版时未做。
- **多重身份验证（MFA）** 尚未实现，后续版本补。
- **首次同步的对象统计确认对话框**（「将同步 N 条 … 可取消」）尚未实现。
- 面板 UI（各状态分区、Recovery Key 横幅的复制 / 另存、三击解锁、批准开关、深浅主题排版）
  未做实机截图走查。
- 从 v0.10.0 升级：旧 Vault / 已同步数据无法迁移，需「清除此设备云数据」后重新初始化。

## Verification

- Build：`dotnet build RemoteFlow.slnx -c Release --nologo` → 0 错误（1 处 pre-existing 测试工程
  `CS0067` 警告，非本版引入）。
- Tests：`dotnet test -c Release` →
  Core.Tests 42 / 42；IntegrationTests 154 / 154（3 项真机端到端在无 `APPSCLOUD_BASE_URL` 时 skip）；
  IntegrationTests.Windows 9 / 9；IntegrationTests.Mac 在 Windows 上 skip（预期）。
- 端到端：`APPSCLOUD_BASE_URL=https://sync.appscloud.cn/`（AppsCloud 0.11 已上线）下
  `CloudRoundTripTests`（口令 / 设备批准 / Recovery Key 三条解锁路径 + 加密 push → 拉回解密）、
  `CloudSyncFacadeTests`（登录 only + 凭据错误提示；干净设备只用邮箱 + 主口令恢复连接 / 凭据 /
  可连接的明文密码）全 3 项通过。
- Runtime：`dotnet publish src/RemoteFlow.App -c Release -r win-x64` → 单文件 `RemoteFlow.exe`
  （约 74.8 MB）启动正常，运行 12s 无异常退出。
- 未执行：完整 UI 手动走查、实机 RDP / SSH / VNC 会话回归、多台真实设备的同步 / 冲突 / 恢复演练。
- Platform / Architecture：Windows 11 · win-x64（.NET 10 self-contained single-file）。
- Schema：AppsCloud 服务端 migration `PasswordVault`（drop `DeviceKeyEnvelopes` / `RecoveryEnvelopes`，
  加 `Vaults.RequireDeviceApproval`，建 `DeviceVaultAccesses` / `VaultKeyEnvelopes`）—— 已在
  `https://sync.appscloud.cn/` 应用；旧 vault 数据不可迁移。本地 SQLite schema 不变。

## Artifacts

由 `.github/workflows/release-windows.yml` 在 `v0.11.0` tag 推送后构建并上传到 GitHub Release
（草稿，人工审核后 Publish）：

- `RemoteFlow-v0.11.0-win-x64.exe` —— Size / SHA-256：见发布后补录
- `RemoteFlow-v0.11.0-win-x64.zip`（含 `Assets/Terminal/`）—— Size / SHA-256：见发布后补录

产物入库策略：`releases/v0.11.0/` 里 `source/` + 本 CHANGELOG **入 Git**；exe / zip 由
`.gitignore` 排除，发布后 `gh release download v0.11.0` 下载一份到本地做磁盘快照，
SHA-256 与 Release 页 / CI 日志核对一致。用户下载走 GitHub Release 页。

## Git

- Source Snapshot：`release: RemoteFlow v0.11.0`，`source/` = 含口令派生 vault + 同步修复 +
  版本号提升（Windows 0.11.0 + macOS 0.5.0）的 HEAD 树（排除 `releases/`）。
- 本版提交范围 `v0.10.0..`：
  `1a0ee11` fix(cloud) 同步循环兜底 · `d70aab1` + `991e537` 口令派生 vault（Core/Infra +
  Presentation/tests）· `6ec8b16` chore 版本号 0.11.0 · `6c9eb01` chore(release) macOS 0.5.0 ·
  快照封版提交。
- Tag：`v0.11.0`（指向 `release: RemoteFlow v0.11.0`）。
- 分支：`main`（双平台统一树）。
