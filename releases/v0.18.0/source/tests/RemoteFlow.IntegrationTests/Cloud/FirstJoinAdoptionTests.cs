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
/// 首次加入消重：两台机器在启用云同步前各自建过「同一台服务器 / 同一个分组 / 标签 / 凭据」
/// （不同 Id），第二台首次接入时应按自然键认领云端 Id，而不是把重复条目推上去。
/// </summary>
public sealed class FirstJoinAdoptionTests : IDisposable
{
    private readonly InMemoryCloudServer _server = new();
    private static readonly Guid UserId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    private const string AppId = "com.appscloud.remoteflow";
    private readonly List<TempWorkspace> _workspaces = [];
    private readonly List<string> logs = [];
    private readonly byte[] _masterKey = VaultCryptography.NewMasterKey();   // 同一个 Vault：各设备共享主密钥

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var w in _workspaces)
        {
            w.Dispose();
        }
    }

    [Fact]
    public async Task Joining_device_claims_the_cloud_ids_instead_of_pushing_duplicates()
    {
        // 设备 A：建 tag / group / credential / connection 并同步上云。
        var alice = NewDevice();
        var tag = await alice.Tags.CreateAsync("prod", "#0F6CBD", "");
        var group = await alice.Groups.CreateAsync("生产环境", parentId: null);
        var credential = await alice.Credentials.CreateAsync(
            new Credential { Name = "sa", Type = CredentialType.LocalPassword, Username = "root" }, "pw", null);
        var connection = await alice.Connections.CreateAsync(new ConnectionProfile
        {
            Name = "web-01", Host = "10.0.0.1", Port = 3389, Protocol = ProtocolType.Rdp,
            GroupId = group.Id, CredentialId = credential.Id, TagIds = [tag.Id],
        });
        Assert.Equal(SyncStatus.Synced, (await alice.RunOnceAsync()).Status);
        Assert.Equal(5, await alice.SyncedEntityCountAsync());   // 分组 / 标签 / 凭据 / 密码 / 连接

        // 设备 B：在**启用云同步之前**建了同样的东西（同名同 host，但 Id 不同）。
        var bob = NewDevice();
        bob.Gate.Enabled = false;
        var bobTag = await bob.Tags.CreateAsync("prod", "#0F6CBD", "");
        var bobGroup = await bob.Groups.CreateAsync("生产环境", parentId: null);
        var bobCredential = await bob.Credentials.CreateAsync(
            new Credential { Name = "sa", Type = CredentialType.LocalPassword, Username = "root" }, "pw", null);
        await bob.Connections.CreateAsync(new ConnectionProfile
        {
            Name = "web-01", Host = "10.0.0.1", Port = 3389, Protocol = ProtocolType.Rdp,
            GroupId = bobGroup.Id, CredentialId = bobCredential.Id, TagIds = [bobTag.Id],
        });
        bob.Gate.Enabled = true;

        var result = await bob.RunOnceAsync();

        // 首次加入：认领云端 Id，本地重复行被合并掉，且**没有**把重复的推上云。
        var counts = string.Join(", ", (await bob.Store.GetSyncedCountsAsync()).Select(x => $"{x.EntityType}={x.Count}"));
        var cloud = string.Join(", ", _server.Pull(0, 500).Changes
            .Select(x => x.EntityType + ":" + x.EntityId[..8] + "@v" + x.Version));
        var detail = "pushed=" + result.Pushed + "; counts=" + counts
            + Environment.NewLine + "cloud=" + cloud
            + Environment.NewLine + "localConn=" + string.Join(",", (await bob.ConnRepo.GetAllAsync()).Select(c => c.Id.ToString()[..8]))
            + " localCred=" + string.Join(",", (await bob.CredRepo.GetAllAsync()).Select(c => c.Id.ToString()[..8]))
            + " localTag=" + string.Join(",", (await bob.TagRepo.GetAllAsync()).Select(t => t.Id.ToString()[..8]));
        Assert.True(result.Pushed == 0, detail);
        Assert.Equal(SyncStatus.Synced, result.Status);
        Assert.Equal(5, await bob.SyncedEntityCountAsync());          // 仍是云端那 5 条，而不是 10 条
        Assert.Null(await bob.ConnRepo.GetByIdAsync(bobGroup.Id));
        Assert.Null(await bob.ConnRepo.GetByIdAsync(bobCredential.Id));
        Assert.DoesNotContain(await bob.TagRepo.GetAllAsync(), t => t.Id == bobTag.Id);

        // 连接改认到云端那份，且引用关系（分组 / 凭据 / 标签）指向云端 Id。
        var restored = await bob.ConnRepo.GetByIdAsync(connection.Id);
        Assert.NotNull(restored);
        Assert.Equal(group.Id, restored!.GroupId);
        Assert.Equal(credential.Id, restored.CredentialId);
        Assert.Equal(new[] { tag.Id }, restored.TagIds);

        // 云端只有一份，没有重复。
        Assert.Equal(5, _server.Pull(0, 500).Changes.Count);
    }

    [Fact]
    public async Task A_local_only_entry_is_kept_and_pushed_as_usual()
    {
        var alice = NewDevice();
        await alice.Connections.CreateAsync(new ConnectionProfile
        {
            Name = "keep-me", Host = "10.10.10.10", Port = 22, Protocol = ProtocolType.Ssh,
        });
        Assert.Equal(SyncStatus.Synced, (await alice.RunOnceAsync()).Status);

        // 另一台只有一条**云端没有**的记录：不该被合并，应正常推上去。
        var bob = NewDevice();
        bob.Gate.Enabled = false;
        var onlyLocal = await bob.Connections.CreateAsync(new ConnectionProfile
        {
            Name = "only-local", Host = "10.20.20.20", Port = 22, Protocol = ProtocolType.Ssh,
        });
        bob.Gate.Enabled = true;

        var result = await bob.RunOnceAsync();

        Assert.True(result.Status == SyncStatus.Synced, string.Join(Environment.NewLine, logs));
        Assert.True(result.Pushed >= 1);
        Assert.NotNull(await bob.ConnRepo.GetByIdAsync(onlyLocal.Id));
        Assert.Equal(2, _server.Pull(0, 500).Changes.Count);
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

        var sources = new ISyncEntitySource[]
        {
            new ConnectionSyncSource(connRepo),
            new CredentialSyncSource(credRepo),
            new CredentialSecretSyncSource(credRepo, vault),
            new GroupSyncSource(groupRepo),
            new TagSyncSource(tagRepo),
        };
        var client = new FakeCloudClient(_server);
        var conflicts = new ConflictService(store, sources, NullLogger<ConflictService>.Instance);
        var adopter = new CloudFirstJoinAdopter(database, vault, NullLogger<CloudFirstJoinAdopter>.Instance);
        var coordinator = new SyncCoordinator(
            client, store, sources, conflicts, new CapturingLogger(logs),
            new SyncOptions { PullBatchSize = 100, PushBatchSize = 100 }, timeProvider: null,
            firstJoinAdopter: adopter);

        var gate = new CloudSyncGate { Enabled = true };
        var tracker = new OutboxSyncChangeTracker(store, gate, NullLogger<OutboxSyncChangeTracker>.Instance);

        return new Device(
            store, connRepo, credRepo, tagRepo, gate, coordinator, adopter, _masterKey,
            new ConnectionService(connRepo, groupRepo, tagRepo, tracker),
            new CredentialService(credRepo, vault, NullLogger<CredentialService>.Instance, tracker),
            new GroupService(groupRepo, connRepo, tracker),
            new TagServiceAdapter(tagRepo));
    }

    private sealed record Device(
        SqliteSyncStore Store, IConnectionRepository ConnRepo, ICredentialRepository CredRepo,
        ITagRepository TagRepo, CloudSyncGate Gate, SyncCoordinator Coordinator, CloudFirstJoinAdopter Adopter,
        byte[] MasterKey, ConnectionService Connections, CredentialService Credentials, GroupService Groups,
        TagServiceAdapter Tags)
    {
        public Task<SyncRunResult> RunOnceAsync() =>
            Coordinator.RunOnceAsync(new SyncContext(UserId, AppId, 1, new VaultSession(MasterKey)));

        /// <summary>本机已同步（server_version&gt;0）的实体数 —— 重复条目若被合并，这个数不会翻倍。</summary>
        public async Task<int> SyncedEntityCountAsync() =>
            (await Store.GetSyncedCountsAsync()).Sum(x => x.Count);
    }

    /// <summary>把 coordinator 的日志收进测试，失败时一并打印，便于定位被兜底 catch 吞掉的异常。</summary>
    private sealed class CapturingLogger(List<string> sink) : Microsoft.Extensions.Logging.ILogger<SyncCoordinator>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => sink.Add($"[{logLevel}] {formatter(state, exception)} {exception}");
    }

    /// <summary>标签的新增走 ConnectionService（应用层未单列 TagService）——这里包一层便于测试可读。</summary>
    private sealed class TagServiceAdapter(ITagRepository tags)
    {
        public async Task<Tag> CreateAsync(string name, string color, string description)
        {
            var tag = new Tag { Name = name, Color = color, Description = description };
            await tags.AddAsync(tag);
            return tag;
        }
    }
}
