using AppKit;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Mac;

/// <summary>
/// 连接资源树顶部的智能视图入口（对齐 Windows <c>ConnectionResourceTree.xaml</c> 的
/// <c>SmartViewList</c>）：固定 3 项（所有设备 / 收藏 / 最近连接），图标来自
/// <see cref="DeviceIconCatalog"/>（<c>ResourceIcon.*</c> 键），计数随 VM 的
/// AllCount/FavoritesCount/RecentCount 变化重绘。
/// </summary>
public sealed class SmartViewSource : NSTableViewDelegate
{
    /// <summary>单行高度。承载它的滚动视图要按「行数 × 本值」定高，
    /// 两边必须用同一个数，否则差几个点就会让内容能微微滚动、选中时整块位移。</summary>
    public const int RowHeight = 32;

    private readonly NSTableView _table;
    private readonly ConnectionsPageViewModel _vm;
    private bool _suppressNotify;

    public SmartViewSource(NSTableView table, ConnectionsPageViewModel vm)
    {
        _table = table;
        _vm = vm;
        _table.DataSource = new RowCount(this);
        _table.Delegate = this;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(vm.AllCount) or nameof(vm.FavoritesCount) or nameof(vm.RecentCount))
            {
                Reload();
            }
        };
        Reload();
    }

    public ConnectionsPageViewModel.SmartViewOption? Selected =>
        _table.SelectedRow >= 0 && _table.SelectedRow < _vm.SmartViews.Count ? _vm.SmartViews[(int)_table.SelectedRow] : null;

    public event EventHandler? SelectionChanged;

    /// <summary>重载并把选中态补回来。
    /// <para>
    /// ReloadData 会清空选中，而本表的重载是由「计数变化」触发的，排在主线程队列里的时机
    /// 比外部补高亮更晚——不在这里自己补，进入工作台时「所有设备」就会没有高亮
    /// （真实事故）。分组被选中时智能视图本就该是空选中，故只在没选分组时补。
    /// </para></summary>
    public void Reload() => NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
    {
        _table.ReloadData();
        if (_vm.SelectedGroupNode is null)
        {
            SelectRowFor(_vm.SelectedSmartView);
        }
    });

    /// <summary>由外部（VM.SelectedSmartView 变化）同步高亮，不重新触发 SelectionChanged。</summary>
    public void SelectRowFor(ConnectionsPageViewModel.SmartViewOption? option)
    {
        _suppressNotify = true;
        if (option is null)
        {
            _table.DeselectAll(null);
        }
        else
        {
            var index = _vm.SmartViews.ToList().IndexOf(option);
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

    public override nfloat GetRowHeight(NSTableView tableView, nint row) => RowHeight;

    public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
    {
        var option = _vm.SmartViews[(int)row];
        const string id = "smartview";

        Row cell;
        if (tableView.MakeView(id, this) is Row reused)
        {
            cell = reused;
        }
        else
        {
            cell = new Row { Identifier = id };

            cell.Icon = new NSImageView { TranslatesAutoresizingMaskIntoConstraints = false };
            cell.Name = new NSTextField
            {
                Bordered = false, Editable = false, Selectable = false, DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(13),
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

            cell.AddSubview(cell.Icon);
            cell.AddSubview(cell.Name);
            cell.AddSubview(cell.Count);
            cell.TextField = cell.Name;

            NSLayoutConstraint.ActivateConstraints(new[]
            {
                cell.Icon.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 4),
                cell.Icon.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                cell.Icon.WidthAnchor.ConstraintEqualTo(18),
                cell.Icon.HeightAnchor.ConstraintEqualTo(18),
                cell.Name.LeadingAnchor.ConstraintEqualTo(cell.Icon.TrailingAnchor, 8),
                cell.Name.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
                cell.Name.TrailingAnchor.ConstraintLessThanOrEqualTo(cell.Count.LeadingAnchor, -6),
                cell.Count.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -6),
                cell.Count.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
            });
        }

        cell.Icon.Image = DeviceIconCatalog.Get(option.IconResourceKey);
        cell.Name.StringValue = option.Name;
        cell.Count.StringValue = option.FilterValue switch
        {
            ConnectionFilter.Favorites => _vm.FavoritesCount.ToString(),
            ConnectionFilter.Recent => _vm.RecentCount.ToString(),
            _ => _vm.AllCount.ToString(),
        };
        return cell;
    }

    public override void SelectionDidChange(Foundation.NSNotification notification)
    {
        if (_suppressNotify) return;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Row : NSTableCellView
    {
        public NSImageView Icon = null!;
        public NSTextField Name = null!;
        public NSTextField Count = null!;
    }

    private sealed class RowCount : NSTableViewDataSource
    {
        private readonly SmartViewSource _o;
        public RowCount(SmartViewSource o) => _o = o;
        public override nint GetRowCount(NSTableView tableView) => _o._vm.SmartViews.Count;
    }
}
