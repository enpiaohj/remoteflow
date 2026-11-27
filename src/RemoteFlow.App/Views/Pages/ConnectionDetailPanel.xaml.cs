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

    /// <summary>「连接 ▾」：这台主机的其他打开方式（PowerShell / 文件传输）。有活动会话时另有
    /// 新建会话（跳过去重强制再开）/ 重新连接（断开全部后重连）/ 断开连接。
    /// 编辑、测试已在下方按钮行，不再重复。</summary>
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

        foreach (var entry in BuildOpenWithItems(vm, item))
        {
            menu.Items.Add(entry);
        }
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>「打开方式」菜单项：PowerShell（Windows 版上的远程桌面主机）与文件传输。</summary>
    internal static IEnumerable<MenuItem> BuildOpenWithItems(ConnectionsPageViewModel vm, ConnectionItemViewModel item)
    {
        if (vm.CanOpenPowerShell(item))
        {
            yield return new MenuItem { Header = "PowerShell 会话", Command = vm.OpenPowerShellCommand, CommandParameter = item }.WithIcon("Ui.ProtocolSsh");
        }

        yield return new MenuItem
        {
            Header = "文件传输…",
            Command = vm.OpenFileTransferCommand,
            CommandParameter = item,
            ToolTip = item.FileTransferHint
        }.WithIcon("Ui.FileTransfer");
    }

    /// <summary>「更多」菜单：名称行旁按钮之外的低频动作。复制地址、收藏旁边已有按钮，不再重复。</summary>
    private void OnActionsMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not ConnectionItemViewModel item
            || DataContext is not ConnectionsPageViewModel vm)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "创建副本", Command = vm.DuplicateCommand, CommandParameter = item }.WithIcon("Ui.Clipboard"));
        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "删除连接", Command = vm.DeleteCommand, CommandParameter = item }.WithIcon("Ui.Delete");
        MenuItemAssist.SetIsDanger(delete, true);
        menu.Items.Add(delete);
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

}
