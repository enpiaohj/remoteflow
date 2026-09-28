using AppKit;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 源列表侧栏数据源：<see cref="ConnectionsPageViewModel.GroupNodes"/> 的分组树
/// （分组 → 子分组 / 连接）。承担 DataSource（层级）与 Delegate（cell 视图）。
/// </summary>
public sealed class ConnectionTreeSource : NSOutlineViewDelegate, INSOutlineViewDataSource
{
    private readonly ConnectionsPageViewModel _vm;

    public ConnectionTreeSource(ConnectionsPageViewModel vm) => _vm = vm;

    public ConnectionItemViewModel? SelectedConnection { get; private set; }

    public event EventHandler? SelectionChanged;

    // ── DataSource ──────────────────────────────────────────────

    public nint GetChildrenCount(NSOutlineView outlineView, NSObject? item) => item switch
    {
        null => _vm.GroupNodes.Count,
        NodeRef { Node: { } n } => n.ChildGroups.Count + n.Connections.Count,
        _ => 0,
    };

    public NSObject GetChild(NSOutlineView outlineView, nint childIndex, NSObject? item)
    {
        if (item is null)
        {
            return new NodeRef(_vm.GroupNodes[(int)childIndex]);
        }

        var node = ((NodeRef)item).Node!;
        if (childIndex < node.ChildGroups.Count)
        {
            return new NodeRef(node.ChildGroups[(int)childIndex]);
        }

        return new ConnRef(node.Connections[(int)childIndex - node.ChildGroups.Count]);
    }

    public bool ItemExpandable(NSOutlineView outlineView, NSObject? item)
        => item is NodeRef { Node: { } n } && (n.ChildGroups.Count + n.Connections.Count) > 0;

    // ── Delegate ────────────────────────────────────────────────

    public override bool IsGroupItem(NSOutlineView outlineView, NSObject item) => item is NodeRef;

    public override bool ShouldSelectItem(NSOutlineView outlineView, NSObject item) => item is ConnRef;

    public override nfloat GetRowHeight(NSOutlineView outlineView, NSObject item)
        => item is NodeRef ? 24 : 44;

    public override NSView GetView(NSOutlineView outlineView, NSTableColumn tableColumn, NSObject item)
        => item is NodeRef nr ? GroupCell(outlineView, nr.Node!) : ConnectionCell(outlineView, ((ConnRef)item).Item!);

    public override void SelectionDidChange(NSNotification notification)
    {
        var outline = (NSOutlineView)notification.Object;
        SelectedConnection = outline.SelectedRow >= 0 && outline.ItemAtRow(outline.SelectedRow) is ConnRef c
            ? c.Item
            : null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>行对象：连接行返回 <see cref="ConnectionItemViewModel"/>，分组行返回 <see cref="ConnectionGroupNodeViewModel"/>。</summary>
    public object? RowObject(NSOutlineView outline, nint row)
        => row < 0
            ? null
            : outline.ItemAtRow(row) switch
            {
                ConnRef c => c.Item,
                NodeRef n => n.Node,
                _ => null,
            };

    // ── cell ────────────────────────────────────────────────────

    private static NSView GroupCell(NSOutlineView outline, ConnectionGroupNodeViewModel node)
    {
        const string id = "group";
        if (outline.MakeView(id, outline) is not NSTableCellView cell)
        {
            var label = new NSTextField
            {
                Identifier = "t",
                Bordered = false,
                Editable = false,
                Selectable = false,
                DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(11, NSFontWeight.Semibold),
                TextColor = NSColor.SecondaryLabel,
                TranslatesAutoresizingMaskIntoConstraints = false,
                LineBreakMode = NSLineBreakMode.TruncatingTail,
            };
            cell = new NSTableCellView { Identifier = id };
            cell.AddSubview(label);
            cell.TextField = label;
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                label.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor),
                label.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6),
                label.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
            });
        }

        var name = node.IsUngrouped ? "未分组" : node.Name;
        cell.TextField!.StringValue = $"{name.ToUpperInvariant()}   {node.TotalCount}";
        return cell;
    }

    private static NSView ConnectionCell(NSOutlineView outline, ConnectionItemViewModel item)
    {
        const string id = "conn";
        NSTableCellView cell;
        NSTextField name;
        NSTextField sub;
        NSImageView icon;
        NSView badge;

        if (outline.MakeView(id, outline) is NSTableCellView reused)
        {
            cell = reused;
            icon = (NSImageView)cell.Subviews[0];
            var stack = (NSStackView)cell.Subviews[1];
            name = (NSTextField)stack.ArrangedSubviews[0];
            sub = (NSTextField)stack.ArrangedSubviews[1];
            badge = cell.Subviews[2];
        }
        else
        {
            cell = new NSTableCellView { Identifier = id };

            icon = new NSImageView
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                ContentTintColor = NSColor.SecondaryLabel,
                SymbolConfiguration = NSImageSymbolConfiguration.Create(15, NSFontWeight.Regular),
            };
            cell.AddSubview(icon);

            name = MakeLabel(13, NSColor.Label);
            sub = MakeLabel(11, NSColor.SecondaryLabel);
            var stack = new NSStackView
            {
                Orientation = NSUserInterfaceLayoutOrientation.Vertical,
                Alignment = NSLayoutAttribute.Leading,
                Spacing = 2,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            stack.AddArrangedSubview(name);
            stack.AddArrangedSubview(sub);
            cell.AddSubview(stack);
            cell.TextField = name;

            badge = ProtocolStyle.StatusBadge();
            cell.AddSubview(badge);

            NSLayoutConstraint.ActivateConstraints(new[]
            {
                badge.TrailingAnchor.ConstraintEqualTo(icon.TrailingAnchor),
                badge.BottomAnchor.ConstraintEqualTo(icon.BottomAnchor),
                icon.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 2),
                icon.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                icon.WidthAnchor.ConstraintEqualTo(20),
                stack.LeadingAnchor.ConstraintEqualTo(icon.TrailingAnchor, 8),
                stack.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6),
                stack.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
            });
        }

        name.StringValue = item.Name;
        sub.StringValue = $"{item.HostDisplay}   ·   {item.ProtocolName}";
        icon.Image = ProtocolStyle.Symbol(item.Profile.Protocol);
        icon.ContentTintColor = ProtocolStyle.Tint(item.Profile.Protocol);
        ProtocolStyle.ApplyStatus(badge, item.IsConnected, item.IsConnecting);
        return cell;
    }

    private static NSTextField MakeLabel(nfloat size, NSColor color) => new()
    {
        Bordered = false,
        Editable = false,
        Selectable = false,
        DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(size),
        TextColor = color,
        LineBreakMode = NSLineBreakMode.TruncatingTail,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    // NSOutlineView item 必须是 NSObject —— 弱引用包装 VM。

    private sealed class NodeRef : NSObject
    {
        public NodeRef(ConnectionGroupNodeViewModel node) => Node = node;
        public ConnectionGroupNodeViewModel? Node { get; }
        public override bool Equals(object? obj) => obj is NodeRef r && ReferenceEquals(r.Node, Node);
        public override int GetHashCode() => Node?.GetHashCode() ?? 0;
    }

    private sealed class ConnRef : NSObject
    {
        public ConnRef(ConnectionItemViewModel item) => Item = item;
        public ConnectionItemViewModel? Item { get; }
        public override bool Equals(object? obj) => obj is ConnRef r && ReferenceEquals(r.Item, Item);
        public override int GetHashCode() => Item?.GetHashCode() ?? 0;
    }
}
