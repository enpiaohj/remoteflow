using RemoteFlow.Core.Models;
using Xunit;

namespace RemoteFlow.Core.Tests;

/// <summary>文件传输通道的解析规则与默认值（决定「不连接传文件」用哪条路）。</summary>
public class FileTransferOptionsTests
{
    [Fact]
    public void 默认值为自动通道与22端口()
    {
        var options = new FileTransferOptions();

        Assert.Equal(FileTransferChannel.Auto, options.Channel);
        Assert.Equal(22, options.SftpPort);
    }

    [Fact]
    public void SSH主机只有SFTP一条路无论保存了什么通道()
    {
        foreach (var channel in Enum.GetValues<FileTransferChannel>())
        {
            var options = new FileTransferOptions { Channel = channel };

            Assert.Equal(FileTransferChannel.Sftp, options.ResolveChannel(ProtocolType.Ssh));
        }
    }

    [Fact]
    public void RDP主机自动通道解析为SMB()
        => Assert.Equal(
            FileTransferChannel.Smb,
            new FileTransferOptions().ResolveChannel(ProtocolType.Rdp));

    [Theory]
    [InlineData(FileTransferChannel.Sftp)]
    [InlineData(FileTransferChannel.Smb)]
    public void RDP主机显式选择的通道原样返回(FileTransferChannel channel)
        => Assert.Equal(
            channel,
            new FileTransferOptions { Channel = channel }.ResolveChannel(ProtocolType.Rdp));

    [Fact]
    public void VNC没有可用通道()
    {
        foreach (var channel in Enum.GetValues<FileTransferChannel>())
        {
            Assert.Null(new FileTransferOptions { Channel = channel }.ResolveChannel(ProtocolType.Vnc));
        }
    }

    [Fact]
    public void 克隆得到独立副本()
    {
        var original = new FileTransferOptions { Channel = FileTransferChannel.Sftp, SftpPort = 2222 };

        var copy = original.Clone();
        copy.Channel = FileTransferChannel.Smb;
        copy.SftpPort = 22;

        Assert.Equal(FileTransferChannel.Sftp, original.Channel);
        Assert.Equal(2222, original.SftpPort);
    }

    [Fact]
    public void 复制连接时文件传输参数随之复制且互不影响()
    {
        var profile = new ConnectionProfile
        {
            Name = "A",
            Protocol = ProtocolType.Rdp,
            FileTransfer = { Channel = FileTransferChannel.Sftp, SftpPort = 2200 }
        };

        var copy = profile.Clone("B");
        copy.FileTransfer.SftpPort = 22;

        Assert.Equal(FileTransferChannel.Sftp, copy.FileTransfer.Channel);
        Assert.Equal(2200, profile.FileTransfer.SftpPort);
    }

    [Fact]
    public void 新建连接默认带有文件传输参数不为空()
        => Assert.NotNull(new ConnectionProfile().FileTransfer);
}
