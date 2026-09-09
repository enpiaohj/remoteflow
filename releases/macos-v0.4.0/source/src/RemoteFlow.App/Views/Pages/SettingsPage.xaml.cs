using System.Windows;
using System.Windows.Controls;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views.Pages;

/// <summary>
/// 「设置」页面。只负责应用级默认配置，不承担高频连接操作。
/// </summary>
public partial class SettingsPage : UserControl
{
    private bool _cloudInitialized;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // 首次显示时拉取一次已恢复的云会话状态，让面板反映后台 AutoRunner 的登录结果。
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_cloudInitialized)
        {
            return;
        }

        _cloudInitialized = true;
        if (DataContext is SettingsPageViewModel vm)
        {
            await vm.Cloud.InitializeAsync();
        }
    }

    // PasswordBox.Password 不支持绑定；登录框的密码在这里单向推给 ViewModel。
    private void CloudPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if ((DataContext as SettingsPageViewModel)?.Cloud is { } cloud && sender is PasswordBox box)
        {
            cloud.Password = box.Password;
        }
    }
}
