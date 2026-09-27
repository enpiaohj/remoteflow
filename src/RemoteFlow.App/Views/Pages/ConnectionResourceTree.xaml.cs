using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 连接工作台的左栏「连接资源」树（智能视图 + 分组树）。
/// 由 MainWindow 以窗口级列承载并跨标题栏行放置，使「连接资源」头部与产品名称
/// 置顶同行；DataContext 绑定 <see cref="ConnectionsPageViewModel"/>。
/// </summary>
public partial class ConnectionResourceTree : UserControl
{
    public ConnectionResourceTree() => InitializeComponent();

    private ConnectionsPageViewModel? ViewModel => DataContext as ConnectionsPageViewModel;

    /// <summary>
    /// 左树折叠箭头：把按下事件吃掉，阻止它冒泡给 ListBoxItem——
    /// 点箭头只做展开 / 折叠，不把该分组选成当前过滤。
    /// </summary>
    private void OnTreeChevronPreviewMouseDown(object sender, MouseButtonEventArgs e)
        => e.Handled = true;

    private void OnNewGroupMenuClick(object sender, RoutedEventArgs e)
        => ViewModel?.CreateGroupCommand.Execute(null);

    // ── 分组右键菜单 ────────────────────────────────────────────

    /// <summary>右键 / Shift+F10 经 ContextMenu.Opened 统一应用分组菜单守卫。</summary>
    private void OnGroupContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && menu.PlacementTarget is FrameworkElement owner)
        {
            ApplyGroupMenuGuards(menu, owner.DataContext as ConnectionGroupNodeViewModel);
        }
    }

    /// <summary>按永久内置身份守卫分组菜单：未分组不能建子组，两个内置组都不能编辑或删除。</summary>
    private void ApplyGroupMenuGuards(ContextMenu menu, ConnectionGroupNodeViewModel? node)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            switch (item.Tag)
            {
                case "newChild":
                    item.Visibility = node is { IsUngrouped: true } ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case "rename":
                case "delete":
                    item.Visibility = node is { IsUngrouped: true } or { IsBuiltIn: true }
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                    item.IsEnabled = node is { IsProtected: false };
                    break;
            }
        }

        // 内置分组隐藏编辑 / 删除后，底部两条分隔线会悬空，统一整理。
        ContextMenuSeparators.Normalize(menu);
    }

    private static ConnectionGroupNodeViewModel? ResolveGroup(object sender)
    {
        if (sender is not MenuItem item)
        {
            return null;
        }

        var menu = item.Parent as ContextMenu;
        return item.DataContext as ConnectionGroupNodeViewModel
            ?? menu?.DataContext as ConnectionGroupNodeViewModel
            ?? (menu?.PlacementTarget as FrameworkElement)?.DataContext as ConnectionGroupNodeViewModel;
    }

    private void OnGroupNewConnectionClick(object sender, RoutedEventArgs e)
    {
        if (ResolveGroup(sender) is { } node)
        {
            _ = ViewModel?.CreateConnectionAsync(
                defaultGroupId: node.IsUngrouped ? ConnectionGroup.UngroupedId : node.GroupId);
        }
    }

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
}
