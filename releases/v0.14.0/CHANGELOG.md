# RemoteFlow v0.14.0

发布日期：2026-09-10

首次加入自动消重 + 凭据重复推送修复。上一正式版本为 v0.13.5。

## Added

- **首次加入按自然键认领云端 Id**：两台机器在启用云同步之前各自建过同一台服务器 / 分组 / 标签 /
  凭据 → 两条不同 Id 的重复条目。现在首次同步（云端全量已落地、对账之前）自动合并：
  - 自然键：连接 = host + 端口 + 协议 + 名称；凭据 = 名称 + 类型 + 账号 + 域；分组 / 标签 = 名称；
  - 以云端那份为准：改写子引用（连接的 group/credential、标签关联、分组父子）指向云端 Id，
    删除本机重复行及其同步状态与待推条目；凭据旧密钥引用提交后从平台密钥库清理；
  - 单事务、顺序 分组/标签 → 凭据 → 连接；**本机独有条目不受影响**。

## Fixed

- 凭据版本每次多涨 1：落地密码 / 私钥时刷新了凭据 UpdateAt → 内容哈希立刻失效 → 对账再推一次。
  改为定点更新引用（SetSecretReferencesAsync），不触碰 updated_at。

## Verification

- Build：`dotnet build RemoteFlow.slnx -c Release --nologo` → 0 错误。
- Tests：`dotnet test -c Release` → Core.Tests 42；IntegrationTests 179；IntegrationTests.Windows 9。
- 未执行：完整 UI 手动走查、两台物理机的同步演练。
- Platform / Architecture：Windows 11 · win-x64。Schema：无变更。

## Artifacts

- `RemoteFlow-v0.14.0-win-x64.exe`
  - Size：74,876,426 bytes
  - SHA-256：`AD7221FE0D6006DC439277DD9776A404866B5AC1AEFB18633FBB3DCC3BDD2180`
- `RemoteFlow-v0.14.0-win-x64.zip`
  - Size：69,402,356 bytes
  - SHA-256：`F4C610BF7D278E7760DE04B22BBE379DE911096A0A7958BD867EFF37E184301F`

> 本次发布时 GitHub Actions 出现平台故障（所有 job 5–10 秒内失败、`steps` 为空、`runner_name` 为空，
> 连 push `main` 的 CI 也如此），CI 无法产出产物。Windows 产物由**本地**以同一命令
> （`dotnet publish src/RemoteFlow.App -c Release -r win-x64`）构建并手动上传；
> macOS 的 DMG 需 macOS runner，等 Actions 恢复后由 `release-macos.yml` 重跑补发。

## Git

- 提交范围 `v0.13.5..`：`ee59aac` feat(cloud) · 版本号 / 快照封版。
- Tag：`v0.14.0`。分支：`main`。
