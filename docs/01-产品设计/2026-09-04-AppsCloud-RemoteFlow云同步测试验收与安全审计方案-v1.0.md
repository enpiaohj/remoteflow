# AppsCloud + RemoteFlow 云同步测试、验收与安全审计方案

> **体系：** AppsCloud + RemoteFlow Cloud Sync  
> **基线：** RemoteFlow 产品设计文档 V1.1（2026-09-03）  
> **文档日期：** 2026-09-04  
> **文档状态：** V1.0 / 开发与实施基线  
> **核心原则：** Local-first、全内容端到端加密、服务器零明文、可离线使用、可恢复、可测试、可回滚

## 1. 目标

验证 AppsCloud 和 RemoteFlow Cloud Sync 在功能、离线、并发、安全、恢复、升级和真实连接场景下形成闭环。

## 2. 测试环境

至少：

```text
Ubuntu AppsCloud 1 台
Windows PC-A 1 台
Windows PC-B 1 台（干净安装）
RDP 测试主机 1 台
SSH 测试主机 1 台
VNC 测试主机 1 台（如 VNC 已实现）
```

## 3. P0 功能测试

### Account/Device

- 注册、登录、Refresh、Logout。
- 同账号两台设备。
- 新设备 Pending Approval。
- PC-A 批准 PC-B。
- PC-B 取得 Envelope 后解锁。
- Device revoke 后 Token 失效。

### Vault

- PC-A 初始化 VMK/Recovery Key。
- Recovery Key 可在无 PC-A 场景恢复。
- 错误 Recovery Key 必须失败。
- VMK/Private Key 不出现在日志和数据库。

### Sync

- Group 创建/修改/删除同步。
- Tag/ConnectionTag 同步。
- RDP Connection 全字段同步。
- SSH Connection 全字段同步。
- Credential Password 同步。
- SSH Private Key + Passphrase 同步。
- User Settings 同步。
- Device Settings 不同步。

## 4. 真实连接验收

### RDP

PC-A 创建：

```text
DC01
Host: 10.10.10.11
Username: CORP\Administrator
Password: TestSecret...
```

PC-B 完成同步后不重新输入凭据，直接连接成功。

### SSH

PC-A 创建 Private Key Credential；PC-B 同步后不手工导入 Key，直接连接成功。

### VNC

如 V0.1 VNC 已可用，同样验证 Password 恢复和连接。

## 5. Offline/Resilience

- AppsCloud 停机，RemoteFlow 启动正常。
- 断网状态新增/修改/删除 Connection。
- 恢复网络后 Outbox 自动提交。
- API 在 Push 后响应前被断网，客户端重试不产生重复 Revision。
- PostgreSQL 重启后同步继续。
- AppsCloud API 容器重启后 Refresh/Sync 正常。
- RemoteFlow 在写 Outbox 后强制终止，重启后继续同步。

## 6. 并发冲突

- PC-A/PC-B 同时修改同一 Connection。
- PC-A/PC-B 同时修改同一 Password。
- 一台删除、另一台修改。
- 冲突必须保留，不静默丢数据。
- Credential Secret 冲突必须人工选择。

## 7. Tombstone

- 删除 Connection 后另一设备删除。
- 离线 7 天设备回来仍能得到删除信息。
- Tombstone 清理前后 Cursor 行为正确。

## 8. Schema/Migration

- V1 客户端数据升级到新 Schema。
- 老客户端遇到不支持 Schema 不覆盖数据。
- SQLite Migration 失败时回滚。
- PostgreSQL Migration 失败时服务不可 Ready，且旧数据未破坏。

## 9. 安全审计

使用专门的唯一测试标记：

```text
Host = RF-SECURITY-TEST-10.77.88.99
Username = RF_TEST_ADMIN
Password = RF-Secret-Do-Not-Leak-2026!
Note = RF-CONFIDENTIAL-NOTE
SSH Key comment = RF-PRIVATE-KEY-MARKER
```

同步后在以下位置全文搜索这些标记：

1. PostgreSQL 表导出。
2. `pg_dump` 文件。
3. AppsCloud API 日志。
4. Nginx Access/Error Log。
5. Docker Log。
6. RemoteFlow 普通应用日志。

期望：均找不到业务明文（本地 Credential Vault 本身除外）。

## 10. 密文完整性测试

- 修改 Ciphertext 1 byte → 解密必须失败。
- 修改 Nonce → 失败。
- 修改 EntityId/AAD → 失败。
- 将用户 A 密文复制给用户 B → 失败。
- 将 RemoteFlow 密文作为其他 AppId 返回 → 失败。

## 11. Device Security

- Revoked Device 不能 Pull。
- Revoked Device 不能 Push。
- Revoked Refresh Token 不能 Refresh。
- 新设备未批准只能读取自身 Pending 状态，不能读取 Sync 数据。
- Key Rotation 后被撤销设备无法解密 v2 数据。

## 12. 性能

基础目标：

- 500 Connections + 200 Credentials 首次同步 UI 不冻结。
- 增量修改单个 Connection 不全量上传。
- Pull 500 条分页稳定。
- 普通单实体 Push/Pull 在正常公网环境下无明显等待感。

具体毫秒阈值以服务器所在区域和网络实测冻结，不在设计阶段伪造绝对值。

## 13. 备份恢复验收

1. 生产式数据库做备份。
2. 清空测试环境数据库。
3. 恢复备份。
4. PC-A/PC-B 重新登录。
5. Vault Envelope 仍有效。
6. Sync Cursor/Revision 连续。
7. RemoteFlow 数据可正常解密和连接。

## 14. 发布 Gate

| Gate | 必须结果 |
|---|---|
| Account/Device | 100% P0 通过 |
| Vault | 100% P0 通过 |
| RDP/SSH Secret Restore | 100% 通过 |
| Offline/Retry | 100% P0 通过 |
| Security marker search | 0 明文命中 |
| Backup Restore | 成功 |
| Critical/High Bug | 0 |
| 回滚演练 | 成功 |

## 15. 最终验收记录

每次候选发布生成：

```text
版本
服务器版本
数据库 Schema Version
RemoteFlow 版本
测试日期
测试人
P0/P1 结果
安全审计结果
备份恢复结果
遗留问题
是否允许发布
```
