using System.Windows;
using System.Windows.Input;
using RemoteFlow.App.Services;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>虚拟机快照管理。逻辑都在 <see cref="VmwareSnapshotManagerViewModel"/>，这里只做视图。</summary>
public partial class VmwareSnapshotDialog : Window
{
    private readonly VmwareSnapshotManagerViewModel _manager;

    private VmwareSnapshotDialog(VmwareSnapshotManagerViewModel manager)
    {
        _manager = manager;
        InitializeComponent();
        WindowBackdrop.PrepareDialog(this);
        DataContext = manager;

        // 只有拖动标题才移动窗口，避免点选列表、输入框时误触发拖动。
        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        Loaded += async (_, _) => await _manager.LoadAsync();
    }

    public static void Show(Window? owner, VmwareSnapshotManagerViewModel manager)
    {
        new VmwareSnapshotDialog(manager)
        {
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        }.ShowDialog();
    }

    // 操作进行中不允许关闭：避免用户以为已取消，而服务器上的任务其实仍在执行。
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_manager.IsBusy)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
