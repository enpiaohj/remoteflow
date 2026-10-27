using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 关于内容：版本、开发者、源码仓库、许可证、软件更新与隐私说明。设置页的「关于」选项卡承载本控件。
/// 外链用系统默认浏览器打开；打不开（无浏览器关联等）静默忽略，不打断。
/// </summary>
public partial class AboutContent : UserControl
{
    public AboutContent()
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
