using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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

    [Theory]
    [InlineData(1200, 240, 480, 720)]
    [InlineData(1200, 480, 360, 840)]
    [InlineData(200, 320, 0, 200)]
    public void RevealBand_BeforeDragging_OnlyCoversCenteredPill(
        double rootWidth, double pillWidth, double left, double right)
    {
        RunRevealBandTest((view, _, _, popup) =>
        {
            // 隐藏的 Popup 偏移可能过期；未拖动时应按当前布局重新计算居中位置。
            popup.HorizontalOffset = 75;

            Assert.True(IsInRevealBand(view, new Point(left, 0)));
            Assert.True(IsInRevealBand(view, new Point(right, 0)));
            Assert.True(IsInRevealBand(view, new Point((left + right) / 2, 0)));
            Assert.False(IsInRevealBand(view, new Point(left - 0.01, 0)));
            Assert.False(IsInRevealBand(view, new Point(right + 0.01, 0)));
        }, rootWidth, pillWidth);
    }

    [Theory]
    [InlineData(90, 104, 424)]
    [InlineData(-174, 0, 160)]
    [InlineData(1046, 1060, 1200)]
    public void RevealBand_AfterDragging_FollowsPillAndClipsToHost(
        double horizontalOffset, double left, double right)
    {
        RunRevealBandTest((view, _, pill, popup) =>
        {
            Assert.Equal(14d, pill.Margin.Left);
            typeof(SessionHostView).GetField("_userMoved", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(view, true);
            popup.HorizontalOffset = horizontalOffset;

            Assert.True(IsInRevealBand(view, new Point(left, 0)));
            Assert.True(IsInRevealBand(view, new Point(right, 0)));
            Assert.True(IsInRevealBand(view, new Point((left + right) / 2, 0)));
            Assert.False(IsInRevealBand(view, new Point(left - 0.01, 0)));
            Assert.False(IsInRevealBand(view, new Point(right + 0.01, 0)));
            Assert.False(IsInRevealBand(view, new Point(600, 0)));
        });
    }

    [Fact]
    public void RevealBand_BeforePillIsMeasured_UsesCenteredFallbackWidth()
    {
        RunRevealBandTest((view, _, pill, _) =>
        {
            Assert.Equal(0d, pill.ActualWidth);

            Assert.True(IsInRevealBand(view, new Point(440, 0)));
            Assert.True(IsInRevealBand(view, new Point(760, 0)));
            Assert.False(IsInRevealBand(view, new Point(439.99, 0)));
            Assert.False(IsInRevealBand(view, new Point(760.01, 0)));
        }, pillWidth: null);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.25, 1.25)]
    [InlineData(1.5, 1.5)]
    [InlineData(2.0, 2.0)]
    [InlineData(1.25, 2.0)]
    public void RevealBand_AtDifferentDpi_IsExactlyThreePhysicalPixelsHigh(double scaleX, double scaleY)
    {
        RunRevealBandTest((view, root, _, _) =>
        {
            // 设置独立可视树的实际 DPI，避免仅调用 OnDpiChanged 而 GetDpi 仍返回旧值。
            VisualTreeHelper.SetRootDpi(root, new DpiScale(scaleX, scaleY));
            Assert.Equal(scaleY, VisualTreeHelper.GetDpi(root).DpiScaleY);

            Assert.True(IsInRevealBand(view, new Point(600, 0)));
            Assert.True(IsInRevealBand(view, new Point(600, 2.99 / scaleY)));
            Assert.True(IsInRevealBand(view, new Point(600, 3 / scaleY)));
            Assert.False(IsInRevealBand(view, new Point(600, 3.01 / scaleY)));
            Assert.False(IsInRevealBand(view, new Point(600, -0.01 / scaleY)));
        });
    }

    private static bool IsInRevealBand(SessionHostView view, Point point) =>
        (bool)typeof(SessionHostView).GetMethod("IsInRevealBand", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [point])!;

    private static void RunRevealBandTest(
        Action<SessionHostView, Grid, Border, Popup> test, double rootWidth = 1200, double? pillWidth = 320)
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                // 不挂载窗口、不打开 Popup，布局即可驱动命中判断，也不会建立协议连接或鼠标钩子。
                var view = new SessionHostView();
                var root = (Grid)view.FindName("RootGrid");
                var pill = (Border)view.FindName("PillBar");
                var popup = (Popup)view.FindName("ToolbarPopup");
                root.Width = rootWidth;
                root.Measure(new Size(rootWidth, 800));
                root.Arrange(new Rect(0, 0, rootWidth, 800));
                Assert.Equal(rootWidth, root.ActualWidth);

                if (pillWidth is { } width)
                {
                    pill.Width = width;
                    var size = new Size(width + pill.Margin.Left + pill.Margin.Right, 100);
                    pill.Measure(size);
                    pill.Arrange(new Rect(size));
                    Assert.Equal(width, pill.ActualWidth);
                }

                test(view, root, pill, popup);
            }
            finally
            {
                UiTestHost.RestoreAppResources();
            }
        });
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
