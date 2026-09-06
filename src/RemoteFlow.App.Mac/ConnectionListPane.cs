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
    private readonly NSTextField _title = Heading();
    private readonly NSTextField _count = Sub();

    private ConnectionTreeSource? _treeSource;
    private FlatListSource? _flatSource;

    public event EventHandler<ConnectionItemViewModel>? ConnectionSelected;
    public event EventHandler<ConnectionItemViewModel>? ConnectionActivated;

    public ConnectionListPane(ConnectionsPageViewModel vm)
    {
        _vm = vm;

        _tree.HeaderView = null;
        _tree.SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.SourceList;
        _tree.RowSizeStyle = NSTableViewRowSizeStyle.Custom;
        _tree.RowHeight = 44;
        _tree.BackgroundColor = NSColor.Clear;
        _tree.AddColumn(new NSTableColumn("c") { ResizingMask = NSTableColumnResizing.Autoresizing });
        _tree.OutlineTableColumn = _tree.TableColumns()[0];
        _tree.DoubleClick += (_, _) => ActivateTree();

        _flat.HeaderView = null;
        _flat.SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.Regular;
        _flat.RowSizeStyle = NSTableViewRowSizeStyle.Custom;
        _flat.RowHeight = 48;
        _flat.BackgroundColor = NSColor.Clear;
        _flat.Style = NSTableViewStyle.Inset;
        _flat.AddColumn(new NSTableColumn("c") { ResizingMask = NSTableColumnResizing.Autoresizing });
        _flat.DoubleClick += (_, _) => ActivateFlat();

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

        var header = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 2,
            EdgeInsets = new NSEdgeInsets(10, 14, 8, 14),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        header.AddArrangedSubview(_title);
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

    public async Task ShowConnectionsAsync()
    {
        _title.StringValue = "我的连接";
        _recentRange.Hidden = true;
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
        _title.StringValue = "收藏";
        _recentRange.Hidden = true;
        _vm.Filter = ConnectionFilter.Favorites;
        await _vm.LoadAsync();
        MountFlat();
    }

    public async Task ShowRecentAsync()
    {
        _title.StringValue = "最近连接";
        _recentRange.Hidden = false;
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
