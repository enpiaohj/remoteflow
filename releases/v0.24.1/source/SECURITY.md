# 安全说明

RemoteFlow 处理远程主机凭据，安全是首要设计约束。

## 凭据存储模型

```
SQLite (remoteflow.db)        Windows DPAPI (vault.dat)
├─ Credential 元数据            ├─ 密码密文
├─ SecretReference（引用键）  →  ├─ 私钥密文
└─ KeyReference（引用键）        └─ Passphrase 密文
```

- 连接数据库**只保存引用键**，不含任何明文 Secret。
- 明文由 `DpapiCredentialVault` 用当前 Windows 用户账户的主密钥加密，
  存于独立的 `vault.dat`。数据库被单独复制走无法得到密码；
  连 `vault.dat` 一起复制走，在其他机器或其他 Windows 账户下同样无法解密。
- `ResolvedCredential` 只在建立连接的瞬间存在，用后立即 `Dispose`，
  且 `ToString()` 不输出任何 Secret。

## 日志脱敏

- 允许记录：主机名/IP、协议、连接阶段、标准化错误码、耗时。
- 禁止记录：密码、私钥正文、Token、完整认证报文、剪贴板内容。
- 两道防线：调用方不传 Secret + `SecretRedactingEnricher` 对疑似敏感属性兜底遮蔽。

## SSH 主机密钥

- 首次连接记录 SHA256 指纹。
- 指纹变化时**中止连接并强警告**，默认焦点在「取消」，信任按钮使用警示色。
- 无校验策略时拒绝连接，绝不静默信任任意主机密钥。

## 导入 / 导出

- CSV 导出文件**不含任何密码或私钥**，只写凭据名称用于人工核对。
- CSV 导入只按名称关联已存在的凭据，不从文件内容创建凭据。

## 报告漏洞

请勿在公开 Issue 中提交安全问题。通过内部渠道联系维护者，
描述问题类别与影响范围，不要附带可用的利用步骤。
