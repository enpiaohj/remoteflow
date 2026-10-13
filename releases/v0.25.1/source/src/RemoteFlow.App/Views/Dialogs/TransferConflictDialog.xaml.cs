using System.Windows;
using System.Windows.Input;
using RemoteFlow.App.Services;
using RemoteFlow.Application.FileTransfer;
using RemoteFlow.Presentation;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>文件传输同名冲突询问：覆盖 / 保留两者 / 跳过 / 取消整批，可对本批后续冲突全部应用。</summary>
public partial class TransferConflictDialog : Window
{
    private TransferConflictDialog(TransferConflict conflict)
    {
        InitializeComponent();
        WindowBackdrop.PrepareDialog(this);

        NameText.Text = conflict.Name;
        ExistingText.Text = Describe(conflict.ExistingIsDirectory, conflict.ExistingSize, conflict.ExistingModified);
        IncomingText.Text = Describe(conflict.IncomingIsDirectory, conflict.IncomingSize, conflict.IncomingModified);

        if (!conflict.CanOverwrite)
        {
            OverwriteButton.IsEnabled = false;
            TypeMismatchText.Visibility = Visibility.Visible;
        }

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    /// <summary>用户的选择。默认取消：Esc / 直接关窗都落在「取消整批」而不是某个会丢数据的选项上。</summary>
    private ConflictDecision Decision { get; set; } = new(ConflictAction.Cancel);

    /// <summary>
    /// 弹出询问并返回决定。<paramref name="cancellationToken"/> 触发（该传输项被用户取消）时自动关闭对话框；
    /// 此时返回值无意义——调用方会因取消而丢弃它，不会把这次关闭误当成「取消整批」。
    /// </summary>
    public static ConflictDecision Ask(Window? owner, TransferConflict conflict, CancellationToken cancellationToken)
    {
        var dialog = new TransferConflictDialog(conflict)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };

        using var registration = cancellationToken.Register(
            () => dialog.Dispatcher.BeginInvoke(() =>
            {
                if (dialog.IsVisible)
                {
                    dialog.Close();
                }
            }));

        dialog.ShowDialog();
        return dialog.Decision;
    }

    private static string Describe(bool isDirectory, long? size, DateTimeOffset? modified)
    {
        var parts = new List<string> { isDirectory ? "文件夹" : (size is { } s ? ByteSizeFormatter.Format(s) : "文件") };
        if (modified is { } time)
        {
            parts.Add(time.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        }

        return string.Join(" · ", parts);
    }

    private void Finish(ConflictAction action)
    {
        Decision = new ConflictDecision(action, ApplyToAllCheck.IsChecked == true);
        DialogResult = true;
        Close();
    }

    private void OnOverwriteClick(object sender, RoutedEventArgs e) => Finish(ConflictAction.Overwrite);

    private void OnRenameClick(object sender, RoutedEventArgs e) => Finish(ConflictAction.Rename);

    private void OnSkipClick(object sender, RoutedEventArgs e) => Finish(ConflictAction.Skip);

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Decision = new ConflictDecision(ConflictAction.Cancel);
        DialogResult = false;
        Close();
    }
}
