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
/// 复现「两台设备显示已同步、数据却不一致」：一台干净设备拉取时，<c>connection</c> 的 Revision
/// 低于它引用的 <c>credential</c> / <c>group</c>（首次同步对账按 source 注册顺序入队，connection 先推、
/// Revision 更小）。旧逻辑按 Revision 顺序落地 connection → 外键失败 → 整轮 <see cref="SyncStatus.Error"/>、
/// 游标不进、每轮都失败。现在：整段收集 → 按依赖排序（connection 最后）→ 落不下去的两轮重试 → 收敛。
/// </summary>
public sealed class SyncDependencyOrderingTests : IDisposable
{
    private readonly InMemoryCloudServer _server = new();
    private readonly byte[] _masterKey = VaultCryptography.NewMasterKey();
    private static readonly Guid UserId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
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
    public async Task A_fresh_device_converges_even_when_connections_have_lower_revision_than_their_credential()
    {
        // 设备 A：在启用云同步「之前」建 group + credential + 引用它们的 connection（gate 关，不登记 Outbox）。
        var alice = NewDevice();
        alice.Gate.Enabled = false;
        var group = await alice.Groups.CreateAsync("Prod", parentId: null);
        var credential = await alice.Credentials.CreateAsync(
            new Credential { Name = "admin", Type = CredentialType.LocalPassword, Username = "root" },
            "s3cr3t", privateKey: null);
        var connection = await alice.Connections.CreateAsync(new ConnectionProfile
        {
            Name = "web-01", Host = "10.0.0.1", Port = 3389, Protocol = ProtocolType.Rdp,
            GroupId = group.Id, CredentialId = credential.Id,
        });

        // 启用后首次同步：ReconcileAsync 按 source 注册顺序入队 —— connection 在 credential / group 之前，
        // 于是 connection 拿到更小的 Revision。
        alice.Gate.Enabled = true;
        var push = await alice.RunOnceAsync();
        Assert.Equal(SyncStatus.Synced, push.Status);
        Assert.True(push.Pushed >= 3);

        // 服务端上 connection 的 Revision 确实低于它引用的 credential / group（对账入队顺序所致）。
        var page = _server.Pull(0, 100);
        long RevOf(string type, string id) =>
            page.Changes.Single(c => c.EntityType == type && c.EntityId == id).Revision;
        Assert.True(RevOf("connection", connection.Id.ToString()) < RevOf("credential", credential.Id.ToString()));
        Assert.True(RevOf("connection", connection.Id.ToString()) < RevOf("group", group.Id.ToString()));

        // 设备 B：干净，一次拉取就应收敛，不再外键失败 / 卡「同步出错」。
        var bob = NewDevice();
        var pull = await bob.RunOnceAsync();

        Assert.Equal(SyncStatus.Synced, pull.Status);
        var restored = await bob.ConnRepo.GetByIdAsync(connection.Id);
        Assert.NotNull(restored);
        Assert.Equal(credential.Id, restored!.CredentialId);
        Assert.Equal(group.Id, restored.GroupId);
        Assert.NotNull(await bob.CredRepo.GetByIdAsync(credential.Id));
        Assert.Contains(await bob.GroupRepo.GetAllAsync(), g => g.Id == group.Id);

        // 再跑一轮：不应有任何推送（对账稳定，不把刚拉取的当漂移）。
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
            coordinator, _masterKey, gate, connRepo, credRepo, groupRepo,
            new ConnectionService(connRepo, groupRepo, tagRepo, tracker),
            new CredentialService(credRepo, vault, NullLogger<CredentialService>.Instance, tracker),
            new GroupService(groupRepo, connRepo, tracker));
    }

    private sealed record Device(
        SyncCoordinator Coordinator, byte[] MasterKey, CloudSyncGate Gate,
        IConnectionRepository ConnRepo, ICredentialRepository CredRepo, IGroupRepository GroupRepo,
        ConnectionService Connections, CredentialService Credentials, GroupService Groups)
    {
        public Task<SyncRunResult> RunOnceAsync() =>
            Coordinator.RunOnceAsync(new SyncContext(UserId, AppId, 1, new VaultSession(MasterKey)));
    }
}
