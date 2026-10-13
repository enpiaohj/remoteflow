using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Sync;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

public sealed class SqliteSyncStoreTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly SqliteSyncStore _store;

    public SqliteSyncStoreTests()
    {
        var database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();
        _store = new SqliteSyncStore(database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task Enqueue_merges_repeated_changes_for_the_same_entity_and_bumps_the_sequence()
    {
        await _store.EnqueueAsync("connection", "c-1", OutboxOperationType.Upsert, 0);
        await _store.EnqueueAsync("connection", "c-1", OutboxOperationType.Upsert, 3);

        Assert.Equal(1, await _store.PendingCountAsync());
        var entry = Assert.Single(await _store.GetDueEntriesAsync(DateTimeOffset.UtcNow, 10));
        Assert.Equal(3, entry.BaseVersion);
        Assert.Equal(2, entry.Sequence);
    }

    [Fact]
    public async Task DeleteIfUnchanged_is_a_no_op_when_a_newer_local_change_bumped_the_sequence()
    {
        await _store.EnqueueAsync("connection", "c-1", OutboxOperationType.Upsert, 0);
        var captured = Assert.Single(await _store.GetDueEntriesAsync(DateTimeOffset.UtcNow, 10));

        // 推送在途时又发生一次本地写入
        await _store.EnqueueAsync("connection", "c-1", OutboxOperationType.Upsert, 0);

        Assert.False(await _store.DeleteIfUnchangedAsync(captured.Id, captured.Sequence));
        Assert.Equal(1, await _store.PendingCountAsync());
    }

    [Fact]
    public async Task Cursor_and_status_round_trip()
    {
        await _store.SetCursorAsync("app", 42);
        await _store.SetStatusAsync("app", SyncStatus.Conflicted, attemptAt: null,
            successAt: DateTimeOffset.Parse("2026-09-09T10:00:00Z"));

        var state = await _store.GetStateAsync("app");

        Assert.Equal(42, state.Cursor);
        Assert.Equal(SyncStatus.Conflicted, state.Status);
        Assert.NotNull(state.LastSuccessfulSyncAt);
    }

    [Fact]
    public async Task Recording_a_conflict_twice_keeps_only_the_latest_unresolved_row()
    {
        var remote = new SyncServerEntity("connection", "c-1", 5, 100, 1, 1, false, [1, 2, 3], [4, 5, 6]);
        await _store.RecordConflictAsync("connection", "c-1", null, remote);
        await _store.RecordConflictAsync("connection", "c-1", null, remote with { Version = 6 });

        var conflict = Assert.Single(await _store.ListUnresolvedAsync());
        Assert.Equal(6, conflict.Remote.Version);
    }
}
