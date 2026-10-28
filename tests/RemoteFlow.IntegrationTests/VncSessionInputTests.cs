using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Protocol.Vnc;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>VNC 会话的新增输入能力在未连接时必须安全：不抛、不发送、不假装成功。</summary>
public sealed class VncSessionInputTests
{
    private static VncSession CreateSession()
        => new(new SessionRequest { Profile = new ConnectionProfile { Name = "t", Host = "h", Port = 5900, Protocol = ProtocolType.Vnc } },
            NullLoggerFactory.Instance);

    [Fact]
    public async Task 未连接时不能请求VMware分辨率()
    {
        await using var session = CreateSession();

        Assert.False(session.CanRequestVmwareResolution);
        Assert.False(await session.RequestVmwareResolutionAsync(1280, 720));
    }

    [Fact]
    public async Task 普通VNC连接永远不请求VMware分辨率()
    {
        // 即使凭据和连接器都齐全，没有启用 Vmware 的普通 VNC 连接也不能走 API 改分辨率。
        var profile = new ConnectionProfile { Name = "t", Host = "h", Port = 5900, Protocol = ProtocolType.Vnc };
        await using var session = new VncSession(new SessionRequest { Profile = profile }, NullLoggerFactory.Instance,
            new FakeVmwareConnector());

        Assert.False(session.CanRequestVmwareResolution);
        Assert.False(await session.RequestVmwareResolutionAsync(1280, 720));
    }

    [Fact]
    public async Task 未连接时发送CtrlAltDel不抛异常()
    {
        await using var session = CreateSession();

        var error = Record.Exception(session.SendCtrlAltDelete);

        Assert.Null(error);
    }

    [Fact]
    public async Task 只读连接不发送CtrlAltDel()
    {
        var profile = new ConnectionProfile { Name = "t", Host = "h", Port = 5900, Protocol = ProtocolType.Vnc };
        profile.Vnc.ViewOnly = true;
        await using var session = new VncSession(new SessionRequest { Profile = profile }, NullLoggerFactory.Instance);

        var error = Record.Exception(session.SendCtrlAltDelete);

        Assert.Null(error);
    }
}
