# RemoteFlow macOS v0.6.0

发布日期：2026-09-10 · 上一版本 macos-v0.5.3

多设备一致性的两个结构改进（共享层，与 Windows v0.12.0 同批）。无破坏性变更，直接覆盖安装。

---

## 安装

DMG 拖入「应用程序」；首次启动右键「打开」，或
`xattr -dr com.apple.quarantine /Applications/RemoteFlow.app`。

---

## 新增

- **「清除本地数据并从云端恢复」**（设置 → 云同步 → 账号卡片）：以云端为权威的兜底。
  清空本机全部连接 / 凭据及密码 / 分组 / 标签 / 连接历史与同步状态（保留云会话与设备身份），
  然后从云端完整拉回。与「清除此设备云数据」（保留本地、清同步状态）相反。
  用途：本机数据混乱 / 与另一台重复、想以另一台的云端数据为准时一键对齐。

## 变更（冲突处理）

- **元数据冲突 Last-Writer-Wins 自动解决**：同一条 connection / credential 在两台设备都被改过时，
  按内容更新时间的最后一笔自动收敛，不再每次弹「保留本机 / 使用云端」。
  - 本地较新 → 以服务端当前版本为基线重推覆盖；云端较新 → 采纳云端、丢弃本地较早一笔。
  - 密码 / 私钥仍弹窗，绝不静默覆盖；组 / 标签、删除冲突、无法解密的仍走对话框（保守回退）。

## 说明

- 两台机器启用云同步前各自手工建过的同名服务器（不同 ID 的重复条目）自动合并尚未实现（下一步），
  当前可用「清除本地数据并从云端恢复」以某一台为准收敛。

---

## 验证

- CI 三 job 全绿；`dotnet test -c Release` —— Core.Tests 42、IntegrationTests 164、
  IntegrationTests.Windows 9 全绿；IntegrationTests.Mac 在非 macOS 上 skip。
- 端到端 `CloudRoundTripTests` / `CloudSyncFacadeTests` 对 `https://sync.appscloud.cn/` 3/3 通过。
- 本版未做 macOS 实机走查。
- 平台：macOS 13+ · 架构：arm64 + x64（分别打包）

---

## Git

- Tag：`macos-v0.6.0` · 提交范围 `macos-v0.5.3..`（restore 兜底 + LWW + 版本号 / 快照）
- 共享层改动同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.12.0]`）。

## 产物

由 `.github/workflows/release-macos.yml` 在 `macos-v0.6.0` tag 推送后构建并上传到 GitHub Release：

- `RemoteFlow-v0.6.0-macos-arm64.dmg` —— Apple Silicon，ad-hoc 签名 —— Size / SHA-256：见发布后补录
- `RemoteFlow-v0.6.0-macos-x64.dmg` —— Intel（arm64 runner 交叉编译），ad-hoc 签名 —— Size / SHA-256：见发布后补录
