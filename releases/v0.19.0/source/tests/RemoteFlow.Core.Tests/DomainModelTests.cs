using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using Xunit;

namespace RemoteFlow.Core.Tests;

/// <summary>领域模型与错误码映射的基础约定。</summary>
public class DomainModelTests
{
    [Theory]
    [InlineData(ProtocolType.Rdp, 3389)]
    [InlineData(ProtocolType.Ssh, 22)]
    [InlineData(ProtocolType.Vnc, 5900)]
    public void 各协议的默认端口符合标准(ProtocolType protocol, int expected)
        => Assert.Equal(expected, ConnectionProfile.GetDefaultPort(protocol));

    [Fact]
    public void 复制连接会生成新Id且不继承收藏与连接记录()
    {
        var source = new ConnectionProfile
        {
            Name = "DC01",
            Host = "10.10.1.10",
            Port = 3389,
            Favorite = true,
            LastConnectedAt = DateTimeOffset.Now,
            TagIds = [Guid.NewGuid()]
        };

        var copy = source.Clone("DC01 - 副本");

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("DC01 - 副本", copy.Name);
        Assert.Equal(source.Host, copy.Host);
        Assert.False(copy.Favorite);
        Assert.Null(copy.LastConnectedAt);
        Assert.Equal(source.TagIds, copy.TagIds);
    }

    [Fact]
    public void 复制连接时协议参数是独立副本而非共享引用()
    {
        var source = new ConnectionProfile();
        source.Rdp.RedirectClipboard = true;

        var copy = source.Clone("副本");
        copy.Rdp.RedirectClipboard = false;

        // 修改副本不得影响原对象，否则编辑一个连接会波及另一个。
        Assert.True(source.Rdp.RedirectClipboard);
    }

    [Fact]
    public void VNC凭据不需要用户名而其余类型需要()
    {
        Assert.False(Credential.RequiresUsername(CredentialType.VncPassword));
        Assert.True(Credential.RequiresUsername(CredentialType.WindowsDomain));
        Assert.True(Credential.RequiresUsername(CredentialType.SshPassword));
    }

    [Fact]
    public void 只有SSH私钥类型需要私钥()
    {
        Assert.True(Credential.RequiresPrivateKey(CredentialType.SshPrivateKey));
        Assert.False(Credential.RequiresPrivateKey(CredentialType.SshPassword));
    }

    /// <summary>
    /// 凭据即使被误传进日志或字符串插值，也不能泄露密码与私钥。
    /// </summary>
    [Fact]
    public void 已解析凭据的字符串表示不包含任何Secret()
    {
        using var credential = new ResolvedCredential
        {
            Type = CredentialType.SshPassword,
            Username = "root",
            Password = "SuperSecret123!",
            PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----"
        };

        var text = credential.ToString();

        Assert.DoesNotContain("SuperSecret123!", text);
        Assert.DoesNotContain("PRIVATE KEY", text);
        Assert.Contains("root", text);
    }

    [Fact]
    public void 已释放的凭据不允许再被使用()
    {
        var credential = new ResolvedCredential { Type = CredentialType.SshPassword, Password = "x" };
        credential.Dispose();

        Assert.Throws<ObjectDisposedException>(credential.ThrowIfDisposed);
    }

    [Fact]
    public void 每个错误码都有面向用户的中文说明()
    {
        foreach (var code in Enum.GetValues<ConnectionErrorCode>())
        {
            var description = ConnectionException.Describe(code);

            Assert.False(string.IsNullOrWhiteSpace(description));
            // 不得把英文枚举名直接抛给用户。
            Assert.DoesNotContain(code.ToString(), description);
        }
    }

    [Fact]
    public void 主机密钥的标识按主机与端口区分()
    {
        Assert.Equal("10.0.0.1:22", SshHostKeyRecord.BuildHostKey("10.0.0.1", 22));
        Assert.NotEqual(
            SshHostKeyRecord.BuildHostKey("10.0.0.1", 22),
            SshHostKeyRecord.BuildHostKey("10.0.0.1", 2222));
    }

    [Fact]
    public void 会话时长在未结束时为空()
    {
        var entry = new ConnectionHistoryEntry { StartedAt = DateTimeOffset.Now };
        Assert.Null(entry.Duration);

        entry.EndedAt = entry.StartedAt.AddMinutes(3);
        Assert.Equal(TimeSpan.FromMinutes(3), entry.Duration);
    }
}
