# AppsCloud API 与数据库设计规范

> **体系：** AppsCloud + RemoteFlow Cloud Sync  
> **基线：** RemoteFlow 产品设计文档 V1.1（2026-09-03）  
> **文档日期：** 2026-09-04  
> **文档状态：** V1.0 / 开发与实施基线  
> **核心原则：** Local-first、全内容端到端加密、服务器零明文、可离线使用、可恢复、可测试、可回滚

## 1. 设计目标

定义 AppsCloud V1 的 REST API、数据库核心表、同步批处理、错误码、版本和幂等规则，使 RemoteFlow 客户端与服务端可以独立开发、联调和测试。

## 2. API 约定

- Base Path：`/api/v1`
- Content-Type：`application/json`
- 认证：`Authorization: Bearer <access_token>`
- 时间：UTC ISO-8601。
- ID：UUID v4/UUID v7 均可，但全项目统一。
- 所有写请求支持 `X-Idempotency-Key`。
- 错误响应统一包含 `code/message/traceId`。

示例：

```json
{
  "code": "SYNC_VERSION_CONFLICT",
  "message": "Entity version does not match current server version.",
  "traceId": "01J..."
}
```

## 3. Identity API

| Method | Path | 用途 |
|---|---|---|
| POST | /auth/register | 注册 |
| POST | /auth/login | 登录 |
| POST | /auth/refresh | Refresh Token 轮换 |
| POST | /auth/logout | 注销当前 Session |
| POST | /auth/logout-all | 注销全部设备 Session |
| POST | /auth/password/forgot | 发起重置 |
| POST | /auth/password/reset | 完成重置 |
| GET | /account/me | 当前账号 |

关键要求：账号密码仅用于身份认证，不得用于服务器端解密 Vault。

## 4. Device API

| Method | Path | 用途 |
|---|---|---|
| POST | /devices/register | 注册当前设备及公钥 |
| GET | /devices | 列出账号设备 |
| GET | /devices/{id} | 设备详情 |
| POST | /devices/{id}/approve | 已授权设备批准新设备 |
| POST | /devices/{id}/revoke | 撤销设备 |
| POST | /devices/{id}/heartbeat | 更新 LastSeen |

设备注册至少提交：

```json
{
  "appId": "com.appscloud.remoteflow",
  "deviceName": "OFFICE-PC",
  "platform": "windows",
  "appVersion": "0.2.0",
  "publicKey": "base64..."
}
```

## 5. Vault API

| Method | Path | 用途 |
|---|---|---|
| POST | /vault/bootstrap | 首设备初始化 Vault 元数据 |
| GET | /vault/status | Vault 状态/KeyVersion |
| GET | /vault/device-envelope | 获取当前设备 VMK Envelope |
| POST | /vault/device-envelopes | 授权设备上传新设备 Envelope |
| PUT | /vault/recovery-envelope | 保存 Recovery Wrapped VMK |
| GET | /vault/recovery-envelope | 恢复流程获取 Envelope |
| POST | /vault/rotate/commit | Key Rotation 提交新版本 |

服务端只保存 Envelope；不得接受裸 VMK。

## 6. Sync API

### 6.1 Push

`POST /sync/push`

```json
{
  "appId": "com.appscloud.remoteflow",
  "deviceId": "...",
  "operations": [
    {
      "operationId": "...",
      "entityType": "connection",
      "entityId": "...",
      "baseVersion": 4,
      "schemaVersion": 1,
      "keyVersion": 1,
      "nonce": "base64...",
      "ciphertext": "base64...",
      "deleted": false
    }
  ]
}
```

成功返回每个 operation 的：`serverVersion/revision/status`。

### 6.2 Pull

`GET /sync/pull?appId=com.appscloud.remoteflow&cursor=1280&limit=500`

返回：

```json
{
  "cursor": 1302,
  "hasMore": false,
  "changes": [
    {
      "entityType": "connection",
      "entityId": "...",
      "version": 5,
      "revision": 1301,
      "schemaVersion": 1,
      "keyVersion": 1,
      "nonce": "base64...",
      "ciphertext": "base64...",
      "deleted": false,
      "updatedAt": "2026-09-04T00:00:00Z"
    }
  ]
}
```

### 6.3 初始全量

初次设备使用 `cursor=0` 分页 Pull。服务端仍走同一增量接口，不单独设计“数据库下载”。

## 7. 冲突响应

Push 时 `baseVersion != currentVersion`：

- 返回 HTTP 409。
- 返回当前服务器实体的加密版本、Version、Revision。
- 服务端不得尝试解密或合并。
- 客户端负责解密后决定使用本地、远端或创建冲突副本。

## 8. PostgreSQL 核心表

### Users

```text
Id UUID PK
Email CITEXT UNIQUE
PasswordHash
Status
CreatedAt
UpdatedAt
```

### RefreshSessions

```text
Id UUID PK
UserId FK
DeviceId FK
RefreshTokenHash
ExpiresAt
RevokedAt
CreatedAt
LastUsedAt
```

Refresh Token 仅存 Hash，不存原文。

### Devices

```text
Id UUID PK
UserId FK
AppId
Name
Platform
PublicKey
KeyAlgorithm
Status
AppVersion
CreatedAt
LastSeenAt
RevokedAt
```

### Vaults

```text
Id UUID PK
UserId FK
AppId
CurrentKeyVersion
CreatedAt
UpdatedAt
UNIQUE(UserId, AppId)
```

### DeviceKeyEnvelopes

```text
Id UUID PK
VaultId FK
DeviceId FK
KeyVersion
Algorithm
Nonce
Ciphertext
CreatedAt
UNIQUE(VaultId, DeviceId, KeyVersion)
```

### RecoveryEnvelopes

```text
Id UUID PK
VaultId FK
KeyVersion
Algorithm
KdfParameters JSONB
Nonce
Ciphertext
CreatedAt
```

### SyncEntities

```text
Id BIGINT PK
UserId UUID
AppId TEXT
EntityType TEXT
EntityId UUID
Version BIGINT
Revision BIGINT
SchemaVersion INT
KeyVersion INT
Nonce BYTEA
Ciphertext BYTEA
DeletedAt TIMESTAMPTZ NULL
CreatedAt TIMESTAMPTZ
UpdatedAt TIMESTAMPTZ
UNIQUE(UserId, AppId, EntityType, EntityId)
```

### SyncChanges

可采用 SyncEntities 上的全局 Revision 查询，也可单独建立 append-only ChangeLog。V1 推荐单独 `SyncChanges`，便于 Tombstone 保留、审计和游标稳定：

```text
Revision BIGSERIAL PK
UserId UUID
AppId TEXT
EntityType TEXT
EntityId UUID
EntityVersion BIGINT
Operation TEXT
CreatedAt TIMESTAMPTZ
```

## 9. 索引

至少建立：

```text
SyncChanges(UserId, AppId, Revision)
SyncEntities(UserId, AppId, EntityType, EntityId) UNIQUE
Devices(UserId, AppId, Status)
RefreshSessions(UserId, DeviceId, RevokedAt)
```

## 10. 事务边界

一次 Push Operation 在同一数据库事务中完成：

1. 锁定/读取当前 Entity Version。
2. 校验 baseVersion。
3. Upsert SyncEntity。
4. 生成 SyncChange Revision。
5. 写入最终 Version/Revision。
6. Commit。

任何一步失败必须全部回滚。

## 11. Tombstone 与清理

删除操作不立即物理删除业务实体，至少保留 Tombstone 90 天（V1 可配置）。超过保留期前必须确保所有已知活跃设备都有机会推进 Cursor；无法保证时宁可延长保留期。

## 12. 错误码

| Code | 说明 |
|---|---|
| AUTH_INVALID_CREDENTIALS | 登录失败 |
| AUTH_TOKEN_EXPIRED | Access Token 过期 |
| DEVICE_REVOKED | 设备被撤销 |
| VAULT_NOT_INITIALIZED | Vault 未初始化 |
| VAULT_DEVICE_NOT_APPROVED | 新设备无 Envelope |
| SYNC_VERSION_CONFLICT | Version 冲突 |
| SYNC_INVALID_CURSOR | Cursor 无效 |
| SYNC_SCHEMA_UNSUPPORTED | Schema 不兼容 |
| SYNC_KEY_VERSION_UNSUPPORTED | Key Version 不兼容 |
| RATE_LIMITED | 触发限流 |

## 13. API 安全边界

- 所有资源以 Access Token 的 UserId 为准，禁止客户端提交任意 UserId 作为授权依据。
- DeviceId 必须属于当前 UserId/AppId。
- Vault Envelope 只能由已授权设备为待批准设备创建。
- 请求体大小限制，尤其 SSH Key/证书。
- API 日志不得记录 Authorization、Ciphertext 全文、Recovery Key、Token。
