using AppKit;
using CoreGraphics;
using Foundation;using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.App.Mac.Host;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 主窗口 —— 原生四栏（导航 / 连接资源 / 连接列表 / 详情，一比一对齐 Windows 版
/// 「图标栏 + 资源分组栏 + 连接主机栏 + 详情栏」）。资源导航与列表各自订阅共享的
/// <see cref="ConnectionsPageViewModel"/>；设置走独立偏好窗口（⌘,）。
/// </summary>
public sealed class MainWindowController : NSWindowController
{
    private readonly IServiceProvider _services;
    private readonly ConnectionsPageViewModel _connectionsVm;
    private readonly SessionManager _sessions;

    private readonly NavSidebar _nav;
    private readonly ConnectionResourcePane _resourcePane;
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
    private bool _resourceWasCollapsed;
    private bool _listWasCollapsed;
    private NSToolbarItem? _sessionsItem;
    private HotZone _pillHotZone = null!;
    private readonly NSView _stage = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private NSView _detailRoot = null!;

    /// <summary>详情栏的默认宽度约束（见 BuildSplit 里的说明）。列表栏靠它腾出的剩余空间伸缩。</summary>
    private NSLayoutConstraint _detailWidth = null!;
    private readonly NSSplitViewController _split = new();
    private readonly NSSearchField _search = new() { PlaceholderString = "搜索连接" };
    private NSSplitViewItem? _resourceItem;
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
        _resourcePane = new ConnectionResourcePane(_connectionsVm);
        _listPane = new ConnectionListPane(_connectionsVm, HydrateConnectionMetaAsync);

        Window.Title = "RemoteFlow";
        // 980 → 1130：导航栏收窄成纯图标（52）省下的宽度，全部让给「连接资源」（230）和
        // 多列表格列表（480，7 列表头放不下时才需要横向滚动）；详情列收窄到 360。
        Window.ContentMinSize = new CGSize(1130, 560);
        Window.TitleVisibility = NSWindowTitleVisibility.Hidden;
        Window.CollectionBehavior |= NSWindowCollectionBehavior.FullScreenPrimary;
        // 默认 false：不按按钮的纯移动不会分发给任何视图的 MouseMoved。RDP/VNC 会话靠
        // MouseMoved 把光标位置转发给远端（见 RdpScreenView/VncScreenView.Pointer），
        // 不开这个开关，远端就收不到「划过但没点」的位置变化——典型症状：点菜单项能选中
        // （MouseDown/Up 不受此开关影响），但鼠标划过菜单项时看不到选中高亮跟着走。
        Window.AcceptsMouseMovedEvents = true;

        BuildSplit();
        BuildToolbar();

        // 窗口材质（设置 → 常规 → 外观与行为）：启动时按当前设置铺一次，之后设置页拖动
        // 材质 / 透明度会通过 AppKitThemeService.GlassAppearanceChanged 实时重铺（见下）。
        var appSettings = services.GetRequiredService<AppSettings>();
        Palette.Material = appSettings.WindowMaterial;
        Palette.Transparency = appSettings.WindowTransparency;
        ApplyGlassAppearance(appSettings.WindowMaterial);
        if (services.GetRequiredService<IThemeService>() is AppKitThemeService themeService)
        {
            themeService.GlassAppearanceChanged += (_, _) =>
                NSApplication.SharedApplication.BeginInvokeOnMainThread(
                    () => ApplyGlassAppearance(Palette.Material));
        }

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
                SyncStatusBar();
                // 首页的「最近连接 / 收藏」卡片也要跟着亮灭 —— 它们是一次性构建的快照，
                // 只能整页重建（首页很短，重建代价可以接受）。
                if (_currentNav == NavSidebar.Item.Home)
                {
                    _detail.ShowHome(_services.GetRequiredService<HomePageViewModel>());
                }

                SyncTabStates();
                SyncPill();
            });

        _connectionsVm.OpenConnectionRequested += (_, profile) => _ = OpenAsync(profile, profile.Name);
        _connectionsVm.NavigationRequested += (_, page) => NavigateTo(page);

        _search.Changed += (_, _) => _listPane.ApplySearch(_search.StringValue);

        _ = StartAsync();
    }

    private NSVisualEffectView? _glass;

    /// <summary>按材质铺 / 收窗口底的磨砂：纯色时窗口本身不透明、不铺材质层；
    /// 云母 / 亚克力时窗口透明、铺一层 <see cref="NSVisualEffectView"/>，
    /// 具体透过多少交给 <see cref="Palette.PageGround"/>（材质本身只决定模糊强弱与色调）。
    /// 设置页拖动材质 / 透明度时会重复调用本方法（见构造函数里的 GlassAppearanceChanged 订阅）。</summary>
    private void ApplyGlassAppearance(WindowMaterial material)
    {
        if (material == WindowMaterial.Solid)
        {
            Window.IsOpaque = true;
            Window.BackgroundColor = NSColor.WindowBackground;
            _glass?.RemoveFromSuperview();
            _glass = null;
        }
        else
        {
            Window.IsOpaque = false;
            Window.BackgroundColor = NSColor.Clear;
            var wanted = material == WindowMaterial.Acrylic
                ? NSVisualEffectMaterial.HudWindow
                : NSVisualEffectMaterial.Sidebar;

            if (_glass is null && Window.ContentView is { } host)
            {
                _glass = new NSVisualEffectView
                {
                    BlendingMode = NSVisualEffectBlendingMode.BehindWindow,
                    State = NSVisualEffectState.FollowsWindowActiveState,
                    TranslatesAutoresizingMaskIntoConstraints = false,
                };
                host.AddSubview(_glass, NSWindowOrderingMode.Below, null);
                NSLayoutConstraint.ActivateConstraints(new[]
                {
                    _glass.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor),
                    _glass.TrailingAnchor.ConstraintEqualTo(host.TrailingAnchor),
                    _glass.TopAnchor.ConstraintEqualTo(host.TopAnchor),
                    _glass.BottomAnchor.ConstraintEqualTo(host.BottomAnchor),
                });
            }

            if (_glass is not null)
            {
                _glass.Material = wanted;
            }
        }

        _detail.RefreshGlassAppearance();
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
        var item = landing switch
        {
            LandingPage.Connections or LandingPage.Favorites or LandingPage.Recent
                => NavSidebar.Item.Connections,
            LandingPage.Credentials => NavSidebar.Item.Credentials,
            _ => NavSidebar.Item.Home,
        };
        _nav.Select(item);
        if (item == NavSidebar.Item.Connections)
        {
            // 收藏 / 最近连接已降级为工作台内的智能视图，这里补一次真正的视图切换
            // （上面 Select 触发的 OnNavSelected 只会刷新「全部」）。
            _ = _listPane.SetView(landing switch
            {
                LandingPage.Favorites => ConnectionListView.Favorites,
                LandingPage.Recent => ConnectionListView.Recent,
                _ => ConnectionListView.All,
            });
        }

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
        // 第一列：纯图标导航栏，一比一对齐 Windows 最左侧窄栏（无文字标签）。
        // Minimum 与 Maximum 取同一个值把宽度彻底钉死：留区间（原来 52~60）等于给
        // NSSplitViewController 在「只有导航+详情两栏」和「四栏全开」两种组合下各自
        // 落在区间内不同值的余地，表现就是在首页/凭据/设置与连接工作台之间来回切时，
        // 这一栏的宽度会跳（真实反馈）。
        var navItem = NSSplitViewItem.CreateSidebar(_nav);
        navItem.MinimumThickness = 56;
        navItem.MaximumThickness = 56;
        navItem.CanCollapse = true;
        navItem.HoldingPriority = 260; // 固定宽度
        _split.AddSplitViewItem(navItem);

        // 第二列：连接资源导航（智能视图 + 分组）。Windows 定宽 216px，这里给到
        // 230~320 留出余量——分组名 / 计数比 Windows 常见样本更长时不至于一直截断。
        _resourceItem = NSSplitViewItem.FromViewController(_resourcePane);
        _resourceItem.MinimumThickness = 230;
        _resourceItem.MaximumThickness = 320;
        _resourceItem.CanCollapse = true;
        _resourceItem.HoldingPriority = 260; // 固定宽度
        _split.AddSplitViewItem(_resourceItem);

        _listItem = NSSplitViewItem.FromViewController(_listPane);
        // 列表列是真正的多列表格（名称/主机/协议/标签/在线/最近连接/收藏），且对齐 Windows：
        // 它是「跟着窗口宽度伸缩」的那一列（HoldingPriority 全场最低），详情列反而定宽——
        // 窗口最小宽 1180 = nav 56 + resource 230 + list 480 + detail 380（大致），仍放得下。
        _listItem.MinimumThickness = 480;
        _listItem.MaximumThickness = 100_000; // 无上限：多出的窗口宽度优先喂给列表，不是详情列
        _listItem.CanCollapse = true;
        _listItem.HoldingPriority = 250; // 全场最低 —— 窗口缩放 / 折叠时优先由列表列伸缩
        _split.AddSplitViewItem(_listItem);

        // 详情区 = 舞台（详情卡 / 会话画面）。会话 Tab 条不在这里——它挂在窗口标题栏上，
        // 横跨整个窗口宽度（对齐 Windows：标签在公共标题栏，而不是缩在详情栏内部）。
        var detailRoot = _detailRoot = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        detailRoot.AddSubview(_stage);

        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _stage.TopAnchor.ConstraintEqualTo(detailRoot.SafeAreaLayoutGuide.TopAnchor),
            _stage.LeadingAnchor.ConstraintEqualTo(detailRoot.LeadingAnchor),
            _stage.TrailingAnchor.ConstraintEqualTo(detailRoot.TrailingAnchor),
            _stage.BottomAnchor.ConstraintEqualTo(detailRoot.BottomAnchor),
        });

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
        // 对齐 Windows：详情列是窄的定宽列，列表列才是跟着窗口宽度伸缩的那个（HoldingPriority
        // 比列表高 → 有多余宽度时列表先吃，详情列常态下停在 Minimum）。Maximum 仍留无上限：
        // 会话全屏时 nav / resource / list 三列全部折叠，详情列（含会话画面）要能吃下整个窗口宽度，
        // 不能被这里的「窄」预设卡死。
        detailItem.MinimumThickness = 360;
        detailItem.MaximumThickness = 100_000;
        detailItem.HoldingPriority = 260;
        _split.AddSplitViewItem(detailItem);

        // 详情栏默认宽度交给 Auto Layout，而不是 NSSplitView.SetPositionOfDivider。
        // NSSplitViewController 内部用约束管理各栏宽度，老的 divider API 设完会在下一轮
        // 约束求解时被覆盖 —— 这正是「切回工作台时列表栏先窄后宽」的根因：早先的写法只能
        // 靠「立即摆一次 + 下一轮 runloop 再摆一次」去追，而第二次纠正必然发生在用户已经
        // 看到画面之后（真实反馈 + 用户截到的中间帧：左边工作台列表栏还很窄、右边详情栏
        // 还显示着首页内容）。改成约束后，展开当帧就由布局系统直接落到目标宽度。
        //
        // 优先级必须**高于本栏的 HoldingPriority(260)**：NSSplitViewController 会按
        // holdingPriority 为每栏生成「坚持当前宽度」的约束，取 DefaultLow(250) 时这条
        // 宽度约束直接被盖掉 —— 实测结果正好反过来：详情栏吃到 1075，列表栏被压到它的
        // 最小值 480。500 足以压过 260，又远低于 Required，不会和分栏系统硬冲突。
        _detailWidth = detailRoot.WidthAnchor.ConstraintEqualTo(DetailDefaultWidth);
        _detailWidth.Priority = 500;
        _detailWidth.Active = true;

        // 分栏之外再套一层：底部挂一条状态栏（对齐 Windows 版窗口底部那条）。
        // 用 AddChildViewController 而不是只把 _split.View 塞进去——后者会绕过
        // 子 VC 的生命周期与外观传递。
        var shell = new NSViewController { View = new NSView { TranslatesAutoresizingMaskIntoConstraints = false } };
        shell.AddChildViewController(_split);
        var splitView = _split.View;
        splitView.TranslatesAutoresizingMaskIntoConstraints = false;
        _statusBar = BuildStatusBar();
        shell.View.AddSubview(splitView);
        shell.View.AddSubview(_statusBar);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            splitView.TopAnchor.ConstraintEqualTo(shell.View.TopAnchor),
            splitView.LeadingAnchor.ConstraintEqualTo(shell.View.LeadingAnchor),
            splitView.TrailingAnchor.ConstraintEqualTo(shell.View.TrailingAnchor),
            splitView.BottomAnchor.ConstraintEqualTo(_statusBar.TopAnchor),

            _statusBar.LeadingAnchor.ConstraintEqualTo(shell.View.LeadingAnchor),
            _statusBar.TrailingAnchor.ConstraintEqualTo(shell.View.TrailingAnchor),
            _statusBar.BottomAnchor.ConstraintEqualTo(shell.View.BottomAnchor),
        });
        // 高度单独留引用：会话全屏时要把它压成 0，只 Hidden 的话这 24pt 仍占位，
        // 画面底部会空出一条。
        _statusBarHeight = _statusBar.HeightAnchor.ConstraintEqualTo(StatusBarHeight);
        _statusBarHeight.Active = true;

        Window.ContentViewController = shell;

        // 会话标签由 BuildToolbar 创建的自定义 NSToolbarItem 承载；这里不再使用
        // NSTitlebarAccessoryViewController.Bottom——后者会在公共标题栏下方额外撑出一整行。
        _tabBar.SetBarHidden(true);
    }

    private const int StatusBarHeight = 24;
    private NSView _statusBar = null!;
    private NSLayoutConstraint _statusBarHeight = null!;
    private NSTextField _statusLeft = null!;
    private NSTextField _statusRight = null!;

    /// <summary>窗口底部状态条：左边会话状态，右边在线统计 + 凭据保护方式
    /// （对齐 Windows 版底部「无活动会话 … 在线 10 / 13 · 本地优先」那条）。</summary>
    private NSView BuildStatusBar()
    {
        var bar = new NSVisualEffectView
        {
            Material = NSVisualEffectMaterial.Titlebar,
            BlendingMode = NSVisualEffectBlendingMode.WithinWindow,
            State = NSVisualEffectState.FollowsWindowActiveState,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        _statusLeft = StatusLabel(NSTextAlignment.Left);
        _statusRight = StatusLabel(NSTextAlignment.Right);
        bar.AddSubview(_statusLeft);
        bar.AddSubview(_statusRight);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _statusLeft.LeadingAnchor.ConstraintEqualTo(bar.LeadingAnchor, 12),
            _statusLeft.CenterYAnchor.ConstraintEqualTo(bar.CenterYAnchor),
            _statusRight.TrailingAnchor.ConstraintEqualTo(bar.TrailingAnchor, -12),
            _statusRight.CenterYAnchor.ConstraintEqualTo(bar.CenterYAnchor),
            _statusRight.LeadingAnchor.ConstraintGreaterThanOrEqualTo(_statusLeft.TrailingAnchor, 12),
        });

        _connectionsVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(_connectionsVm.TotalConnectionsDisplay)
                or nameof(_connectionsVm.PresenceProbeEnabled))
            {
                NSApplication.SharedApplication.BeginInvokeOnMainThread(SyncStatusBar);
            }
        };

        SyncStatusBar();
        return bar;
    }

    private static NSTextField StatusLabel(NSTextAlignment align) => new()
    {
        Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(11),
        TextColor = NSColor.SecondaryLabel,
        Alignment = align,
        LineBreakMode = NSLineBreakMode.TruncatingTail,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    /// <summary>刷新底部状态条文案。会话数变化、列表重载后都会走到这里。</summary>
    private void SyncStatusBar()
    {
        if (_statusLeft is null || _statusRight is null)
        {
            return;
        }

        var active = _sessions.ActiveSessions.Count;
        _statusLeft.StringValue = active == 0 ? "无活动会话" : $"已连接 {active} 个会话";
        _statusRight.StringValue = $"{_connectionsVm.TotalConnectionsDisplay}   ·   本地优先 · 凭据由 macOS 钥匙串保护";
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

        // 离开常规模式时记住左侧三列原本的折叠状态，回来时照原样还原。
        if (wasNormal && !goingNormal)
        {
            _navWasCollapsed = NavItem?.Collapsed ?? false;
            _resourceWasCollapsed = _resourceItem?.Collapsed ?? false;
            _listWasCollapsed = _listItem?.Collapsed ?? false;
        }

        _mode = mode;

        // 只有「舞台正在显示会话画面」才隐藏左侧三列与工具栏；
        // 在首页 / 凭据等页面按 ⌃⌘F，就只是普通的原生全屏，界面保持完整。
        var chromeHidden = mode != ViewMode.Normal && _stageIsSession;
        if (NavItem is { } nav)
        {
            nav.Collapsed = chromeHidden || (goingNormal && _navWasCollapsed);
        }

        if (_resourceItem is not null)
        {
            _resourceItem.Collapsed = chromeHidden || (goingNormal && _resourceWasCollapsed);
        }

        if (_listItem is not null)
        {
            _listItem.Collapsed = chromeHidden || (goingNormal && _listWasCollapsed);
        }

        if (Window.Toolbar is { } tb)
        {
            tb.Visible = !chromeHidden;
        }

        SyncDetailWidthConstraint();

        // 会话铺满窗口时，底部状态条跟着其它 chrome 一起让位（高度一并压成 0）。
        if (_statusBar is not null)
        {
            _statusBar.Hidden = chromeHidden;
            _statusBarHeight.Constant = chromeHidden ? 0 : StatusBarHeight;
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

    /// <summary>把每个标签上的状态点对齐会话实际状态（绿=已连接，橙=连接中 / 异常），
    /// 对齐 Windows 版标题栏标签里标题右侧那颗点。</summary>
    private void SyncTabStates()
    {
        foreach (var (id, _, _) in _tabBar.Sessions)
        {
            var session = _sessions.ActiveSessions.FirstOrDefault(s => s.SessionId == id);
            _tabBar.SetTabConnected(id, session?.State == ConnectionState.Connected);
        }
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

    /// <summary>会话标签工具栏项的唯一标识。它按需插入统一标题栏工具栏，
    /// 无会话或进入全屏时直接移除，不在标题栏下方另占一行。</summary>
    private const string SessionTabsToolbarIdentifier = "rf.sessiontabs";

    private void SyncTabBarVisibility()
    {
        // 只要有活跃会话就常驻公共标题栏（对齐 Windows 版）：切到首页 / 我的连接 / 设置等页面
        // 仍可随时看见并切回。两档全屏改用悬浮药丸，因此从系统工具栏移除。
        var show = _tabBar.Count > 0 && _mode == ViewMode.Normal;
        if (Window.Toolbar is not { } toolbar)
        {
            return;
        }

        var existing = Array.FindIndex(
            toolbar.Items,
            item => item.Identifier == SessionTabsToolbarIdentifier);

        if (show && existing < 0)
        {
            _tabBar.SetBarHidden(false);
            // 对齐 Windows：先是「回到会话」按钮，活动会话标签从它右侧开始；
            // 其它页面动作仍留在按钮左侧，搜索框继续固定在标题栏最右。
            var returnButton = Array.FindIndex(
                toolbar.Items,
                item => item.Identifier == "rf.sessions");
            var insertAt = returnButton >= 0
                ? returnButton + 1
                : Math.Min(2, toolbar.Items.Length);
            toolbar.InsertItem(SessionTabsToolbarIdentifier, insertAt);
        }
        else if (!show && existing >= 0)
        {
            toolbar.RemoveItem(existing);
            _tabBar.SetBarHidden(true);
        }
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

        SyncPanesForStage();
    }

    /// <summary>
    /// 会话画面铺满「图标栏以外的全部区域」——上台的是会话就收起资源栏与列表栏，
    /// 回到详情 / 工作台再放回来（对齐 Windows：会话期间只剩最左侧图标栏）。
    /// 两档全屏另有 <see cref="SetViewMode"/> 负责（那里连图标栏一起收），此处不插手。
    /// </summary>
    private void SyncPanesForStage()
    {
        if (_mode != ViewMode.Normal)
        {
            return;
        }

        SetListVisible(!_stageIsSession && _listApplicable);
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
        private const string Refresh = "rf.refresh";
        private const string Probe = "rf.probe";
        private readonly MainWindowController _o;
        public ToolbarDelegate(MainWindowController o) => _o = o;

        public override string[] DefaultItemIdentifiers(NSToolbar t) => new[]
        {
            NSToolbar.NSToolbarToggleSidebarItemIdentifier,
            NSToolbar.NSToolbarSidebarTrackingSeparatorItemIdentifier,
            ToggleList,
            NewConn,
            Refresh,
            Probe,
            Sessions,
            NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
            Search,
        };

        public override string[] AllowedItemIdentifiers(NSToolbar t) => new[]
        {
            NSToolbar.NSToolbarToggleSidebarItemIdentifier,
            NSToolbar.NSToolbarSidebarTrackingSeparatorItemIdentifier,
            ToggleList,
            NewConn,
            Refresh,
            Probe,
            Sessions,
            SessionTabsToolbarIdentifier,
            NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
            Search,
        };

        public override NSToolbarItem? WillInsertItem(NSToolbar toolbar, string id, bool willInsert)
        {
            switch (id)
            {
                case SessionTabsToolbarIdentifier:
                    // 真正的标题栏内标签：自定义 View 是 NSToolbarItem 的一部分，
                    // 与刷新 / 搜索等控件共享 Unified toolbar 行，不再产生标题栏下方附加行。
                    _o._tabBar.Frame = new CGRect(0, 0, 520, SessionTabBar.BarHeightPoints);
                    _o._tabBar.SetBarHidden(false);
                    return new NSToolbarItem(SessionTabsToolbarIdentifier)
                    {
                        Label = "活动会话",
                        PaletteLabel = "活动会话",
                        ToolTip = "活动会话",
                        View = _o._tabBar,
                        VisibilityPriority = (nint)(long)NSToolbarItemVisibilityPriority.High,
                    };
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
                case Refresh:
                    // 对齐 Windows 上下文条的「刷新列表」。
                    var refresh = new NSToolbarItem(Refresh)
                    {
                        Label = "刷新",
                        ToolTip = "刷新连接列表",
                        Image = NSImage.GetSystemSymbol("arrow.clockwise", null),
                        Bordered = true,
                    };
                    refresh.Activated += (_, _) => _ = _o._listPane.RefreshAsync();
                    return refresh;
                case Probe:
                    // 对齐 Windows 上下文条的「探测当前列表的可达性」。
                    var probe = new NSToolbarItem(Probe)
                    {
                        Label = "探测",
                        ToolTip = "探测当前列表的可达性（需在设置里开启在线探测）",
                        Image = NSImage.GetSystemSymbol("dot.radiowaves.left.and.right", null)
                                ?? NSImage.GetSystemSymbol("wave.3.right", null),
                        Bordered = true,
                    };
                    probe.Activated += (_, _) => _o._connectionsVm.ProbePresenceCommand.Execute(null);
                    return probe;
                case ToggleList:
                    var toggle = new NSToolbarItem(ToggleList)
                    {
                        Label = "列表",
                        ToolTip = "显示 / 隐藏连接资源与列表（⌘⌥L）",
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
        // 重复点当前页通常无事可做 —— 但会话铺满舞台时，图标栏是唯一还可见的导航，
        // 点「我的连接」就是用户「回到工作台」的入口（对齐 Windows）。此时 _currentNav
        // 仍停在进入会话前的那一页，短路掉就再也回不去了（真实事故：连上后点图标没反应）。
        if (_currentNav == item && !_stageIsSession)
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
                // 只刷新当前智能视图（全部 / 收藏 / 最近），不强制重置——用户离开工作台
                // 再点回来，应该还在离开时的那个视图；要切视图走 NavigateTo → SetView。
                _ = _listPane.RefreshAsync();
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

    /// <summary>列表列与资源导航列（第二 / 三列）总是同进同退——资源导航脱离连接列表没有意义。</summary>
    private void SetListVisible(bool visible)
    {
        var resourceChanging = _resourceItem is not null && _resourceItem.Collapsed == visible;
        var listChanging = _listItem is not null && _listItem.Collapsed == visible;
        if (!resourceChanging && !listChanging)
        {
            return;
        }

        // 展开 / 折叠只需要翻这两个开关：各栏宽度由 Auto Layout 决定（详情栏那条定宽约束
        // + 列表栏最低 HoldingPriority），展开当帧就落到目标比例。
        // 这里刻意不再调 NSSplitView.SetPositionOfDivider —— 它是 NSSplitView 的老 API，
        // 在 NSSplitViewController 的约束体系下会被下一轮布局覆盖，只能靠「下一轮 runloop
        // 再摆一次」去追，而那次纠正必然发生在用户已经看到画面之后，正是列表栏由窄跳宽的来源。
        if (_resourceItem is not null) _resourceItem.Collapsed = !visible;
        if (_listItem is not null) _listItem.Collapsed = !visible;
        SyncDetailWidthConstraint();
    }

    /// <summary>
    /// 详情栏那条 <see cref="DetailDefaultWidth"/> 定宽约束只在**列表栏可见**时生效。
    /// 列表栏一折叠（首页 / 凭据 / 设置，或会话铺满图标栏以外的全部区域），详情栏就该吃满
    /// 剩余空间；此时这条优先级 500 的约束必须让位，否则会和分栏系统的填充约束打架 ——
    /// 布局最终仍以填充为准，但控制台会持续刷约束冲突警告。
    /// </summary>
    private void SyncDetailWidthConstraint()
    {
        if (_detailWidth is not null && _listItem is not null)
        {
            _detailWidth.Active = !_listItem.Collapsed;
        }
    }

    /// <summary>详情栏的默认宽度。列表栏吃掉剩下的全部宽度（对齐 Windows：列表是随窗口
    /// 伸缩的那一栏，详情栏定宽）。420 是实测折中——520 时详情栏明显比内容需要的宽、
    /// 白白占走列表的横向空间（真实反馈）；压到 360 以下卡片里的主机名 / 凭据名又开始截断。</summary>
    private const int DetailDefaultWidth = 420;

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
            NavigationPage.Credentials => NavSidebar.Item.Credentials,
            NavigationPage.Settings => NavSidebar.Item.Settings,
            _ => NavSidebar.Item.Connections, // Connections / Favorites / Recent 都落在工作台
        };
        _nav.Select(item);

        // 收藏 / 最近连接不再是独立导航项，落地后还要告诉工作台切到对应的智能视图
        // （_nav.Select 若目标行未变不会重新触发 OnNavSelected，这里独立设置不受影响）。
        switch (page)
        {
            case NavigationPage.Favorites:
                _ = _listPane.SetView(ConnectionListView.Favorites);
                break;
            case NavigationPage.Recent:
                _ = _listPane.SetView(ConnectionListView.Recent);
                break;
            case NavigationPage.Connections:
                _ = _listPane.SetView(ConnectionListView.All);
                break;
        }
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

        // 从单击「连接」这一刻就进入会话舞台：这是独立占位视图，不是 _detail，
        // ShowStage 会立即收起资源栏与连接列表，只保留左侧图标栏。
        ShowStage(_detail.MakeConnectingPlaceholder(name));
        Window.ContentView?.LayoutSubtreeIfNeeded();
        _stage.DisplayIfNeeded();

        // CreateSessionAsync 可能先同步读取 Keychain；先把控制权交回 AppKit 一轮，确保用户
        // 单击后的第一帧已经收起资源 / 列表并显示会话舞台，而不是继续停在详情页等待。
        await Task.Yield();

        RemoteFlow.Core.Sessions.IRemoteSession? session = null;
        try
        {
            session = await _sessions.CreateSessionAsync(profile);

            // 会话视图必须在 ConnectAsync 前进入可视树：RDP / VNC 需要先拿到实际舞台尺寸，
            // SSH 的 WKWebView 也可以在网络握手期间同步完成终端资源加载。
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

            // 连接尚在握手时就建立标签并把真实会话视图放上舞台；标签位于统一标题栏工具栏，
            // 不会再出现「详情页里显示正在登录，连上后才突然切过去」的跳变。
            _tabBar.AddTab(session.SessionId, name, profile.Protocol);
            SyncTabBarVisibility();
            SyncSessionsItem();
            ShowSessionStage(session.SessionId);
            Window.Title = $"{name} — RemoteFlow";

            // ConnectAsync 的部分协议实现会在首次 await 前执行同步握手准备；先让标题栏标签和
            // 真正的会话视图完成一帧绘制，连接中阶段就不会继续残留上一张详情页快照。
            await Task.Yield();
            Window.ContentView?.LayoutSubtreeIfNeeded();
            _stage.LayoutSubtreeIfNeeded();

            // RDP「适应窗口」：按已经收起资源栏 / 列表栏后的真实会话舞台请求桌面尺寸。
            // 标签现在与其它控件共用标题栏工具栏行，不再额外吃掉内容区 38pt。
            if (session is RemoteFlow.Protocol.Rdp.Mac.RdpSession rdpSession
                && profile.Rdp is { DisplayMode: not RemoteFlow.Core.Models.RdpDisplayMode.FixedResolution })
            {
                var stage = _stage.Bounds.Size;
                var scale = Window.BackingScaleFactor;
                double w, h;
                if (stage.Width >= 320 && stage.Height >= 240)
                {
                    w = (double)stage.Width * scale;
                    h = (double)stage.Height * scale;
                }
                else
                {
                    var screen = (Window.Screen ?? NSScreen.MainScreen)?.Frame.Size ?? new CGSize(1680, 1050);
                    w = (double)screen.Width;
                    h = (double)screen.Height;
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
                var title = RemoteFlow.Presentation.ConnectionErrorText.Title(session.ErrorCode);
                var message = RemoteFlow.Presentation.ConnectionErrorText.Describe(
                    session.ErrorCode,
                    session.ErrorMessage);
                await CloseSessionAsync(session.SessionId);
                _detail.ShowError(title, message);
                ShowStage(_detail);
                SyncTabBarVisibility();
            }
        }
        catch (Exception ex)
        {
            if (session is not null)
            {
                await CloseSessionAsync(session.SessionId);
            }

            _detail.ShowError(ex.Message);
            ShowStage(_detail);
            SyncTabBarVisibility();
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
        var removedView = _sessionViews.Remove(id, out var view);
        if (removedView && view is not null)
        {
            (view as SshTerminalView)?.Detach();
            (view as VncScreenView)?.Detach();
            (view as RdpScreenView)?.Detach();
            view.RemoveFromSuperview();
        }

        _sessionNames.Remove(id);
        var removedProfile = _sessionProfileIds.Remove(id, out var closedProfileId);

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

        // SessionClosed 会排到主线程再通知一次，而 CloseSessionAsync 的调用方也会立即清理；
        // 第二次进入必须幂等，不能把刚显示的连接失败说明又覆盖成空态。
        if (_tabBar.Count == 0 && (removedView || removedProfile || wasShowing))
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
