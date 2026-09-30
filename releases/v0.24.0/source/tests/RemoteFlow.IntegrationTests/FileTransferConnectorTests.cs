using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.FileTransfer;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.FileTransfer;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure.Data;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 「不建立会话直接传文件」的入口：按连接选通道、凭据处理与释放。
/// 凭据处理必须与会话一致（缺失 / 密钥丢失快速失败），且明文凭据在工厂返回后立即释放。
/// </summary>
public sealed class FileTransferConnectorTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly CredentialService _credentials;
    private readonly ICredentialRepository _repository;
    private readonly RecordingFactory _sftp = new(FileTransferChannel.Sftp);
    private readonly RecordingFactory _smb = new(FileTransferChannel.Smb);
    private readonly StubHostKeyPolicy _hostKeys = new();
    private readonly InMemoryCredentialVault _vault = new();

    public FileTransferConnectorTests()
    {
        var database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();
        _repository = new SqliteCredentialRepository(database);
        _credentials = new CredentialService(_repository, _vault, NullLogger<CredentialService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    // ── 通道选择 ──

    [Fact]
    public async Task SSH连接走SFTP并把连接凭据与主机密钥策略交给工厂()
    {
        var profile = await ProfileAsync(ProtocolType.Ssh);

        await using var fs = await NewConnector().OpenAsync(profile, CancellationToken.None);

        var request = Assert.Single(_sftp.Requests);
        Assert.Same(profile, request.Profile);
        Assert.Equal("u", _sftp.SeenUsername);
        Assert.Equal("pw", _sftp.SeenPassword);
        Assert.Same(_hostKeys, request.HostKeyPolicy);
        Assert.Empty(_smb.Requests);
        Assert.Equal(FileTransferChannel.Sftp, fs.Channel);
    }

    [Fact]
    public async Task RDP连接默认走SMB()
    {
        var profile = await ProfileAsync(ProtocolType.Rdp);

        await using var fs = await NewConnector().OpenAsync(profile, CancellationToken.None);

        Assert.Single(_smb.Requests);
        Assert.Empty(_sftp.Requests);
    }

    [Fact]
    public async Task RDP连接显式选SFTP时走SFTP()
    {
        var profile = await ProfileAsync(ProtocolType.Rdp);
        profile.FileTransfer.Channel = FileTransferChannel.Sftp;

        await using var fs = await NewConnector().OpenAsync(profile, CancellationToken.None);

        Assert.Single(_sftp.Requests);
        Assert.Empty(_smb.Requests);
    }

    // ── 支持性判断（不触网） ──

    [Theory]
    [InlineData(ProtocolType.Ssh, FileTransferChannel.Sftp)]
    [InlineData(ProtocolType.Rdp, FileTransferChannel.Smb)]
    public void 支持性判断给出通道且不访问工厂的连接方法(ProtocolType protocol, FileTransferChannel expected)
    {
        var support = NewConnector().GetSupport(new ConnectionProfile { Protocol = protocol });

        Assert.True(support.IsSupported);
        Assert.Equal(expected, support.Channel);
        Assert.Null(support.Reason);
        Assert.Empty(_sftp.Requests);
        Assert.Empty(_smb.Requests);
    }

    [Fact]
    public async Task VNC不支持文件传输且打开时抛组件不可用()
    {
        var connector = NewConnector();
        var profile = await ProfileAsync(ProtocolType.Vnc);

        var support = connector.GetSupport(profile);
        var ex = await Assert.ThrowsAsync<ConnectionException>(() => connector.OpenAsync(profile, CancellationToken.None));

        Assert.False(support.IsSupported);
        Assert.Null(support.Channel);
        Assert.Contains("VNC", support.Reason);
        Assert.Equal(ConnectionErrorCode.ComponentUnavailable, ex.ErrorCode);
        Assert.Contains("VNC", ex.Message);
    }

    [Fact]
    public void 通道工厂缺失时不支持并说明原因()
    {
        var connector = new FileTransferConnector([_sftp], _credentials, _hostKeys, NullLogger<FileTransferConnector>.Instance);

        var support = connector.GetSupport(new ConnectionProfile { Protocol = ProtocolType.Rdp });

        Assert.False(support.IsSupported);
        Assert.Equal(FileTransferChannel.Smb, support.Channel);
        Assert.Contains("没有提供", support.Reason);
    }

    [Fact]
    public void 通道在当前环境不可用时透出其原因()
    {
        _smb.UnavailableReason = "SMB 管理共享仅 Windows 版可用。";

        var support = NewConnector().GetSupport(new ConnectionProfile { Protocol = ProtocolType.Rdp });

        Assert.False(support.IsSupported);
        Assert.Equal("SMB 管理共享仅 Windows 版可用。", support.Reason);
    }

    // ── 凭据 ──

    [Fact]
    public async Task 连接未指定凭据时抛凭据缺失且提示可操作()
    {
        var profile = new ConnectionProfile { Protocol = ProtocolType.Ssh, Host = "h" };

        var ex = await Assert.ThrowsAsync<ConnectionException>(
            () => NewConnector().OpenAsync(profile, CancellationToken.None));

        Assert.Equal(ConnectionErrorCode.CredentialMissing, ex.ErrorCode);
        Assert.Contains("未指定凭据", ex.Message);
        Assert.Empty(_sftp.Requests);
    }

    [Fact]
    public async Task 引用的凭据已不存在时抛凭据缺失()
    {
        var profile = new ConnectionProfile { Protocol = ProtocolType.Ssh, Host = "h", CredentialId = Guid.NewGuid() };

        var ex = await Assert.ThrowsAsync<ConnectionException>(
            () => NewConnector().OpenAsync(profile, CancellationToken.None));

        Assert.Equal(ConnectionErrorCode.CredentialMissing, ex.ErrorCode);
        Assert.Empty(_sftp.Requests);
    }

    [Fact]
    public async Task 凭据密钥已从保险库丢失时快速失败而不是带空密码去连()
    {
        var profile = await ProfileAsync(ProtocolType.Ssh);
        var connector = new FileTransferConnector(
            [_sftp, _smb],
            new CredentialService(_repository, new SecretMissingVault(_vault), NullLogger<CredentialService>.Instance),
            _hostKeys,
            NullLogger<FileTransferConnector>.Instance);

        var ex = await Assert.ThrowsAsync<ConnectionException>(() => connector.OpenAsync(profile, CancellationToken.None));

        Assert.Equal(ConnectionErrorCode.CredentialSecretMissing, ex.ErrorCode);
        Assert.Empty(_sftp.Requests);
    }

    [Fact]
    public async Task 工厂返回后明文凭据立即被释放()
    {
        var profile = await ProfileAsync(ProtocolType.Ssh);

        await using var fs = await NewConnector().OpenAsync(profile, CancellationToken.None);

        var credential = Assert.Single(_sftp.Requests).Credential;
        Assert.Throws<ObjectDisposedException>(credential.ThrowIfDisposed);
    }

    [Fact]
    public async Task 工厂失败时连接异常原样抛出且凭据同样被释放()
    {
        var profile = await ProfileAsync(ProtocolType.Ssh);
        _sftp.Failure = ConnectionException.FromCode(ConnectionErrorCode.AuthenticationFailed);

        var ex = await Assert.ThrowsAsync<ConnectionException>(
            () => NewConnector().OpenAsync(profile, CancellationToken.None));

        Assert.Same(_sftp.Failure, ex);
        Assert.Throws<ObjectDisposedException>(Assert.Single(_sftp.Requests).Credential.ThrowIfDisposed);
    }

    [Fact]
    public async Task 取消令牌会转交给工厂()
    {
        var profile = await ProfileAsync(ProtocolType.Ssh);
        using var cts = new CancellationTokenSource();

        await using var fs = await NewConnector().OpenAsync(profile, cts.Token);

        Assert.Equal(cts.Token, _sftp.SeenToken);
    }

    // ── 辅助 ──

    private FileTransferConnector NewConnector()
        => new([_sftp, _smb], _credentials, _hostKeys, NullLogger<FileTransferConnector>.Instance);

    private async Task<ConnectionProfile> ProfileAsync(ProtocolType protocol)
    {
        var credential = await _credentials.CreateAsync(
            new Credential { Name = "cred-" + Guid.NewGuid().ToString("N")[..6], Type = CredentialType.SshPassword, Username = "u" },
            "pw",
            null);
        return new ConnectionProfile
        {
            Name = "conn",
            Host = "host.example",
            Port = ConnectionProfile.GetDefaultPort(protocol),
            Protocol = protocol,
            CredentialId = credential.Id
        };
    }

    private sealed class RecordingFactory(FileTransferChannel channel) : IRemoteFileSystemFactory
    {
        public List<FileSystemOpenRequest> Requests { get; } = [];

        public string? UnavailableReason { get; set; }

        public Exception? Failure { get; set; }

        public string? SeenUsername { get; private set; }

        public string? SeenPassword { get; private set; }

        public CancellationToken SeenToken { get; private set; }

        public FileTransferChannel Channel => channel;

        public bool IsAvailable(out string? unavailableReason)
        {
            unavailableReason = UnavailableReason;
            return UnavailableReason is null;
        }

        public Task<IRemoteFileSystem> OpenAsync(FileSystemOpenRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            SeenUsername = request.Credential.Username;
            SeenPassword = request.Credential.Password;
            SeenToken = cancellationToken;
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult<IRemoteFileSystem>(new FakeRemoteFileSystem { Channel = channel });
        }
    }

    private sealed class StubHostKeyPolicy : ISshHostKeyPolicy
    {
        public SshHostKeyVerificationContext Lookup(SshHostKeyVerificationContext context) => context;

        public Task<bool> ConfirmAndRememberAsync(SshHostKeyVerificationContext context, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    /// <summary>模拟凭据记录还在、但保险库里的密钥被手动删掉。</summary>
    private sealed class SecretMissingVault(ICredentialVault inner) : ICredentialVault
    {
        public Task<string> StoreSecretAsync(string reference, string secret, CancellationToken ct = default)
            => inner.StoreSecretAsync(reference, secret, ct);

        public Task<string?> RetrieveSecretAsync(string reference, CancellationToken ct = default)
            => inner.RetrieveSecretAsync(reference, ct);

        public Task DeleteSecretAsync(string reference, CancellationToken ct = default)
            => inner.DeleteSecretAsync(reference, ct);

        public Task<ResolvedCredential> ResolveAsync(Credential credential, CancellationToken ct = default)
            => Task.FromResult(new ResolvedCredential
            {
                Type = credential.Type,
                Username = credential.Username,
                SecretMissing = true
            });
    }
}
