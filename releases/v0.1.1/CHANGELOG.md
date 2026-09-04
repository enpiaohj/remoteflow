# RemoteFlow v0.1.1

发布日期：2026-09-04

在 v0.1.0 基础上的两处 bug 修复。功能范围不变。

## Fixed

### SSH 主机密钥确认对话框被握手超时吞掉
`OnHostKeyReceived` 由 SSH.NET 在握手线程上同步触发，原实现直接在那里阻塞等待
UI 弹窗的结果。`ConnectionInfo.Timeout` 会先到——SSH.NET 抛超时、连接已判失败，
用户这时才点完对话框，结果被丢弃，会话最终报「连接超时」。

改为两步，弹窗不再卡在握手线程上：
- 握手线程只做 `Lookup`：本机指纹与记录一致才 `CanTrust = true` 放行；否则
  `CanTrust = false` 中止握手（此时凭据尚未发送），把待确认的密钥记下。
- 连接失败后，由正常异步流程弹出确认对话框；用户接受则记录指纹并自动重试一轮
  （这轮 `Lookup` 静默通过），拒绝则按「首次拒绝 / 指纹不一致」给准确错误码。
- 指纹变化仍是强警告（信任按钮用警示色、默认焦点在「取消」），绝不静默接受。

### 连接 CSV 导入不去重
`ImportExportService.ImportAsync` 原来对每行无条件新增，重复导入同一文件会产生副本。
现以 **名称 + 主机 + 端口 + 协议** 为身份去重：与库中已有的、或同一 CSV 内先前行
相同的行跳过，并在结果里逐条说明。名称相同但主机 / 端口不同仍视为不同连接。
（凭据加密备份导入本就按名称去重，不受影响。）

## Known Issues

- RDP 服务器证书告警对话框由系统 `mstsc` 控件弹出（非本应用 UI）；用户勾选
  「不再询问」后由 Windows 记住该服务器，证书变化时 `mstsc` 仍会重新告警。
- 单文件 `RemoteFlow.exe` 单独运行时 RDP / VNC 可用；SSH 终端需要同目录下的
  `Assets/Terminal/`，请使用 `.zip` 包。
- 强制结束进程仍会残留 SQLite `-wal` / `-shm`；下次启动由退避重试 / 自愈接管。
- SSH / VNC 未做真实设备的深度联调，人工测试清单随源码归档。

## Verification

- Build：`dotnet build -c Release` → 0 警告 0 错误
- Tests：**60 通过 / 0 失败**（32 单元 + 28 集成，新增 4 个连接导入去重测试）
- Publish：`dotnet publish -c Release`（win-x64、self-contained、单文件、压缩）
- Runtime：应用启动 / 渲染 / 优雅退出正常（exit 0、WAL 清零、日志无 ERR/FTL）
- Platform：Windows 11 Pro（10.0.26200）
- Architecture：x64

## Artifacts

| 文件 | 说明 | SHA256 |
| --- | --- | --- |
| `RemoteFlow-v0.1.1-win-x64.zip` | 完整发布包（含 `Assets/Terminal/`），**推荐** | `177fcd923d09b1e03bba47804fcfc8869bf7b9ee06d64c468b94de755f4c2c5f` |
| `RemoteFlow-v0.1.1-win-x64.exe` | 单文件（RDP / VNC 可用，SSH 终端需 zip 包） | `e93da1b46bc3766f10eb26ad8326f947b957ea64b1b68789f9306ec6eb8c004e` |

产物为本地磁盘不可变快照，不入库；`source/` 与本文件入库，大文件走 GitHub Release。

## Git

- Commit：`release: RemoteFlow v0.1.1`
- Tag：`v0.1.1`
- 快照对应源码：父提交 `971faf5`（发布二进制 ProductVersion 内嵌 `0.1.1+971faf5`）
- `releases/v0.1.0/` 不变。
