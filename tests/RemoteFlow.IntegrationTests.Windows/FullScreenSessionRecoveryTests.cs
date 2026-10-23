using System.IO;
using System.Windows;
using System.Windows.Controls;
using RemoteFlow.App.Views.Sessions;
using Xunit;

namespace RemoteFlow.IntegrationTests;

[Collection(WpfApplicationCollection.Name)]
public sealed class FullScreenSessionRecoveryTests
{
    [Fact]
    public void InterruptedSession_HidesProtocolHostAndShowsRecoveryCard()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                var view = new SessionHostView();
                var content = (ContentControl)view.FindName("SessionContent");
                var overlay = (Border)view.FindName("InterruptedOverlay");

                // 不创建协议连接，只驱动与真实 VM 相同的状态绑定。
                content.DataContext = overlay.DataContext = new InterruptionState { IsInterrupted = false };
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                content.GetBindingExpression(UIElement.VisibilityProperty)!.UpdateTarget();
                overlay.GetBindingExpression(UIElement.VisibilityProperty)!.UpdateTarget();
                Assert.Equal(Visibility.Visible, content.Visibility);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);

                content.DataContext = overlay.DataContext = new InterruptionState { IsInterrupted = true };
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                content.GetBindingExpression(UIElement.VisibilityProperty)!.UpdateTarget();
                overlay.GetBindingExpression(UIElement.VisibilityProperty)!.UpdateTarget();
                Assert.True(content.Visibility == Visibility.Collapsed,
                    $"协议宿主={content.Visibility}，绑定路径={content.GetBindingExpression(UIElement.VisibilityProperty)?.ParentBinding.Path}，状态源={content.DataContext}");
                Assert.True(overlay.Visibility == Visibility.Visible,
                    $"状态层={overlay.Visibility}，绑定路径={overlay.GetBindingExpression(UIElement.VisibilityProperty)?.ParentBinding.Path}");

                content.DataContext = overlay.DataContext = new InterruptionState { IsInterrupted = false };
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                content.GetBindingExpression(UIElement.VisibilityProperty)!.UpdateTarget();
                overlay.GetBindingExpression(UIElement.VisibilityProperty)!.UpdateTarget();
                Assert.Equal(Visibility.Visible, content.Visibility);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            }
            finally
            {
                UiTestHost.RestoreAppResources();
            }
        });
    }

    [Fact]
    public void Pill_StaysAtTopAndProtectsSharedContextMenu()
    {
        var code = File.ReadAllText(ProjectFile("SessionHostView.xaml.cs"));
        var markup = File.ReadAllText(ProjectFile("SessionHostView.xaml"));

        Assert.DoesNotContain("ClampVertical", code);
        Assert.DoesNotContain("_dragStartV", code);
        Assert.Contains("ToolbarPopup.VerticalOffset = 0;", code);
        Assert.Contains("ContextMenuOpening=\"OnPillContextMenuOpening\"", markup);
        Assert.Contains("TryFindResource(\"SessionTabContextMenu\")", code);
        Assert.Contains("menu.DataContext = _tab;", code);
        Assert.Contains("menu.PlacementTarget = PillBar;", code);
        Assert.Contains("!IsToolbarMenuOpen", code);
        Assert.Contains("|| IsToolbarMenuOpen", code);
        Assert.Contains("CloseToolbarMenus();", code);
        Assert.Contains("Command=\"{Binding ExitFullScreenCommand}\"", markup);
    }

    public sealed class InterruptionState
    {
        public bool IsInterrupted { get; init; }
    }

    private static string ProjectFile(string name)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "RemoteFlow.App", "Views", "Sessions", name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException(name);
    }
}
