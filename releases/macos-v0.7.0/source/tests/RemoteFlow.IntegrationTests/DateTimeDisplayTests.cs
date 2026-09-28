using RemoteFlow.Core.Models;
using Xunit;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 日期时间统一格式化的纯逻辑测试。DateTimeDisplay 是静态全局单例，
/// 每个用例结束前恢复默认设置，避免污染其它并行用例。
/// </summary>
public sealed class DateTimeDisplayTests
{
    private static readonly DateTimeOffset Fixed = new(new DateTime(2026, 9, 5, 9, 5, 0));

    [Fact]
    public void Compact_跟随横线与24小时()
    {
        DateTimeDisplay.Configure(new AppSettings
        {
            DateFormat = AppDateFormat.Dash,
            TimeFormat = AppTimeFormat.Hour24
        });
        try
        {
            Assert.Equal("2026-09-05 09:05", DateTimeDisplay.Compact(Fixed));
        }
        finally
        {
            DateTimeDisplay.Configure(new AppSettings());
        }
    }

    [Fact]
    public void Compact_跟随中文日期与12小时()
    {
        DateTimeDisplay.Configure(new AppSettings
        {
            DateFormat = AppDateFormat.Chinese,
            TimeFormat = AppTimeFormat.Hour12
        });
        try
        {
            Assert.Equal("2026年9月5日 9:05 AM", DateTimeDisplay.Compact(Fixed));
        }
        finally
        {
            DateTimeDisplay.Configure(new AppSettings());
        }
    }
}
