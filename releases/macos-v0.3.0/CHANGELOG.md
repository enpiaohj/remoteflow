# RemoteFlow macOS v0.3.0

发布日期：2026-09-08

上一版本 macos-v0.2.0。本版聚焦三协议（RDP / SSH / VNC）的性能与手感，
并修掉一个默认分组的存量数据问题。macOS 与 Windows 为独立版本线，本版本号不与 Windows 版对齐。

## Changed

- **默认分组规则**：老用户（升级前已有分组的库）此前会把第一个真实分组静默提成
  受保护默认组、且不创建「我的设备」。现改为首启时一律新建「我的设备」作默认 + 保护，
  排在所有现有分组之前，现有分组一个不动。

## Improved — RDP

- **启用 RDP8+ 图形管线（GFX / Progressive）**：滚动、切窗口等大重绘不再整屏卡顿，
  文本走 ClearCodec 更清晰，带宽占用大幅下降，帧确认背压避免服务端甩帧。
- **脏区帧推送**：只回写画面变化的矩形，不再每帧全量拷贝（1080p 约 8MB / 帧），
  打字 / 移动鼠标等日常操作对协议解码线程的压力明显减小。
- **服务端自适应图形**（NetworkAutoDetect）：服务端测量 RTT / 带宽后动态调整图形质量与
  帧率，跨 VPN / 公网时自动降质而非硬发导致卡顿。
- **本地光标渲染**：远端光标形状在本地鼠标位置绘制，零延迟，且正确反映 I 型 / 手型 /
  忙等待等各种形状。
- **连接性能参数**：内网带宽提示 + 关闭无谓的菜单 / 拖影动画；壁纸、主题、字体平滑一律保留。

## Improved — SSH

- **xterm.js WebGL 渲染器**：终端渲染改为 GPU 加速，大输出刷屏 / 彩色日志 /
  `top`、`htop` 明显更顺，渲染 CPU 占用更低。
- **中文输入法快速输入掉字修复**：拼音 / 搜狗 / 豆包等输入法即便英文模式也对每次按键
  上报 `keyCode=229`，触发 xterm.js 的一个去重缺陷（上游未修，issue #5887）——
  快速连打时「cd 只出 c」「dir 出 dr」。本版给 xterm 打了补丁；真正的中文合成输入不受影响。
- 出向输入改为批量发送，减少逐字符 Write/Flush 开销。

## Improved — VNC

- **会话画面渲染**（同时惠及 RDP）：去掉每帧的数组钉固、色彩空间重建、整帧拷贝；
  双缓冲乒乓 + GPU 图层直引用；会话不可见（切标签 / 窗口最小化）时暂停取帧。
- **画质参数**：内网优先清晰 —— JPEG 高质量、4:4:4 不做色度抽样、低压缩级别
  （省客户端解码 CPU）。
- **本地光标渲染**：与 RDP 同款，零延迟。

## Fixed

- 预置标签种子挪出启动关键路径，缩短冷启动。
- CI 发布流水线多处修复（详见 Git 历史 `fix(ci)`）。

## Known Issues

- 提供 **arm64（Apple Silicon）** 与 **x64（Intel）** 两个独立 DMG；不提供融合 universal 二进制。
  x64 那份在 arm64 CI runner 上交叉编译。
- **Ad-hoc 签名，未公证**：首次打开需在 Finder 里右键 →「打开」，或
  `xattr -dr com.apple.quarantine /Applications/RemoteFlow.app`。
  配置 Apple「Developer ID Application」证书 + 公证凭据后，后续版本将自动带完整签名。
- RDP 动态分辨率 / GFX 需服务端为 Windows 8 / Server 2012 及以上。

## Verification

- Build：GitHub Actions `Release · macOS`（arm64 原生 + x64 交叉编译）；CI 三 job（shared / windows / macos）+ actionlint 通过。
- Tests：`RemoteFlow.Core.Tests`、`RemoteFlow.IntegrationTests`（Linux）、`IntegrationTests.Windows`、`IntegrationTests.Mac` 通过。
- Runtime：macOS 端逐项实机验证（默认分组修复、RDP GFX / 脏区 / 自适应 / 光标、SSH WebGL / 中文输入法、VNC 画质 / 光标）；Windows 端本轮未做运行时冒烟。
- Platform：macOS 13+
- Architecture：arm64 + x64（分别打包）

## Git

- Tag：macos-v0.3.0
- 范围：macos-v0.2.0..（12 个提交，`1d69b9b`..`09496b3`）

## 产物

- `RemoteFlow-v0.3.0-macos-arm64.dmg`（Apple Silicon，ad-hoc 签名）
- `RemoteFlow-v0.3.0-macos-x64.dmg`（Intel，ad-hoc 签名）
