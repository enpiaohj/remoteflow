# AppsCloud 身份、设备与 E2EE Vault 安全设计

> **体系：** AppsCloud + RemoteFlow Cloud Sync  
> **基线：** RemoteFlow 产品设计文档 V1.1（2026-09-03）  
> **文档日期：** 2026-09-04  
> **文档状态：** V1.0 / 开发与实施基线  
> **核心原则：** Local-first、全内容端到端加密、服务器零明文、可离线使用、可恢复、可测试、可回滚

## 1. 安全目标

RemoteFlow 可能存储企业服务器地址、域账号、RDP 密码、SSH Private Key、Passphrase、VNC Password、Gateway Credential 等高价值信息。因此第一阶段必须把“云同步 Secret”当作 Vault 产品设计，而不是普通配置同步。

安全目标：

- AppsCloud 数据库管理员无法读取 RemoteFlow 业务数据。
- 数据库泄露不直接导致连接资料或 Secret 泄露。
- TLS 终止层和 API 日志不得获得业务明文。
- 新设备必须经过授权或 Recovery Key 恢复。
- 设备撤销立即终止云端 Session；必要时支持 Vault Key Rotation。
- 本机仍使用 Windows DPAPI/Credential Manager 保护 Secret。

## 2. 威胁模型

考虑：

1. PostgreSQL 数据库被复制。
2. 数据库备份泄露。
3. 云端运维管理员读取数据库。
4. API 日志/错误日志泄露。
5. Refresh Token 被盗。
6. 一台已授权 Windows 设备丢失。
7. 新设备冒充合法设备。
8. 网络中间人。
9. 客户端崩溃产生 Dump。

不承诺防御：已解锁且被完全控制的终端主机上的本地恶意软件。终端被完全攻陷时，正在使用的 Secret 可能被窃取。

## 3. 密钥层次

```text
Vault Master Key (VMK, 256-bit random)
│
├─ HKDF("entity", EntityId) → Entity Encryption Key
│      └─ AES-256-GCM → RemoteFlow Payload
│
├─ 由 Device ECDH 产生的 KEK 包装 → DeviceKeyEnvelope
│
└─ Recovery Key 派生 KEK 包装 → RecoveryEnvelope
```

VMK 永远不以明文上传 AppsCloud。

## 4. 算法基线

| 用途 | 建议 |
|---|---|
| Payload Encryption | AES-256-GCM |
| KDF / Domain Separation | HKDF-SHA-256 |
| Device Key Agreement | ECDH P-256（使用 .NET/OS 成熟实现） |
| Recovery Key | 256-bit CSPRNG，Base32/分组显示 |
| Recovery KEK | HKDF-SHA-256(RecoveryKey, salt, context) |
| Password Hash | ASP.NET Core PasswordHasher 或 Argon2id 成熟库 |
| Random | RandomNumberGenerator |

禁止自研密码算法、固定 IV/Nonce、ECB、可逆“字符串加密”、把密钥写入 appsettings.json。

## 5. Payload 加密

RemoteFlow 在上传前序列化业务实体，使用稳定 JSON/二进制 Schema，然后：

```text
entityKey = HKDF(VMK, salt=EntityId, info="RemoteFlow/Entity/v1")
nonce = Random(12 bytes)
AAD = UserId | AppId | EntityType | EntityId | SchemaVersion | KeyVersion
ciphertext, tag = AES-256-GCM(entityKey, nonce, plaintext, AAD)
```

AAD 防止密文被跨用户、跨 App 或跨实体替换。

## 6. 首设备初始化

1. 用户注册/登录 AppsCloud Account。
2. RemoteFlow 生成设备 ECDH Key Pair。
3. Private Key 由 Windows DPAPI 绑定当前用户保护，Public Key 上传。
4. 客户端生成随机 VMK。
5. 客户端生成 Recovery Key 并要求用户保存。
6. 创建本设备 `DeviceKeyEnvelope`。
7. 创建 `RecoveryEnvelope`。
8. 上传 Envelope 与 Vault Metadata。
9. VMK 仅在本机安全内存生命周期内使用，需要持久化时由 DPAPI 包装。

## 7. 新设备授权

### 方式 A：已授权设备批准（默认）

PC-B 登录 AppsCloud Account：

1. 生成自己的设备 Key Pair，上传 Public Key。
2. 状态为 `PendingApproval`。
3. PC-A 获取待批准设备信息并由用户确认。
4. PC-A 使用 ECDH 与 PC-B Public Key 派生 KEK。
5. PC-A 用该 KEK 包装 VMK，上传 `DeviceKeyEnvelope`。
6. PC-B 下载 Envelope，用 Private Key 解开 VMK。
7. PC-B 开始 Pull 和解密。

### 方式 B：Recovery Key

无旧设备时，PC-B 获取 RecoveryEnvelope，用户手工输入 Recovery Key，在客户端恢复 VMK，然后为本设备生成新的 DeviceKeyEnvelope。

## 8. Recovery Key 规则

- Recovery Key 只显示给用户，服务端不保存明文。
- 第一次开启 Cloud Vault 必须提示保存。
- UI 支持“我已保存”确认和后续重新生成。
- 重新生成 Recovery Key 时，应创建新的 RecoveryEnvelope 并废弃旧 Envelope。
- 丢失所有授权设备且没有 Recovery Key：服务端无法恢复旧 Vault 数据。这是设计结果，不是 Bug。

## 9. 账号密码重置

账号 Password 和 Vault Key 完全分离。

密码重置只恢复账号登录能力，不自动产生 VMK。用户仍需：

- 通过已授权设备批准；或
- 使用 Recovery Key。

因此 AppsCloud 不存在“客服重置密码后即可查看用户 RDP 密码”的后门。

## 10. 设备撤销

撤销设备时立即：

- 撤销该 Device 的所有 Refresh Session。
- Device 状态置为 Revoked。
- 拒绝该 Device 的 Push/Pull/Vault API。
- 删除或禁用其 DeviceKeyEnvelope。

重要：如果设备在撤销前已取得 VMK，单纯服务器撤销无法让其“忘记”密钥。高安全场景必须提供 `Rotate Vault Key`：生成新 VMK、重新加密实体、为剩余设备重新建立 Envelope。

## 11. 本地 Secret 存储

延续 RemoteFlow V1.1：

```text
SQLite
  └─ Credential Metadata / SecretReference
                 │
                 ▼
Windows Credential Manager / DPAPI
                 │
                 ▼
              Secret
```

云端同步恢复 Secret 后，不允许明文落入 SQLite。

建议：

- VMK 缓存由 DPAPI 保护。
- Device Private Key 由 DPAPI 保护。
- Secret 临时字符串生命周期最短化。
- 不将 Secret 放入 ViewModel、日志、异常 Message、Telemetry。

## 12. SSH Private Key

Private Key 作为 Secret Payload 同步。云端只保存 Ciphertext。

客户端恢复后优先保存到加密 Vault/受控文件区域；如果协议库必须使用临时文件：

- 使用 ACL 限制当前用户。
- 文件名不可包含 Key 内容。
- 会话结束及时删除。
- 崩溃恢复时清理残留临时文件。

## 13. TLS 与 API 安全

- 只允许 HTTPS。
- TLS 1.2+；优先系统现代协议。
- HSTS。
- 账号登录和敏感 API 限流。
- Refresh Token Rotate + Reuse Detection。
- CORS 对桌面 API 通常不构成主要边界，但 Web 管理页出现后必须严格配置。

## 14. 日志脱敏

允许：

```text
TraceId
UserId（内部 ID）
DeviceId
AppId
EntityType
EntityId
Revision
Version
HTTP Status
Latency
```

禁止：

```text
Email（默认不打；必要时脱敏）
Password
Recovery Key
VMK
Device Private Key
Host/IP/Username/Notes
SSH Private Key
Authorization/Refresh Token
完整 Ciphertext
```

## 15. 安全验收

必须完成：

1. 数据库全文搜索无法找到测试 Host/IP/Username/Password/Private Key。
2. API/Reverse Proxy 日志全文搜索无上述明文。
3. pg_dump 备份全文搜索无上述明文。
4. PC-B 未批准时无法解密任何同步 Payload。
5. Recovery Key 错误无法解密 VMK。
6. 被撤销设备 Refresh Token 不能继续换取 Access Token。
7. 篡改 AAD、Nonce、Ciphertext 必须导致认证失败而不是返回乱码明文。
8. Secret 不进入 crash dump/普通日志的测试路径。
