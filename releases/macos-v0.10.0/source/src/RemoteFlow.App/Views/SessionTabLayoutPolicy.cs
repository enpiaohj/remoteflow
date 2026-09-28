namespace RemoteFlow.App.Views;

/// <summary>
/// 标题栏会话 Tab 的呈现模式判定。
/// <para>
/// 会话达到阈值，或普通 Tab 实测宽度放不下（会挤压会话工具条 / 窗口按钮）时，
/// 标题栏切换为紧凑模式：工作台 Tab 固定 + 「当前会话」下拉选择器。
/// 纯函数、无 WPF 依赖，可在测试工程直接验证。
/// </para>
/// </summary>
internal static class SessionTabLayoutPolicy
{
    /// <summary>会话数达到该值时无条件进入紧凑模式。</summary>
    internal const int CompactSessionThreshold = 5;

    /// <summary>宽度比较容差（DIU），只吸收亚像素布局抖动；整 1 DIU 的溢出即视为放不下。</summary>
    private const double WidthTolerance = 0.5;

    /// <summary>
    /// 是否应使用紧凑模式。
    /// <paramref name="requiredWidth"/> 为普通 Tab 的实测需求宽度（ScrollViewer.ExtentWidth），
    /// <paramref name="availableWidth"/> 为标题栏 Tab 条的可用宽度。
    /// </summary>
    internal static bool ShouldUseCompactMode(
        int sessionCount,
        double requiredWidth,
        double availableWidth)
        => sessionCount >= CompactSessionThreshold
           || requiredWidth > availableWidth + WidthTolerance;
}
