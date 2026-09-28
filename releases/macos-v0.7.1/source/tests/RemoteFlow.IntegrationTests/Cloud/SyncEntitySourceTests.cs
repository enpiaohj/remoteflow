using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Sync;
using RemoteFlow.Infrastructure.Sync.Sources;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

public sealed class SyncEntitySourceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly RemoteFlowDatabase _database;
    private readonly SqliteConnectionRepository _connections;
    private readonly SqliteGroupRepository _groups;
    private readonly SqliteTagRepository _tags;
    private readonly SqliteCredentialRepository _credentials;

    public SyncEntitySourceTests()
    {
        _database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        _database.Initialize();
        _connections = new SqliteConnectionRepository(_database);
        _groups = new SqliteGroupRepository(_database);
        _tags = new SqliteTagRepository(_database);
        _credentials = new SqliteCredentialRepository(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task Connection_source_round_trips_a_profile_through_get_and_apply()
    {
        var source = new ConnectionSyncSource(_connections);
        var profile = new ConnectionProfile { Name = "DC01", Host = "10.0.0.1", Port = 3389, Protocol = ProtocolType.Rdp };
        await _connections.AddAsync(profile);

        var payload = await source.GetPlaintextAsync(SyncEntityTypes.Connection, profile.Id.ToString());
        Assert.NotNull(payload);

        // 干净库上 Apply → 建出同一条
        using var otherWorkspace = new TempWorkspace();
        var otherDb = new RemoteFlowDatabase(otherWorkspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        otherDb.Initialize();
        var otherRepo = new SqliteConnectionRepository(otherDb);
        var otherSource = new ConnectionSyncSource(otherRepo);

        await otherSource.ApplyAsync(SyncEntityTypes.Connection, profile.Id.ToString(), payload, false, 1);

        var applied = await otherRepo.GetByIdAsync(profile.Id);
        Assert.NotNull(applied);
        Assert.Equal("DC01", applied!.Name);
        Assert.Equal(3389, applied.Port);

        await otherSource.ApplyAsync(SyncEntityTypes.Connection, profile.Id.ToString(), null, deleted: true, 1);
        Assert.Null(await otherRepo.GetByIdAsync(profile.Id));
    }

    [Fact]
    public async Task Connection_source_returns_null_plaintext_for_a_missing_entity()
    {
        var source = new ConnectionSyncSource(_connections);
        Assert.Null(await source.GetPlaintextAsync(SyncEntityTypes.Connection, Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task Group_source_excludes_system_groups_and_ignores_writes_to_the_ungrouped_group()
    {
        var source = new GroupSyncSource(_groups);
        // 「未分组」系统组由 v2 迁移预置，无需再插。
        var real = new ConnectionGroup { Name = "生产" };
        await _groups.AddAsync(real);

        var ids = await source.ListEntityIdsAsync(SyncEntityTypes.Group);

        Assert.Equal([real.Id.ToString()], ids);
        Assert.Null(await source.GetPlaintextAsync(SyncEntityTypes.Group, ConnectionGroup.UngroupedId.ToString()));

        // Apply 到「未分组」是空操作
        await source.ApplyAsync(
            SyncEntityTypes.Group, ConnectionGroup.UngroupedId.ToString(),
            SyncSerializer.Serialize(new ConnectionGroup { Name = "改名尝试" }, 1), false, 1);
        Assert.Equal("未分组", (await _groups.GetAllAsync()).Single(g => g.Id == ConnectionGroup.UngroupedId).Name);
    }

    [Fact]
    public async Task Credential_source_payload_carries_no_local_secret_references()
    {
        var source = new CredentialSyncSource(_credentials);
        var credential = new Credential
        {
            Name = "DomainAdmin", Type = CredentialType.WindowsDomain, Username = "admin", Domain = "CORP",
            SecretReference = "password:abc123", KeyReference = null,
        };
        await _credentials.AddAsync(credential);

        var payload = await source.GetPlaintextAsync(SyncEntityTypes.Credential, credential.Id.ToString());
        var text = System.Text.Encoding.UTF8.GetString(payload!);

        Assert.DoesNotContain("password:abc123", text);
        Assert.DoesNotContain("SecretReference", text);
        Assert.Contains("DomainAdmin", text);
    }

    [Fact]
    public async Task Credential_source_apply_preserves_the_local_devices_secret_references()
    {
        var source = new CredentialSyncSource(_credentials);
        var id = Guid.NewGuid();
        await _credentials.AddAsync(new Credential
        {
            Id = id, Name = "旧名", Type = CredentialType.LocalPassword, Username = "u",
            SecretReference = "password:local-ref",
        });

        var incoming = SyncSerializer.Serialize(
            new CredentialSyncSource.CredentialMetadata(
                "新名", CredentialType.LocalPassword, "u2", "", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            1);
        await source.ApplyAsync(SyncEntityTypes.Credential, id.ToString(), incoming, false, 1);

        var updated = await _credentials.GetByIdAsync(id);
        Assert.Equal("新名", updated!.Name);
        Assert.Equal("password:local-ref", updated.SecretReference); // 本机引用未被覆盖
    }
}
