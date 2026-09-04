# RemoteFlow UI 布局与分组优化提示词

继续当前 RemoteFlow 项目。

请先读取并遵循现有 Claude Code 全局规则、项目 `CLAUDE.md`、`README.md`、RemoteFlow 产品设计文档和当前实际代码。不要重新初始化项目，不要推翻现有架构。

本轮属于：

> UI / UX Layout Polish + Connection Grouping Enhancement

不是 UI Rewrite。

---

## 1. 本轮目标

一次完成以下内容：

1. 建立统一桌面端自适应布局规则。
2. 优化首页。
3. 优化设置页。
4. 将“导入 / 导出”彻底合并至“设置 → 数据与备份”。
5. 优化左侧导航。
6. 完成“我的连接”分组模型。
7. 支持根分组与子分组。
8. 增加新建分组、编辑/重命名分组、删除分组。
9. 增加连接移动到分组。
10. 在稳定前提下支持拖动分组、拖动连接和同级排序。
11. 保存分组展开/折叠状态。
12. 搜索时临时自动展开命中分组，清空搜索后恢复原状态。
13. 完整回归 RDP / SSH / VNC Session UI。

产品原则保持：

> 功能很多，但默认界面很简单。

---

## 2. 左侧导航正式结构

删除一级菜单：

```text
导入 / 导出
```

正式导航：

```text
连接

首页
我的连接
收藏
最近连接


管理

凭据



────────────────

● 已连接 N 个会话

设置
```

要求：

- 设置固定左下角。
- 已连接会话状态位于设置上方。
- 不增加 RDP / SSH / VNC 一级导航。
- 不增加分组、标签、历史等一级导航。
- 不为了“菜单数量”增加无意义入口。

---

## 3. 建立统一 Layout System

当前大屏存在：页面过窄、内容集中、空白过大、设置页像固定宽度 Web 表单、不同页面宽度策略不一致。

不要在各页面散落大量固定 `Width / MaxWidth / Margin` 魔法数字。

建议建立或抽象为：

```text
WorkspacePageHost
ContentPageHost
SettingsPageHost
```

或按当前架构实现等价统一布局资源。

### Workspace 页面

用于：我的连接、收藏、最近连接、RDP/SSH/VNC Session。

```text
使用可用宽度 100%
左右 Padding 24~32px
不设置传统 MaxWidth
```

有右侧详情时：

```text
中央区域：*
详情区域：300~340px
```

窄窗口时允许详情折叠或隐藏。

### Content 页面

用于：首页、凭据。

```text
MaxWidth：1400~1500px
HorizontalAlignment：Center
最小左右 Padding：24~32px
```

### Settings 页面

用于：常规、RDP、SSH、VNC、安全、数据与备份。

```text
MaxWidth：1280~1360px
HorizontalAlignment：Center
左右 Padding：32px
Section Width = Stretch
```

不要再出现只占中间 600~700px 的设置 Card。

---

## 4. 响应式规则

至少针对以下宽度：

### ≥ 1600px
- 首页双列。
- 设置内容完整展开。
- 右侧详情常驻。

### 1200~1599px
- 减少 Padding。
- 首页继续双列。
- 设置保持舒适宽度。

### 900~1199px
- 首页局部单列。
- 右详情允许隐藏。
- 连接列表减少次要列。

### < 900px
- 首页单列。
- 详情隐藏或抽屉。
- 左导航允许压缩。
- 不出现水平滚动条。

不需要做手机布局。

---

## 5. 首页重新设计

首页不是完整历史表，而是回答：

> 我现在最可能继续做什么？

只保留：

1. 欢迎/状态。
2. 最近连接。
3. 收藏。
4. 最近活动。

不要增加仪表盘、CPU/内存图、饼图、新闻、AI、拓扑。

推荐结构：

```text
下午好

共 12 个连接 · 2 个会话进行中

最近连接                                      查看全部

[ PHJ-HomePC ]
192.0.2.100
● 已连接

[ internal-fs-01 ]
192.0.2.15
17 分钟前

[ internal-dc-01 ]
192.0.2.11
2 小时前


收藏                          最近活动
┌──────────────────┐         ┌──────────────────┐
│ ★ DC01      RDP │         │ 连接 PHJ-PC  刚刚 │
│ ★ Linux01   SSH │         │ 连接 DC01   17分 │
│ ★ HomePC    RDP │         │ 更新凭据     昨天 │
└──────────────────┘         └──────────────────┘
```

大屏最近连接 3 张横排卡片，收藏/最近活动双列；窄屏改单列。

---

## 6. 设置页调整

顶部结构固定：

```text
常规 | RDP | SSH | VNC | 安全 | 数据与备份
```

设置 Section 宽度应随内容区域拉伸，而不是固定窄卡片。

例如 RDP：

```text
RDP 默认设置

┌─────────────────────────────────────────────┐
│ ☑ 画面适应窗口                              │
│   窗口尺寸变化时动态调整远程分辨率           │
│                                             │
│ ☑ 启用剪贴板重定向                          │
│                                             │
│ □ 将远程音频播放到本机                      │
│                                             │
│ □ 使用全部显示器                            │
└─────────────────────────────────────────────┘
```

外层容器自适应，内部 Dropdown/TextBox 保持合理宽度约 240~320px，不要被拉到全屏。

---

## 7. 导入/导出并入“数据与备份”

删除左侧独立“导入/导出”。

统一放：

```text
设置 → 数据与备份
```

包含：

```text
连接数据
- 导入 CSV
- 导出 CSV

凭据备份
- 导入 .rfbackup
- 导出 .rfbackup

应用数据
- 数据位置
- 打开数据目录
- 备份数据库
- 清理连接历史
```

---

## 8. 我的连接：正式分组模型

当前 Windows / Linux 不再作为写死系统分组。

正式定义：

> Group = 用户自己的连接组织目录。

Windows / Linux / macOS / RDP / SSH 等仅是连接属性。

不要引入第二套“系统分组 / 协议分组 / 标签分组 / 自动分组”。

RemoteFlow V0.x 只保留一个 Group 模型。

---

## 9. 默认根分组

第一次启动且数据库中没有用户 Group 时，自动创建：

```text
我的设备
```

规则：

- 默认展开。
- 新连接默认进入该组。
- 可重命名。
- 可创建子分组。
- 可删除。
- 属于普通用户 Group。

不要默认创建 Windows、Linux、公司、家庭、生产、测试等业务含义分组。

---

## 10. 系统兜底分组“未分组”

保留唯一系统 Group：

```text
未分组
```

规则：

- 不允许删除。
- 不允许重命名。
- 无连接时默认隐藏。
- 新连接未指定 Group 时进入这里。
- 删除普通 Group 时，组内连接默认移动到这里。

数据库/模型中必须显式区分 System Group，不允许只根据名称字符串判断。

可使用：

```text
IsSystem
SystemGroupType
```

或现有架构中等价设计。

---

## 11. 首次使用空状态

第一次启动：

```text
我的连接                                      0

[搜索名称、IP或标签] [全部协议 ▼]      + 新建连接
──────────────────────────────────────────────

⌄ 我的设备                                    0

   暂无连接
   添加 RDP、SSH 或 VNC 连接开始使用

   + 新建连接
```

不要展示完全空白页面，也不要使用大型插画。

---

## 12. 有连接后的分组效果

```text
我的连接                                      6

[搜索名称、IP或标签] [全部协议 ▼]      [+ 新建连接 ▾]
──────────────────────────────────────────────

⌄ 我的设备                                    3

  ★ PHJ-HomePC            192.0.2.100 RDP
  ☆ DC01                  192.0.2.11  RDP
  ☆ app.cygdi.cn          app.cygdi.cn   SSH

> 公司                                         2

> 实验室                                       1
```

支持子分组：

```text
⌄ 公司
   > 生产
      DC01
      Linux-App

   > 测试
      Test01

> 家庭
   PHJ-HomePC
```

技术可支持多层，但 UX 建议 2~3 层以内。

---

## 13. 新建入口

不要增加一排按钮。

优先：

```text
[ + 新建连接 ] [ ▾ ]
```

下拉：

```text
新建连接
新建分组
```

如当前 WPF SplitButton 成本较高，可保留“+ 新建连接”，旁边用轻量 `...` 提供“新建分组”。

---

## 14. 新建分组

点击“新建分组”打开小型对话框：

```text
新建分组

名称
[________________]

上级分组
[根目录                 ▼]

                    取消    创建
```

规则：

- 名称不能为空。
- Trim 前后空格。
- 同一父级下禁止完全同名。
- 创建成功后自动刷新。
- 新分组默认展开。
- 如果从某 Group 的右键菜单创建子分组，则上级分组默认当前 Group。

---

## 15. 编辑 / 重命名分组

Group 右键菜单保持克制：

```text
新建连接
新建子分组
────────────
重命名
────────────
删除分组
```

V0.x 优先使用稳定 Dialog 重命名；Inline Rename 可作为增强。

重命名后：

- Group ID 不变化。
- Connection GroupId 不变化。
- 子 Group 不变化。
- UI 即时刷新。

---

## 16. 删除分组

删除 Group 永远不能默认删除 Connection。

为保持简单，统一采用：

```text
删除 Group

Group 内所有 Connection
→ 未分组

Child Group
→ 提升到被删除 Group 的父级
```

例如：

```text
公司
├ 生产
│ └ DC01
└ Test01
```

删除“公司”后：

```text
生产
└ DC01

未分组
└ Test01
```

删除确认框不要提供五六个复杂选择。

---

## 17. 连接移动到分组

Connection 右键菜单增加：

```text
连接
编辑
────────────
移动到分组 >
收藏
────────────
删除
```

“移动到分组”展示 Group Tree：

```text
我的设备
公司
  生产
  测试
实验室
未分组
```

点击立即移动并保存。

---

## 18. 拖放与排序

如果当前 WPF 架构适合稳定实现：

- Connection 可拖动到 Group。
- Group 可拖动到 Parent Group。
- 同级 Group 可拖动排序。
- 排序使用 `SortOrder` 持久化。

但拖放属于增强，不得为了拖放重写核心 Group 架构。

右键“移动到分组”必须始终可用，作为可靠路径。

Connection 内部继续支持按名称排序，不引入复杂自定义连接排序。

---

## 19. 分组展开状态

首次创建：

```text
我的设备 = Expanded
```

以后记住用户最后一次展开/折叠状态。

例如关闭前：

```text
⌄ 公司
> 家庭
> 实验室
```

下次启动仍保持该状态。

V0.1 不增加“启动时全部展开/折叠”的额外设置项。

---

## 20. 搜索时自动展开

正常：

```text
> 公司
> 家庭
> Lab
```

搜索 `dc`：

```text
⌄ 公司
   DC01
   DC02

⌄ Lab
   Lab-DC01
```

清空 Search 后恢复用户原展开状态。

搜索不得永久修改 Group Expanded 状态。

---

## 21. Group / Tag / Favorite 职责固定

```text
Group = 放在哪里
Tag = 是什么
Favorite = 常用什么
```

例如：

```text
DC01

Group：公司 / 生产
Tag：AD、域控、生产
Favorite：★
```

不要再增加“按标签分组 / 按协议分组 / 按 OS 分组”。

---

## 22. 我的连接最终视觉要求

参考已确定的 RemoteFlow 分组概念图，但以当前实际项目为准，不要求像素级复制。

```text
我的连接                                           6

[搜索名称、IP或标签] [全部协议 ▼]       [+ 新建连接 ▾]
─────────────────────────────────────────────────────

⌄ 我的设备                                      3

   名称              主机/IP        协议    标签    最近连接

 ★ PHJ-HomePC        192.0.2.100 RDP     生产     刚刚
 ☆ DC01              192.0.2.11  RDP     测试     17分钟前
 ☆ app.cygdi.cn      app.cygdi.cn   SSH     生产     17分钟前

> 公司                                           2

> 实验室                                         1
```

右侧详情保持当前设计。

Group Header：

```text
Chevron + Folder Icon + Group Name + Count
```

推荐：

```text
Group Header 行高：44~48px
Connection Row：40~44px
```

Group Header 默认不显示一排按钮，低频操作通过右键或 Hover `...` 提供。

---

## 23. 迁移现有 Windows / Linux 分组

当前数据库可能已有 Windows / Linux Group。

不要粗暴删除。

如果它们当前就是普通 Group：

- 保留现有数据。
- 升级后允许重命名、删除、移动。
- 不再将其视为不可编辑系统分组。

首次新安装则只自动创建：

```text
我的设备
```

---

## 24. 数据库与服务层

如 Group 模型需要调整，允许增加安全 Migration，但必须保证：

- 现有 Connection 不丢。
- Group 不丢。
- Credential 不受影响。
- History 不受影响。
- Tag 不受影响。
- Migration 可重复安全运行。

不要让 ViewModel 直接操作 SQL。

通过现有架构完善：

```text
GroupRepository
GroupService
```

至少支持：

```text
Create
Rename
Delete
Move
GetTree
Reorder
```

具体命名遵循现有项目规范。

---

## 25. 自动测试

至少覆盖：

1. 创建 Group。
2. 创建 Child Group。
3. 重命名 Group。
4. 删除空 Group。
5. 删除有 Connection 的 Group。
6. Connection 自动进入未分组。
7. 删除 Parent 后 Child 正确提升。
8. System Ungrouped 不能删除。
9. System Ungrouped 不能重命名。
10. Migration 不丢现有数据。
11. Expanded 状态可持久化。
12. Group 排序可持久化（若实现）。

---

## 26. UI / DPI 回归

至少检查：

```text
2560×1440
1920×1080
1600×900
1366×768
```

DPI：

```text
100%
125%
150%
```

要求：

- 首页大屏无巨大无意义空白。
- 设置页不再过窄。
- 我的连接保持 Wide Workspace。
- 右详情比例合理。
- 窄窗口不崩。
- 无水平滚动条。

---

## 27. 协议回归

本轮 UI / Layout / Group 改动不得影响：

```text
RDP
SSH
VNC
Session Tab
RDP Fullscreen
F11
Credential
History
Search
Import / Export
```

必须至少再次实测：

```text
RDP 连接
RDP 窗口
RDP 全屏
退出全屏
关闭 Session
```

---

## 28. 本轮不要做

不要加入：

- 自动系统分类。
- OS 自动分组。
- 协议分组模式。
- 标签分组模式。
- Group 权限。
- Group 图标自定义。
- Group 颜色。
- Group 模板。
- Group 云同步。
- Group RBAC。
- Group 批量执行。
- CMDB 功能。
- 新 Dashboard。
- 新一级导航。

这些都会让 V0.x 过度复杂。

---

## 29. 最终原则

RemoteFlow 的连接组织模型正式固定为：

```text
Group
Tag
Favorite
```

其中：

```text
Group = 放在哪里
Tag = 是什么
Favorite = 常用什么
```

首次安装：

```text
⌄ 我的设备
```

默认展开。

另有隐藏系统兜底：

```text
未分组
```

仅在存在未归类连接时显示。

这套结构应让普通用户无需阅读说明即可理解。

---

## 30. 完成流程

完成后执行：

1. `dotnet clean`
2. Debug Build
3. Release Build
4. 全量 Test
5. Runtime
6. 数据 Migration 验证
7. Group CRUD 手工验证
8. RDP 回归
9. 大屏 / 小屏 Layout 验证
10. DPI 验证

最终输出：

# RemoteFlow UI / Group 优化完成报告

包含：

- Layout System
- 首页
- 设置
- 左侧导航
- 默认 Group
- 未分组
- 新建 Group
- Rename Group
- Delete Group
- Child Group
- Move Connection
- Drag & Drop
- Expand State
- Search Expand
- Migration
- 新增测试
- Build / Test
- RDP 回归
- Known Limitations
