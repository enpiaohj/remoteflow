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
    public async Task 未连接时不能调整桌面尺寸()
    {
        await using var session = CreateSession();

        Assert.False(session.CanResizeDesktop);
        Assert.False(session.RequestDesktopSize(1280, 720));
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
