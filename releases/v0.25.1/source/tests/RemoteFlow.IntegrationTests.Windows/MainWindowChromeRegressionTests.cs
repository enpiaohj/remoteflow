using System.IO;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class MainWindowChromeRegressionTests
{
    [Fact]
    public void TabStrip_HidesNativeScrollbarAndSwitchesToCompactSelector()
    {
        var markup = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "MainWindow.xaml"));
        var code = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "MainWindow.xaml.cs"));

        // 原生横向滚动条保持隐藏；箭头方案已由紧凑选择器取代。
        Assert.Contains(
            "HorizontalScrollBarVisibility=\"Hidden\"",
            markup,
            StringComparison.Ordinal);
        Assert.DoesNotContain("TabScrollLeftButton", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("TabScrollRightButton", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("ScrollTabStrip", code, StringComparison.Ordinal);

        // 双呈现容器与紧凑判定。
        Assert.Contains("x:Name=\"NormalTabStrip\"", markup, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompactTabStrip\"", markup, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SessionSelector\"", markup, StringComparison.Ordinal);
        Assert.Contains("EvaluateSessionTabLayout", code, StringComparison.Ordinal);
        Assert.Contains(
            "SessionTabLayoutPolicy.ShouldUseCompactMode(",
            code,
            StringComparison.Ordinal);

        // 紧凑态下 Normal 保持 Hidden 测量；普通态恢复时才滚动到选中项。
        Assert.Contains(
            "NormalTabStrip.Visibility = compact ? Visibility.Hidden : Visibility.Visible;",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (_isCompactTabMode || _viewModel.SelectedTab is not { } selected)",
            code,
            StringComparison.Ordinal);
        Assert.Contains("container.BringIntoView();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TabStrip_SharesSessionContextMenuAcrossNormalAndCompactModes()
    {
        var markup = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "MainWindow.xaml"));
        var selector = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Sessions", "SessionTabSelector.xaml"));
        var selectorCode = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Sessions", "SessionTabSelector.xaml.cs"));

        // 共享右键菜单（x:Shared=False）：普通 Tab 与紧凑行各自实例化。
        Assert.Contains(
            "x:Key=\"SessionTabContextMenu\"",
            markup,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Shared=\"False\"",
            markup,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContextMenu=\"{DynamicResource SessionTabContextMenu}\"",
            markup,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContextMenu\" Value=\"{DynamicResource SessionTabContextMenu}\"",
            selector,
            StringComparison.Ordinal);

        // 选择器必须可命中标题栏（WindowChrome 命中区），并且键盘导航抑制立即切换。
        Assert.Contains(
            "shell:WindowChrome.IsHitTestVisibleInChrome=\"True\"",
            selector,
            StringComparison.Ordinal);
        Assert.Contains("_keyboardNavigating", selectorCode, StringComparison.Ordinal);
        Assert.Contains("case Key.Delete:", selectorCode, StringComparison.Ordinal);
        Assert.Contains("case Key.Escape:", selectorCode, StringComparison.Ordinal);
    }

    [Fact]
    public void QualityFlyout_IsCreatedWithoutANativeOwner()
    {
        var code = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Sessions", "SessionHostView.xaml.cs"));

        Assert.DoesNotContain("Owner = window,", code, StringComparison.Ordinal);
        Assert.DoesNotContain("var window = flyout.Owner;", code, StringComparison.Ordinal);
        Assert.Contains(
            "var window = _window ?? Window.GetWindow(this);",
            code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SessionOverlay_IsActiveOnlyInScreenFullMode()
    {
        var code = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Sessions", "SessionHostView.xaml.cs"));

        Assert.DoesNotContain(
            "ApplyScreenFullMode(_main?.IsSessionFullScreen == true)",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyScreenFullMode(_main?.IsScreenFull == true)",
            code,
            StringComparison.Ordinal);
        Assert.Contains("ApplyScreenFullMode(main.IsScreenFull);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionOverlay_ClosesQualityFlyoutWhenScreenFullEnds()
    {
        var code = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Sessions", "SessionHostView.xaml.cs"));
        var overlayMethod = ExtractSection(
            code,
            "private void ApplyScreenFullMode",
            "private bool MaybeShowFirstRunHint");

        Assert.Contains("CloseQualityFlyout();", overlayMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void QualityFlyout_FollowsTheHostAndUsesOneCleanupPath()
    {
        var code = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Sessions", "SessionHostView.xaml.cs"));
        var repositionMethod = ExtractSection(
            code,
            "private void RepositionOverlays",
            "private void RepositionPopup");
        var closeMethod = ExtractSection(
            code,
            "private void CloseQualityFlyout",
            "private void OnQualityFlyoutClosed");

        Assert.Contains("RepositionPopup();", repositionMethod, StringComparison.Ordinal);
        Assert.Contains("PositionQualityFlyout();", repositionMethod, StringComparison.Ordinal);
        Assert.Contains("flyout.Close();", closeMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("Closed -= OnQualityFlyoutClosed", closeMethod, StringComparison.Ordinal);
        Assert.Contains("_tab?.Quality.CancelRunningProbe();", code, StringComparison.Ordinal);
    }

    private static string ExtractSection(string code, string startMarker, string endMarker)
    {
        var start = code.IndexOf(startMarker, StringComparison.Ordinal);
        var end = code.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"未找到代码段：{startMarker}");
        return code[start..end];
    }

    private static string FindProjectFile(params string[] segments)
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }.Distinct())
        {
            for (DirectoryInfo? directory = new(root);
                 directory is not null;
                 directory = directory.Parent)
            {
                var candidate = Path.Combine([directory.FullName, .. segments]);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"未找到 {Path.Combine(segments)}。");
    }
}
