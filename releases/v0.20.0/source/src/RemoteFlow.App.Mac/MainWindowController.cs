using AppKit;
using CoreGraphics;
using Foundation;using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 主窗口 —— 原生三栏（导航 / 列表 / 详情，对齐邮件 / 备忘录）。
/// 列表绑共享 <see cref="ConnectionsPageViewModel"/>；设置走独立偏好窗口（⌘,）。
/// </summary>
public sealed class MainWindowController : NSWindowController
{
    private readonly IServiceProvider _services;
    private readonly ConnectionsPageViewModel _connectionsVm;
    private readonly SessionManager _sessions;

    private readonly NavSidebar _nav;
    private readonly ConnectionListPane _listPane;
    private readonly DetailView _detail = new();
    private readonly SessionTabBar _tabBar = new();
    private readonly SessionPillBar _pill = new();
    private bool _stageIsSession;

    /// <summary>会话画面的三档视图模式。</summary>
    private enum ViewMode
    {
        /// <summary>常规三栏：导航 + 列表 + 详情（会话在详情列，带 Tab 条）。</summary>
        Normal,
        /// <summary>窗口内全屏：折叠导航列与列表列，会话铺满窗口；窗口仍是窗口。</summary>
        WindowFull,
        /// <summary>完全全屏：在窗口内全屏基础上进入 macOS 原生全屏（整屏、菜单栏自动隐藏）。</summary>
        ScreenFull,
    }

    private ViewMode _mode = ViewMode.Normal;
    private bool _navWasCollapsed;
    private bool _listWasCollapsed;
    private NSToolbarItem? _sessionsItem;
    private HotZone _pillHotZone = null!;
    private readonly NSView _stage = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private NSView _detailRoot = null!;
    private readonly NSSplitViewController _split = new();
    private readonly NSSearchField _search = new() { PlaceholderString = "搜索连接" };
    private NSSplitViewItem? _listItem;
    private NSToolbarItem? _toggleListItem;
    /// <summary>当前页是否有可折叠的列表列（首页 / 凭据没有）。</summary>
    private bool _listApplicable = true;

    // 会话 Id → 其画面视图（SshTerminalView / VncScreenView）。多 Tab 并存。
    private readonly Dictionary<Guid, NSView> _sessionViews = new();
    private readonly Dictionary<Guid, string> _sessionNames = new();
    /// <summary>会话 Id → 它连的那条连接，会话结束后用来把详情页切回该连接的信息卡。</summary>
    private readonly Dictionary<Guid, Guid> _sessionProfileIds = new();

    private NavSidebar.Item? _currentNav;

    public MainWindowController(IServiceProvider services)
        : base(NewWindow())
    {
        _services = services;
        _sessions = services.GetRequiredService<SessionManager>();

        _connectionsVm = services.GetRequiredService<ConnectionsPageViewModel>();
        _nav = new NavSidebar();
        _listPane = new ConnectionListPane(_connectionsVm, HydrateConnectionMetaAsync);

        Window.Title = "RemoteFlow";
        Window.ContentMinSize = new CGSize(980, 560);
        Window.TitleVisibility = NSWindowTitleVisibility.Hidden;
        Window.CollectionBehavior |= NSWindowCollectionBehavior.FullScreenPrimary;

        BuildSplit();
        BuildToolbar();

        // 初始尺寸放在 ContentViewController 设好**之后** —— 见 ApplyInitialSizeAndCenter。
        ApplyInitialSizeAndCenter();

        _nav.Selected += OnNavSelected;
        _listPane.ConnectionSelected += (_, c) => ShowInfoCard(c);
        _listPane.ConnectionActivated += (_, c) => _ = OpenAsync(c.Profile, c.Name);
        _detail.ConnectRequested += (_, c) => _ = OpenAsync(c.Profile, c.Name);
        _detail.NewConnectionRequested += (_, _) => BeginNewConnection();

        var homeVm = _services.GetRequiredService<HomePageViewModel>();
        homeVm.NavigationRequested += (_, page) => NavigateTo(page);
        homeVm.ConnectionActionRequested += (_, args) => HandleHomeAction(args.Item, args.Action);

        _tabBar.TabSelected += (_, id) => ShowSessionStage(id);
        _tabBar.TabClosed += (_, id) => _ = CloseSessionAsync(id);
        _tabBar.WindowFullScreenRequested += (_, _) => EnterWindowFullScreen();
        _tabBar.ScreenFullScreenRequested += (_, _) => ToggleScreenFullScreen();

        _pill.SessionsProvider = () => _tabBar.Sessions;
        _pill.SessionPicked += (_, id) => _tabBar.RequestSelect(id);
        _pill.ExitOneLevelRequested += (_, _) => ExitOneLevel();
        _pill.ToggleScreenFullRequested += (_, _) => ToggleScreenFullScreen();
        _pill.MinimizeRequested += (_, _) => Window.Miniaturize(null);
        _pill.CloseSessionRequested += (_, _) =>
        {
            if (_activeSessionId != Guid.Empty)
            {
                _ = CloseSessionAsync(_activeSessionId);
            }
        };
        WireFullScreenNotifications();
        WirePillDismissOnContentClick();
        _sessions.SessionClosed += (_, id) =>
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() => DropSession(id));
        // 会话创建 / 状态跳变 / 移除后重画列表行，让「在线」徽标跟上。
        _sessions.SessionsChanged += (_, _) =>
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                _listPane.RefreshRowStatus();
                // 首页的「最近连接 / 收藏」卡片也要跟着亮灭 —— 它们是一次性构建的快照，
                // 只能整页重建（首页很短，重建代价可以接受）。
                if (_currentNav == NavSidebar.Item.Home)
                {
                    _detail.ShowHome(_services.GetRequiredService<HomePageViewModel>());
                }

                SyncPill();
            });

        _connectionsVm.OpenConnectionRequested += (_, profile) => _ = OpenAsync(profile, profile.Name);
        _connectionsVm.NavigationRequested += (_, page) => NavigateTo(page);

        _search.Changed += (_, _) => _listPane.ApplySearch(_search.StringValue);

        _ = StartAsync();
    }

    /// <summary>初始窗口尺寸与居中。档位取自「设置 → 常规 → 外观与行为 → 初始窗口大小」
    /// （<see cref="WindowSizePreset"/>）：高分屏 / 普通屏 / 笔记本适合的尺寸差得远，写死一个
    /// 值总有一头不合适，所以做成可选项。无论选哪档都会再夹进当前屏幕可用区域，选「大」也不会
    /// 在小屏上超出。
    ///
    /// 必须在 <c>Window.ContentViewController</c> 设好之后调用：设内容 VC 会让窗口按它的
    /// fittingSize 重算尺寸并压到 contentMinSize，放在前面设的值会被直接盖掉（表现为
    /// 每次打开都是 980×560 的最小尺寸）。</summary>
    private void ApplyInitialSizeAndCenter()
    {
        var preset = _services.GetRequiredService<AppSettings>().WindowSize;
        var visible = (Window.Screen ?? NSScreen.MainScreen)?.VisibleFrame
                      ?? new CGRect(0, 0, 1440, 900);

        nfloat width = preset switch
        {
            WindowSizePreset.Large => 1600,
            WindowSizePreset.Medium => 1280,
            WindowSizePreset.Compact => 1080,
            _ => visible.Width * (nfloat)0.72,  // 跟随屏幕
        };
        nfloat height = preset switch
        {
            WindowSizePreset.Large => 1040,
            WindowSizePreset.Medium => 800,
            WindowSizePreset.Compact => 700,
            _ => visible.Height * (nfloat)0.78, // 跟随屏幕
        };

        if (width < 980) { width = 980; }
        if (height < 560) { height = 560; }
        var maxWidth = visible.Width - 40;
        if (width > maxWidth) { width = maxWidth; }
        var maxHeight = visible.Height - 40;
        if (height > maxHeight) { height = maxHeight; }

        Window.SetContentSize(new CGSize(width, height));
        Window.Center();
    }

    private Task StartAsync()
    {
        // 启动落在「设置 → 常规 → 外观与行为 → 默认页面」选的那一页 —— 映射与共享层
        // MainViewModel 保持一致。原来调 _nav.SelectFirst()：它名字叫「第一个」，内部却写死
        // 选中第 1 行「我的连接」，于是「默认页面」这个设置在 macOS 端从来没生效过
        // （选「首页」照样进「我的连接」）。
        var landing = _services.GetRequiredService<AppSettings>().DefaultLandingPage;
        _nav.Select(landing switch
        {
            LandingPage.Connections => NavSidebar.Item.Connections,
            LandingPage.Favorites => NavSidebar.Item.Favorites,
            LandingPage.Recent => NavSidebar.Item.Recent,
            LandingPage.Credentials => NavSidebar.Item.Credentials,
            _ => NavSidebar.Item.Home,
        });
        return Task.CompletedTask;
    }

    private static NSWindow NewWindow() => new(
        new CGRect(0, 0, 1160, 720),
        NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable
            | NSWindowStyle.Miniaturizable | NSWindowStyle.FullSizeContentView
            | NSWindowStyle.UnifiedTitleAndToolbar,
        NSBackingStore.Buffered,
        deferCreation: false);

    private void BuildSplit()
    {
        var navItem = NSSplitViewItem.CreateSidebar(_nav);
        navItem.MinimumThickness = 176;
        navItem.MaximumThickness = 260;
        navItem.CanCollapse = true;
        navItem.HoldingPriority = 260; // 固定宽度
        _split.AddSplitViewItem(navItem);

        _listItem = NSSplitViewItem.FromViewController(_listPane);
        _listItem.MinimumThickness = 240;
        _listItem.MaximumThickness = 460;
        _listItem.CanCollapse = true;
        _listItem.HoldingPriority = 260; // 固定宽度 —— 折叠时让详情列吃掉空出的宽度，而不是缩窗口
        _split.AddSplitViewItem(_listItem);

        // 详情区 = [会话 Tab 条（空时隐藏）] + [舞台：详情卡 / 会话画面]。
        var detailRoot = _detailRoot = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        detailRoot.AddSubview(_tabBar);
        detailRoot.AddSubview(_stage);
        _tabBar.Hidden = true;

        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _tabBar.TopAnchor.ConstraintEqualTo(detailRoot.SafeAreaLayoutGuide.TopAnchor),
            _tabBar.LeadingAnchor.ConstraintEqualTo(detailRoot.LeadingAnchor),
            _tabBar.TrailingAnchor.ConstraintEqualTo(detailRoot.TrailingAnchor),
            _stage.LeadingAnchor.ConstraintEqualTo(detailRoot.LeadingAnchor),
            _stage.TrailingAnchor.ConstraintEqualTo(detailRoot.TrailingAnchor),
            _stage.BottomAnchor.ConstraintEqualTo(detailRoot.BottomAnchor),
        });
        _stageTop = _stage.TopAnchor.ConstraintEqualTo(detailRoot.SafeAreaLayoutGuide.TopAnchor);
        _stageTopWithTabs = _stage.TopAnchor.ConstraintEqualTo(_tabBar.BottomAnchor);
        _stageTop.Active = true;

        // 全屏悬浮药丸 + 顶沿唤出热区（都盖在舞台之上，热区不吃点击）。
        _pillHotZone = new HotZone(
            onEnter: () => { if (_mode != ViewMode.Normal && _tabBar.Count > 0) _pill.Reveal(); },
            onExit: () => _pill.MaybeHide());
        detailRoot.AddSubview(_pillHotZone);
        detailRoot.AddSubview(_pill);
        _pill.CenterOffset = _pill.CenterXAnchor.ConstraintEqualTo(_stage.CenterXAnchor);
        _pill.CenterOffset.Active = true;
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _pillHotZone.LeadingAnchor.ConstraintEqualTo(_stage.LeadingAnchor),
            _pillHotZone.TrailingAnchor.ConstraintEqualTo(_stage.TrailingAnchor),
            _pillHotZone.TopAnchor.ConstraintEqualTo(_stage.TopAnchor),
            _pillHotZone.HeightAnchor.ConstraintEqualTo(6),

            _pill.TopAnchor.ConstraintEqualTo(_stage.TopAnchor),
        });

        // 详情列必须"乐意被拉宽"，否则 NSSplitViewController 会按它的 fittingSize
        // 当最大厚度，窗口就出现宽度锁定。
        detailRoot.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _stage.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);

        ShowStage(_detail);

        var detailVc = new NSViewController { View = detailRoot };
        var detailItem = NSSplitViewItem.FromViewController(detailVc);
        detailItem.MinimumThickness = 420;
        detailItem.MaximumThickness = 100_000; // 显式无上限：不设的话 NSSplitViewController 只让
                                               // 详情列长到 max(min, fittingSize)，窗口就拉不宽
        detailItem.HoldingPriority = 250;      // 最低 —— 窗口 / 折叠变化时优先由详情列伸缩
        _split.AddSplitViewItem(detailItem);

        Window.ContentViewController = _split;
    }

    /// <summary>
    /// 会话视图模式切换。三档：
    ///   Normal      常规三栏（会话在详情列，带 Tab 条）
    ///   WindowFull  窗口内全屏 —— 折叠导航列 + 列表列 + 隐藏工具栏，会话铺满窗口
    ///   ScreenFull  完全全屏 —— 在上者基础上进 macOS 原生全屏
    /// </summary>
    private void SetViewMode(ViewMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        var wasNormal = _mode == ViewMode.Normal;
        var goingNormal = mode == ViewMode.Normal;

        // 离开常规模式时记住左侧两列原本的折叠状态，回来时照原样还原。
        if (wasNormal && !goingNormal)
        {
            _navWasCollapsed = NavItem?.Collapsed ?? false;
            _listWasCollapsed = _listItem?.Collapsed ?? false;
        }

        _mode = mode;

        // 只有「舞台正在显示会话画面」才隐藏左侧两列与工具栏；
        // 在首页 / 凭据等页面按 ⌃⌘F，就只是普通的原生全屏，界面保持完整。
        var chromeHidden = mode != ViewMode.Normal && _stageIsSession;
        if (NavItem is { } nav)
        {
            nav.Collapsed = chromeHidden || (goingNormal && _navWasCollapsed);
        }

        if (_listItem is not null)
        {
            _listItem.Collapsed = chromeHidden || (goingNormal && _listWasCollapsed);
        }

        if (Window.Toolbar is { } tb)
        {
            tb.Visible = !chromeHidden;
        }

        // 全屏顶部那条白线：工具栏一藏，系统仍会在标题栏下沿画一条 hairline 分隔线
        // （NSTitlebarSeparatorStyle 默认 Automatic）。会话画面顶到屏幕边缘时它就格外扎眼。
        // 隐藏 chrome 时关掉分隔线并让标题栏透明，恢复时再交还系统。
        Window.TitlebarSeparatorStyle = chromeHidden
            ? NSTitlebarSeparatorStyle.None
            : NSTitlebarSeparatorStyle.Automatic;
        Window.TitlebarAppearsTransparent = chromeHidden;

        SyncTabBarVisibility();
        SyncPill();
        SyncSessionMatte();

        // 原生全屏只在 ScreenFull 这一档开启。
        var wantNative = mode == ViewMode.ScreenFull;
        if (wantNative != IsNativeFullScreen)
        {
            Window.ToggleFullScreen(null);
        }
    }

    /// <summary>
    /// 会话画面四周的衬底：常规三栏用页面底衬（画面像嵌在页面里的一张图），
    /// 两档全屏用纯黑。VNC 的桌面分辨率由服务端定死、比例对不上必然留边，
    /// 纯黑衬底压在窗口模式下看着像渲染坏了。
    /// </summary>
    private void SyncSessionMatte()
    {
        var cinematic = _mode != ViewMode.Normal;
        foreach (var v in _sessionViews.Values)
        {
            (v as VncScreenView)?.SetCinematicMatte(cinematic);
            (v as RdpScreenView)?.SetCinematicMatte(cinematic);
        }
    }

    internal bool IsNativeFullScreen
        => (Window.StyleMask & NSWindowStyle.FullScreenWindow) == NSWindowStyle.FullScreenWindow;

    /// <summary>侧栏（导航列）当前是否折叠 —— 供「显示」菜单动态切换「显示 / 隐藏边栏」标题。</summary>
    internal bool IsSidebarCollapsed => NavItem?.Collapsed ?? true;

    /// <summary>当前页面是否有可折叠的列表列（首页 / 凭据页没有）—— 供「显示」菜单控制启用。</summary>
    internal bool IsListApplicable => _listApplicable;

    /// <summary>列表列当前是否可见 —— 供「显示」菜单动态切换「显示 / 隐藏连接列表」标题。</summary>
    internal bool IsListVisible => _listApplicable && !(_listItem?.Collapsed ?? true);

    private NSSplitViewItem? NavItem => _split.SplitViewItems.Length > 0 ? _split.SplitViewItems[0] : null;

    /// <summary>「全屏」入口：常规 → 窗口内全屏 → 完全全屏，逐档进；药丸上的退出键逐档退。</summary>
    public void EnterWindowFullScreen() => SetViewMode(ViewMode.WindowFull);

    public void ToggleScreenFullScreen()
        => SetViewMode(_mode == ViewMode.ScreenFull ? ViewMode.WindowFull : ViewMode.ScreenFull);

    /// <summary>逐档退出：完全全屏 → 窗口内全屏 → 常规三栏。</summary>
    public void ExitOneLevel()
        => SetViewMode(_mode == ViewMode.ScreenFull ? ViewMode.WindowFull : ViewMode.Normal);

    /// <summary>系统侧（绿灯 / Esc / 调度中心）进出原生全屏时，把内部档位对齐。</summary>
    private readonly List<NSObject> _windowObservers = new();

    /// <summary>全屏下监听画面点击：点在药丸之外就立刻收起工具条（见 <see cref="SessionPillBar.DismissForContentClick"/>）。</summary>
    private NSObject? _pillDismissMonitor;

    private void WirePillDismissOnContentClick()
    {
        _pillDismissMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(
            NSEventMask.LeftMouseDown | NSEventMask.RightMouseDown,
            evt =>
            {
                if (_mode != ViewMode.Normal && !_pill.Hidden
                    && evt.Window is not null && evt.Window == Window)
                {
                    var p = _detailRoot.ConvertPointFromView(evt.LocationInWindow, null);
                    if (_pill.HitTest(p) is null)
                    {
                        _pill.DismissForContentClick();
                    }
                }

                return evt;
            });
    }

    private void WireFullScreenNotifications()
    {
        var nc = NSNotificationCenter.DefaultCenter;
        _windowObservers.Add(nc.AddObserver(NSWindow.DidEnterFullScreenNotification, _ =>
        {
            if (_mode != ViewMode.ScreenFull)
            {
                SetViewMode(ViewMode.ScreenFull);
            }
        }, Window));
        _windowObservers.Add(nc.AddObserver(NSWindow.DidExitFullScreenNotification, _ =>
        {
            if (_mode == ViewMode.ScreenFull)
            {
                SetViewMode(ViewMode.WindowFull);
            }
        }, Window));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var o in _windowObservers)
            {
                NSNotificationCenter.DefaultCenter.RemoveObserver(o);
            }

            _windowObservers.Clear();

            if (_pillDismissMonitor is not null)
            {
                NSEvent.RemoveMonitor(_pillDismissMonitor);
                _pillDismissMonitor = null;
            }
        }

        base.Dispose(disposing);
    }

    private void SyncPill()
    {
        if (_mode == ViewMode.Normal || _tabBar.Count == 0)
        {
            _pill.ForceHide();
            return;
        }

        var active = _tabBar.Sessions.FirstOrDefault(x => x.Id == _tabBar.ActiveId);
        if (active.Id != Guid.Empty)
        {
            var s = _sessions.ActiveSessions.FirstOrDefault(x => x.SessionId == active.Id);
            var host = s?.Profile is { } p
                ? (p.Port > 0 ? $"{p.Host}:{p.Port}" : p.Host)
                : string.Empty;
            var connected = s?.State == RemoteFlow.Core.Models.ConnectionState.Connected;
            _pill.SetSession(active.Title, active.Protocol, host, connected);
        }

        _pill.SetScreenFull(_mode == ViewMode.ScreenFull);
        _pill.Reveal(); // 进全屏先露一下告诉用户工具条在哪；未固定则移开鼠标后收起
    }

    /// <summary>顶沿唤出热区：只感知鼠标进出，不拦截点击（HitTest 返回 null）。</summary>
    private sealed class HotZone : NSView
    {
        private readonly Action _onEnter;
        private readonly Action _onExit;
        private NSTrackingArea? _tracking;

        public HotZone(Action onEnter, Action onExit)
        {
            _onEnter = onEnter;
            _onExit = onExit;
            TranslatesAutoresizingMaskIntoConstraints = false;
        }

        public override NSView? HitTest(CGPoint aPoint) => null;

        public override void UpdateTrackingAreas()
        {
            base.UpdateTrackingAreas();
            if (_tracking is not null)
            {
                RemoveTrackingArea(_tracking);
            }

            _tracking = new NSTrackingArea(Bounds,
                NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow,
                this, null);
            AddTrackingArea(_tracking);
        }

        public override void MouseEntered(NSEvent theEvent) => _onEnter();

        public override void MouseExited(NSEvent theEvent) => _onExit();
    }

    private NSLayoutConstraint _stageTop = null!;
    private NSLayoutConstraint _stageTopWithTabs = null!;


    private void SyncTabBarVisibility()
    {
        // 只有「舞台正在显示会话画面」时才出现 Tab 条 —— 首页 / 凭据 / 连接详情上
        // 挂一条会话标签既突兀也没用。全屏下也不显示（改用悬浮药丸）。
        var show = _tabBar.Count > 0 && _stageIsSession && _mode == ViewMode.Normal;
        _tabBar.Hidden = !show;
        // 顺序要紧：先停用旧的再启用新的，两条同时生效会互相冲突。
        if (show)
        {
            _stageTop.Active = false;
            _stageTopWithTabs.Active = true;
        }
        else
        {
            _stageTopWithTabs.Active = false;
            _stageTop.Active = true;
        }

        // 立刻走一次布局：约束换完不强制排版的话，全屏下切会话时舞台会停在
        // 「给 Tab 条让出 38pt」的旧位置，顶上留一条白边，直到别的什么触发了重排。
        _detailRoot?.LayoutSubtreeIfNeeded();
    }

    /// <summary>
    /// 把页面内容放进舞台，用 Auto Layout 四边贴死 —— 会话画面必须**精确**填满舞台，
    /// 否则 RDP/VNC 画面四周会露出底色（表现为黑边）。
    /// （曾一度改用 autoresizing 来躲"窗口宽度锁定"，但那个问题的真因是
    /// NSButton.CreateButton 默认 TAMIC=true 与显式约束打架，已在别处修掉。）
    /// </summary>
    private void ShowStage(NSView view)
    {
        foreach (var v in _stage.Subviews.ToArray())
        {
            v.RemoveFromSuperview();
        }

        // 舞台上只有「会话画面」和「详情视图」两类，据此决定是否显示 Tab 条。
        _stageIsSession = !ReferenceEquals(view, _detail);

        view.TranslatesAutoresizingMaskIntoConstraints = false;
        _stage.AddSubview(view);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            view.TopAnchor.ConstraintEqualTo(_stage.TopAnchor),
            view.LeadingAnchor.ConstraintEqualTo(_stage.LeadingAnchor),
            view.TrailingAnchor.ConstraintEqualTo(_stage.TrailingAnchor),
            view.BottomAnchor.ConstraintEqualTo(_stage.BottomAnchor),
        });
    }

    /// <summary>首页卡片右键动作 → 桥接到「我的连接」既有命令（对齐 Windows MainViewModel）。</summary>
    private void HandleHomeAction(ConnectionItemViewModel item, string action)
    {
        // 尽量用「我的连接」里同一 Profile 的实例（命令内部可能按引用回选）。
        var target = _connectionsVm.Items.FirstOrDefault(x => x.Id == item.Id) ?? item;
        switch (action)
        {
            case HomeRowActions.Connect:
                _ = OpenAsync(item.Profile, item.Name);
                break;
            case HomeRowActions.Disconnect:
                _ = _connectionsVm.DisconnectItemCommand.ExecuteAsync(target);
                break;
            case HomeRowActions.Edit:
                _ = _connectionsVm.EditCommand.ExecuteAsync(target);
                break;
            case HomeRowActions.Test:
                _ = _connectionsVm.TestConnectionCommand.ExecuteAsync(target);
                break;
            case HomeRowActions.Favorite:
                _ = _connectionsVm.ToggleFavoriteCommand.ExecuteAsync(target);
                break;
            case HomeRowActions.Manage:
                NavigateTo(NavigationPage.Connections);
                break;
        }
    }

    /// <summary>连接加载后回填凭据名（VM 不直接依赖凭据服务，见 ApplyCredentialNames）。</summary>
    private async Task HydrateConnectionMetaAsync()
    {
        try
        {
            var creds = await _services.GetRequiredService<ICredentialRepository>().GetAllAsync();
            _connectionsVm.ApplyCredentialNames(creds.ToDictionary(x => x.Id, x => x.Name));
        }
        catch
        {
            // 凭据名回填失败不阻塞列表；详情页会显示「未指定」。
        }
    }

    private void ShowInfoCard(ConnectionItemViewModel c)
    {
        _tabBar.ClearHighlight();
        _connectionsVm.SelectedItem = c; // 触发历史 / 迷你图 / 状态的异步加载
        _detail.ShowConnection(_connectionsVm);
        ShowStage(_detail);
    }

    private void ShowDetailStage()
    {
        _tabBar.ClearHighlight();
        ShowStage(_detail);
        SyncTabBarVisibility();
    }

    /// <summary>工具栏「会话」按钮：有会话才可用，点一下从任意页面回到当前会话。</summary>
    private void SyncSessionsItem()
    {
        if (_sessionsItem is null)
        {
            return;
        }

        var n = _tabBar.Count;
        _sessionsItem.Enabled = n > 0;
        _sessionsItem.Label = n > 0 ? $"会话 {n}" : "会话";
        _sessionsItem.ToolTip = n > 0 ? $"回到会话（共 {n} 个）" : "当前没有会话";
    }

    /// <summary>从任意页面回到会话画面。</summary>
    public void ReturnToSessions()
    {
        if (_tabBar.Count == 0)
        {
            return;
        }

        var id = _tabBar.ActiveId != Guid.Empty ? _tabBar.ActiveId : _tabBar.Sessions[^1].Id;
        ShowSessionStage(id);
    }

    private void ShowSessionStage(Guid id)
    {
        if (_sessionViews.TryGetValue(id, out var view))
        {
            _activeSessionId = id;
            _tabBar.HighlightOnly(id);
            ShowStage(view);
            SyncTabBarVisibility();
            SyncSessionsItem();
            SyncPill();
            if (_sessionNames.TryGetValue(id, out var n))
            {
                Window.Title = $"{n} — RemoteFlow";
            }
        }
    }

    private void BuildToolbar()
    {
        var toolbar = new NSToolbar("rf.main")
        {
            Delegate = new ToolbarDelegate(this),
            DisplayMode = NSToolbarDisplayMode.Icon,
            AllowsUserCustomization = false,
        };
        Window.Toolbar = toolbar;
        Window.ToolbarStyle = NSWindowToolbarStyle.Unified;
    }

    /// <summary>中间列表列折叠 / 展开（⌘⌥L / 工具栏按钮）。左侧导航列走系统 toggleSidebar。</summary>
    public void ToggleListPane()
    {
        if (_listApplicable && _listItem is not null)
        {
            SetListVisible(_listItem.Collapsed);
        }
    }

    private sealed class ToolbarDelegate : NSToolbarDelegate
    {
        private const string NewConn = "rf.new";
        private const string Sessions = "rf.sessions";
        private const string ToggleList = "rf.togglelist";
        private const string Search = "rf.search";
        private readonly MainWindowController _o;
        public ToolbarDelegate(MainWindowController o) => _o = o;

        public override string[] DefaultItemIdentifiers(NSToolbar t) => new[]
        {
            NSToolbar.NSToolbarToggleSidebarItemIdentifier,
            NSToolbar.NSToolbarSidebarTrackingSeparatorItemIdentifier,
            ToggleList,
            NewConn,
            Sessions,
            NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
            Search,
        };

        public override string[] AllowedItemIdentifiers(NSToolbar t) => DefaultItemIdentifiers(t);

        public override NSToolbarItem? WillInsertItem(NSToolbar toolbar, string id, bool willInsert)
        {
            switch (id)
            {
                case NewConn:
                    var item = new NSToolbarItem(NewConn)
                    {
                        Label = "新建连接",
                        ToolTip = "新建连接（Cmd N）",
                        Image = NSImage.GetSystemSymbol("plus", null),
                        Bordered = true,
                    };
                    item.Activated += (_, _) => _o.BeginNewConnection();
                    return item;
                case ToggleList:
                    var toggle = new NSToolbarItem(ToggleList)
                    {
                        Label = "列表",
                        ToolTip = "显示 / 隐藏连接列表（⌘⌥L）",
                        Image = NSImage.GetSystemSymbol("sidebar.squares.left", null)
                                ?? NSImage.GetSystemSymbol("sidebar.left", null),
                        Bordered = true,
                    };
                    toggle.Activated += (_, _) => _o.ToggleListPane();
                    // 关掉自动校验：否则系统每轮事件按「target 是否响应 action」把它重新启用。
                    toggle.Autovalidates = false;
                    toggle.Enabled = _o._listApplicable;
                    _o._toggleListItem = toggle;
                    return toggle;
                case Sessions:
                    var ses = new NSToolbarItem(Sessions)
                    {
                        Label = "会话",
                        ToolTip = "当前没有会话",
                        Image = NSImage.GetSystemSymbol("macwindow.on.rectangle", null)
                                ?? NSImage.GetSystemSymbol("rectangle.stack", null),
                        Bordered = true,
                    };
                    ses.Activated += (_, _) => _o.ReturnToSessions();
                    ses.Autovalidates = false;
                    ses.Enabled = false;
                    _o._sessionsItem = ses;
                    return ses;
                case Search:
                    return new NSSearchToolbarItem(Search) { SearchField = _o._search };
                default:
                    return null;
            }
        }
    }

    private void OnNavSelected(object? sender, NavSidebar.Item item)
    {
        if (_currentNav == item)
        {
            return;
        }

        _currentNav = item;

        SetViewMode(ViewMode.Normal); // 从会话切到普通页面，先退出全屏形态
        _tabBar.ClearHighlight();
        ShowStage(_detail);
        SyncTabBarVisibility();

        switch (item)
        {
            case NavSidebar.Item.Connections:
                SetListApplicable(true);
                SetListVisible(true);
                _detail.ShowEmpty();
                _ = _listPane.ShowConnectionsAsync();
                break;
            case NavSidebar.Item.Favorites:
                SetListApplicable(true);
                SetListVisible(true);
                _detail.ShowEmpty();
                _ = _listPane.ShowFavoritesAsync();
                break;
            case NavSidebar.Item.Recent:
                SetListApplicable(true);
                SetListVisible(true);
                _detail.ShowEmpty();
                _ = _listPane.ShowRecentAsync();
                break;
            case NavSidebar.Item.Home:
                SetListApplicable(false);
                SetListVisible(false); // 首页不显示「我的连接」列
                _detail.ShowHome(_services.GetRequiredService<HomePageViewModel>());
                break;
            case NavSidebar.Item.Credentials:
                SetListApplicable(false);
                SetListVisible(false);
                _detail.ShowCredentials(_services.GetRequiredService<CredentialsPageViewModel>());
                break;
            case NavSidebar.Item.Settings:
                SetListApplicable(false);
                SetListVisible(false);
                _detail.ShowSettings(_services.GetRequiredService<SettingsPageViewModel>());
                break;
        }
    }

    /// <summary>切页时更新「列表折叠按钮 / ⌘⌥L」是否可用：首页 / 凭据没有列表列，禁用。</summary>
    private void SetListApplicable(bool applicable)
    {
        _listApplicable = applicable;
        if (_toggleListItem is not null)
        {
            _toggleListItem.Enabled = applicable;
        }

        if (NSApplication.SharedApplication.Delegate is AppDelegate app && app.ToggleListMenuItem is { } mi)
        {
            mi.Enabled = applicable;
        }
    }

    private void SetListVisible(bool visible)
    {
        if (_listItem is null || _listItem.Collapsed == !visible)
        {
            return;
        }

        _listItem.Collapsed = !visible;
    }

    /// <summary>⌘, / 菜单「设置…」：切到导航栏的「设置」页，而不是另开偏好窗口。</summary>
    public void OpenSettings() => NavigateTo(NavigationPage.Settings);

    public async void BeginNewConnection()
    {
        try
        {
            await _connectionsVm.CreateConnectionAsync();
            await _listPane.RefreshAsync();
        }
        catch (Exception ex)
        {
            _detail.ShowError("新建连接失败", ex.Message);
        }
    }

    /// <summary>菜单「断开会话」⌘⇧W：关闭当前 Tab 的会话。</summary>
    public async void DisconnectCurrentSession()
    {
        // async void：异常逃逸出去就是进程级崩溃，这里必须兜住。
        try
        {
            if (_activeSessionId != Guid.Empty)
            {
                await CloseSessionAsync(_activeSessionId);
            }
        }
        catch (Exception ex)
        {
            _detail.ShowError("断开会话失败", ex.Message);
        }
    }

    private void NavigateTo(NavigationPage page)
    {
        var item = page switch
        {
            NavigationPage.Home => NavSidebar.Item.Home,
            NavigationPage.Favorites => NavSidebar.Item.Favorites,
            NavigationPage.Recent => NavSidebar.Item.Recent,
            NavigationPage.Credentials => NavSidebar.Item.Credentials,
            NavigationPage.Settings => NavSidebar.Item.Settings,
            _ => NavSidebar.Item.Connections,
        };
        _nav.Select(item);
    }

    private async Task OpenAsync(ConnectionProfile profile, string name)
    {
        // RDP：内嵌 FreeRDP 可用则走会话 / Tab（同 SSH/VNC）；否则回落外部客户端。
        if (profile.Protocol == ProtocolType.Rdp
            && !_sessions.IsProtocolAvailable(ProtocolType.Rdp, out _))
        {
            try
            {
                RemoteFlow.Core.Models.ResolvedCredential? cred = null;
                if (profile.CredentialId is { } cid)
                {
                    cred = await _services.GetRequiredService<CredentialService>().ResolveAsync(cid);
                }

                using (cred)
                {
                    var note = RdpLauncher.Launch(profile, cred);
                    _detail.ShowSessionInfo(name, "RDP 会话已启动（外部客户端）", note);
                }
            }
            catch (Exception ex)
            {
                _detail.ShowError(ex.Message);
            }

            return;
        }

        // 已有同一连接的活动会话 → 切到它的 Tab，不重复建。
        var existing = _sessions.ActiveSessions.FirstOrDefault(s => s.Profile.Id == profile.Id);
        if (existing is not null && _sessionViews.ContainsKey(existing.SessionId))
        {
            ShowSessionStage(existing.SessionId);
            return;
        }

        _detail.ShowConnecting(name);
        ShowStage(_detail);
        try
        {
            var session = await _sessions.CreateSessionAsync(profile);

            // RDP「适应窗口」：按**会话实际要渲染的舞台区域**请求桌面尺寸。
            // 这里若按整块屏幕的比例要（旧做法），三栏模式下舞台是竖长条，比例对不上，
            // 画面就会被 letterbox 出上下黑边。连上之后 RdpScreenView 还会随视图尺寸
            // 变化继续做 DynamicResolutionUpdate。
            if (session is RemoteFlow.Protocol.Rdp.Mac.RdpSession rdpSession
                && profile.Rdp is { DisplayMode: not RemoteFlow.Core.Models.RdpDisplayMode.FixedResolution })
            {
                var stage = _stage.Bounds.Size;

                // 关键：这条会话建立后，若会话标签条从无到有，舞台会**变矮** 38pt。
                // 按当前（标签条还没出现的）高度去要桌面尺寸，连上后画面就比视图高一截，
                // 等比缩放后左右各留一道黑边 —— 实测正是 748 vs 710 差的这 38。
                var tabBarWillAppear = _tabBar.Count == 0;
                var usableHeight = stage.Height - (tabBarWillAppear ? SessionTabBar.BarHeightPoints : 0);

                var scale = Window.BackingScaleFactor;
                double w, h;
                if (stage.Width >= 320 && usableHeight >= 240)
                {
                    w = (double)stage.Width * scale;
                    h = (double)usableHeight * scale;
                }
                else
                {
                    var s = (Window.Screen ?? NSScreen.MainScreen)?.Frame.Size ?? new CGSize(1680, 1050);
                    w = (double)s.Width;
                    h = (double)s.Height;
                }

                if (w > 2560)
                {
                    h = h * 2560 / w;
                    w = 2560;
                }

                rdpSession.PreferredSize = ((int)Math.Round(w / 2) * 2, (int)Math.Round(h / 2) * 2);
            }

            // 不套人为总超时：各协议自带连接超时（SSH ConnectionInfo.Timeout / RDP 线程），
            // 且首次连接的主机密钥确认框会停在中途，硬 cap 会把用户读指纹的时间也算进去。
            await session.ConnectAsync(CancellationToken.None);

            if (session.State != RemoteFlow.Core.Models.ConnectionState.Connected)
            {
                _detail.ShowError(
                    RemoteFlow.Presentation.ConnectionErrorText.Title(session.ErrorCode),
                    RemoteFlow.Presentation.ConnectionErrorText.Describe(session.ErrorCode, session.ErrorMessage));
                await _sessions.CloseSessionAsync(session.SessionId);
                return;
            }

            NSView view = session switch
            {
                RemoteFlow.Protocol.Ssh.SshSession ssh => _detail.MakeSshTerminal(ssh),
                RemoteFlow.Protocol.Vnc.VncSession vnc => _detail.MakeVncScreen(vnc),
                RemoteFlow.Protocol.Rdp.Mac.RdpSession rdp => _detail.MakeRdpScreen(rdp),
                _ => _detail.MakeSessionPlaceholder(name),
            };
            switch (view)
            {
                case SshTerminalView st:
                    st.ReconnectRequested += (_, _) => _ = OpenAsync(profile, name);
                    break;
                case VncScreenView vv:
                    vv.ReconnectRequested += (_, _) => _ = OpenAsync(profile, name);
                    break;
                case RdpScreenView rv:
                    rv.ReconnectRequested += (_, _) => _ = OpenAsync(profile, name);
                    break;
            }

            (view as VncScreenView)?.SetCinematicMatte(_mode != ViewMode.Normal);
            (view as RdpScreenView)?.SetCinematicMatte(_mode != ViewMode.Normal);
            _sessionViews[session.SessionId] = view;
            _sessionNames[session.SessionId] = name;
            _sessionProfileIds[session.SessionId] = profile.Id;
            _tabBar.AddTab(session.SessionId, name, profile.Protocol);
            SyncTabBarVisibility();
            SyncSessionsItem();
            ShowSessionStage(session.SessionId);
            Window.Title = $"{name} — RemoteFlow";
        }
        catch (Exception ex)
        {
            _detail.ShowError(ex.Message);
        }
    }

    private async Task CloseSessionAsync(Guid id)
    {
        try
        {
            await _sessions.CloseSessionAsync(id);
        }
        catch
        {
            // 收尾尽力而为。
        }

        DropSession(id);
    }

    /// <summary>会话（本地关 / 远端断）后清理 Tab 与视图。</summary>
    private void DropSession(Guid id)
    {
        if (_sessionViews.Remove(id, out var view))
        {
            (view as SshTerminalView)?.Detach();
            (view as VncScreenView)?.Detach();
            (view as RdpScreenView)?.Detach();
            view.RemoveFromSuperview();
        }

        _sessionNames.Remove(id);
        _sessionProfileIds.Remove(id, out var closedProfileId);

        // 全屏是「用户对当前这个会话」做的操作，不该被下一个会话继承：
        // 主动点标签 / 药丸菜单切会话时保持全屏（那是明确意图），但因为**关闭**当前
        // 会话而被动跳到另一个会话时，先退回常规三栏 —— 用户从没对那个会话要过全屏。
        var wasShowing = _activeSessionId == id;
        if (wasShowing && _mode != ViewMode.Normal)
        {
            SetViewMode(ViewMode.Normal);
        }

        _tabBar.RemoveTab(id); // 若还有 Tab，内部会重选最后一个并触发 ShowSessionStage
        SyncTabBarVisibility();
        SyncSessionsItem();
        SyncPill();

        if (_tabBar.Count == 0)
        {
            SetViewMode(ViewMode.Normal); // 没有会话了就退回常规三栏，别把界面卡在全屏形态
            _activeSessionId = Guid.Empty;
            Window.Title = "RemoteFlow";
            ShowDetailStage();

            // ShowDetailStage 只是把 _detail 放回舞台，_detail 自己的内容还停在
            // 「正在连接 …」那一屏 —— 会话都关了还显示连接中，状态是错的。
            // 切回这条连接的信息卡（能看到"未连接"和重新连接入口）；找不到就回空态。
            var back = closedProfileId != Guid.Empty
                ? _connectionsVm.Items.FirstOrDefault(x => x.Id == closedProfileId)
                : null;
            if (back is not null)
            {
                _connectionsVm.SelectedItem = back;
                _detail.ShowConnection(_connectionsVm);
            }
            else
            {
                _detail.ShowEmpty();
            }
        }
    }

    private Guid _activeSessionId;
}
