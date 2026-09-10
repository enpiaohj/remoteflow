using System.Windows;
using System.Windows.Input;
using RemoteFlow.App.Services;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>删除默认分组后，单选一个新的默认分组。返回 null 表示取消。</summary>
public partial class SelectDefaultGroupDialog : Window
{
    private SelectDefaultGroupDialog(string deletedDefaultName, IReadOnlyList<DefaultGroupOption> options)
    {
        InitializeComponent();
        TitleText.Text = "选择新的默认分组";
        SubtitleText.Text = $"即将删除默认分组「{deletedDefaultName}」，请选择新的默认新建连接分组：";
        GroupList.ItemsSource = options;
        GroupList.SelectedIndex = 0;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    public static DefaultGroupOption? Pick(Window? owner, string deletedDefaultName, IReadOnlyList<DefaultGroupOption> options)
    {
        var dialog = new SelectDefaultGroupDialog(deletedDefaultName, options)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };
        return dialog.ShowDialog() == true && dialog.GroupList.SelectedItem is DefaultGroupOption picked
            ? picked
            : null;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
