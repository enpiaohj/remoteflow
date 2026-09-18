using System.Net;
using RemoteFlow.Core.Models;
using Sockets = System.Net.Sockets;
using Xunit;
using RemoteFlow.Presentation.Services;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// ConnectionTestService 纯逻辑回归：只测服务，不弹对话框。
/// 刻意不加易碎的 Ping 成功断言（Ping 有 / 无仅作为报告字段存在）；
/// TCP 可达 / 拒绝用本机回环 TcpListener 控制，避免依赖外部网络。
/// </summary>
public sealed class TestConnectionServiceTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(1500);

    private static ConnectionProfile Profile(string host, int port, ProtocolType protocol = ProtocolType.Ssh)
        => new()
        {
            Host = host,
            Port = port,
            Protocol = protocol
        };

    private static ConnectionTestService CreateService() => new();

    /// <summary>起一个监听 127.0.0.1 的临时监听器，返回（Listener, Port）；测试结束在 finally 里释放。</summary>
    private static Sockets.TcpListener ListenOnLoopback()
    {
        var listener = new Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    /// <summary>起一个监听器取端口后立刻释放，得到一个确定无人监听的本地端口。</summary>
    private static int ClosedLocalPort()
    {
        var listener = ListenOnLoopback();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Host为IP_DnsWasIp且Text为使用IP地址()
    {
        using var listener = ListenOnLoopback();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var report = await CreateService().RunAsync(
            Profile("127.0.0.1", port),
            ShortTimeout,
            CancellationToken.None);

        Assert.True(report.DnsWasIp);
        Assert.Equal(StepStatus.Success, report.Dns.Status);
        Assert.Equal("使用 IP 地址", report.Dns.Text);
    }

    [Fact]
    public async Task 域名解析_走Resolve且Dns成功()
    {
        using var listener = ListenOnLoopback();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var report = await CreateService().RunAsync(
            Profile("localhost", port),
            ShortTimeout,
            CancellationToken.None);

        Assert.False(report.DnsWasIp);
        Assert.Equal(StepStatus.Success, report.Dns.Status);
        Assert.StartsWith("正常 · ", report.Dns.Text);
    }

    [Fact]
    public async Task TCP成功_本机监听_结论含可以建立SSH连接()
    {
        using var listener = ListenOnLoopback();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var report = await CreateService().RunAsync(
            Profile("127.0.0.1", port, ProtocolType.Ssh),
            ShortTimeout,
            CancellationToken.None);

        Assert.True(report.TcpReachable);
        Assert.Equal(StepStatus.Success, report.Tcp.Status);
        Assert.Contains("目标可以建立 SSH 连接", report.Conclusion);
        Assert.Empty(report.Causes);
    }

    [Fact]
    public async Task TCP拒绝_本地无监听端口_不可达且给通用原因()
    {
        var port = ClosedLocalPort();

        var report = await CreateService().RunAsync(
            Profile("127.0.0.1", port, ProtocolType.Ssh),
            ShortTimeout,
            CancellationToken.None);

        Assert.False(report.TcpReachable);
        Assert.Contains("当前无法建立 SSH 连接", report.Conclusion);
        Assert.Equal(StepStatus.Failed, report.Tcp.Status);
        Assert.Equal(4, report.Causes.Count);
        Assert.DoesNotContain(report.Causes, c => c.Contains("主机名", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 提前取消_抛OperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService().RunAsync(
                Profile("127.0.0.1", ClosedLocalPort()),
                ShortTimeout,
                cts.Token));
    }

    [Fact]
    public async Task DNS失败_不存在主机_Dns步骤Failed()
    {
        // .invalid 是 RFC 2606 保留域名，本地解析器应返回 NXDOMAIN；
        // 若个别环境解析超时慢，DNS 步骤会在超时后判 Failed，同样覆盖本断言。
        var report = await CreateService().RunAsync(
            Profile("nonexistent-host.invalid", 22, ProtocolType.Ssh),
            ShortTimeout,
            CancellationToken.None);

        Assert.Equal(StepStatus.Failed, report.Dns.Status);
        Assert.False(report.TcpReachable);
        Assert.Contains("当前无法建立 SSH 连接", report.Conclusion);
    }
}
