using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RemoteFlow.App.Services;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>
/// 通用消息 / 确认对话框。替代系统 MessageBox，以便跟随应用主题并保持一致的排版。
/// </summary>
public partial class MessageDialog : Window
{
    private MessageDialog()
    {
        InitializeComponent();

        // 无系统标题栏，允许拖拽窗体空白处移动。
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    /// <summary>显示只有一个「知道了」按钮的信息提示。</summary>
    public static void ShowMessage(Window? owner, string title, string message, DialogKind kind)
    {
        var dialog = Create(owner, title, message, kind);
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        dialog.ConfirmButton.Content = "知道了";
        dialog.ShowDialog();
    }

    /// <summary>显示确认对话框，返回用户是否确认。</summary>
    public static bool ShowConfirm(
        Window? owner, string title, string message, string confirmText, bool isDanger)
    {
        var dialog = Create(owner, title, message, isDanger ? DialogKind.Warning : DialogKind.Info);
        dialog.ConfirmButton.Content = confirmText;

        if (isDanger)
        {
            // 危险操作使用独立的警示色，与主操作色区分开。
            dialog.ConfirmButton.Style = (Style)System.Windows.Application.Current.FindResource("Button.Danger");
            // 默认焦点给「取消」，避免回车误触发不可撤销的操作。
            dialog.ConfirmButton.IsDefault = false;
            dialog.CancelButton.IsDefault = true;
            dialog.Loaded += (_, _) => dialog.CancelButton.Focus();
        }

        return dialog.ShowDialog() == true;
    }

    private static MessageDialog Create(Window? owner, string title, string message, DialogKind kind)
    {
        var dialog = new MessageDialog
        {
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };

        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;

        // 图标与颜色同时表达类型，不单靠颜色传达信息。
        var (glyphKey, brushKey) = kind switch
        {
            DialogKind.Success => ("Icon.Success", "Status.Success"),
            DialogKind.Warning => ("Icon.Warning", "Status.Warning"),
            DialogKind.Error => ("Icon.Error", "Status.Danger"),
            _ => ("Icon.Info", "Status.Info")
        };

        dialog.IconGlyph.Text = (string)System.Windows.Application.Current.FindResource(glyphKey);
        dialog.IconGlyph.Foreground = (Brush)System.Windows.Application.Current.FindResource(brushKey);

        return dialog;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
