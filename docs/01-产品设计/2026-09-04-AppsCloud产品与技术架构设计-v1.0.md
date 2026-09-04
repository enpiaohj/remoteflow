# AppsCloud 产品与技术架构设计

> **体系：** AppsCloud + RemoteFlow Cloud Sync  
> **基线：** RemoteFlow 产品设计文档 V1.1（2026-09-03）  
> **文档日期：** 2026-09-04  
> **文档状态：** V1.0 / 开发与实施基线  
> **核心原则：** Local-first、全内容端到端加密、服务器零明文、可离线使用、可恢复、可测试、可回滚

## 1. 产品定义

AppsCloud 是一套面向多个自研客户端产品复用的公共云底座。第一阶段服务 RemoteFlow，后续可复用到 Nianli、QuotaFlow 等产品。

它提供的是“身份、设备、端到端加密密钥封装、增量同步和运维能力”，而不是承载 RemoteFlow 的 RDP/SSH/VNC 协议逻辑。

## 2. 核心目标

- 一个 AppsCloud Account 可登录多个 AppsCloud 生态应用。
- 每个 App 的业务数据相互隔离。
- 支持多设备、Refresh Token、设备撤销。
- 支持 Local-first 客户端的增量同步。
- 对 RemoteFlow 采用 E2EE：服务器不解密业务数据。
- 云端故障不得导致 RemoteFlow 本地核心连接功能不可用。
- 使用简单、稳定、可独立部署和备份的架构。

## 3. 非目标

第一阶段不构建复杂微服务平台，不引入 Kubernetes/Kafka/Service Mesh，不实现企业组织、团队共享、RBAC 和 PAM。

## 4. 总体架构

```text
                   ┌──────────────────────────┐
                   │      AppsCloud      │
                   │                          │
Client HTTPS ─────▶│ API / ASP.NET Core       │
                   │ ├─ Identity              │
                   │ ├─ Devices               │
                   │ ├─ Applications          │
                   │ ├─ Vault Metadata        │
                   │ ├─ Sync                  │
                   │ └─ Operations            │
                   │          │               │
                   │      PostgreSQL          │
                   └──────────────────────────┘

RemoteFlow ─┐
Nianli ─────┼── AppsCloud Account / Device / Sync 共用
QuotaFlow ──┘
```

## 5. 技术基线

| 层 | 选择 | 原因 |
|---|---|---|
| OS | Ubuntu Server 22.04/24.04 LTS | 现有服务器可直接部署 |
| Backend | ASP.NET Core / .NET 10 LTS | 与 RemoteFlow 同技术生态、长期维护 |
| 架构 | Modular Monolith | 简单、可测试、未来可拆分 |
| Database | PostgreSQL | 事务、JSON、索引、稳定性 |
| ORM | EF Core | Migration 与 .NET 集成 |
| Reverse Proxy | Nginx | HTTPS、限流、访问日志 |
| Container | Docker Compose | 单机部署与升级简单 |
| Auth | Access Token + Rotating Refresh Token | 桌面客户端友好 |
| API | REST/JSON | 第一阶段最简单稳定 |
| Realtime | V1 可选 SignalR | 仅做“有变化”通知，不承担数据传输 |

## 6. 模块边界

### Identity

负责注册、登录、密码重置、Access Token、Refresh Token。不得持有或解密业务 Vault Key。

### Applications

维护 `AppId` 和 App 权限边界，例如：

```text
com.appscloud.remoteflow
com.appscloud.nianli
com.appscloud.quotaflow
```

### Devices

记录用户已授权设备、平台、App、最后在线、撤销状态和设备公钥。

### Vault

只保存加密后的 Key Envelope 和元数据；不保存可解密 VMK 的服务器密钥。

### Sync

提供加密实体的 Push、Pull、Revision、Version、Tombstone、冲突检测和幂等处理。

### Operations

提供 Health、Metrics、结构化日志、部署版本、审计元数据。不得记录业务明文或 Secret。

## 7. 多 App 数据隔离

每个同步实体必须包含：

```text
UserId
AppId
EntityType
EntityId
```

服务端所有查询必须至少以 `UserId + AppId` 作为授权过滤条件，禁止客户端仅凭 EntityId 获取数据。

数据库唯一约束：

```text
UNIQUE(UserId, AppId, EntityType, EntityId)
```

## 8. 同步对象模型

AppsCloud 不理解 RemoteFlow 业务字段。

服务端只存：

```text
SyncEntity
├─ UserId
├─ AppId
├─ EntityType
├─ EntityId
├─ Version
├─ Revision
├─ KeyVersion
├─ SchemaVersion
├─ Ciphertext
├─ Nonce
├─ CreatedAt
├─ UpdatedAt
└─ DeletedAt
```

这种设计保证将来 Nianli 可以使用同一个 Sync Service，同步 `calendar/event/todo`，无需重新设计云端业务表。

## 9. 可用性设计

- RemoteFlow 核心业务不依赖 AppsCloud 在线状态。
- API 超时只影响同步，不影响本地连接。
- Push 失败进入 Outbox 重试。
- Pull 失败保留 Cursor，后续继续。
- 服务端重启后 Revision 和 Version 必须保持一致。
- API 必须支持幂等键避免网络重试制造重复实体。

## 10. 扩展路线

### V1.0

Account + Device + E2EE Vault + Sync + Operations。

### V1.1

SignalR 变化通知、设备审批体验优化、密钥轮换、Web 账号设备管理页。

### V1.5

统一订阅/License、Nianli/QuotaFlow 接入。

### V2.0

Team/Organization、共享 Vault、RBAC、SSO/MFA、企业私有化部署。

## 11. 架构约束

1. AppsCloud 不得拥有 RemoteFlow 业务解密密钥。
2. 服务端不得为了“搜索方便”要求上传明文 Host/IP/Username。
3. 不允许把 PostgreSQL 5432 暴露公网。
4. 不允许把数据库备份或日志当成“内部可信”而记录明文 Secret。
5. 客户端和服务端 Schema 必须版本化。
6. 所有变更必须可 Migration、可回滚或有恢复方案。
7. 不因云同步破坏 RemoteFlow 原有 Local-first 体验。
