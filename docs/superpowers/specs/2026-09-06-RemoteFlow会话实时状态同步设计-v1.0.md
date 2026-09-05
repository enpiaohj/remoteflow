# RemoteFlow 会话实时状态同步设计

## 1. 目标
SessionManager 作为**唯一实时状态事实来源**：不再让 Home / Connections / 页面局部变量各算各的 IsConnected；数据库只存配置与历史。任何会话状态变化后，所有引用该 ConnectionId 的 UI（首页统计与卡片、我的连接/收藏/最近连接行、右侧详情、左下状态栏、托盘、右键）无需切页立即同步。

## 2. SessionManager：唯一状态源 + 全局通知
### 2.1 快照查询（新增，纯内存从 `_sessions` 派生）
- `bool HasActiveSession(Guid connectionProfileId)`：该 Profile 是否存在于活动集合（含 Connecting/Connected/Failed 尚未移除）。
- `bool HasConnectedSession(Guid connectionProfileId)`：是否存在 `State==Connected` 的会话。
- `ConnectionState? GetSessionState(Guid connectionProfileId)`：取“最佳”状态（优先级 Connected > Connecting/Reconnecting > Failed > Idle/Disconnected）；无会话返回 `null`，以区分「无会话」与「存在 Idle 会话」。
- 保留 `ActiveSessionCount`（集合内会话数）、`ConnectedSessionCount`（仅 Connected）、`ActiveSessions`（列表）。

### 2.2 事件
- 保留 `SessionCreated`（建 Tab 用）、`SessionClosed`（删 Tab/托盘生命周期）。
- 新增统一聚合：
  - `event EventHandler<SessionStateChangedAggregatedEventArgs>? SessionStateChanged`（聚合参数携带 `SessionId / ConnectionProfileId / OldState / NewState / ErrorCode / ErrorMessage`，定义在 Application 层，不与 Core 的 `SessionStateChangedEventArgs` 重名以免命名空间歧义）——每条会话 `StateChanged` 都转发，转发时填上对应 Session 与 Profile 身份；
  - `event EventHandler? SessionsChanged`——作为「快照失效 / 重算信号」：会话创建、任意状态跳变、移除 后都触发（成员增减与状态变化都可能触发，一次即可覆盖 UI 重算）。
- SessionManager 内部已订阅每条会话 `StateChanged`（历史/触碰逻辑保留），在该处一并广播 `SessionStateChanged` 与 `SessionsChanged`；`CreateSessionAsync` 成功与 `CloseSessionAsync` 移除(MarkClosed) 后也广播。
- 事件可在任意线程触发（状态来自协议线程、创建/关闭多来自 UI）；消费者自行 marshal（现有 Dispatcher 模式）。
- 各 UI 不再“逐会话订阅”；MainVM/ConnectionsPage 详情/Home/Tray 只订阅 `SessionsChanged`（或状态事件）一次。

### 2.3 关闭顺序
维持现有关闭模板并确保移除后广播：Disconnecting → Cleanup → 从集合移除 → MarkClosed → 广播 SessionsChanged（再发 SessionClosed 供 UI 删 Tab）。这样计数与卡片在移除即更新，避免“Tab 没了但卡片仍已连接”。

## 3. 各 UI 读取/刷新
- **MainVM 状态栏**：删逐会话订阅字典，订阅 `SessionsChanged`；handler marshal → 刷新 `SessionStatusText/BrushKey`。`SessionCreated/Closed` 仍用于 Tab 增删。
- **ConnectionsPage 详情**：删逐会话订阅，订阅 `SessionsChanged` → `RefreshSelectedDetail()`（marshal）。
- **HomePageViewModel**：订阅 `SessionsChanged`：
  - 立即刷新 `ConnectedSessions`；
  - 用去抖（~150ms DispatcherTimer）异步 `LoadAsync()`（重建最近/收藏，使 LastConnectedDisplay 与 HasActiveSession 同步、最近排序正确）。不停机时也即时把对应卡片 `HasActiveSession` 置 true（响应 Connecting→Connected 点亮）。
- **行级实时**：
  - `ConnectionItemViewModel` 增加 `[ObservableProperty] bool isConnected`（与 hasActiveSession 语义区分：Connected 才真“已连接”）与已有 `HasActiveSession`（任意活动）。列表/卡片用 `IsConnected` 决定“● 已连接”，用 `LastConnectedDisplay` 显示时间（未连时）。
  - ConnectionsPage 行构建后在状态变化时由页面 VM 统一更新每行 `IsConnected`（SessionsChanged → 遍历 `_allItems`/Items 按 `HasConnectedSession(profileId)` 写 `IsConnected`；最近连接列：有连显示“已连接”，否则时间）。
  - 我的连接/收藏/最近连接行与首页卡片都显示同一实时状态。
- **详情状态与右键**：
  - 详情状态刷新改走 SessionsChanged。
  - 右键/详情主操作动态：无会话 →「连接」；有活动 →「切换到会话」（聚焦，已有）与「断开连接」（关闭该 Profile 的活动会话）；由 SessionManager 统一 close。

## 4. 页面生命周期与防泄漏
- VM 多为 App 单例（Home/Connections/Main 常驻），订阅 `SessionsChanged` 用实例方法并实现 `Dispose` 退订（App.OnExit 调 MainVM.Dispose 时级联 Home/Connections Dispose）；避免重复订阅/泄漏。
- Home/Connections 不依赖 `OnNavigatedTo`；`LoadAsync`（导航）仅作快照校准保险。
- 不去 DB 写 IsConnected；持久化仅配置/LastConnectedAt/历史。

## 5. 回归（自动化为主，用 FakeSession）
- SM：创建 3 → `ConnectedSessionCount=3` 且 SessionsChanged 触发；关 1 → 2；全关 → 0；失败/取消/断线/重连各状态经聚合事件；快速开关不旧覆盖新；移除后查询 `HasActiveSession` false。
- UI 无法自动，人工核对首页/详情/行/状态栏/托盘/右键一致。

## 6. 不在范围
- 改变持久化模型/协议/凭据；新增协议。
