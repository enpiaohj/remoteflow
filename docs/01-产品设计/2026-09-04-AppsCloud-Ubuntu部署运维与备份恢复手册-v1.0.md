# AppsCloud Ubuntu 部署、运维与备份恢复手册

> **体系：** AppsCloud + RemoteFlow Cloud Sync  
> **基线：** RemoteFlow 产品设计文档 V1.1（2026-09-03）  
> **文档日期：** 2026-09-04  
> **文档状态：** V1.0 / 开发与实施基线  
> **核心原则：** Local-first、全内容端到端加密、服务器零明文、可离线使用、可恢复、可测试、可回滚

## 1. 适用范围

用于将 AppsCloud V1 部署到现有 Ubuntu Server。默认单机 Docker Compose 架构，适合开发、测试和第一阶段生产使用。

## 2. 推荐最低环境

```text
Ubuntu Server 22.04 / 24.04 LTS
2 vCPU
4 GB RAM
40~80 GB SSD
公网/可访问 IP
域名（生产必须）
80/443 可开放
```

数据库不开放公网。

## 3. 目标拓扑

```text
Internet
   │ 443
   ▼
Nginx
   │
   ▼
AppsCloud.Api : 8080 (Docker internal)
   │
   ▼
PostgreSQL : 5432 (Docker internal only)
```

SSH 22 仅允许管理来源 IP/VPN。

## 4. 目录建议

```text
/opt/appscloud/
├─ compose.yaml
├─ .env                 # chmod 600，不进 Git
├─ nginx/
├─ data/
│  └─ postgres/
├─ backups/
├─ logs/
└─ scripts/
```

## 5. Docker Compose 服务

- `appscloud-api`
- `postgres`
- `nginx`

第一阶段不强制 Redis。

镜像必须使用固定版本 Tag，不使用 `latest`。

## 6. 环境变量

至少：

```text
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__Main=...
Jwt__Issuer=...
Jwt__Audience=...
Jwt__SigningKey=...
Security__AllowedOrigins=...
AppsCloud__PublicBaseUrl=https://api.example.com
```

生产 Secret 不写入源码、README 或 Dockerfile。

## 7. 防火墙

建议 UFW：

```text
22/tcp   仅管理网/IP
80/tcp   公网（证书签发/跳转）
443/tcp  公网
5432     禁止公网
8080     禁止公网
```

## 8. HTTPS

生产必须使用域名和有效证书。

Nginx：

- 80 强制跳转 443。
- TLS 终止。
- HSTS。
- 请求体大小限制。
- 登录/注册/密码重置限流。
- 不记录 Authorization Header。

## 9. 数据库初始化与 Migration

部署原则：

1. 备份当前数据库。
2. 启动新版本 API 前执行兼容 Migration。
3. Migration 必须记录版本。
4. 破坏性 Migration 必须有单独回滚/恢复方案。
5. 禁止生产环境自动执行未经检查的 `EnsureCreated()`。

## 10. Health Check

API 提供：

```text
GET /health/live
GET /health/ready
```

Live：进程是否存活。

Ready：PostgreSQL、Migration 状态和关键依赖是否可用。

Nginx/容器健康检查使用 Ready，但不得把业务数据写入 Health 输出。

## 11. 日志

使用结构化日志，建议 JSON：

```text
Timestamp
Level
TraceId
Route
StatusCode
DurationMs
UserId(optional internal)
DeviceId(optional)
```

日志轮转：至少日切 + 大小限制 + 保留期。

禁止记录 Request Body 的同步 Ciphertext 全文，更禁止 Secret。

## 12. 备份策略

### PostgreSQL

建议：

- 每日逻辑备份 `pg_dump`。
- 每周存储/磁盘快照（如云平台支持）。
- 至少一份异机/对象存储副本。
- 备份加密。

参考保留：

```text
Daily 7
Weekly 4
Monthly 6~12
```

### 配置

备份：

- compose.yaml
- Nginx 配置
- 数据库 Migration 版本
- 环境变量模板（不含 Secret）

生产 Secret 应单独安全保管。

## 13. 恢复演练

至少季度执行一次：

1. 新建空 Ubuntu/测试环境。
2. 部署同版本 AppsCloud。
3. 恢复 PostgreSQL 备份。
4. 启动 API。
5. 验证账号登录、Device、Vault Envelope、Sync Cursor。
6. 使用测试 RemoteFlow PC 拉取并成功解密。

“有备份”不等于“可恢复”，恢复演练通过才算闭环。

## 14. 升级流程

```text
备份
→ 拉取/构建新镜像
→ Migration Dry Run/Review
→ 部署新 API
→ Health Check
→ RemoteFlow 冒烟测试
→ 观察日志
→ 宣布完成
```

## 15. 回滚

如果新版本只包含向后兼容 Schema：回滚 API 镜像即可。

如果包含不可逆 Migration：不得直接回滚二进制，应从发布前备份恢复数据库，具体按 09 SOP 执行。

## 16. 监控建议

第一阶段至少：

- API 5xx 数量。
- P95/P99 请求延迟。
- 登录失败异常峰值。
- Sync Push/Pull 失败率。
- 409 Conflict 比例。
- PostgreSQL 连接数/磁盘使用率。
- 磁盘剩余空间。
- 容器重启次数。
- Backup 成功/失败。

## 17. 安全运维

- 系统及时安装安全更新。
- SSH 禁止弱口令；优先 Key + 管理网/VPN。
- 禁止以 root 直接运行应用容器。
- 数据库使用独立最小权限账号。
- `.env` 权限 600。
- 防止 Docker Socket 暴露给应用容器。
- 定期检查被撤销设备和异常 Refresh Session。

## 18. 首次部署验收

- HTTPS 证书有效。
- 80 自动跳转 443。
- 5432/8080 从公网不可访问。
- `/health/live` `/health/ready` 正常。
- Migration 成功。
- 注册/登录/Refresh 正常。
- RemoteFlow 测试设备能 Push/Pull。
- `pg_dump` 成功并完成一次测试恢复。
