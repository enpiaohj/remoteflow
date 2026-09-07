# RemoteFlow v0.8.1

发布日期：2026-09-07

上一正式版本为 v0.8.0。本版为 Windows 平台修复版：修掉会话全屏在“非标准最大化”窗口下的溢出与工具条无法唤出。macOS 平台不在本次发布范围内。

## Fixed

- **会话全屏在最大化窗口下溢出**。从 `WindowState.Maximized` 进入会话全屏时，旧代码在窗口态迁移的同步块里改 `Window.Left/Top/Width/Height`，会被 WPF 覆盖，残留 WindowChrome 的约 8px 溢出矩形：
  - 窗口上沿被顶出显示器可视区（“上边偏上移”），底部露出本机任务栏（本机任务栏压在远端画面的目标系统任务栏之上）；
  - 顶沿的胶囊工具条触发带落到可视区之外，鼠标移到屏幕顶端也无法唤出工具条。
- **根因**：`ApplyFullScreen` 经 `TransformFromDevice` 把显示器物理边界换算成 WPF 的 DIU 再赋值定位。PerMonitorV2 下该 DIU 空间随所在显示器缩放而变，且在 `WindowState` / `WindowStyle` 刚切换、HWND 尚未稳定关联到目标显示器时取到的变换是旧值——换算出的边界会差几像素，窗口与显示器矩形没有严格重合，系统就不认作全屏。

## Changed

- `ApplyFullScreen` 不再走 WPF 的 DIU 属性定位，改用原始 `SetWindowPos` 按**物理像素**铺到当前显示器完整边界（`Screen.FromHandle(...).Bounds` = `rcMonitor`），严格边到边、覆盖本机任务栏；`GetWindowRect` 已重合则不动，避免自激。
- 进全屏后经 `Dispatcher.BeginInvoke` 重断言一次（抢在 WPF 重建窗口框架之后），并挂 `LocationChanged` / `SizeChanged` 兜底：被 WPF 重排挪离边界就 snap 回。
- 退出全屏、`_preFullScreenBounds` 精确还原逻辑不变。
- `SessionHostView` 顶沿唤出判定带 `EdgeRevealBand` 由 4 DIU 放宽到 20 DIU，且上下对称容差；作为定位残余误差的兜底，不改变正常态观感。

## Known Issues

- 无已知阻塞项。
- 全屏定位改动为窗口管理器互操作，无法用单元测试覆盖，已用隔离 Win32 探针在本机（RDP 会话 2560×1305 非标准分辨率）复现原缺陷并验证修复；建议实机再走一遍“最大化 → 进全屏 → 退出还原”与多显示器场景。

## Verification

- Build：`dotnet build -c Release --nologo` → 0 错误 / 0 警告。
- Tests：Core → 42 / 42 通过；Integration → 94 / 94 通过。
- Interop 探针：复刻“最大化 → 进全屏”序列，最大化态 `GetWindowRect` = `L-8 T-8 R2568 B1265`（WindowChrome 溢出），`SetWindowPos` snap 后 = `L0 T0 R2560 B1305`（严格命中 `rcMonitor`），经 dispatcher cycle 与 450ms 沉淀均稳定。
- Runtime：单文件 exe 拷至空目录启动 / 退出正常，无异常。
- Platform / Architecture：Windows 11 · win-x64（.NET 10 self-contained single-file）。
- Schema：数据库 schema 保持 3，无数据迁移。

## Artifacts

- `RemoteFlow-v0.8.1-win-x64.exe`
  - Size：74,704,559 bytes
  - SHA-256：`40095C25D448788955367C659A0658CFC607418F3F24C753A1CC5C61791F481D`
- `RemoteFlow-v0.8.1-win-x64.zip`
  - Size：69,023,831 bytes
  - SHA-256：`D951E769A6F4DECD5D17D382359FC75C73B4CA9E109C44D7D46D1B27617C81E5`

## Git

- Source Snapshot Commit：`a1d1ce5`（`chore: 版本号提升至 0.8.1`）；修复本体 `4629b1c`（`fix(window): 无边框全屏改用物理像素定位…`）。
- Tag：`v0.8.1`（指向 `release: RemoteFlow v0.8.1`）。
- 分支：本版仅含全屏修复，基于 `main`（v0.8.0），不含 `feature/macos-phase1` 的重构。
