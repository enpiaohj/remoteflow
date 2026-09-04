# AppsCloud + RemoteFlow Cloud Sync Claude Code 完整开发实施提示词

> **体系：** AppsCloud + RemoteFlow Cloud Sync  
> **基线：** RemoteFlow 产品设计文档 V1.1（2026-09-03）  
> **文档日期：** 2026-09-04  
> **文档状态：** V1.0 / 开发与实施基线  
> **核心原则：** Local-first、全内容端到端加密、服务器零明文、可离线使用、可恢复、可测试、可回滚

## 使用说明

将本文件直接提供给 Claude Code。执行前确保它能读取：

- RemoteFlow 当前仓库。
- RemoteFlow V1.1 产品设计文档。
- 本套 00~07 设计文档。
- AppsCloud 新仓库目录。

---

## 给 Claude Code 的正式任务

你现在要为现有 RemoteFlow 项目建设 AppsCloud V1，并完成 RemoteFlow 第一阶段“全量配置 + Password + SSH Private Key + Passphrase”的端到端加密多端同步。

这不是概念 POC。目标是形成可部署、可测试、可恢复、可升级、可回滚的第一版完整闭环。

### 一、不可破坏的 RemoteFlow 基线

RemoteFlow 当前产品基线为：

```text
C# + .NET 10 LTS + WPF + MVVM
SQLite Local-first
Connection 与 Credential 分离
SecretReference → Windows Credential Manager / DPAPI
RDP + SSH + VNC Provider
Multi-Tab Sessions
```

不要为了云同步重写 RemoteFlow UI、Provider 或本地 Credential 模型。云同步必须作为新增基础设施层接入。

### 二、核心安全约束

1. AppsCloud 不得持有 RemoteFlow 业务解密密钥。
2. Host/IP/Username/Password/SSH Private Key/Notes/Group/Tag 等业务字段全部在客户端加密后上传。
3. 数据库、日志、备份均不得出现业务明文。
4. Account Authentication 与 Vault Decryption 分离。
5. 第一个设备生成随机 256-bit Vault Master Key。
6. 新设备必须：已授权设备批准，或 Recovery Key 恢复。
7. 本地 Secret 继续使用 Windows Credential Manager/DPAPI，不写 SQLite 明文。
8. 不得自研密码算法。优先使用 .NET/OS 成熟密码学 API。
9. Payload 使用 AES-256-GCM；每次随机 Nonce；使用 AAD 绑定 User/App/Entity/Schema/KeyVersion。
10. 不要把 VMK、Recovery Key、Device Private Key 写入日志、配置文件或数据库明文。

### 三、AppsCloud 新仓库

创建独立项目，例如：

```text
D:\AIProjects\AppsCloud\
├─ src/
│  ├─ AppsCloud.Api/
│  ├─ AppsCloud.Domain/
│  ├─ AppsCloud.Application/
│  └─ AppsCloud.Infrastructure/
├─ tests/
├─ deploy/
├─ docs/
└─ AppsCloud.sln
```

使用 Modular Monolith，不要拆微服务。

### 四、后端技术

```text
.NET 10
ASP.NET Core Web API
EF Core
PostgreSQL
Docker / Docker Compose
Nginx
```

不要第一阶段引入 Kubernetes、Kafka、RabbitMQ 或 Redis，除非有明确、可验证的必需理由。

### 五、后端模块

实现：

```text
Identity
Applications
Devices
Vault
Sync
Operations
```

#### Identity

- Register
- Login
- Refresh Token Rotation
- Logout current device
- Logout all
- Password reset
- Current account

Refresh Token 服务器只存 Hash，并实现 revoke/rotate。

#### Devices

- Register device + public key
- List devices
- Pending approval
- Approve device
- Revoke device
- Heartbeat/LastSeen

#### Vault

服务端仅保存：

```text
Vault metadata
DeviceKeyEnvelope
RecoveryEnvelope
KeyVersion
```

任何接口不得上传裸 VMK。

#### Sync

实现：

```text
POST /api/v1/sync/push
GET  /api/v1/sync/pull
```

支持：

- Batch
- Version
- Revision/Cursor
- Tombstone
- Idempotency
- HTTP 409 conflict
- SchemaVersion
- KeyVersion

服务端不做业务 Payload Merge，因为它无法解密。

### 六、RemoteFlow 客户端改造

在不破坏现有架构的前提下新增：

```text
Infrastructure/Sync
Infrastructure/Security
```

至少实现：

```text
AppsCloudClient
SyncCoordinator
SyncOutboxRepository
SyncStateRepository
SyncEntityStateRepository
ConflictService
VaultMasterKeyService
DeviceKeyService
PayloadEncryptionService
RecoveryKeyService
```

### 七、SQLite Migration

增加：

```text
SyncOutbox
SyncState
SyncEntityState
SyncConflict
```

Outbox 必须与业务数据修改处于同一 SQLite Transaction。

Outbox 不得保存 Password/Private Key 明文。

### 八、同步对象

第一阶段完整同步：

```text
Connection
Credential Metadata
Credential Secret
Group
Tag
ConnectionTag
Favorite
Notes
Protocol Settings
User-scoped Settings
SSH Private Key
SSH Passphrase
VNC Password
RDP Password
Gateway/Proxy Credential（如果当前模型存在）
```

不要默认同步：

```text
完整 ConnectionHistory
Session Tab
Crash Dump
Cache
Window Pixel Position
Machine-specific Temp Path
```

### 九、设备和 Vault 流程

#### 首设备

```text
Login
→ Generate Device Key Pair
→ Protect Private Key with DPAPI
→ Generate VMK
→ Generate Recovery Key
→ Create Device Envelope
→ Create Recovery Envelope
→ Bootstrap Vault
```

#### 新设备

```text
Login
→ Register device/public key
→ PendingApproval
→ Existing device approves and wraps VMK for new device
→ New device decrypts VMK
→ Pull
```

备用：使用 Recovery Key 恢复。

### 十、Secret 恢复

PC-B Pull `credential-secret` 后：

```text
Decrypt in memory
→ Store in Windows Credential Manager/DPAPI
→ Generate local SecretReference
→ Save only SecretReference/metadata in SQLite
```

云端 SecretReference 不可直接复用，因为它可能是设备绑定的。

### 十一、同步策略

Local-first：

- 所有 CRUD 先本地成功。
- Cloud 失败不能阻塞 RDP/SSH/VNC。
- Outbox 后台重试。
- Pull Batch 全部成功应用后才推进 Cursor。
- 401 → Refresh Token。
- 409 → Conflict。
- Tombstone 删除。
- OperationId 作为 Idempotency Key。

### 十二、冲突

普通实体出现 409：保留本地和远端版本，提供冲突状态。

Credential Secret 冲突不得静默覆盖。至少允许：

```text
保留本机
使用云端
```

并保留审计元数据，不记录 Secret 本身。

### 十三、UI

设置增加 Cloud Sync 页面：

```text
AppsCloud Account
同步状态
当前设备
已授权设备
待批准设备
Recovery Key
最后同步时间
立即同步
退出登录
```

正常同步不要使用频繁模态弹窗。

### 十四、Ubuntu 部署

在 `deploy/` 提供：

```text
compose.yaml
.env.example
Nginx config
backup script
restore instructions
upgrade script/checklist
```

只暴露 80/443；PostgreSQL 5432 和 API 内部端口不可直接暴露公网。

提供：

```text
/health/live
/health/ready
```

### 十五、测试

后端至少：

- Identity tests
- Refresh rotation/revoke
- Device authorization
- Vault envelope authorization
- Sync version conflict
- Idempotency
- Tombstone
- Authorization isolation UserId/AppId

RemoteFlow 至少：

- Payload encrypt/decrypt
- AAD tamper failure
- Outbox transaction
- Retry
- Pull cursor
- Secret restore to local Vault
- Conflict
- Schema migration

Integration：

- PC-A/模拟客户端 Push → PC-B Pull。
- RDP Password Restore。
- SSH Private Key Restore。

### 十六、安全测试标记

使用唯一值同步后扫描：

```text
RF-SECURITY-TEST-10.77.88.99
RF_TEST_ADMIN
RF-Secret-Do-Not-Leak-2026!
RF-CONFIDENTIAL-NOTE
RF-PRIVATE-KEY-MARKER
```

扫描 PostgreSQL dump、API log、Nginx log、Docker log、RemoteFlow log。任何业务明文命中均为阻断发布的 P0。

### 十七、实施顺序

严格按以下阶段推进，每阶段完成后跑测试并输出结果：

```text
Phase 0  Audit current RemoteFlow
Phase 1  AppsCloud skeleton + PostgreSQL + Docker + Health
Phase 2  Identity + Refresh Token
Phase 3  Device registration/revoke
Phase 4  Vault crypto foundation + Recovery
Phase 5  Sync API + Version/Revision/Idempotency
Phase 6  RemoteFlow local Outbox/State Migration
Phase 7  Group sync vertical slice
Phase 8  Connection sync
Phase 9  Credential metadata + Password sync
Phase 10 SSH Private Key/Passphrase sync
Phase 11 Settings + Delete + Conflict
Phase 12 Ubuntu deployment
Phase 13 Dual-device E2E validation
Phase 14 Security marker audit
Phase 15 Backup/restore + rollback rehearsal
```

不要跨阶段“假装完成”。如果底层能力未通过测试，不要继续堆 UI。

### 十八、每阶段输出

每个 Phase 完成后输出：

```text
1. 完成内容
2. 修改文件
3. 数据库 Migration
4. API 变化
5. 安全影响
6. 测试结果
7. 遗留问题
8. 是否满足进入下一 Phase 的 Gate
```

### 十九、禁止事项

- 禁止明文上传 Secret。
- 禁止把账号密码作为可由服务器解密 Vault 的 Key。
- 禁止把 Password/Private Key 写 SQLite 普通字段。
- 禁止日志输出 Secret。
- 禁止全量数据库覆盖式同步。
- 禁止用系统时间代替 Server Version/Revision。
- 禁止忽略 409 冲突并无条件覆盖。
- 禁止云端失败导致 RemoteFlow 不能本地连接。
- 禁止未经测试的破坏性 DB Migration 自动上线。

### 二十、最终完成标准

必须完成真实双设备场景：

```text
PC-A:
创建 DC01 RDP + CORP\Administrator + Password
创建 nginx01 SSH + Private Key + Passphrase

PC-B:
全新安装
登录同一账号
通过设备批准/Recovery
自动同步
无需重新输入 Password/导入 Key
RDP DC01 成功
SSH nginx01 成功
```

同时确认 AppsCloud PostgreSQL、pg_dump、API/Nginx/Docker 日志中找不到测试 Host、Username、Password、Private Key、Notes 的明文。

完成后再生成版本发布报告和部署/回滚记录。
