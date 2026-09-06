using AppKit;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 主窗口第二列：连接列表。按导航项切换三种形态：
/// 我的连接（分组树）/ 收藏（扁平）/ 最近连接（扁平 + 时间分段）。
/// 均绑同一 <see cref="ConnectionsPageViewModel"/>（切 Filter）。
/// </summary>
public sealed class ConnectionListPane : NSViewController
{
    private readonly ConnectionsPageViewModel _vm;

    private readonly NSOutlineView _tree = new();
    private readonly NSTableView _flat = new();
    private readonly NSScrollView _treeScroll;
    private readonly NSScrollView _flatScroll;
    private readonly NSSegmentedControl _recentRange;
    private NSButton _addGroup = null!;
    private readonly NSTextField _title = Heading();
    private readonly NSTextField _count = Sub();

    private ConnectionTreeSource? _treeSource;
    private FlatListSource? _flatSource;
    private Func<Task> _reload;

    public event EventHandler<ConnectionItemViewModel>? ConnectionSelected;
    public event EventHandler<ConnectionItemViewModel>? ConnectionActivated;

    public ConnectionListPane(ConnectionsPageViewModel vm)
    {
        _vm = vm;
        _reload = ShowConnectionsAsync;

        _tree.HeaderView = null;
        _tree.SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.SourceList;
        _tree.RowSizeStyle = NSTableViewRowSizeStyle.Custom;
        _tree.RowHeight = 44;
        _tree.BackgroundColor = NSColor.Clear;
        _tree.AddColumn(new NSTableColumn("c") { ResizingMask = NSTableColumnResizing.Autoresizing });
        _tree.OutlineTableColumn = _tree.TableColumns()[0];
        _tree.DoubleClick += (_, _) => ActivateTree();
        _tree.Menu = new NSMenu { Delegate = new RowMenu(this, flat: false) };

        _flat.HeaderView = null;
        _flat.SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.Regular;
        _flat.RowSizeStyle = NSTableViewRowSizeStyle.Custom;
        _flat.RowHeight = 48;
        _flat.BackgroundColor = NSColor.Clear;
        _flat.Style = NSTableViewStyle.Inset;
        _flat.AddColumn(new NSTableColumn("c") { ResizingMask = NSTableColumnResizing.Autoresizing });
        _flat.DoubleClick += (_, _) => ActivateFlat();
        _flat.Menu = new NSMenu { Delegate = new RowMenu(this, flat: true) };

        _treeScroll = Scroll(_tree);
        _flatScroll = Scroll(_flat);
        _flatScroll.Hidden = true;

        _recentRange = NSSegmentedControl.FromLabels(
            new[] { "今天", "7 天", "全部" }, NSSegmentSwitchTracking.SelectOne,
            () => { });
        _recentRange.SelectedSegment = 0;
        _recentRange.Hidden = true;
        _recentRange.Activated += (_, _) =>
        {
            _vm.RecentRange = _recentRange.SelectedSegment switch
            {
                1 => RecentRange.Week,
                2 => RecentRange.All,
                _ => RecentRange.Today,
            };
            _flatSource?.Reload();
            RefreshCount();
        };

        _addGroup = new NSButton
        {
            Image = NSImage.GetSystemSymbol("folder.badge.plus", null),
            BezelStyle = NSBezelStyle.TexturedRounded,
            ToolTip = "新建分组",
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _addGroup.Activated += (_, _) => _ = RunGroupCreateAsync(null);

        var titleRow = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        titleRow.AddArrangedSubview(_title);
        titleRow.AddArrangedSubview(new NSView());
        titleRow.AddArrangedSubview(_addGroup);

        var header = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 2,
            EdgeInsets = new NSEdgeInsets(10, 14, 8, 14),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        header.AddArrangedSubview(titleRow);
        titleRow.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -28).Active = true;
        header.AddArrangedSubview(_count);
        header.AddArrangedSubview(_recentRange);

        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        root.AddSubview(header);
        root.AddSubview(_treeScroll);
        root.AddSubview(_flatScroll);
        _treeScroll.TranslatesAutoresizingMaskIntoConstraints = false;
        _flatScroll.TranslatesAutoresizingMaskIntoConstraints = false;

        NSLayoutConstraint.ActivateConstraints(new[]
        {
            header.LeadingAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.LeadingAnchor),
            header.TrailingAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TrailingAnchor),
            header.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),

            _treeScroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _treeScroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _treeScroll.TopAnchor.ConstraintEqualTo(header.BottomAnchor, 2),
            _treeScroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),

            _flatScroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _flatScroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _flatScroll.TopAnchor.ConstraintEqualTo(header.BottomAnchor, 2),
            _flatScroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
        });

        View = root;
    }

    // ── 模式切换 ────────────────────────────────────────────────

    /// <summary>按当前形态重新加载（新建 / 编辑连接后刷新用）。</summary>
    public Task RefreshAsync() => _reload();

    /// <summary>工具栏搜索框联动：写入 VM 搜索词并就地刷新当前视图。</summary>
    public void ApplySearch(string text)
    {
        _vm.SearchText = text;
        _tree.ReloadData();
        _tree.ExpandItem(null, expandChildren: true);
        _flatSource?.Reload();
        RefreshCount();
    }

    public async Task ShowConnectionsAsync()
    {
        _reload = ShowConnectionsAsync;
        _title.StringValue = "我的连接";
        _recentRange.Hidden = true;
        _addGroup.Hidden = false;
        _vm.Filter = ConnectionFilter.All;
        await _vm.LoadAsync();

        _treeSource = new ConnectionTreeSource(_vm);
        _tree.DataSource = _treeSource;
        _tree.Delegate = _treeSource;
        _tree.ReloadData();
        _tree.ExpandItem(null, expandChildren: true);
        _treeSource.SelectionChanged += (_, _) =>
        {
            if (_treeSource.SelectedConnection is { } c) ConnectionSelected?.Invoke(this, c);
        };

        _treeScroll.Hidden = false;
        _flatScroll.Hidden = true;
        RefreshCount();
    }

    public async Task ShowFavoritesAsync()
    {
        _reload = ShowFavoritesAsync;
        _title.StringValue = "收藏";
        _recentRange.Hidden = true;
        _addGroup.Hidden = true;
        _vm.Filter = ConnectionFilter.Favorites;
        await _vm.LoadAsync();
        MountFlat();
    }

    public async Task ShowRecentAsync()
    {
        _reload = ShowRecentAsync;
        _title.StringValue = "最近连接";
        _recentRange.Hidden = false;
        _addGroup.Hidden = true;
        _vm.Filter = ConnectionFilter.Recent;
        await _vm.LoadAsync();
        MountFlat();
    }

    private void MountFlat()
    {
        _flatSource = new FlatListSource(_flat, _vm.Items);
        _flatSource.SelectionChanged += (_, _) =>
        {
            if (_flatSource.Selected is { } c) ConnectionSelected?.Invoke(this, c);
        };
        _treeScroll.Hidden = true;
        _flatScroll.Hidden = false;
        RefreshCount();
    }

    private void RefreshCount()
    {
        var n = _vm.Items.Count(_ => true);
        var treeN = _vm.GroupNodes.Sum(g => g.TotalCount);
        _count.StringValue = _title.StringValue == "我的连接"
            ? (treeN == 0 ? "暂无连接" : $"{treeN} 个连接")
            : (_vm.Items.Count == 0 ? "暂无连接" : $"{_vm.Items.Count} 个连接");
    }

    private void ActivateTree()
    {
        if (_treeSource?.SelectedConnection is { } c) ConnectionActivated?.Invoke(this, c);
    }

    private void ActivateFlat()
    {
        if (_flatSource?.Selected is { } c) ConnectionActivated?.Invoke(this, c);
    }

    // ── 右键菜单 ────────────────────────────────────────────────

    private sealed class RowMenu : NSMenuDelegate
    {
        private readonly ConnectionListPane _pane;
        private readonly bool _flat;

        public RowMenu(ConnectionListPane pane, bool flat)
        {
            _pane = pane;
            _flat = flat;
        }

        public override void MenuWillOpen(NSMenu menu)
        {
            menu.RemoveAllItems();

            var (conn, group) = TargetRow();
            if (conn is not null)
            {
                BuildConnectionMenu(menu, conn);
            }
            else if (group is not null && !_flat)
            {
                BuildGroupMenu(menu, group);
            }
        }

        private (ConnectionItemViewModel? Conn, ConnectionGroupNodeViewModel? Group) TargetRow()
        {
            if (_flat)
            {
                var r = (int)_pane._flat.ClickedRow;
                return r >= 0 && r < _pane._vm.Items.Count ? (_pane._vm.Items[r], null) : (null, null);
            }

            var row = _pane._tree.ClickedRow;
            return _pane._treeSource?.RowObject(_pane._tree, row) switch
            {
                ConnectionItemViewModel c => (c, null),
                ConnectionGroupNodeViewModel g => (null, g),
                _ => (null, null),
            };
        }

        private void BuildConnectionMenu(NSMenu menu, ConnectionItemViewModel conn)
        {
            menu.AddItem(Item("连接", () => _pane.ConnectionActivated?.Invoke(_pane, conn)));
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(Item("编辑…", () => _pane.RunConnItem(_pane._vm.EditCommand, conn)));
            menu.AddItem(Item("复制", () => _pane.RunConnItem(_pane._vm.DuplicateCommand, conn)));
            menu.AddItem(Item(conn.IsFavorite ? "取消收藏" : "收藏",
                () => _pane.RunConnItem(_pane._vm.ToggleFavoriteCommand, conn)));
            menu.AddItem(Item("测试连接…", () => _pane.RunConnItem(_pane._vm.TestConnectionCommand, conn)));
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(Item("删除…", () => _pane.RunConnItem(_pane._vm.DeleteCommand, conn)));
        }

        private void BuildGroupMenu(NSMenu menu, ConnectionGroupNodeViewModel group)
        {
            menu.AddItem(Item("新建子分组…", () => _pane.RunGroupItem(_pane._vm.CreateChildGroupCommand, group)));
            menu.AddItem(Item("重命名…", () => _pane.RunGroupItem(_pane._vm.RenameGroupCommand, group)));
            menu.AddItem(Item("设为默认分组", () => _pane.RunGroupItem(_pane._vm.SetDefaultGroupCommand, group)));
            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(Item("删除…", () => _pane.RunGroupItem(_pane._vm.DeleteGroupCommand, group)));
        }

        private static NSMenuItem Item(string title, Action action)
        {
            var item = new NSMenuItem(title);
            item.Activated += (_, _) => action();
            return item;
        }
    }

    private async void RunConnItem(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command, ConnectionItemViewModel conn)
    {
        try
        {
            await command.ExecuteAsync(conn);
        }
        catch
        {
            // 命令内部已负责用户提示。
        }

        await RefreshAsync();
    }

    private async void RunGroupItem(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command, ConnectionGroupNodeViewModel group)
    {
        try
        {
            await command.ExecuteAsync(group);
        }
        catch
        {
            // 命令内部已负责用户提示。
        }

        await RefreshAsync();
    }

    private async Task RunGroupCreateAsync(ConnectionGroupNodeViewModel? parent)
    {
        try
        {
            await _vm.CreateGroupCommand.ExecuteAsync(null);
        }
        catch
        {
            // 命令内部已负责用户提示。
        }

        await RefreshAsync();
    }

    // ── helpers ─────────────────────────────────────────────────

    private static NSScrollView Scroll(NSView doc) => new()
    {
        DocumentView = doc,
        DrawsBackground = false,
        HasVerticalScroller = true,
        AutomaticallyAdjustsContentInsets = true,
    };

    private static NSTextField Heading() => new()
    {
        Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(17, NSFontWeight.Bold),
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static NSTextField Sub() => new()
    {
        Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(11),
        TextColor = NSColor.SecondaryLabel,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };
}
