using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.App.Services;
using RemoteFlow.App.Views.Sessions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.ViewModels;
using RemoteFlow.Protocol.Rdp;
using RemoteFlow.Protocol.Rdp.Interop;
using Xunit;

namespace RemoteFlow.IntegrationTests;

[Collection(WpfApplicationCollection.Name)]
public sealed class RdpDisplayLifecycleTests
{
    [Theory]
    [InlineData(RdpDisplayMode.FitToWindow, true)]
    [InlineData(RdpDisplayMode.FixedResolution, false)]
    public void LoginComplete_SchedulesDisplaySyncWithoutAnotherWindowResize(RdpDisplayMode mode, bool expected)
    {
        Run(mode, (session, view, timer) =>
        {
            Assert.False(timer.IsEnabled);
            typeof(RdpSession).GetProperty(nameof(RdpSession.State))!.SetValue(session, ConnectionState.Connecting);

            session.OnLoginComplete();
            view.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal(expected, timer.IsEnabled);
        });
    }

    [Fact]
    public void MonitorDpiChange_SchedulesDisplaySyncWithoutSizeChanged()
    {
        Run(RdpDisplayMode.FitToWindow, (_, view, timer) =>
        {
            typeof(RdpSessionView).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [new DpiScale(1, 1), new DpiScale(1.5, 1.5)]);

            Assert.True(timer.IsEnabled);
        });
    }

    [Fact]
    public void ReturningToHiddenTab_SchedulesDisplaySync()
    {
        Run(RdpDisplayMode.FitToWindow, (_, view, timer) =>
        {
            // 为可见性测试建立真实可视树，但不显示窗口、不发起 RDP 连接。
            typeof(RdpSessionView).GetField("_connectStarted", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(view, true);
            using var source = new HwndSource(new HwndSourceParameters("RDP display regression")
            {
                Width = 800, Height = 600, WindowStyle = 0
            });
            source.RootVisual = view;
            view.Visibility = Visibility.Collapsed;
            timer.Stop();

            view.Visibility = Visibility.Visible;

            Assert.True(timer.IsEnabled);
            source.RootVisual = null;
        });
    }

    [Fact]
    public void Dispose_PreventsQueuedLoginSyncFromRestartingTimer()
    {
        Run(RdpDisplayMode.FitToWindow, (session, view, timer) =>
        {
            typeof(RdpSession).GetProperty(nameof(RdpSession.State))!.SetValue(session, ConnectionState.Connecting);
            session.OnLoginComplete();

            view.Dispose();
            view.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.False(timer.IsEnabled);
        });
    }

    private static void Run(RdpDisplayMode mode, Action<RdpSession, RdpSessionView, DispatcherTimer> test)
    {
        UiTestHost.RunOnSta(() =>
        {
            var located = RdpControlLocator.Locate();
            Assert.NotNull(located);
            var session = new RdpSession(new SessionRequest
            {
                Profile = new ConnectionProfile { Rdp = new RdpOptions { DisplayMode = mode } }
            }, located.Value.Clsid.ToString(), NullLogger.Instance);
            using var vm = new SessionTabViewModel(session, _ => Task.CompletedTask, _ => Task.CompletedTask,
                new WpfUiDispatcher(), new WpfUiTimerFactory());
            using var view = new RdpSessionView(session, vm, NullLogger<RdpSessionView>.Instance);
            try
            {
                var timer = (DispatcherTimer)typeof(RdpSessionView)
                    .GetField("_resizeTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                test(session, view, timer);
            }
            finally
            {
                view.Dispose();
                session.DisposeAsync().GetAwaiter().GetResult();
            }
        });
    }
}
