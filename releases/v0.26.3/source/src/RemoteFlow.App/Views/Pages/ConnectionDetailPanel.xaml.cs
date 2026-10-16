using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 右侧详情面板。展示选中连接的信息与快速连接入口，不显示凭据密码。
/// </summary>
public partial class ConnectionDetailPanel : UserControl
{
    public ConnectionDetailPanel() => InitializeComponent();

    /// <summary>复制主机 / IP 到剪贴板。</summary>
    private void OnCopyHostClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConnectionItemViewModel item)
        {
            CopyHost(item);
        }
    }

    private void CopyHost(ConnectionItemViewModel item)
    {
        if (string.IsNullOrEmpty(item.Host))
        {
            return;
        }

        ClipboardText.TryCopy(item.Host);
    }

    /// <summary>「连接 / 打开会话 ▾」的备选动作菜单。有活动会话时提供
    /// 新建会话（跳过去重强制再开）/ 重新连接（断开全部后重连）/ 断开连接；
    /// 无会话时只有 测试连接 / 编辑连接。</summary>
    private void OnConnectMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not ConnectionItemViewModel item
            || DataContext is not ConnectionsPageViewModel vm)
        {
            return;
        }

        var menu = new ContextMenu();
        if (item.HasActiveSession)
        {
            menu.Items.Add(new MenuItem { Header = "新建会话", Command = vm.NewSessionCommand, CommandParameter = item }.WithIcon("Ui.Add"));
            menu.Items.Add(new MenuItem { Header = "重新连接", Command = vm.ReconnectItemCommand, CommandParameter = item }.WithIcon("Ui.Refresh"));
            menu.Items.Add(new MenuItem { Header = "断开连接", Command = vm.DisconnectItemCommand, CommandParameter = item }.WithIcon("Ui.Disconnected"));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem { Header = "测试连接", Command = vm.TestConnectionCommand, CommandParameter = item }.WithIcon("Ui.Pending"));
        menu.Items.Add(new MenuItem
        {
            Header = "文件传输…",
            Command = vm.OpenFileTransferCommand,
            CommandParameter = item,
            ToolTip = item.FileTransferHint
        }.WithIcon("Ui.FileTransfer"));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "编辑连接", Command = vm.EditCommand, CommandParameter = item }.WithIcon("Ui.Edit"));
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>「更多」菜单：低频动作收纳（复制地址 / 收藏 / 创建副本）。</summary>
    private void OnActionsMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not ConnectionItemViewModel item
            || DataContext is not ConnectionsPageViewModel vm)
        {
            return;
        }

        var menu = new ContextMenu();
        var copyHostItem = new MenuItem { Header = "复制地址" }.WithIcon("Ui.Copy");
        copyHostItem.Click += OnCopyHostMenuClick;
        menu.Items.Add(copyHostItem);
        menu.Items.Add(new MenuItem
        {
            Header = item.IsFavorite ? "取消收藏" : "收藏",
            Command = vm.ToggleFavoriteCommand,
            CommandParameter = item
        }.WithIcon(item.IsFavorite ? "Ui.Favorite.Filled" : "Ui.Favorite"));
        menu.Items.Add(new MenuItem { Header = "创建副本", Command = vm.DuplicateCommand, CommandParameter = item }.WithIcon("Ui.Clipboard"));
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnCopyHostMenuClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConnectionItemViewModel item)
        {
            CopyHost(item);
        }
    }
}
