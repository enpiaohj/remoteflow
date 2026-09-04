# RemoteFlow 同步协议、数据迁移与冲突处理设计

> **体系：** AppsCloud + RemoteFlow Cloud Sync  
> **基线：** RemoteFlow 产品设计文档 V1.1（2026-09-03）  
> **文档日期：** 2026-09-04  
> **文档状态：** V1.0 / 开发与实施基线  
> **核心原则：** Local-first、全内容端到端加密、服务器零明文、可离线使用、可恢复、可测试、可回滚

## 1. 目标

定义 RemoteFlow Local-first 数据如何可靠进入 AppsCloud，以及多设备并发、删除、离线、重复请求、升级和旧数据迁移的处理方式。

## 2. 本地同步表

在 SQLite 增加：

### SyncOutbox

```text
Id
OperationId
EntityType
EntityId
OperationType (Upsert/Delete)
BaseVersion
CreatedAt
RetryCount
NextRetryAt
LastErrorCode
```

Outbox 不保存 Secret 明文；需要 Push 时从正式业务存储/Vault 读取后即时加密。

### SyncState

```text
AppId
Cursor
LastSuccessfulSyncAt
LastAttemptAt
Status
```

### SyncEntityState

```text
EntityType
EntityId
ServerVersion
LastLocalChangeAt
ConflictState
```

### SyncConflict

```text
Id
EntityType
EntityId
LocalSnapshotEncrypted
RemoteSnapshotEncrypted
DetectedAt
ResolvedAt
Resolution
```

冲突副本自身也必须本地加密。

## 3. Outbox Pattern

任何本地业务变更必须遵循：

```text
BEGIN SQLite Transaction
  修改业务表
  写入/合并 SyncOutbox
COMMIT
```

不能先保存业务数据、随后“尽力”写 Outbox，否则崩溃窗口会造成永不同步的数据。

## 4. Outbox 合并

同一个 Entity 在尚未 Push 前连续修改多次，可合并为最新 Upsert。

规则：

```text
Create + Update -> Upsert
Update + Update -> 最新 Upsert
Create + Delete（从未成功上云） -> 可直接取消 Outbox 和本地实体
已上云 Update + Delete -> Delete/Tombstone
```

## 5. Push 状态机

```text
Pending
→ Encrypting
→ Sending
→ Acked
→ Remove/Archive Outbox

失败：
Sending → RetryWaiting → Sending
```

指数退避 + 抖动，例如：5s、15s、45s、2m、5m，封顶后周期重试。

401：先 Refresh Token；失败则转为需要登录。

409：进入 Conflict，不做无限自动覆盖。

## 6. Pull 状态机

```text
cursor=N
→ GET /sync/pull
→ decrypt each change
→ apply local transaction
→ advance cursor to returned cursor
```

关键：只有一批 Changes 全部成功应用后才能推进 Cursor。某条解密失败时必须停止并记录错误，不能跳过造成永久缺口。

## 7. 删除/Tombstone

本地业务实体删除时：

- 如果已同步过，写 Delete Outbox。
- Cloud 生成 Tombstone 和新 Revision。
- 其他设备 Pull 后删除/归档本地对象及关联 Secret。
- Tombstone 服务端保留期默认 90 天，可配置。

凭据删除需先检查是否被 Connection 引用，沿用 RemoteFlow 原有业务约束。

## 8. Version 与 Revision

- `Version`：单个 Entity 的乐观并发版本，从 1 递增。
- `Revision`：用户+App 变化流的单调游标。

例：

```text
DC01 connection Version 1 → 2 → 3
整个用户变化 Revision 1001 → 1002 → 1003...
```

客户端 Push 必须提交 `baseVersion`。

## 9. 冲突模型

由于服务端看不到明文，冲突只能检测不能合并。

### Connection/Group/Tag

V1 默认：

1. 检测 409。
2. 客户端解密远端版本。
3. 若可以证明只改不同字段，可做客户端字段级合并（可选增强）。
4. 否则保存 Conflict，提示“保留本机 / 使用云端 / 另存副本”。

### Credential Secret

不得静默字段合并。Password/Private Key 冲突必须明确选择，避免把错误密码自动覆盖到全部设备。

## 10. 设备间排序

Group SortOrder/Connection Order 属于 User Scope 时同步。为降低冲突，可采用间隔排序值（如 1000、2000、3000），插入时取中值，必要时后台重排。

## 11. 初次启用云同步

现有 RemoteFlow 本地已有数据时：

1. 完整本地数据一致性检查。
2. 确认所有 Credential SecretReference 可解析。
3. 初始化 Account/Device/Vault。
4. 为所有同步对象建立 EntityId（已有 Id 保留）。
5. 写入 Initial Outbox。
6. 分批加密 Push。
7. 每批 Ack 后更新 ServerVersion。
8. 全部成功后保存 Cursor。

不得在首次同步前修改原有 ID，避免 Connection-Credential 引用断裂。

## 12. 干净设备首次恢复

1. 拉取 Group/Tag/Connection/Credential 等全部变更。
2. 解密到内存。
3. 按依赖顺序落地：Group/Tag → Credential Metadata → Secret → Connection → ConnectionTag/Settings。
4. Secret 写入 Windows Credential Manager/DPAPI。
5. 建立新的本机 SecretReference。
6. 事务提交。
7. 更新 Cursor。

不要把云端 SecretReference 原样复制到另一台设备，因为 SecretReference 是本机绑定的。

## 13. Schema Migration

每个 Payload 包含 `SchemaVersion`。

客户端必须提供：

```text
Deserialize v1
Migrate v1 -> Current
Validate
Persist
```

新客户端上传新 Schema 前，AppsCloud 无需理解字段，但旧客户端 Pull 到不支持 Schema 时应返回/显示 `SYNC_SCHEMA_UNSUPPORTED`，不得把密文当损坏数据覆盖。

## 14. Key Rotation

当高风险设备丢失或主动轮换：

1. 在安全设备生成 VMK v2。
2. 本地逐实体解密 v1、加密 v2。
3. 批量 Push keyVersion=2。
4. 为剩余设备创建 v2 Device Envelopes。
5. 创建新 RecoveryEnvelope。
6. Cloud CurrentKeyVersion 切换到 2。
7. 旧 Key Envelope 进入废弃状态。

Key Rotation 要支持中断恢复，不能要求一次性全部完成。

## 15. 幂等性

每个 Outbox Operation 使用固定 `OperationId` 作为 Idempotency Key。网络超时重试同一 OperationId，服务端必须返回第一次提交结果，而不是再产生一条 Revision。

## 16. 本地时间与顺序

同步正确性不得依赖不同设备系统时间完全一致。Version/Revision 由服务器控制；客户端时间只用于 UI 和审计参考。

## 17. 失败隔离

单个损坏/无法解密实体：

- 不推进对应 Pull Batch Cursor。
- 标记 Sync Error。
- 保留原本地可用数据。
- 提供诊断 TraceId/EntityId，不打印明文。

不能因为一个云实体损坏而删除本地所有 Connection。
