using System.Collections.ObjectModel;
using AppKit;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 连接工作台第三列的核心：连接列表，对齐 Windows 版 <c>ConnectionsPage.xaml</c> 的
/// <c>ConnRowTemplate</c>——真正的多列表格（名称 / 主机 / 协议 / 标签 / 在线 / 最近连接 / 收藏），
/// 而不是单列复合卡片，可一眼扫出哪些在线、哪些离线。列的 Schema 由本类持有并在构造时
/// 装配到 <see cref="NSTableView"/> 上（含表头），调用方只负责把这张表放进布局。
/// </summary>
public sealed class FlatListSource : NSTableViewDelegate
{
    private readonly NSTableView _table;
    private readonly ObservableCollection<ConnectionItemViewModel> _items;

    public FlatListSource(NSTableView table, ObservableCollection<ConnectionItemViewModel> items)
    {
        _table = table;
        _items = items;

        table.HeaderView = new NSTableHeaderView();
        // 列表栏是整个窗口里唯一会随窗口宽度伸缩的一栏（对齐 Windows 的 `*` 列表列），
        // 栏变宽时表格必须有一列跟着吃掉多出来的宽度，否则 6 个固定宽度列右边会空出
        // 一大片死白。FirstColumnOnly 把这件事定死在「名称」列上——只给列自己挂
        // Autoresizing 掩码不够，掩码是否生效还要看表格级的 ColumnAutoresizingStyle。
        table.ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly;
        table.AddColumn(Column("name", "名称", 170, 130, 1000,
            NSTableColumnResizing.UserResizingMask | NSTableColumnResizing.Autoresizing));
        table.AddColumn(Column("host", "主机", 110, 80, 260, NSTableColumnResizing.UserResizingMask));
        table.AddColumn(Column("protocol", "协议", 58, 50, 70, NSTableColumnResizing.None));
        table.AddColumn(Column("tags", "标签", 90, 50, 220, NSTableColumnResizing.UserResizingMask));
        table.AddColumn(Column("status", "在线", 76, 60, 100, NSTableColumnResizing.None));
        // 96 而不是 82：「09-28 21:17」这类 11 字符时间戳在 82 下正好卡在截断边界上
        // （同样长度的串有的完整、有的被截成「09-28 21:…」，看着像数据本身不一致）。
        table.AddColumn(Column("recent", "最近连接", 96, 84, 120, NSTableColumnResizing.None));
        table.AddColumn(Column("favorite", "收藏", 34, 30, 34, NSTableColumnResizing.None));
        table.AddColumn(Column("more", string.Empty, 30, 28, 30, NSTableColumnResizing.None));

        _table.DataSource = new RowCount(this);
        _table.Delegate = this;
        _items.CollectionChanged += (_, _) => Reload();
        Reload();
    }

    public ConnectionItemViewModel? Selected =>
        _table.SelectedRow >= 0 && _table.SelectedRow < _items.Count ? _items[(int)_table.SelectedRow] : null;

    public event EventHandler? SelectionChanged;

    /// <summary>收藏列点击星标：由调用方决定怎么落地（跑 VM 的 ToggleFavoriteCommand 并刷新）。</summary>
    public event EventHandler<ConnectionItemViewModel>? FavoriteToggleRequested;

    /// <summary>行尾「⋯」被点击：调用方在给定锚点视图下方弹出该行的操作菜单。</summary>
    public event EventHandler<(ConnectionItemViewModel Item, NSView Anchor)>? MoreRequested;

    public void Reload() => NSApplication.SharedApplication.BeginInvokeOnMainThread(_table.ReloadData);

    private static NSTableColumn Column(string id, string title, nfloat width, nfloat min, nfloat max, NSTableColumnResizing resizing) => new(id)
    {
        Title = title,
        Width = width,
        MinWidth = min,
        MaxWidth = max,
        ResizingMask = resizing,
        HeaderCell = { Alignment = id is "status" or "recent" or "favorite" ? NSTextAlignment.Center : NSTextAlignment.Left },
    };

    public override nfloat GetRowHeight(NSTableView tableView, nint row) => 40;

    public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
    {
        var item = _items[(int)row];
        return tableColumn?.Identifier switch
        {
            "name" => NameCell(tableView, item),
            "host" => TextCell(tableView, "host", item.HostDisplay, mono: true),
            "protocol" => ProtocolCell(tableView, item),
            "tags" => TagsCell(tableView, item),
            "status" => StatusCell(tableView, item),
            "recent" => TextCell(tableView, "recent", item.LastConnectedCompact, secondary: true, align: NSTextAlignment.Center),
            "favorite" => FavoriteCell(tableView, item),
            "more" => MoreCell(tableView, item),
            _ => new NSView(),
        };
    }

    // ── 标签列：首个标签用标签色的胶囊，其余折叠成「+N」──────────────
    // 对齐 Windows 版的彩色 chip；列宽有限，只铺第一个，避免挤成一坨。

    private NSView TagsCell(NSTableView tableView, ConnectionItemViewModel item)
    {
        const string id = "tags";
        TagRow cell;
        if (tableView.MakeView(id, this) is TagRow reused)
        {
            cell = reused;
        }
        else
        {
            cell = new TagRow { Identifier = id };
            cell.Pill = new PillView(10f);
            cell.Overflow = new NSTextField
            {
                Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(10),
                TextColor = NSColor.TertiaryLabel,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            cell.Empty = new NSTextField
            {
                StringValue = "—",
                Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(12),
                TextColor = NSColor.SecondaryLabel,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            cell.AddSubview(cell.Pill);
            cell.AddSubview(cell.Overflow);
            cell.AddSubview(cell.Empty);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                cell.Pill.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 4),
                cell.Pill.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                cell.Pill.TrailingAnchor.ConstraintLessThanOrEqualTo(cell.Overflow.LeadingAnchor, -4),
                cell.Overflow.TrailingAnchor.ConstraintLessThanOrEqualTo(cell.TrailingAnchor, -4),
                cell.Overflow.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                cell.Empty.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 4),
                cell.Empty.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
            });
        }

        if (item.Tags.Count == 0)
        {
            cell.Pill.Hidden = true;
            cell.Overflow.Hidden = true;
            cell.Empty.Hidden = false;
            cell.ToolTip = null;
            return cell;
        }

        var first = item.Tags[0];
        var color = OrganizationStyle.ColorFromHex(first.Color) ?? NSColor.SystemGray;
        cell.Pill.Hidden = false;
        cell.Pill.Apply(first.Name, color, OrganizationStyle.TagSymbol(first.Icon));
        cell.Empty.Hidden = true;

        // 列宽放不下时其余标签折叠成 +N；完整清单挂 tooltip，悬停可见。
        var hidden = item.Tags.Count - 1 + item.OverflowTagCount;
        cell.Overflow.Hidden = hidden <= 0;
        cell.Overflow.StringValue = hidden > 0 ? $"+{hidden}" : string.Empty;
        cell.ToolTip = string.Join("、", item.Tags.Select(t => t.Name));
        return cell;
    }

    // ── 名称列：图标 + 名称 + 会话状态角标 ─────────────────────────

    private NSView NameCell(NSTableView tableView, ConnectionItemViewModel item)
    {
        const string id = "name";
        NSTableCellView cell;
        NSImageView icon;
        NSView badge;

        if (tableView.MakeView(id, this) is NSTableCellView reused)
        {
            cell = reused;
            icon = (NSImageView)cell.Subviews[0];
            badge = cell.Subviews[2];
        }
        else
        {
            cell = new NSTableCellView { Identifier = id };
            icon = new NSImageView
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                ContentTintColor = NSColor.SecondaryLabel,
                SymbolConfiguration = NSImageSymbolConfiguration.Create(16, NSFontWeight.Regular),
            };
            var name = new NSTextField
            {
                Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(12),
                TextColor = NSColor.Label,
                LineBreakMode = NSLineBreakMode.TruncatingTail,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            badge = ProtocolStyle.StatusBadge();

            cell.AddSubview(icon);
            cell.AddSubview(name);
            cell.AddSubview(badge);
            cell.TextField = name;

            NSLayoutConstraint.ActivateConstraints(new[]
            {
                icon.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 4),
                icon.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                icon.WidthAnchor.ConstraintEqualTo(20),
                icon.HeightAnchor.ConstraintEqualTo(20),
                name.LeadingAnchor.ConstraintEqualTo(icon.TrailingAnchor, 8),
                name.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -4),
                name.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                badge.TrailingAnchor.ConstraintEqualTo(icon.TrailingAnchor),
                badge.BottomAnchor.ConstraintEqualTo(icon.BottomAnchor),
            });
        }

        cell.TextField!.StringValue = item.Name;
        var deviceIcon = DeviceIconCatalog.Get(item.DeviceIconKey);
        if (deviceIcon is not null)
        {
            icon.Image = deviceIcon;
            icon.ContentTintColor = null;
        }
        else
        {
            icon.Image = ProtocolStyle.Symbol(item.Profile.Protocol);
            icon.ContentTintColor = ProtocolStyle.Tint(item.Profile.Protocol);
        }
        ProtocolStyle.ApplyStatus(badge, item.IsConnected, item.IsConnecting);
        return cell;
    }

    // ── 协议列：协议色胶囊（图标 + 缩写），对齐 Windows 的协议徽章 ─────

    private NSView ProtocolCell(NSTableView tableView, ConnectionItemViewModel item)
    {
        var cell = PillCell(tableView, "protocol");
        cell.Pill.Apply(
            item.ProtocolName,
            ProtocolStyle.Tint(item.Profile.Protocol),
            ProtocolStyle.Symbol(item.Profile.Protocol));
        return cell;
    }

    // ── 在线列：状态色胶囊（色点 + 文字），会话实时状态优先、回退在线探测 ──

    private NSView StatusCell(NSTableView tableView, ConnectionItemViewModel item)
    {
        var cell = PillCell(tableView, "status");
        cell.Pill.Apply(item.ConnectionStatusDisplay, StatusColor(item.ConnectionStatusBrushKey), showDot: true);
        cell.ToolTip = item.ConnectionStatusTooltip;
        return cell;
    }

    /// <summary>只装一个胶囊的列（协议 / 在线）共用的 cell 骨架。</summary>
    private PillRow PillCell(NSTableView tableView, string id)
    {
        if (tableView.MakeView(id, this) is PillRow reused)
        {
            return reused;
        }

        var cell = new PillRow { Identifier = id };
        cell.Pill = new PillView(10f);
        cell.AddSubview(cell.Pill);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            cell.Pill.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 4),
            cell.Pill.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
            cell.Pill.TrailingAnchor.ConstraintLessThanOrEqualTo(cell.TrailingAnchor, -4),
        });
        return cell;
    }

    private static NSColor StatusColor(string brushKey) => brushKey switch
    {
        "Status.Success" => NSColor.SystemGreen,
        "Status.Info" => NSColor.SystemBlue,
        "Status.Warning" => NSColor.SystemOrange,
        "Status.Danger" => NSColor.SystemRed,
        _ => NSColor.SystemGray,
    };

    // ── 收藏列：星标按钮，点击切换 ───────────────────────────────────

    private NSView FavoriteCell(NSTableView tableView, ConnectionItemViewModel item)
    {
        const string id = "favorite";
        NSButton button;

        if (tableView.MakeView(id, this) is NSButton reused)
        {
            button = reused;
        }
        else
        {
            button = new NSButton
            {
                Identifier = id,
                Bordered = false,
                ImagePosition = NSCellImagePosition.ImageOnly,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            button.Activated += (_, _) =>
            {
                if (button.Tag >= 0 && button.Tag < _items.Count)
                {
                    FavoriteToggleRequested?.Invoke(this, _items[(int)button.Tag]);
                }
            };
        }

        button.Tag = _items.IndexOf(item);
        button.Image = NSImage.GetSystemSymbol(item.IsFavorite ? "star.fill" : "star", null);
        button.ContentTintColor = item.IsFavorite ? NSColor.SystemYellow : NSColor.TertiaryLabel;
        button.ToolTip = item.FavoriteToggleText;
        return button;
    }

    // ── 「⋯」列：行内打开与右键同一套操作菜单（对齐 Windows 的行尾更多按钮）──

    private NSView MoreCell(NSTableView tableView, ConnectionItemViewModel item)
    {
        const string id = "more";
        NSButton button;

        if (tableView.MakeView(id, this) is NSButton reused)
        {
            button = reused;
        }
        else
        {
            button = new NSButton
            {
                Identifier = id,
                Bordered = false,
                ImagePosition = NSCellImagePosition.ImageOnly,
                Image = NSImage.GetSystemSymbol("ellipsis", null),
                ContentTintColor = NSColor.SecondaryLabel,
                ToolTip = "更多操作",
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            button.Activated += (_, _) =>
            {
                if (button.Tag >= 0 && button.Tag < _items.Count)
                {
                    MoreRequested?.Invoke(this, (_items[(int)button.Tag], button));
                }
            };
        }

        button.Tag = _items.IndexOf(item);
        return button;
    }

    // ── 主机 / 标签等纯文本列 ────────────────────────────────────────

    private NSView TextCell(NSTableView tableView, string id, string text, bool mono = false, bool secondary = false,
        NSTextAlignment align = NSTextAlignment.Left)
    {
        NSTableCellView cell;
        if (tableView.MakeView(id, this) is NSTableCellView reused)
        {
            cell = reused;
        }
        else
        {
            var label = new NSTextField
            {
                Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
                LineBreakMode = NSLineBreakMode.TruncatingTail,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            cell = new NSTableCellView { Identifier = id };
            cell.AddSubview(label);
            cell.TextField = label;
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                label.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 4),
                label.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -4),
                label.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
            });
        }

        cell.TextField!.StringValue = text;
        cell.TextField!.Font = mono ? NSFont.MonospacedSystemFont(11, NSFontWeight.Regular) : NSFont.SystemFontOfSize(12);
        cell.TextField!.TextColor = secondary ? NSColor.SecondaryLabel : NSColor.Label;
        cell.TextField!.Alignment = align;
        return cell;
    }

    public override void SelectionDidChange(Foundation.NSNotification notification)
        => SelectionChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>只有一个胶囊的 cell（协议 / 在线）。</summary>
    private sealed class PillRow : NSTableCellView
    {
        public PillView Pill = null!;
    }

    /// <summary>标签 cell：首个标签的胶囊 + 「+N」+ 无标签时的占位。</summary>
    private sealed class TagRow : NSTableCellView
    {
        public PillView Pill = null!;
        public NSTextField Overflow = null!;
        public NSTextField Empty = null!;
    }

    private sealed class RowCount : NSTableViewDataSource
    {
        private readonly FlatListSource _o;
        public RowCount(FlatListSource o) => _o = o;
        public override nint GetRowCount(NSTableView tableView) => _o._items.Count;
    }
}
