using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RemoteFlow.App.Services;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>
/// 三选一确认对话框的结果。两按钮路径不使用该类型。
/// </summary>
public enum MessageDialogChoice
{
    /// <summary>最右的主操作按钮（如「退出」）。</summary>
    Confirm,

    /// <summary>中间的次操作按钮。仅 <see cref="MessageDialog.ShowChoice"/> 会显示该按钮。</summary>
    Alternative,

    /// <summary>取消按钮，或直接关窗（Esc / 系统关闭）。</summary>
    Cancel,
}

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

    /// <summary>
    /// 用户实际选择的按钮。默认 <see cref="MessageDialogChoice.Cancel"/>，
    /// 以便 Esc / Alt+F4 直接关窗时也落在「取消」而不是某个会被误当成确认的默认值上。
    /// </summary>
    public MessageDialogChoice Outcome { get; private set; } = MessageDialogChoice.Cancel;

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

    /// <summary>
    /// 显示三选一确认对话框，按「取消 / 次操作 / 主操作」从左到右排列。
    /// <para>
    /// 用于「主操作有破坏性、但还存在一条更安全的岔路」的场景：主操作走
    /// <c>Button.Danger</c> 警示色，两个安全出口（取消与次操作）保持中性样式。
    /// </para>
    /// </summary>
    /// <param name="owner">所有者窗口。</param>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    /// <param name="confirmText">最右的主操作按钮文案。</param>
    /// <param name="altText">中间次操作按钮文案。</param>
    /// <param name="isDanger">主操作是否为破坏性操作（决定是否使用警示色与默认焦点位置）。</param>
    public static MessageDialogChoice ShowChoice(
        Window? owner,
        string title,
        string message,
        string confirmText,
        string altText,
        bool isDanger)
    {
        var dialog = Create(owner, title, message, isDanger ? DialogKind.Warning : DialogKind.Info);

        dialog.ConfirmButton.Content = confirmText;
        dialog.AltButton.Content = altText;
        dialog.AltButton.Visibility = Visibility.Visible;

        if (isDanger)
        {
            dialog.ConfirmButton.Style = (Style)System.Windows.Application.Current.FindResource("Button.Danger");

            // 与 ShowConfirm 的破坏性路径一致：默认焦点给「取消」，回车不触发不可撤销的操作。
            // 若默认焦点落在主操作上，用户连按回车就会直接丢掉会话。
            dialog.ConfirmButton.IsDefault = false;
            dialog.CancelButton.IsDefault = true;
            dialog.Loaded += (_, _) => dialog.CancelButton.Focus();
        }

        dialog.ShowDialog();
        return dialog.Outcome;
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
        Outcome = MessageDialogChoice.Confirm;
        DialogResult = true;
        Close();
    }

    private void OnAltClick(object sender, RoutedEventArgs e)
    {
        // 与主操作一样以 DialogResult = true 收尾，三选一的真实结果由 Outcome 区分；
        // 两按钮路径永远看不到这颗按钮，因此 ShowConfirm 的 bool 语义不受影响。
        Outcome = MessageDialogChoice.Alternative;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Outcome = MessageDialogChoice.Cancel;
        DialogResult = false;
        Close();
    }
}
