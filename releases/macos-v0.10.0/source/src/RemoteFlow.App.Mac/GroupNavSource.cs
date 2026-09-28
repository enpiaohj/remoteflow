using System.Collections.ObjectModel;
using AppKit;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 连接资源树「分组」导航列表（对齐 Windows <c>ConnectionResourceTree.xaml</c> 的
/// <c>GroupTreeList</c>）：<see cref="ConnectionsPageViewModel.FlatTreeNodes"/> 已经是摊平好的
/// 序列（折叠分组不展开子级），这里只按 Depth 缩进渲染，不再承担分组层级的 DataSource 职责。
/// 选中某一行只是「导航」——真正的连接内容始终在 <see cref="FlatListSource"/> 里展示。
/// </summary>
public sealed class GroupNavSource : NSTableViewDelegate
{
    private readonly NSTableView _table;
    private readonly ObservableCollection<ConnectionGroupNodeViewModel> _nodes;
    private bool _suppressNotify;

    public GroupNavSource(NSTableView table, ObservableCollection<ConnectionGroupNodeViewModel> nodes)
    {
        _table = table;
        _nodes = nodes;
        _table.DataSource = new RowCount(this);
        _table.Delegate = this;
        _nodes.CollectionChanged += (_, _) => Reload();
        Reload();
    }

    public ConnectionGroupNodeViewModel? Selected =>
        _table.SelectedRow >= 0 && _table.SelectedRow < _nodes.Count ? _nodes[(int)_table.SelectedRow] : null;

    public ConnectionGroupNodeViewModel? RowObject(nint row) => row >= 0 && row < _nodes.Count ? _nodes[(int)row] : null;

    public event EventHandler? SelectionChanged;

    public void Reload() => NSApplication.SharedApplication.BeginInvokeOnMainThread(_table.ReloadData);

    /// <summary>由外部（VM.SelectedGroupNode 变化）同步高亮，不重新触发 SelectionChanged。</summary>
    public void SelectRowFor(ConnectionGroupNodeViewModel? node)
    {
        _suppressNotify = true;
        if (node is null)
        {
            _table.DeselectAll(null);
        }
        else
        {
            var index = _nodes.IndexOf(node);
            if (index >= 0)
            {
                _table.SelectRow(index, byExtendingSelection: false);
            }
            else
            {
                _table.DeselectAll(null);
            }
        }
        _suppressNotify = false;
    }

    public override nfloat GetRowHeight(NSTableView tableView, nint row) => 28;

    public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
    {
        var node = _nodes[(int)row];
        const string id = "groupnav";

        Row cell;
        if (tableView.MakeView(id, this) is Row reused)
        {
            cell = reused;
        }
        else
        {
            cell = new Row { Identifier = id };

            cell.Chevron = new NSButton
            {
                BezelStyle = NSBezelStyle.Inline,
                Bordered = false,
                Image = NSImage.GetSystemSymbol("chevron.right", null),
                ImageScaling = NSImageScale.ProportionallyDown,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            cell.Chevron.Activated += (_, _) =>
            {
                if (cell.Node is { } n) n.ToggleExpandCommand.Execute(null);
            };

            cell.Icon = new NSImageView
            {
                TranslatesAutoresizingMaskIntoConstraints = false,
                SymbolConfiguration = NSImageSymbolConfiguration.Create(13, NSFontWeight.Regular),
            };

            cell.Name = new NSTextField
            {
                Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(12),
                TextColor = NSColor.Label,
                LineBreakMode = NSLineBreakMode.TruncatingTail,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };

            cell.Count = new NSTextField
            {
                Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(11),
                TextColor = NSColor.TertiaryLabel,
                Alignment = NSTextAlignment.Right,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };

            cell.AddSubview(cell.Chevron);
            cell.AddSubview(cell.Icon);
            cell.AddSubview(cell.Name);
            cell.AddSubview(cell.Count);
            cell.TextField = cell.Name;

            cell.IndentConstraint = cell.Chevron.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 4);
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                cell.IndentConstraint,
                cell.Chevron.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                cell.Chevron.WidthAnchor.ConstraintEqualTo(14),
                cell.Icon.LeadingAnchor.ConstraintEqualTo(cell.Chevron.TrailingAnchor, 2),
                cell.Icon.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                cell.Icon.WidthAnchor.ConstraintEqualTo(15),
                cell.Name.LeadingAnchor.ConstraintEqualTo(cell.Icon.TrailingAnchor, 6),
                cell.Name.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                cell.Name.TrailingAnchor.ConstraintLessThanOrEqualTo(cell.Count.LeadingAnchor, -6),
                cell.Count.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6),
                cell.Count.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
            });
        }

        cell.Node = node;
        cell.IndentConstraint.Constant = 4 + node.Depth * 14;
        cell.Chevron.Hidden = !node.HasChildGroups;
        cell.Chevron.Image = NSImage.GetSystemSymbol(node.IsExpanded ? "chevron.down" : "chevron.right", null);
        cell.Icon.Image = OrganizationStyle.GroupSymbol(node.IsUngrouped ? "GroupIcon.Ungrouped" : node.IconResourceKey);
        cell.Icon.ContentTintColor = OrganizationStyle.GroupTint(node.FolderBrushKey);
        cell.Name.StringValue = node.IsUngrouped ? "未分组" : node.Name;
        cell.Count.StringValue = node.TotalCount.ToString();
        return cell;
    }

    public override void SelectionDidChange(Foundation.NSNotification notification)
    {
        if (_suppressNotify) return;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Row : NSTableCellView
    {
        public ConnectionGroupNodeViewModel? Node;
        public NSButton Chevron = null!;
        public NSImageView Icon = null!;
        public NSTextField Name = null!;
        public NSTextField Count = null!;
        public NSLayoutConstraint IndentConstraint = null!;
    }

    private sealed class RowCount : NSTableViewDataSource
    {
        private readonly GroupNavSource _o;
        public RowCount(GroupNavSource o) => _o = o;
        public override nint GetRowCount(NSTableView tableView) => _o._nodes.Count;
    }
}
