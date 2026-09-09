# RemoteFlow macOS v0.4.0

发布日期：2026-09-09 · 上一版本 macos-v0.3.0

本版落地**云同步（Cloud Sync）**：连接、分组、标签、凭据元数据与密码经端到端加密
后同步到自建的 AppsCloud 服务，多台设备之间保持一致。服务端只存密文，看不到任何
明文、主密钥或 Recovery Key。与 Windows v0.10.0 同一功能、同一版本一起发布。

> macOS 与 Windows 是相互独立的版本线，本版本号（0.4.0）不与 Windows 版（0.10.0）对齐。
> 云同步的加密栈与同步引擎是双端共享的 `RemoteFlow.Core` / `RemoteFlow.Infrastructure`，
> 只有设置面板是各自的原生实现。

无破坏性变更，直接覆盖安装即可。已有连接、凭据、历史不受影响；不登录云账号也能完整使用。

---

## 安装

1. 下载对应架构的 DMG（Apple Silicon 选 `arm64`，Intel 选 `x64`），打开后把
   `RemoteFlow.app` 拖进「应用程序」。
2. **首次启动**：本版为 ad-hoc 签名、未做 Apple 公证，直接双击会被 Gatekeeper 拦。
   在「应用程序」里 **右键 RemoteFlow →「打开」**，弹窗再点一次「打开」即可；
   或在终端执行一次：
   ```
   xattr -dr com.apple.quarantine /Applications/RemoteFlow.app
   ```
3. 之后正常双击启动。数据存放在 `~/Library/Application Support/RemoteFlow/`，
   升级不影响既有连接、凭据、历史。

---

## 变更详情

### 云同步（Added）

**设置 → 云同步** 分页，按状态分区：

| 状态 | 能做什么 |
| --- | --- |
| 未登录 | 填邮箱 + 密码登录 AppsCloud。默认服务地址 `https://sync.appscloud.cn/`、默认锁定，连自建 / 内网实例时勾选「使用自定义服务地址」解锁。 |
| 需初始化 Vault | 本账号第一台设备：本机生成加密主密钥（VMK）与 Recovery Key（只显示一次，务必抄下）。 |
| 需要授权 | 已有 Vault、本设备未授权：在另一台已登录设备上批准，或输入 Recovery Key 恢复。 |
| 已就绪 | 查看同步状态 / 待上传数、立即同步 / 刷新、批准新设备、解决冲突、退出或清除本机云数据。 |

- **首次登录自动开户**：邮箱在 AppsCloud 尚无账号时自动创建后登录（设计文档「注册 / 登录」为同一步）；
  账号已存在但密码不符按凭据错误处理，绝不覆盖既有账号。
- **本地优先**：未登录 / 云端不可达时，连接、RDP / SSH / VNC 会话、密码读取全部正常，
  改动在本地 Outbox 累积，恢复连接后由后台补传。
- 密码 / 私钥经 macOS 钥匙串保管，同步时用设备密钥 + Recovery Key 双通道封装的 VMK 派生实体密钥
  加密（AES-256-GCM，AAD 绑定 UserId | AppId | 实体类型 | 实体 Id | Schema | KeyVersion），
  服务端只拿到密文。

### 加密与同步引擎（Architecture，双端共享）

- `VaultCryptography`：ECDH-P256 + HKDF-SHA256 + AES-256-GCM；设备信封（临时 ECDH）、
  Recovery 信封（Recovery Key = 256-bit CSPRNG，RFC 4648 Base32）、载荷加密。
- `SqliteSyncStore` + `SyncCoordinator`：Outbox + 每流单调游标、乐观版本、`SELECT…FOR UPDATE`
  由服务端分配 Revision、逐操作幂等（OperationId）、内容哈希对账作为崩溃兜底。
- `ConflictService`：检测而非合并 —— 冲突时让用户选「保留本机」或「使用云端」。
- 5 个实体源：连接 / 分组（排除系统组与「未分组」）/ 标签 / 凭据元数据 / 凭据 Secret。
- `CloudSyncAutoRunner`：启动恢复会话 + Vault 解锁后每 3 分钟跑一次同步循环。
- 本地 SQLite `user_version` 4 → 5：4 张同步表 + `sync_entity_state.content_hash`，旧库自动升级。

### 组合根

- `AppDelegate` 注册 `AddCloudSync()`，种子标签之后启动 `CloudSyncAutoRunner`；退出路径 `Dispose`。
- `SettingsPageViewModel` 增加共享的 `CloudSyncViewModel` 子面板；`SettingsPaneView` 增加「云同步」分页，
  用最小绑定辅助 `Binder`（属性变更切主线程）把 VM 绑到 AppKit 控件。

### 其它

- 设置面板首次显示时拉取一次已恢复的云会话状态。
- 登录 / 同步失败的提示改为人读中文（解析 RFC 7807，按状态码与异常类型分类）。

---

## 已知问题

- **macOS 云同步面板尚未真机走查**：加密栈与同步引擎有对真实 AppsCloud 测试环境的
  端到端集成测试覆盖（`CloudRoundTripTests` / `CloudSyncFacadeTests`，在 Linux / Windows CI 上跑），
  AppKit「云同步」分页本身在 macOS CI 上**编译通过**，但**界面交互、绑定刷新、明暗主题下的排版
  未在真机点验**。首个可用基线，后续版本据实机反馈打磨。
- `sync.appscloud.cn` 的 AppsCloud 正式部署尚未上线；端到端验证跑在测试环境。
- **架构**：提供 arm64（Apple Silicon）与 x64（Intel）两个独立 DMG，不提供 universal 融合二进制。
- **签名**：ad-hoc 签名、未公证，安装见上方说明。
- 首次同步的对象统计确认对话框（「将同步 N 条」）尚未实现，后续版本补。

---

## 验证

- **构建**：CI（push `main`）三 job —— shared（Ubuntu）/ windows / **macos** 全绿；
  macos job `dotnet build src/RemoteFlow.App.Mac -c Debug -r osx-arm64` 成功。
- **测试**：`RemoteFlow.Core.Tests` 42、`RemoteFlow.IntegrationTests` 154（含对真机 AppsCloud
  测试环境的 `CloudRoundTripTests` / `CloudSyncFacadeTests` 端到端：设备 A 登录 → 初始化 Vault →
  造连接 / 凭据 / 密码 → push；干净设备 B 登录 → Recovery Key → pull → 解密 → 解析出可连接的密码）、
  `IntegrationTests.Windows` 9 全绿；`IntegrationTests.Mac` 10 项（`MacOnlyFact`）在非 macOS 上 skip。
- **实机**：本版**未做 macOS 实机走查**（见「已知问题」）。上一版的 RDP / SSH / VNC 手感改进不在本版范围，未回归。
- **平台**：macOS 13 及以上 · **架构**：arm64 + x64（分别打包）

---

## Git

- Tag：`macos-v0.4.0`
- 提交范围：`macos-v0.3.0..`（云同步 11 个提交 `9f2d995 … 57078fa` + 默认地址 / 版本号 / 快照封版提交）
- 共享层改动同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.10.0]`）。

## 产物

- `RemoteFlow-v0.4.0-macos-arm64.dmg` —— Apple Silicon，ad-hoc 签名
- `RemoteFlow-v0.4.0-macos-x64.dmg` —— Intel，ad-hoc 签名
