using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Sync;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

public sealed class SyncCoordinatorTests : IDisposable
{
    private const string EntityType = "connection";
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string AppId = "com.appscloud.remoteflow";

    private readonly InMemoryCloudServer _server = new();
    private readonly byte[] _masterKey = VaultCryptography.NewMasterKey();
    private readonly List<TempWorkspace> _workspaces = [];
    private readonly TestClock _clock = new(DateTimeOffset.Parse("2026-09-09T12:00:00Z"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var workspace in _workspaces)
        {
            workspace.Dispose();
        }
    }

    [Fact]
    public async Task Push_drains_the_outbox_and_records_the_server_version()
    {
        var device = NewDevice();
        device.Source.SetLocal("c-1", "payload-1");
        await device.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 0);

        var result = await device.RunOnceAsync();

        Assert.Equal(1, result.Pushed);
        Assert.Equal(SyncStatus.Synced, result.Status);
        Assert.Equal(0, await device.Store.PendingCountAsync());
        Assert.Equal(1, await device.Store.GetServerVersionAsync(EntityType, "c-1"));
    }

    [Fact]
    public async Task Pull_applies_a_remote_change_and_advances_the_cursor()
    {
        var alice = NewDevice();
        alice.Source.SetLocal("c-1", "from-alice");
        await alice.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 0);
        await alice.RunOnceAsync();

        var bob = NewDevice();
        var result = await bob.RunOnceAsync();

        Assert.Equal(1, result.Pulled);
        Assert.True(result.Cursor > 0);
        Assert.Equal("from-alice", bob.Source.GetLocal("c-1"));
        Assert.Equal((long)1, await bob.Store.GetServerVersionAsync(EntityType, "c-1"));
    }

    [Fact]
    public async Task Tombstone_is_applied_as_a_local_delete()
    {
        var alice = NewDevice();
        alice.Source.SetLocal("c-1", "doomed");
        await alice.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 0);
        await alice.RunOnceAsync();
        await alice.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Delete, 0);
        alice.Source.RemoveLocal("c-1");
        await alice.RunOnceAsync();

        var bob = NewDevice();
        await bob.RunOnceAsync();

        Assert.Contains(bob.Source.Applied, a => a is { Id: "c-1", Deleted: true });
    }

    [Fact]
    public async Task A_stale_base_version_push_is_recorded_as_a_conflict()
    {
        var alice = NewDevice();
        alice.Source.SetLocal("c-1", "alice-v1");
        await alice.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 0);
        await alice.RunOnceAsync();
        alice.Source.SetLocal("c-1", "alice-v2");
        await alice.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 1);
        await alice.RunOnceAsync(); // server now at v2

        // Bob knows nothing of the server; enqueues a create for the same id
        var bob = NewDevice();
        bob.Source.SetLocal("c-1", "bob-parallel");
        await bob.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 0);

        var result = await bob.RunOnceAsync();

        Assert.Equal(1, result.PushConflicts);
        Assert.Equal(SyncStatus.Conflicted, result.Status);
        Assert.Equal(0, await bob.Store.PendingCountAsync());
        var conflict = Assert.Single(await bob.Conflicts.ListAsync());
        Assert.Equal(2, conflict.Remote.Version);
    }

    [Fact]
    public async Task A_local_pending_change_plus_an_incoming_remote_change_is_a_conflict()
    {
        var alice = NewDevice();
        alice.Source.SetLocal("c-1", "alice");
        await alice.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 0);
        await alice.RunOnceAsync();

        var bob = NewDevice();
        await bob.RunOnceAsync(); // bob now has c-1 @ v1

        // both edit c-1; alice pushes first
        alice.Source.SetLocal("c-1", "alice-edit");
        await alice.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 1);
        await alice.RunOnceAsync();

        bob.Source.SetLocal("c-1", "bob-edit");
        await bob.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 1);
        var result = await bob.RunOnceAsync();

        Assert.True(result.PushConflicts + result.PullConflicts >= 1);
        Assert.Equal(SyncStatus.Conflicted, result.Status);
        Assert.Single(await bob.Conflicts.ListAsync());
        Assert.Equal("bob-edit", bob.Source.GetLocal("c-1")); // 本地未被远端覆盖
    }

    [Fact]
    public async Task KeepLocal_resolution_requeues_and_the_next_push_wins()
    {
        var (bob, conflict) = await ArrangeConflictAsync();

        await bob.Conflicts.ResolveAsync(bob.Context(), conflict.Id, ConflictResolution.KeepLocal);
        var result = await bob.RunOnceAsync();

        Assert.Equal(1, result.Pushed);
        Assert.Empty(await bob.Conflicts.ListAsync());
        // 服务端现在拿到的是 bob 的内容
        var page = _server.Pull(0, 100);
        var latest = page.Changes.Last(c => c.EntityId == "c-1");
        Assert.Equal("bob-keeps-this", Decrypt(latest));
    }

    [Fact]
    public async Task UseRemote_resolution_applies_the_server_copy_and_clears_the_outbox()
    {
        var (bob, conflict) = await ArrangeConflictAsync();

        await bob.Conflicts.ResolveAsync(bob.Context(), conflict.Id, ConflictResolution.UseRemote);

        Assert.Equal("alice-wins", bob.Source.GetLocal("c-1"));
        Assert.Equal(0, await bob.Store.PendingCountAsync());
        Assert.Empty(await bob.Conflicts.ListAsync());
    }

    [Fact]
    public async Task A_transient_push_failure_schedules_a_backoff_retry()
    {
        var device = NewDevice();
        device.Source.SetLocal("c-1", "eventually");
        await device.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 0);
        device.Client.FailNextPushes = 1;

        var first = await device.RunOnceAsync();
        Assert.Equal(SyncStatus.Offline, first.Status);
        Assert.Equal(1, await device.Store.PendingCountAsync());

        // 未到重试时间前不再推
        var due = await device.Store.GetDueEntriesAsync(_clock.GetUtcNow(), 10);
        Assert.Empty(due);

        _clock.Advance(TimeSpan.FromSeconds(10));
        var second = await device.RunOnceAsync();
        Assert.Equal(1, second.Pushed);
        Assert.Equal(SyncStatus.Synced, second.Status);
    }

    [Fact]
    public async Task A_decryption_failure_stops_the_pull_without_advancing_the_cursor()
    {
        var alice = NewDevice();
        alice.Source.SetLocal("c-1", "fine");
        await alice.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 0);
        await alice.RunOnceAsync();
        _server.CorruptCiphertext(EntityType, "c-1");

        var bob = NewDevice();
        var result = await bob.RunOnceAsync();

        Assert.Equal(SyncStatus.Error, result.Status);
        Assert.Equal(0, (await bob.Store.GetStateAsync(AppId)).Cursor);
    }

    // ── 组装 ────────────────────────────────────────────────────

    private async Task<(Device Bob, SyncConflictRecord Conflict)> ArrangeConflictAsync()
    {
        var alice = NewDevice();
        alice.Source.SetLocal("c-1", "alice-wins");
        await alice.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 0);
        await alice.RunOnceAsync();

        var bob = NewDevice();
        bob.Source.SetLocal("c-1", "bob-keeps-this");
        await bob.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 0);
        await bob.RunOnceAsync();

        var conflict = Assert.Single(await bob.Conflicts.ListAsync());
        return (bob, conflict);
    }

    private string Decrypt(SyncPulledChange change)
    {
        var context = new PayloadContext(UserId, AppId, change.EntityType, change.EntityId, change.SchemaVersion, change.KeyVersion);
        using var session = new VaultSession(_masterKey);
        return Encoding.UTF8.GetString(session.Decrypt(context,
            new EncryptedPayload(change.Ciphertext!, change.Nonce!, change.KeyVersion, change.SchemaVersion)));
    }

    [Fact]
    public async Task Deleting_a_record_that_was_never_pushed_is_silent_and_leaves_no_conflict()
    {
        var device = NewDevice();
        device.Source.SetLocal("c-1", "never-uploaded");
        await device.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 0);
        // 推送前又删掉：Outbox 合并成 Delete(base 0) —— 云端没有这条，不该产生冲突。
        device.Source.RemoveLocal("c-1");
        await device.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Delete, 0);

        var result = await device.RunOnceAsync();

        Assert.Equal(SyncStatus.Synced, result.Status);
        Assert.Empty(await device.Conflicts.ListAsync());
        Assert.Equal(0, await device.Store.PendingCountAsync());
        Assert.Empty(_server.Pull(0, 100).Changes);
    }

    [Fact]
    public async Task Deleting_a_synced_record_propagates_a_tombstone_to_other_devices()
    {
        var alice = NewDevice();
        alice.Source.SetLocal("c-1", "doomed");
        await alice.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Upsert, 0);
        Assert.Equal(SyncStatus.Synced, (await alice.RunOnceAsync()).Status);

        // 本地删除 → 下一次同步应以 Delete 传播。
        alice.Source.RemoveLocal("c-1");
        await alice.Store.EnqueueAsync(EntityType, "c-1", OutboxOperationType.Delete, await alice.Store.GetServerVersionAsync(EntityType, "c-1"));

        var result = await alice.RunOnceAsync();

        Assert.Equal(SyncStatus.Synced, result.Status);
        Assert.Empty(await alice.Conflicts.ListAsync());
        var change = Assert.Single(_server.Pull(0, 100).Changes);
        Assert.True(change.Deleted);

        // 另一台设备拉到墓碑 → 本地同样删除，且「已同步条目」不再把它算进去。
        var bob = NewDevice();
        await bob.RunOnceAsync();
        Assert.Null(bob.Source.GetLocal("c-1"));
        Assert.Contains(bob.Source.Applied, a => a is { Id: "c-1", Deleted: true });
        Assert.Empty(await bob.Store.GetSyncedCountsAsync());   // 墓碑不计入已同步数
        Assert.Empty(await alice.Store.GetSyncedCountsAsync()); // 删除方同样不计入
    }

    private Device NewDevice()
    {
        var workspace = new TempWorkspace();
        _workspaces.Add(workspace);
        var database = new RemoteFlowDatabase(workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();

        var store = new SqliteSyncStore(database);
        var source = new FakeSyncEntitySource(EntityType);
        var client = new FakeCloudClient(_server);
        var conflicts = new ConflictService(store, [source], NullLogger<ConflictService>.Instance);
        var coordinator = new SyncCoordinator(
            client, store, [source], conflicts, NullLogger<SyncCoordinator>.Instance,
            new SyncOptions { PullBatchSize = 50, PushBatchSize = 50 }, _clock);
        return new Device(store, source, client, conflicts, coordinator, _masterKey);
    }

    private sealed record Device(
        SqliteSyncStore Store,
        FakeSyncEntitySource Source,
        FakeCloudClient Client,
        ConflictService Conflicts,
        SyncCoordinator Coordinator,
        byte[] MasterKey)
    {
        public SyncContext Context() => new(UserId, AppId, KeyVersion: 1, new VaultSession(MasterKey));

        public Task<SyncRunResult> RunOnceAsync() => Coordinator.RunOnceAsync(Context());
    }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
