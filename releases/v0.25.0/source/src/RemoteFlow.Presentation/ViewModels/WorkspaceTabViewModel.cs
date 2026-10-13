using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteFlow.Presentation.ViewModels;

/// <summary>
/// 中央工作区的 Tab 基类。
/// <para>
/// 产品设计文档 §7.5 要求「连接列表与远程会话共享同一区域」，
/// 因此工作区第一个 Tab 固定承载当前导航页（<see cref="PageTabViewModel"/>），
/// 其后是各个远程会话（<see cref="SessionTabViewModel"/>）。
/// </para>
/// </summary>
public abstract partial class WorkspaceTabViewModel : ObservableObject
{
    /// <summary>Tab 标题。</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Tab 图标字形。</summary>
    [ObservableProperty]
    private string _icon = string.Empty;

    /// <summary>
    /// 该 Tab 是否为当前选中项。
    /// <para>
    /// 之所以由 ViewModel 显式持有而不依赖 <c>TabControl</c>，
    /// 是因为 WPF 的 TabControl 只为选中项生成可视化树，切换 Tab 会卸载并重建内容——
    /// 那会直接销毁 RDP ActiveX 控件与终端 WebView，等同于掐断会话。
    /// 因此工作区改为「所有 Tab 内容常驻可视树、仅切换可见性」，
    /// 由本属性驱动，满足「切换 Tab 不应主动断开会话」的要求。
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>是否可以关闭。工作区 Tab 不可关闭。</summary>
    public virtual bool CanClose => true;
}
