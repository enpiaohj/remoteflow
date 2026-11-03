using RemoteFlow.Core.Models;

namespace RemoteFlow.App.Views;

/// <summary>
/// 标题栏会话 Tab 的呈现模式判定。
/// <para>
/// 「自动折叠」模式下，普通 Tab 实测宽度放不下（会挤压会话工具条 / 窗口按钮）时，
/// 标题栏切换为紧凑模式：工作台 Tab 固定 + 「当前会话」下拉选择器；只要放得下，
/// 不论开了多少会话都逐个显示标签。「横向滚动」模式（类似 Excel 工作表）永远不折叠，
/// 放不下时由两端箭头与滚轮滚动。
/// 纯函数、无 WPF 依赖，可在测试工程直接验证。
/// </para>
/// </summary>
internal static class SessionTabLayoutPolicy
{
    /// <summary>宽度比较容差（DIU），只吸收亚像素布局抖动；整 1 DIU 的溢出即视为放不下。</summary>
    private const double WidthTolerance = 0.5;

    /// <summary>
    /// 是否应使用紧凑模式。
    /// <paramref name="requiredWidth"/> 为普通 Tab 的实测需求宽度（ScrollViewer.ExtentWidth），
    /// <paramref name="availableWidth"/> 为标题栏 Tab 条的可用宽度。
    /// </summary>
    internal static bool ShouldUseCompactMode(
        SessionTabLayout layout,
        double requiredWidth,
        double availableWidth)
        => layout == SessionTabLayout.AutoCollapse
           && requiredWidth > availableWidth + WidthTolerance;

    /// <summary>横向滚动模式下，是否需要显示两端的滚动箭头（内容比可视区宽）。</summary>
    internal static bool ShouldShowScrollArrows(
        SessionTabLayout layout,
        double extentWidth,
        double viewportWidth)
        => layout == SessionTabLayout.Scroll
           && extentWidth > viewportWidth + WidthTolerance;
}
