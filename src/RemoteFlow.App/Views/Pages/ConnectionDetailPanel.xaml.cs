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

        try
        {
            Clipboard.SetText(item.Host);
        }
        catch
        {
            // 剪贴板被占用等偶发失败不打扰用户。
        }
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
            menu.Items.Add(new MenuItem { Header = "新建会话", Command = vm.NewSessionCommand, CommandParameter = item });
            menu.Items.Add(new MenuItem { Header = "重新连接", Command = vm.ReconnectItemCommand, CommandParameter = item });
            menu.Items.Add(new MenuItem { Header = "断开连接", Command = vm.DisconnectItemCommand, CommandParameter = item });
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem { Header = "测试连接（Ping）", Command = vm.TestConnectionCommand, CommandParameter = item });
        menu.Items.Add(new MenuItem { Header = "编辑连接", Command = vm.EditCommand, CommandParameter = item });
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>「更多」菜单：低频动作收纳（复制 IP / 收藏 / 复制连接 / 预留项）。</summary>
    private void OnActionsMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not ConnectionItemViewModel item
            || DataContext is not ConnectionsPageViewModel vm)
        {
            return;
        }

        var menu = new ContextMenu();
        var copyHostItem = new MenuItem { Header = "复制 IP" };
        copyHostItem.Click += OnCopyHostMenuClick;
        menu.Items.Add(copyHostItem);
        menu.Items.Add(new MenuItem
        {
            Header = item.IsFavorite ? "取消收藏" : "收藏",
            Command = vm.ToggleFavoriteCommand,
            CommandParameter = item
        });
        menu.Items.Add(new MenuItem { Header = "复制连接", Command = vm.DuplicateCommand, CommandParameter = item });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Traceroute", IsEnabled = false, ToolTip = "SSH 连接可用，后续版本提供" });
        menu.Items.Add(new MenuItem { Header = "打开 Web", IsEnabled = false, ToolTip = "打开设备的 Web 管理页：后续版本提供" });
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
