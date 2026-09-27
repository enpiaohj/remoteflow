# RemoteFlow v0.23.0 图标体系说明

- 文档版本：v1.0
- 日期：2026-09-27
- 适用软件版本：RemoteFlow v0.23.0（Windows）；macOS 界面未改动
- 读者：RemoteFlow 用户、贡献者

---

## 1. 概览

v0.21.0 已把设备类型、协议、分组、标签换成正式矢量图标；v0.23.0 把**其余所有图标**也换掉，
不再使用 Segoe Fluent Icons 字体字形表达语义。全应用的图标分为五个家族，职责互不重叠：

| 家族 | 资源键前缀 | 形态 | 表达什么 | 所在字典 |
| --- | --- | --- | --- | --- |
| 设备 | `DeviceIcon.*` | 彩色矢量 | 这台主机是什么设备 | `DeviceIcons.xaml`（v0.21.0） |
| 协议徽章 / 资源视图 | `ProtocolIcon.*` / `ResourceIcon.*` | 彩色矢量 | RDP / SSH / VNC；全部设备 / 收藏 / 最近 | `DeviceIcons.xaml`（v0.21.0） |
| 分组 / 标签 | `GroupIcon.*` / `TagIcon.*` | 单色几何，着分组色 / 标签色 | 组织结构 | `OrganizationIcons.xaml`（v0.21.0） |
| **身份** | `Id.*` | 彩色矢量（本版新增） | 「这是什么」：凭据类型、设置分区、页面分区、对话框、空状态 | `IdentityIcons.xaml` |
| **操作** | `Ui.*` | 单色线性，颜色跟随文字 | 「做什么」：按钮、工具条、状态指示 | `UiIcons.xaml` |

一句话区分：**身份图标回答「这是什么」，用颜色与体积感帮助扫读；操作图标回答「点它做什么」，
保持单色克制，跟随按钮的悬停 / 禁用 / 选中配色。**

## 2. 用户能看到的变化

### 2.1 导航栏与页面标签

- 左侧导航栏（首页、连接工作台、凭据、设置、帮助、关于）：常态为**线性**图标，当前页换为**实心**图标并使用品牌色，
  一眼看出身在何处。
- 顶部页面标签的图标与导航栏同一规则：当前标签实心 + 品牌色，其它为线性灰。

### 2.2 身份图标出现的位置

| 位置 | 图标 |
| --- | --- |
| 凭据列表每行 | Windows 域账号（人物 + 楼宇）、本地账号（人物 + 本机笔记本角标）、SSH 口令（终端 + 口令掩码）、SSH 私钥（金色钥匙）、VNC 口令（显示器 + 锁） |
| 设置 · 常规 | 外观与行为（调色板）、日期与时间（日历 + 时钟）、启动与托盘（电源键）、全屏悬浮工具条、语言（地球 + 文字气泡） |
| 设置 · 数据与备份 | 连接数据、凭据备份、应用数据、数据安全提示、加密保护 |
| 首页 | 收藏（金星）、最近活动（时钟 + 回溯）、安全提示（盾牌） |
| 帮助 | 快速上手（闪电）、连接管理（全部设备）、会话操作、三档全屏与 F11、凭据与安全、数据与备份、云同步、快捷键速查 |
| 关于 | 版本与归属（信息）、隐私与数据（盾牌） |
| 对话框标题 | 分组（文件夹）、标签 / 标签管理（吊牌）、输入口令（钥匙） |
| 空状态 | 连接列表为空（全部设备）、凭据列表为空（钥匙） |

### 2.3 操作与状态图标

- 所有操作按钮（新建、编辑、复制、删除、刷新、搜索、批量选择、收藏、更多、导入导出、显示 / 隐藏密码等）
  改用 Fluent 线性图标，悬停、禁用、选中时颜色随按钮变化。
- 会话工具条（缩放、键盘、任务管理器、复制、粘贴、清屏、查找、全屏）、全屏药丸（固定 / 取消固定、切换会话、
  完全全屏 / 退出）改用同一套线性图标。
- **状态图标**统一为实心：已连接 = 实心对勾圆、断开 / 网络波动 = 实心警告三角、失败 = 实心错误圆、
  连接中 / 准备中 = 空心圆；颜色仍沿用全应用状态色（已连接蓝 / 连接中琥珀 / 异常红 / 中性灰）。
  覆盖会话常驻条、药丸、连接质量浮层、断线状态层、测试连接对话框的每一步与结论。
- 消息对话框与主机密钥对话框的级别图标（信息 / 成功 / 警告 / 错误）改为实心状态图标。
- 会话选择器与会话切换菜单按协议显示图标（RDP 显示器、SSH 控制台窗口、VNC 带光标的显示器）。

### 2.4 保留系统字形的地方

- 窗口右上角最小化 / 最大化 / 还原 / 关闭：与 Windows 系统标题栏保持一致的原生观感。
- 各处折叠箭头（下拉、展开、链接行右侧的 `›`）与复选框内的对勾：不承载语义，保持系统样式。

## 3. 设计规格

### 3.1 操作图标（Ui.*）

- 来源：Microsoft [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons)（MIT），20px 规格，
  Regular 为线性、Filled 为实心；只取几何路径，不引入字体或 NuGet 依赖。
- 每枚几何前缀 `F1 M0,0 M20,20`：`F1` 为非零环绕填充（钥匙等图形不会被挖空），两个空 MoveTo 把边界锚定为
  完整 20×20 画布——按 `Stretch=Uniform` 缩放时保留图标在画布内的留白，省略号、双向箭头等扁平图形不会被拉满，
  所有图标视觉大小一致。
- 颜色：`Fill` 绑定所在元素继承的 `TextElement.Foreground`，因此放进任何按钮都自动跟随按钮文字色。
- 尺寸：放在按钮 `Content` 里时按按钮 `FontSize × 1.15` 取边长（取整到 0.5px），与原字形视觉大小基本一致。

### 3.2 身份图标（Id.*）

- 与设备图标同一设计语言：64×64 画布、渐变主体、右下偏移阴影层、左上白色高光，全部为 WPF 原生
  `DrawingImage`，原创绘制，无第三方版权。
- 常用尺寸：设置分区 20px、帮助 / 关于分区 30px、对话框标题 24px、凭据列表 24px、空状态 36px；
  在 16px 下仍保持可辨识的主轮廓。
- 色板与设备图标一致：蓝 / 靛 / 紫 / 绿 / 青 / 金 / 橙 / 红 / 石板灰 / 夜色 / 纸白。

## 4. 实现要点（贡献者）

### 4.1 在 XAML 中使用

```xml
<!-- 按钮：Content 直接给 Geometry，隐式 DataTemplate 画成图标，大小跟随 FontSize -->
<Button Style="{StaticResource Button.Icon}" FontSize="14" Content="{StaticResource Ui.Edit}" ToolTip="编辑" />

<!-- 独立图标：UiIcon 样式，默认 16px、颜色跟随文字前景；需要特定颜色时设 Fill -->
<Path Style="{StaticResource UiIcon}" Data="{StaticResource Ui.Warning.Filled}" Width="14" Height="14"
      Fill="{DynamicResource Status.Warning}" />

<!-- 绑定资源键：UiIconGeometry 转换器，参数为变体后缀，变体不存在时回落基础键 -->
<Path Style="{StaticResource UiIcon}"
      Data="{Binding IconKey, Converter={StaticResource UiIconGeometry}, ConverterParameter=.Filled}" />

<!-- 身份图标：Image + DrawingImage -->
<Image Width="20" Height="20" Source="{StaticResource Id.Appearance}" RenderOptions.BitmapScalingMode="HighQuality" />
```

后台代码中取图标用 `UiIconResources.Find(key)`；协议图标键用 `UiIconResources.ProtocolKey(protocol)`。

### 4.2 共享层（Presentation）

图标键在 ViewModel 中只以**字符串资源键**出现，不引用任何 WPF 类型。本版只做加法：

| 属性 | 用途 |
| --- | --- |
| `PageTabViewModel.IconKey` | 页面标签的 Ui.* 基础键（选中时视图层自动取 `.Filled`） |
| `CredentialItemViewModel.TypeIconKey` | 凭据类型的 Id.* 键 |
| `SessionTabViewModel.StateIconKey` / `FlyoutStateIconKey` | 会话状态的 Ui.* 键 |

原有字形属性（`Icon`、`TypeIcon`、`StateIcon`、`FlyoutStateIcon`）保留不变，供 macOS 端继续使用。

### 4.3 新增一枚操作图标

1. 在 `scripts/build-ui-icons.py` 的 `ICONS` 列表加一行：`("Ui.<名称>", ["<Fluent 图标名>", "<备选名>"], <是否需要实心变体>)`。
   图标名见 Fluent UI System Icons 仓库 `assets/` 目录。
2. 运行 `python scripts/build-ui-icons.py` 重新生成 `UiIcons.xaml`（需联网拉取 SVG），`--check` 只校验键齐全。
3. 在 XAML 引用 `{StaticResource Ui.<名称>}`，运行 `UiIconSystemTests`。

### 4.4 新增一枚身份图标

在 `IdentityIcons.xaml` 按现有结构新增 `DrawingImage`（64 画布：阴影层 → 渐变主体 → 细节 → 高光）。
**不要**用 `<StaticResource x:Key="别名" ResourceKey="…"/>` 给已有图标起别名：资源字典延迟加载遇到别名条目会让
相邻键查找失败（开发中实测导致设置页因 `Id.Encryption` 找不到而无法打开）。需要同一图形时直接复制一份独立定义。

### 4.5 模板注意事项

控件模板若用 `TextBlock Text="{TemplateBinding Content}"` 显示内容，Content 换成 Geometry 后会显示成一串文字——
图标按钮的模板必须使用 `ContentPresenter`（会话药丸的「固定」按钮在开发中踩过）。

### 4.6 回归测试

`tests/RemoteFlow.IntegrationTests.Windows/UiIconSystemTests.cs`：

- 所有 Ui.* 为非空几何且边界锚定 20×20；导航用到的图标都有实心变体；
- 代码与标记中引用的 Ui.* / Id.* 键全部存在；
- `Views/` 下不再出现 `Icon.*` 字形（窗口按钮与折叠箭头除外）；
- `THIRD-PARTY-NOTICES.md` 披露 Fluent UI System Icons 并附许可证全文；
- **真实加载应用资源后实例化全部视图**（19 个页面 / 对话框 / 会话视图），任何缺失的 `StaticResource`
  都会以失败报告出来。

操作进程级 WPF `Application` 的用例（玻璃透明度、图标加载）归入同一 xUnit 集合串行执行，并在结束后还原全局资源。

## 5. 许可与合规

- Fluent UI System Icons：MIT License，Copyright (c) 2020 Microsoft Corporation。仓库根 `THIRD-PARTY-NOTICES.md`
  登记组件并附许可证全文；`UiIcons.xaml` 文件头注明来源与许可证。与本项目 GPL-3.0 兼容。
- 身份图标、设备图标、组织图标、程序图标均为原创，随本项目 GPL-3.0 发布。

## 6. 已知问题

- 会话内工具条、全屏药丸、连接质量浮层的新图标已通过「真实加载视图」集成测试与代码审查，
  但**未在实际远程会话中做实机目视验证**。
- 页面切换后新页面内容不在 UI 自动化（辅助功能）树中（既有问题，非本版引入）。
- macOS 界面未改动，仍使用 SF Symbols；共享层新增的图标键属性在 macOS 端未被使用，
  未在 Mac 上编译验证。
