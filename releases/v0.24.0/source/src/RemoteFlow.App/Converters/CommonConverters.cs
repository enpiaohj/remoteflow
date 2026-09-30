using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RemoteFlow.Core.Models;
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

/// <summary>
/// 把语义图标资源键解析为 WPF 矢量 <see cref="ImageSource"/>。
/// 坏键或空值回退到 <c>ConverterParameter</c> 指定的资源；未指定时使用 Windows 电脑图标。
/// </summary>
public sealed class ResourceKeyToImageSourceConverter : IValueConverter
{
    private const string DefaultFallbackKey = "DeviceIcon.WindowsPc";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key
            && System.Windows.Application.Current?.TryFindResource(key) is ImageSource source)
        {
            return source;
        }

        var fallbackKey = parameter as string ?? DefaultFallbackKey;
        return System.Windows.Application.Current?.TryFindResource(fallbackKey) as ImageSource;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把组织图标语义键解析为 WPF <see cref="Geometry"/>；坏键回退到参数指定资源。</summary>
public sealed class ResourceKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key
            && System.Windows.Application.Current?.TryFindResource(key) is Geometry geometry)
        {
            return geometry;
        }

        var fallbackKey = parameter as string ?? GroupIconCatalog.DefaultCustomKey;
        return System.Windows.Application.Current?.TryFindResource(fallbackKey) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 字号 → 线性图标边长。图标沿用原字体图标「按字号定尺寸」的用法：Fluent 20 网格的图形约占画布 85%，
/// 边长取字号 × 1.15（参数可覆盖倍率），视觉大小与原字形基本一致；取整到 0.5 像素避免模糊。
/// </summary>
public sealed class IconSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fontSize = value is double d && d > 0 ? d : 13d;
        var factor = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
            ? f
            : 1.15;
        return Math.Round(fontSize * factor * 2, MidpointRounding.AwayFromZero) / 2;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>后台代码取用 Ui.* 线性图标的统一入口（会话菜单、对话框级别图标等无法写在 XAML 的场景）。</summary>
public static class UiIconResources
{
    public static string ProtocolKey(ProtocolType protocol) => protocol switch
    {
        ProtocolType.Rdp => "Ui.ProtocolRdp",
        ProtocolType.Ssh => "Ui.ProtocolSsh",
        _ => "Ui.ProtocolVnc"
    };

    public static Geometry? Find(string key)
        => System.Windows.Application.Current?.TryFindResource(key) as Geometry;
}

/// <summary>
/// Ui.* 图标键 → Geometry；参数为变体后缀（如 ".Filled"），变体不存在时回落到基础键。
/// 用于「线性常态、选中填充」的导航图标，调用方只需给出基础键。
/// </summary>
public sealed class UiIconGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0)
        {
            return null;
        }

        var app = System.Windows.Application.Current;
        if (parameter is string suffix
            && suffix.Length > 0
            && app?.TryFindResource(key + suffix) is Geometry variant)
        {
            return variant;
        }

        return app?.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>远端文件条目 → 系统资源管理器同款文件 / 文件夹图标（按扩展名取，查询失败为 null）。</summary>
public sealed class RemoteFileShellIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RemoteFileItemViewModel item
            ? RemoteFlow.App.Services.ShellIconProvider.Get(item.Name, item.IsDirectory)
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>设备类型枚举 → 正式矢量图标，供编辑器下拉预览；Unknown 不显示图标。</summary>
public sealed class DeviceTypeToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DeviceType type || type == DeviceType.Unknown)
        {
            return null;
        }

        var key = DeviceTypeCatalog.Resolve(type)?.IconResourceKey;
        return key is not null
            ? System.Windows.Application.Current?.TryFindResource(key) as ImageSource
            : null;
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
