using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RemoteFlow.App.Views.Dialogs;
using RemoteFlow.App.Views.Pages;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>对话框拖动范围、搜索框深色前景、行内图标按钮尺寸的回归。</summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class DialogAndListRegressionTests
{
    /// <summary>
    /// 编辑器的拖动在预览阶段判断按下位置：输入框 / 按钮 / 下拉 / 列表 / 滚动条原样放行，
    /// 普通文本、面板、边框等才拖动。（表单在 ScrollViewer 里，它会吃掉冒泡的按下事件。）
    /// </summary>
    [Fact]
    public void DialogDrag_TreatsControlsAsInteractiveButPlainContentAsDraggable()
    {
        UiTestHost.RunOnSta(() =>
        {
            Assert.True(DialogDrag.IsInteractive(new Button()));
            Assert.True(DialogDrag.IsInteractive(new RadioButton()));
            Assert.True(DialogDrag.IsInteractive(new TextBox()));
            Assert.True(DialogDrag.IsInteractive(new PasswordBox()));
            Assert.True(DialogDrag.IsInteractive(new ComboBox()));
            Assert.True(DialogDrag.IsInteractive(new ListBox()));
            Assert.True(DialogDrag.IsInteractive(new ScrollBar()));
            Assert.True(DialogDrag.IsInteractive(new Thumb()));

            // 可交互控件的内部元素（按钮里的文字）同样视为交互。
            var label = new TextBlock { Text = "保存" };
            _ = new Button { Content = label };
            Assert.True(DialogDrag.IsInteractive(label));

            Assert.False(DialogDrag.IsInteractive(new TextBlock()));
            Assert.False(DialogDrag.IsInteractive(new Border()));
            Assert.False(DialogDrag.IsInteractive(new Grid()));
            Assert.False(DialogDrag.IsInteractive(new ScrollViewer()));
        });
    }

    /// <summary>
    /// 搜索框没有套输入框样式，前景必须显式指定，否则深色主题下回落成系统黑字（深色背景上几乎看不见）。
    /// </summary>
    [Theory]
    [InlineData(typeof(CredentialsPage), "SearchBox")]
    [InlineData(typeof(ConnectionsPage), "PageFilterBox")]
    public void SearchBoxes_UseThemeForegroundInDarkMode(Type pageType, string boxName)
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                UiTestHost.UsePalette(dark: true);
                var page = (FrameworkElement)Activator.CreateInstance(pageType)!;
                var box = (TextBox)page.FindName(boxName);

                var expected = ((SolidColorBrush)System.Windows.Application.Current!.FindResource("Text.Primary")).Color;
                var actual = (box.Foreground as SolidColorBrush)?.Color;

                Assert.Equal(expected, actual);
                Assert.Equal(expected, (box.CaretBrush as SolidColorBrush)?.Color);
            }
            finally
            {
                try { UiTestHost.RestoreAppResources(); } catch (NullReferenceException) { }
            }
        });
    }

    /// <summary>
    /// Button.Icon 自带 MinWidth/MinHeight=36，会顶掉元素上较小的 Width/Height；
    /// 行里 26 宽的列放 36 宽的按钮，「更多」的三个点就被裁掉一截。
    /// 这里把行模板里两个小图标按钮的最小尺寸压到与列宽一致。
    /// </summary>
    [Fact]
    public void RowIconButtons_OverrideStyleMinimumSize()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                var template = (DataTemplate)new ConnectionsPage().Resources["ConnRowTemplate"];
                var row = (FrameworkElement)template.LoadContent();

                var buttons = AllDescendants(row).OfType<Button>().ToList();
                var more = buttons.Single(b => b.ToolTip as string == "更多操作");
                var star = buttons.Single(b => b.ToolTip as string == "收藏 / 取消收藏");

                Assert.Equal(26, more.MinWidth);
                Assert.Equal(26, more.MinHeight);
                Assert.Equal(26, more.Width);
                Assert.Equal(24, star.MinWidth);
                Assert.Equal(24, star.MinHeight);
            }
            finally
            {
                try { UiTestHost.RestoreAppResources(); } catch (NullReferenceException) { }
            }
        });
    }

    private static IEnumerable<DependencyObject> AllDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in AllDescendants(child))
            {
                yield return nested;
            }
        }
    }
}
