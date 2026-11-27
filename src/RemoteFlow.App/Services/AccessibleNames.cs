using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace RemoteFlow.App.Services;

/// <summary>
/// 给只有图标的按钮补上无障碍名称。图标按钮的内容是几何图形，读屏软件会把整串路径数据当名称读出来；
/// 这类按钮几乎都有提示文字，加载时若没有显式名称就用提示文字。带文字的按钮不受影响。
/// </summary>
public static class AccessibleNames
{
    /// <remarks>
    /// 挂在 SizeChanged 上：WPF 的 Loaded 不会触发按类型注册的处理程序（实测）；SizeChanged 在按钮第一次布局时一定触发。
    /// </remarks>
    public static void Register()
        => EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.SizeChangedEvent,
            new SizeChangedEventHandler((sender, _) => Apply(sender)));

    private static void Apply(object sender)
    {
        if (sender is ButtonBase button && string.IsNullOrEmpty(AutomationProperties.GetName(button)))
        {
            var name = ResolveName(button.Content, button.ToolTip);
            if (name is not null)
            {
                button.SetCurrentValue(AutomationProperties.NameProperty, name);
            }
        }
    }

    /// <summary>内容是图标（几何图形 / 图形元素）且提示是文字时返回提示文字，否则返回 null。</summary>
    internal static string? ResolveName(object? content, object? toolTip)
    {
        var isIcon = content is Geometry or DrawingImage or System.Windows.Shapes.Shape
                     || (content is string text && text.Length <= 2 && !char.IsLetterOrDigit(text, 0));
        return isIcon && toolTip is string tip && !string.IsNullOrWhiteSpace(tip) ? tip.Split('\n')[0].Trim() : null;
    }
}
