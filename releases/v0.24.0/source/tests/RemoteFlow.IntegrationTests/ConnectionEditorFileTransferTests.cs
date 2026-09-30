using RemoteFlow.Core.Models;
using RemoteFlow.Presentation.ViewModels;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>连接编辑器里的「文件传输通道」选项：预选、写回、校验。</summary>
public class ConnectionEditorFileTransferTests
{
    [Fact]
    public void 新建RDP连接预选SMB且不改动已保存的Auto()
    {
        var vm = NewEditor(existing: null, preselected: ProtocolType.Rdp);

        Assert.Equal(FileTransferChannel.Smb, vm.SelectedFileTransferChannel!.Value);
        Assert.False(vm.IsFileTransferSftp);
        Assert.Equal(FileTransferChannel.Auto, vm.FileTransfer.Channel);
    }

    [Fact]
    public void 没动过通道就保存时连接保持Auto()
    {
        var vm = NewEditor(existing: null, preselected: ProtocolType.Rdp);
        vm.Name = "srv";
        vm.Host = "h";

        var profile = vm.Build();

        Assert.NotNull(profile);
        Assert.Equal(FileTransferChannel.Auto, profile.FileTransfer.Channel);
    }

    [Fact]
    public void 选SFTP写回连接并开启端口校验()
    {
        var vm = NewEditor(existing: null, preselected: ProtocolType.Rdp);
        vm.Name = "srv";
        vm.Host = "h";

        vm.SelectedFileTransferChannel = vm.FileTransferChannelOptions.Single(o => o.Value == FileTransferChannel.Sftp);
        vm.FileTransfer.SftpPort = 2222;
        var profile = vm.Build();

        Assert.True(vm.IsFileTransferSftp);
        Assert.NotNull(profile);
        Assert.Equal(FileTransferChannel.Sftp, profile.FileTransfer.Channel);
        Assert.Equal(2222, profile.FileTransfer.SftpPort);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void RDP走SFTP时端口非法不能保存(int port)
    {
        var vm = NewEditor(existing: null, preselected: ProtocolType.Rdp);
        vm.Name = "srv";
        vm.Host = "h";
        vm.SelectedFileTransferChannel = vm.FileTransferChannelOptions.Single(o => o.Value == FileTransferChannel.Sftp);
        vm.FileTransfer.SftpPort = port;

        Assert.Null(vm.Build());
        Assert.Contains("SFTP 端口", vm.ValidationMessage);
    }

    [Fact]
    public void 通道为SMB时SFTP端口不生效不拦保存()
    {
        var vm = NewEditor(existing: null, preselected: ProtocolType.Rdp);
        vm.Name = "srv";
        vm.Host = "h";
        vm.FileTransfer.SftpPort = 0;

        Assert.NotNull(vm.Build());
    }

    [Fact]
    public void SSH连接的SFTP端口不参与校验因为使用连接自身端口()
    {
        var vm = NewEditor(existing: null, preselected: ProtocolType.Ssh);
        vm.Name = "srv";
        vm.Host = "h";
        vm.SelectedFileTransferChannel = vm.FileTransferChannelOptions.Single(o => o.Value == FileTransferChannel.Sftp);
        vm.FileTransfer.SftpPort = 0;

        Assert.NotNull(vm.Build());
    }

    [Fact]
    public void 编辑已有连接时回填已保存的SFTP选择和端口()
    {
        var existing = new ConnectionProfile
        {
            Name = "srv", Host = "h", Port = 3389, Protocol = ProtocolType.Rdp,
            FileTransfer = { Channel = FileTransferChannel.Sftp, SftpPort = 2200 }
        };

        var vm = NewEditor(existing);

        Assert.Equal(FileTransferChannel.Sftp, vm.SelectedFileTransferChannel!.Value);
        Assert.True(vm.IsFileTransferSftp);
        Assert.Equal(2200, vm.FileTransfer.SftpPort);
    }

    [Fact]
    public void 在编辑器里从SSH切到RDP时不会错误地预选SFTP()
    {
        // SSH 连接的 Auto 解析为 SFTP；但下拉只用 RDP 语义解析，切到 RDP 后应显示默认的 SMB。
        var existing = new ConnectionProfile { Name = "srv", Host = "h", Port = 22, Protocol = ProtocolType.Ssh };

        var vm = NewEditor(existing);
        vm.Protocol = ProtocolType.Rdp;

        Assert.Equal(FileTransferChannel.Smb, vm.SelectedFileTransferChannel!.Value);
    }

    [Fact]
    public void 编辑时改动不会污染原连接对象直到保存()
    {
        var existing = new ConnectionProfile
        {
            Name = "srv", Host = "h", Port = 3389, Protocol = ProtocolType.Rdp,
            FileTransfer = { Channel = FileTransferChannel.Sftp, SftpPort = 2200 }
        };

        var vm = NewEditor(existing);
        vm.SelectedFileTransferChannel = vm.FileTransferChannelOptions.Single(o => o.Value == FileTransferChannel.Smb);
        vm.FileTransfer.SftpPort = 1;

        Assert.Equal(FileTransferChannel.Sftp, existing.FileTransfer.Channel);
        Assert.Equal(2200, existing.FileTransfer.SftpPort);
    }

    private static ConnectionEditorViewModel NewEditor(ConnectionProfile? existing, ProtocolType? preselected = null)
        => new(existing, credentials: [], groups: [], tags: [], defaults: new AppSettings(), defaultGroupId: null, preselectedProtocol: preselected);
}
