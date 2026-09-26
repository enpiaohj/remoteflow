using RemoteFlow.Core.Models;
using Xunit;

namespace RemoteFlow.Core.Tests;

/// <summary>全屏悬浮工具条延迟的档位映射与夹取约定。</summary>
public class SessionPillTimingTests
{
    [Fact]
    public void 按档位取对应的显示与消失延迟()
    {
        var settings = new AppSettings
        {
            PillRevealDelayWindowFullMs = 1000,
            PillRevealDelayScreenFullMs = 2000,
            PillHideDelayWindowFullMs = 500,
            PillHideDelayScreenFullMs = 1500
        };

        Assert.Equal(TimeSpan.FromSeconds(1), settings.GetRevealDelay(screenFull: false));
        Assert.Equal(TimeSpan.FromSeconds(2), settings.GetRevealDelay(screenFull: true));
        Assert.Equal(TimeSpan.FromMilliseconds(500), settings.GetHideDelay(screenFull: false));
        Assert.Equal(TimeSpan.FromSeconds(1.5), settings.GetHideDelay(screenFull: true));
    }

    [Fact]
    public void 零值表示立即显示与立即收起()
    {
        var settings = new AppSettings
        {
            PillRevealDelayWindowFullMs = 0,
            PillHideDelayScreenFullMs = 0
        };

        Assert.Equal(TimeSpan.Zero, settings.GetRevealDelay(screenFull: false));
        Assert.Equal(TimeSpan.Zero, settings.GetHideDelay(screenFull: true));
    }

    [Theory]
    [InlineData(-100, 0)]
    [InlineData(999_999, 10_000)]
    public void 越界值被夹取到合法区间(int configuredMs, int expectedMs)
    {
        var settings = new AppSettings
        {
            PillRevealDelayScreenFullMs = configuredMs,
            PillHideDelayWindowFullMs = configuredMs
        };

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), settings.GetRevealDelay(screenFull: true));
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), settings.GetHideDelay(screenFull: false));
    }
}
