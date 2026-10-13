using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 会话三档视图模式的状态机回归测试。
/// <para>
/// 档位规则是本次「窗口最大化档」改动里唯一可自动化的业务逻辑：纯函数、无 DI、
/// 无 STA、无 HWND，因此能在跨平台测试工程里跑——macOS 侧同样引用
/// RemoteFlow.Presentation，本文件顺带成为共享层改动的双平台回归网。
/// </para>
/// </summary>
public sealed class SessionViewModeTests
{
    [Fact]
    public void Advance_FromNormal_EntersWindowFull()
        => Assert.Equal(SessionViewMode.WindowFull, SessionViewModeRules.Advance(SessionViewMode.Normal));

    [Fact]
    public void Advance_FromWindowFull_EntersScreenFull()
        => Assert.Equal(SessionViewMode.ScreenFull, SessionViewModeRules.Advance(SessionViewMode.WindowFull));

    [Fact]
    public void Advance_FromScreenFull_WrapsBackToNormal()
        => Assert.Equal(SessionViewMode.Normal, SessionViewModeRules.Advance(SessionViewMode.ScreenFull));

    [Fact]
    public void Advance_ThreeTimes_ReturnsToNormal()
    {
        var mode = SessionViewMode.Normal;

        mode = SessionViewModeRules.Advance(mode);
        mode = SessionViewModeRules.Advance(mode);
        mode = SessionViewModeRules.Advance(mode);

        Assert.Equal(SessionViewMode.Normal, mode);
    }

    [Fact]
    public void ExitFullScreen_FromScreenFull_GoesStraightToNormal()
        => Assert.Equal(SessionViewMode.Normal, SessionViewModeRules.ExitFullScreen(SessionViewMode.ScreenFull));

    [Fact]
    public void ExitFullScreen_FromWindowFull_GoesToNormal()
        => Assert.Equal(SessionViewMode.Normal, SessionViewModeRules.ExitFullScreen(SessionViewMode.WindowFull));

    [Fact]
    public void ExitFullScreen_FromNormal_StaysAtNormal()
        => Assert.Equal(SessionViewMode.Normal, SessionViewModeRules.ExitFullScreen(SessionViewMode.Normal));

    [Fact]
    public void ExitFullScreen_AlwaysReachesNormal_SoItNeverDuplicatesTheScreenFullToggle()
    {
        // 药丸上两颗窗口钮必须互不重复：完全全屏只退一档（回窗口最大化），
        // 退出全屏一路退到底（回常规）。完全全屏档下两者若都回窗口最大化就是 bug。
        Assert.Equal(
            SessionViewMode.WindowFull,
            SessionViewModeRules.ToggleScreenFull(SessionViewMode.ScreenFull));

        Assert.Equal(
            SessionViewMode.Normal,
            SessionViewModeRules.ExitFullScreen(SessionViewMode.ScreenFull));
    }

    [Fact]
    public void ToggleScreenFull_FromScreenFull_FallsBackToWindowFull()
        => Assert.Equal(SessionViewMode.WindowFull, SessionViewModeRules.ToggleScreenFull(SessionViewMode.ScreenFull));

    [Fact]
    public void ToggleScreenFull_FromWindowFull_EntersScreenFull()
        => Assert.Equal(SessionViewMode.ScreenFull, SessionViewModeRules.ToggleScreenFull(SessionViewMode.WindowFull));

    [Fact]
    public void ToggleScreenFull_FromNormal_JumpsStraightToScreenFull()
        => Assert.Equal(SessionViewMode.ScreenFull, SessionViewModeRules.ToggleScreenFull(SessionViewMode.Normal));

    [Theory]
    [InlineData(SessionViewMode.Normal, false)]
    [InlineData(SessionViewMode.WindowFull, true)]
    [InlineData(SessionViewMode.ScreenFull, true)]
    public void IsFullScreenLevel_IsTrueForEveryNonNormalMode(SessionViewMode mode, bool expected)
        => Assert.Equal(expected, mode.IsFullScreenLevel());

    [Theory]
    [InlineData(SessionViewMode.Normal, false)]
    [InlineData(SessionViewMode.WindowFull, false)]
    [InlineData(SessionViewMode.ScreenFull, true)]
    public void IsScreenFull_IsTrueOnlyForScreenFull(SessionViewMode mode, bool expected)
        => Assert.Equal(expected, mode.IsScreenFull());
}
