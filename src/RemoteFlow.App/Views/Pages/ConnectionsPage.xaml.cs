using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using RemoteFlow.Presentation.ViewModels;
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
    /// <summary>
    /// 连接列表的查看（带分组）。VM 只维护扁平 <c>Items</c> 集合，分组由 UI 层承接：
    /// 「最近连接」按日期桶（今天 / 昨天 / 更早）分组，其余为平铺。
    /// 在代码后置重建 <see cref="ListCollectionView"/> 以保持此行为（技术方案 §6.6）。
    /// </summary>
    private ListCollectionView? _connectionView;

    public ConnectionsPage()
    {
        InitializeComponent();

        // 多选模式下行单击 = 切换勾选（不进详情）。
        ConnectionList.PreviewMouseLeftButtonDown += OnListMouseDown;

        // VM 经 DataTemplate 注入、可整体切换；跟随其变化重建分组查看并把列表绑回。
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ConnectionsPageViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is ConnectionsPageViewModel vm)
        {
            _connectionView = new ListCollectionView(vm.Items);
            ApplyGrouping();
            ConnectionList.ItemsSource = _connectionView;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 筛选切到 / 切出「最近连接」时，分组与否随之变化。
        if (e.PropertyName == nameof(ConnectionsPageViewModel.IsRecentView))
        {
            ApplyGrouping();
        }
    }

    private void ApplyGrouping()
    {
        _connectionView?.GroupDescriptions.Clear();

        if (_connectionView is not null && ViewModel is { IsRecentView: true })
        {
            _connectionView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(ConnectionItemViewModel.RecentBucket)));
        }
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

    /// <summary>右击整行前，动态填充「移动到分组」子菜单。</summary>
    private void OnRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is ListBoxItem { ContextMenu: { } menu } row
            && row.DataContext is ConnectionItemViewModel item)
        {
            // ContextMenu 不会自动改变 ListBox.SelectedItem；先同步页面选中态，
            // 让详情面板和后续键盘 Enter 都指向右键行。
            if (ViewModel is { } viewModel)
            {
                viewModel.SelectedItem = item;
            }

            PopulateMoveToGroup(menu, item);
        }
    }

    /// <summary>菜单打开（右击 / 「⋯」都触发）时按当前视图裁剪菜单项。</summary>
    private void OnRowContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            ApplyConnectionRowMenuGuards(menu);
        }
    }

    /// <summary>
    /// 按所在页面的 Filter 裁剪连接行菜单：
    /// <list type="bullet">
    /// <item>「我的连接」（Filter=All，含分组树）→ 完整管理项：复制连接 / 移动到分组 / 删除可见；「管理连接」隐藏。</item>
    /// <item>收藏 / 最近连接（Filter≠All）→ 快速访问：只留 连接 / 编辑 / 测试连接 / 收藏；显示「管理连接」。</item>
    /// </list>
    /// 同时把收藏项 Header 按视图与行状态写成 收藏 / 取消收藏；把主操作 connect 项按行会话状态
    /// 写成 连接 / 切换到会话，并同步显隐 disconnect 项；裁剪后顺手隐藏空分隔组。
    /// </summary>
    private void ApplyConnectionRowMenuGuards(ContextMenu menu)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var isAll = vm.Filter == ConnectionFilter.All;
        var row = menu.DataContext as ConnectionItemViewModel
            ?? (menu.PlacementTarget as FrameworkElement)?.DataContext as ConnectionItemViewModel;

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            switch (item.Tag as string)
            {
                case "connect":
                    // 主操作按行会话状态改名：任意活动（连接中 / 已连 / 失败未清）→ 切换到会话（聚焦既有）；
                    // 无活动 → 连接（走统一漏斗新建 / 聚焦）。
                    item.Header = row?.HasActiveSession == true ? "切换到会话" : "连接";
                    break;
                case "disconnect":
                    // 断开连接只在存在活动会话时可用（关闭该 Profile 全部活动会话）。
                    item.Visibility = row?.HasActiveSession == true ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "duplicate":
                case "move":
                case "delete":
                    item.Visibility = isAll ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "locate":
                    item.Visibility = isAll ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case "favorite":
                    // 收藏页（Filter=Favorites）展示的都是收藏，恒「取消收藏」；其它页按行当前状态。
                    item.Header = vm.Filter == ConnectionFilter.Favorites || row?.IsFavorite == true
                        ? "取消收藏"
                        : "收藏";
                    break;
            }
        }

        NormalizeSeparators(menu);
    }

    /// <summary>
    /// 隐藏某菜单项后，它夹住的分隔线可能变成空组（悬空 / 相邻 Separator）。
    /// 规则：Separator 只在两侧都还有可见菜单项时保留，其余折叠，保证视觉不出现空分组。
    /// </summary>
    private static void NormalizeSeparators(ContextMenu menu)
    {
        var items = menu.Items.Cast<object>().ToList();
        var n = items.Count;

        var hasBefore = false;
        var visibleBefore = new bool[n];
        for (var i = 0; i < n; i++)
        {
            visibleBefore[i] = hasBefore;
            if (items[i] is MenuItem { Visibility: Visibility.Visible })
            {
                hasBefore = true;
            }
        }

        var hasAfter = false;
        var visibleAfter = new bool[n];
        for (var i = n - 1; i >= 0; i--)
        {
            visibleAfter[i] = hasAfter;
            if (items[i] is MenuItem { Visibility: Visibility.Visible })
            {
                hasAfter = true;
            }
        }

        for (var i = 0; i < n; i++)
        {
            if (items[i] is Separator sep)
            {
                sep.Visibility = visibleBefore[i] && visibleAfter[i]
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private void PopulateMoveToGroup(ContextMenu menu, ConnectionItemViewModel? connection)
    {
        var moveItem = menu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(m => Equals(m.Tag, "move"));
        if (moveItem is null || ViewModel is null)
        {
            return;
        }

        menu.DataContext = connection;
        moveItem.Items.Clear();
        moveItem.IsEnabled = connection is not null;

        var currentGroupId = connection?.Profile.GroupId == ConnectionGroup.UngroupedId
            ? null
            : connection?.Profile.GroupId;

        foreach (var target in ViewModel.GroupTargets)
        {
            var sub = CreateMoveTargetMenuItem(target, connection, currentGroupId);
            sub.Click += OnMoveToGroupClick;
            moveItem.Items.Add(sub);
        }
    }

    private void OnMoveToGroupSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu menu })
        {
            var connection = menu.DataContext as ConnectionItemViewModel
                ?? (menu.PlacementTarget as FrameworkElement)?.DataContext as ConnectionItemViewModel;
            PopulateMoveToGroup(menu, connection);
        }
    }

    private static MenuItem CreateMoveTargetMenuItem(
        GroupTargetOption target,
        ConnectionItemViewModel? connection,
        Guid? currentGroupId)
        => new()
        {
            Header = new string(' ', target.Depth * 2) + target.Name,
            Tag = target,
            DataContext = connection,
            IsEnabled = connection is not null && target.GroupId != currentGroupId
        };

    private async void OnMoveToGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: GroupTargetOption target }
            && ResolveItem(sender) is { } connection
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

    /// <summary>行悬停浮出的「连接」按钮：DataContext 即该行 VM。</summary>
    private async void OnRowConnectClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConnectionItemViewModel item
            && ViewModel is { IsMultiSelect: false } viewModel)
        {
            await viewModel.ConnectAsync(item);
        }
    }

    /// <summary>「断开连接」：关闭该连接的全部活动会话（含连接中 / 失败未清）。</summary>
    private async void OnDisconnectMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.DisconnectItemCommand.ExecuteAsync(item);
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

    private async void OnTestMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.TestConnectionCommand.ExecuteAsync(item);
        }
    }

    /// <summary>「管理连接」：切到全部视图并让该连接可见、选中。</summary>
    private void OnLocateMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            viewModel.SelectById(item.Id);
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

    /// <summary>
    /// 动作条「批量操作 ▾」：选择与批量的统一入口（勾选框常驻可见后，
    /// 这里把原「多选开关」的能力全部收进菜单）。
    /// </summary>
    private async void OnBatchOperationsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ViewModel is not { } vm)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "全选当前列表", Command = vm.SelectAllVisibleCommand });
        menu.Items.Add(new MenuItem
        {
            Header = "清空选择",
            Command = vm.ClearSelectionCommand,
            IsEnabled = vm.HasSelection
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "批量连接",
            Command = vm.ConnectSelectedCommand,
            IsEnabled = vm.HasSelection
        });
        menu.Items.Add(new MenuItem
        {
            Header = vm.FavoriteActionText,
            Command = vm.ToggleFavoriteSelectedCommand,
            IsEnabled = vm.HasSelection
        });

        var targets = vm.GroupTargets;
        if (targets.Count > 0 && vm.HasSelection)
        {
            var move = new MenuItem { Header = "移动到分组" };
            foreach (var target in targets)
            {
                var captured = target;
                var sub = new MenuItem { Header = target.Name, Tag = captured };
                sub.Click += async (_, _) => await vm.MoveSelectedToGroupAsync(captured.GroupId);
                move.Items.Add(sub);
            }
            menu.Items.Add(move);
        }

        var tags = await LoadTagsAsync(vm);
        if (tags.Count > 0 && vm.HasSelection)
        {
            var addTags = new MenuItem { Header = "添加标签" };
            foreach (var tag in tags)
            {
                addTags.Items.Add(new MenuItem
                {
                    Header = tag.Name,
                    Command = vm.AddTagToSelectedCommand,
                    CommandParameter = tag
                });
            }
            menu.Items.Add(addTags);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "删除所选",
            Command = vm.DeleteSelectedCommand,
            IsEnabled = vm.HasSelection
        });

        OpenBatchMenu(button, menu);
    }

    /// <summary>动作条「…」：对当前选中行的低频操作。</summary>
    private void OnRowActionsMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ViewModel is not { } vm || vm.SelectedItem is not { } item)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "复制连接", Command = vm.DuplicateCommand, CommandParameter = item });
        menu.Items.Add(new MenuItem { Header = "测试连接", Command = vm.TestConnectionCommand, CommandParameter = item });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "删除", Command = vm.DeleteCommand, CommandParameter = item });

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

    // ── 新建 / 页面快捷键 ────────────────────────────────────────

    private void OnNewConnectionMenuClick(object sender, RoutedEventArgs e)
        => ViewModel?.CreateCommand.Execute(null);

    /// <summary>Ctrl+K 聚焦页内搜索框（与设计稿的工作台交互一致）。</summary>
    private void OnPageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control
            && PageFilterBox is { IsVisible: true })
        {
            PageFilterBox.Focus();
            PageFilterBox.SelectAll();
            e.Handled = true;
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

    /// <summary>从菜单项、菜单自身或 PlacementTarget 解析右键目标连接。</summary>
    private static ConnectionItemViewModel? ResolveItem(object sender)
    {
        if (sender is not MenuItem item)
        {
            return null;
        }

        var menu = item.Parent as ContextMenu;
        return item.DataContext as ConnectionItemViewModel
            ?? menu?.DataContext as ConnectionItemViewModel
            ?? (menu?.PlacementTarget as FrameworkElement)?.DataContext as ConnectionItemViewModel;
    }

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
