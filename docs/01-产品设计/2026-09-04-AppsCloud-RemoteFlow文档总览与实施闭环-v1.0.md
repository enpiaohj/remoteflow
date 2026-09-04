# AppsCloud + RemoteFlow 云同步文档总览与实施闭环

> **体系：** AppsCloud + RemoteFlow Cloud Sync  
> **基线：** RemoteFlow 产品设计文档 V1.1（2026-09-03）  
> **文档日期：** 2026-09-04  
> **文档状态：** V1.0 / 开发与实施基线  
> **核心原则：** Local-first、全内容端到端加密、服务器零明文、可离线使用、可恢复、可测试、可回滚

## 1. 文档目的

本套文档用于把 RemoteFlow 从“本地优先的统一远程连接工作台”扩展为“本地优先 + AppsCloud 加密多端同步”的完整产品形态，同时将 AppsCloud 建设成可被 Nianli、QuotaFlow 等后续产品复用的公共账号与同步底座。

本套文档不是替换 RemoteFlow V1.1 产品设计，而是其配套扩展。RemoteFlow 原有 C# + .NET 10 + WPF、MVVM、SQLite、Credential Vault、RDP/SSH/VNC Provider、多 Tab、本地优先等基线全部保留。

## 2. 闭环目标

第一阶段最终必须完成以下真实业务闭环：

1. Ubuntu Server 上部署 AppsCloud。
2. RemoteFlow 接入 AppsCloud Account。
3. PC-A 创建 RDP、SSH、VNC 连接及凭据。
4. Connection、Credential、Group、Tag、Favorite、用户级 Settings、Password、SSH Private Key、Passphrase 等全部同步。
5. PC-B 登录同一账号，并通过“已授权设备批准”或 Recovery Key 获得 Vault 访问权。
6. PC-B 自动恢复完整数据，并可直接使用同步后的 Credential 连接。
7. AppsCloud 数据库、日志、备份中均不得出现 Host、IP、Username、Password、SSH Private Key、Notes 等业务明文。
8. 断网修改、重连、重复请求、客户端崩溃、服务端重启、设备撤销、删除和冲突场景均有明确处理。
9. 有部署、监控、备份、恢复、升级、回滚、测试和验收流程。

## 3. 文档清单

| 编号 | 文档 | 作用 | 主要使用者 |
|---|---|---|---|
| 00 | 文档总览与实施闭环 | 定义全套文档关系、阶段门和最终验收 | 产品/开发/实施 |
| 01 | AppsCloud 产品与技术架构设计 | 公共云底座总体设计 | 架构/后端 |
| 02 | AppsCloud API 与数据库设计 | API、表结构、同步对象、错误码 | 后端/客户端 |
| 03 | 身份、设备与 E2EE Vault 安全设计 | 密钥、设备授权、Recovery、加密 | 安全/开发 |
| 04 | Ubuntu 部署运维与备份恢复手册 | 云端安装、升级、备份、监控 | 实施/运维 |
| 05 | RemoteFlow 云同步与加密 Vault 接入设计 | 如何无侵入接入现有 RemoteFlow | Windows 客户端 |
| 06 | 同步协议、数据迁移与冲突处理设计 | Outbox、Pull/Push、Tombstone、Migration | 客户端/后端 |
| 07 | 云同步测试验收与安全审计方案 | 功能、故障、安全、恢复测试 | QA/验收 |
| 08 | Claude Code 完整开发实施提示词 | 可直接用于实施开发 | Claude Code |
| 09 | 上线发布、回滚与运维 SOP | 从测试到生产的上线闭环 | 发布/运维 |

## 4. 架构关系

```text
RemoteFlow Windows
├─ WPF / MVVM
├─ RDP / SSH / VNC Providers
├─ SQLite
├─ Windows Credential Manager / DPAPI
├─ Sync Engine
└─ E2EE Vault
       │ HTTPS 443
       ▼
AppsCloud
├─ Identity
├─ Devices
├─ Vault Metadata / Key Envelopes
├─ Sync API
├─ PostgreSQL
└─ Operations / Audit Metadata

AppsCloud 永远不需要解密 RemoteFlow 业务数据。
```

## 5. 第一阶段功能边界

### 5.1 必须实现

- AppsCloud Account：注册、登录、Refresh Token、Logout、密码重置。
- Device：设备注册、设备列表、最后在线、撤销设备。
- E2EE Vault：VMK、Device Key、Recovery Key、Key Envelope、Key Version。
- RemoteFlow 全量可迁移数据同步。
- Local-first：无网络仍可查看配置和建立远程连接。
- 增量 Push/Pull、Outbox、Tombstone、乐观并发、冲突保留。
- 云端 HTTPS、数据库隔离、备份恢复、日志脱敏。
- 双设备完整同步验证。

### 5.2 暂不实现

- Team Vault / 多人共享。
- RBAC / 企业组织空间。
- Kubernetes、Kafka、RabbitMQ、微服务拆分。
- 手机/Web RemoteFlow 客户端。
- 服务器端搜索 RemoteFlow 业务字段（因全内容加密不可见）。
- 自动密码轮换和企业 PAM。

## 6. 阶段门

| Gate | 条件 | 未通过时 |
|---|---|---|
| G0 架构冻结 | 01~03 文档确认 | 不进入云端开发 |
| G1 云端可用 | HTTPS/API/PostgreSQL/Health 正常 | 不接 RemoteFlow |
| G2 身份闭环 | Account + Device + Token 通过测试 | 不进入 Vault |
| G3 Vault 闭环 | PC-A 生成 VMK，PC-B 可批准/恢复 | 不同步 Secret |
| G4 普通同步 | Group/Connection 可双端增量同步 | 不迁 Credential |
| G5 Secret 同步 | Password/SSH Key 可安全恢复并连接 | 不进入生产候选 |
| G6 故障闭环 | 断网/冲突/删除/重试/崩溃通过 | 不发布 |
| G7 安全验收 | DB/Log/Backup 无业务明文 | 不发布 |
| G8 生产发布 | 备份、回滚、监控、SOP 完成 | 才允许上线 |

## 7. 最终验收场景

PC-A 创建：

```text
客户A
├─ AD
│  └─ DC01 / RDP / CORP\Administrator / Password
└─ Linux
   └─ nginx01 / SSH / root / Private Key + Passphrase
```

PC-B 全新安装 RemoteFlow：

```text
登录 AppsCloud Account
→ 新设备批准或 Recovery Key
→ Pull 增量数据
→ 解密并落入本地 SQLite + Windows Credential Vault
→ 无需重复输入密码或导入 Key
→ DC01 RDP 成功
→ nginx01 SSH 成功
```

服务端核查：

```text
允许看到：UserId、AppId、EntityId、EntityType、Version、Revision、Ciphertext、时间戳
禁止看到：名称、IP、Host、Username、Password、Private Key、Notes、Group 名称、Tag 内容
```

## 8. 文档使用顺序

开发前依次阅读：00 → 01 → 02 → 03 → 05 → 06。

部署人员阅读：00 → 01 → 04 → 09。

测试与验收阅读：00 → 03 → 06 → 07 → 09。

Claude Code 实施时，以 08 为执行提示词，但任何实现冲突必须以 01~07 的设计基线为准。
