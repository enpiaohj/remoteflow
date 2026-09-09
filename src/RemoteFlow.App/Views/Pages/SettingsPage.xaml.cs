using System.Windows;
using System.Windows.Controls;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 「设置」页面。只负责应用级默认配置，不承担高频连接操作。
/// </summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    // PasswordBox.Password 不支持绑定；登录框的密码在这里单向推给 ViewModel。
    private void CloudPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if ((DataContext as SettingsPageViewModel)?.Cloud is { } cloud && sender is PasswordBox box)
        {
            cloud.Password = box.Password;
        }
    }
}
