using System.Windows;
using System.Windows.Input;
using RemoteFlow.App.Services;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>新建 / 重命名分组的小对话框——只收一个分组名。</summary>
public partial class GroupNameDialog : Window
{
    private GroupNameDialog(GroupNamePrompt prompt)
    {
        InitializeComponent();

        TitleText.Text = prompt.Title;
        NameInput.Text = prompt.InitialName;

        if (!string.IsNullOrEmpty(prompt.ParentName))
        {
            SubtitleText.Text = $"将创建在「{prompt.ParentName}」下";
            SubtitleText.Visibility = Visibility.Visible;
        }

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    /// <summary>用户填写的分组名（已 Trim）。<c>null</c> 表示取消。</summary>
    public string? GroupName { get; private set; }

    public static string? Prompt(Window? owner, GroupNamePrompt prompt)
    {
        var dialog = new GroupNameDialog(prompt)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };
        return dialog.ShowDialog() == true ? dialog.GroupName : null;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var name = NameInput.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "分组名称不能为空。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        GroupName = name;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
