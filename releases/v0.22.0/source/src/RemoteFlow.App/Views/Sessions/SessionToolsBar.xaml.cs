using System.Windows;
using System.Windows.Controls;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Views.Sessions;

/// <summary>
/// 会话工具条：显示在标题栏行（Tab 条右侧）的会话级操作区。
/// <para>
/// 内容自会话视图内的常驻条迁移而来（状态入口 / 主机名 / 协议专属操作 / 全屏档切换）。
/// DataContext 为选中的 <see cref="SessionTabViewModel"/>；协议专属分组（RDP / SSH / VNC）
/// 按 <see cref="SessionTabViewModel.Protocol"/> 显隐。状态入口点击通过
/// <see cref="SessionTabViewModel.StatusEntryRequested"/> 通知宿主打开质量详情 Flyout
/// （Flyout 定位与生命周期仍由会话视图管理）。
/// </para>
/// </summary>
public sealed partial class SessionToolsBar : UserControl
{
    public SessionToolsBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ApplyProtocolVisibility();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => ApplyProtocolVisibility();

    /// <summary>按会话协议显示对应的工具分组，其余隐藏。</summary>
    private void ApplyProtocolVisibility()
    {
        if (DataContext is not SessionTabViewModel tab)
        {
            return;
        }

        RdpToolsDock.Visibility = tab.Protocol == ProtocolType.Rdp ? Visibility.Visible : Visibility.Collapsed;
        SshToolsDock.Visibility = tab.Protocol == ProtocolType.Ssh ? Visibility.Visible : Visibility.Collapsed;
        VncToolsDock.Visibility = tab.Protocol == ProtocolType.Vnc ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStatusEntryClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SessionTabViewModel tab)
        {
            tab.RaiseStatusEntryRequested();
        }
    }
}
