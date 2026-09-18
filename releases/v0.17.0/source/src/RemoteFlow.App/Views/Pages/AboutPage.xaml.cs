using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 关于页（内嵌工作区页面）：版本、开发者、源码仓库、许可证与隐私说明。
/// 外链用系统默认浏览器打开；打不开（无浏览器关联等）静默忽略，不打断。
/// </summary>
public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
    }

    private void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink { NavigateUri: { } uri })
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            }
            catch
            {
                // 打不开浏览器不值得打断用户，静默忽略。
            }
        }
    }
}
