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
    private NSToolbarItem? _sessionsItem;
    private HotZone _pillHotZone = null!;
    private bool _isFullScreen;
    private readonly NSView _stage = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly NSSplitViewController _split = new();
    private readonly NSSearchField _search = new() { PlaceholderString = "搜索连接" };
    private NSSplitViewItem? _listItem;
    private NSToolbarItem? _toggleListItem;
    /// <summary>当前页是否有可折叠的列表列（首页 / 凭据没有）。</summary>
    private bool _listApplicable = true;

    // 会话 Id → 其画面视图（SshTerminalView / VncScreenView）。多 Tab 并存。
    private readonly Dictionary<Guid, NSView> _sessionViews = new();
    private readonly Dictionary<Guid, string> _sessionNames = new();

    private SettingsWindowController? _settingsWindow;
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
        Window.SetContentSize(new CGSize(1160, 720));
        Window.Center();
        Window.TitleVisibility = NSWindowTitleVisibility.Hidden;
        Window.CollectionBehavior |= NSWindowCollectionBehavior.FullScreenPrimary;

        BuildSplit();
        BuildToolbar();

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
        _tabBar.FullScreenRequested += (_, _) => Window.ToggleFullScreen(null);

        _pill.SessionsProvider = () => _tabBar.Sessions;
        _pill.SessionPicked += (_, id) => _tabBar.RequestSelect(id);
        _pill.ExitFullScreenRequested += (_, _) => Window.ToggleFullScreen(null);
        WireFullScreenNotifications();
        _sessions.SessionClosed += (_, id) =>
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() => DropSession(id));

        _connectionsVm.OpenConnectionRequested += (_, profile) => _ = OpenAsync(profile, profile.Name);
        _connectionsVm.NavigationRequested += (_, page) => NavigateTo(page);

        _search.Changed += (_, _) => _listPane.ApplySearch(_search.StringValue);

        _ = StartAsync();
    }

    private Task StartAsync()
    {
        _nav.SelectFirst();
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
        var detailRoot = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
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
            onEnter: () => { if (_isFullScreen && _tabBar.Count > 0) _pill.Reveal(); },
            onExit: () => _pill.MaybeHide());
        detailRoot.AddSubview(_pillHotZone);
        detailRoot.AddSubview(_pill);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _pillHotZone.LeadingAnchor.ConstraintEqualTo(_stage.LeadingAnchor),
            _pillHotZone.TrailingAnchor.ConstraintEqualTo(_stage.TrailingAnchor),
            _pillHotZone.TopAnchor.ConstraintEqualTo(_stage.TopAnchor),
            _pillHotZone.HeightAnchor.ConstraintEqualTo(6),

            _pill.CenterXAnchor.ConstraintEqualTo(_stage.CenterXAnchor),
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
    /// 全屏时隐藏 Tab 条（垂直空间全给画面），改用顶沿悬浮药丸；退出全屏还原。
    /// </summary>
    private void WireFullScreenNotifications()
    {
        var nc = NSNotificationCenter.DefaultCenter;
        nc.AddObserver(NSWindow.DidEnterFullScreenNotification, _ =>
        {
            _isFullScreen = true;
            SyncTabBarVisibility();
            SyncPill();
        }, Window);
        nc.AddObserver(NSWindow.DidExitFullScreenNotification, _ =>
        {
            _isFullScreen = false;
            _pill.ForceHide();
            SyncTabBarVisibility();
        }, Window);
    }

    private void SyncPill()
    {
        if (!_isFullScreen || _tabBar.Count == 0)
        {
            _pill.ForceHide();
            return;
        }

        var active = _tabBar.Sessions.FirstOrDefault(x => x.Id == _tabBar.ActiveId);
        if (active.Id != Guid.Empty)
        {
            _pill.SetSession(active.Title, active.Protocol);
        }

        _pill.Reveal(); // 刚进全屏先露一下，告诉用户工具条在哪；未固定则移开鼠标后收起
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
        var show = _tabBar.Count > 0 && _stageIsSession && !_isFullScreen;
        _tabBar.Hidden = !show;
        _stageTop.Active = !show;
        _stageTopWithTabs.Active = show;
    }

    /// <summary>
    /// 把页面内容放进舞台。刻意用 **autoresizing（frame 布局）** 而不是 Auto Layout 约束：
    /// 页面内部照常用约束自适应，但它的尺寸诉求不会沿 详情列 → NSSplitViewController → 窗口
    /// 反向传播。用约束固定时，NSScrollView 这类内容会把详情列的最大厚度压成它的最小厚度，
    /// 窗口就出现"宽度锁定"（拉不宽）。
    /// </summary>
    private void ShowStage(NSView view)
    {
        foreach (var v in _stage.Subviews.ToArray())
        {
            v.RemoveFromSuperview();
        }

        // 舞台上只有「会话画面」和「详情视图」两类，据此决定是否显示 Tab 条。
        _stageIsSession = !ReferenceEquals(view, _detail);

        view.TranslatesAutoresizingMaskIntoConstraints = true;
        view.Frame = _stage.Bounds;
        view.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;
        _stage.AddSubview(view);
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
        if (_currentNav == item && item != NavSidebar.Item.Settings)
        {
            return;
        }

        _currentNav = item;

        if (item != NavSidebar.Item.Settings)
        {
            _tabBar.ClearHighlight();
            ShowStage(_detail);
            SyncTabBarVisibility();
        }

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
                OpenSettings();
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

    public void OpenSettings()
    {
        _settingsWindow ??= new SettingsWindowController(
            _services.GetRequiredService<SettingsPageViewModel>());
        _settingsWindow.ShowWindow(this);
        _settingsWindow.Window.MakeKeyAndOrderFront(this);
    }

    public async void BeginNewConnection()
    {
        try
        {
            await _connectionsVm.CreateConnectionAsync();
            await _listPane.RefreshAsync();
        }
        catch (Exception ex)
        {
            _detail.ShowError(ex.Message);
        }
    }

    /// <summary>菜单「断开会话」⌘⇧W：关闭当前 Tab 的会话。</summary>
    public async void DisconnectCurrentSession()
    {
        if (_activeSessionId != Guid.Empty)
        {
            await CloseSessionAsync(_activeSessionId);
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

            // RDP「适应窗口」：按主屏比例请求桌面尺寸，避免默认 1920×1080 在 16:10 屏幕上出现上下黑边。
            if (session is RemoteFlow.Protocol.Rdp.Mac.RdpSession rdpSession
                && profile.Rdp is { DisplayMode: not RemoteFlow.Core.Models.RdpDisplayMode.FixedResolution })
            {
                var s = (Window.Screen ?? NSScreen.MainScreen)?.Frame.Size ?? new CGSize(1680, 1050);
                var w = (double)s.Width;
                var h = (double)s.Height;
                if (w > 1920) { h = h * 1920 / w; w = 1920; }
                rdpSession.PreferredSize = ((int)Math.Round(w / 2) * 2, (int)Math.Round(h / 2) * 2);
            }

            // 不套人为总超时：各协议自带连接超时（SSH ConnectionInfo.Timeout / RDP 线程），
            // 且首次连接的主机密钥确认框会停在中途，硬 cap 会把用户读指纹的时间也算进去。
            await session.ConnectAsync(CancellationToken.None);

            if (session.State != RemoteFlow.Core.Models.ConnectionState.Connected)
            {
                _detail.ShowError(session.ErrorMessage ?? session.ErrorCode.ToString());
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

            _sessionViews[session.SessionId] = view;
            _sessionNames[session.SessionId] = name;
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
        _tabBar.RemoveTab(id); // 若还有 Tab，内部会重选最后一个并触发 ShowSessionStage
        SyncTabBarVisibility();
        SyncSessionsItem();
        SyncPill();

        if (_tabBar.Count == 0)
        {
            _activeSessionId = Guid.Empty;
            Window.Title = "RemoteFlow";
            ShowDetailStage();
        }
    }

    private Guid _activeSessionId;
}
