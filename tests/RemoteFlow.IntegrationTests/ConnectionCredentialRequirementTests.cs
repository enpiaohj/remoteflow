using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Data;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>连接前「要不要当场输入账号 / 密码」的判定：没分配凭据、凭据只有账户名、凭据已齐全。</summary>
public sealed class ConnectionCredentialRequirementTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly CredentialService _service;

    public ConnectionCredentialRequirementTests()
    {
        var database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();
        _service = new CredentialService(
            new SqliteCredentialRepository(database),
            new InMemoryCredentialVault(),
            NullLogger<CredentialService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Theory]
    [InlineData(ProtocolType.Rdp, CredentialType.LocalPassword, ConnectionCredentialPrompt.UsernameAndPassword)]
    [InlineData(ProtocolType.Ssh, CredentialType.SshPassword, ConnectionCredentialPrompt.UsernameAndPassword)]
    [InlineData(ProtocolType.Vnc, CredentialType.VncPassword, ConnectionCredentialPrompt.PasswordOnly)]
    public async Task 没分配凭据时按协议要求输入(ProtocolType protocol, CredentialType type, ConnectionCredentialPrompt expected)
    {
        var requirement = await _service.GetConnectionRequirementAsync(new ConnectionProfile { Protocol = protocol });

        Assert.Equal(expected, requirement.Prompt);
        Assert.Equal(type, requirement.Type);
    }

    [Fact]
    public async Task 凭据只有账户名时只要求密码_并带出账户名与域()
    {
        var credential = await _service.CreateAsync(
            new Credential { Name = "仅账号", Type = CredentialType.WindowsDomain, Domain = "CORP", Username = "admin" },
            password: null,
            privateKey: null);

        var requirement = await _service.GetConnectionRequirementAsync(
            new ConnectionProfile { Protocol = ProtocolType.Rdp, CredentialId = credential.Id });

        Assert.Equal(ConnectionCredentialPrompt.PasswordOnly, requirement.Prompt);
        Assert.Equal("admin", requirement.Username);
        Assert.Equal("CORP", requirement.Domain);
        Assert.True(requirement.NeedsUsername);
    }

    [Fact]
    public async Task 凭据已保存密码时不需要输入()
    {
        var credential = await _service.CreateAsync(
            new Credential { Name = "完整", Type = CredentialType.SshPassword, Username = "root" },
            "secret",
            null);

        var requirement = await _service.GetConnectionRequirementAsync(
            new ConnectionProfile { Protocol = ProtocolType.Ssh, CredentialId = credential.Id });

        Assert.Equal(ConnectionCredentialPrompt.None, requirement.Prompt);
    }

    [Fact]
    public async Task 私钥凭据即使没有口令短语也不弹框()
    {
        var credential = await _service.CreateAsync(
            new Credential { Name = "密钥", Type = CredentialType.SshPrivateKey, Username = "root" },
            password: null,
            privateKey: "-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END OPENSSH PRIVATE KEY-----");

        var requirement = await _service.GetConnectionRequirementAsync(
            new ConnectionProfile { Protocol = ProtocolType.Ssh, CredentialId = credential.Id });

        Assert.Equal(ConnectionCredentialPrompt.None, requirement.Prompt);
    }

    [Fact]
    public async Task 凭据记录已丢失时交给原有路径报错_不弹框()
    {
        var requirement = await _service.GetConnectionRequirementAsync(
            new ConnectionProfile { Protocol = ProtocolType.Ssh, CredentialId = Guid.NewGuid() });

        Assert.Equal(ConnectionCredentialPrompt.None, requirement.Prompt);
    }
}
