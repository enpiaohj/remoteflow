using System.Globalization;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Presentation.Services;

/// <summary>
/// 全项目统一的日期 / 时间显示格式化。
/// <para>
/// 各页面不要再各自拼 ToString——首页完整日期、列表与历史的紧凑时间、创建等
/// 绝对时间、周数与星期，统一走这里。当前格式由 <see cref="Configure"/> 注入
/// （应用启动读取设置后调用；保存设置后再次调用刷新）。
/// </para>
/// </summary>
public static class DateTimeDisplay
{
    private static volatile AppSettings _settings = Defaults();

    private static AppSettings Defaults() => new()
    {
        DateFormat = AppDateFormat.Chinese,
        TimeFormat = AppTimeFormat.Hour24
    };

    /// <summary>用最新设置刷新显示规则。启动与设置保存后各调用一次。</summary>
    public static void Configure(AppSettings settings)
        => _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    private static AppSettings S => _settings;

    private static readonly string[] WeekdayNames =
        ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    /// <summary>日期部分：跟随配置的日期格式。</summary>
    public static string Date(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var pattern = S.DateFormat switch
        {
            AppDateFormat.Dash => "yyyy-MM-dd",
            AppDateFormat.Slash => "yyyy/MM/dd",
            AppDateFormat.Chinese => "yyyy年M月d日",
            _ => CurrentCultureShortDate()
        };

        return local.ToString(pattern, CultureInfo.InvariantCulture);
    }

    private static string CurrentCultureShortDate()
    {
        var p = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
        return p;
    }

    /// <summary>时间部分：跟随配置的 24/12 小时。</summary>
    public static string Time(DateTimeOffset value)
        => value.ToLocalTime().ToString(
            S.TimeFormat == AppTimeFormat.Hour24 ? "HH:mm" : "h:mm tt",
            CultureInfo.InvariantCulture);

    /// <summary>时钟字符串（含/不含秒），跟随 12/24 小时设置。例：14:05 或 2:05 PM / 14:05:09。</summary>
    public static string Clock(DateTimeOffset value, bool withSeconds)
    {
        var local = value.ToLocalTime();
        var pattern = S.TimeFormat == AppTimeFormat.Hour24
            ? (withSeconds ? "HH:mm:ss" : "HH:mm")
            : (withSeconds ? "h:mm:ss tt" : "h:mm tt");
        return local.ToString(pattern, CultureInfo.InvariantCulture);
    }

    /// <summary>紧凑的“日期 时间”，用于迷你图悬停等短提示，跟随用户日期与 12/24 小时设置。</summary>
    public static string Compact(DateTimeOffset value)
        => $"{Date(value)} {Time(value)}";

    /// <summary>星期短名（周一～周日）。</summary>
    public static string Weekday(DateTimeOffset value)
        => WeekdayNames[(int)value.ToLocalTime().DayOfWeek];

    /// <summary>ISO-8601 周数（周一为每周第一天）。</summary>
    public static int IsoWeek(DateTimeOffset value)
        => ISOWeek.GetWeekOfYear(value.ToLocalTime().Date);

    /// <summary>
    /// 首页标题下方的日期行，详略由 <see cref="HomeDateLine"/> 决定。
    /// 例（Full）：<c>2026年9月8日 周一 · 第37周 · 22:30</c>。
    /// 星期紧跟日期（空格分隔），周数与时间各占一段（<c> · </c> 分隔）。
    /// </summary>
    public static string HomeDateLineText(DateTimeOffset value, HomeDateLine style)
    {
        var head = Date(value);
        if (style is HomeDateLine.DateWeekdayTime or HomeDateLine.Full)
        {
            head += " " + Weekday(value);
        }

        var parts = new List<string> { head };
        if (style == HomeDateLine.Full)
        {
            parts.Add($"第{IsoWeek(value)}周");
        }

        if (style != HomeDateLine.DateOnly)
        {
            parts.Add(Clock(value, withSeconds: false));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>创建时间等“档案/绝对时间”：固定 <c>yyyy-MM-dd HH:mm</c>。</summary>
    public static string Absolute(DateTimeOffset value)
        => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// 紧凑历史时间：今天 16:34 / 昨天 23:27 / 当年 09-03 14:20 / 跨年 2025-12-31 23:55。
    /// </summary>
    public static string HistoryTimestamp(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var now = DateTime.Now;

        if (local.Date == now.Date)
        {
            return $"今天 {Time(local)}";
        }

        if (local.Date == now.Date.AddDays(-1))
        {
            return $"昨天 {Time(local)}";
        }

        return local.Year == now.Year
            ? local.ToString($"MM-dd {TimeToken()}", CultureInfo.InvariantCulture)
            : local.ToString($"yyyy-MM-dd {TimeToken()}", CultureInfo.InvariantCulture);
    }

    private static string TimeToken()
        => S.TimeFormat == AppTimeFormat.Hour24 ? "HH:mm" : "h:mm tt";

    /// <summary>
    /// “最近一次”类相对时间：刚刚 / N 分钟前；超过一小时落入历史紧凑时间。
    /// </summary>
    public static string RelativeRecent(DateTimeOffset value)
    {
        var elapsed = DateTimeOffset.Now - value;
        if (elapsed < TimeSpan.FromSeconds(60))
        {
            return "刚刚";
        }

        if (elapsed < TimeSpan.FromMinutes(60))
        {
            return $"{(int)elapsed.TotalMinutes} 分钟前";
        }

        return HistoryTimestamp(value);
    }
}
