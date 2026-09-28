# RemoteFlow macOS v0.8.0

发布日期：2026-09-10 · 上一版本 macos-v0.7.5

首次加入自动消重（共享层，与 Windows v0.14.0 同批）。

> ## ⚠ 本版未发布（未构建产物、未创建 GitHub Release）
>
> 封版时 GitHub Actions 因**账户计费问题**（`The job was not started because recent account
> payments have failed...`）全部 job 在启动前即被拒，无法由 CI 产出 DMG；
> macOS 本地发布脚本当时尚未就绪，故本版**从未构建任何产物**。
>
> **功能已由 [`macos-v0.8.1`](../macos-v0.8.1/CHANGELOG.md) 完整覆盖并发布**，
> 两版差异仅为系统信息采集修正。`macos-v0.8.0` tag 保留（历史事实），但不补发产物；
> 用户请使用 `macos-v0.8.1`。此为一次性决定，记录于此以便追溯。

## 新增

- 首次加入已有 Vault 时，按自然键（连接 host+端口+协议+名称 / 凭据 名称+类型+账号+域 /
  分组 标签 名称）把本地重复条目认领到云端 Id，不再推重复条目；本机独有条目照常推送。

## 修复

- 落地密码 / 私钥不再刷新凭据 UpdateAt（此前导致凭据每次同步都白推一次）。

## 验证

- **本版未获得门禁结果**：封版推送时 GitHub Actions 已因账户计费故障全部 job 被拒，
  CI 三个 job 均为失败（非代码失败，job 未启动）。
- **本版未做 macOS 实机走查**，未在 macOS 上执行 `dotnet test`。
- 同批的 Windows 侧（`v0.14.0`）有本地测试结论，见 `releases/v0.14.0/CHANGELOG.md`。
- 平台：macOS 13+ · 架构：arm64 + x64（计划分别打包）

## Git

- Tag：`macos-v0.8.0` · 提交范围 `macos-v0.7.5..`
- 共享层改动同时影响 Windows 版（见根 `CHANGELOG.md` 的 `[0.14.0]`）。

## 产物

- **无**。本版未构建、未发布（见顶部说明）。
