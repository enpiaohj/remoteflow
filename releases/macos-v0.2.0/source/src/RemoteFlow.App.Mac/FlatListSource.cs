using System.Collections.ObjectModel;
using AppKit;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 扁平连接列表（收藏 / 最近连接用）：ObservableCollection&lt;ConnectionItemViewModel&gt; → NSTableView。
/// 行：协议图标 + 名称 / 主机·协议 + 尾部时间 + 收藏星。
/// </summary>
public sealed class FlatListSource : NSTableViewDelegate
{
    private readonly NSTableView _table;
    private readonly ObservableCollection<ConnectionItemViewModel> _items;

    public FlatListSource(NSTableView table, ObservableCollection<ConnectionItemViewModel> items)
    {
        _table = table;
        _items = items;
        _table.DataSource = new RowCount(this);
        _table.Delegate = this;
        _items.CollectionChanged += (_, _) => Reload();
        Reload();
    }

    public ConnectionItemViewModel? Selected =>
        _table.SelectedRow >= 0 && _table.SelectedRow < _items.Count ? _items[(int)_table.SelectedRow] : null;

    public event EventHandler? SelectionChanged;

    public void Reload() => NSApplication.SharedApplication.BeginInvokeOnMainThread(_table.ReloadData);

    public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
    {
        var item = _items[(int)row];
        const string id = "flat";

        NSTableCellView cell;
        NSImageView icon;
        NSTextField name, sub, trailing;
        NSImageView star;
        NSView badge;

        if (tableView.MakeView(id, this) is NSTableCellView reused)
        {
            cell = reused;
            icon = (NSImageView)cell.Subviews[0];
            var stack = (NSStackView)cell.Subviews[1];
            name = (NSTextField)stack.ArrangedSubviews[0];
            sub = (NSTextField)stack.ArrangedSubviews[1];
            trailing = (NSTextField)cell.Subviews[2];
            star = (NSImageView)cell.Subviews[3];
            badge = cell.Subviews[4];
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
            name = Lbl(13, NSColor.Label);
            sub = Lbl(11, NSColor.SecondaryLabel);
            var stack = new NSStackView
            {
                Orientation = NSUserInterfaceLayoutOrientation.Vertical,
                Alignment = NSLayoutAttribute.Leading,
                Spacing = 2,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            stack.AddArrangedSubview(name);
            stack.AddArrangedSubview(sub);

            trailing = Lbl(11, NSColor.TertiaryLabel);
            trailing.Alignment = NSTextAlignment.Right;

            star = new NSImageView
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                ContentTintColor = NSColor.SystemYellow,
                SymbolConfiguration = NSImageSymbolConfiguration.Create(11, NSFontWeight.Regular),
            };

            badge = ProtocolStyle.StatusBadge();

            cell.AddSubview(icon);
            cell.AddSubview(stack);
            cell.AddSubview(trailing);
            cell.AddSubview(star);
            cell.AddSubview(badge);
            cell.TextField = name;

            NSLayoutConstraint.ActivateConstraints(new[]
            {
                icon.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 4),
                icon.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                icon.WidthAnchor.ConstraintEqualTo(22),
                stack.LeadingAnchor.ConstraintEqualTo(icon.TrailingAnchor, 8),
                stack.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                stack.TrailingAnchor.ConstraintLessThanOrEqualTo(trailing.LeadingAnchor, -8),
                trailing.TrailingAnchor.ConstraintEqualTo(star.LeadingAnchor, -6),
                trailing.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                star.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6),
                star.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                star.WidthAnchor.ConstraintEqualTo(14),
                // 在线徽标压在协议图标右下角
                badge.TrailingAnchor.ConstraintEqualTo(icon.TrailingAnchor),
                badge.BottomAnchor.ConstraintEqualTo(icon.BottomAnchor),
            });
        }

        name.StringValue = item.Name;
        sub.StringValue = $"{item.HostDisplay}   ·   {item.ProtocolName}";
        trailing.StringValue = item.LastConnectedDisplay;
        icon.Image = ProtocolStyle.Symbol(item.Profile.Protocol);
        icon.ContentTintColor = ProtocolStyle.Tint(item.Profile.Protocol);
        star.Image = item.IsFavorite ? NSImage.GetSystemSymbol("star.fill", null) : null;
        star.Hidden = !item.IsFavorite;
        ProtocolStyle.ApplyStatus(badge, item.IsConnected, item.IsConnecting);
        return cell;
    }

    public override void SelectionDidChange(Foundation.NSNotification notification)
        => SelectionChanged?.Invoke(this, EventArgs.Empty);

    private static NSTextField Lbl(nfloat size, NSColor color) => new()
    {
        Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
        Font = NSFont.SystemFontOfSize(size),
        TextColor = color,
        LineBreakMode = NSLineBreakMode.TruncatingTail,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    private sealed class RowCount : NSTableViewDataSource
    {
        private readonly FlatListSource _o;
        public RowCount(FlatListSource o) => _o = o;
        public override nint GetRowCount(NSTableView tableView) => _o._items.Count;
    }
}
