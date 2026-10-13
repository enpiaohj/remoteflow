using RemoteFlow.App.Views;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class SessionTabLayoutPolicyTests
{
    [Theory]
    [InlineData(5, 900, 900, true)]
    [InlineData(6, 700, 900, true)]
    [InlineData(4, 901, 900, true)]
    [InlineData(4, 900, 900, false)]
    [InlineData(1, 300, 900, false)]
    public void CompactMode_DependsOnSessionCountOrAvailableWidth(
        int sessionCount,
        double requiredWidth,
        double availableWidth,
        bool expected)
    {
        var actual = SessionTabLayoutPolicy.ShouldUseCompactMode(
            sessionCount,
            requiredWidth,
            availableWidth);

        Assert.Equal(expected, actual);
    }
}
