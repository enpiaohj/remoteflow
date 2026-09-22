using System.Windows;
using System.Windows.Input;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>
/// 口令输入对话框。用于凭据加密备份的「设置口令」与「输入口令」。
/// <para>
/// <b>安全设计：</b>口令走 <see cref="System.Windows.Controls.PasswordBox"/> 且不参与数据绑定，
/// 只在用户确认时读取一次；窗口关闭时立即清空所有输入框。「眼睛」切换时在密文框与
/// 明文框之间互传值，明文不进入任何绑定属性。
/// </para>
/// </summary>
public partial class PasswordPromptDialog : Window
{
    private readonly bool _confirm;

    private PasswordPromptDialog(string title, string message, bool confirm)
    {
        InitializeComponent();

        _confirm = confirm;
        TitleText.Text = title;
        MessageText.Text = message;

        if (confirm)
        {
            ConfirmLabel.Visibility = Visibility.Visible;
            ConfirmRow.Visibility = Visibility.Visible;
        }

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        Loaded += (_, _) => PasswordInput.Focus();
    }

    /// <summary>用户输入的口令。<c>null</c> 表示取消。</summary>
    public string? Password { get; private set; }

    /// <summary>弹出口令对话框，返回用户输入的口令，取消返回 <c>null</c>。</summary>
    public static string? Prompt(Window? owner, string title, string message, bool confirm)
    {
        var dialog = new PasswordPromptDialog(title, message, confirm)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
        };

        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    private void OnRevealToggled(object sender, RoutedEventArgs e)
    {
        if (RevealToggle.IsChecked == true)
        {
            PasswordReveal.Text = PasswordInput.Password;
            ConfirmReveal.Text = ConfirmInput.Password;
            Swap(passwordVisible: true);
        }
        else
        {
            PasswordInput.Password = PasswordReveal.Text;
            ConfirmInput.Password = ConfirmReveal.Text;
            PasswordReveal.Clear();
            ConfirmReveal.Clear();
            Swap(passwordVisible: false);
        }
    }

    private void Swap(bool passwordVisible)
    {
        var masked = passwordVisible ? Visibility.Collapsed : Visibility.Visible;
        var plain = passwordVisible ? Visibility.Visible : Visibility.Collapsed;

        PasswordInput.Visibility = masked;
        PasswordReveal.Visibility = plain;
        ConfirmInput.Visibility = masked;
        ConfirmReveal.Visibility = plain;
    }

    private string CurrentPassword =>
        RevealToggle.IsChecked == true ? PasswordReveal.Text : PasswordInput.Password;

    private string CurrentConfirm =>
        RevealToggle.IsChecked == true ? ConfirmReveal.Text : ConfirmInput.Password;

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        var entered = CurrentPassword;

        if (string.IsNullOrEmpty(entered))
        {
            ShowError("请输入口令。");
            return;
        }

        if (_confirm && entered != CurrentConfirm)
        {
            ShowError("两次输入的口令不一致。");
            return;
        }

        Password = entered;
        ClearInputs();
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        ClearInputs();
        DialogResult = false;
        Close();
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearInputs()
    {
        PasswordInput.Clear();
        ConfirmInput.Clear();
        PasswordReveal.Clear();
        ConfirmReveal.Clear();
    }
}
