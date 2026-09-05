using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteFlow.App.ViewModels;
using RemoteFlow.Core.Models;

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

    public ConnectionsPage()
    {
        InitializeComponent();

        // 多选模式下行单击 = 切换勾选（不进详情）。挂在两个列表上统一处理。
        ConnectionList.PreviewMouseLeftButtonDown += OnListMouseDown;
        GroupedList.PreviewMouseLeftButtonDown += OnListMouseDown;
    }

    private ConnectionsPageViewModel? ViewModel => DataContext as ConnectionsPageViewModel;

    private async void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 多选模式下不通过双击直接连接。
        if (ViewModel is { IsMultiSelect: true })
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source
            && FindAncestor<ListBoxItem>(source)?.DataContext is ConnectionItemViewModel item
            && ViewModel is { } viewModel)
        {
            await viewModel.ConnectAsync(item);
        }
    }

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is { IsMultiSelect: true } vm)
        {
            // Esc 退出多选。
            if (e.Key == Key.Escape)
            {
                vm.ExitMultiSelectCommand.Execute(null);
                e.Handled = true;
            }

            return; // 多选模式下回车不连接。
        }

        if (e.Key != Key.Enter || ViewModel is not { SelectedItem: { } selected } viewModel)
        {
            return;
        }

        e.Handled = true;
        await viewModel.ConnectAsync(selected);
    }

    /// <summary>多选模式下行单击切换勾选；点 CheckBox 本身不拦截。</summary>
    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { IsMultiSelect: true } vm
            || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // 点 CheckBox 让 IsChecked 绑定自己翻转，避免这里再补一次成“取消”。
        if (FindAncestor<CheckBox>(source) is not null)
        {
            return;
        }

        if (FindAncestor<ListBoxItem>(source)?.DataContext is ConnectionItemViewModel item)
        {
            vm.ToggleSelect(item);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 多选模式下行勾选框点击。IsChecked 已 TwoWay 绑定行 VM；
    /// 此处理器仅用于吞掉点击，避免冒泡成 ListBox 的行选中/详情展示。
    /// </summary>
    private void OnRowCheckBoxClick(object sender, RoutedEventArgs e) => e.Handled = true;

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

    // ── 批量动作条菜单 ─────────────────────────────────────────

    private async void OnBatchMoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ViewModel is not { } vm)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var target in vm.GroupTargets)
        {
            var sub = new MenuItem
            {
                Header = new string(' ', target.Depth * 2) + target.Name,
                Tag = target
            };
            sub.Click += async (_, _) =>
            {
                if (sub.Tag is GroupTargetOption g)
                {
                    await vm.MoveSelectedToGroupAsync(g.GroupId);
                }
            };
            menu.Items.Add(sub);
        }

        OpenBatchMenu(button, menu);
    }

    private async void OnBatchTagClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ViewModel is not { } vm)
        {
            return;
        }

        IReadOnlyList<Tag> tags;
        try
        {
            tags = await vm.GetTagsAsync();
        }
        catch
        {
            tags = [];
        }

        var menu = new ContextMenu();

        var add = new MenuItem { Header = "添加标签", IsEnabled = tags.Count > 0 };
        foreach (var tag in tags)
        {
            var sub = new MenuItem
            {
                Header = tag.Name,
                Command = vm.AddTagToSelectedCommand,
                CommandParameter = tag
            };
            add.Items.Add(sub);
        }
        menu.Items.Add(add);

        var remove = new MenuItem { Header = "移除标签", IsEnabled = tags.Count > 0 };
        foreach (var tag in tags)
        {
            var sub = new MenuItem
            {
                Header = tag.Name,
                Command = vm.RemoveTagFromSelectedCommand,
                CommandParameter = tag
            };
            remove.Items.Add(sub);
        }
        menu.Items.Add(remove);

        if (tags.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "还没有标签", IsEnabled = false });
        }

        OpenBatchMenu(button, menu);
    }

    private static void OpenBatchMenu(Button button, ContextMenu menu)
    {
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
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
