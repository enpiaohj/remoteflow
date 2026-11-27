using System.Windows.Media;
using RemoteFlow.App.Services;
using RemoteFlow.App.Views.Pages;
using RemoteFlow.Protocol.PowerShell;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>界面走查（2026-11-27）各项修复中与 Windows 界面层相关的部分。</summary>
public sealed class UxAuditWindowsTests
{
    [Theory]
    [InlineData(1500, new[] { "Host", "Proto", "Recent", "Group", "Tags" })]
    [InlineData(968, new[] { "Host", "Proto", "Recent" })]
    [InlineData(760, new[] { "Host" })]
    [InlineData(390, new string[0])]
    public void 工作台表格按列表宽度收起次要列(double width, string[] expected)
        => Assert.Equal(expected, ConnectionsPage.VisibleAdaptiveColumns(width));

    [Fact]
    public void 图标按钮用提示文字作无障碍名称_文字按钮不改()
    {
        Assert.Equal("最小化", AccessibleNames.ResolveName(Geometry.Parse("M0,0 L1,1"), "最小化"));
        Assert.Equal("连接 (Enter)", AccessibleNames.ResolveName(Geometry.Parse("M0,0 L1,1"), "连接 (Enter)\n第二行"));
        Assert.Equal("上一页", AccessibleNames.ResolveName("‹", "上一页"));
        Assert.Null(AccessibleNames.ResolveName("批量操作", "勾选设备进行批量操作"));
        Assert.Null(AccessibleNames.ResolveName(Geometry.Parse("M0,0 L1,1"), null));
    }

    [Theory]
    [InlineData("192.0.2.10,host.example.test, 192.0.2.11 ", new[] { "192.0.2.10", "host.example.test", "192.0.2.11" })]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void 信任列表按逗号解析(string? raw, string[] expected)
        => Assert.Equal(expected, ElevatedTrustedHostsWriter.ParseList(raw));

    [Fact]
    public void 移出信任列表只删除完全相同的一项()
    {
        var script = ElevatedTrustedHostsWriter.BuildRemoveScript("192.0.2.10");
        Assert.Contains("$name = '192.0.2.10'", script, StringComparison.Ordinal);
        Assert.Contains("Where-Object { $_ -ne $name }", script, StringComparison.Ordinal);
        Assert.Contains("if ($started) { Stop-Service WinRM }", script, StringComparison.Ordinal);
        // 手工加的通配条目也能移出；夹带命令的输入一律拒绝。
        Assert.Contains("'*'", ElevatedTrustedHostsWriter.BuildRemoveScript("*"), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ElevatedTrustedHostsWriter.BuildRemoveScript("x'; calc; '"));
        Assert.Throws<ArgumentException>(() => ElevatedTrustedHostsWriter.BuildRemoveScript("a,b"));
    }
}
