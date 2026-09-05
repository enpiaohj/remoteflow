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

        // 多选态不做双击动作；第二击直接忽略，避免“选中又被取消”。
        if (e.ClickCount > 1)
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

    /// <summary>勾选框点击 = 切换该行选中（与点行同一入口，保证“已选”集合同步）。</summary>
    private void OnRowCheckBoxClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConnectionItemViewModel item)
        {
            ViewModel?.ToggleSelect(item);
        }

        e.Handled = true;
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

        var tags = await LoadTagsAsync(vm);
        var menu = new ContextMenu();

        if (tags.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "还没有标签", IsEnabled = false });
        }
        else
        {
            foreach (var tag in tags)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = tag.Name,
                    Command = vm.AddTagToSelectedCommand,
                    CommandParameter = tag
                });
            }
        }

        OpenBatchMenu(button, menu);
    }

    /// <summary>「更多 ▾」：低频但仍有用，避免与高频操作同排堆叠。</summary>
    private async void OnBatchMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ViewModel is not { } vm)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
        {
            Header = "连接所选项",
            Command = vm.ConnectSelectedCommand
        });

        var tags = await LoadTagsAsync(vm);
        if (tags.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "移除标签…", IsEnabled = false });
        }
        else
        {
            var remove = new MenuItem { Header = "移除标签…" };
            foreach (var tag in tags)
            {
                remove.Items.Add(new MenuItem
                {
                    Header = tag.Name,
                    Command = vm.RemoveTagFromSelectedCommand,
                    CommandParameter = tag
                });
            }

            menu.Items.Add(remove);
        }

        OpenBatchMenu(button, menu);
    }

    /// <summary>列头 CheckBox：全选 / 取消全选当前可见项。</summary>
    private void OnSelectAllHeaderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (vm.IsAllSelected)
        {
            vm.ClearSelectionCommand.Execute(null);
        }
        else
        {
            vm.SelectAllVisibleCommand.Execute(null);
        }
    }

    private static async Task<IReadOnlyList<Tag>> LoadTagsAsync(ConnectionsPageViewModel vm)
    {
        try
        {
            return await vm.GetTagsAsync();
        }
        catch
        {
            return [];
        }
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

    /// <summary>右键 / Shift+F10 与左键「⋯」都经 ContextMenu.Opened 统一应用分组菜单守卫。</summary>
    private void OnGroupContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && menu.PlacementTarget is FrameworkElement owner)
        {
            ApplyGroupMenuGuards(menu, owner.DataContext as ConnectionGroupNodeViewModel);
        }
    }

    /// <summary>按分组状态守卫右键菜单项：受保护组禁重命名 / 删除；默认组与未分组不出现「设为默认分组」。</summary>
    private void ApplyGroupMenuGuards(ContextMenu menu, ConnectionGroupNodeViewModel? node)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            switch (item.Tag as string)
            {
                case "rename":
                case "delete":
                    item.IsEnabled = node is not null && !node.IsProtected;
                    break;
                case "setDefault":
                    item.Visibility = node is { IsDefault: false, IsUngrouped: false }
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    item.IsEnabled = !ViewModel!.HasProtectedDefault;
                    break;
            }
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

    private void OnGroupSetDefaultClick(object sender, RoutedEventArgs e)
    {
        if (ResolveGroup(sender) is { } node)
        {
            ViewModel?.SetDefaultGroupCommand.Execute(node);
        }
    }

    /// <summary>组头 CheckBox 按下 = 全选 / 取消全选该分组（含子分组）。拦截默认三态循环。</summary>
    private void OnGroupHeaderCheckMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConnectionGroupNodeViewModel node)
        {
            ViewModel?.ToggleGroupSelection(node);
        }

        e.Handled = true;
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
