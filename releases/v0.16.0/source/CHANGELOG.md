# 更新日志

本文件记录 RemoteFlow 的版本变更。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

每个正式版本的完整变更详情见 `releases/v<X.Y.Z>/CHANGELOG.md`。

---

## [0.16.0] — 2026-09-14

全屏悬浮工具条交互更新：顶沿唤出改为悬停判定防误弹，显示 / 消失延迟按全屏档位可配置。
本版本仅发布 Windows；macOS 未改动、未发布。

### 新增
- **顶沿悬停唤出**：全屏下鼠标扫过屏幕顶沿不再立即弹出工具条——光标需在顶沿带持续悬停
  （默认 1.5 秒，可配）才唤出；悬停期间单击远端画面会取消唤出，避免点完远端顶部 UI 后
  工具条过一会儿突然弹出挡住刚点的地方。
- **显示 / 消失延迟按档位可配**（设置 → 常规 → 全屏悬浮工具条）：窗口最大化档与完全全屏档
  各配一对延迟，下拉含「立即 / 0.5 / 1 / 1.5 / 2 / 3 / 5 秒」；显示默认 1.5 秒、消失默认 0.9 秒
  （与旧版一致），改即生效。「立即」即恢复旧版行为。

### 变更
- **顶沿唤出从即时改为需悬停**（默认 1.5 秒）。原「移到顶沿立即显示」为刻意设计变更：
  全屏 RDP 下鼠标去点远端浏览器标签栏 / 应用菜单时经常扫过本机顶沿，工具条会跳出来挡一下。

## [0.15.0] — 2026-09-12

会话窗口行为更新：三档全屏与关闭窗口的会话确认；另含上一版未发布的 Windows 右键菜单修复。
本版本仅发布 Windows；macOS 未改动、未发布。

### 新增
- **会话三档视图模式**：会话画面由「常规 / 完全全屏」两态拆成「常规 / 窗口最大化 / 完全全屏」，对齐 macOS 的模型。窗口最大化档保留标题栏与窗口边框，只折起左导航、Tab 栏、详情面板与状态栏。
- **F11 逐档循环**：常规 → 窗口最大化 → 完全全屏 → 常规。
- **常驻工具条新增「窗口最大化」与直达「完全全屏」两颗按钮**：此前只有逐档入口，没法一下进全屏。
- **悬浮药丸新增「完全全屏」开关**：图标随档位双态、从不禁用与隐藏，使窗口最大化档也保留会话切换入口。
- **关闭主窗口的会话确认**：还开着会话时弹「退出 / 最小化到托盘 / 取消」，给出保住会话的出路；「退出」走警示色且默认焦点在「取消」，回车不会误退。

### 变更
- **药丸「退出全屏」改为一路退到底回常规**：此前它与「完全全屏」开关在完全全屏档都回到窗口最大化，两颗按钮行为重复。
- **Esc 不再被全屏热键拦截**：恢复送达远端会话（此前在非常规档被吞掉用于退出全屏，既易误触，又让远端收不到 Esc）。
- **离开会话语境统一回常规档**：含用户在窗口最大化档手动还原窗口。
- **托盘菜单「退出」保持直接退出**，不再二次追问。

### 修复
- **F11 连按吞档、漏键后永久失效**：去抖由固定时间窗改为按键跃迁去重，修掉连按两次被吞一档，以及漏收 KeyUp 后 F11 永久失效。
- **连接的「移动到分组」恢复完整二级菜单**：菜单显示全部分组及「未分组」，当前所属分组不可选；动态菜单项始终绑定触发的连接，刷新后连续移动不再使用陈旧行对象。
- **全局 WPF `MenuItem` 子菜单可见且可展开**：自定义模板补齐 `Popup`、`IsSubmenuOpen` 绑定和二级菜单箭头，修正了有子项却无法显示或展开的根因。
- **右键操作始终指向触发项**：连接、凭据、分组和会话 Tab 右键时同步当前目标；会话 Tab 的「关闭其他会话」与「关闭右侧会话」按实际会话位置正确启用或禁用。
- **分组操作更准确**：从分组菜单新建连接时预选该分组；「未分组」不再提供分组管理菜单。
- **托盘恢复窗口遵循 WPF 生命周期**：先恢复隐藏窗口，再处理最小化状态，避免直接调用 Win32 恢复隐藏窗口导致的黑屏。
- **集成测试稳定性**：串行执行含 SQLite 全局连接池清理的测试，避免并行 teardown 关闭其它测试正在使用的连接。

### 验证
- Release 测试：Core.Tests 42 通过；IntegrationTests 197 通过、5 跳过；IntegrationTests.Windows 12 通过；IntegrationTests.Mac 10 跳过（Windows 上不可运行）；无失败。
- 实机走查 publish 产物：能起、主界面与左侧导航正常、能进设置页、点标题栏「关闭」干净退出（exit code 0），退出后无 `remoteflow.db-wal/-shm` 残留。
- 本次新增的会话三档全屏与关窗确认已实机逐项验证：F11 三档循环、药丸「完全全屏」与「退出全屏」、关窗提示三个分支、托盘退出不追问。

详见 [`releases/v0.15.0/CHANGELOG.md`](releases/v0.15.0/CHANGELOG.md)。

## [0.14.1] — 2026-09-10

系统信息采集修正。Windows 与 macOS 同步发布，macOS 侧见
[`releases/macos-v0.8.1/CHANGELOG.md`](releases/macos-v0.8.1/CHANGELOG.md)。

### 修复
- **后台「系统」列显示不准**（如「Windows 25H2 (X64)」）：
  - 版本族与 SKU 改用 `EditionID` 判定（不随系统语言变）+ 构建号 ≥22000 判 Windows 11
    → 「Windows 11 专业版」（Win11 的 `ProductName` 常误写 Windows 10 Pro）；
  - `UBR` 是 `REG_DWORD`，此前读到 null 导致构建号缺修订 → 「25H2 (26200.9445)」；
  - 架构由枚举 `X64` 规范化成 `x64`；macOS 取 `kern.osproductversion`，Linux 取 `/etc/os-release`。
  - 管理台展示改为「名称 版本 · 架构」。

### 验证
- Build：`dotnet build RemoteFlow.slnx -c Release` 0 错误；Tests：Core.Tests 42、
  IntegrationTests 179、IntegrationTests.Windows 9。端到端真实上报核验，服务端存储为
  `Windows 11 专业版 / 25H2 (26200.9445) / x64`。

详见 [`releases/v0.14.1/CHANGELOG.md`](releases/v0.14.1/CHANGELOG.md)。

## [0.14.0] — 2026-09-10

首次加入自动消重 + 凭据重复推送修复。macOS 侧对应版本 `macos-v0.8.0` **因 GitHub Actions
账户计费故障未构建、未发布**，功能由 `macos-v0.8.1` 覆盖
（见 [`releases/macos-v0.8.0/CHANGELOG.md`](releases/macos-v0.8.0/CHANGELOG.md)）。

### 新增
- **首次加入按自然键认领云端 Id**：两台机器在启用云同步之前各自建过同一台服务器 / 分组 / 标签 /
  凭据 → 两条不同 Id 的重复条目。现在首次同步（云端全量已落地、对账之前）自动合并：
  - 自然键：连接 = host + 端口 + 协议 + 名称；凭据 = 名称 + 类型 + 账号 + 域；分组 / 标签 = 名称；
  - 以云端那份为准：改写子引用（连接的 group/credential、标签关联、分组父子）指向云端 Id，
    删除本机重复行及其同步状态与待推条目；凭据旧密钥引用提交后从平台密钥库清理；
  - 单事务、顺序 分组/标签 → 凭据 → 连接；**本机独有条目不受影响**。

### 修复
- **凭据版本每次多涨 1**：落地密码 / 私钥时刷新了凭据 `UpdateAt` → 内容哈希立刻失效 →
  对账再推一次。改为定点更新引用（`SetSecretReferencesAsync`），不触碰 `updated_at`。

### 验证
- `dotnet build RemoteFlow.slnx -c Release` 0 错误；`dotnet test -c Release` —— Core.Tests 42、
  IntegrationTests 179、IntegrationTests.Windows 9 全绿。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。

详见 [`releases/v0.14.0/CHANGELOG.md`](releases/v0.14.0/CHANGELOG.md)。

## [0.13.5] — 2026-09-10

同步状态自愈（v0.13.4 计数修正的补完）。macOS 侧见
[`releases/macos-v0.7.5/CHANGELOG.md`](releases/macos-v0.7.5/CHANGELOG.md)。

### 修复
- **删除过的条目仍被计入「云端已存」**：v0.13.4 只对此后发生的删除清状态行，修复前已删除实体的
  状态行仍在（标签显示 6 而实际只剩 3）。现在对账会清理「云端有、本地已无」实体的本地同步状态行
  —— 只忘记版本、不产生上行操作，**不传播删除**；统计随下一次对账自愈。

### 验证
- `dotnet build RemoteFlow.slnx -c Release` 0 错误；`dotnet test -c Release` —— Core.Tests 42、
  IntegrationTests 177、IntegrationTests.Windows 9 全绿。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。

详见 [`releases/v0.13.5/CHANGELOG.md`](releases/v0.13.5/CHANGELOG.md)。

## [0.13.4] — 2026-09-10

> 本版曾以 `v0.13.3` / `macos-v0.7.3` 封版（版本号与快照均已提交），随后**整版重切**为
> `v0.13.4` / `macos-v0.7.4`。`v0.13.3` 的 tag 从未推送、没有 Release，是**未发布的中间态**；
> 本条目即该次变更的最终版本。

计数修正 + 危险操作防误触。macOS 侧见
[`releases/macos-v0.7.4/CHANGELOG.md`](releases/macos-v0.7.4/CHANGELOG.md)。

### 修复
- **「云端已存」把已删除的条目也算进去了**：统计取自 `sync_entity_state`（`server_version>0`），
  而删除产生的**墓碑**仍留在表里 —— 例如标签实际只剩 3 个却显示 6。
  现在「删除推送成功」与「墓碑落地」都会清掉该实体的状态行，统计只反映仍然存在的条目
  （连接 / 凭据 / 密码 / 分组 / 标签）。

### 变更（防误触）
- **两个清除按钮移入独立的「危险操作」卡片并默认折叠**，需先在输入框手动键入「清除」
  才启用按钮（双端一致）；卡片内分别写清「保留本地数据」与「以云端为准」的后果。
  账号卡片只保留非破坏性的「退出云账号」。

### 验证
- `dotnet build RemoteFlow.slnx -c Release` 0 错误；`dotnet test -c Release` —— Core.Tests 42、
  IntegrationTests 176、IntegrationTests.Windows 9 全绿（新增：墓碑不计入已同步条目）。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。

详见 [`releases/v0.13.4/CHANGELOG.md`](releases/v0.13.4/CHANGELOG.md)。

## [0.13.2] — 2026-09-10

修「永远显示已同步、却什么都不同步」与冲突看不懂。Windows 与 macOS 同步发布，macOS 侧见
[`releases/macos-v0.7.2/CHANGELOG.md`](releases/macos-v0.7.2/CHANGELOG.md)。

### 修复
- **客户端卡在假「已同步」、两边数据不再互通**：服务端账号被清空 / Vault 重新初始化后
  `VaultId` 变化，而客户端仍保留旧 Vault 的游标（如 30）与各实体的 `server_version`
  → 对账认为「都已同步」→ 既不推也不拉。新增 `VaultSwitchDetector`：解锁后比对当前
  `VaultId` 与上次记录，不一致即清空 `sync_state` / `sync_entity_state` / `sync_outbox` /
  `sync_conflict`，下一次同步把本机既有数据作为新 Vault 的**全量内容重新推送**。
  `AppSettings` 新增 `CloudVaultId`。
- **冲突对话框看不出在问谁**：本机已删除的实体（如「(已删除的分组) 9f5fd131-…」）现在改为
  **解密云端那份密文取名称**（`SyncEntityLabeler.DescribePayload`）；并把
  「本机已删除，云端仍存在 —— 保留本机=仍删除；使用云端=恢复它」这类**处置后果写进说明**。

### 验证
- `dotnet build RemoteFlow.slnx -c Release` 0 错误；`dotnet test -c Release` —— Core.Tests 42、
  IntegrationTests 175（+`VaultSwitchDetectorTests` 3、+`SyncEntityLabelerTests` 5）、
  IntegrationTests.Windows 9 全绿。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。

详见 [`releases/v0.13.2/CHANGELOG.md`](releases/v0.13.2/CHANGELOG.md)。

## [0.13.1] — 2026-09-10

同步删除语义收紧（安全修复）。Windows 与 macOS 同步发布，macOS 侧见
[`releases/macos-v0.7.1/CHANGELOG.md`](releases/macos-v0.7.1/CHANGELOG.md)。

### 修复
- **删除不再从「本地不存在」推断传播**：对账（崩溃兜底）原本会把「云端有、本地已无」自动补成
  `Delete` 推上云端 —— 一次误删（本地文件缺失、数据库被部分还原、程序异常）会被复制到云端
  与所有设备，**不可逆**。
  - 现在对账**不再补 Delete**，只记 Warning 供排查；删除的唯一传播途径是服务层删除时登记的
    Outbox 墓碑（连接 / 凭据 / 分组 / 标签的删除都走这条路），仍受 LWW 与冲突保护。
  - 「删除 vs 他人修改」的冲突依旧不自动裁决，留给用户在客户端选择「保留本机 / 使用云端」。
  - 代价：极小概率下（删除瞬间崩溃、Outbox 未登记）云端会保留该条并在下次同步回到本地 ——
    这比误删被扩散安全得多。

### 验证
- `dotnet build RemoteFlow.slnx -c Release` 0 错误；`dotnet test -c Release` —— Core.Tests 42、
  IntegrationTests 168（含新增：对账不传播未登记的删除 / 显式登记的删除仍传播）、
  IntegrationTests.Windows 9 全绿。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。

详见 [`releases/v0.13.1/CHANGELOG.md`](releases/v0.13.1/CHANGELOG.md)。

## [0.13.0] — 2026-09-10

客户端资产上报 + 冲突可读化 + 关联清理。Windows 与 macOS 同步发布，macOS 侧见
[`releases/macos-v0.7.0/CHANGELOG.md`](releases/macos-v0.7.0/CHANGELOG.md)。

### 新增
- **上报本机基础系统信息（资产信息）**：登录 / 恢复会话后自动上报，
  AppsCloud 管理台「设备」页与「用户详情」可见——主机名、操作系统(名称/版本/架构)、
  CPU(型号/物理核/逻辑核/主频)、内存(总量/可用)、磁盘(卷明细 + 汇总)、主 IPv4、
  当前用户、时区、运行时长、屏幕、客户端版本、.NET 运行时版本。
  采集为尽力而为：任何失败都降级留空，绝不影响登录与同步。
  - Windows 走注册表 + kernel32（`GlobalMemoryStatusEx` / `GetLogicalProcessorInformationEx` / `GetSystemMetrics`）；
    macOS / Linux 走 `sysctl` 与 `/proc`。
- **冲突列表显示可读名称**：不再只显示裸 GUID —— 连接显示「名称（host）」、凭据/分组/标签显示各自名称、
  密码冲突显示「XX 的密码」并标注「两边都改了，需你选择」；实体已删除时明确标注。
- **同步状态新增「云端已存」汇总**：连接 N · 凭据 N · 密码 N · 分组 N · 标签 N。

### 修复
- **删除连接未清理关联记录**：`connection_history`（最近活动）没有外键，删连接时残留孤儿条目。
  改为事务内先删历史再删连接（`connection_tags` 仍由外键级联）。
- **从未上云又删除的记录不再产生假冲突**：创建后、推送前删除 → Outbox 合并为 `Delete(baseVersion=0)`，
  云端没有该实体，旧逻辑会拿它去推并记一条冲突（对话框里出现一个云端根本不存在的东西）。
  现在「Delete 且 baseVersion=0」直接丢弃 Outbox 条目 —— 删除无须传播。

### 验证
- `dotnet build RemoteFlow.slnx -c Release` 0 错误；`dotnet test -c Release` —— Core.Tests 42、
  IntegrationTests 165、IntegrationTests.Windows 9 全绿；IntegrationTests.Mac 在 Windows 上 skip。
- 端到端（对 `https://sync.appscloud.cn/`）：`ViewerParityFixtureTests`（为网页自查页造对照数据）、
  `SystemInfoUploadTests`（登录后上报系统信息，Windows 上断言 CPU/内存/磁盘非空）、
  `CloudSyncFacadeTests` 全通过；服务端 Devices 表核验到真实采集值。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。

详见 [`releases/v0.13.0/CHANGELOG.md`](releases/v0.13.0/CHANGELOG.md)。

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

[0.16.0]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.16.0
[0.15.0]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.15.0
[0.14.1]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.14.1
[0.14.0]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.14.0
[0.13.5]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.13.5
[0.13.4]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.13.4
[0.13.2]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.13.2
[0.13.1]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.13.1
[0.13.0]: https://github.com/enpiaohj/remoteflow/releases/tag/v0.13.0
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
