using AppKit;
using CoreGraphics;
using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.Application.Services;
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
    private readonly NSSplitViewController _split = new();
    private readonly NSSearchField _search = new() { PlaceholderString = "搜索连接" };
    private NSSplitViewItem? _listItem;

    private SettingsWindowController? _settingsWindow;
    private NavSidebar.Item? _currentNav;

    public MainWindowController(IServiceProvider services)
        : base(NewWindow())
    {
        _services = services;
        _sessions = services.GetRequiredService<SessionManager>();

        _connectionsVm = services.GetRequiredService<ConnectionsPageViewModel>();
        _nav = new NavSidebar();
        _listPane = new ConnectionListPane(_connectionsVm);

        Window.Title = "RemoteFlow";
        Window.ContentMinSize = new CGSize(980, 560);
        Window.SetContentSize(new CGSize(1160, 720));
        Window.Center();
        Window.TitleVisibility = NSWindowTitleVisibility.Hidden;

        BuildSplit();
        BuildToolbar();

        _nav.Selected += OnNavSelected;
        _listPane.ConnectionSelected += (_, c) => _detail.ShowConnection(c);
        _listPane.ConnectionActivated += (_, c) => _ = OpenAsync(c.Profile, c.Name);
        _detail.ConnectRequested += (_, c) => _ = OpenAsync(c.Profile, c.Name);

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
        navItem.MaximumThickness = 220;
        navItem.CanCollapse = true;
        _split.AddSplitViewItem(navItem);

        _listItem = NSSplitViewItem.FromViewController(_listPane);
        _listItem.MinimumThickness = 260;
        _listItem.MaximumThickness = 420;
        _listItem.CanCollapse = true;
        _split.AddSplitViewItem(_listItem);

        var detailVc = new NSViewController { View = _detail };
        var detailItem = NSSplitViewItem.FromViewController(detailVc);
        detailItem.MinimumThickness = 420;
        _split.AddSplitViewItem(detailItem);

        Window.ContentViewController = _split;
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

    private sealed class ToolbarDelegate : NSToolbarDelegate
    {
        private const string NewConn = "rf.new";
        private const string Search = "rf.search";
        private readonly MainWindowController _o;
        public ToolbarDelegate(MainWindowController o) => _o = o;

        public override string[] DefaultItemIdentifiers(NSToolbar t) => new[]
        {
            NSToolbar.NSToolbarToggleSidebarItemIdentifier,
            NSToolbar.NSToolbarSidebarTrackingSeparatorItemIdentifier,
            NewConn,
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

        switch (item)
        {
            case NavSidebar.Item.Connections:
                SetListVisible(true);
                _detail.ShowEmpty();
                _ = _listPane.ShowConnectionsAsync();
                break;
            case NavSidebar.Item.Favorites:
                SetListVisible(true);
                _detail.ShowEmpty();
                _ = _listPane.ShowFavoritesAsync();
                break;
            case NavSidebar.Item.Recent:
                SetListVisible(true);
                _detail.ShowEmpty();
                _ = _listPane.ShowRecentAsync();
                break;
            case NavSidebar.Item.Home:
                SetListVisible(false);
                _detail.ShowHome(_services.GetRequiredService<HomePageViewModel>());
                break;
            case NavSidebar.Item.Credentials:
                SetListVisible(false);
                _detail.ShowCredentials(_services.GetRequiredService<CredentialsPageViewModel>());
                break;
            case NavSidebar.Item.Settings:
                OpenSettings();
                break;
        }
    }

    private void SetListVisible(bool visible)
    {
        if (_listItem is not null)
        {
            _listItem.Collapsed = !visible;
        }
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

    /// <summary>菜单「断开会话」：收掉当前会话并回到连接信息卡。</summary>
    public async void DisconnectCurrentSession()
    {
        if (_sessions.ActiveSessionCount == 0)
        {
            return;
        }

        await CloseActiveSessionsAsync();
        Window.Title = "RemoteFlow";
        await _listPane.RefreshAsync();
    }

    private async Task CloseActiveSessionsAsync()
    {
        foreach (var s in _sessions.ActiveSessions.ToArray())
        {
            try
            {
                await _sessions.CloseSessionAsync(s.SessionId);
            }
            catch
            {
                // 收尾尽力而为。
            }
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
        // macOS RDP 首版：交系统 RDP 客户端（方案 §8.E，FreeRDP 内嵌待接）。
        if (profile.Protocol == ProtocolType.Rdp)
        {
            try
            {
                var note = RdpLauncher.Launch(profile);
                _detail.ShowSessionInfo(name, "已在系统 RDP 客户端中打开", note);
            }
            catch (Exception ex)
            {
                _detail.ShowError(ex.Message);
            }

            return;
        }

        // 单会话详情：开新会话前先收掉旧的（用户反馈「详情页只留一个连接」）。
        await CloseActiveSessionsAsync();

        _detail.ShowConnecting(name);
        try
        {
            var session = await _sessions.CreateSessionAsync(profile);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await session.ConnectAsync(cts.Token);

            if (session.State != RemoteFlow.Core.Models.ConnectionState.Connected)
            {
                _detail.ShowError(session.ErrorMessage ?? session.ErrorCode.ToString());
                return;
            }

            switch (session)
            {
                case RemoteFlow.Protocol.Ssh.SshSession ssh:
                    _detail.ShowSshTerminal(ssh);
                    break;
                case RemoteFlow.Protocol.Vnc.VncSession vnc:
                    _detail.ShowVncScreen(vnc);
                    break;
                default:
                    _detail.ShowSessionPlaceholder(name, session.SessionId);
                    break;
            }

            Window.Title = $"{name} — RemoteFlow";
        }
        catch (Exception ex)
        {
            _detail.ShowError(ex.Message);
        }
    }

}
