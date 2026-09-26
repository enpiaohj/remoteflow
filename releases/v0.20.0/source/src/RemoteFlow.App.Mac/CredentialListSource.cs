using System.Collections.ObjectModel;
using AppKit;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 凭据表：CredentialsPageViewModel.Items → NSTableView。按列渲染，对齐 Windows 版：
/// 名称（头像 + 名字）/ 类型（彩色徽章）/ 用户名（等宽）/ 保险库状态 / 引用 / 操作。
/// </summary>
public sealed class CredentialListSource : NSTableViewDelegate
{
    private readonly NSTableView _table;
    private readonly ObservableCollection<CredentialItemViewModel> _items;
    private readonly Action<CredentialItemViewModel?>? _onSelect;
    private readonly Action<CredentialItemViewModel>? _onActivate;
    private readonly Action<CredentialItemViewModel>? _onDelete;

    public CredentialListSource(
        NSTableView table,
        ObservableCollection<CredentialItemViewModel> items,
        Action<CredentialItemViewModel?>? onSelect = null,
        Action<CredentialItemViewModel>? onActivate = null,
        Action<CredentialItemViewModel>? onDelete = null)
    {
        _table = table;
        _items = items;
        _onSelect = onSelect;
        _onActivate = onActivate;
        _onDelete = onDelete;
        _table.DataSource = new RowCount(this);
        _table.Delegate = this;
        _items.CollectionChanged += (_, _) =>
            NSApplication.SharedApplication.BeginInvokeOnMainThread(_table.ReloadData);
        if (_onActivate is not null)
        {
            _table.DoubleClick += (_, _) =>
            {
                var r = (int)_table.ClickedRow;
                if (r >= 0 && r < _items.Count)
                {
                    _onActivate(_items[r]);
                }
            };
        }
        _table.ReloadData();
    }

    public override void SelectionDidChange(Foundation.NSNotification notification)
    {
        var row = (int)_table.SelectedRow;
        _onSelect?.Invoke(row >= 0 && row < _items.Count ? _items[row] : null);
    }

    public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
    {
        var item = _items[(int)row];
        return (tableColumn?.Identifier ?? "name") switch
        {
            "type" => TypeBadge(item),
            "user" => Mono(item.Username),
            "vault" => VaultCell(item),
            "usage" => Plain(item.UsageDisplay, item.UsageCount == 0 ? NSColor.TertiaryLabel : NSColor.SecondaryLabel),
            "ops" => OpsCell(item),
            _ => NameCell(item),
        };
    }

    /// <summary>名称列：圆形头像（按类型着色）+ 名称。</summary>
    private static NSView NameCell(CredentialItemViewModel item)
    {
        var tint = TypeTint(item);
        var avatar = new RoundFill(tint.ColorWithAlphaComponent(0.16f));
        var glyph = new NSImageView
        {
            Image = NSImage.GetSystemSymbol(TypeSymbol(item), null),
            ContentTintColor = tint,
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(13, NSFontWeight.Regular),
        };
        avatar.AddSubview(glyph);

        var name = Lbl(13, NSColor.Label);
        name.StringValue = item.Name;
        name.ToolTip = string.IsNullOrEmpty(item.Description) ? item.Name : item.Description;

        var cell = new NSTableCellView();
        cell.AddSubview(avatar);
        cell.AddSubview(name);
        cell.TextField = name;
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            glyph.CenterXAnchor.ConstraintEqualTo(avatar.CenterXAnchor),
            glyph.CenterYAnchor.ConstraintEqualTo(avatar.CenterYAnchor),
            avatar.WidthAnchor.ConstraintEqualTo(28),
            avatar.HeightAnchor.ConstraintEqualTo(28),
            avatar.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 4),
            avatar.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
            name.LeadingAnchor.ConstraintEqualTo(avatar.TrailingAnchor, 10),
            name.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6),
            name.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
        });
        return cell;
    }

    /// <summary>类型列：彩色胶囊徽章（与协议色系一致，便于扫读）。</summary>
    private static NSView TypeBadge(CredentialItemViewModel item)
    {
        var tint = TypeTint(item);
        var pill = new RoundFill(tint.ColorWithAlphaComponent(0.14f), 5);
        var text = Lbl(11, tint);
        text.StringValue = item.TypeName;

        pill.AddSubview(text);
        var cell = new NSTableCellView();
        cell.AddSubview(pill);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            text.LeadingAnchor.ConstraintEqualTo(pill.LeadingAnchor, 8),
            text.TrailingAnchor.ConstraintEqualTo(pill.TrailingAnchor, -8),
            text.CenterYAnchor.ConstraintEqualTo(pill.CenterYAnchor),
            pill.HeightAnchor.ConstraintEqualTo(20),
            pill.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor),
            pill.TrailingAnchor.ConstraintLessThanOrEqualTo(cell.TrailingAnchor, -6),
            pill.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
        });
        return cell;
    }

    /// <summary>保险库列：锁图标 + 「已保存密码 / 未保存密码」。只说有无，绝不显示内容。</summary>
    private static NSView VaultCell(CredentialItemViewModel item)
    {
        var lockIcon = new NSImageView
        {
            Image = NSImage.GetSystemSymbol(item.HasSecret ? "lock.fill" : "lock.open", null),
            ContentTintColor = item.HasSecret ? NSColor.SystemGreen : NSColor.SystemOrange,
            TranslatesAutoresizingMaskIntoConstraints = false,
            SymbolConfiguration = NSImageSymbolConfiguration.Create(11, NSFontWeight.Regular),
        };
        var text = Lbl(11, NSColor.SecondaryLabel);
        text.StringValue = item.SecretStateDisplay;

        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 5,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        row.AddArrangedSubview(lockIcon);
        row.AddArrangedSubview(text);

        var cell = new NSTableCellView();
        cell.AddSubview(row);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            row.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor),
            row.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
        });
        return cell;
    }

    /// <summary>操作列：编辑 / 删除。</summary>
    private NSView OpsCell(CredentialItemViewModel item)
    {
        var edit = IconBtn("pencil", "编辑凭据", () => _onActivate?.Invoke(item));
        var del = IconBtn("trash", "删除凭据", () => _onDelete?.Invoke(item));
        del.ContentTintColor = NSColor.SystemRed;

        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Alignment = NSLayoutAttribute.CenterY,
            Spacing = 2,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        row.AddArrangedSubview(edit);
        row.AddArrangedSubview(del);

        var cell = new NSTableCellView();
        cell.AddSubview(row);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            row.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor),
            row.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
        });
        return cell;
    }

    private static NSButton IconBtn(string symbol, string tip, Action onClick)
    {
        var b = NSButton.CreateButton(string.Empty, () => onClick());
        b.TranslatesAutoresizingMaskIntoConstraints = false;
        b.Bordered = false;
        b.ToolTip = tip;
        b.Image = NSImage.GetSystemSymbol(symbol, null);
        b.ContentTintColor = NSColor.SecondaryLabel;
        b.SymbolConfiguration = NSImageSymbolConfiguration.Create(12, NSFontWeight.Regular);
        b.WidthAnchor.ConstraintEqualTo(26).Active = true;
        b.HeightAnchor.ConstraintEqualTo(24).Active = true;
        return b;
    }

    private static NSView Plain(string text, NSColor color)
    {
        var l = Lbl(11, color);
        l.StringValue = text;
        var cell = new NSTableCellView();
        cell.AddSubview(l);
        cell.TextField = l;
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            l.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor),
            l.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6),
            l.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
        });
        return cell;
    }

    private static NSView Mono(string text)
    {
        var l = Lbl(11, NSColor.SecondaryLabel);
        l.StringValue = text;
        l.Font = NSFont.MonospacedSystemFont(11, NSFontWeight.Regular);
        var cell = new NSTableCellView();
        cell.AddSubview(l);
        cell.TextField = l;
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            l.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor),
            l.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6),
            l.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
        });
        return cell;
    }

    private static NSColor TypeTint(CredentialItemViewModel item) => item.TypeAccentBrushKey switch
    {
        "Protocol.Rdp" => NSColor.SystemBlue,
        "Protocol.Ssh" => NSColor.SystemGreen,
        "Protocol.Vnc" => NSColor.SystemPurple,
        _ => NSColor.SystemGray,
    };

    private static string TypeSymbol(CredentialItemViewModel item) => item.TypeAccentBrushKey switch
    {
        "Protocol.Ssh" => "apple.terminal",
        "Protocol.Vnc" => "key",
        _ => "person",
    };

    /// <summary>圆角/圆形纯色底，跟随明暗重刷。</summary>
    private sealed class RoundFill : NSView
    {
        private readonly Func<NSColor> _fill;
        private readonly nfloat? _radius;

        public RoundFill(NSColor fill, nfloat radius = default)
        {
            _fill = () => fill;
            _radius = radius > 0 ? radius : null;
            WantsLayer = true;
            TranslatesAutoresizingMaskIntoConstraints = false;
            Refresh();
        }

        public override void SetFrameSize(CoreGraphics.CGSize newSize)
        {
            base.SetFrameSize(newSize);
            Layer!.CornerRadius = _radius ?? (newSize.Height / 2);
        }

        public override void ViewDidChangeEffectiveAppearance()
        {
            base.ViewDidChangeEffectiveAppearance();
            Refresh();
        }

        private void Refresh()
        {
            var prev = NSAppearance.CurrentAppearance;
            NSAppearance.CurrentAppearance = EffectiveAppearance;
            Layer!.BackgroundColor = _fill().CGColor;
            NSAppearance.CurrentAppearance = prev;
        }
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
