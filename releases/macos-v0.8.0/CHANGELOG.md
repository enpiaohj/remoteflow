# RemoteFlow macOS v0.8.0

发布日期：2026-09-10 · 上一版本 macos-v0.7.5

首次加入自动消重（共享层，与 Windows v0.14.0 同批）。

## 新增

- 首次加入已有 Vault 时，按自然键（连接 host+端口+协议+名称 / 凭据 名称+类型+账号+域 /
  分组 标签 名称）把本地重复条目认领到云端 Id，不再推重复条目；本机独有条目照常推送。

## 修复

- 落地密码 / 私钥不再刷新凭据 UpdateAt（此前导致凭据每次同步都白推一次）。

## 验证

- CI 三 job 全绿；`dotnet test -c Release` —— Core.Tests 42、IntegrationTests 179、
  IntegrationTests.Windows 9 全绿；IntegrationTests.Mac 在非 macOS 上 skip。
- 本版未做 macOS 实机走查。
- 平台：macOS 13+ · 架构：arm64 + x64（分别打包）

## Git

- Tag：`macos-v0.8.0` · 提交范围 `macos-v0.7.5..`
- 共享层改动同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.14.0]`）。

## 产物

- `RemoteFlow-v0.8.0-macos-arm64.dmg` —— Size / SHA-256：见发布后补录
- `RemoteFlow-v0.8.0-macos-x64.dmg` —— Size / SHA-256：见发布后补录
