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
    private readonly ConnectionService _connections;
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
        _connections = services.GetRequiredService<ConnectionService>();
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

    private async Task StartAsync()
    {
        await SeedSampleIfEmptyAsync();
        _nav.SelectFirst();
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

    private async Task SeedSampleIfEmptyAsync()
    {
        if ((await _connections.GetAllAsync()).Count > 0)
        {
            return;
        }

        var groupService = _services.GetRequiredService<GroupService>();
        var prod = await groupService.CreateAsync("生产环境", null);
        var test = await groupService.CreateAsync("测试环境", null);

        var samples = new (string Name, string Host, int Port, ProtocolType Proto, Guid? Group, bool Fav)[]
        {
            ("Web 服务器 01", "192.0.2.20", 22, ProtocolType.Ssh, prod?.Id, true),
            ("数据库主库", "10.0.1.15", 22, ProtocolType.Ssh, prod?.Id, false),
            ("Windows 域控", "192.0.2.11", 3389, ProtocolType.Rdp, prod?.Id, true),
            ("Mac 构建机", "192.0.2.30", 5900, ProtocolType.Vnc, test?.Id, false),
            ("测试跳板机", "172.16.0.9", 22, ProtocolType.Ssh, test?.Id, false),
        };

        foreach (var s in samples)
        {
            await _connections.CreateAsync(new ConnectionProfile
            {
                Name = s.Name,
                Host = s.Host,
                Port = s.Port,
                Protocol = s.Proto,
                GroupId = s.Group,
                Favorite = s.Fav,
            });
        }
    }
}
