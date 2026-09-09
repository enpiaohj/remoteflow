using System.Windows;
using System.Windows.Input;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>标签管理对话框——新建 / 编辑 / 删除标签，纯粹的列表 CRUD，没有单独的「保存」步骤。</summary>
public partial class TagManagerDialog : Window
{
    public TagManagerDialog(TagManagerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
