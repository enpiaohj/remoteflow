using System.Collections.ObjectModel;
using AppKit;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>凭据表：CredentialsPageViewModel.Items → NSTableView。行：图标 + 名称/用户名 + 类型 + 引用数 + 锁。</summary>
public sealed class CredentialListSource : NSTableViewDelegate
{
    private readonly NSTableView _table;
    private readonly ObservableCollection<CredentialItemViewModel> _items;

    public CredentialListSource(NSTableView table, ObservableCollection<CredentialItemViewModel> items)
    {
        _table = table;
        _items = items;
        _table.DataSource = new RowCount(this);
        _table.Delegate = this;
        _items.CollectionChanged += (_, _) =>
            NSApplication.SharedApplication.BeginInvokeOnMainThread(_table.ReloadData);
        _table.ReloadData();
    }

    public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
    {
        var item = _items[(int)row];
        const string id = "cred";

        NSTableCellView cell;
        NSImageView icon;
        NSTextField name, sub, type, usage;
        NSImageView locked;

        if (tableView.MakeView(id, this) is NSTableCellView reused)
        {
            cell = reused;
            icon = (NSImageView)cell.Subviews[0];
            var st = (NSStackView)cell.Subviews[1];
            name = (NSTextField)st.ArrangedSubviews[0];
            sub = (NSTextField)st.ArrangedSubviews[1];
            type = (NSTextField)cell.Subviews[2];
            usage = (NSTextField)cell.Subviews[3];
            locked = (NSImageView)cell.Subviews[4];
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
            var st = new NSStackView
            {
                Orientation = NSUserInterfaceLayoutOrientation.Vertical,
                Alignment = NSLayoutAttribute.Leading,
                Spacing = 2,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            st.AddArrangedSubview(name);
            st.AddArrangedSubview(sub);
            type = Lbl(11, NSColor.SecondaryLabel);
            usage = Lbl(11, NSColor.TertiaryLabel);
            usage.Alignment = NSTextAlignment.Right;
            locked = new NSImageView
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                ContentTintColor = NSColor.SystemGreen,
                SymbolConfiguration = NSImageSymbolConfiguration.Create(11, NSFontWeight.Regular),
            };

            cell.AddSubview(icon);
            cell.AddSubview(st);
            cell.AddSubview(type);
            cell.AddSubview(usage);
            cell.AddSubview(locked);
            cell.TextField = name;

            NSLayoutConstraint.ActivateConstraints(new[]
            {
                icon.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 4),
                icon.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                icon.WidthAnchor.ConstraintEqualTo(22),
                st.LeadingAnchor.ConstraintEqualTo(icon.TrailingAnchor, 8),
                st.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                type.LeadingAnchor.ConstraintGreaterThanOrEqualTo(st.TrailingAnchor, 8),
                type.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                usage.LeadingAnchor.ConstraintEqualTo(type.TrailingAnchor, 12),
                usage.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                usage.WidthAnchor.ConstraintEqualTo(72),
                locked.LeadingAnchor.ConstraintEqualTo(usage.TrailingAnchor, 8),
                locked.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6),
                locked.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                locked.WidthAnchor.ConstraintEqualTo(14),
            });
        }

        name.StringValue = item.Name;
        sub.StringValue = item.Username;
        type.StringValue = item.TypeName;
        usage.StringValue = item.UsageDisplay;
        icon.Image = NSImage.GetSystemSymbol("key.fill", null);
        locked.Image = item.HasSecret ? NSImage.GetSystemSymbol("lock.fill", null) : NSImage.GetSystemSymbol("lock.open", null);
        locked.ContentTintColor = item.HasSecret ? NSColor.SystemGreen : NSColor.TertiaryLabel;
        return cell;
    }

    private static NSTextField Lbl(nfloat size, NSColor color) => new()
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

    private sealed class RowCount : NSTableViewDataSource
    {
        private readonly CredentialListSource _o;
        public RowCount(CredentialListSource o) => _o = o;
        public override nint GetRowCount(NSTableView tableView) => _o._items.Count;
    }
}
