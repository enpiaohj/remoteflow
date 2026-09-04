# RemoteFlow v0.1.1

发布日期：2026-09-04

在 v0.1.0（Unified Connection MVP）基础上补齐凭据迁移、密码显隐、右键菜单，
并做了几处启动稳定性与全屏工具条的打磨。

## Added

### 凭据加密备份（导入 / 导出）
- 连接列表的 CSV 仍不含任何 Secret；要连凭据一起迁移，走这条独立的口令加密路径。
- 文件格式 `.rfbackup`：PBKDF2-HMAC-SHA256（600k 迭代 / 16B 盐）派生密钥，
  AES-256-GCM（12B nonce / 16B tag）加密。GCM 是 AEAD——口令错或文件被篡改都会在
  解密时被认证标签挡下，绝不解出垃圾数据去覆盖用户配置。
- 明文（含密码 / 私钥）只在内存中存在，加解密前后 `CryptographicOperations.ZeroMemory`；
  本地 DPAPI 保险库不受影响。
- 导入按凭据名称（忽略大小写）跳过已存在的，不覆盖本机已有密钥；结束给
  「新增 N / 跳过 M」报告。口令错 / 文件损坏时不写入任何数据。
- 入口在「导入 / 导出」页新增的「凭据（加密备份）」两张卡片，含醒目安全提示。

### 密码显隐切换
- 凭据编辑框的密码 / Passphrase 字段右侧加「眼睛」按钮，可在密文与明文间切换。
  明文始终不进入 ViewModel（延续 PasswordBox 不参与绑定的设计），保存 / 取消后清空。

### 右键菜单
- 连接列表：右击整行弹出菜单（原先只有行内「⋯」按钮）——连接 / 编辑 / 复制 /
  收藏（可勾选）/ 删除。
- 凭据列表：新增右键菜单——编辑 / 删除。
- 顶部会话 Tab：新增右键菜单——重新连接 / 关闭会话 / 关闭其他会话 / 关闭全部会话
  （首页 Tab 不弹）。

### 其他
- 首次进入全屏时，悬浮工具条多停留数秒并给一次性提示「工具条会自动隐藏 ·
  鼠标移到屏幕顶部再唤出」，之后不再提示。

## Fixed

- 全屏悬浮工具条固定 / 显示时没贴住屏幕上边缘——去掉顶部边距，顶角切平底角圆。
- **启动稳定性**：上一实例被强制结束后残留的 `-wal` / `-shm` 与主库不一致导致
  `SQLITE_IOERR`、退避重试也救不回来时，若这两个文件当前没有被任何进程占用，
  自动删除让 SQLite 从主库重新生成（已提交数据都在主库里），再打开一次。
- 页面数据加载失败原来只写日志、用户对着空白页发懵——改为在中央工作区顶部显示
  带「重试」按钮的警示横幅。
- 启动时的种子数据创建从 UI 线程上的 sync-over-async 改为线程池执行
  （SQLite 的 async 目前是同步实现，暂不会死锁，但不再依赖这个巧合）。
- 会话 Tab 的关闭按钮从 22×22 放大到 26×26，更好点中。

## Known Issues

- RDP 服务器证书告警对话框由系统 `mstsc` 控件弹出（非本应用 UI）；用户勾选
  「不再询问」后由 Windows 记住该服务器，证书变化时 `mstsc` 仍会重新告警。
- 单文件 `RemoteFlow.exe` 单独运行时 RDP / VNC 可用；SSH 终端需要同目录下的
  `Assets/Terminal/`，请使用 `.zip` 包。
- 强制结束进程仍会残留 SQLite `-wal` / `-shm`；下次启动由退避重试 / 自愈接管，
  正常退出则会 checkpoint 清理。
- SSH / VNC 未在本轮做真实设备的深度联调，人工测试清单见
  `docs/02-测试/2026-09-03-RemoteFlow-V0.1.0-SSH-VNC人工测试清单-v1.0.md`。

## Verification

- Build：`dotnet build -c Release` → 0 警告 0 错误
- Tests：56 通过 / 0 失败（32 单元 + 24 集成，新增 9 个：8 凭据备份 + 1 SHM 自愈）
- Publish：`dotnet publish -c Release`（win-x64、self-contained、单文件、压缩）
- Runtime：应用启动 / 渲染 / 优雅退出正常（exit 0、WAL 清零、日志无 ERR/FTL）；
  RDP 实机连接 POC DC（`cygdi\piaohj`）此前已验证
- Platform：Windows 11 Pro（10.0.26200）
- Architecture：x64

## Artifacts

| 文件 | 说明 | SHA256 |
| --- | --- | --- |
| `RemoteFlow-v0.1.1-win-x64.zip` | 完整发布包（含 `Assets/Terminal/`），**推荐** | `9116da1350f4aa5fa90fa161154aa64d8be7309b192cb4d44ba32a9b7b2a158e` |
| `RemoteFlow-v0.1.1-win-x64.exe` | 单文件（RDP / VNC 可用，SSH 终端需 zip 包） | `331862d16ece1dfaeea2e222bdf59e8ee8558bada255cf8c4575a2713b2de32c` |

产物为本地磁盘不可变快照，不入库；`source/` 与本文件入库。

## Git

- Commit：`release: RemoteFlow v0.1.1`
- Tag：`v0.1.1`
- 快照对应源码：`38ac7c2`（发布二进制 ProductVersion 内嵌同一 commit）
- 上一版本 `releases/v0.1.0/` 不变。
