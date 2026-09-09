using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Security;
using RemoteFlow.Infrastructure.Sync;
using Xunit;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>
/// 复现「第二台设备一直同步出错」：服务端按 Revision 排序，credential-secret 的 Revision
/// 可能低于其 credential（父实体被后续更新推高）。拉取时 secret 先到、父实体还没落地 →
/// 旧逻辑 `ApplyAsync` 直接抛 `InvalidOperationException` → 整轮 `SyncStatus.Error`、游标不推进、死循环。
/// 现在改为延后重试。
/// </summary>
public sealed class SyncDependencyOrderingTests : IDisposable
{
    private const string ParentType = "credential";
    private const string ChildType = SyncEntityTypes.CredentialSecret;
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string AppId = "com.appscloud.remoteflow";

    private readonly InMemoryCloudServer _server = new();
    private readonly byte[] _masterKey = VaultCryptography.NewMasterKey();
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
    public async Task Child_that_arrives_before_its_parent_is_deferred_and_retried_not_fatal()
    {
        const string id = "cred-1";

        // 设备 A：先建父 + 子，再更新父 —— 父的 Revision 被推到子之上（真实场景里 credential
        // 元数据被另一台改过、secret 没动就是这样）。
        var alice = NewDevice();
        alice.Parent.SetLocal(id, "parent-v1");
        alice.Child.SetLocal(id, "secret-v1");
        await alice.Store.EnqueueAsync(ChildType, id, OutboxOperationType.Upsert, 0);
        await alice.Store.EnqueueAsync(ParentType, id, OutboxOperationType.Upsert, 0);
        await alice.RunOnceAsync();
        alice.Parent.SetLocal(id, "parent-v2");
        await alice.Store.EnqueueAsync(ParentType, id, OutboxOperationType.Upsert, 1);
        await alice.RunOnceAsync();

        // 设备 B：干净，一次拉取。子实体 Revision 低、先到，父还没落地。
        var bob = NewDevice();
        var result = await bob.RunOnceAsync();

        Assert.Equal(SyncStatus.Synced, result.Status);
        Assert.True(result.Cursor > 0);
        Assert.Equal("parent-v2", bob.Parent.GetLocal(id));
        Assert.Equal("secret-v1", bob.Child.GetLocal(id));   // 延后重试后成功落地
        Assert.Equal(0, await bob.Store.PendingCountAsync());

        // 再跑一轮不应再产生推送（对账用落地后内容算哈希，不把正常拉取当漂移）。
        var again = await bob.RunOnceAsync();
        Assert.Equal(0, again.Pushed);
        Assert.Equal(SyncStatus.Synced, again.Status);
    }

    private Device NewDevice()
    {
        var workspace = new TempWorkspace();
        _workspaces.Add(workspace);
        var database = new RemoteFlowDatabase(workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();

        var store = new SqliteSyncStore(database);
        var parent = new FakeSyncEntitySource(ParentType);
        var child = new DependentFakeSource(ChildType, parent);
        var client = new FakeCloudClient(_server);
        var conflicts = new ConflictService(store, [parent, child], NullLogger<ConflictService>.Instance);
        var coordinator = new SyncCoordinator(
            client, store, [parent, child], conflicts, NullLogger<SyncCoordinator>.Instance,
            new SyncOptions { PullBatchSize = 50, PushBatchSize = 50 });
        return new Device(store, parent, child, coordinator, _masterKey);
    }

    private sealed record Device(
        SqliteSyncStore Store, FakeSyncEntitySource Parent, DependentFakeSource Child,
        SyncCoordinator Coordinator, byte[] MasterKey)
    {
        public Task<SyncRunResult> RunOnceAsync() =>
            Coordinator.RunOnceAsync(new SyncContext(UserId, AppId, 1, new VaultSession(MasterKey)));
    }

    /// <summary>父实体不在时抛 <see cref="SyncDependencyNotReadyException"/>，模拟 CredentialSecretSyncSource。</summary>
    private sealed class DependentFakeSource(string entityType, FakeSyncEntitySource parent) : ISyncEntitySource
    {
        private readonly Dictionary<string, string> _local = [];

        public int SchemaVersion => 1;
        public bool Handles(string type) => type == entityType;
        public IReadOnlyList<string> EntityTypes => [entityType];

        public Task<IReadOnlyList<string>> ListEntityIdsAsync(string type, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([.. _local.Keys]);

        public string? GetLocal(string id) => _local.GetValueOrDefault(id);

        public void SetLocal(string id, string plaintext) => _local[id] = plaintext;

        public Task<byte[]?> GetPlaintextAsync(string type, string id, CancellationToken ct = default) =>
            Task.FromResult(_local.TryGetValue(id, out var t) ? Encoding.UTF8.GetBytes(t) : null);

        public Task ApplyAsync(
            string type, string id, byte[]? plaintext, bool deleted, int schemaVersion, CancellationToken ct = default)
        {
            if (!deleted && parent.GetLocal(id) is null)
            {
                throw new SyncDependencyNotReadyException(entityType, id, "credential");
            }

            if (deleted)
            {
                _local.Remove(id);
            }
            else if (plaintext is not null)
            {
                _local[id] = Encoding.UTF8.GetString(plaintext);
            }

            return Task.CompletedTask;
        }
    }
}
