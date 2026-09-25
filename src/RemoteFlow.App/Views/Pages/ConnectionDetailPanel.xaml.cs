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
        if ((sender as FrameworkElement)?.DataContext is ConnectionItemViewModel item
            && !string.IsNullOrEmpty(item.Host))
        {
            try
            {
                Clipboard.SetText(item.Host);
            }
            catch
            {
                // 剪贴板被占用等偶发失败不打扰用户。
            }
        }
    }

    /// <summary>「连接 ▾」的备选动作菜单（作用于当前详情连接）。</summary>
    private void OnConnectMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not ConnectionItemViewModel item
            || DataContext is not ConnectionsPageViewModel vm)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "测试连接（Ping）", Command = vm.TestConnectionCommand, CommandParameter = item });
        menu.Items.Add(new MenuItem { Header = "编辑连接", Command = vm.EditCommand, CommandParameter = item });
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
