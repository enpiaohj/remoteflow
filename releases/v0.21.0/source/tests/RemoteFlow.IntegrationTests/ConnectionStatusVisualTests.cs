using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

public sealed class ConnectionStatusVisualTests
{
    [Fact]
    public void 在线状态使用绿色状态点语义()
    {
        var item = CreateItem();
        item.SetProbeResult(online: true);

        Assert.Equal("在线", item.ConnectionStatusDisplay);
        Assert.Equal("Status.Success", item.ConnectionStatusBrushKey);
    }

    [Fact]
    public void 已连接状态优先于在线探测并使用蓝色语义()
    {
        var item = CreateItem();
        item.SetProbeResult(online: true);
        item.IsConnected = true;

        Assert.Equal("已连接", item.ConnectionStatusDisplay);
        Assert.Equal("Status.Info", item.ConnectionStatusBrushKey);
    }

    [Fact]
    public void 连接失败状态使用红色异常语义()
    {
        var item = CreateItem();
        item.IsFailed = true;

        Assert.Equal("异常", item.ConnectionStatusDisplay);
        Assert.Equal("Status.Danger", item.ConnectionStatusBrushKey);
    }

    [Fact]
    public void 离线状态使用中性灰语义()
    {
        var item = CreateItem();
        item.SetProbeResult(online: false);

        Assert.Equal("离线", item.ConnectionStatusDisplay);
        Assert.Equal("Status.Idle", item.ConnectionStatusBrushKey);
    }

    [Fact]
    public void 连接中使用警告色且优先于探测状态()
    {
        var item = CreateItem();
        item.SetProbeResult(online: true);
        item.IsConnecting = true;

        Assert.Equal("连接中", item.ConnectionStatusDisplay);
        Assert.Equal("Status.Warning", item.ConnectionStatusBrushKey);
    }

    [Theory]
    [InlineData(ProtocolType.Rdp, "ProtocolIcon.Rdp")]
    [InlineData(ProtocolType.Ssh, "ProtocolIcon.Ssh")]
    [InlineData(ProtocolType.Vnc, "ProtocolIcon.Vnc")]
    public void 支持的协议映射到独立矢量图标(ProtocolType protocol, string expectedKey)
    {
        var item = new ConnectionItemViewModel(new ConnectionProfile { Protocol = protocol });

        Assert.Equal(expectedKey, item.ProtocolIconKey);
    }

    private static ConnectionItemViewModel CreateItem() => new(new ConnectionProfile
    {
        Name = "状态测试机",
        Host = "status.example.test",
        Protocol = ProtocolType.Rdp
    });
}
