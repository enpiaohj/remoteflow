using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using RemoteFlow.Core.FileTransfer;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Protocol.Ssh;
using Xunit;
using ProtocolType = RemoteFlow.Core.Models.ProtocolType;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// SSH 协议层里与文件传输相关的纯逻辑：主机密钥闸门、连接信息构建、异常翻译、端口解析。
/// 这些都不需要真实服务器；真实 SFTP 收发由实机验证覆盖。
/// </summary>
public class SshFileTransferProtocolTests
{
    // ── 主机密钥闸门 ──

    [Fact]
    public void 闸门未配置策略时一律拒绝且不放行()
    {
        var gate = NewGate(policy: null);

        Assert.False(gate.Decide("ssh-ed25519", "SHA256:abc"));
        Assert.Equal(ConnectionErrorCode.HostKeyRejected, gate.Failure);
        Assert.Null(gate.PendingHostKey);
    }

    [Fact]
    public void 已信任且指纹一致时静默放行()
    {
        var gate = NewGate(new FakePolicy("SHA256:abc"));

        Assert.True(gate.Decide("ssh-ed25519", "SHA256:abc"));
        Assert.Null(gate.PendingHostKey);
        Assert.Equal(ConnectionErrorCode.None, gate.Failure);
    }

    [Fact]
    public void 首次连接的密钥不放行而是记为待确认()
    {
        var gate = NewGate(new FakePolicy(known: null));

        Assert.False(gate.Decide("ssh-ed25519", "SHA256:new"));

        var pending = Assert.IsType<SshHostKeyVerificationContext>(gate.PendingHostKey);
        Assert.False(pending.IsMismatch);
        Assert.Equal("SHA256:new", pending.Fingerprint);
        Assert.Equal(ConnectionErrorCode.None, gate.Failure);
    }

    [Fact]
    public void 指纹变化时不放行且标记为不一致()
    {
        var gate = NewGate(new FakePolicy("SHA256:old"));

        Assert.False(gate.Decide("ssh-ed25519", "SHA256:changed"));

        Assert.True(gate.PendingHostKey!.IsMismatch);
    }

    [Fact]
    public void 策略抛异常时按拒绝处理()
    {
        var gate = NewGate(new FakePolicy(known: null) { Throw = true });

        Assert.False(gate.Decide("ssh-ed25519", "SHA256:x"));
        Assert.Equal(ConnectionErrorCode.HostKeyRejected, gate.Failure);
    }

    [Fact]
    public void 闸门把目标主机端口算法与指纹交给策略查询()
    {
        var policy = new FakePolicy("SHA256:abc");
        var gate = new SshHostKeyGate(policy, "files.example", 2222, NullLogger.Instance, Guid.NewGuid());

        gate.Decide("ssh-rsa", "SHA256:abc");

        var seen = Assert.Single(policy.Lookups);
        Assert.Equal("files.example", seen.Host);
        Assert.Equal(2222, seen.Port);
        Assert.Equal("ssh-rsa", seen.KeyAlgorithm);
        Assert.Equal("SHA256:abc", seen.Fingerprint);
    }

    [Fact]
    public void 用户确认后重试闸门静默通过()
    {
        var policy = new FakePolicy(known: null);
        var gate = NewGate(policy);
        Assert.False(gate.Decide("ssh-ed25519", "SHA256:new"));

        policy.Known = "SHA256:new"; // 模拟 ConfirmAndRememberAsync 记录指纹
        gate.Reset();

        Assert.True(gate.Decide("ssh-ed25519", "SHA256:new"));
        Assert.Null(gate.PendingHostKey);
    }

    [Fact]
    public void ClearPending只清待确认不清失败原因()
    {
        var gate = NewGate(new FakePolicy(known: null));
        gate.Decide("ssh-ed25519", "SHA256:new");

        gate.ClearPending();

        Assert.Null(gate.PendingHostKey);
    }

    // ── 连接信息构建 ──

    [Fact]
    public void 口令凭据构建密码认证的连接信息()
    {
        using var credential = new ResolvedCredential
        {
            Type = CredentialType.SshPassword, Username = "deploy", Password = "pw"
        };

        var info = SshConnectionInfoFactory.Build("host.example", 2200, credential, 15, "UTF-8");

        Assert.Equal("host.example", info.Host);
        Assert.Equal(2200, info.Port);
        Assert.Equal("deploy", info.Username);
        Assert.IsType<PasswordAuthenticationMethod>(Assert.Single(info.AuthenticationMethods));
        Assert.Equal(TimeSpan.FromSeconds(15), info.Timeout);
    }

    [Fact]
    public void 连接超时下限为5秒()
    {
        using var credential = new ResolvedCredential { Type = CredentialType.SshPassword, Username = "u" };

        var info = SshConnectionInfoFactory.Build("h", 22, credential, 0, "UTF-8");

        Assert.Equal(TimeSpan.FromSeconds(5), info.Timeout);
    }

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("")]
    [InlineData("not-a-real-encoding")]
    public void 编码无法识别时退回UTF8(string name)
        => Assert.Equal("utf-8", SshConnectionInfoFactory.ResolveEncoding(name).WebName);

    [Fact]
    public void 缺少凭据时抛凭据缺失()
    {
        var ex = Assert.Throws<ConnectionException>(() => SshConnectionInfoFactory.Build("h", 22, null, 15, "UTF-8"));

        Assert.Equal(ConnectionErrorCode.CredentialMissing, ex.ErrorCode);
    }

    [Fact]
    public void 用户名为空时抛认证失败()
    {
        using var credential = new ResolvedCredential { Type = CredentialType.SshPassword, Username = "  " };

        var ex = Assert.Throws<ConnectionException>(() => SshConnectionInfoFactory.Build("h", 22, credential, 15, "UTF-8"));

        Assert.Equal(ConnectionErrorCode.AuthenticationFailed, ex.ErrorCode);
    }

    [Fact]
    public void 已释放的凭据不能再用来构建()
    {
        var credential = new ResolvedCredential { Type = CredentialType.SshPassword, Username = "u" };
        credential.Dispose();

        Assert.Throws<ObjectDisposedException>(() => SshConnectionInfoFactory.Build("h", 22, credential, 15, "UTF-8"));
    }

    [Fact]
    public void 私钥凭据缺少私钥正文时抛凭据缺失()
    {
        using var credential = new ResolvedCredential { Type = CredentialType.SshPrivateKey, Username = "u" };

        var ex = Assert.Throws<ConnectionException>(() => SshConnectionInfoFactory.Build("h", 22, credential, 15, "UTF-8"));

        Assert.Equal(ConnectionErrorCode.CredentialMissing, ex.ErrorCode);
    }

    [Fact]
    public void 私钥无法解析时错误消息不泄露私钥内容()
    {
        const string body = "SECRET-KEY-BODY-DO-NOT-LEAK";
        using var credential = new ResolvedCredential
        {
            Type = CredentialType.SshPrivateKey,
            Username = "u",
            PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\n" + body + "\n-----END OPENSSH PRIVATE KEY-----"
        };

        var ex = Assert.Throws<ConnectionException>(() => SshConnectionInfoFactory.Build("h", 22, credential, 15, "UTF-8"));

        Assert.Equal(ConnectionErrorCode.AuthenticationFailed, ex.ErrorCode);
        Assert.DoesNotContain(body, ex.Message);
        Assert.DoesNotContain(body, ex.ToString().Split("---> ")[0]);
    }

    // ── SFTP 端口解析 ──

    [Fact]
    public void SSH连接用连接自身端口()
    {
        var profile = new ConnectionProfile { Protocol = ProtocolType.Ssh, Port = 2201 };
        profile.FileTransfer.SftpPort = 9999;

        Assert.Equal(2201, SftpFileSystemFactory.ResolvePort(profile));
    }

    [Fact]
    public void RDP主机走OpenSSH时用配置的SFTP端口()
    {
        var profile = new ConnectionProfile { Protocol = ProtocolType.Rdp, Port = 3389 };
        profile.FileTransfer.SftpPort = 2222;

        Assert.Equal(2222, SftpFileSystemFactory.ResolvePort(profile));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(70000)]
    public void SFTP端口非法时退回22(int port)
    {
        var profile = new ConnectionProfile { Protocol = ProtocolType.Rdp };
        profile.FileTransfer.SftpPort = port;

        Assert.Equal(22, SftpFileSystemFactory.ResolvePort(profile));
    }

    // ── 连接阶段异常翻译 ──

    [Fact]
    public void 连接异常原样透传()
    {
        var original = ConnectionException.FromCode(ConnectionErrorCode.Timeout);

        Assert.Same(original, SftpFileSystemFactory.ToConnectionException(original, NewGate(new FakePolicy("x"))));
    }

    [Theory]
    [InlineData("SHA256:old", ConnectionErrorCode.HostKeyMismatch)]
    [InlineData(null, ConnectionErrorCode.HostKeyRejected)]
    public void 存在待确认主机密钥时一律按拒绝处理绝不放行(string? known, ConnectionErrorCode expected)
    {
        var gate = NewGate(new FakePolicy(known));
        gate.Decide("ssh-ed25519", "SHA256:other");

        var result = SftpFileSystemFactory.ToConnectionException(new SshConnectionException("aborted"), gate);

        Assert.Equal(expected, result.ErrorCode);
    }

    [Fact]
    public void 服务端未启用sftp子系统时给出准确提示()
    {
        var result = SftpFileSystemFactory.ToConnectionException(
            new SshException("Subsystem 'sftp' could not be executed"), NewGate(new FakePolicy("x")));

        Assert.Contains("SFTP", result.Message);
        Assert.Contains("子系统", result.Message);
    }

    [Fact]
    public void 认证失败与网络错误映射到标准错误码()
    {
        var gate = NewGate(new FakePolicy("x"));

        Assert.Equal(
            ConnectionErrorCode.AuthenticationFailed,
            SftpFileSystemFactory.ToConnectionException(new SshAuthenticationException("denied"), gate).ErrorCode);
        Assert.Equal(
            ConnectionErrorCode.HostNotFound,
            SftpFileSystemFactory.ToConnectionException(new SocketException((int)SocketError.HostNotFound), gate).ErrorCode);
    }

    [Fact]
    public void 主机密钥闸门的失败原因优先于异常类型()
    {
        var gate = NewGate(policy: null);
        gate.Decide("ssh-ed25519", "SHA256:x"); // → Failure = HostKeyRejected

        var result = SftpFileSystemFactory.ToConnectionException(new SshConnectionException("aborted"), gate);

        Assert.Equal(ConnectionErrorCode.HostKeyRejected, result.ErrorCode);
    }

    // ── 操作阶段异常翻译 ──

    [Fact]
    public void 权限与路径不存在映射到对应分类()
    {
        Assert.Equal(
            FileTransferErrorCode.PermissionDenied,
            SftpFileSystem.Translate(new SftpPermissionDeniedException("x"))!.Code);
        Assert.Equal(
            FileTransferErrorCode.NotFound,
            SftpFileSystem.Translate(new SftpPathNotFoundException("x"))!.Code);
    }

    [Fact]
    public void 连接类异常映射为连接中断()
    {
        Assert.Equal(
            FileTransferErrorCode.ConnectionLost,
            SftpFileSystem.Translate(new SshConnectionException("closed"))!.Code);
        Assert.Equal(
            FileTransferErrorCode.ConnectionLost,
            SftpFileSystem.Translate(new SocketException((int)SocketError.ConnectionReset))!.Code);
    }

    [Theory]
    [InlineData(StatusCode.NoConnection)]
    [InlineData(StatusCode.ConnectionLost)]
    public void SFTP状态码表示连接丢失时映射为连接中断(StatusCode status)
        => Assert.Equal(
            FileTransferErrorCode.ConnectionLost,
            SftpFileSystem.Translate(new SftpException(status, "x"))!.Code);

    [Fact]
    public void SFTP状态码表示不支持时映射为不支持()
        => Assert.Equal(
            FileTransferErrorCode.NotSupported,
            SftpFileSystem.Translate(new SftpException(StatusCode.OperationUnsupported, "x"))!.Code);

    [Theory]
    [InlineData("No space left on device")]
    [InlineData("Disk quota exceeded")]
    public void 远端磁盘满按消息识别(string message)
        => Assert.Equal(FileTransferErrorCode.DiskFull, SftpFileSystem.Translate(new SftpException(StatusCode.Failure, message))!.Code);

    [Fact]
    public void 其他SFTP错误归为未知且消息不含服务端原文()
    {
        var translated = SftpFileSystem.Translate(new SftpException(StatusCode.Failure, "internal detail /srv/secret/path"))!;

        Assert.Equal(FileTransferErrorCode.Unknown, translated.Code);
        Assert.DoesNotContain("/srv/secret/path", translated.Message);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070))] // ERROR_DISK_FULL
    [InlineData(unchecked((int)0x80070027))] // ERROR_HANDLE_DISK_FULL
    public void 本机磁盘满按HResult识别不依赖系统语言(int hresult)
        => Assert.Equal(
            FileTransferErrorCode.DiskFull,
            SftpFileSystem.Translate(new IOException("磁盘空间不足", hresult))!.Code);

    [Fact]
    public void 不认识的异常不翻译让其照常冒泡()
    {
        Assert.Null(SftpFileSystem.Translate(new InvalidOperationException("bug")));
        Assert.Null(SftpFileSystem.Translate(new IOException("其他 IO 错误")));
    }

    // ── 会话上的文件传输能力 ──

    [Fact]
    public async Task 会话未连接时打开文件传输抛明确异常且不触网()
    {
        var session = new SshSession(
            new SessionRequest { Profile = new ConnectionProfile { Protocol = ProtocolType.Ssh, Host = "127.0.0.1", Port = 1 } },
            NullLogger.Instance);

        Assert.IsAssignableFrom<IFileTransferSession>(session);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((IFileTransferSession)session).OpenFileSystemAsync(CancellationToken.None));
        Assert.Contains("未连接", ex.Message);
        await session.DisposeAsync();
    }

    // ── 辅助 ──

    private static SshHostKeyGate NewGate(ISshHostKeyPolicy? policy)
        => new(policy, "host.example", 22, NullLogger.Instance, Guid.NewGuid());

    private sealed class FakePolicy(string? known) : ISshHostKeyPolicy
    {
        public string? Known { get; set; } = known;

        public bool Throw { get; init; }

        public List<SshHostKeyVerificationContext> Lookups { get; } = [];

        public SshHostKeyVerificationContext Lookup(SshHostKeyVerificationContext context)
        {
            Lookups.Add(context);
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            return new SshHostKeyVerificationContext
            {
                Host = context.Host,
                Port = context.Port,
                KeyAlgorithm = context.KeyAlgorithm,
                Fingerprint = context.Fingerprint,
                KnownFingerprint = Known
            };
        }

        public Task<bool> ConfirmAndRememberAsync(SshHostKeyVerificationContext context, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
