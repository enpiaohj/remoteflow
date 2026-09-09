# 更新日志

本文件记录 RemoteFlow 的版本变更。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

每个正式版本的完整变更详情见 `releases/v<X.Y.Z>/CHANGELOG.md`。

---

## [0.12.0] — 2026-09-10

多设备一致性的两个结构改进。Windows 与 macOS 同步发布，macOS 侧见
[`releases/macos-v0.6.0/CHANGELOG.md`](releases/macos-v0.6.0/CHANGELOG.md)。

### 新增
- **「清除本地数据并从云端恢复」**（云同步 → 账号卡片，双端）：以云端为权威的兜底。
  清空本机全部连接 / 凭据及密码 / 分组 / 标签 / 连接历史与同步状态（保留云会话与设备身份），
  然后从云端完整拉回。与「清除此设备云数据」（保留本地、清同步状态）语义相反。
  - `LocalDataWiper` 事务执行；凭据 Secret 先经平台密钥库（DPAPI / Keychain）删除；
    系统「未分组」保留、SSH 主机密钥不受影响。
  - 用途：本机数据混乱 / 与另一台重复 / 想以另一台的云端数据为准时一键对齐。

### 变更（冲突处理）
- **元数据冲突 Last-Writer-Wins 自动解决**：同一条 connection / credential 在两台设备都被改过时，
  按内容更新时间（`UpdatedAt`）的**最后一笔**自动收敛，不再每次弹「保留本机 / 使用云端」：
  - 本地较新 → 以服务端当前版本为基线重推覆盖；重推竞态再失败则采纳最新快照。
  - 云端较新 → 采纳云端、丢弃本地较早一笔，清掉待推。
  - 密码 / 私钥（credential-secret）**仍弹窗**，绝不静默覆盖；组 / 标签（尚无内容时间戳）、
    删除冲突、无法解密的也仍走对话框（保守回退）。

### 说明
- 两台机器在启用云同步前各自手工建过同名服务器，会产生**不同 ID 的重复条目**——这类
  「一条服务器两套 ID」的合并（首次加入按 host+port+protocol+name 认领云端 ID）尚未实现，
  是下一步。当前可用「清除本地数据并从云端恢复」以某一台为准收敛，或用 LWW 让同 ID 的
  并发修改自动取最新。

### 验证
- `dotnet build RemoteFlow.slnx -c Release` 0 错误；`dotnet test -c Release` —— Core.Tests 42、
  IntegrationTests 164（新增 `SyncLwwTests` 3 项 + `SyncRestoreFromCloudTests` + VM restore 测试）、
  IntegrationTests.Windows 9 全绿；IntegrationTests.Mac 在 Windows 上 skip。
- 端到端 `CloudRoundTripTests` / `CloudSyncFacadeTests` 对 `https://sync.appscloud.cn/` 3/3 通过。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。

详见 [`releases/v0.12.0/CHANGELOG.md`](releases/v0.12.0/CHANGELOG.md)。

## [0.11.3] — 2026-09-10

修复「两台设备都显示已同步、连接数却不一致」。Windows 与 macOS 同步发布，macOS 侧见
[`releases/macos-v0.5.3/CHANGELOG.md`](releases/macos-v0.5.3/CHANGELOG.md)。

### 修复
- **干净设备拉取时 `connection` 落地报外键错误 → 整轮同步失败、数据永不收敛**：
  首次同步时 `ReconcileAsync` 按 source 注册顺序把本地既有数据入队，`connection` 排在
  `credential` / `group` 之前，于是 `connection` 在服务端拿到更小的 `Revision`。一台干净设备
  按 `Revision` 顺序拉取时，`connection` 先到、它引用的 `credential` / `group` 还没落地 →
  `SqliteConnectionRepository.AddAsync` 触发 `FOREIGN KEY constraint failed` → 抛
  `SqliteException`（不在 v0.11.2 的延后捕获范围内）→ `SyncCoordinator` 整轮 `catch` →
  `SyncStatus.Error`、游标不推进、每轮都失败。界面可能停在上一次的「已同步」，
  两台机器的数据一直不一致。
- 按设计文档「同步协议 §6 / §12 / §17」重构拉取：
  - 整段变更先收进内存（跨页按实体去重、保留最高 `Revision`），**不再逐页推进游标**
    —— 一批全部成功才推进（§6）。
  - **按依赖顺序落地**：`group` / `tag` / `credential` → `credential-secret` → `connection`（§12）。
  - 落不下去的（依赖信号或外键失败）**两轮重试**；跨页时父实体在后一页的，第二轮就能过。
  - 两轮后仍失败：**不推进游标、标记 `Error`、保留已落地的本地数据**，下轮整批重试
    （已落地的靠版本号跳过），不造成永久缺口（§17）。

### 验证
- `dotnet build RemoteFlow.slnx -c Release` 0 错误；`dotnet test -c Release` ——
  Core.Tests 42、IntegrationTests 159（`SyncDependencyOrderingTests` 重写为真实仓储：
  在启用云同步前建 group + credential + 引用它们的 connection → 首次同步使 connection
  Revision 更小 → 干净设备一次拉取即收敛、`Synced` 而非 `Error`、再跑一轮 0 推送）、
  IntegrationTests.Windows 9 全绿。
- 端到端 `CloudRoundTripTests` / `CloudSyncFacadeTests` 对 `https://sync.appscloud.cn/` 3/3 通过。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。

### 升级说明
- 之前卡住的设备升级后会自动重试整批拉取并收敛。若因为服务端数据被重置过，本机的同步
  指针可能指向已不存在的云端数据 → 在「云同步」里做一次「清除此设备云数据」再重新登录即可。

详见 [`releases/v0.11.3/CHANGELOG.md`](releases/v0.11.3/CHANGELOG.md)。

## [0.11.2] — 2026-09-10

继续修复云同步在「第二台设备」上的阻塞。v0.11.1 修了陈旧 VMK 缓存，本版修更常见的两个成因。
Windows 与 macOS 同步发布，macOS 侧见
[`releases/macos-v0.5.2/CHANGELOG.md`](releases/macos-v0.5.2/CHANGELOG.md)。

### 修复
- **第二台设备一直「同步出错，将自动重试」（拉取时父实体未到）**：服务端按 Revision 排序返回变更，
  而 `credential-secret` 的 Revision 常常低于它的 `credential`（父实体被另一台设备改过、secret 没动
  就会这样）。secret 先到、父 `credential` 还没落地时，`CredentialSecretSyncSource.ApplyAsync`
  直接抛 `InvalidOperationException` → `SyncCoordinator` 整轮 `catch` → `SyncStatus.Error`，
  游标不推进，**重启也一样**。
  - 改抛可延后的 `SyncDependencyNotReadyException`。
  - `PullAsync`：同一页内按依赖排序（`credential` 等先于 `credential-secret`）；跨页时把延后项
    收集起来，整轮拉完（父实体都在了）再重试；仍找不到父实体则记为孤儿 Secret，跳过并告警，不再卡死。
- **两台设备版本号无限互相推高 / 反复出现「刷新后好多冲突」**：`SqliteConnectionRepository` /
  `SqliteCredentialRepository` 的 `UpdateAsync` 会把 `UpdatedAt` 置为当前时间。于是拉取落地后，
  `GetPlaintextAsync` 读出来的内容和刚拉下来的明文不一致 → 对账把正常拉取当成本地漂移又推上去，
  两台设备来回推、版本号乱涨（也正是上面 Revision 倒挂的根源）。
  - 拉取落地后，内容哈希改用「重新读取的实际状态」计算，不用拉下来的明文——对账从此稳定。
- **注册 2 台设备，账号里显示 5 台**：「云同步 → 清除此设备云数据」会清空本机的设备标识，
  于是每次「清除 + 重新登录」都在服务端注册一个新设备行。
  - 「清除此设备云数据」不再清空设备标识——它是这台机器的稳定身份，清缓存 / 清同步状态即可。

### 验证
- `dotnet build RemoteFlow.slnx -c Release` 0 错误；`dotnet test -c Release` ——
  Core.Tests 42、IntegrationTests 159（+`SyncDependencyOrderingTests`：父实体后到时延后重试不再整轮失败、
  重跑不再假推送）、IntegrationTests.Windows 9 全绿。
- 端到端 `CloudRoundTripTests` / `CloudSyncFacadeTests` 对 `https://sync.appscloud.cn/` 3/3 通过。
- 未执行：完整 UI 手动走查、实机会话回归、两台物理机的同步演练。

详见 [`releases/v0.11.2/CHANGELOG.md`](releases/v0.11.2/CHANGELOG.md)。

## [0.11.1] — 2026-09-09

修复 v0.11.0 的一个云同步阻塞性缺陷。Windows 与 macOS 同步发布，macOS 侧见
[`releases/macos-v0.5.1/CHANGELOG.md`](releases/macos-v0.5.1/CHANGELOG.md)。

### 修复
- **第二台设备一直「同步出错，将自动重试」，重开也不恢复**：VMK（Vault 主密钥）
  本地缓存是单一全局槽、不区分账号 / Vault。当 Vault 被重建（如从 v0.10.0 升级后
  重新初始化）或切换过账号时，`VaultMasterKeyService.TryUnlockAsync` 会无条件复用
  旧 VMK → 每次拉取的密文都解密失败 → `SyncStatus.Error`，且缓存不失效，重启也一样。
  - 现在缓存 VMK 时附带其所属 Vault 的标识（`CloudVaultStatus.VaultId`，需 AppsCloud
    ≥ 0.11.1）。解锁时先取 `/vault/status` 比对：不一致就清掉旧缓存、回到「输入主口令」，
    一致才复用。离线（拿不到 status）时仍信任本地缓存，不影响离线解锁。
  - 覆盖 bootstrap / Recovery Key 恢复 / 口令解锁三条路径。

### 变更
- 服务端 `GET /api/v1/vault/status` 响应新增 `vaultId` 字段（AppsCloud 0.11.1）。
- 移除 `IVaultKeyStore` 中口令派生模型不再使用的设备私钥方法。

### 验证
- `dotnet build RemoteFlow.slnx -c Release` 0 错误；`dotnet test -c Release` ——
  Core.Tests 42、IntegrationTests 158（+4 `VaultMasterKeyServiceTests`：陈旧缓存失效 /
  归属校验 / 离线回退 / 打 tag）、IntegrationTests.Windows 9 全绿。
- 端到端 `CloudRoundTripTests` / `CloudSyncFacadeTests` 对已升级的 `https://sync.appscloud.cn/`
  （AppsCloud 0.11.1）3/3 通过。
- 未执行：完整 UI 手动走查、实机会话回归、两台物理机的同步演练。

### 升级说明
- 已卡在「同步出错」的设备：升级到本版后会自动检测到缓存陈旧，提示重新输入主口令即可恢复；
  或手动「云同步 → 清除此设备云数据」后重新登录。

详见 [`releases/v0.11.1/CHANGELOG.md`](releases/v0.11.1/CHANGELOG.md)。

## [0.11.0] — 2026-09-09

端到端加密模型改为**口令派生**（Bitwarden 式），并补齐云同步的账号 / 使用体验。
Windows(WPF) 与 macOS(AppKit) 同版本同步发布：本条目记 Windows 侧，macOS 侧见
[`releases/macos-v0.5.0/CHANGELOG.md`](releases/macos-v0.5.0/CHANGELOG.md)（同一功能、AppKit 界面）。

### 架构
- **VMK 加密改为口令派生**（共享层）：主密钥（VMK）改由 `PBKDF2-SHA256(主口令, salt, 600k 迭代)` 派生的 KEK 用 AES-256-GCM 包装。
  另一台设备**只需邮箱 + 主口令**即可解锁，不再需要「设备批准」或 Recovery Key 走通。
  Recovery Key 回归本职——只在忘记主口令时恢复访问权。
- **客户端派生认证密钥**：登录 / 注册 / 改口令时发给 AppsCloud 的是 `PBKDF2-SHA256(主口令, "RemoteFlow/auth/v1:"+邮箱, 600k)`（Base64），
  服务端再 hash 一层——**服务端全程拿不到主口令明文**。
- 统一信封类型 `VaultKeyEnvelope`（`Kind` = password / recovery），移除设备 ECDH 密钥对与 `DeviceKeyService`。
- **设备批准降为可选**：账号可开「新设备需批准」开关（默认关）——开启后，新设备即使主口令正确也要一台已登录设备批准才能同步。
- 依赖 AppsCloud 后端 G3.2（口令 Vault + `POST /api/v1/auth/change-password` + `DeviceVaultAccess`）。服务端 migration `PasswordVault`；旧「设备信封」数据不可迁移，需重新初始化 Vault。

### 新增
- 设置页「云同步」：**注册入口**（登录 / 注册模式切换，注册时二次确认口令 + ≥12 位提示 + 可选「新设备需批准」）。
- **更改主口令**入口（改认证密钥 + 重新包装口令信封，其它设备需用新口令重新登录）。
- **重置 Recovery Key** 入口。
- Recovery Key 一次性展示：**「复制」/「另存为文件」**按钮，且必须先复制或另存才能点「我已妥善保存」。
- 「补输主口令」卡片：本机已登录但无缓存 VMK 时，输入主口令解锁（旁附 Recovery Key 恢复）。
- 记住上次登录邮箱（`AppSettings.CloudEmail`）。
- 手动同步后显示条数摘要（「上传 N · 下载 M」）。
- 服务地址字段默认只读，**左键三击进入可编辑**（无界面提示，属刻意隐藏的高级操作）。

### 变更
- 登录不再「首次自动注册」——改为明确的注册入口；登录失败按凭据错误提示，引导去注册。
- `CloudSyncViewModel.Describe`：补 `CryptographicException`（主口令 / Recovery Key 不正确）等分类。

### 修复
- **同步循环卡在「同步中」**：`SyncCoordinator.RunOnceAsync` 加兜底 `catch` + `finally`，任何异常都会把状态落到 Error / Offline 并可重试，不再永久停在 Syncing。
- **「刷新后看到好多冲突」**：首次同步（游标 0 且无待推变更）先拉一轮，认领服务端既有实体的版本 / 内容哈希，避免对账把本地既有条目全部当「新建」推上去逐条撞冲突。已有真实待推变更时不做这步，走正常 Push→冲突。

### 未包含
- 多重身份验证（MFA）—— 后续版本。
- 首次同步的对象统计确认对话框（「将同步 N 条」）。
- Key Rotation（后端 G3.2）客户端接入。

### 验证
- 构建：`dotnet build RemoteFlow.slnx -c Release` 0 错误。
- 测试：`dotnet test -c Release` —— IntegrationTests 154、Core.Tests 42、IntegrationTests.Windows 9 全绿；IntegrationTests.Mac 在 Windows 上 skip。
- 端到端：`CloudRoundTripTests` / `CloudSyncFacadeTests` 对**已上线的 `https://sync.appscloud.cn/`**（AppsCloud 0.11）跑通口令 / 批准 / Recovery Key 三条解锁路径 + 干净设备只用邮箱 + 口令恢复全部数据。
- publish：`dotnet publish src/RemoteFlow.App -c Release -r win-x64` 单文件启动正常（运行 12s 无异常退出）。
- 未执行：完整 UI 手动走查、实机 RDP / SSH / VNC 会话、多台真实设备的同步 / 冲突 / 恢复演练。

详见 [`releases/v0.11.0/CHANGELOG.md`](releases/v0.11.0/CHANGELOG.md)。

## [0.10.0] — 2026-09-09

云同步（Cloud Sync）落地。Windows(WPF) 与 macOS(AppKit) 同一版本同步发布：
本条目记 Windows 侧，macOS 侧见 [`releases/macos-v0.4.0/CHANGELOG.md`](releases/macos-v0.4.0/CHANGELOG.md)（同一功能、AppKit 界面）。

### 架构
- 新增端到端加密同步栈（共享层，双端共用）：
  - `RemoteFlow.Core.Cloud` —— 契约、`RecoveryKey`（RFC 4648 Base32）、`CloudEndpoint` / `CloudSyncGate` / `CloudSignInException`。
  - `RemoteFlow.Infrastructure.Security` —— `VaultCryptography`（ECDH-P256 + HKDF-SHA256 + AES-256-GCM）、设备信封 / Recovery 信封 / 载荷加密，`VaultMasterKeyService`（解锁 / 初始化 / 恢复 / 批准设备）。
  - `RemoteFlow.Infrastructure.Sync` —— `AppsCloudClient`（REST + Token 生命周期）、`SqliteSyncStore`（Outbox + 游标 + 冲突 + 内容哈希）、`SyncCoordinator`（Reconcile → Push → Pull）、`ConflictService`（检测非合并：保留本机 / 使用云端）、5 个实体源（连接 / 分组 / 标签 / 凭据元数据 / 凭据 Secret）、`OutboxSyncChangeTracker`、`CloudSyncAutoRunner`（后台周期同步）。
  - `CloudSyncService` —— UI 唯一入口门面；`CloudSyncViewModel` —— 面板状态机（共享）。
- 本地 SQLite 迁移 `user_version` 4 → 5：4 张同步表 + `sync_entity_state.content_hash`。向后兼容，旧库自动升级。
- 依赖后端 AppsCloud（独立仓库）的 G3 E2EE Vault + G4 Sync 服务；服务端只搬运密文，看不到任何明文 / VMK / Recovery Key。

### 新增
- 设置页新增「云同步」分页：登录 AppsCloud、初始化 / 恢复加密 Vault、手动同步、批准新设备、解决冲突、退出与清除本机云数据；一次性 Recovery Key 横幅。
- 默认服务地址 `https://sync.appscloud.cn/`，面板中默认锁定；连自建 / 内网 AppsCloud 时勾选「使用自定义服务地址」解锁。
- 首次登录若邮箱在 AppsCloud 尚无账号，自动创建后登录（设计文档「注册 / 登录」为同一步）；账号已存在但密码不符按凭据错误处理，绝不覆盖。
- 本地优先不变：未登录 / 云端不可达时连接、会话、Secret 读取均正常，改动在 Outbox 累积。

### 变更
- 登录 / 同步失败的提示改为人读中文：`AppsCloudClient` 解析 RFC 7807（字段校验 / detail / title），`CloudSyncViewModel.Describe` 按类型与状态码分类（401 / 403 / 409 / 5xx / 网络 / 超时）。
- 设置页（WPF + AppKit）首次显示时拉取一次已恢复的云会话状态。

### 修复
- `CloudSyncAutoRunner.Dispose` 幂等：`_disposed` 守卫 + 吞 `ObjectDisposedException`，避免 `App.OnExit` 与容器 `DisposeAsync` 双重释放崩溃。

### 未包含
- 首次同步的对象统计确认对话框（「将同步 N 条」）—— 后续版本。
- `AppSettings` 的 User / Device / Session 作用域区分 —— 后续版本。
- Key Rotation（后端 G3.2）客户端接入。

### 验证
- 构建：`dotnet build RemoteFlow.slnx -c Release` 0 错误。
- 测试：`dotnet test -c Release` —— IntegrationTests 154、Core.Tests 42、IntegrationTests.Windows 9 全绿（含对真机 AppsCloud 测试环境的 `CloudRoundTripTests` / `CloudSyncFacadeTests` 端到端）；IntegrationTests.Mac 10 项在 Windows 上 skip。
- CI（push `main`）：ubuntu / windows / **macos** 三 job 全绿 —— AppKit「云同步」分页在 macOS 编译通过。
- publish：`dotnet publish src/RemoteFlow.App -c Release -r win-x64` 单文件启动正常（运行 12s 无异常退出）。
- 未执行：完整 UI 手动走查、实机 RDP / SSH / VNC 会话、多台真实设备的同步 / 冲突 / 恢复演练；`sync.appscloud.cn` 正式部署尚未上线（端到端验证跑在测试环境）。

详见 [`releases/v0.10.0/CHANGELOG.md`](releases/v0.10.0/CHANGELOG.md)。

## [0.9.0] — 2026-09-09

Windows 平台版本。macOS 原生版走独立的 `macos-v*` 版本线，不在本次范围内。

### 架构
- 抽出平台无关的 `RemoteFlow.Presentation` 工程：ViewModel 与视图无关服务（格式化、探测、对话框抽象、终端资产）下沉，Windows(WPF) 与 macOS(AppKit) 双端共用。
- 新增 GitHub Actions：每次 push / PR 在 Ubuntu 跑共享测试、Windows / macOS 各验构建与平台专属测试；打 `v*` tag 自动构建 win-x64 单文件并挂 GitHub Release 草稿。

### 新增
- 会话状态灯：连接列表、首页最近连接、收藏行的图标右下角，连接中琥珀、已连接绿，空闲不显示。
- 悬停就地连接：「我的连接」列表行、首页收藏行悬停浮出「连接」按钮；首页最近连接大卡悬停显「连接 →」提示。
- 详情面板「协议」字段改用协议色徽章，与列表 / 首页统一。
- SSH 终端启用 xterm.js WebGL 渲染器（共享层带入）。
- VNC 画质旋钮：内网高画质 + 低压缩（共享层带入）。

### 变更
- 全屏胶囊工具条默认停留 2.5s → 3s；常驻条 / 全屏药丸状态入口只有状态图标可点，主机 IP 不再是点击热区。
- 首页问候区收紧（无会话时隐藏「0 个会话已连接」、间距与卡片高度下调）；凭据列表「保险库」列瘦身、孤儿凭据警示色。
- 分组：老用户升级也新建「我的设备」，不再借用现有分组顶默认。
- 设置页「首页时间显示」5 控件（显示时间 / 秒 / 星期 / 周数 / 顺序）合并为「首页时间行」一个下拉即预览（只日期 / 日期+时间 / 日期+星期+时间 / 完整）。`AppSettings` 相应字段收敛为 `HomeDateLine`，老配置缺字段回退默认、向后兼容。

### 修复
- 多会话全屏下切换会话后胶囊工具条丢失；关闭会话后回到常驻条（切换会话保持全屏）。
- 双击常驻条状态入口会把窗口最小化。
- Windows VNC 会话看不到远端光标（共享层无条件挂 `CursorHandler` 后的回归）——WPF 侧改为按热点把光标套成画面指针。
- SSH 中文输入法快速输入掉字（keyCode=229）+ 出向批量泵（共享层带入）。
- 稳定性打磨：堵住 async void 崩溃口、错误提示说人话（共享层带入）。

### 跨平台协调项
- 「首页时间显示」合并（上）从共享 `HomePageViewModel` 删除了 `ClockLine` / `HasClockLine`；macOS 的 `DetailView.cs` 消费这两个成员，**本版发布后 macOS CI 构建会失败，直到 macOS 平台同步改视图**。Windows 版本不受影响。

详见 [`releases/v0.9.0/CHANGELOG.md`](releases/v0.9.0/CHANGELOG.md)。

## [0.8.1] — 2026-09-07

### 修复
- 会话全屏在“非标准最大化”下溢出：从最大化窗口进全屏时，无边框窗口残留 WindowChrome 的约 8px 溢出矩形——上沿被顶出屏幕、底部露出本机任务栏，且顶沿胶囊工具条无法唤出。改为按物理像素定位到当前显示器完整边界（`SetWindowPos` → `rcMonitor`），并在被 WPF 重排时 snap 回；顶沿唤出判定带一并放宽做兜底。

详见 [`releases/v0.8.1/CHANGELOG.md`](releases/v0.8.1/CHANGELOG.md)。

## [0.8.0] — 2026-09-06

### 新增 / 变更
- 会话常驻条与全屏药丸新增统一的连接状态入口和连接质量详情；RDP、SSH、VNC 均可查看连接状态、会话时长、重连次数与质量指标，并可重新检测。
- RDP 工具条新增“启动任务管理器”，通过 mstsc ActiveX 官方远端语义动作执行，并保留协议级 `Ctrl+Shift+Esc` 回退。

### 修复
- 发送 RDP 安全组合键或启动远端任务管理器前，自动恢复宿主窗口与 ActiveX 输入焦点，避免工具条夺焦后动作无响应、误作用于本机或退出应用全屏。

详见 [`releases/v0.8.0/CHANGELOG.md`](releases/v0.8.0/CHANGELOG.md)。

## [0.7.0] — 2026-09-06

### 新增 / 变更
- 协议功能增强：RDP 高级设置收口（显示/本地资源/连接与安全/体验 Section、分辨率预设、多显示器↔启动全屏联动、麦克风与连接质量真实映射）；SSH 终端主题与实时预览、粘贴安全、清屏、搜索输出；VNC 剪贴板远端→本机接收。
- 托盘右键结构化菜单：打开/隐藏、新建连接（预选协议）、最近连接、活动会话 + 断开全部、设置、开机启动（双向同步）、正式退出。
- 会话 Tab「关闭右侧会话」。

详见 [`releases/v0.7.0/CHANGELOG.md`](releases/v0.7.0/CHANGELOG.md)。

## [0.6.0] — 2026-09-06

### 新增 / 变更
- Session 生命周期统一与资源清理（RemoteSessionBase/Tracker、统一关闭编排、RDP/SSH/VNC 清理加固、WebView2 共享环境、有序退出与崩溃标记）。
- 会话实时状态同步（SessionManager 唯一状态源、聚合通知、首页/列表/详情/托盘实时、右键 连接/切换/断开）。
- 首页时间显示与顺序预设、已连接口径、托盘与会话切换、右键菜单分组、复制自动命名、统一「测试连接」对话框。

详见 [`releases/v0.6.0/CHANGELOG.md`](releases/v0.6.0/CHANGELOG.md)。

## [0.5.0] — 2026-09-05

### 新增 / 变更
- 默认分组保护与规则（分组 is_default/is_protected、schema v3、删除默认选新默认、设为默认分组、删光不复活）。
- VNC 输入发送链路修复与会话三档缩放。

详见 [`releases/v0.5.0/CHANGELOG.md`](releases/v0.5.0/CHANGELOG.md)。

## [0.4.0] — 2026-09-05

### 新增 / 变更
- 统一连接链路与首页收藏连接；我的连接多选批量；凭据批量与筛选；右侧详情分区完善；首页密度与统计；图标全应用统一；导航键盘可达性。

详见 [`releases/v0.4.0/CHANGELOG.md`](releases/v0.4.0/CHANGELOG.md)。

## [0.3.2] — 2026-09-05

### 修复
- 单文件发布包缺失终端资源导致 SSH 无法连接——SSH 终端资源（xterm.js 前端）
  现随 exe 内嵌并在运行时释放，不再依赖 exe 旁存在 `Assets/Terminal/`。
  已实机验证。

详见 [`releases/v0.3.2/CHANGELOG.md`](releases/v0.3.2/CHANGELOG.md)。

## [0.3.1] — 2026-09-05

### 修复
- SSH 主机密钥信任弹窗仍会被残留鼠标输入瞬间关闭——无边框窗口的拖动处理器
  未被输入宽限期覆盖，现已一并纳入，并为 `DragMove()` 增加防御性异常处理。
  已实机验证。

详见 [`releases/v0.3.1/CHANGELOG.md`](releases/v0.3.1/CHANGELOG.md)。

## [0.1.1] — 2026-09-04

### 修复
- SSH 主机密钥确认对话框被握手超时吞掉——弹窗改到握手中止后的异步流程里，
  用户接受则记录指纹并自动重试；指纹变化仍是强警告。
- 连接 CSV 导入不去重——以名称 + 主机 + 端口 + 协议为身份，重复导入不再产生副本。

详见 [`releases/v0.1.1/CHANGELOG.md`](releases/v0.1.1/CHANGELOG.md)。

## [0.1.0] — 2026-09-04

首个正式版本（Unified Connection MVP）。先把「连接」这件事做扎实。

> 开发期间曾迭代打过内部标签（v0.1.0 早版、v0.1.1），均未推送、未对外分发，
> 已合并为单一的正式首版 v0.1.0。

### 新增
- 连接资产管理：CRUD、多级分组、跨分组标签、收藏、连接历史。
- 全局搜索：按名称 / Host/IP / 分组 / 标签 / 备注即时过滤，相关度排序。
- 连接列表行 / 凭据列表行 / 会话 Tab 均有右键菜单。
- 多协议会话：RDP（系统 ActiveX）、SSH（SSH.NET + xterm.js/WebView2）、
  VNC（纯托管 RFB）；统一 Tab 外壳、多会话并行、切 Tab 不断开、断线内部重连。
- 会话工具条：非全屏为顶部常驻长条，全屏为可拖动 / 可固定 / 自动隐藏的悬浮药丸
  （独立 Popup，盖在 RDP 原生画面之上，贴齐屏幕上边缘，首次进全屏给一次性提示）。
- 会话全屏：按显示器完整边界铺满、无溢出；`F11` / `Esc` 经低级键盘钩子响应。
- Credential Vault：密码 / 私钥经 Windows DPAPI 加密单独存储，连接库只存引用键；
  凭据编辑框与备份口令框均可「眼睛」显隐，明文不进 ViewModel。
- 连接 CSV 导入 / 导出（不含 Secret）。
- 凭据加密备份 `.rfbackup`：口令加密的凭据导入 / 导出，AES-256-GCM + PBKDF2 600k 迭代；
  明文密钥只在内存、绝不落盘；导入按名称跳过已存在。
- 启动 DB 自愈：残留的 `-wal` / `-shm` 与主库不一致且无人占用时自动清理重开。
- 页面数据加载失败改为顶部可重试横幅，不再静默。
- 深浅双主题、系统托盘、响应式窗口、单实例、i18n 基础设施。

### 已知问题
- RDP 证书对话框是系统 `mstsc` 的，「不再询问」在 Windows 层持久化。
- 单文件 exe 的 SSH 终端需同目录 `Assets/Terminal/`，用 `.zip` 包。
- SSH / VNC 未做真实设备深度联调，人工测试清单已随快照归档。

详见 [`releases/v0.1.0/CHANGELOG.md`](releases/v0.1.0/CHANGELOG.md)。

[0.12.0]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.12.0
[0.11.3]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.11.3
[0.11.2]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.11.2
[0.11.1]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.11.1
[0.11.0]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.11.0
[0.10.0]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.10.0
[0.9.0]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.9.0
[0.8.1]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.8.1
[0.8.0]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.8.0
[0.3.1]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.3.1
[0.3.2]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.3.2
[0.3.1]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.3.1
[0.1.1]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.1.1
[0.1.0]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.1.0
