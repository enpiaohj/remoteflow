using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AppKit;
using Foundation;

namespace RemoteFlow.App.Mac.Binding;

/// <summary>
/// <see cref="ObservableCollection{T}"/> → <see cref="NSTableView"/> 的通用绑定适配器。
/// <para>
/// 承担 DataSource（行数）与 Delegate（cell view）。集合变化自动 <c>ReloadData</c>（主线程）。
/// cell 内容由 <paramref name="configureCell"/> 回调填充，选中变化经 <see cref="SelectionChanged"/> 抛出。
/// AppKit 无 XAML 绑定引擎，这是 Xamarin.Mac 标准做法——写一次，各列表复用。
/// </para>
/// </summary>
public sealed class TableSource<T> : NSTableViewDelegate
{
    private readonly NSTableView _table;
    private readonly ObservableCollection<T> _items;
    private readonly Action<NSTextField, T> _configureCell;

    public TableSource(NSTableView table, ObservableCollection<T> items, Action<NSTextField, T> configureCell)
    {
        _table = table;
        _items = items;
        _configureCell = configureCell;

        _table.DataSource = new RowCount(this);
        _table.Delegate = this;
        _items.CollectionChanged += OnCollectionChanged;
    }

    /// <summary>当前选中项（无选中为 default）。</summary>
    public T? Selected =>
        _table.SelectedRow >= 0 && _table.SelectedRow < _items.Count
            ? _items[(int)_table.SelectedRow]
            : default;

    public event EventHandler? SelectionChanged;

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => NSApplication.SharedApplication.BeginInvokeOnMainThread(_table.ReloadData);

    public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
    {
        const string id = "cell";
        if (tableView.MakeView(id, this) is not NSTextField field)
        {
            field = new NSTextField
            {
                Identifier = id,
                Bordered = false,
                Editable = false,
                Selectable = false,
                DrawsBackground = false,
                Font = NSFont.SystemFontOfSize(13),
                LineBreakMode = NSLineBreakMode.TruncatingTail,
            };
        }

        if (row >= 0 && row < _items.Count)
        {
            _configureCell(field, _items[(int)row]);
        }

        return field;
    }

    public override void SelectionDidChange(NSNotification notification)
        => SelectionChanged?.Invoke(this, EventArgs.Empty);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _items.CollectionChanged -= OnCollectionChanged;
        }

        base.Dispose(disposing);
    }

    private sealed class RowCount : NSTableViewDataSource
    {
        private readonly TableSource<T> _owner;
        public RowCount(TableSource<T> owner) => _owner = owner;
        public override nint GetRowCount(NSTableView tableView) => _owner._items.Count;
    }
}
