using System.Windows;
using System.Windows.Input;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Views.Dialogs;

/// <summary>
/// 新建 / 编辑连接对话框。首屏只放必填字段，协议专项参数收进「高级设置」折叠区。
/// </summary>
public partial class ConnectionEditorDialog : Window
{
    private readonly ConnectionEditorViewModel _viewModel;
    private readonly Func<Task<IReadOnlyList<Tag>>> _manageTags;

    /// <param name="manageTags">
    /// 弹出标签管理对话框、返回最新标签列表——由 <c>DialogService</c> 注入，
    /// 这样 <see cref="ConnectionEditorViewModel"/> 本身不需要持有任何服务引用，
    /// 保持它一贯的「纯数据」构造方式（数据由 DialogService 预先取好传入）。
    /// </param>
    public ConnectionEditorDialog(ConnectionEditorViewModel viewModel, Func<Task<IReadOnlyList<Tag>>> manageTags)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _manageTags = manageTags;
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

    private async void OnManageTagsClick(object sender, RoutedEventArgs e)
    {
        var latestTags = await _manageTags();
        _viewModel.RefreshAvailableTags(latestTags);
    }
}
