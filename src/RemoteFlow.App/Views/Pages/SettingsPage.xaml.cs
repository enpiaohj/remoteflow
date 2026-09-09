using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private CloudSyncViewModel? Cloud => (DataContext as SettingsPageViewModel)?.Cloud;

    // PasswordBox.Password 不支持绑定；这里把各口令框单向推给 ViewModel。
    private void CloudPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Cloud is { } cloud && sender is PasswordBox box)
        {
            cloud.Password = box.Password;
        }
    }

    private void CloudPasswordConfirmBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Cloud is { } cloud && sender is PasswordBox box)
        {
            cloud.PasswordConfirm = box.Password;
        }
    }

    private void CloudUnlockPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Cloud is { } cloud && sender is PasswordBox box)
        {
            cloud.UnlockPassword = box.Password;
        }
    }

    // 服务地址默认只读；左键三击进入可编辑（无界面提示，属刻意隐藏的高级操作）。
    private void CloudServerUrlBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 3 && Cloud is { } cloud && sender is TextBox box)
        {
            cloud.UnlockServerUrlField();
            box.IsReadOnly = false;
            box.Focus();
            box.SelectAll();
            e.Handled = true;
        }
    }
}
