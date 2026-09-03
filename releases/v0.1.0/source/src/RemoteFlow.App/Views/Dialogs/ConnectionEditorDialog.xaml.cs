using System.Windows;
using System.Windows.Input;
using RemoteFlow.App.ViewModels;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>
/// 新建 / 编辑连接对话框。首屏只放必填字段，协议专项参数收进「高级设置」折叠区。
/// </summary>
public partial class ConnectionEditorDialog : Window
{
    private readonly ConnectionEditorViewModel _viewModel;

    public ConnectionEditorDialog(ConnectionEditorViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // 无系统标题栏，允许拖拽标题区移动窗口。
        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>校验通过后的连接配置。</summary>
    public ConnectionProfile? Result { get; private set; }

    /// <summary>用户是否选择了「保存并连接」。</summary>
    public bool ConnectImmediately { get; private set; }

    private void OnSaveClick(object sender, RoutedEventArgs e) => TrySave(connect: false);

    private void OnSaveAndConnectClick(object sender, RoutedEventArgs e) => TrySave(connect: true);

    private void TrySave(bool connect)
    {
        // 校验不通过时保持对话框打开，错误提示显示在底部操作区左侧。
        if (_viewModel.Build() is not { } profile)
        {
            return;
        }

        Result = profile;
        ConnectImmediately = connect;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
