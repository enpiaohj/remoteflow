using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Presentation.Host;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// RDP 工具条「文件传输」入口：RDP 没有会话内侧栏，入口在新标签页打开该主机的文件传输
/// （SMB 管理共享 / SFTP，与连接列表「文件传输…」同一入口）。
/// </summary>
public sealed class SessionFileTransferEntryTests
{
    private static SessionTabViewModel NewTab(ProtocolType protocol, Action<ConnectionProfile>? open)
        => new(
            new StubSession(new ConnectionProfile { Name = "主机", Host = "h", Protocol = protocol }),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            SynchronousUiDispatcher.Instance,
            new ManualTimerFactory(),
            openFileTransferTab: open);

    [Fact]
    public void RDP会话显示入口且点击打开该主机的文件传输标签页()
    {
        ConnectionProfile? opened = null;
        var tab = NewTab(ProtocolType.Rdp, p => opened = p);

        Assert.True(tab.CanOpenFileTransferTab);

        tab.OpenFileTransferTabCommand.Execute(null);

        Assert.Same(tab.Profile, opened);
    }

    [Fact]
    public void RDP默认走SMB并在提示里说明是新标签页()
    {
        var tab = NewTab(ProtocolType.Rdp, _ => { });

        Assert.Contains("SMB", tab.FileTransferTabToolTip);
        Assert.Contains("新标签页", tab.FileTransferTabToolTip);
    }

    [Fact]
    public void RDP改用SFTP时提示跟着变()
    {
        var tab = NewTab(ProtocolType.Rdp, _ => { });
        tab.Profile.FileTransfer.Channel = FileTransferChannel.Sftp;

        Assert.Contains("SFTP", tab.FileTransferTabToolTip);
    }

    [Fact]
    public void VNC没有文件传输通道不显示入口且命令不执行()
    {
        var opened = 0;
        var tab = NewTab(ProtocolType.Vnc, _ => opened++);

        Assert.False(tab.CanOpenFileTransferTab);

        tab.OpenFileTransferTabCommand.Execute(null);

        Assert.Equal(0, opened);
    }

    [Fact]
    public void 没有启动器的平台不显示入口()
    {
        var tab = NewTab(ProtocolType.Rdp, open: null);

        Assert.False(tab.CanOpenFileTransferTab);
    }

    private sealed class StubSession(ConnectionProfile profile) : IRemoteSession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public ProtocolType Protocol => profile.Protocol;
        public ConnectionProfile Profile => profile;
        public ConnectionState State => ConnectionState.Idle;
        public ConnectionErrorCode ErrorCode => ConnectionErrorCode.None;
        public string? ErrorMessage => null;

        public event EventHandler<SessionStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
