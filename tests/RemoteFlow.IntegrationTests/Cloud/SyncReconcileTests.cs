using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Sync;
using RemoteFlow.Infrastructure.Sync.Sources;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

public sealed class SyncReconcileTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string AppId = "com.appscloud.remoteflow";

    private readonly TempWorkspace _workspace = new();
    private readonly RemoteFlowDatabase _database;
    private readonly SqliteConnectionRepository _connections;
    private readonly SqliteSyncStore _store;
    private readonly byte[] _masterKey = VaultCryptography.NewMasterKey();

    public SyncReconcileTests()
    {
        _database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        _database.Initialize();
        _connections = new SqliteConnectionRepository(_database);
        _store = new SqliteSyncStore(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task Change_tracker_wired_into_ConnectionService_enqueues_the_outbox()
    {
        var tracker = new OutboxSyncChangeTracker(
            _store, new CloudSyncGate { Enabled = true }, NullLogger<OutboxSyncChangeTracker>.Instance);
        var service = new ConnectionService(_connections, new SqliteGroupRepository(_database),
            new SqliteTagRepository(_database), tracker);

        var profile = await service.CreateAsync(new ConnectionProfile
        {
            Name = "DC01", Host = "10.0.0.1", Port = 22, Protocol = ProtocolType.Ssh,
        });

        Assert.Equal(1, await _store.PendingCountAsync());
        Assert.True(await _store.HasPendingAsync(SyncEntityTypes.Connection, profile.Id.ToString()));
    }

    [Fact]
    public async Task Reconcile_enqueues_an_untracked_local_change()
    {
        var coordinator = NewCoordinator();
        // 直接写仓储（模拟「业务写已提交，Outbox 未入队」的崩溃窗口）
        var profile = new ConnectionProfile { Name = "X", Host = "h", Port = 22, Protocol = ProtocolType.Ssh };
        await _connections.AddAsync(profile);

        await coordinator.ReconcileAsync(Context());

        Assert.True(await _store.HasPendingAsync(SyncEntityTypes.Connection, profile.Id.ToString()));
    }

    [Fact]
    public async Task Reconcile_does_not_re_enqueue_an_unchanged_entity()
    {
        var server = new InMemoryCloudServer();
        var coordinator = NewCoordinator(server);
        var profile = new ConnectionProfile { Name = "X", Host = "h", Port = 22, Protocol = ProtocolType.Ssh };
        await _connections.AddAsync(profile);
        await _store.EnqueueAsync(SyncEntityTypes.Connection, profile.Id.ToString(), OutboxOperationType.Upsert, 0);

        await coordinator.RunOnceAsync(Context()); // pushes it, records content hash
        Assert.Equal(0, await _store.PendingCountAsync());

        await coordinator.ReconcileAsync(Context()); // 内容未变

        Assert.Equal(0, await _store.PendingCountAsync());
    }

    [Fact]
    public async Task Reconcile_enqueues_a_delete_for_a_synced_entity_that_vanished_locally()
    {
        var coordinator = NewCoordinator();
        var profile = new ConnectionProfile { Name = "X", Host = "h", Port = 22, Protocol = ProtocolType.Ssh };
        await _connections.AddAsync(profile);
        await _store.EnqueueAsync(SyncEntityTypes.Connection, profile.Id.ToString(), OutboxOperationType.Upsert, 0);
        await coordinator.RunOnceAsync(Context());

        await _connections.DeleteAsync(profile.Id); // untracked local delete

        await coordinator.ReconcileAsync(Context());

        var entry = Assert.Single(await _store.GetDueEntriesAsync(DateTimeOffset.UtcNow, 10));
        Assert.Equal(OutboxOperationType.Delete, entry.OperationType);
    }

    private SyncCoordinator NewCoordinator(InMemoryCloudServer? server = null)
    {
        var source = new ConnectionSyncSource(_connections);
        var client = new FakeCloudClient(server ?? new InMemoryCloudServer());
        var conflicts = new ConflictService(_store, [source], NullLogger<ConflictService>.Instance);
        return new SyncCoordinator(
            client, _store, [source], conflicts, NullLogger<SyncCoordinator>.Instance);
    }

    private SyncContext Context() => new(UserId, AppId, KeyVersion: 1, new VaultSession(_masterKey));
}
