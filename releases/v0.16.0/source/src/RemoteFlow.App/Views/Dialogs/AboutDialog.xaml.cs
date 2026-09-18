using System.Windows;
using System.Windows.Input;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>「关于 RemoteFlow」+ 快捷键速查，一起放一个对话框里——内容不多，
/// 不值得单独占一个设置标签或者导航页面。</summary>
public partial class AboutDialog : Window
{
    private AboutDialog(string version)
    {
        InitializeComponent();

        VersionRun.Text = version;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    public static void Show(Window? owner, string version)
    {
        var dialog = new AboutDialog(version)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };
        dialog.ShowDialog();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
