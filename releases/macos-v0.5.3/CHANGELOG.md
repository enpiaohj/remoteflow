# RemoteFlow macOS v0.5.3

发布日期：2026-09-10 · 上一版本 macos-v0.5.2

修复「两台设备都显示已同步、连接数却不一致」。与 Windows v0.11.3 同一批修复
（共享层 `RemoteFlow.Core` / `RemoteFlow.Infrastructure`）。无破坏性变更，直接覆盖安装。

---

## 安装

DMG 拖入「应用程序」；首次启动右键「打开」，或
`xattr -dr com.apple.quarantine /Applications/RemoteFlow.app`。

---

## 修复

- **干净设备拉取时 `connection` 落地报外键错误 → 整轮同步失败、数据永不收敛**：
  首次同步时对账按 source 注册顺序入队，`connection` 排在它引用的 `credential` / `group`
  之前 → 在服务端拿到更小的 `Revision`。一台干净设备按 `Revision` 顺序拉取时 `connection`
  先到、父实体还没落地 → 数据库外键失败 → 抛异常 → 整轮同步失败、游标不推进、每轮都失败。
  界面可能停在上一次的「已同步」，两台机器的数据一直不一致。
- 按设计文档「同步协议 §6 / §12 / §17」重构拉取：
  - 整段变更先收进内存（跨页去重、保留最高 `Revision`），一批全部成功才推进游标（§6）。
  - 按依赖顺序落地：`group` / `tag` / `credential` → `credential-secret` → `connection`（§12）。
  - 落不下去的两轮重试；跨页时父实体在后一页的第二轮就能过。
  - 两轮后仍失败：不推进游标、标记 `Error`、保留已落地的本地数据，下轮整批重试（§17）。

---

## 升级说明

之前卡住的设备升级后会自动重试整批拉取并收敛。若服务端数据被重置过，本机的同步指针
可能指向已不存在的云端数据 → 在「云同步」里做一次「清除此设备云数据」再重新登录即可。

---

## 验证

- CI 三 job 全绿；`dotnet test -c Release` —— Core.Tests 42、IntegrationTests 159、
  IntegrationTests.Windows 9 全绿；IntegrationTests.Mac 在非 macOS 上 skip。
- 端到端 `CloudRoundTripTests` / `CloudSyncFacadeTests` 对 `https://sync.appscloud.cn/` 3/3 通过。
- 本版未做 macOS 实机走查。
- 平台：macOS 13+ · 架构：arm64 + x64（分别打包）

---

## Git

- Tag：`macos-v0.5.3` · 提交范围 `macos-v0.5.2..`（`fix(cloud)` 拉取整批按依赖顺序 + 失败隔离 + 版本号 / 快照）
- 共享层修复同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.11.3]`）。

## 产物

由 `.github/workflows/release-macos.yml` 在 `macos-v0.5.3` tag 推送后构建并上传到 GitHub Release：

- `RemoteFlow-v0.5.3-macos-arm64.dmg` —— Apple Silicon，ad-hoc 签名 —— Size / SHA-256：见发布后补录
- `RemoteFlow-v0.5.3-macos-x64.dmg` —— Intel（arm64 runner 交叉编译），ad-hoc 签名 —— Size / SHA-256：见发布后补录
