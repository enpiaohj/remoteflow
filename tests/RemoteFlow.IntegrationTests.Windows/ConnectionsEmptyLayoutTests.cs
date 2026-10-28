using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteFlow.App.Views.Pages;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

[Collection(WpfApplicationCollection.Name)]
public sealed class ConnectionsEmptyLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyLibrary_ActionsFitNarrowWorkspaceAndFollowPalette(bool dark)
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                UiTestHost.UsePalette(dark);
                var vm = new ConnectionsPageViewModel(null!, null!, null!, null!, null!, null!,
                    null!, null!, null!, SynchronousUiDispatcher.Instance, null!);
                // 960 × 640 主窗口扣除导航、标题与状态栏后的保守可用空间。
                var page = new ConnectionsPage { DataContext = vm };
                page.Measure(new Size(740, 490));
                page.Arrange(new Rect(0, 0, 740, 490));
                page.UpdateLayout();
                var action = (Button)page.FindName("EmptyStateAction");
                var title = (TextBlock)page.FindName("EmptyStateTitle");
                var scroll = (ScrollViewer)page.FindName("EmptyStateScroll");
                Assert.Equal(Visibility.Visible, scroll.Visibility);
                Assert.Equal("从第一个连接开始", title.Text);
                Assert.Equal(((SolidColorBrush)page.FindResource("Text.Primary")).Color,
                    ((SolidColorBrush)title.Foreground).Color);
                Assert.True(action.ActualWidth > 0);
                var bounds = action.TransformToAncestor(page).TransformBounds(new Rect(action.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Right <= 740 && bounds.Top >= 0 && bounds.Bottom <= 490,
                    $"主要操作超出工作区：{bounds}");
                Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                vm.IsLoading = true;
                page.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, scroll.Visibility);
            }
            finally { UiTestHost.RestoreAppResources(); }
        });
    }
}
