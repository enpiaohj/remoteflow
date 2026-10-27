using RemoteFlow.App.Services;
using RemoteFlow.Core.Models;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>托盘悬停提示：含版本号，有会话时汇总各状态并列出会话名，且不超过系统长度上限。</summary>
public sealed class TrayTooltipTests
{
    private const string Version = "v0.27.3";

    [Fact]
    public void 没有会话时显示版本号和操作提示()
    {
        var text = TrayTooltip.Build(Version, []);

        Assert.StartsWith($"RemoteFlow {Version}", text, StringComparison.Ordinal);
        Assert.Contains("当前没有活动会话", text, StringComparison.Ordinal);
        Assert.Contains("右键打开菜单", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 有会话时汇总各状态并列出会话名()
    {
        var text = TrayTooltip.Build(Version,
        [
            ("域控一", ConnectionState.Connected),
            ("域控二", ConnectionState.Connected),
            ("文件服务器", ConnectionState.Connecting),
            ("旧机器", ConnectionState.Failed),
        ]);

        var lines = text.Split('\n');
        Assert.Equal($"RemoteFlow {Version}", lines[0]);
        Assert.Equal("2 个已连接 · 1 个连接中 · 1 个异常", lines[1]);
        Assert.Equal("· 域控一", lines[2]);
        Assert.Equal("· 域控二", lines[3]);
        Assert.Equal("· 文件服务器", lines[4]);
        Assert.Equal("… 另有 1 个", lines[5]);
    }

    [Fact]
    public void 数量为零的状态不写_过渡态也给出总数()
    {
        var onlyConnected = TrayTooltip.Build(Version, [("甲", ConnectionState.Connected)]);
        Assert.Equal("1 个已连接", onlyConnected.Split('\n')[1]);

        var transitional = TrayTooltip.Build(Version, [("乙", ConnectionState.Disconnecting)]);
        Assert.Equal("1 个会话", transitional.Split('\n')[1]);
    }

    [Fact]
    public void 超长会话名被截断且整段不超过系统上限()
    {
        var longName = new string('长', 80);
        var text = TrayTooltip.Build(Version,
        [
            (longName, ConnectionState.Connected),
            (longName, ConnectionState.Connected),
            (longName, ConnectionState.Connected),
            (longName, ConnectionState.Connected),
        ]);

        Assert.True(text.Length <= TrayTooltip.MaxLength, $"实际长度 {text.Length}");
        Assert.DoesNotContain(longName, text, StringComparison.Ordinal);
    }

    [Fact]
    public void 会话名里的换行不会撑出多余行()
    {
        var text = TrayTooltip.Build(Version, [("甲\n乙", ConnectionState.Connected)]);

        Assert.Equal(3, text.Split('\n').Length);
        Assert.Contains("· 甲 乙", text, StringComparison.Ordinal);
    }
}
