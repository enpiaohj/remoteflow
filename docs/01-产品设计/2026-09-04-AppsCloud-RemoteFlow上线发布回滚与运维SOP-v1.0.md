# AppsCloud + RemoteFlow 上线发布、回滚与运维 SOP

> **体系：** AppsCloud + RemoteFlow Cloud Sync  
> **基线：** RemoteFlow 产品设计文档 V1.1（2026-09-03）  
> **文档日期：** 2026-09-04  
> **文档状态：** V1.0 / 开发与实施基线  
> **核心原则：** Local-first、全内容端到端加密、服务器零明文、可离线使用、可恢复、可测试、可回滚

## 1. 目的

定义从开发完成到测试、生产发布、故障回滚和日常运维的标准流程，确保 AppsCloud 和 RemoteFlow Cloud Sync 不出现“代码完成但无法安全上线”的断层。

## 2. 环境

至少区分：

```text
Development
Test/Staging
Production
```

生产数据库和测试数据库物理/逻辑隔离，禁止用生产 Vault 数据做开发测试。

## 3. 版本标识

AppsCloud：

```text
appscloud-api:1.0.0
DB Schema: 1
Sync Protocol: 1
Vault Schema: 1
```

RemoteFlow：

```text
RemoteFlow-v0.2.0-win-x64.exe
```

客户端需要记录：最低支持 Server API Version / Sync Protocol Version。

## 4. 发布前 Checklist

### Code

- main/release branch clean。
- 编译无错误。
- Unit/Integration 测试通过。
- 依赖 License/安全扫描无阻断项。

### Database

- Migration Review 完成。
- 发布前备份完成。
- 备份可读取。
- Staging Migration 已验证。

### Security

- Security Marker Audit 0 明文泄露。
- Secret/Token 不在 config/repo/log。
- 被撤销 Device 测试通过。
- Recovery 测试通过。

### Operations

- `/health/live` `/health/ready` 正常。
- 磁盘空间足够。
- Backup Job 正常。
- 回滚镜像存在。

## 5. 发布步骤

```text
1. 宣布维护/冻结写入（如 Migration 需要）
2. PostgreSQL Backup
3. 记录当前 API Image + DB Schema
4. Pull 新 Image
5. 执行 Migration
6. 启动新 API
7. Health Check
8. Account/Device 冒烟
9. PC-A Push 测试
10. PC-B Pull/Decrypt 测试
11. RDP/SSH 测试连接
12. 观察 5xx/日志/DB
13. 解除维护状态
14. 记录发布结果
```

## 6. 快速回滚判定

出现以下任一情况立即停止发布并评估回滚：

- Login/Refresh 大面积失败。
- Sync 数据损坏或不可解密。
- Version/Revision 异常倒退/重复。
- DB Migration 导致旧 API 不兼容。
- 明文 Secret 出现在服务端。
- RemoteFlow 本地数据被错误删除。

## 7. 回滚类型

### A. 仅 API Bug，DB Schema 兼容

```text
停止新 API
→ 启动旧 API Image
→ Health/Smoke Test
```

### B. DB Migration 可逆

执行已测试 Down/Reverse Migration，再回滚 API。

### C. DB Migration 不可逆或数据损坏

```text
停止写入
→ 保全故障现场
→ 新建/清理数据库
→ 恢复发布前 Backup
→ 启动旧 API
→ 验证 Revision/Cursor/Vault Envelope
```

## 8. RemoteFlow 客户端回滚

客户端升级必须保留 SQLite Backup/Migration Strategy。

如果新客户端 Schema 不兼容旧版：

- 升级前创建本地数据库备份。
- 提供恢复工具/步骤。
- 不允许用户简单安装旧 exe 后直接打开新 Schema DB。

## 9. 日常运维

每日：

- API/Container 状态。
- Backup 结果。
- 5xx/Sync Error。
- 磁盘空间。

每周：

- 被撤销/异常设备检查。
- PostgreSQL 增长。
- Tombstone/ChangeLog 增长。
- 日志轮转。

每月：

- Ubuntu/Docker 安全更新窗口。
- 恢复抽检。
- 账号/设备异常行为分析。

每季度：

- 完整 Backup Restore 演练。
- Recovery Key/新设备恢复测试。
- Key Rotation 演练。
- 安全 Marker Audit 重跑。

## 10. 故障分类

| 故障 | 用户影响 | 首要动作 |
|---|---|---|
| API Down | 无法同步，Local 可用 | 恢复 API，不动客户端本地数据 |
| PostgreSQL Down | 无法登录/同步 | 恢复 DB/连接，保持 API NotReady |
| Token Issue | 登录/Refresh 失败 | 检查 Signing/Clock/Session |
| Sync Conflict 激增 | 多端变更受阻 | 检查 Version/幂等逻辑 |
| Vault 解密失败 | 新设备不可用 | 检查 Envelope/KeyVersion，不重置数据 |
| 明文泄露 | 安全事件 | 停止相关日志/服务，保全证据，轮换 Secret/Key |

## 11. 数据保留

- Tombstone 默认 >=90 天。
- Refresh Session 按过期/撤销清理。
- 结构化操作日志按实际隐私和存储要求设置保留期。
- 不因为“日志排障方便”增加业务明文字段。

## 12. 变更管理

任何影响以下内容的变更都必须更新文档并重新过 Gate：

```text
Sync Protocol
Payload Schema
Vault Algorithm
Key Envelope
DB Schema
Authentication/Refresh
Device Approval
Tombstone Retention
```

## 13. 发布记录模板

```text
发布日期：
AppsCloud Version：
RemoteFlow Version：
DB Schema：
Sync Protocol：
Vault Key Version：
发布人：
备份路径/编号：
Migration：
Smoke Test：
Security Audit：
Rollback Image：
结果：Success / Rolled Back
问题：
```

## 14. 上线完成定义

只有当：

- 生产 Health 正常；
- PC-A/PC-B 双端真实同步通过；
- RDP/SSH Secret Restore 成功；
- 数据库/日志无明文；
- 备份完成且恢复方案可用；
- 回滚路径确认；

才可宣布 Cloud Sync 正式上线。
