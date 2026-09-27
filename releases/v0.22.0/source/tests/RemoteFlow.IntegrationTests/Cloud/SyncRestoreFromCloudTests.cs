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
/// 「清除本地数据并从云端恢复」：LocalDataWiper 清空本机业务数据 + 同步状态（游标归 0），
/// 下一次同步把云端作为权威完整拉回 —— 本机独有、从未上云的改动被丢弃。
/// </summary>
public sealed class SyncRestoreFromCloudTests : IDisposable
{
    private readonly InMemoryCloudServer _server = new();
    private readonly byte[] _masterKey = VaultCryptography.NewMasterKey();
    private static readonly Guid UserId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
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
    public async Task Wipe_then_sync_restores_the_cloud_copy_and_discards_local_only_data()
    {
        var alice = NewDevice();
        var aliceCredential = await alice.Credentials.CreateAsync(
            new Credential { Name = "admin", Type = CredentialType.LocalPassword, Username = "root" },
            "cloud-secret", privateKey: null);
        var aliceConnection = await alice.Connections.CreateAsync(new ConnectionProfile
        {
            Name = "web-01", Host = "10.0.0.1", Port = 3389, Protocol = ProtocolType.Rdp,
            CredentialId = aliceCredential.Id,
        });
        Assert.Equal(SyncStatus.Synced, (await alice.RunOnceAsync()).Status);

        // Bob 同步到云端数据。
        var bob = NewDevice();
        Assert.Equal(SyncStatus.Synced, (await bob.RunOnceAsync()).Status);
        Assert.NotNull(await bob.CredRepo.GetByIdAsync(aliceCredential.Id));
        Assert.NotNull(await bob.ConnRepo.GetByIdAsync(aliceConnection.Id));

        // Bob 造一条本地独有、从未上云的凭据 + 连接。
        bob.Gate.Enabled = false;
        var localCredential = await bob.Credentials.CreateAsync(
            new Credential { Name = "local-only", Type = CredentialType.LocalPassword, Username = "x" },
            "local-secret", privateKey: null);
        var localConnection = await bob.Connections.CreateAsync(new ConnectionProfile
        {
            Name = "scratch", Host = "198.51.100.9", Port = 22, Protocol = ProtocolType.Ssh,
            CredentialId = localCredential.Id,
        });
        bob.Gate.Enabled = true;

        // 兜底：清除本地数据并从云端恢复。
        await bob.Wiper.WipeLocalDataForCloudRestoreAsync();
        Assert.Empty(await bob.CredRepo.GetAllAsync());
        Assert.Empty(await bob.ConnRepo.GetAllAsync());
        // 非系统分组全清；系统「未分组」保留（本机概念，不入库到云端）。
        var groups = await bob.GroupRepo.GetAllAsync();
        Assert.DoesNotContain(groups, g => !g.IsSystem);
        Assert.Contains(groups, g => g.Id == ConnectionGroup.UngroupedId);
        Assert.Equal(0, (await bob.Store.GetStateAsync(AppId)).Cursor); // 同步状态清空 → 游标归 0
        Assert.Equal(0, await bob.Store.PendingCountAsync());

        var run = await bob.RunOnceAsync();
        Assert.Equal(SyncStatus.Synced, run.Status);

        // 云端数据回来了；本地独有数据没了。
        Assert.NotNull(await bob.CredRepo.GetByIdAsync(aliceCredential.Id));
        Assert.NotNull(await bob.ConnRepo.GetByIdAsync(aliceConnection.Id));
        var restoredCredential = await bob.CredRepo.GetByIdAsync(aliceCredential.Id);
        Assert.Equal("cloud-secret", await bob.Vault.RetrieveSecretAsync(restoredCredential!.SecretReference!));
        Assert.Null(await bob.CredRepo.GetByIdAsync(localCredential.Id));
        Assert.Null(await bob.ConnRepo.GetByIdAsync(localConnection.Id));
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
        var wiper = new LocalDataWiper(database, vault, NullLogger<LocalDataWiper>.Instance);

        return new Device(
            store, gate, vault, _masterKey, connRepo, credRepo, groupRepo, coordinator, wiper,
            new ConnectionService(connRepo, groupRepo, tagRepo, tracker),
            new CredentialService(credRepo, vault, NullLogger<CredentialService>.Instance, tracker),
            new GroupService(groupRepo, connRepo, tracker));
    }

    private sealed record Device(
        SqliteSyncStore Store, CloudSyncGate Gate, InMemoryCredentialVault Vault, byte[] MasterKey,
        IConnectionRepository ConnRepo, ICredentialRepository CredRepo, IGroupRepository GroupRepo,
        SyncCoordinator Coordinator, LocalDataWiper Wiper,
        ConnectionService Connections, CredentialService Credentials, GroupService Groups)
    {
        public Task<SyncRunResult> RunOnceAsync() =>
            Coordinator.RunOnceAsync(new SyncContext(UserId, AppId, 1, new VaultSession(MasterKey)));
    }
}
