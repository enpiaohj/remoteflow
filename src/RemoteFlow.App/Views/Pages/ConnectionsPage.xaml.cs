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

            // 工作台列表默认每页 20 条（不分页是 VM 的默认值，供没有分页条的 macOS 端使用）。
            if (vm.PageSize == 0)
            {
                vm.PageSize = DefaultPageSize;
            }
        }
    }

    private const int DefaultPageSize = 20;

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
        if (TryHandleSelectionKey(e))
        {
            return;
        }

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

        if (ViewModel is not { SelectedItem: { } selected } viewModel)
        {
            return;
        }

        // 与行右键菜单标注的快捷键一致：Enter 连接、F2 编辑；Del 删除只在「我的连接」可用
        // （收藏 / 最近是快速访问视图，菜单里同样不提供删除），且仍会弹确认。
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await viewModel.ConnectAsync(selected);
                break;

            case Key.F2:
                e.Handled = true;
                await viewModel.EditCommand.ExecuteAsync(selected);
                break;

            case Key.Delete when viewModel.Filter == ConnectionFilter.All:
                e.Handled = true;
                await viewModel.DeleteCommand.ExecuteAsync(selected);
                break;
        }
    }

    /// <summary>
    /// 行单击：Ctrl = 切换该行勾选，Shift = 从起点连选到该行（Ctrl+Shift 追加），资源管理器习惯；
    /// 多选模式下无修饰键的单击也是切换勾选；普通单击只记下连选起点。点 CheckBox / 行内按钮本身不拦截。
    /// </summary>
    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // 不做双击动作时第二击直接忽略，避免“选中又被取消”。
        if (e.ClickCount > 1)
        {
            return;
        }

        // 点 CheckBox 让 IsChecked 绑定自己翻转，避免这里再补一次成“取消”；行内按钮（收藏 / 编辑 / 更多…）自己处理。
        if (FindAncestor<CheckBox>(source) is not null || FindAncestor<Button>(source) is not null)
        {
            return;
        }

        if (FindAncestor<ListBoxItem>(source)?.DataContext is not ConnectionItemViewModel item)
        {
            return;
        }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl || shift)
        {
            vm.SelectWithModifiers(item, ctrl, shift);
            e.Handled = true;
        }
        else if (vm.IsMultiSelect)
        {
            vm.ToggleSelect(item);
            vm.NoteSelectionAnchor(item);
            e.Handled = true;
        }
        else
        {
            vm.NoteSelectionAnchor(item);
        }
    }

    /// <summary>Ctrl+A 全选当前列表；Shift+↑/↓（可加 Ctrl 追加）连选。已处理返回 true。</summary>
    private bool TryHandleSelectionKey(KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return false;
        }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (e.Key == Key.A && ctrl && !shift)
        {
            vm.SelectAllVisibleCommand.Execute(null);
            e.Handled = true;
            return true;
        }

        if (shift && e.Key is Key.Up or Key.Down && vm.SelectedItem is { } current)
        {
            var index = vm.Items.IndexOf(current) + (e.Key == Key.Down ? 1 : -1);
            if (index >= 0 && index < vm.Items.Count)
            {
                var target = vm.Items[index];
                vm.SelectRange(target, additive: ctrl);
                vm.SelectedItem = target;
                ConnectionList.ScrollIntoView(target);
            }

            e.Handled = true;
            return true;
        }

        return false;
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

        PopulateRowSubmenus(menu, row.DataContext as ConnectionItemViewModel);
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

            PopulateRowSubmenus(menu, item);
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
                case "newsession":
                case "reconnect":
                    // 断开连接 / 新建会话只在存在活动会话时出现（前者关闭该连接全部活动会话，后者强制再开一个）。
                    item.Visibility = row?.HasActiveSession == true ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "copyaddress":
                    item.IsEnabled = !string.IsNullOrEmpty(row?.Host);
                    break;
                case "duplicate":
                case "move":
                case "credential":
                case "delete":
                    item.Visibility = isAll ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "locate":
                    item.Visibility = isAll ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case "filetransfer":
                    // VNC 协议本身没有文件传输通道：菜单项置灰并在提示里说明，而不是悄悄隐藏让人找不到。
                    item.IsEnabled = row?.SupportsFileTransfer != false;
                    item.ToolTip = row?.FileTransferHint;
                    break;
                case "favorite":
                    // 收藏页（Filter=Favorites）展示的都是收藏，恒「取消收藏」；其它页按行当前状态。
                    var favorited = vm.Filter == ConnectionFilter.Favorites || row?.IsFavorite == true;
                    item.Header = favorited ? "取消收藏" : "收藏";
                    MenuItemAssist.SetIconKey(item, favorited ? "Ui.Favorite.Filled" : "Ui.Favorite");
                    break;
            }
        }

        ContextMenuSeparators.Normalize(menu);
    }

    /// <summary>行菜单里需要按行动态填充的子菜单：移动到分组、更改凭据。</summary>
    private void PopulateRowSubmenus(ContextMenu menu, ConnectionItemViewModel? connection)
    {
        PopulateMoveToGroup(menu, connection);
        PopulateChangeCredential(menu, connection);
    }

    /// <summary>「更改凭据」子菜单的一项：Tag 携带目标凭据（null = 未指定）。</summary>
    private sealed record CredentialChoice(Guid? Id);

    private void PopulateChangeCredential(ContextMenu menu, ConnectionItemViewModel? connection)
    {
        var credentialItem = menu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(m => Equals(m.Tag, "credential"));
        if (credentialItem is null || ViewModel is null)
        {
            return;
        }

        menu.DataContext = connection;
        credentialItem.Items.Clear();
        credentialItem.IsEnabled = connection is not null;

        var current = connection?.Profile.CredentialId;
        credentialItem.Items.Add(CreateCredentialChoice("未指定", null, connection, current));
        foreach (var choice in ViewModel.CredentialChoices)
        {
            credentialItem.Items.Add(CreateCredentialChoice(choice.Value, choice.Key, connection, current));
        }
    }

    private MenuItem CreateCredentialChoice(string name, Guid? id, ConnectionItemViewModel? connection, Guid? current)
    {
        var item = new MenuItem
        {
            // 当前引用的那项置灰并标注，和「移动到分组」一致。
            Header = id == current ? $"{name}（当前）" : name,
            Tag = new CredentialChoice(id),
            DataContext = connection,
            IsEnabled = connection is not null && id != current,
        };
        item.Click += OnChangeCredentialClick;
        return item;
    }

    private void OnChangeCredentialSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu menu })
        {
            var connection = menu.DataContext as ConnectionItemViewModel
                ?? (menu.PlacementTarget as FrameworkElement)?.DataContext as ConnectionItemViewModel;
            PopulateChangeCredential(menu, connection);
        }
    }

    private async void OnChangeCredentialClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: CredentialChoice choice }
            && ResolveItem(sender) is { } connection
            && ViewModel is { } viewModel)
        {
            await viewModel.ChangeCredentialAsync(connection, choice.Id);
        }
    }

    // 空白处右键菜单：新建主机连接 / 新建凭据 / 刷新。
    private void OnBlankNewConnectionClick(object sender, RoutedEventArgs e)
        => ViewModel?.CreateCommand.Execute(null);

    private void OnBlankNewCredentialClick(object sender, RoutedEventArgs e)
        => ViewModel?.RequestNewCredentialCommand.Execute(null);

    private void OnBlankRefreshClick(object sender, RoutedEventArgs e)
        => ViewModel?.RefreshCommand.Execute(null);

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
    private async void OnReconnectMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.ReconnectItemCommand.ExecuteAsync(item);
        }
    }

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

    /// <summary>已有会话时强制再开一个（跳过「聚焦已有会话」的去重）。</summary>
    private void OnNewSessionMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            viewModel.NewSessionCommand.Execute(item);
        }
    }

    private void OnCopyAddressMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item)
        {
            ClipboardText.TryCopy(item.Host);
        }
    }

    /// <summary>在工作区打开文件传输 Tab（不建立会话）。</summary>
    private void OnFileTransferMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            viewModel.OpenFileTransferCommand.Execute(item);
        }
    }

    /// <summary>「在工作台中显示」：切到全部视图并让该连接可见、选中。</summary>
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

        var tags = await LoadTagsAsync(vm);
        if (tags.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "移除标签", IsEnabled = false });
        }
        else
        {
            var remove = new MenuItem { Header = "移除标签" }.WithIcon("Ui.Tag");
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
    /// 动作条「批量操作 ▾」：全选 / 取消选择与批量动作的快捷菜单
    /// （多选模式的开关在筛选行末的图标 ToggleButton 上）。
    /// </summary>
    /// <summary>列头点击排序：Tag 里放排序方式（Name / Protocol / LastConnected）。</summary>
    private void OnSortHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }
            && Enum.TryParse<ConnectionSortMode>(tag, out var mode))
        {
            ViewModel?.SortByCommand.Execute(mode);
            e.Handled = true;
        }
    }

    private async void OnBatchOperationsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ViewModel is not { } vm)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "全选当前列表", Command = vm.SelectAllVisibleCommand }.WithIcon("Ui.Checklist"));
        menu.Items.Add(new MenuItem
        {
            Header = "取消选择",
            Command = vm.ClearSelectionCommand,
            IsEnabled = vm.HasSelection
        }.WithIcon("Ui.Clear"));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "批量连接",
            Command = vm.ConnectSelectedCommand,
            IsEnabled = vm.HasSelection
        }.WithIcon("Ui.Connect"));
        menu.Items.Add(new MenuItem
        {
            Header = vm.FavoriteActionText,
            Command = vm.ToggleFavoriteSelectedCommand,
            IsEnabled = vm.HasSelection
        }.WithIcon("Ui.Favorite"));

        var targets = vm.GroupTargets;
        if (targets.Count > 0 && vm.HasSelection)
        {
            var move = new MenuItem { Header = "移动到分组" }.WithIcon("Ui.Folder");
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
            var addTags = new MenuItem { Header = "添加标签" }.WithIcon("Ui.Tag");
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
            Header = "删除所选项",
            Command = vm.DeleteSelectedCommand,
            IsEnabled = vm.HasSelection
        }.WithIcon("Ui.Delete").AsDanger());

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
        menu.Items.Add(new MenuItem { Header = "测试连接", Command = vm.TestConnectionCommand, CommandParameter = item }.WithIcon("Ui.Pending"));
        menu.Items.Add(new MenuItem { Header = "创建副本", Command = vm.DuplicateCommand, CommandParameter = item }.WithIcon("Ui.Clipboard"));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "删除连接", Command = vm.DeleteCommand, CommandParameter = item }.WithIcon("Ui.Delete").AsDanger());

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
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusFilter();
            e.Handled = true;
        }
    }

    /// <summary>聚焦页内搜索框（Ctrl+K / 全局焦点搜索的落点）。</summary>
    public void FocusFilter()
    {
        if (PageFilterBox is { IsVisible: true })
        {
            PageFilterBox.Focus();
            PageFilterBox.SelectAll();
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
