using System.IO;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// 收起态头部：会话数角标 + 当前会话关闭按钮。
    /// 这两件都必须常驻头部，而不是只在展开的下拉里——那正是用户反馈的缺口。
    /// </summary>
    [Fact]
    public void CompactSelectorHeader_ShowsSessionCountBadgeAndCloseButton()
    {
        var selector = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Sessions", "SessionTabSelector.xaml"));
        var selectorCode = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Sessions", "SessionTabSelector.xaml.cs"));

        // 头部元素存在。
        Assert.Contains("x:Name=\"HeaderCountBadge\"", selector, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HeaderCountText\"", selector, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HeaderClose\"", selector, StringComparison.Ordinal);

        // 角标排在图标右侧、不吃点击；不得叠在图标上——
        // 压在框形图标右上角会切断协议图标的框线（实测压掉 37%~55% 笔迹）。
        Assert.Contains("IsHitTestVisible=\"False\"", selector, StringComparison.Ordinal);
        var badgeIndex = selector.IndexOf("x:Name=\"HeaderCountBadge\"", StringComparison.Ordinal);
        var iconIndex = selector.IndexOf("x:Name=\"HeaderIcon\"", StringComparison.Ordinal);
        Assert.True(iconIndex > 0 && badgeIndex > iconIndex,
            "会话数角标应排在图标之后（右侧），不得叠在图标上。");
        Assert.DoesNotContain("Canvas.Right", selector, StringComparison.Ordinal);

        // 角标参与布局宽度，必须与标题同处一个有上限的 Grid：
        // 角标出现时标题所在星号列自动收缩，否则头部会顶到 240 上限、把箭头挤出可视区。
        Assert.Contains("<Grid MaxWidth=", selector, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"Auto\" />", selector, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"*\" />", selector, StringComparison.Ordinal);

        // 关闭按钮排在箭头之前：箭头恒定锚在最右，不随 × 出现而位移。
        var closeIndex = selector.IndexOf("x:Name=\"HeaderClose\"", StringComparison.Ordinal);
        var chevronIndex = selector.IndexOf("&#xE70D;", StringComparison.Ordinal);
        Assert.True(closeIndex > 0 && chevronIndex > closeIndex,
            "关闭按钮应排在下拉箭头之前，使箭头位置恒定。");

        // 计数口径：会话数不含文件传输 Tab；≥2 才显示，上限 99。
        Assert.Contains("CountBadgeMax", selectorCode, StringComparison.Ordinal);
        Assert.Contains(
            "_listedTabs.Count(static t => t is SessionTabViewModel)",
            selectorCode,
            StringComparison.Ordinal);

        // 头部整块是 ToggleButton，关闭必须拦截冒泡，否则会顺带打开下拉。
        Assert.Contains("OnHeaderCloseClick", selector, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true;", selectorCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// 文件传输 Tab 在折叠态必须有入口：它既要在下拉列表里可切换 / 可关闭，
    /// 选中它时头部也要正确显示它自己的标题与图标（而不是退回「活动连接」）。
    /// </summary>
    [Fact]
    public void CompactSelector_IncludesFileTransferTab()
    {
        var selector = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Sessions", "SessionTabSelector.xaml"));
        var selectorCode = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "Sessions", "SessionTabSelector.xaml.cs"));

        // 列表要同时容纳会话与文件传输两种行模板。
        Assert.Contains(
            "DataType=\"{x:Type vm:SessionTabViewModel}\"",
            selector,
            StringComparison.Ordinal);
        Assert.Contains(
            "DataType=\"{x:Type vm:FileTransferTabViewModel}\"",
            selector,
            StringComparison.Ordinal);

        // 选中文件传输 Tab 时头部显示它自己，而不是回退成工作台头部。
        Assert.Contains("ShowTransferHeader", selectorCode, StringComparison.Ordinal);
        Assert.Contains("_headerTransferTab", selectorCode, StringComparison.Ordinal);

        // 头部的 × 同时服务会话与文件传输两种 Tab，提示语必须跟着切换：
        // XAML 里的默认值是「关闭会话」，文件传输分支若不覆盖就会写错。
        Assert.Contains(
            "HeaderClose.ToolTip = \"关闭会话 (Ctrl+W)\";",
            selectorCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "HeaderClose.ToolTip = \"关闭文件传输 (Ctrl+W)\";",
            selectorCode,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 托盘「退出」同样要确认已连接会话：应用缩在托盘时后台可能挂着多个会话，
    /// 一键退出静默断开它们正是要避免的事故。
    /// 另一条不变式：取消退出必须清掉 _isExiting——否则托盘确认框点取消后，
    /// 再点窗口关闭按钮会被当成真退出，绕过「最小化到托盘」设置直接关掉应用。
    /// </summary>
    [Fact]
    public void TrayExit_ConfirmsSessionsAndResetsExitingFlagOnCancel()
    {
        var code = File.ReadAllText(FindProjectFile(
            "src", "RemoteFlow.App", "Views", "MainWindow.xaml.cs"));

        var closing = ExtractSection(
            code,
            "protected override void OnClosing",
            "private void CancelExit");

        // 托盘退出仍跳过「最小化到托盘」岔路（那是给关闭按钮的去路），
        // 但有会话时走「退出 / 取消」确认；窗口隐藏时不以它为对话框 owner。
        Assert.Contains(
            "if (!_isExiting && _settings.CloseBehavior == WindowCloseBehavior.MinimizeToTray)",
            closing,
            StringComparison.Ordinal);
        Assert.Contains("if (_isExiting)", closing, StringComparison.Ordinal);
        Assert.Contains("_viewModel.OpenSessionCount", closing, StringComparison.Ordinal);
        Assert.Contains("MessageDialog.ShowConfirm(", closing, StringComparison.Ordinal);
        Assert.Contains("IsVisible ? this : null", closing, StringComparison.Ordinal);

        // 关闭按钮路径保留三选一：退出 / 最小化到托盘 / 取消。
        Assert.Contains("最小化到托盘", closing, StringComparison.Ordinal);

        // 会话确认与文件传输确认的取消都走 CancelExit，由它复位 _isExiting。
        Assert.Equal(2, Regex.Matches(closing, @"CancelExit\(e\);").Count);

        var cancelExit = ExtractSection(
            code,
            "private void CancelExit",
            "private static class NativeMethods");
        Assert.Contains("_isExiting = false;", cancelExit, StringComparison.Ordinal);
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
