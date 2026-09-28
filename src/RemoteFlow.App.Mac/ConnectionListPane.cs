using AppKit;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>「我的连接」工作台的三种智能视图（对齐 Windows 版：收藏 / 最近连接是智能视图导航项，
/// 不再是侧栏一级导航项）。</summary>
public enum ConnectionListView { All, Favorites, Recent }

/// <summary>
/// 主窗口第三列：连接列表，对齐 Windows 版 <c>ConnectionsPage.xaml</c> 的信息架构——
/// 固定页头「连接工作台」+ 全量统计 → 筛选条（协议 / 标签 / 状态 / 排序）→ 多列表格。
/// 分组 / 智能视图导航是独立的第二列（<see cref="ConnectionResourcePane"/>），两者共享同一个
/// <see cref="ConnectionsPageViewModel"/>，各自订阅、互不直接引用。
/// </summary>
public sealed class ConnectionListPane : NSViewController
{
    private readonly ConnectionsPageViewModel _vm;

    private readonly NSTableView _flat = new();
    private readonly NSScrollView _flatScroll;
    private readonly NSSegmentedControl _recentRange;
    private readonly NSStackView _filterBar;
    private readonly NSPopUpButton _protocolPopup = FilterPopup();
    private readonly NSPopUpButton _tagPopup = FilterPopup();
    private readonly NSPopUpButton _presencePopup = FilterPopup();
    private readonly NSPopUpButton _sortPopup = FilterPopup();
    private readonly NSTextField _title = Heading();
    private readonly NSTextField _subtitle = Sub();
    private readonly NSTextField _groupPillLabel = Sub();
    private readonly NSStackView _groupPill;

    private FlatListSource? _flatSource;

    /// <summary>
    /// 会话或在线探测状态变化后重画现有行。使用按行/按列重载而不是 ReloadData，
    /// 保留当前选择与滚动位置；状态既出现在名称角标，也出现在「在线」列，因此两列都要刷新。
    /// </summary>
    public void RefreshRowStatus() => NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
    {
        if (_flat.RowCount > 0)
        {
            var rows = Foundation.NSIndexSet.FromNSRange(
                new Foundation.NSRange(0, _flat.RowCount));
            var columns = new Foundation.NSMutableIndexSet();
            columns.Add(0); // 名称列：会话连接状态角标
            columns.Add(4); // 在线列：探测 / 会话统一状态胶囊
            _flat.ReloadData(rows, columns);
        }
    });

    public event EventHandler<ConnectionItemViewModel>? ConnectionSelected;
    public event EventHandler<ConnectionItemViewModel>? ConnectionActivated;

    /// <summary>每次 LoadAsync 后回填连接的凭据名 / 分组名等外部元数据（VM 不直接依赖凭据服务）。</summary>
    private readonly Func<Task>? _hydrate;

    public ConnectionListPane(ConnectionsPageViewModel vm, Func<Task>? hydrate = null)
    {
        _vm = vm;
        _hydrate = hydrate;

        // ── 连接列表（永远扁平：所有视图 / 分组过滤都归约成同一份 vm.Items）──
        _flat.SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.Regular;
        _flat.RowSizeStyle = NSTableViewRowSizeStyle.Custom;
        _flat.UsesAlternatingRowBackgroundColors = true;
        _flat.Style = NSTableViewStyle.Inset;
        _flat.FocusRingType = NSFocusRingType.None;
        _flat.DoubleClick += (_, _) => ActivateFlat();
        _flat.Menu = new NSMenu { Delegate = new ConnectionRowMenu(this) };
        _flatSource = new FlatListSource(_flat, _vm.Items);
        _flatSource.SelectionChanged += (_, _) =>
        {
            if (_flatSource!.Selected is { } c) ConnectionSelected?.Invoke(this, c);
        };
        _flatSource.FavoriteToggleRequested += (_, c) => RunConnItem(_vm.ToggleFavoriteCommand, c);
        _flatSource.MoreRequested += (_, e) =>
        {
            var menu = new NSMenu();
            FillConnectionMenu(menu, e.Item);
            menu.PopUpMenu(null, new CoreGraphics.CGPoint(0, e.Anchor.Bounds.Height + 2), e.Anchor);
        };
        _flatScroll = Scroll(_flat);
        _flatScroll.TranslatesAutoresizingMaskIntoConstraints = false;

        _vm.Items.CollectionChanged += (_, _) => RefreshSubtitle();

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
            });
        _recentRange.SelectedSegment = 0;
        _recentRange.Hidden = true;

        // ── 筛选工具栏（协议 / 标签 / 在线状态 / 排序，对齐 Windows 顶部四个下拉）──
        // 「最近连接」视图不适用页内筛选（见 VM.ApplyFilter 的短路逻辑），与 _recentRange 互斥显示。
        foreach (var opt in _vm.ProtocolFilterOptions) _protocolPopup.AddItem(ProtocolFilterLabel(opt));
        _protocolPopup.Activated += (_, _) =>
            _vm.ProtocolFilter = _vm.ProtocolFilterOptions[(int)_protocolPopup.IndexOfSelectedItem];

        RebuildTagPopup();
        _tagPopup.Activated += (_, _) =>
            _vm.TagFilter = _vm.TagFilterOptions[(int)_tagPopup.IndexOfSelectedItem];

        foreach (var opt in _vm.PresenceFilterOptions) _presencePopup.AddItem(PresenceFilterLabel(opt));
        _presencePopup.Activated += (_, _) =>
            _vm.PresenceFilter = _vm.PresenceFilterOptions[(int)_presencePopup.IndexOfSelectedItem];
        _presencePopup.Hidden = !_vm.PresenceProbeEnabled;

        foreach (var opt in _vm.SortModeOptions) _sortPopup.AddItem(SortModeLabel(opt));
        _sortPopup.Activated += (_, _) =>
            _vm.SortMode = _vm.SortModeOptions[(int)_sortPopup.IndexOfSelectedItem];

        var filterRow = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Horizontal, Spacing = 6 };
        filterRow.AddArrangedSubview(_protocolPopup);
        filterRow.AddArrangedSubview(_tagPopup);
        filterRow.AddArrangedSubview(_presencePopup);
        filterRow.AddArrangedSubview(_sortPopup);
        _filterBar = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _filterBar.AddArrangedSubview(filterRow);

        // 分组浏览提示：来自第二列（资源导航）的分组选中，一键退出回到「所有设备」。
        var exitGroup = NSButton.CreateButton("退出", () => _vm.ClearGroupFilterCommand.Execute(null));
        exitGroup.BezelStyle = NSBezelStyle.Inline;
        exitGroup.ControlSize = NSControlSize.Small;
        exitGroup.Font = NSFont.SystemFontOfSize(10);
        _groupPillLabel.TextColor = NSColor.ControlAccent;
        _groupPill = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 5,
            Hidden = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _groupPill.AddArrangedSubview(_groupPillLabel);
        _groupPill.AddArrangedSubview(exitGroup);

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(_vm.Filter):
                    _recentRange.Hidden = _vm.Filter != ConnectionFilter.Recent;
                    _filterBar.Hidden = _vm.Filter == ConnectionFilter.Recent;
                    break;
                case nameof(_vm.HasGroupFilter):
                case nameof(_vm.ViewTitle):
                    _groupPill.Hidden = !_vm.HasGroupFilter;
                    _groupPillLabel.StringValue = _vm.ViewTitle;
                    break;
                case nameof(_vm.TagFilterOptions):
                    RebuildTagPopup();
                    break;
                case nameof(_vm.PresenceProbeEnabled):
                    _presencePopup.Hidden = !_vm.PresenceProbeEnabled;
                    RefreshSubtitle();
                    RefreshRowStatus();
                    break;
                case nameof(_vm.WorkbenchSubtitle):
                    // AppKit 表格不是数据绑定控件：ConnectionItemViewModel 虽然已经从
                    // Probing 落定为 Online/Offline，不主动重画「在线」列仍会保留旧胶囊。
                    RefreshSubtitle();
                    RefreshRowStatus();
                    break;
                case nameof(_vm.ProtocolFilter):
                    _protocolPopup.SelectItem(_vm.ProtocolFilterOptions.ToList().IndexOf(_vm.ProtocolFilter));
                    break;
                case nameof(_vm.TagFilter):
                    _tagPopup.SelectItem(Math.Max(0, _vm.TagFilterOptions.ToList().IndexOf(_vm.TagFilter)));
                    break;
                case nameof(_vm.PresenceFilter):
                    _presencePopup.SelectItem(_vm.PresenceFilterOptions.ToList().IndexOf(_vm.PresenceFilter));
                    break;
                case nameof(_vm.SortMode):
                    _sortPopup.SelectItem(_vm.SortModeOptions.ToList().IndexOf(_vm.SortMode));
                    break;
            }
        };

        var titleRow = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 10,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        titleRow.AddArrangedSubview(_title);
        titleRow.AddArrangedSubview(_subtitle);
        titleRow.AddArrangedSubview(_groupPill);
        titleRow.AddArrangedSubview(new NSView());

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
        header.AddArrangedSubview(_filterBar);
        _filterBar.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -28).Active = true;
        header.AddArrangedSubview(_recentRange);

        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        root.AddSubview(header);
        root.AddSubview(_flatScroll);

        NSLayoutConstraint.ActivateConstraints(new[]
        {
            header.LeadingAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.LeadingAnchor),
            header.TrailingAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TrailingAnchor),
            header.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),

            _flatScroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _flatScroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _flatScroll.TopAnchor.ConstraintEqualTo(header.BottomAnchor, 6),
            _flatScroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
        });

        _title.StringValue = "连接工作台";
        RefreshSubtitle();
        View = root;
    }

    // ── 模式切换 ────────────────────────────────────────────────

    /// <summary>重新从数据源加载（新建 / 编辑连接后刷新用，也是首次进入本页时的入口）。</summary>
    public async Task RefreshAsync()
    {
        await _vm.LoadAsync();
        if (_hydrate is not null) await _hydrate();
        _recentRange.Hidden = _vm.Filter != ConnectionFilter.Recent;
        _filterBar.Hidden = _vm.Filter == ConnectionFilter.Recent;
        _groupPill.Hidden = !_vm.HasGroupFilter;
        _groupPillLabel.StringValue = _vm.ViewTitle;
        RefreshSubtitle();
        SelectFirstRow();
    }

    /// <summary>工具栏搜索框联动：写入 VM 搜索词，ApplyFilter 会自动刷新 vm.Items。</summary>
    public void ApplySearch(string text) => _vm.SearchText = text;

    /// <summary>切到指定的智能视图（外部——如首页「查看全部收藏」、设置里的默认页面——都走这个入口）。</summary>
    public Task SetView(ConnectionListView view)
    {
        _vm.SelectedSmartView = view switch
        {
            ConnectionListView.Favorites => _vm.SmartViews[1],
            ConnectionListView.Recent => _vm.SmartViews[2],
            _ => _vm.SmartViews[0],
        };
        return RefreshAsync();
    }

    /// <summary>默认选中列表第一项，并触发选中事件——进入工作台时详情栏就有内容，
    /// 不会停在「选择一个连接」的空态。
    /// <para>
    /// 必须排到下一轮 runloop：<see cref="FlatListSource.Reload"/> 自己是
    /// BeginInvokeOnMainThread 派发的，紧跟着同步选中时表格还没重新加载、行数是 0，
    /// SelectRow 直接落空（真实事故：进入工作台详情栏一直空白，手动点一下才正常）。
    /// </para></summary>
    private void SelectFirstRow() => NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
    {
        if (_vm.Items.Count == 0 || _flat.RowCount == 0)
        {
            return;
        }

        // 已经有选中就别抢（例如用户在加载期间自己点了一行）。
        if (_flat.SelectedRow >= 0)
        {
            return;
        }

        _flat.SelectRow(0, byExtendingSelection: false);
        if (_flatSource?.Selected is { } first) ConnectionSelected?.Invoke(this, first);
    });

    /// <summary>页头副标题：全量统计（对齐 Windows「N 台设备 · M 台在线」，探测关闭时只报总量）。</summary>
    private void RefreshSubtitle() => _subtitle.StringValue = _vm.WorkbenchSubtitle;

    private void ActivateFlat()
    {
        if (_flatSource?.Selected is { } c) ConnectionActivated?.Invoke(this, c);
    }

    // ── 右键菜单：连接列表 ──────────────────────────────────────────

    private sealed class ConnectionRowMenu : NSMenuDelegate
    {
        private readonly ConnectionListPane _pane;
        public ConnectionRowMenu(ConnectionListPane pane) => _pane = pane;

        public override void MenuWillOpen(NSMenu menu)
        {
            menu.RemoveAllItems();
            var row = (int)_pane._flat.ClickedRow;
            if (row < 0 || row >= _pane._vm.Items.Count) return;
            _pane.FillConnectionMenu(menu, _pane._vm.Items[row]);
        }
    }

    /// <summary>装配单条连接的操作菜单。右键菜单和行尾「⋯」按钮共用同一处，
    /// 免得两边菜单项漂移。</summary>
    private void FillConnectionMenu(NSMenu menu, ConnectionItemViewModel conn)
    {
        menu.AddItem(Item("连接", () => ConnectionActivated?.Invoke(this, conn)));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(Item("编辑…", () => RunConnItem(_vm.EditCommand, conn)));
        menu.AddItem(Item("复制连接", () => RunConnItem(_vm.DuplicateCommand, conn)));
        menu.AddItem(Item(conn.IsFavorite ? "取消收藏" : "收藏",
            () => RunConnItem(_vm.ToggleFavoriteCommand, conn)));
        menu.AddItem(Item("测试连接…", () => RunConnItem(_vm.TestConnectionCommand, conn)));

        var move = new NSMenuItem("移动到分组");
        var sub = new NSMenu();
        foreach (var t in _vm.GroupTargets)
        {
            var target = t;
            var mi = new NSMenuItem(new string(' ', target.Depth * 2) + target.Name);
            mi.Activated += (_, _) => RunMoveToGroup(conn, target.GroupId);
            sub.AddItem(mi);
        }
        move.Submenu = sub;
        move.Enabled = sub.Count > 0;
        menu.AddItem(move);

        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(Item("删除…", () => RunConnItem(_vm.DeleteCommand, conn)));
    }

    private static NSMenuItem Item(string title, Action action)
    {
        var item = new NSMenuItem(title);
        item.Activated += (_, _) => action();
        return item;
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

    /// <summary>标签下拉的选项随 VM.TagFilterOptions（LoadAsync 重建）变化，需要整体重建菜单项。</summary>
    private void RebuildTagPopup()
    {
        var previous = _vm.TagFilter;
        _tagPopup.RemoveAllItems();
        foreach (var opt in _vm.TagFilterOptions) _tagPopup.AddItem(opt.Name);
        var index = _vm.TagFilterOptions.ToList().IndexOf(previous);
        _tagPopup.SelectItem(index < 0 ? 0 : index);
    }

    // ── helpers ─────────────────────────────────────────────────

    private static NSPopUpButton FilterPopup() => new(CoreGraphics.CGRect.Empty, pullsDown: false)
    {
        ControlSize = NSControlSize.Small,
        Font = NSFont.SystemFontOfSize(11),
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private static string ProtocolFilterLabel(ProtocolFilterOption option) => option switch
    {
        ProtocolFilterOption.Rdp => "RDP",
        ProtocolFilterOption.Ssh => "SSH",
        ProtocolFilterOption.Vnc => "VNC",
        _ => "全部协议",
    };

    private static string PresenceFilterLabel(PresenceFilterOption option) => option switch
    {
        PresenceFilterOption.Online => "仅在线",
        PresenceFilterOption.Offline => "仅离线",
        _ => "全部状态",
    };

    private static string SortModeLabel(ConnectionSortMode mode) => mode switch
    {
        ConnectionSortMode.LastConnected => "按最近连接排序",
        ConnectionSortMode.Protocol => "按协议排序",
        _ => "按名称排序",
    };

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
        Font = NSFont.SystemFontOfSize(15, NSFontWeight.Bold),
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
