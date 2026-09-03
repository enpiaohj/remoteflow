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
    public ConnectionsPage() => InitializeComponent();

    private ConnectionsPageViewModel? ViewModel => DataContext as ConnectionsPageViewModel;

    private async void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 只有双击到具体行才连接；双击空白区域或分组头不应触发。
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

    /// <summary>「更多」按钮点击时手动弹出其上下文菜单。</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
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
