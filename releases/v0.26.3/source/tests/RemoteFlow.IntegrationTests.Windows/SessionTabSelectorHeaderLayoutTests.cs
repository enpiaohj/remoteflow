using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RemoteFlow.App.Views.Sessions;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 收起态头部的真实布局测量。
/// <para>
/// 头部宽度上限 240（ToggleButton MaxWidth），加入会话数角标与关闭按钮后余量很小；
/// 而 <c>Button.Icon</c> 样式自带 <c>MinWidth=36</c> —— 只写 Width=24 会被样式最小值覆盖。
/// 这类「样式最小值盖过局部设定」的问题静态断言看不出来，必须真实测量。
/// </para>
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class SessionTabSelectorHeaderLayoutTests
{
    /// <summary>头部内容区宽度：MaxWidth 240 − 模板 Padding 左右各 10 − 边框 1×2。</summary>
    private const double ContentWidth = 218;

    /// <summary>
    /// 最坏排布下的宽度上限：长标题 + 两位数角标 + × 同时出现。
    /// 头部超 240 会把最右的下拉箭头挤出可视区，因此必须真实测量。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("5")]
    [InlineData("12")]
    [InlineData("99+")]
    public void 头部不超出宽度上限(string? count)
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                var (selector, _, close, badge) = BuildSelector();

                // 让 × 与角标都出现，测量「最坏排布」而不是默认的收起态。
                close.Visibility = Visibility.Visible;
                badge.IsHitTestVisible = false;
                if (count is not null)
                {
                    ((TextBlock)selector.FindName("HeaderCountText")).Text = count;
                    badge.Visibility = Visibility.Visible;
                }

                // 长标题逼出宽度上限，才是真正的溢出场景。
                ((TextBlock)selector.FindName("HeaderTitle")).Text = "上海-生产-数据库-主节点-01";
                Relayout(selector);

                // 关闭按钮必须是 24，而不是 Button.Icon 的 MinWidth=36。
                Assert.Equal(24d, close.ActualWidth);
                Assert.Equal(24d, close.ActualHeight);

                // 用无限宽度测量读真实需求宽度（ActualWidth 会被外层拉伸填满）。
                selector.Measure(new Size(double.PositiveInfinity, 36));
                var desired = selector.DesiredSize.Width;
                Assert.True(
                    desired <= 240 + 0.5,
                    $"角标为「{count ?? "无"}」时头部需求宽度 {desired} 超出 240 上限");
            }
            finally
            {
                UiTestHost.RestoreAppResources();
            }
        });
    }

    /// <summary>
    /// 角标必须排在图标右侧、完全不重叠：压在框形图标右上角会切断协议图标的框线
    /// （实测压掉 37%~55% 笔迹），协议因此难以辨认。
    /// </summary>
    [Fact]
    public void 角标排在图标右侧且不重叠()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                var (selector, toggle, _, badge) = BuildSelector();
                badge.Visibility = Visibility.Visible;
                ((TextBlock)selector.FindName("HeaderCountText")).Text = "99+";
                Relayout(selector);

                var panel = FindHeaderPanel(toggle);
                var icon = (System.Windows.Shapes.Path)selector.FindName("HeaderIcon");

                Rect Bounds(FrameworkElement e) =>
                    e.TransformToAncestor(panel).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));

                var iconRect = Bounds(icon);
                var badgeRect = Bounds(badge);
                var overlap = Rect.Intersect(iconRect, badgeRect);

                Assert.True(overlap.IsEmpty, $"角标与图标重叠 {overlap.Width:F1}×{overlap.Height:F1}");
                Assert.True(
                    badgeRect.X >= iconRect.Right,
                    $"角标左缘 {badgeRect.X:F1} 应不早于图标右缘 {iconRect.Right:F1}");

                // 高度 16 是 10px 字号的排版下限，再矮会在非整数 DPI 缩放下裁切文字。
                Assert.Equal(16d, badge.ActualHeight);
            }
            finally
            {
                UiTestHost.RestoreAppResources();
            }
        });
    }

    [Fact]
    public void 关闭按钮排在箭头之前()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                var (selector, toggle, close, _) = BuildSelector();
                close.Visibility = Visibility.Visible;
                Relayout(selector);

                // 头部子元素顺序（含未命名的箭头 TextBlock）：
                // 图标盒 → 标题 → 状态点 → 关闭按钮 → 下拉箭头。
                // 箭头排在最后，因此它的位置不随 × 的出现与消失而改变。
                var panel = FindHeaderPanel(toggle);
                var children = panel.Children.OfType<FrameworkElement>().ToList();

                var closeIndex = children.IndexOf(close);
                Assert.True(closeIndex >= 0, "关闭按钮不在头部面板内");

                var last = children[^1];
                Assert.IsType<TextBlock>(last);
                Assert.True(
                    ReferenceEquals(last, children[^1]) && closeIndex < children.Count - 1,
                    "关闭按钮应排在下拉箭头之前，使箭头位置恒定。");
            }
            finally
            {
                UiTestHost.RestoreAppResources();
            }
        });
    }

    [Fact]
    public void 角标与关闭按钮默认隐藏且角标不吃点击()
    {
        UiTestHost.RunOnSta(() =>
        {
            UiTestHost.LoadAppResources();
            try
            {
                var (_, _, close, badge) = BuildSelector();

                // 没有会话时不显示角标；工作台头部没有可关闭项，× 也不显示。
                Assert.Equal(Visibility.Collapsed, badge.Visibility);
                Assert.Equal(Visibility.Collapsed, close.Visibility);

                // 角标叠在图标上，不能吃掉点击（否则点图标区域不会展开列表）。
                Assert.False(badge.IsHitTestVisible);
            }
            finally
            {
                UiTestHost.RestoreAppResources();
            }
        });
    }

    /// <summary>构造选择器并完成一次离屏布局。</summary>
    private static (SessionTabSelector Selector, ToggleButton Toggle, Button Close, Border Badge) BuildSelector()
    {
        var selector = new SessionTabSelector();
        Relayout(selector);

        return (
            selector,
            (ToggleButton)selector.FindName("SelectorToggle"),
            (Button)selector.FindName("HeaderClose"),
            (Border)selector.FindName("HeaderCountBadge"));
    }

    private static void Relayout(FrameworkElement element)
    {
        element.Measure(new Size(400, 36));
        element.Arrange(new Rect(0, 0, 400, 36));
        element.UpdateLayout();
    }

    /// <summary>头部内容的 StackPanel（ToggleButton 模板内的 ContentPresenter 之子）。</summary>
    private static StackPanel FindHeaderPanel(ToggleButton toggle)
    {
        var root = (Border)toggle.Template.FindName("Root", toggle)!;
        var presenter = (ContentPresenter)root.Child;
        return (StackPanel)presenter.Content;
    }
}
