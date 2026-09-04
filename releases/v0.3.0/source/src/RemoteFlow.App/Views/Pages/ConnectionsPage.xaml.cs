using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteFlow.App.ViewModels;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 「我的连接」页面。
/// <para>
/// 交互约定（产品设计文档 §7.8）：<b>单击仅选中</b>并在右侧显示详情，
/// <b>双击或 Enter 才发起连接</b>，避免误触直接开会话。
/// </para>
/// </summary>
public partial class ConnectionsPage : UserControl
{
    /// <summary>右键菜单打开时记录的目标连接，供「移动到分组」子项使用。</summary>
    private ConnectionItemViewModel? _menuConnection;

    public ConnectionsPage() => InitializeComponent();

    private ConnectionsPageViewModel? ViewModel => DataContext as ConnectionsPageViewModel;

    private async void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<ListBoxItem>(source)?.DataContext is ConnectionItemViewModel item
            && ViewModel is { } viewModel)
        {
            await viewModel.ConnectAsync(item);
        }
    }

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not { SelectedItem: { } selected } viewModel)
        {
            return;
        }

        e.Handled = true;
        await viewModel.ConnectAsync(selected);
    }

    /// <summary>「更多」按钮点击时弹出所在行的右键菜单。</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || FindAncestor<ListBoxItem>(button) is not { ContextMenu: { } menu } row)
        {
            return;
        }

        PopulateMoveToGroup(menu, row.DataContext as ConnectionItemViewModel);
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>右击整行前，动态填充「移动到分组」子菜单并记录目标连接。</summary>
    private void OnRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is ListBoxItem { ContextMenu: { } menu } row)
        {
            PopulateMoveToGroup(menu, row.DataContext as ConnectionItemViewModel);
        }
    }

    private void PopulateMoveToGroup(ContextMenu menu, ConnectionItemViewModel? connection)
    {
        _menuConnection = connection;

        var moveItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "移动到分组");
        if (moveItem is null || ViewModel is null)
        {
            return;
        }

        moveItem.Items.Clear();
        moveItem.IsEnabled = connection is not null;

        foreach (var target in ViewModel.GroupTargets)
        {
            var sub = new MenuItem
            {
                Header = new string(' ', target.Depth * 2) + target.Name,
                Tag = target
            };
            sub.Click += OnMoveToGroupClick;
            moveItem.Items.Add(sub);
        }
    }

    private async void OnMoveToGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: GroupTargetOption target }
            && _menuConnection is { } connection
            && ViewModel is { } viewModel)
        {
            await viewModel.MoveConnectionToGroupAsync(connection, target.GroupId);
        }
    }

    private async void OnConnectMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.ConnectAsync(item);
        }
    }

    private async void OnEditMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.EditCommand.ExecuteAsync(item);
        }
    }

    private async void OnDuplicateMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.DuplicateCommand.ExecuteAsync(item);
        }
    }

    private async void OnDeleteMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.DeleteCommand.ExecuteAsync(item);
        }
    }

    private async void OnToggleFavoriteMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.ToggleFavoriteCommand.ExecuteAsync(item);
        }
    }

    // ── 新建 ▾ ──────────────────────────────────────────────────

    private void OnNewMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnNewConnectionMenuClick(object sender, RoutedEventArgs e)
        => ViewModel?.CreateCommand.Execute(null);

    private void OnNewGroupMenuClick(object sender, RoutedEventArgs e)
        => ViewModel?.CreateGroupCommand.Execute(null);

    // ── 分组右键菜单 ────────────────────────────────────────────

    private void OnGroupMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private ConnectionGroupNodeViewModel? ResolveGroup(object sender)
        => (sender as MenuItem)?.DataContext as ConnectionGroupNodeViewModel;

    private void OnGroupNewConnectionClick(object sender, RoutedEventArgs e)
        => ViewModel?.CreateCommand.Execute(null);

    private void OnGroupNewChildClick(object sender, RoutedEventArgs e)
    {
        if (ResolveGroup(sender) is { } node)
        {
            ViewModel?.CreateChildGroupCommand.Execute(node);
        }
    }

    private void OnGroupRenameClick(object sender, RoutedEventArgs e)
    {
        if (ResolveGroup(sender) is { } node)
        {
            ViewModel?.RenameGroupCommand.Execute(node);
        }
    }

    private void OnGroupDeleteClick(object sender, RoutedEventArgs e)
    {
        if (ResolveGroup(sender) is { } node)
        {
            ViewModel?.DeleteGroupCommand.Execute(node);
        }
    }

    /// <summary>菜单项的 DataContext 继承自弹出菜单的 PlacementTarget，即所在行。</summary>
    private static ConnectionItemViewModel? ResolveItem(object sender)
        => (sender as MenuItem)?.DataContext as ConnectionItemViewModel;

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
