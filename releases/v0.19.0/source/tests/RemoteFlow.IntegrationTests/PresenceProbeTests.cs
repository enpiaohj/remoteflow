using System.Net;
using System.Net.Sockets;
using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.Services;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 在线探测的纯逻辑：探测服务用本机 TCP 监听器验证在线 / 离线 / 取消三条路径，
/// 行 VM 验证状态列的展示映射。不碰数据库与真实远端。
/// </summary>
public sealed class PresenceProbeTests
{
    private static async Task<(TcpListener Listener, int Port)> StartListenerAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        // 接受连接的任务挂着即可，探测端 ConnectAsync 一握手就成功返回。
        _ = listener.AcceptTcpClientAsync();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    [Fact]
    public async Task 探测监听中的端口返回在线()
    {
        var (listener, port) = await StartListenerAsync();
        try
        {
            var probe = new PresenceProbeService();
            var state = await probe.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(3), CancellationToken.None);
            Assert.Equal(PresenceState.Online, state);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 探测无监听的端口在超时后返回离线()
    {
        // 占一个端口再立即释放：拿到「大概率没有监听者」的端口号。
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var probe = new PresenceProbeService();
        var state = await probe.ProbeAsync("127.0.0.1", port, TimeSpan.FromMilliseconds(800), CancellationToken.None);
        Assert.Equal(PresenceState.Offline, state);
    }

    [Fact]
    public async Task 批量取消时探测抛出取消而不误报离线()
    {
        using var cts = new CancellationTokenSource(50);
        var probe = new PresenceProbeService();

        // 一个不可能快速的地址：取消令牌先于探测完成触发。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.ProbeAsync("10.255.255.1", 5900, TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public void 行状态列的展示映射符合语义()
    {
        var item = new ConnectionItemViewModel(new ConnectionProfile
        {
            Name = "demo",
            Protocol = RemoteFlow.Core.Models.ProtocolType.Ssh,
            Host = "demo.example.com"
        });

        // 未探测：中性占位
        Assert.Equal("—", item.PresenceDisplay);
        Assert.Equal("Text.Tertiary", item.PresenceBrushKey);

        // 探测在线：绿
        item.SetProbeResult(true);
        Assert.Equal("在线", item.PresenceDisplay);
        Assert.Equal("Status.Success", item.PresenceBrushKey);
        Assert.Contains("探测于", item.PresenceTooltip);

        // 探测离线：灰（「现在不通」不是错误，不用告警色）
        item.SetProbeResult(false);
        Assert.Equal("离线", item.PresenceDisplay);
        Assert.Equal("Text.Tertiary", item.PresenceBrushKey);

        // 有活动会话：无论探测结果一律「已连接」+ 绿
        item.IsConnected = true;
        Assert.Equal("已连接", item.PresenceDisplay);
        Assert.Equal("Status.Success", item.PresenceBrushKey);

        // 清空探测态回到未探测
        item.IsConnected = false;
        item.ClearProbe();
        Assert.Equal(PresenceState.Unknown, item.Presence);
        Assert.Equal("—", item.PresenceDisplay);
    }
}
