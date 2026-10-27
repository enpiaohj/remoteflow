using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 「凭据」页面。只管理凭据元数据，界面从不显示任何 Secret 明文。
/// </summary>
public partial class CredentialsPage : UserControl
{
    public CredentialsPage()
    {
        InitializeComponent();

        // 多选模式：行单击切换勾选；Esc 退出。
        CredentialList.PreviewMouseLeftButtonDown += OnListMouseDown;
        CredentialList.KeyDown += OnListKeyDown;
    }

    private CredentialsPageViewModel? ViewModel => DataContext as CredentialsPageViewModel;

    /// <summary>行勾选框点击 = 切换该行选中（与点行同一入口）。</summary>
    private void OnRowCheckBoxClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CredentialItemViewModel item)
        {
            ViewModel?.ToggleSelect(item);
        }

        e.Handled = true;
    }

    /// <summary>批量「更改类型」：按当前合法目标弹出菜单（含 SSH 私钥时无可选项）。</summary>
    private async void OnChangeTypeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || ViewModel is not { } vm)
        {
            return;
        }

        var menu = new ContextMenu();
        if (vm.ChangeTypeOptions.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "所选包含 SSH 私钥，无法批量更改类型",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var option in vm.ChangeTypeOptions)
            {
                var target = option.Value;
                var item = new MenuItem { Header = option.Label };
                item.Click += async (_, _) => await vm.ChangeTypeSelectedAsync(target);
                menu.Items.Add(item);
            }
        }

        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>多选模式下行单击切换勾选；点勾选框本身不重复处理。</summary>
    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { IsMultiSelect: true } vm
            || e.OriginalSource is not DependencyObject source
            || e.ClickCount > 1)
        {
            return;
        }

        if (FindAncestor<CheckBox>(source) is not null)
        {
            return;
        }

        if (FindAncestor<ListBoxItem>(source)?.DataContext is CredentialItemViewModel item)
        {
            vm.ToggleSelect(item);
            e.Handled = true;
        }
    }

    private void OnRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: CredentialItemViewModel item }
            && ViewModel is { } viewModel)
        {
            // ContextMenu 不会自动改变 ListBox.SelectedItem；同步后编辑/删除完成时页面反馈仍对应右键行。
            viewModel.SelectedItem = item;
        }
    }

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (vm.IsMultiSelect)
        {
            if (e.Key == Key.Escape)
            {
                vm.ExitMultiSelectCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        // 与行右键菜单标注的快捷键一致：F2 编辑、Del 删除（删除仍会弹确认）。
        if (CredentialList.SelectedItem is not CredentialItemViewModel selected)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F2:
                e.Handled = true;
                await vm.EditCommand.ExecuteAsync(selected);
                break;

            case Key.Delete:
                e.Handled = true;
                await vm.DeleteCommand.ExecuteAsync(selected);
                break;
        }
    }

    private async void OnEditMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.EditCommand.ExecuteAsync(item);
        }
    }

    private async void OnDeleteMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.DeleteCommand.ExecuteAsync(item);
        }
    }

    private async void OnDuplicateMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item && ViewModel is { } viewModel)
        {
            await viewModel.DuplicateCommand.ExecuteAsync(item);
        }
    }

    /// <summary>「新建主机连接」：新连接直接引用该凭据，连接名预填为账户名。</summary>
    private void OnNewConnectionByAccountClick(object sender, RoutedEventArgs e)
        => ViewModel?.CreateConnectionFromCredential(ResolveItem(sender), useAccountAsName: true);

    // 空白处右键菜单：新建凭据 / 新建主机连接 / 刷新。
    private void OnBlankNewCredentialClick(object sender, RoutedEventArgs e)
        => ViewModel?.CreateCommand.Execute(null);

    private void OnBlankNewConnectionClick(object sender, RoutedEventArgs e)
        => ViewModel?.CreateConnectionCommand.Execute(null);

    private void OnBlankRefreshClick(object sender, RoutedEventArgs e)
        => ViewModel?.RefreshCommand.Execute(null);

    /// <summary>只复制用户名（含域）；密码与私钥不提供复制入口。</summary>
    private void OnCopyUsernameMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveItem(sender) is { } item)
        {
            ClipboardText.TryCopy(item.Username);
        }
    }

    /// <summary>从菜单项、菜单自身或 PlacementTarget 解析右键目标凭据。</summary>
    private static CredentialItemViewModel? ResolveItem(object sender)
    {
        if (sender is not MenuItem item)
        {
            return null;
        }

        // 子菜单项（如「用此凭据新建主机连接」下的两项）的 Parent 是上级菜单项，一路向上找到菜单本身。
        DependencyObject? parent = item.Parent;
        while (parent is MenuItem parentItem)
        {
            parent = parentItem.Parent;
        }

        var menu = parent as ContextMenu;

        return item.DataContext as CredentialItemViewModel
            ?? menu?.DataContext as CredentialItemViewModel
            ?? (menu?.PlacementTarget as FrameworkElement)?
                .DataContext as CredentialItemViewModel;
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
