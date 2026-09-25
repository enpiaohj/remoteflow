using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RemoteFlow.Presentation.ViewModels;

namespace RemoteFlow.App.Converters;

/// <summary>true → Visible，false → Collapsed。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>true → Collapsed，false → Visible。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

/// <summary>取反布尔值。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>非 null → Visible。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>集合数量大于 0 → Visible。</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 把资源键字符串解析为画刷。
/// <para>
/// ViewModel 只持有语义键（如 "Status.Success"），不直接引用任何颜色值，
/// 这样切换深浅主题时状态色会自动跟随。
/// </para>
/// </summary>
public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && System.Windows.Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return System.Windows.Application.Current?.TryFindResource("Text.Secondary") as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把 #RRGGBB 颜色字符串转为画刷，用于标签色。</summary>
public sealed class ColorStringToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text && !string.IsNullOrWhiteSpace(text))
        {
            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
            }
            catch (FormatException)
            {
                // 颜色值损坏时退回品牌色，不让界面因为一个标签而崩溃。
            }
        }

        return System.Windows.Application.Current?.TryFindResource("Brand.Default") as Brush ?? Brushes.SteelBlue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 枚举值与 <c>ConverterParameter</c> 相等时返回 true。
/// 用于把 RadioButton 组绑定到单个枚举属性。
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null && value.ToString() == parameter.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 只有选中时才回写，取消选中由另一个选项的选中事件负责。
        if (value is true && parameter is not null && targetType.IsEnum)
        {
            return Enum.Parse(targetType, parameter.ToString()!);
        }

        return Binding.DoNothing;
    }
}

/// <summary>
/// 枚举值与 <c>ConverterParameter</c> 相等时返回 Visible，否则 Collapsed。
/// 用于按当前选中的标签页切换内容面板。
/// </summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null && value.ToString() == parameter.ToString()
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>值是分组节点（<c>ConnectionGroupNodeViewModel</c>）时返回 true——混合行列表按类型分流。</summary>
public sealed class IsGroupNodeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ConnectionGroupNodeViewModel;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>分组嵌套层级 → 左缩进（每级 20px）。</summary>
public sealed class DepthToMarginConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new Thickness(value is int depth ? depth * 20 : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把 0~1 的比值乘以 <c>ConverterParameter</c>（像素高度），用于迷你柱状图。</summary>
public sealed class RatioToHeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ratio = value is double d ? d : 0;
        var max = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : 40;
        return Math.Max(2, ratio * max);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把枚举转换为中文显示名。</summary>
public sealed class EnumDisplayNameConverter : IValueConverter
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["System"] = "跟随系统",
        ["Light"] = "浅色",
        ["Dark"] = "深色",
        ["Rdp"] = "RDP",
        ["Ssh"] = "SSH",
        ["Vnc"] = "VNC",
        ["Home"] = "首页",
        ["Connections"] = "连接工作台",
        ["Favorites"] = "收藏",
        ["Recent"] = "最近连接",
        ["Credentials"] = "凭据",
        ["zh-CN"] = "中文（简体）",
        ["WindowsDomain"] = "Windows 域账号",
        ["LocalPassword"] = "本地账号",
        ["SshPassword"] = "SSH 口令",
        ["SshPrivateKey"] = "SSH 私钥",
        ["VncPassword"] = "VNC 口令"
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString() ?? string.Empty;
        return Names.GetValueOrDefault(key, key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 窗口最大化时补偿 WindowChrome 造成的边缘溢出。
/// <para>
/// 这是自定义标题栏的经典问题：最大化后窗口会超出工作区约 8px，
/// 导致内容被屏幕边缘裁掉。此处按当前 DPI 换算出补偿边距。
/// </para>
/// </summary>
public sealed class MaximizedPaddingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is WindowState.Maximized ? new Thickness(8) : new Thickness(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 全屏会话时把应用标题栏那一行的高度收为 0（否则即使 Visibility=Collapsed，
/// 行定义的固定高度仍会留白）。false → 48，true → 0。
/// </summary>
public sealed class FullScreenRowHeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new GridLength(0) : new GridLength(48);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 全屏会话时把某个 Grid 行/列的尺寸收为 0；否则取 ConverterParameter 指定的常态尺寸
/// （数值，缺省 216）。只让内容 Collapsed 不够——行/列定义的固定尺寸仍会留白，
/// 这正是「全屏后左边一条空带」的成因。
/// </summary>
public sealed class FullScreenSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
        {
            return new GridLength(0);
        }

        var normal = 216d;
        if (parameter is string text && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            normal = parsed;
        }

        return new GridLength(normal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
