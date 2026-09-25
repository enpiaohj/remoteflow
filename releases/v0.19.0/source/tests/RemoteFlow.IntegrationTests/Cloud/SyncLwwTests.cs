using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Sync;
using RemoteFlow.Infrastructure.Sync.Sources;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>
/// Last-Writer-Wins 冲突自动解决：connection / credential 元数据同时被两台设备修改时，
/// 谁的内容更新时间（UpdatedAt）更晚听谁的，不再弹窗；密码（credential-secret）仍走对话框。
/// </summary>
public sealed class SyncLwwTests : IDisposable
{
    private readonly InMemoryCloudServer _server = new();
    private readonly byte[] _masterKey = VaultCryptography.NewMasterKey();
    private static readonly Guid UserId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private const string AppId = "com.appscloud.remoteflow";
    private readonly List<TempWorkspace> _workspaces = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var w in _workspaces)
        {
            w.Dispose();
        }
    }

    [Fact]
    public async Task Connection_edited_on_both_devices_the_later_write_wins_without_a_dialog()
    {
        // 共享一条 connection v1。
        var alice = NewDevice();
        var conn = await alice.Connections.CreateAsync(new ConnectionProfile
        {
            Name = "web-01", Host = "10.0.0.1", Port = 3389, Protocol = ProtocolType.Rdp,
        });
        Assert.Equal(SyncStatus.Synced, (await alice.RunOnceAsync()).Status);

        var bob = NewDevice();
        Assert.Equal(SyncStatus.Synced, (await bob.RunOnceAsync()).Status);

        // 两台各改 notes：Bob 先（较旧），Alice 后（较新，最后一笔）。
        var bobCopy = await bob.ConnRepo.GetByIdAsync(conn.Id);
        bobCopy!.Notes = "bob 备注（较早）";
        await bob.Connections.UpdateAsync(bobCopy);
        await Task.Delay(40); // 保证两条 UpdatedAt 严格递增
        var aliceCopy = await alice.ConnRepo.GetByIdAsync(conn.Id);
        aliceCopy!.Notes = "alice 备注（较新）";
        await alice.Connections.UpdateAsync(aliceCopy);

        // Alice 先同步（服务端 = alice 较新那笔），Bob 后同步：冲突按 LWW 采纳 alice，不弹窗。
        Assert.Equal(SyncStatus.Synced, (await alice.RunOnceAsync()).Status);
        Assert.Equal(SyncStatus.Synced, (await bob.RunOnceAsync()).Status);

        Assert.Empty(await bob.Conflicts.ListAsync()); // 没有遗留的用户冲突
        Assert.Equal(0, await bob.Store.PendingCountAsync());
        var finalBob = await bob.ConnRepo.GetByIdAsync(conn.Id);
        Assert.Equal("alice 备注（较新）", finalBob!.Notes);

        // 云端最终也是 alice 那笔。
        Assert.Equal("alice 备注（较新）", (await ReadCloudAsync(conn.Id.ToString()))!.Notes);
    }

    [Fact]
    public async Task Connection_local_edit_newer_than_the_cloud_re_pushes_and_wins()
    {
        var alice = NewDevice();
        var conn = await alice.Connections.CreateAsync(new ConnectionProfile
        {
            Name = "web-01", Host = "10.0.0.1", Port = 3389, Protocol = ProtocolType.Rdp,
        });
        Assert.Equal(SyncStatus.Synced, (await alice.RunOnceAsync()).Status);
        var bob = NewDevice();
        Assert.Equal(SyncStatus.Synced, (await bob.RunOnceAsync()).Status);

        // Alice 先改并推上去（较旧那笔已上云）。
        var a = await alice.ConnRepo.GetByIdAsync(conn.Id);
        a!.Notes = "alice 较早";
        await alice.Connections.UpdateAsync(a);
        Assert.Equal(SyncStatus.Synced, (await alice.RunOnceAsync()).Status);

        // Bob 后改（较新），离线期间没推过。
        await Task.Delay(40);
        var b = await bob.ConnRepo.GetByIdAsync(conn.Id);
        b!.Notes = "bob 较新";
        await bob.Connections.UpdateAsync(b);

        // Bob 同步：冲突按 LWW 本地较新 → 顶上去覆盖，不弹窗。
        Assert.Equal(SyncStatus.Synced, (await bob.RunOnceAsync()).Status);
        Assert.Empty(await bob.Conflicts.ListAsync());
        Assert.Equal(0, await bob.Store.PendingCountAsync());

        // Alice 下一轮拉到 bob 的版本 → 收敛。
        Assert.Equal(SyncStatus.Synced, (await alice.RunOnceAsync()).Status);
        Assert.Equal("bob 较新", (await alice.ConnRepo.GetByIdAsync(conn.Id))!.Notes);
    }

    [Fact]
    public async Task Password_changed_on_both_devices_still_surfaces_a_conflict()
    {
        var alice = NewDevice();
        var credential = await alice.Credentials.CreateAsync(
            new Credential { Name = "sa", Type = CredentialType.LocalPassword, Username = "root" },
            "pass-v1", privateKey: null);
        Assert.Equal(SyncStatus.Synced, (await alice.RunOnceAsync()).Status);

        var bob = NewDevice();
        Assert.Equal(SyncStatus.Synced, (await bob.RunOnceAsync()).Status);

        // Bob 先改密码并上云；Alice 后改但还没推 —— 一旦推送必然冲突。
        var bobCred = await bob.CredRepo.GetByIdAsync(credential.Id);
        bobCred!.Name = "sa";
        await bob.Credentials.UpdateAsync(bobCred, "pass-bob", privateKey: null);
        Assert.Equal(SyncStatus.Synced, (await bob.RunOnceAsync()).Status);

        var aliceCred = await alice.CredRepo.GetByIdAsync(credential.Id);
        aliceCred!.Name = "sa";
        await alice.Credentials.UpdateAsync(aliceCred, "pass-alice", privateKey: null);
        // 密码冲突必须留给用户选择 —— 不会像元数据那样被自动覆盖。
        Assert.Equal(SyncStatus.Conflicted, (await alice.RunOnceAsync()).Status);
        Assert.NotEmpty(await alice.Conflicts.ListAsync());
    }

    private async Task<ConnectionProfile?> ReadCloudAsync(string connectionId)
    {
        var page = _server.Pull(0, 500);
        var change = page.Changes.Single(c => c.EntityType == "connection" && c.EntityId == connectionId);
        if (change.Deleted)
        {
            return null;
        }

        using var session = new VaultSession(_masterKey);
        var ctx = new PayloadContext(UserId, AppId, "connection", change.EntityId, change.SchemaVersion, change.KeyVersion);
        var plaintext = session.Decrypt(
            ctx, new EncryptedPayload(change.Ciphertext!, change.Nonce!, change.KeyVersion, change.SchemaVersion));
        var (_, profile) = SyncSerializer.Deserialize<ConnectionProfile>(plaintext);
        return profile;
    }

    private Device NewDevice()
    {
        var workspace = new TempWorkspace();
        _workspaces.Add(workspace);
        var database = new RemoteFlowDatabase(workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();

        var connRepo = new SqliteConnectionRepository(database);
        var credRepo = new SqliteCredentialRepository(database);
        var groupRepo = new SqliteGroupRepository(database);
        var tagRepo = new SqliteTagRepository(database);
        var vault = new InMemoryCredentialVault();
        var store = new SqliteSyncStore(database);

        ISyncEntitySource[] sources =
        [
            new ConnectionSyncSource(connRepo),
            new CredentialSyncSource(credRepo),
            new CredentialSecretSyncSource(credRepo, vault),
            new GroupSyncSource(groupRepo),
            new TagSyncSource(tagRepo),
        ];
        var client = new FakeCloudClient(_server);
        var conflicts = new ConflictService(store, sources, NullLogger<ConflictService>.Instance);
        var coordinator = new SyncCoordinator(
            client, store, sources, conflicts, NullLogger<SyncCoordinator>.Instance,
            new SyncOptions { PullBatchSize = 100, PushBatchSize = 100 });

        var gate = new CloudSyncGate { Enabled = true };
        var tracker = new OutboxSyncChangeTracker(store, gate, NullLogger<OutboxSyncChangeTracker>.Instance);

        return new Device(
            store, _masterKey, connRepo, credRepo, groupRepo, conflicts, coordinator,
            new ConnectionService(connRepo, groupRepo, tagRepo, tracker),
            new CredentialService(credRepo, vault, NullLogger<CredentialService>.Instance, tracker));
    }

    private sealed record Device(
        SqliteSyncStore Store, byte[] MasterKey, IConnectionRepository ConnRepo,
        ICredentialRepository CredRepo, IGroupRepository GroupRepo, ConflictService Conflicts,
        SyncCoordinator Coordinator, ConnectionService Connections, CredentialService Credentials)
    {
        public Task<SyncRunResult> RunOnceAsync() =>
            Coordinator.RunOnceAsync(new SyncContext(UserId, AppId, 1, new VaultSession(MasterKey)));
    }
}
