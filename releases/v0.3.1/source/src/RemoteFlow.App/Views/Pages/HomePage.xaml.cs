using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RemoteFlow.App.ViewModels;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 首页。只呈现最近连接、收藏与最近活动，帮助用户尽快回到工作状态。
/// </summary>
public partial class HomePage : UserControl
{
    public HomePage() => InitializeComponent();

    private HomePageViewModel? ViewModel => DataContext as HomePageViewModel;

    /// <summary>「⋯」按钮点击即在按钮位置弹出其上下文菜单。</summary>
    private void OnItemMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnItemConnectClick(object sender, RoutedEventArgs e)
    {
        if (Resolve(sender) is { } item)
        {
            ViewModel?.ConnectCommand.Execute(item);
        }
    }

    private void OnItemManageClick(object sender, RoutedEventArgs e)
        => ViewModel?.ViewAllFavoritesCommand.Execute(null);

    /// <summary>菜单项的 DataContext 就是所在行 / 卡片的 VM。</summary>
    private static ConnectionItemViewModel? Resolve(object sender)
        => (sender as MenuItem)?.DataContext as ConnectionItemViewModel;
}
