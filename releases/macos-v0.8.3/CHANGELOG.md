# RemoteFlow macOS v0.8.3

发布日期：2026-09-11 · 上一版本 macos-v0.8.2

窗口与启动落位的修复 + 界面比例优化。改动集中在 `RemoteFlow.App.Mac`；
共享层（`Core` / `Presentation`）为**纯加法**，Windows 端行为与文案不变。

## 修复

- **「默认页面」设置从未生效**（macOS 独有）—— 设置里选「首页」，打开照样进「我的连接」。

  根因：`NavSidebar.SelectFirst()` 名字叫「选第一个」，实现却写死 `SelectRow(1, ...)`
  （第 1 行是「我的连接」，第 0 行才是「首页」），并且**完全没读 `DefaultLandingPage`**。
  该方法还有第二个问题：`SelectRow` 已经会经 `SelectionDidChange` 触发一次 `Selected`，
  它又显式 `Invoke` 一次，导航回调被触发两遍。

  现改为按 `DefaultLandingPage` 映射选择导航项（映射表与共享层 `MainViewModel` 一致），
  并删除 `SelectFirst()`。Windows 侧本来就走共享层映射，不受影响。

- **主窗口初始尺寸无效** —— 每次打开都是 `980×560` 的最小尺寸。

  根因：初始 `SetContentSize` 写在 `BuildSplit()` 之前，而 `BuildSplit()` 末尾的
  `Window.ContentViewController = _split` 会让窗口按内容 VC 的 `fittingSize` 重算尺寸
  并压到 `contentMinSize`，前面设的值被直接盖掉。现抽成 `ApplyInitialSizeAndCenter()`，
  移到内容设好之后调用。

## 新增

- **主窗口「初始窗口大小」预置**（设置 → 常规 → 外观与行为）：

  | 档位 | 尺寸 | 适合 |
  | --- | --- | --- |
  | 跟随屏幕（默认） | 可用区域的 72% 宽 / 78% 高 | 不确定选哪个 |
  | 大 | 1600 × 1040 | 高分辨率外接显示器 |
  | 中 | 1280 × 800 | 常规桌面 |
  | 小 | 1080 × 700 | 笔记本 / 小屏 |

  任何档位都会再夹进当前屏幕可用区域（留 40pt 余量），小屏误选「大」也不会超出边界。
  设置持久化到设置文件，改完**下次启动生效**（卡片说明里已注明）。

## 改进

- **首页在窗口拉高（尤其全屏）时不再下半屏留白**。

  原来 `ScrollHost` 里 documentView 的高度只等于内容高度，窗口比内容高时内容停在顶部。
  现在内容比视口矮时把文档撑到视口高，并由「收藏 / 最近活动」两列吸收剩余高度 ——
  卡片变高、列表视口随之变大，显示更多条目，而不是单纯拉出空白。两列卡片的 460pt
  高度上限一并去掉（否则顶到上限就不再长）。连接详情页不启用该行为，保持自然高度。

- **「测试连接」面板尺寸贴合内容**。

  原来固定 440×320：内容比它矮时 Auto Layout 只能把几行拉开填满那 320（看着松散）；
  结论带「可能原因」多行时又顶出面板底部。两种现象同一成因。现在宽度固定、高度按内容算，
  结论异步更新后（完成 / 取消 / 出错三条路径）自动重算。

## 验证

- 本地回归（本机 Intel，2026-09-11）：
  - `dotnet build -c Release`（解决方案，含 Windows 侧编译校验）—— 0 错误；
  - `Core.Tests` 42 通过；
  - `IntegrationTests` 179 通过 / 5 跳过；
  - `IntegrationTests.Mac` 10 通过；
  - `dotnet build src/RemoteFlow.App.Mac -c Release` —— 0 错误；
  - `IntegrationTests.Windows` 需 Windows 运行时，**本机未执行**（由 Windows 侧覆盖）。
- 实机验证（本机启动并截取窗口范围核对界面）：
  - 默认页面设「首页」→ 落在首页；设「我的连接」→ 落在我的连接。
    **两个值都测**，因为「首页」恰好是默认值，只测它测不出设置是否真被读取；
  - 初始窗口尺寸：「跟随屏幕」档 1844×1040、「小」档 1080×700，且确认是从设置文件读取生效；
  - Auto Layout 约束冲突：0 条。
- **未实机走查**：arm64 包无法在 Intel 构建机上运行（交叉产出），arm64 产物未做运行验证。
- 平台：macOS 13+ · 架构：arm64 + x64（分别打包）

## 已知问题

- **未签名 / 未公证**：本机无 Developer ID 证书，产物为 ad-hoc / Apple Development 签名，
  其它机器首次打开会被 Gatekeeper 拦（需右键「打开」或到「系统设置 → 隐私与安全性」放行）。
- 设置文件里若出现非法枚举值（只有手工改坏才会触发），行为不可预期 ——
  本轮测试中把 `WindowSize` 误写成数字曾导致窗口尺寸异常。正常使用不会触发。
- arm64 包未经实机验证。

## Git

- Tag：`macos-v0.8.3` · 提交范围 `macos-v0.8.2..`

## 产物

- `RemoteFlow-v0.8.3-macos-arm64.dmg` —— 待发布后补录
- `RemoteFlow-v0.8.3-macos-x64.dmg` —— 待发布后补录
