using AppKit;

namespace RemoteFlow.App.Mac;

/// <summary>主窗口第一列：导航源列表。连接 / 管理两组，路由到中间列表。</summary>
public sealed class NavSidebar : NSViewController
{
    public enum Item { Home, Connections, Favorites, Recent, Credentials, Settings }

    private static readonly (Item Item, string Title, string Symbol, bool GroupStart)[] Rows =
    {
        (Item.Home, "首页", "house", true),
        (Item.Connections, "我的连接", "rectangle.stack", false),
        (Item.Favorites, "收藏", "star", false),
        (Item.Recent, "最近连接", "clock.arrow.circlepath", false),
        (Item.Credentials, "凭据", "key", true),
        (Item.Settings, "设置", "gearshape", false),
    };

    private readonly NSTableView _table = new();

    public event EventHandler<Item>? Selected;

    public NavSidebar()
    {
        _table.HeaderView = null;
        _table.SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.SourceList;
        _table.RowSizeStyle = NSTableViewRowSizeStyle.Default;
        _table.BackgroundColor = NSColor.Clear;
        _table.FloatsGroupRows = false;
        _table.AddColumn(new NSTableColumn("n") { ResizingMask = NSTableColumnResizing.Autoresizing });
        _table.IntercellSpacing = new CoreGraphics.CGSize(0, 3);

        var source = new Source(this);
        _table.DataSource = new RowCount();
        _table.Delegate = source;

        var scroll = new NSScrollView
        {
            DocumentView = _table,
            DrawsBackground = false,
            HasVerticalScroller = false,
            AutomaticallyAdjustsContentInsets = true,
        };

        var fx = new NSVisualEffectView
        {
            Material = NSVisualEffectMaterial.Sidebar,
            BlendingMode = NSVisualEffectBlendingMode.BehindWindow,
            State = NSVisualEffectState.FollowsWindowActiveState,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        scroll.TranslatesAutoresizingMaskIntoConstraints = false;
        fx.AddSubview(scroll);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            scroll.LeadingAnchor.ConstraintEqualTo(fx.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(fx.TrailingAnchor),
            scroll.TopAnchor.ConstraintEqualTo(fx.TopAnchor, 6),
            scroll.BottomAnchor.ConstraintEqualTo(fx.BottomAnchor),
        });

        View = fx;
    }

    /// <summary>按导航项定位并选中对应行（触发 <see cref="Selected"/>）。</summary>
    public void Select(Item item)
    {
        var row = Array.FindIndex(Rows, r => r.Item == item);
        if (row >= 0)
        {
            _table.SelectRow(row, byExtendingSelection: false);
        }
    }

    private sealed class RowCount : NSTableViewDataSource
    {
        public override nint GetRowCount(NSTableView tableView) => Rows.Length;
    }

    private sealed class Source : NSTableViewDelegate
    {
        private readonly NavSidebar _owner;
        public Source(NavSidebar owner) => _owner = owner;

        public override bool ShouldSelectRow(NSTableView tableView, nint row) => true;

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var (item, title, symbol, _) = Rows[(int)row];
            const string id = "nav";
            if (tableView.MakeView(id, _owner) is not NSTableCellView cell)
            {
                var icon = new NSImageView
                {
                    TranslatesAutoresizingMaskIntoConstraints = false,
                    SymbolConfiguration = NSImageSymbolConfiguration.Create(14, NSFontWeight.Regular),
                };
                var label = new NSTextField
                {
                    Bordered = false,
                    Editable = false,
                    Selectable = false,
                    DrawsBackground = false,
                    Font = NSFont.SystemFontOfSize(13),
                    TranslatesAutoresizingMaskIntoConstraints = false,
                    LineBreakMode = NSLineBreakMode.TruncatingTail,
                };
                cell = new NSTableCellView { Identifier = id };
                cell.AddSubview(icon);
                cell.AddSubview(label);
                cell.ImageView = icon;
                cell.TextField = label;
                NSLayoutConstraint.ActivateConstraints(new[]
                {
                    icon.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 4),
                    icon.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                    icon.WidthAnchor.ConstraintEqualTo(18),
                    label.LeadingAnchor.ConstraintEqualTo(icon.TrailingAnchor, 7),
                    label.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6),
                    label.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                });
            }

            cell.ImageView!.Image = NSImage.GetSystemSymbol(symbol, null);
            cell.TextField!.StringValue = title;
            return cell;
        }

        public override NSTableRowView? CoreGetRowView(NSTableView tableView, nint row) => null;

        public override void SelectionDidChange(Foundation.NSNotification notification)
        {
            var t = (NSTableView)notification.Object;
            if (t.SelectedRow >= 0)
            {
                _owner.Selected?.Invoke(_owner, Rows[(int)t.SelectedRow].Item);
            }
        }
    }
}
