using System.Windows;
using System.Windows.Input;
using RemoteFlow.App.Services;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>单行文本输入对话框（重命名 / 新建文件夹）。名称是否合法由调用方校验并提示。</summary>
public partial class TextPromptDialog : Window
{
    private TextPromptDialog(string title, string label, string initialText)
    {
        InitializeComponent();
        WindowBackdrop.PrepareDialog(this);

        TitleText.Text = title;
        LabelText.Text = label;
        Input.Text = initialText;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        Loaded += (_, _) =>
        {
            Input.Focus();
            // 重命名时只选中不含扩展名的部分，直接输入即可改名主体（与资源管理器一致）。
            var dot = initialText.LastIndexOf('.');
            if (dot > 0)
            {
                Input.Select(0, dot);
            }
            else
            {
                Input.SelectAll();
            }
        };
    }

    /// <summary>用户输入的文本（未 Trim）。<c>null</c> 表示取消。</summary>
    public string? Value { get; private set; }

    public static string? Prompt(Window? owner, string title, string label, string initialText)
    {
        var dialog = new TextPromptDialog(title, label, initialText)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Value = Input.Text;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
