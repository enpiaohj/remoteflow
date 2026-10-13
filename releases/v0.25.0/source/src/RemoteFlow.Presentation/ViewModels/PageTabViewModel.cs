using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>
/// 工作区固定 Tab，承载左侧导航选中的页面（首页 / 我的连接 / 凭据 / 设置等）。
/// 该 Tab 始终存在且不可关闭，保证用户随时能从会话回到连接列表。
/// </summary>
public sealed partial class PageTabViewModel : WorkspaceTabViewModel
{
    /// <summary>当前显示的页面 ViewModel，由左侧导航切换。</summary>
    [ObservableProperty]
    private ObservableObject? _page;

    /// <summary>
    /// 页面图标资源键（Ui.* 线性图标，Windows 端使用）；<see cref="WorkspaceTabViewModel.Icon"/> 字形保留给其它端。
    /// </summary>
    [ObservableProperty]
    private string _iconKey = "Ui.Connections";

    public PageTabViewModel()
    {
        Title = "连接";
        Icon = "\uE968";
    }

    public override bool CanClose => false;
}
