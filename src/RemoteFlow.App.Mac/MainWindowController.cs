using AppKit;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 主窗口：统一工具栏 + 源列表侧栏（分组 / 连接）+ 详情区。
/// 侧栏数据来自共享 <see cref="ConnectionsPageViewModel"/>。
/// </summary>
public sealed class MainWindowController : NSWindowController
{
    private readonly IServiceProvider _services;
    private readonly ConnectionsPageViewModel _connectionsVm;
    private readonly ConnectionService _connections;

    private readonly NSOutlineView _outline = new();
    private SidebarSource? _sidebarSource;
    private readonly DetailView _detail = new();
    private readonly NSSearchField _search = new() { PlaceholderString = "搜索连接" };

    public MainWindowController(IServiceProvider services)
        : base(NewWindow())
    {
        _services = services;
        _connectionsVm = services.GetRequiredService<ConnectionsPageViewModel>();
        _connections = services.GetRequiredService<ConnectionService>();

        Window.Title = "RemoteFlow";
        Window.ContentMinSize = new CGSize(920, 560);
        Window.SetFrame(new CGRect(0, 0, 1180, 720), display: true);
        Window.Center();

        BuildSplit();
        BuildToolbar();

        _ = LoadAsync();
    }

    private static NSWindow NewWindow() => new(
        new CGRect(0, 0, 1180, 720),
        NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable
            | NSWindowStyle.FullSizeContentView | NSWindowStyle.UnifiedTitleAndToolbar,
        NSBackingStore.Buffered,
        deferCreation: false);

    // ── 分栏 ─────────────────────────────────────────────────────

    private void BuildSplit()
    {
        _outline.HeaderView = null;
        _outline.FloatsGroupRows = false;
        _outline.IndentationPerLevel = 14;
        _outline.RowSizeStyle = NSTableViewRowSizeStyle.Custom;
        _outline.RowHeight = 40;
        _outline.SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.SourceList;
        _outline.BackgroundColor = NSColor.Clear;
        _outline.AddColumn(new NSTableColumn("main") { ResizingMask = NSTableColumnResizing.Autoresizing });
        _outline.OutlineTableColumn = _outline.TableColumns()[0];
        _outline.DoubleClick += (_, _) => OpenSelected();

        var sidebarScroll = new NSScrollView
        {
            DocumentView = _outline,
            HasVerticalScroller = true,
            DrawsBackground = false,
            AutomaticallyAdjustsContentInsets = true,
        };

        var sidebarVc = new NSViewController { View = WrapVibrant(sidebarScroll, NSVisualEffectMaterial.Sidebar) };
        var detailVc = new NSViewController { View = _detail };

        var split = new NSSplitViewController();
        var sidebarItem = NSSplitViewItem.CreateSidebar(sidebarVc);
        sidebarItem.MinimumThickness = 220;
        sidebarItem.MaximumThickness = 360;
        sidebarItem.CanCollapse = true;
        split.AddSplitViewItem(sidebarItem);

        var detailItem = NSSplitViewItem.FromViewController(detailVc);
        detailItem.MinimumThickness = 480;
        split.AddSplitViewItem(detailItem);

        Window.ContentViewController = split;
    }

    private static NSView WrapVibrant(NSView content, NSVisualEffectMaterial material)
    {
        var fx = new NSVisualEffectView
        {
            Material = material,
            BlendingMode = NSVisualEffectBlendingMode.BehindWindow,
            State = NSVisualEffectState.FollowsWindowActiveState,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        fx.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            content.LeadingAnchor.ConstraintEqualTo(fx.LeadingAnchor),
            content.TrailingAnchor.ConstraintEqualTo(fx.TrailingAnchor),
            content.TopAnchor.ConstraintEqualTo(fx.TopAnchor),
            content.BottomAnchor.ConstraintEqualTo(fx.BottomAnchor),
        });
        return fx;
    }

    // ── 工具栏 ───────────────────────────────────────────────────

    private void BuildToolbar()
    {
        var toolbar = new NSToolbar("rf.main")
        {
            Delegate = new ToolbarDelegate(this),
            DisplayMode = NSToolbarDisplayMode.Icon,
            AllowsUserCustomization = false,
            ShowsBaselineSeparator = true,
        };
        Window.Toolbar = toolbar;
        Window.ToolbarStyle = NSWindowToolbarStyle.Unified;
        Window.TitleVisibility = NSWindowTitleVisibility.Hidden;
    }

    private sealed class ToolbarDelegate : NSToolbarDelegate
    {
        private const string NewConn = "rf.new";
        private const string Search = "rf.search";
        private readonly MainWindowController _owner;

        public ToolbarDelegate(MainWindowController owner) => _owner = owner;

        public override string[] DefaultItemIdentifiers(NSToolbar toolbar) => new[]
        {
            NSToolbar.NSToolbarToggleSidebarItemIdentifier,
            NSToolbar.NSToolbarSidebarTrackingSeparatorItemIdentifier,
            NewConn,
            NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
            Search,
        };

        public override string[] AllowedItemIdentifiers(NSToolbar toolbar) => DefaultItemIdentifiers(toolbar);

        public override NSToolbarItem? WillInsertItem(NSToolbar toolbar, string itemIdentifier, bool willBeInserted)
        {
            switch (itemIdentifier)
            {
                case NewConn:
                    var item = new NSToolbarItem(NewConn)
                    {
                        Label = "新建连接",
                        ToolTip = "新建连接（⌘N）",
                        Image = NSImage.GetSystemSymbol("plus", null),
                        Bordered = true,
                    };
                    item.Activated += (_, _) => _owner.BeginNewConnection();
                    return item;

                case Search:
                    return new NSSearchToolbarItem(Search)
                    {
                        SearchField = _owner._search,
                        ResignsFirstResponderWithCancel = true,
                    };

                default:
                    return null;
            }
        }
    }

    // ── 数据加载 ─────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        await SeedSampleIfEmptyAsync();

        _connectionsVm.Filter = ConnectionFilter.All;
        await _connectionsVm.LoadAsync();

        _sidebarSource = new SidebarSource(_connectionsVm);
        _outline.DataSource = _sidebarSource;
        _outline.Delegate = _sidebarSource;
        _outline.ReloadData();
        _outline.ExpandItem(null, expandChildren: true);

        _sidebarSource.SelectionChanged += (_, _) =>
        {
            if (_sidebarSource.SelectedConnection is { } c)
            {
                _detail.ShowConnection(c);
            }
        };
    }

    /// <summary>首启无连接时种入样例，让 UI 有内容可看（正式版移除）。</summary>
    private async Task SeedSampleIfEmptyAsync()
    {
        if ((await _connections.GetAllAsync()).Count > 0)
        {
            return;
        }

        var groups = await _connections.GetGroupsAsync();
        var prod = groups.FirstOrDefault(g => g.Name == "生产环境")
                   ?? await CreateGroupAsync("生产环境");
        var test = groups.FirstOrDefault(g => g.Name == "测试环境")
                   ?? await CreateGroupAsync("测试环境");

        var samples = new (string Name, string Host, int Port, ProtocolType Proto, Guid? Group)[]
        {
            ("Web 服务器 01", "192.0.2.20", 22, ProtocolType.Ssh, prod?.Id),
            ("数据库主库", "10.0.1.15", 22, ProtocolType.Ssh, prod?.Id),
            ("Windows 域控", "192.0.2.11", 3389, ProtocolType.Rdp, prod?.Id),
            ("Mac 构建机", "192.0.2.30", 5900, ProtocolType.Vnc, test?.Id),
            ("测试跳板机", "172.16.0.9", 22, ProtocolType.Ssh, test?.Id),
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
            });
        }
    }

    private async Task<ConnectionGroup?> CreateGroupAsync(string name)
    {
        var groupService = _services.GetRequiredService<GroupService>();
        return await groupService.CreateAsync(name, parentId: null);
    }

    // ── 动作 ─────────────────────────────────────────────────────

    public void BeginNewConnection()
    {
        var sheet = new ConnectionEditorSheet();
        Window.BeginSheet(sheet.Window, result =>
        {
            if ((long)result == (long)NSModalResponse.OK && sheet.Result is { } r)
            {
                _ = CreateAndReloadAsync(r);
            }
        });
    }

    private async Task CreateAndReloadAsync(ConnectionDraft r)
    {
        await _connections.CreateAsync(new ConnectionProfile
        {
            Name = r.Name,
            Host = r.Host,
            Port = r.Port,
            Protocol = r.Protocol,
        });
        await LoadAsync();
    }

    private void OpenSelected()
    {
        if (_sidebarSource?.SelectedConnection is { } c)
        {
            _detail.ShowConnecting(c);
        }
    }
}
