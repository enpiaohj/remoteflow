using AppKit;

namespace RemoteFlow.App.Mac;

/// <summary>主窗口第一列：导航源列表。连接 / 管理两组，路由到中间列表。</summary>
public sealed class NavSidebar : NSViewController
{
    /// <summary>收藏 / 最近连接不再是一级导航项（对齐 Windows「连接工作台」），
    /// 降级为 <see cref="ConnectionListPane"/> 顶部的智能视图切换。</summary>
    public enum Item { Home, Connections, Credentials, Settings }

    private static readonly (Item Item, string Title, string Symbol, bool GroupStart)[] Rows =
    {
        (Item.Home, "首页", "house", true),
        (Item.Connections, "我的连接", "rectangle.stack", false),
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
        // 侧栏顶部的产品标识：强调色圆角块 + 白色图标 + 产品名（对齐设计稿）。
        var brandTile = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, WantsLayer = true };
        brandTile.Layer!.CornerRadius = 6;
        Palette.With(brandTile, () => brandTile.Layer.BackgroundColor = NSColor.ControlAccent.CGColor);
        var brandGlyph = new NSImageView
        {
            Image = NSImage.GetSystemSymbol("display", null),
            ContentTintColor = NSColor.White,
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(12, NSFontWeight.Medium),
        };
        brandTile.AddSubview(brandGlyph);

        var brandLabel = new NSTextField
        {
            StringValue = "RemoteFlow",
            Bordered = false,
            Editable = false,
            Selectable = false,
            DrawsBackground = false,
            Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var versionLabel = new NSTextField
        {
            StringValue = AppVersion,
            Bordered = false,
            Editable = false,
            Selectable = false,
            DrawsBackground = false,
            TextColor = NSColor.SecondaryLabel,
            Font = NSFont.SystemFontOfSize(11, NSFontWeight.Regular),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        // 不用 NSStackView 拼这一行：普通 NSView 没有固有尺寸，stack 会把它压成 0 → 图文重叠。
        fx.AddSubview(brandTile);
        fx.AddSubview(brandLabel);
        fx.AddSubview(versionLabel);

        scroll.TranslatesAutoresizingMaskIntoConstraints = false;
        fx.AddSubview(scroll);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            // 顶部留出标题栏的高度（窗口是 FullSizeContentView，内容会延伸到红黄绿按钮那一行）。
            brandTile.LeadingAnchor.ConstraintEqualTo(fx.LeadingAnchor, 16),
            brandTile.TopAnchor.ConstraintEqualTo(fx.SafeAreaLayoutGuide.TopAnchor, 10),
            brandTile.WidthAnchor.ConstraintEqualTo(24),
            brandTile.HeightAnchor.ConstraintEqualTo(24),
            brandGlyph.CenterXAnchor.ConstraintEqualTo(brandTile.CenterXAnchor),
            brandGlyph.CenterYAnchor.ConstraintEqualTo(brandTile.CenterYAnchor),

            brandLabel.LeadingAnchor.ConstraintEqualTo(brandTile.TrailingAnchor, 8),
            brandLabel.CenterYAnchor.ConstraintEqualTo(brandTile.CenterYAnchor),

            versionLabel.LeadingAnchor.ConstraintEqualTo(brandLabel.TrailingAnchor, 6),
            versionLabel.LastBaselineAnchor.ConstraintEqualTo(brandLabel.LastBaselineAnchor),

            scroll.LeadingAnchor.ConstraintEqualTo(fx.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(fx.TrailingAnchor),
            scroll.TopAnchor.ConstraintEqualTo(brandTile.BottomAnchor, 14),
            scroll.BottomAnchor.ConstraintEqualTo(fx.BottomAnchor),
        });

        View = fx;
    }

    /// <summary>产品版本号，取自 bundle 的 <c>CFBundleShortVersionString</c>
    /// （= csproj 的 <c>ApplicationDisplayVersion</c>，构建时写进 Info.plist）。</summary>
    private static string AppVersion =>
        "v" + (NSBundle.MainBundle.InfoDictionary?["CFBundleShortVersionString"]?.ToString() ?? "?");

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
