using System.Collections.Specialized;
using AppKit;
using CoreGraphics;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>标签管理：列表 + 新建 / 编辑 / 删除，绑共享 <see cref="TagManagerViewModel"/>。App-Modal。</summary>
public sealed class TagManagerSheet : NSWindowController
{
    private readonly TagManagerViewModel _vm;
    private readonly NSTableView _table = new()
    {
        HeaderView = null,
        RowHeight = 40,
        BackgroundColor = NSColor.Clear,
        SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.Regular,
        Style = NSTableViewStyle.Inset,
    };
    private readonly NSButton _edit;
    private readonly NSButton _delete;

    public TagManagerSheet(TagManagerViewModel vm)
        : base(NewPanel())
    {
        _vm = vm;
        Window.Title = "管理标签";

        _table.AddColumn(new NSTableColumn("c") { ResizingMask = NSTableColumnResizing.Autoresizing });
        _table.DataSource = new Source(this);
        _table.Delegate = new Deleg(this);
        _table.DoubleClick += (_, _) => RunEdit();
        _vm.Tags.CollectionChanged += OnTagsChanged;

        var scroll = new NSScrollView
        {
            DocumentView = _table,
            BorderType = NSBorderType.BezelBorder,
            HasVerticalScroller = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var add = NSButton.CreateButton("新建标签…", () => _ = _vm.AddTagCommand.ExecuteAsync(null));
        add.BezelStyle = NSBezelStyle.Rounded;
        _edit = NSButton.CreateButton("编辑…", RunEdit);
        _edit.BezelStyle = NSBezelStyle.Rounded;
        _edit.Enabled = false;
        _delete = NSButton.CreateButton("删除…", RunDelete);
        _delete.BezelStyle = NSBezelStyle.Rounded;
        _delete.Enabled = false;
        var close = NSButton.CreateButton("完成", () =>
        {
            NSApplication.SharedApplication.StopModal();
            Window.OrderOut(null);
        });
        close.BezelStyle = NSBezelStyle.Rounded;
        close.KeyEquivalent = "\r";

        var left = new NSStackView { Spacing = 8, TranslatesAutoresizingMaskIntoConstraints = false };
        left.AddArrangedSubview(add);
        left.AddArrangedSubview(_edit);
        left.AddArrangedSubview(_delete);

        var bar = new NSStackView { Spacing = 8, TranslatesAutoresizingMaskIntoConstraints = false };
        bar.AddArrangedSubview(left);
        bar.AddArrangedSubview(new NSView());
        bar.AddArrangedSubview(close);

        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        root.AddSubview(scroll);
        root.AddSubview(bar);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            scroll.TopAnchor.ConstraintEqualTo(root.TopAnchor, 18),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 20),
            scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -20),
            bar.TopAnchor.ConstraintEqualTo(scroll.BottomAnchor, 12),
            bar.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 20),
            bar.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -20),
            bar.BottomAnchor.ConstraintEqualTo(root.BottomAnchor, -16),
        });
        Window.ContentView = root;
    }

    public void Run()
    {
        Window.Center();
        Window.MakeKeyAndOrderFront(null);
        NSApplication.SharedApplication.RunModalForWindow(Window);
        _vm.Tags.CollectionChanged -= OnTagsChanged;
    }

    private void OnTagsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => NSApplication.SharedApplication.BeginInvokeOnMainThread(_table.ReloadData);

    private TagRowViewModel? Selected =>
        _table.SelectedRow >= 0 && _table.SelectedRow < _vm.Tags.Count ? _vm.Tags[(int)_table.SelectedRow] : null;

    private void RunEdit()
    {
        if (Selected is { } row)
        {
            _ = _vm.EditTagCommand.ExecuteAsync(row);
        }
    }

    private void RunDelete()
    {
        if (Selected is { } row)
        {
            _ = _vm.DeleteTagCommand.ExecuteAsync(row);
        }
    }

    private static NSWindow NewPanel() => new NSPanel(
        new CGRect(0, 0, 420, 360),
        NSWindowStyle.Titled | NSWindowStyle.Closable,
        NSBackingStore.Buffered,
        deferCreation: false);

    private sealed class Source : NSTableViewDataSource
    {
        private readonly TagManagerSheet _o;
        public Source(TagManagerSheet o) => _o = o;
        public override nint GetRowCount(NSTableView tableView) => _o._vm.Tags.Count;
    }

    private sealed class Deleg : NSTableViewDelegate
    {
        private readonly TagManagerSheet _o;
        public Deleg(TagManagerSheet o) => _o = o;

        public override void SelectionDidChange(Foundation.NSNotification notification)
        {
            var has = _o.Selected is not null;
            _o._edit.Enabled = has;
            _o._delete.Enabled = has;
        }

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var tag = _o._vm.Tags[(int)row];
            const string id = "tag";
            if (tableView.MakeView(id, _o) is not NSTableCellView cell)
            {
                var swatch = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, WantsLayer = true };
                swatch.Layer!.CornerRadius = 4;
                var name = Lbl(13, NSColor.Label);
                var desc = Lbl(11, NSColor.SecondaryLabel);
                var col = new NSStackView
                {
                    Orientation = NSUserInterfaceLayoutOrientation.Vertical,
                    Alignment = NSLayoutAttribute.Leading,
                    Spacing = 1,
                    TranslatesAutoresizingMaskIntoConstraints = false,
                };
                col.AddArrangedSubview(name);
                col.AddArrangedSubview(desc);
                cell = new NSTableCellView { Identifier = id };
                cell.AddSubview(swatch);
                cell.AddSubview(col);
                cell.TextField = name;
                NSLayoutConstraint.ActivateConstraints(new[]
                {
                    swatch.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 6),
                    swatch.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                    swatch.WidthAnchor.ConstraintEqualTo(14),
                    swatch.HeightAnchor.ConstraintEqualTo(14),
                    col.LeadingAnchor.ConstraintEqualTo(swatch.TrailingAnchor, 10),
                    col.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                    col.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6),
                });
            }

            var sw = cell.Subviews[0];
            sw.Layer!.BackgroundColor = (ColorFromHex(tag.Color) ?? NSColor.SystemGray).CGColor;
            var stack = (NSStackView)cell.Subviews[1];
            ((NSTextField)stack.ArrangedSubviews[0]).StringValue = tag.Name;
            ((NSTextField)stack.ArrangedSubviews[1]).StringValue = tag.Description;
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
    }

    private static NSColor? ColorFromHex(string hex)
    {
        var s = (hex ?? string.Empty).TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v))
        {
            return null;
        }

        return NSColor.FromRgb(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
    }
}
