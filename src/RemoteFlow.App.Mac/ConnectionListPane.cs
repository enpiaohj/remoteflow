using AppKit;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>「我的连接」工作台的三种智能视图（对齐 Windows 版：收藏 / 最近连接已降级为
/// 工作台内视图切换，不再是侧栏一级导航项）。</summary>
public enum ConnectionListView { All, Favorites, Recent }

/// <summary>
/// 主窗口第二列：连接列表。顶部分段控制切换三种形态：
/// 全部（分组树）/ 收藏（扁平）/ 最近（扁平 + 时间分段）。
/// 均绑同一 <see cref="ConnectionsPageViewModel"/>（切 Filter）。
/// </summary>
public sealed class ConnectionListPane : NSViewController
{
    private readonly ConnectionsPageViewModel _vm;

    private readonly NSOutlineView _tree = new();
    private readonly NSTableView _flat = new();
    private readonly NSScrollView _treeScroll;
    private readonly NSScrollView _flatScroll;
    private readonly NSSegmentedControl _viewSwitch;
    private readonly NSSegmentedControl _recentRange;
    private NSButton _addGroup = null!;
    private readonly NSTextField _title = Heading();
    private readonly NSTextField _count = Sub();

    private ConnectionTreeSource? _treeSource;
    private FlatListSource? _flatSource;

    /// <summary>
    /// 会话状态变化后重画行（在线徽标）。只重画、不重建数据源，避免打断选中与滚动位置。
    /// </summary>
    public void RefreshRowStatus() => NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
    {
        var col = Foundation.NSIndexSet.FromIndex(0);
        if (_tree.RowCount > 0)
        {
            _tree.ReloadData(Foundation.NSIndexSet.FromNSRange(new Foundation.NSRange(0, _tree.RowCount)), col);
        }

        if (_flat.RowCount > 0)
        {
            _flat.ReloadData(Foundation.NSIndexSet.FromNSRange(new Foundation.NSRange(0, _flat.RowCount)), col);
        }
    });
    private Func<Task> _reload;

    public event EventHandler<ConnectionItemViewModel>? ConnectionSelected;
    public event EventHandler<ConnectionItemViewModel>? ConnectionActivated;

    /// <summary>每次 LoadAsync 后回填连接的凭据名 / 分组名等外部元数据（VM 不直接依赖凭据服务）。</summary>
    private readonly Func<Task>? _hydrate;

    public ConnectionListPane(ConnectionsPageViewModel vm, Func<Task>? hydrate = null)
    {
        _vm = vm;
        _hydrate = hydrate;
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
            new[] { "今天", "近 7 天", "全部" }, NSSegmentSwitchTracking.SelectOne,
            () =>
            {
                _vm.RecentRange = _recentRange.SelectedSegment switch
                {
                    1 => RecentRange.Week,
                    2 => RecentRange.All,
                    _ => RecentRange.Today,
                };
                _flatSource?.Reload();
                RefreshCount();
            });
        _recentRange.SelectedSegment = 0;
        _recentRange.Hidden = true;

        _addGroup = new NSButton
        {
            Image = NSImage.GetSystemSymbol("folder.badge.plus", null),
            BezelStyle = NSBezelStyle.TexturedRounded,
            ToolTip = "新建分组",
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _addGroup.Activated += (_, _) => _ = RunGroupCreateAsync(null);

        // 智能视图切换（对齐 Windows「连接工作台」）：收藏 / 最近连接不再是侧栏一级导航项，
        // 降级为这里的三段切换，跟「我的连接」共用同一块工作区。
        _viewSwitch = NSSegmentedControl.FromLabels(
            new[] { "全部", "收藏", "最近" }, NSSegmentSwitchTracking.SelectOne,
            () => _ = SetView((ConnectionListView)_viewSwitch.SelectedSegment));
        _viewSwitch.SelectedSegment = 0;
        _viewSwitch.TranslatesAutoresizingMaskIntoConstraints = false;

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
            Spacing = 8,
            EdgeInsets = new NSEdgeInsets(10, 14, 8, 14),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        header.AddArrangedSubview(titleRow);
        titleRow.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -28).Active = true;
        header.AddArrangedSubview(_viewSwitch);
        _viewSwitch.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -28).Active = true;
        header.AddArrangedSubview(_count);
        header.AddArrangedSubview(_recentRange);

        // 列表列不刷底色，露出窗口的系统背景 —— 与左侧栏（毛玻璃）保持同调，不抢眼。
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

        _title.StringValue = "我的连接";
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

    /// <summary>切到指定的智能视图，并同步顶部分段控制的选中态
    /// （外部——如首页「查看全部收藏」、设置里的默认页面——都走这个入口）。</summary>
    public Task SetView(ConnectionListView view)
    {
        _viewSwitch.SelectedSegment = (int)view;
        return view switch
        {
            ConnectionListView.Favorites => ShowFavoritesAsync(),
            ConnectionListView.Recent => ShowRecentAsync(),
            _ => ShowConnectionsAsync(),
        };
    }

    private async Task ShowConnectionsAsync()
    {
        _reload = ShowConnectionsAsync;
        _recentRange.Hidden = true;
        _addGroup.Hidden = false;
        _vm.Filter = ConnectionFilter.All;
        await _vm.LoadAsync();
        if (_hydrate is not null) await _hydrate();

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
        SelectFirstTree();
    }

    /// <summary>选中树里第一个连接行（分组头跳过），并触发选中事件。</summary>
    private void SelectFirstTree()
    {
        for (nint r = 0; r < _tree.RowCount; r++)
        {
            if (_treeSource?.RowObject(_tree, r) is ConnectionItemViewModel)
            {
                _tree.SelectRow(r, byExtendingSelection: false);
                if (_treeSource?.SelectedConnection is { } c)
                {
                    ConnectionSelected?.Invoke(this, c);
                }

                return;
            }
        }
    }

    private async Task ShowFavoritesAsync()
    {
        _reload = ShowFavoritesAsync;
        _recentRange.Hidden = true;
        _addGroup.Hidden = true;
        _vm.Filter = ConnectionFilter.Favorites;
        await _vm.LoadAsync();
        if (_hydrate is not null) await _hydrate();
        MountFlat();
    }

    private async Task ShowRecentAsync()
    {
        _reload = ShowRecentAsync;
        _recentRange.Hidden = false;
        _addGroup.Hidden = true;
        _vm.Filter = ConnectionFilter.Recent;
        await _vm.LoadAsync();
        if (_hydrate is not null) await _hydrate();
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

        // 默认选中第一项（最近连接列表按最近一次连接倒序 → 即最后连过的那台）。
        if (_vm.Items.Count > 0)
        {
            _flat.SelectRow(0, byExtendingSelection: false);
            if (_flatSource.Selected is { } first)
            {
                ConnectionSelected?.Invoke(this, first);
            }
        }
    }

    private void RefreshCount()
    {
        // 标题固定是「我的连接」，不能再靠它判断当前视图——改用分段控制的选中态。
        if (_viewSwitch.SelectedSegment == (int)ConnectionListView.All)
        {
            var treeN = _vm.GroupNodes.Sum(g => g.TotalCount);
            _count.StringValue = treeN == 0 ? "暂无连接" : $"{treeN} 个连接";
            return;
        }

        var n = _vm.Items.Count;
        var unit = _viewSwitch.SelectedSegment == (int)ConnectionListView.Favorites ? "个收藏" : "个连接";
        _count.StringValue = n == 0 ? "暂无连接" : $"{n} {unit}";
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
            menu.AddItem(Item("复制连接", () => _pane.RunConnItem(_pane._vm.DuplicateCommand, conn)));
            menu.AddItem(Item(conn.IsFavorite ? "取消收藏" : "收藏",
                () => _pane.RunConnItem(_pane._vm.ToggleFavoriteCommand, conn)));
            menu.AddItem(Item("测试连接…", () => _pane.RunConnItem(_pane._vm.TestConnectionCommand, conn)));

            var move = new NSMenuItem("移动到分组");
            var sub = new NSMenu();
            foreach (var t in _pane._vm.GroupTargets)
            {
                var target = t;
                var mi = new NSMenuItem(new string(' ', target.Depth * 2) + target.Name);
                mi.Activated += (_, _) => _pane.RunMoveToGroup(conn, target.GroupId);
                sub.AddItem(mi);
            }
            move.Submenu = sub;
            move.Enabled = sub.Count > 0;
            menu.AddItem(move);

            menu.AddItem(NSMenuItem.SeparatorItem);
            menu.AddItem(Item("删除…", () => _pane.RunConnItem(_pane._vm.DeleteCommand, conn)));
        }

        private void BuildGroupMenu(NSMenu menu, ConnectionGroupNodeViewModel group)
        {
            menu.AddItem(Item("新建子分组…", () => _pane.RunGroupItem(_pane._vm.CreateChildGroupCommand, group)));
            menu.AddItem(Item("重命名…", () => _pane.RunGroupItem(_pane._vm.RenameGroupCommand, group)));
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
        // async void：任何异常逃逸都会变成进程级崩溃，刷新也得包进来。
        try
        {
            await command.ExecuteAsync(conn);
            await RefreshAsync();
        }
        catch
        {
            // 命令内部已负责用户提示。
        }
    }

    private async void RunMoveToGroup(ConnectionItemViewModel conn, Guid? groupId)
    {
        // async void：任何异常逃逸都会变成进程级崩溃，刷新也得包进来。
        try
        {
            await _vm.MoveConnectionToGroupAsync(conn, groupId);
            await RefreshAsync();
        }
        catch
        {
            // VM 内部已负责用户提示。
        }
    }

    private async void RunGroupItem(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command, ConnectionGroupNodeViewModel group)
    {
        // async void：任何异常逃逸都会变成进程级崩溃，刷新也得包进来。
        try
        {
            await command.ExecuteAsync(group);
            await RefreshAsync();
        }
        catch
        {
            // 命令内部已负责用户提示。
        }
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
