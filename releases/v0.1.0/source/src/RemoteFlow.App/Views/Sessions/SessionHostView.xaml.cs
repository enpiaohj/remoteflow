using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.App.Services;
using RemoteFlow.App.ViewModels;
using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Views.Sessions;

/// <summary>
/// 会话 Tab 的外壳：工具条 + 协议视图 + 状态层。
/// <para>
/// 三种协议共用同一套 Tab 外壳与状态层，
/// 但工具条只显示当前协议真正需要的操作（产品设计文档 §7.9）。
/// </para>
/// </summary>
public partial class SessionHostView : UserControl
{
    private FrameworkElement? _protocolView;

    public SessionHostView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not SessionTabViewModel tab)
        {
            return;
        }

        ShowToolsFor(tab.Protocol);

        // 协议视图只创建一次；Tab 切换靠可见性，不重建视图，
        // 否则会话会被反复销毁重连。
        if (_protocolView is not null)
        {
            return;
        }

        var factory = App.Services.GetRequiredService<ISessionViewFactory>();
        _protocolView = factory.Create(tab);
        SessionContent.Content = _protocolView;
    }

    /// <summary>按协议显示对应的工具条分组。</summary>
    private void ShowToolsFor(ProtocolType protocol)
    {
        RdpTools.Visibility = protocol == ProtocolType.Rdp ? Visibility.Visible : Visibility.Collapsed;
        SshTools.Visibility = protocol == ProtocolType.Ssh ? Visibility.Visible : Visibility.Collapsed;
        VncTools.Visibility = protocol == ProtocolType.Vnc ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Tab 被移出集合时释放协议视图持有的宿主资源（WinForms 宿主、WebView2、渲染回调）。
    /// <para>
    /// 注意：切换 Tab 只改变可见性，不会触发 Unloaded，因此不会误伤后台会话；
    /// 只有 Tab 真正从工作区移除时才走到这里。会话本身由 SessionManager 释放。
    /// </para>
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_protocolView is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _protocolView = null;
        SessionContent.Content = null;
    }
}
