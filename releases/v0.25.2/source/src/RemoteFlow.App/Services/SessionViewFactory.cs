using System.Windows;
using Microsoft.Extensions.Logging;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.App.Views.Sessions;
using RemoteFlow.Core.Models;
using RemoteFlow.Protocol.Rdp;
using RemoteFlow.Protocol.Ssh;
using RemoteFlow.Protocol.Vnc;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.App.Services;

/// <summary>
/// 按协议创建对应的会话宿主视图。
/// <para>
/// 存在的意义：会话视图由 XAML 触发创建，无法走构造注入，
/// 因此把依赖集中到本工厂，视图侧只需要一次工厂调用。
/// </para>
/// </summary>
public interface ISessionViewFactory
{
    /// <summary>为会话创建承载视图。协议不受支持时返回一个说明性占位视图。</summary>
    FrameworkElement Create(SessionTabViewModel tab);
}

public sealed class SessionViewFactory(
    AppSettings settings,
    ThemeService theme,
    IDialogService dialogs,
    ILoggerFactory loggerFactory) : ISessionViewFactory
{
    public FrameworkElement Create(SessionTabViewModel tab) => tab.Session switch
    {
        RdpSession rdp => new RdpSessionView(rdp, tab, loggerFactory.CreateLogger<RdpSessionView>()),

        SshSession ssh => new SshSessionView(
            ssh, tab, settings, theme, dialogs, loggerFactory.CreateLogger<SshSessionView>()),

        VncSession vnc => new VncSessionView(vnc, tab, loggerFactory.CreateLogger<VncSessionView>()),

        _ => new System.Windows.Controls.TextBlock
        {
            Text = $"暂不支持 {tab.Protocol} 协议的会话显示。",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = System.Windows.Media.Brushes.Gray
        }
    };
}
