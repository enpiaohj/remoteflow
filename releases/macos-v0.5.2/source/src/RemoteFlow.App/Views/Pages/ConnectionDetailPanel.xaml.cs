using System.Windows;
using System.Windows.Controls;
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
}
