using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteFlow.App.Views.Dialogs;
using RemoteFlow.App.Views.Pages;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Vmware;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

[Collection(WpfApplicationCollection.Name)]
public sealed class VmwareLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewSourceForm_ScrollsWhileSaveRemainsReachable(bool dark)
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                UiTestHost.UsePalette(dark);
                var editor = new VmwareSourceEditorViewModel(null!, null, []);
                var dialog = (Window)Activator.CreateInstance(typeof(VmwareSourceEditorDialog),
                    BindingFlags.Instance | BindingFlags.NonPublic, null, [editor], null)!;
                var root = (FrameworkElement)dialog.Content;
                root.Measure(new Size(620, 540));
                root.Arrange(new Rect(0, 0, 620, 540));
                root.UpdateLayout();

                var scroll = Descendants(root).OfType<ScrollViewer>().First();
                Assert.True(scroll.ScrollableHeight > 0, "新凭据展开后应滚动正文，而不是挤出保存按钮。");
                var save = Descendants(root).OfType<Button>().Single(b => b.Content as string == "登录并保存");
                var bounds = save.TransformToAncestor(root).TransformBounds(new Rect(save.RenderSize));
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= 540);
                Assert.True(editor.UsesNewCredential);
                dialog.Close();
            }
            finally { UiTestHost.RestoreAppResources(); }
        });
    }

    [Fact]
    public void NarrowWorkspace_KeepsActionColumnAndMovesDetailsBelowList()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                var page = new VmwarePage();
                page.Measure(new Size(900, 640));
                page.Arrange(new Rect(0, 0, 900, 640));
                page.UpdateLayout();
                var detail = (Border)page.FindName("DetailPanel");
                var header = (Grid)page.FindName("TableHeader");
                Assert.Equal(1, Grid.GetRow(detail));
                Assert.Equal(96, header.ColumnDefinitions[5].Width.Value);

                page.Measure(new Size(1440, 900));
                page.Arrange(new Rect(0, 0, 1440, 900));
                page.UpdateLayout();
                Assert.Equal(0, Grid.GetRow(detail));
                Assert.Equal(4, Grid.GetColumn(detail));
            }
            finally { UiTestHost.RestoreAppResources(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyWorkspace_WelcomeActionFitsNarrowWindowAndUsesTheme(bool dark)
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                UiTestHost.UsePalette(dark);
                using var model = new VmwarePageViewModel(null!, null!, null!, new EmptyStateTimerFactory());
                var page = new VmwarePage { DataContext = model };
                page.Measure(new Size(900, 640));
                page.Arrange(new Rect(0, 0, 900, 640));
                page.UpdateLayout();
                var action = Descendants(page).OfType<Button>().Single(b => b.Command == model.AddSourceCommand && b.Padding.Left == 18);
                var bounds = action.TransformToAncestor(page).TransformBounds(new Rect(action.RenderSize));
                Assert.True(action.ActualWidth > 0);
                Assert.True(bounds.Left >= 0 && bounds.Right <= 900 && bounds.Top >= 0 && bounds.Bottom <= 640);
                Assert.Equal(((SolidColorBrush)page.FindResource("Bg.Workspace")).Color, ((SolidColorBrush)page.Background).Color);
                Assert.False(model.ShowNoVms);
                Assert.False(model.ShowLoadError);
                Assert.False(model.ShowLoadingVms);
            }
            finally { UiTestHost.RestoreAppResources(); }
        });
    }

    private sealed class EmptyStateTimerFactory : RemoteFlow.Presentation.Host.IUiTimerFactory
    {
        public RemoteFlow.Presentation.Host.IUiTimer Create(TimeSpan interval) => new EmptyStateTimer();
    }

    private sealed class EmptyStateTimer : RemoteFlow.Presentation.Host.IUiTimer
    {
        public TimeSpan Interval { get; set; }
        public event EventHandler? Tick { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
