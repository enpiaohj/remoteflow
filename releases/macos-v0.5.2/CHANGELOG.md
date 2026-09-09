# RemoteFlow macOS v0.5.2

发布日期：2026-09-10 · 上一版本 macos-v0.5.1

继续修复云同步在「第二台设备」上的阻塞。与 Windows v0.11.2 同一批修复
（共享层 `RemoteFlow.Core` / `RemoteFlow.Infrastructure`）。无破坏性变更，直接覆盖安装。

---

## 安装

DMG 拖入「应用程序」；首次启动右键「打开」，或
`xattr -dr com.apple.quarantine /Applications/RemoteFlow.app`。

---

## 修复

- **第二台设备一直「同步出错，将自动重试」（拉取时父实体未到）**：服务端按 Revision 排序，
  `credential-secret` 的 Revision 常低于它的 `credential`（父实体被另一台改过、secret 没动）。
  secret 先到、父还没落地时旧逻辑直接抛异常 → 整轮同步失败、游标不推进、重启一样。
  - 现在改为延后重试：同页内父实体先应用；跨页时整轮拉完再重试延后项；真找不到父实体才跳过并告警。
- **两台设备版本号无限互推 / 反复「刷新后好多冲突」**：仓储 `UpdateAsync` 会把 `UpdatedAt`
  置为当前时间，导致拉取落地后内容和刚拉下来的不一致 → 对账把正常拉取当漂移又推上去。
  - 拉取落地后，内容哈希改用「重新读取的实际状态」计算——对账从此稳定。
- **注册 2 台设备、账号里显示 5 台**：「清除此设备云数据」会清空本机设备标识，
  每次清除 + 重新登录就多注册一个设备行。
  - 「清除此设备云数据」不再清空设备标识（这台机器的稳定身份）。

---

## 验证

- CI 三 job 全绿；`dotnet test -c Release` —— Core.Tests 42、IntegrationTests 159、
  IntegrationTests.Windows 9 全绿；IntegrationTests.Mac 在非 macOS 上 skip。
- 端到端 `CloudRoundTripTests` / `CloudSyncFacadeTests` 对 `https://sync.appscloud.cn/` 3/3 通过。
- 本版未做 macOS 实机走查。
- 平台：macOS 13+ · 架构：arm64 + x64（分别打包）

---

## Git

- Tag：`macos-v0.5.2` · 提交范围 `macos-v0.5.1..`（`fix(cloud)` 拉取依赖顺序 / 对账假漂移 / 设备重复 + 版本号 / 快照）
- 共享层修复同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.11.2]`）。

## 产物

由 `.github/workflows/release-macos.yml` 在 `macos-v0.5.2` tag 推送后构建并上传到 GitHub Release：

- `RemoteFlow-v0.5.2-macos-arm64.dmg` —— Apple Silicon，ad-hoc 签名 —— Size / SHA-256：见发布后补录
- `RemoteFlow-v0.5.2-macos-x64.dmg` —— Intel（arm64 runner 交叉编译），ad-hoc 签名 —— Size / SHA-256：见发布后补录
