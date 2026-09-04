# RemoteFlow 云同步与加密 Vault 接入设计

> **体系：** AppsCloud + RemoteFlow Cloud Sync  
> **基线：** RemoteFlow 产品设计文档 V1.1（2026-09-03）  
> **文档日期：** 2026-09-04  
> **文档状态：** V1.0 / 开发与实施基线  
> **核心原则：** Local-first、全内容端到端加密、服务器零明文、可离线使用、可恢复、可测试、可回滚

## 1. 设计原则

本设计基于 RemoteFlow V1.1 原有基线，不推翻现有 WPF/MVVM/SQLite/Credential Vault/Provider 架构。

原有核心规则继续有效：

- View 不直接操作协议。
- ViewModel 不保存 Secret。
- Connection 引用 Credential。
- Secret 不明文落入 SQLite。
- 本地优先，无云仍可使用。

Cloud Sync 是新增的基础设施能力，不是新的业务中心。

## 2. RemoteFlow 新架构

```text
WPF UI / MVVM
      │
Application Services
      │
├─ ConnectionManager ─ RDP/SSH/VNC Provider
├─ Asset Service ───── SQLite
├─ CredentialVault ─── Windows Credential Manager / DPAPI
└─ SyncCoordinator
      ├─ Outbox
      ├─ Pull/Push
      ├─ E2EE Encryption
      ├─ Device/Vault
      └─ AppsCloudClient
```

## 3. 项目结构调整

不建议第一阶段大量拆 Project。保留现有 Solution，只新增：

```text
RemoteFlow.Infrastructure/
├─ Sync/
│  ├─ AppsCloudClient
│  ├─ SyncCoordinator
│  ├─ SyncOutboxRepository
│  ├─ SyncStateRepository
│  ├─ SyncSerializer
│  └─ ConflictService
└─ Security/
   ├─ VaultMasterKeyService
   ├─ DeviceKeyService
   ├─ PayloadEncryptionService
   ├─ RecoveryKeyService
   └─ SecureMemoryHelper
```

接口放在 Core/Application，具体网络/DPAPI 实现在 Infrastructure。

## 4. 同步对象

### 必须同步

- Connection
- Credential Metadata
- Credential Secret
- Group
- Tag
- ConnectionTag
- Favorite
- Notes
- RDP/SSH/VNC 连接参数
- SSH Private Key / Passphrase
- VNC Password
- Gateway/Proxy Credential（存在时）
- 用户级 Settings
- Connection Template（实现后）

### 第一阶段不默认同步

- 完整 ConnectionHistory（仅 `LastConnectedAt` 可同步）
- 当前已打开 Session Tab
- 窗口坐标/像素尺寸
- Crash Dump
- Cache/临时文件
- Machine-specific 路径

## 5. Settings Scope

为 Setting 增加：

```text
Scope = User | Device | Session
```

仅 `User` Scope 同步。

例如：

- Theme：User。
- DefaultRdpClipboard：User。
- WindowLeft/WindowTop：Device。
- 当前 Tab：Session。

## 6. Connection/Credential 保持分离

本地仍然：

```text
Connection.CredentialId
      ↓
Credential.SecretReference
      ↓
Windows Credential Manager / DPAPI
```

Cloud 同步时分别产生：

```text
EntityType = connection
EntityType = credential
EntityType = credential-secret
```

三者均在客户端加密后上传。

## 7. 新建 RDP 的写入流程

用户保存：

```text
DC01 / RDP / 10.10.10.11
CORP\Administrator / Password
```

同一业务事务：

1. 保存 Connection 到 SQLite。
2. 保存 Credential Metadata 到 SQLite。
3. 保存 Password 到 Windows Credential Vault。
4. 在 SyncOutbox 写入 connection/credential/credential-secret 三个待同步操作。
5. UI 立即返回成功，不等待云端。
6. 后台 SyncCoordinator 加密并 Push。

## 8. Pull 恢复流程

PC-B：

1. 登录 AppsCloud Account。
2. 完成 Device Approval/Recovery。
3. 获取 VMK。
4. Pull changes。
5. 校验 AAD 并解密。
6. Connection/Group/Tag 写 SQLite。
7. Credential Metadata 写 SQLite。
8. Credential Secret 写 Windows Credential Vault，并生成本地 SecretReference。
9. 更新 Local Version/Cursor。

任何 Secret 不允许以明文持久化到 Sync 临时表。

## 9. Cloud Sync UI

设置增加：

```text
Cloud Sync
├─ AppsCloud Account
├─ 同步状态
├─ 当前设备
├─ 已授权设备
├─ Recovery Key
├─ 最后同步时间
├─ [立即同步]
└─ [退出登录]
```

状态：

```text
未登录
等待设备批准
正在同步
已同步
离线
存在冲突
同步错误
Vault 已锁定
```

主界面不要持续弹窗；正常同步仅使用轻量状态。

## 10. 云端故障行为

AppsCloud 不可达时：

- Connection CRUD 正常。
- RDP/SSH/VNC 正常。
- Secret 读取正常。
- Outbox 累积。
- UI 显示“离线，稍后同步”。
- 不阻塞应用启动。

## 11. 退出账号

默认区分：

### 仅退出云账号

撤销本机 Refresh Token，保留本地 RemoteFlow 数据，应用继续作为本地工具使用。

### 清除此设备云数据

用户明确选择后：

- 注销。
- 清除本机 VMK/Device Key 缓存。
- 可选择删除本地同步数据和 Credential。

危险操作必须二次确认。

## 12. 设备批准体验

PC-B：

```text
这是一台新设备，需要获得 Vault 访问权。
[等待其他设备批准]
[使用 Recovery Key]
```

PC-A：

```text
新设备请求访问 RemoteFlow Vault
设备：HOME-PC
平台：Windows
时间：...

[拒绝] [批准]
```

不允许只有账号密码就自动拿到 Vault。

## 13. 发布迁移

现有无云 RemoteFlow 用户升级时：

- 默认保持本地模式。
- 不强制注册。
- 用户主动启用 Cloud Sync 后，进行 Vault 初始化和首次上传。
- 首次上传必须显示对象统计并可取消。

例如：

```text
将同步：
Connections  128
Credentials   37
Groups        12
Tags          24
Secrets       37
```

## 14. 性能要求

- 500 条连接首次加密上传应后台进行，不冻结 UI。
- Batch Push 默认 100~500 条可配置。
- Pull 分页。
- 密钥派生/加解密放后台线程。
- 不对未变化对象重复加密/上传。

## 15. 完成定义

只有当第二台干净 RemoteFlow 能恢复完整 RDP/SSH/VNC 配置和 Credential，并直接成功连接，同时服务端无业务明文时，Cloud Sync 才视为完成。
