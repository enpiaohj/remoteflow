# RemoteFlow macOS v0.5.0

发布日期：2026-09-09 · 上一版本 macos-v0.4.0

本版把云同步的**端到端加密模型改为「口令派生」**（Bitwarden 式）：另一台设备只需
**邮箱 + 主口令**即可解锁同步数据，不再必须走「设备批准」或 Recovery Key。Recovery Key
回归本职 —— 只在忘记主口令时恢复访问权。同时补齐云同步的账号与使用体验。
与 Windows v0.11.0 同一功能、同一版本一起发布。

> macOS 与 Windows 是相互独立的版本线，本版本号（0.5.0）不与 Windows 版（0.11.0）对齐。
> 加密栈与同步引擎是双端共享的 `RemoteFlow.Core` / `RemoteFlow.Infrastructure`，
> 只有设置面板是各自的原生实现。

**破坏性变更**：加密模型不兼容 v0.4.0。升级后需在「云同步」里重新初始化 Vault
（旧的「设备信封」数据无法迁移）；服务端 AppsCloud 也需同步升级到 0.11。

---

## 安装

1. 下载对应架构的 DMG（Apple Silicon 选 `arm64`，Intel 选 `x64`），打开后把
   `RemoteFlow.app` 拖进「应用程序」。
2. **首次启动**：本版为 ad-hoc 签名、未做 Apple 公证。在「应用程序」里
   **右键 RemoteFlow →「打开」**，弹窗再点一次「打开」；或执行一次：
   ```
   xattr -dr com.apple.quarantine /Applications/RemoteFlow.app
   ```
3. 数据存放在 `~/Library/Application Support/RemoteFlow/`，升级不影响既有连接、凭据、历史。

---

## 变更详情

### 加密模型改为口令派生（Architecture，双端共享）

- **VMK 由主口令派生**：主密钥（VMK）改由 `PBKDF2-SHA256(主口令, salt, 600k 迭代)` 得到的
  KEK 用 AES-256-GCM 包装。另一台设备**只需邮箱 + 主口令**即可解锁。
- **客户端派生认证密钥**：登录 / 注册 / 改口令时发给 AppsCloud 的是
  `PBKDF2-SHA256(主口令, "RemoteFlow/auth/v1:"+邮箱, 600k)`（Base64），服务端再 hash 一层
  —— **服务端全程拿不到主口令明文**。
- 统一信封类型 `VaultKeyEnvelope`（`Kind` = password / recovery），移除设备 ECDH 密钥对与
  `DeviceKeyService`。
- **设备批准降为可选**：账号可开「新设备需批准」开关（默认关）—— 开启后，新设备即使主口令
  正确也要一台已登录设备批准才能同步。
- 依赖 AppsCloud 后端 G3.2（口令 Vault + `POST /api/v1/auth/change-password` + `DeviceVaultAccess`）。

### 云同步面板（Added / Changed）

**设置 → 云同步**：

| 状态 | 能做什么 |
| --- | --- |
| 未登录 | **登录 / 注册模式切换**。注册时二次确认主口令（≥12 位提示）+ 可选「新设备需批准」。默认服务地址 `https://sync.appscloud.cn/`、默认只读，**左键三击**进入可编辑（无界面提示）。 |
| 需初始化 Vault | 本账号第一台设备：生成加密主密钥与 Recovery Key。Recovery Key 一次性展示，**可「复制」/「另存为文件」**，且必须先复制或另存才能点「我已妥善保存」。 |
| 需补输主口令 | 本机已登录但无缓存主密钥：输入主口令解锁（旁附 Recovery Key 恢复）。 |
| 待批准 | 账号开了「新设备需批准」且本机未获批：在已登录设备上批准后点「我已被批准，重试」；或用 Recovery Key 恢复。 |
| 已就绪 | 同步状态 / 待上传数 / **上次同步条数摘要**、立即同步 / 刷新、**更改主口令**、**重置 Recovery Key**、**「新设备需批准」开关**、批准待批准设备、解决冲突、退出 / 清除本机云数据。 |

- 记住上次登录邮箱。
- 登录不再「首次自动注册」—— 改为明确的注册入口。

### 修复（双端共享）

- **同步循环卡在「同步中」**：`SyncCoordinator.RunOnceAsync` 加兜底 `catch` + `finally`，
  任何异常都会把状态落到 Error / Offline 并可重试。
- **「刷新后看到好多冲突」**：首次同步（游标 0 且无待推变更）先拉一轮认领服务端既有实体的
  版本 / 内容哈希，避免对账把本地既有条目全部当「新建」推上去逐条撞冲突。

---

## 已知问题

- **macOS 云同步面板仍未真机走查**：加密栈与同步引擎有对**已上线的 `https://sync.appscloud.cn/`**
  （AppsCloud 0.11）的端到端集成测试覆盖（`CloudRoundTripTests` / `CloudSyncFacadeTests`，
  在 Linux / Windows CI 上跑），AppKit「云同步」分页本身在 macOS CI 上**编译通过**，
  但**界面交互、绑定刷新、明暗主题下的排版未在真机点验**。
- 多重身份验证（MFA）尚未实现，后续版本补。
- 首次同步的对象统计确认对话框（「将同步 N 条」）尚未实现。
- **架构**：提供 arm64（Apple Silicon）与 x64（Intel）两个独立 DMG，不提供 universal 融合二进制。
- **签名**：ad-hoc 签名、未公证，安装见上方说明。

---

## 验证

- **构建**：CI（push `main`）三 job —— shared（Ubuntu）/ windows / **macos** 全绿；
  macos job `dotnet build src/RemoteFlow.App.Mac` 成功。
- **测试**：`RemoteFlow.Core.Tests` 42、`RemoteFlow.IntegrationTests` 154、
  `IntegrationTests.Windows` 9 全绿；`IntegrationTests.Mac` 在非 macOS 上 skip。
- **端到端**：`CloudRoundTripTests` / `CloudSyncFacadeTests` 对已上线的 `https://sync.appscloud.cn/`
  跑通口令 / 批准 / Recovery Key 三条解锁路径 + 干净设备只用邮箱 + 口令恢复全部数据。
- **实机**：本版**未做 macOS 实机走查**（见「已知问题」）。
- **平台**：macOS 13 及以上 · **架构**：arm64 + x64（分别打包）

---

## Git

- Tag：`macos-v0.5.0`
- 提交范围：`macos-v0.4.0..`（口令派生 vault + 同步修复 + 版本号 / 快照封版）
- 共享层改动同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.11.0]`）。

## 产物

由 `.github/workflows/release-macos.yml` 在 `macos-v0.5.0` tag 推送后构建并上传到 GitHub Release：

- `RemoteFlow-v0.5.0-macos-arm64.dmg` —— Apple Silicon，ad-hoc 签名
  - Size：57,312,134 bytes
  - SHA-256：`0470AB3842E58445514902AE5FABF0BD0D10090C833CA113262C2CF6B2EC9B0C`
- `RemoteFlow-v0.5.0-macos-x64.dmg` —— Intel（arm64 runner 交叉编译），ad-hoc 签名
  - Size：60,011,588 bytes
  - SHA-256：`C6D880A406EA58A290CF49A1ECF48D9CB8E80C863B6359257B866387B0A661CE`
