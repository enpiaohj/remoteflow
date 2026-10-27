using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Data;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>凭据「创建副本」：Secret 另存一份、与原凭据互不影响，名称不冲突。</summary>
public sealed class CredentialDuplicateTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly InMemoryCredentialVault _vault = new();
    private readonly SqliteCredentialRepository _repository;
    private readonly CredentialService _service;

    public CredentialDuplicateTests()
    {
        var database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();
        _repository = new SqliteCredentialRepository(database);
        _service = new CredentialService(_repository, _vault, NullLogger<CredentialService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task 副本沿用账号信息与密码且拥有独立的保险库条目()
    {
        var source = await _service.CreateAsync(
            new Credential
            {
                Name = "域管理员",
                Type = CredentialType.WindowsDomain,
                Domain = "CORP",
                Username = "admin",
                Description = "生产域",
            },
            "p@ss w0rd ",
            null);

        var copy = await _service.DuplicateAsync(source.Id);

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("域管理员 - 副本", copy.Name);
        Assert.Equal(CredentialType.WindowsDomain, copy.Type);
        Assert.Equal("CORP", copy.Domain);
        Assert.Equal("admin", copy.Username);
        Assert.Equal("生产域", copy.Description);
        Assert.NotEqual(source.SecretReference, copy.SecretReference);

        // 密码按原样复制（含首尾空格，密码不能被 Trim）。
        using var resolved = await _service.ResolveAsync(copy.Id);
        Assert.Equal("p@ss w0rd ", resolved!.Password);
        Assert.Equal(2, _vault.Count);

        // 互不影响：删掉副本，原凭据的密码仍在。
        await _service.DeleteAsync(copy.Id);
        using var original = await _service.ResolveAsync(source.Id);
        Assert.Equal("p@ss w0rd ", original!.Password);
        Assert.Equal(1, _vault.Count);
    }

    [Fact]
    public async Task 私钥凭据连同私钥一起复制()
    {
        var source = await _service.CreateAsync(
            new Credential { Name = "跳板机密钥", Type = CredentialType.SshPrivateKey, Username = "ops" },
            "passphrase",
            "-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END OPENSSH PRIVATE KEY-----");

        var copy = await _service.DuplicateAsync(source.Id);

        using var resolved = await _service.ResolveAsync(copy.Id);
        Assert.Equal("passphrase", resolved!.Password);
        Assert.Contains("fake", resolved.PrivateKey);
        Assert.NotEqual(source.KeyReference, copy.KeyReference);
    }

    [Fact]
    public async Task 没有密码的凭据也能复制()
    {
        var source = await _service.CreateAsync(
            new Credential { Name = "无密码", Type = CredentialType.LocalPassword, Username = "guest" }, null, null);

        var copy = await _service.DuplicateAsync(source.Id);

        Assert.Null(copy.SecretReference);
        Assert.Equal("guest", copy.Username);
    }

    [Fact]
    public async Task 副本名称依次避让已有名称()
    {
        var source = await _service.CreateAsync(
            new Credential { Name = "A", Type = CredentialType.LocalPassword, Username = "u" }, "x", null);

        var first = await _service.DuplicateAsync(source.Id);
        var second = await _service.DuplicateAsync(source.Id);
        var third = await _service.DuplicateAsync(source.Id);

        Assert.Equal("A - 副本", first.Name);
        Assert.Equal("A - 副本 2", second.Name);
        Assert.Equal("A - 副本 3", third.Name);
    }

    [Fact]
    public async Task 指定名称时按指定名称并去掉首尾空格()
    {
        var source = await _service.CreateAsync(
            new Credential { Name = "A", Type = CredentialType.LocalPassword, Username = "u" }, "x", null);

        var copy = await _service.DuplicateAsync(source.Id, "  新名称 ");

        Assert.Equal("新名称", copy.Name);
    }

    [Fact]
    public async Task 原凭据密码已从保险库丢失时拒绝复制并且不产生副本()
    {
        var source = await _service.CreateAsync(
            new Credential { Name = "丢密码", Type = CredentialType.LocalPassword, Username = "u" }, "x", null);

        // 真实的 DPAPI 保险库在条目丢失时会把 ResolvedCredential 标成 SecretMissing，这里用替身复现。
        var broken = new CredentialService(
            _repository, new SecretMissingVault(_vault), NullLogger<CredentialService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => broken.DuplicateAsync(source.Id));

        Assert.Single(await _service.GetAllAsync());
    }

    private sealed class SecretMissingVault(RemoteFlow.Core.Abstractions.ICredentialVault inner)
        : RemoteFlow.Core.Abstractions.ICredentialVault
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
                SecretMissing = true,
            });
    }

    [Fact]
    public async Task 复制不存在的凭据抛出明确错误()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DuplicateAsync(Guid.NewGuid()));
    }
}
