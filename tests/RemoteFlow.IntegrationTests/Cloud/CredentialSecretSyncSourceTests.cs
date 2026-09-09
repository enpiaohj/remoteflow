using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Sync.Sources;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

public sealed class CredentialSecretSyncSourceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly RemoteFlowDatabase _database;
    private readonly SqliteCredentialRepository _credentials;
    private readonly InMemoryCredentialVault _vault = new();
    private readonly CredentialSecretSyncSource _source;

    public CredentialSecretSyncSourceTests()
    {
        _database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        _database.Initialize();
        _credentials = new SqliteCredentialRepository(_database);
        _source = new CredentialSecretSyncSource(_credentials, _vault);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task Get_plaintext_resolves_the_secret_from_the_vault()
    {
        var id = Guid.NewGuid();
        var secretRef = await _vault.StoreSecretAsync(InMemoryCredentialVault.CreateReference("pw"), "hunter2");
        await _credentials.AddAsync(new Credential
        {
            Id = id, Name = "c", Type = CredentialType.LocalPassword, Username = "u", SecretReference = secretRef,
        });

        var payload = await _source.GetPlaintextAsync(SyncEntityTypes.CredentialSecret, id.ToString());

        Assert.NotNull(payload);
        Assert.Contains("hunter2", System.Text.Encoding.UTF8.GetString(payload!));
    }

    [Fact]
    public async Task Get_plaintext_is_null_when_the_credential_has_no_secret()
    {
        var id = Guid.NewGuid();
        await _credentials.AddAsync(new Credential { Id = id, Name = "c", Type = CredentialType.LocalPassword, Username = "u" });

        Assert.Null(await _source.GetPlaintextAsync(SyncEntityTypes.CredentialSecret, id.ToString()));
    }

    [Fact]
    public async Task Apply_stores_the_secret_into_the_vault_and_points_the_credential_at_it()
    {
        var id = Guid.NewGuid();
        await _credentials.AddAsync(new Credential { Id = id, Name = "c", Type = CredentialType.SshPrivateKey, Username = "u" });

        var payload = System.Text.Encoding.UTF8.GetBytes(
            """{"Password":"pass","PrivateKey":"-----BEGIN KEY-----"}""");
        await _source.ApplyAsync(SyncEntityTypes.CredentialSecret, id.ToString(), payload, false, 1);

        var credential = await _credentials.GetByIdAsync(id);
        Assert.NotNull(credential!.SecretReference);
        Assert.NotNull(credential.KeyReference);
        Assert.Equal("pass", await _vault.RetrieveSecretAsync(credential.SecretReference!));
        Assert.Equal("-----BEGIN KEY-----", await _vault.RetrieveSecretAsync(credential.KeyReference!));
    }

    [Fact]
    public async Task Apply_before_the_credential_metadata_arrives_defers()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("""{"Password":"x","PrivateKey":null}""");

        // 父 credential 尚未落地 —— 抛可延后的信号，由 SyncCoordinator 整轮拉完再重试，不使整轮失败。
        await Assert.ThrowsAsync<SyncDependencyNotReadyException>(
            () => _source.ApplyAsync(SyncEntityTypes.CredentialSecret, Guid.NewGuid().ToString(), payload, false, 1));
    }
}
