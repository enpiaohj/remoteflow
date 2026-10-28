using System.Windows;
using System.Windows.Input;
using RemoteFlow.App.Services;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>添加 / 编辑 ESXi 主机或 vCenter。逻辑都在 <see cref="VmwareSourceEditorViewModel"/>，这里只做视图。</summary>
public partial class VmwareSourceEditorDialog : Window
{
    private readonly VmwareSourceEditorViewModel _editor;

    private VmwareSourceEditorDialog(VmwareSourceEditorViewModel editor)
    {
        _editor = editor;
        InitializeComponent();
        WindowBackdrop.PrepareDialog(this);
        DataContext = editor;

        // 保存成功后由视图模型通知关闭。
        editor.SaveCompleted += (_, _) =>
        {
            DialogResult = true;
            Close();
        };

        // 只有拖动标题才移动窗口，避免点选列表、输入框时误触发拖动。
        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    /// <summary>保存成功返回 true。</summary>
    public static bool Show(Window? owner, VmwareSourceEditorViewModel editor)
    {
        var dialog = new VmwareSourceEditorDialog(editor)
        {
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };
        return dialog.ShowDialog() == true;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // 密码在这里读取一次并直接交给保存流程，不进入 ViewModel；保存成功对话框即关闭，
        // 失败时保留输入，方便用户改地址或指纹后重试。
        await _editor.SaveAsync(PasswordInput.Password);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
