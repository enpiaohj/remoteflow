using RemoteFlow.App.Views;
using RemoteFlow.Core.Models;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class SessionTabLayoutPolicyTests
{
    [Theory]
    [InlineData(901, 900, true)]
    [InlineData(900, 900, false)]
    [InlineData(900.4, 900, false)]
    [InlineData(300, 900, false)]
    public void AutoCollapse_OnlyDependsOnAvailableWidth(
        double requiredWidth,
        double availableWidth,
        bool expected)
    {
        var actual = SessionTabLayoutPolicy.ShouldUseCompactMode(
            SessionTabLayout.AutoCollapse,
            requiredWidth,
            availableWidth);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AutoCollapse_DoesNotCollapseJustBecauseOfSessionCount()
    {
        // 此前会话数 ≥ 5 就强制折叠；现在只要标题栏放得下，开再多会话也逐个显示。
        Assert.False(SessionTabLayoutPolicy.ShouldUseCompactMode(SessionTabLayout.AutoCollapse, 700, 900));
    }

    [Theory]
    [InlineData(5000, 400)]
    [InlineData(901, 900)]
    public void ScrollMode_NeverCollapses(double requiredWidth, double availableWidth)
    {
        Assert.False(SessionTabLayoutPolicy.ShouldUseCompactMode(
            SessionTabLayout.Scroll, requiredWidth, availableWidth));
    }

    [Theory]
    [InlineData(SessionTabLayout.Scroll, 1200, 900, true)]
    [InlineData(SessionTabLayout.Scroll, 900, 900, false)]
    [InlineData(SessionTabLayout.Scroll, 900.3, 900, false)]
    [InlineData(SessionTabLayout.AutoCollapse, 1200, 900, false)]
    public void ScrollArrows_OnlyShownInScrollModeWhenContentOverflows(
        SessionTabLayout layout,
        double extentWidth,
        double viewportWidth,
        bool expected)
    {
        Assert.Equal(expected, SessionTabLayoutPolicy.ShouldShowScrollArrows(layout, extentWidth, viewportWidth));
    }
}
