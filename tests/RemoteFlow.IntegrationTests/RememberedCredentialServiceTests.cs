using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Data;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>连接前凭据输入框勾选「记住我的凭据」：新建凭据并分配给连接 / 补存已有凭据的密码。</summary>
public sealed class RememberedCredentialServiceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly InMemoryCredentialVault _vault = new();
    private readonly CredentialService _credentials;
    private readonly ConnectionService _connections;
    private readonly RememberedCredentialService _service;

    public RememberedCredentialServiceTests()
    {
        var database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();
        _credentials = new CredentialService(
            new SqliteCredentialRepository(database), _vault, NullLogger<CredentialService>.Instance);
        _connections = new ConnectionService(
            new SqliteConnectionRepository(database), new SqliteGroupRepository(database), new SqliteTagRepository(database));
        _service = new RememberedCredentialService(_credentials, _connections);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    private async Task<ConnectionProfile> AddConnectionAsync(string name, Guid? credentialId = null)
    {
        var profile = new ConnectionProfile
        {
            Name = name, Host = "host", Port = 22, Protocol = ProtocolType.Ssh, CredentialId = credentialId,
        };
        await _connections.CreateAsync(profile);
        return profile;
    }

    [Fact]
    public async Task 没分配凭据时新建凭据并分配给连接_密码进保险库()
    {
        var profile = await AddConnectionAsync("生产库");
        var requirement = new ConnectionCredentialRequirement(ConnectionCredentialPrompt.UsernameAndPassword, CredentialType.SshPassword);

        var id = await _service.RememberAsync(profile, requirement, "root", "", "s3cret");

        Assert.Equal(id, profile.CredentialId);
        var saved = await _connections.GetByIdAsync(profile.Id);
        Assert.Equal(id, saved!.CredentialId);
        var credential = await _credentials.GetByIdAsync(id);
        Assert.Equal("root", credential!.Name);
        Assert.Equal(CredentialType.SshPassword, credential.Type);
        using var resolved = await _credentials.ResolveAsync(id);
        Assert.Equal("s3cret", resolved!.Password);
        Assert.Equal(ConnectionCredentialPrompt.None, (await _credentials.GetConnectionRequirementAsync(saved)).Prompt);
    }

    [Fact]
    public async Task 凭据名称重复时自动编号避让()
    {
        var requirement = new ConnectionCredentialRequirement(ConnectionCredentialPrompt.UsernameAndPassword, CredentialType.SshPassword);
        var a = await AddConnectionAsync("同名");
        var b = await AddConnectionAsync("同名");

        var first = await _service.RememberAsync(a, requirement, "root", "", "p1");
        var second = await _service.RememberAsync(b, requirement, "root", "", "p2");

        Assert.Equal("root", (await _credentials.GetByIdAsync(first))!.Name);
        Assert.Equal("root 2", (await _credentials.GetByIdAsync(second))!.Name);
    }

    [Fact]
    public async Task 凭据只有账户名时把密码补存进该凭据_不新建()
    {
        var credential = await _credentials.CreateAsync(
            new Credential { Name = "仅账号", Type = CredentialType.SshPassword, Username = "deploy" }, null, null);
        var profile = await AddConnectionAsync("主机", credential.Id);
        var requirement = await _credentials.GetConnectionRequirementAsync(profile);
        Assert.Equal(ConnectionCredentialPrompt.PasswordOnly, requirement.Prompt);

        var id = await _service.RememberAsync(profile, requirement, "deploy", "", "typed");

        Assert.Equal(credential.Id, id);
        Assert.Single(await _credentials.GetAllAsync());
        using var resolved = await _credentials.ResolveAsync(id);
        Assert.Equal("typed", resolved!.Password);
        Assert.Equal(ConnectionCredentialPrompt.None, (await _credentials.GetConnectionRequirementAsync(profile)).Prompt);
    }

    [Fact]
    public async Task 更多选项里选已有凭据并记住_只分配不新建()
    {
        var saved = await _credentials.CreateAsync(
            new Credential { Name = "已有", Type = CredentialType.SshPassword, Username = "ops" }, "pw", null);
        var profile = await AddConnectionAsync("某主机");

        await _service.AssignAsync(profile, saved.Id);

        Assert.Equal(saved.Id, (await _connections.GetByIdAsync(profile.Id))!.CredentialId);
        Assert.Single(await _credentials.GetAllAsync());
    }

    [Fact]
    public async Task 名称默认为用户名全称_可自定义名称与类型()
    {
        var requirement = new ConnectionCredentialRequirement(ConnectionCredentialPrompt.UsernameAndPassword, CredentialType.LocalPassword);

        var domainId = await _service.RememberAsync(await AddConnectionAsync("A"), requirement, "alice", "corp", "pw");
        var customId = await _service.RememberAsync(
            await AddConnectionAsync("B"), requirement, "admin", "CORP", "pw", " 运维账号 ", CredentialType.WindowsDomain);

        Assert.Equal("corp\\alice", (await _credentials.GetByIdAsync(domainId))!.Name);
        var custom = (await _credentials.GetByIdAsync(customId))!;
        Assert.Equal("运维账号", custom.Name);
        Assert.Equal(CredentialType.WindowsDomain, custom.Type);
        Assert.Equal("CORP", custom.Domain);
    }
}