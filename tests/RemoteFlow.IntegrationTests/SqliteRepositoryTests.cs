using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Data;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// SQLite 仓储的端到端验证：真实建库、迁移、增删改查、关联表级联。
/// </summary>
public sealed class SqliteRepositoryTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly RemoteFlowDatabase _database;

    public SqliteRepositoryTests()
    {
        _database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        _database.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public void 初始化后Schema版本为当前版本()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";

        Assert.Equal(RemoteFlowDatabase.CurrentSchemaVersion, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void 重复初始化是幂等的()
    {
        _database.Initialize();
        _database.Initialize();

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='connections';";

        Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public async Task 连接的增查改删往返一致()
    {
        var repo = new SqliteConnectionRepository(_database);

        var profile = new ConnectionProfile
        {
            Name = "DC01",
            Host = "10.10.1.10",
            Port = 3389,
            Protocol = ProtocolType.Rdp,
            Notes = "主域控制器",
            Favorite = true
        };
        profile.Rdp.RedirectClipboard = true;
        profile.Rdp.Domain = "CORP";

        await repo.AddAsync(profile);

        var loaded = await repo.GetByIdAsync(profile.Id);
        Assert.NotNull(loaded);
        Assert.Equal("DC01", loaded!.Name);
        Assert.Equal("10.10.1.10", loaded.Host);
        Assert.Equal(ProtocolType.Rdp, loaded.Protocol);
        Assert.True(loaded.Favorite);
        // 协议参数以 JSON 存 options_json 列，需正确往返
        Assert.True(loaded.Rdp.RedirectClipboard);
        Assert.Equal("CORP", loaded.Rdp.Domain);

        loaded.Name = "DC01-renamed";
        loaded.Favorite = false;
        await repo.UpdateAsync(loaded);

        var updated = await repo.GetByIdAsync(profile.Id);
        Assert.Equal("DC01-renamed", updated!.Name);
        Assert.False(updated.Favorite);

        await repo.DeleteAsync(profile.Id);
        Assert.Null(await repo.GetByIdAsync(profile.Id));
    }

    [Fact]
    public async Task 删除分组不会删除组内连接而是迁移它们()
    {
        var groups = new SqliteGroupRepository(_database);
        var connections = new SqliteConnectionRepository(_database);

        var group = new ConnectionGroup { Name = "临时分组" };
        await groups.AddAsync(group);

        var profile = new ConnectionProfile
        {
            Name = "srv-a",
            Host = "10.0.0.1",
            Port = 22,
            Protocol = ProtocolType.Ssh,
            GroupId = group.Id
        };
        await connections.AddAsync(profile);

        await groups.DeleteAsync(group.Id, moveConnectionsTo: null, CancellationToken.None);

        var survivor = await connections.GetByIdAsync(profile.Id);
        Assert.NotNull(survivor);
        Assert.Null(survivor!.GroupId); // 迁移到「未分组」
    }

    [Fact]
    public async Task 删除标签会解除关联但保留连接()
    {
        var tags = new SqliteTagRepository(_database);
        var connections = new SqliteConnectionRepository(_database);

        var tag = new Tag { Name = "生产" };
        await tags.AddAsync(tag);

        var profile = new ConnectionProfile
        {
            Name = "srv-b",
            Host = "10.0.0.2",
            Port = 5900,
            Protocol = ProtocolType.Vnc,
            TagIds = [tag.Id]
        };
        await connections.AddAsync(profile);

        await tags.DeleteAsync(tag.Id);

        var survivor = await connections.GetByIdAsync(profile.Id);
        Assert.NotNull(survivor);
        Assert.Empty(survivor!.TagIds);
    }

    [Fact]
    public async Task 删除凭据后引用它的连接变为未指定凭据()
    {
        var credentials = new SqliteCredentialRepository(_database);
        var connections = new SqliteConnectionRepository(_database);

        var credential = new Credential { Name = "DomainAdmin", Type = CredentialType.WindowsDomain, Username = "admin" };
        await credentials.AddAsync(credential);

        var profile = new ConnectionProfile
        {
            Name = "srv-c",
            Host = "10.0.0.3",
            Port = 3389,
            Protocol = ProtocolType.Rdp,
            CredentialId = credential.Id
        };
        await connections.AddAsync(profile);

        Assert.Equal(1, await connections.CountByCredentialAsync(credential.Id));

        await credentials.DeleteAsync(credential.Id);

        var survivor = await connections.GetByIdAsync(profile.Id);
        Assert.NotNull(survivor);
        Assert.Null(survivor!.CredentialId); // ON DELETE SET NULL
    }

    [Fact]
    public async Task 连接历史只记录标准化错误码不含敏感列()
    {
        var history = new SqliteHistoryRepository(_database);

        var entry = new ConnectionHistoryEntry
        {
            ConnectionId = Guid.NewGuid(),
            ConnectionName = "DC01",
            Host = "10.10.1.10",
            Protocol = ProtocolType.Rdp,
            StartedAt = DateTimeOffset.Now,
            Result = ConnectionResult.Failed,
            ErrorCode = ConnectionErrorCode.AuthenticationFailed
        };
        await history.AddAsync(entry);
        await history.CompleteAsync(entry.Id, DateTimeOffset.Now.AddSeconds(3),
            ConnectionResult.Failed, ConnectionErrorCode.AuthenticationFailed, CancellationToken.None);

        var recent = await history.GetRecentAsync(10);
        var loaded = Assert.Single(recent);
        Assert.Equal(ConnectionErrorCode.AuthenticationFailed, loaded.ErrorCode);
        Assert.NotNull(loaded.EndedAt);

        // 表结构不应有任何 password / secret / key 列
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT GROUP_CONCAT(name) FROM pragma_table_info('connection_history');";
        var columns = (command.ExecuteScalar() as string ?? string.Empty).ToLowerInvariant();
        Assert.DoesNotContain("password", columns);
        Assert.DoesNotContain("secret", columns);
        Assert.DoesNotContain("private", columns);
    }

    [Fact]
    public async Task SSH主机密钥可保存并按主机端口检索()
    {
        var repo = new SqliteHostKeyRepository(_database);

        await repo.SaveAsync(new SshHostKeyRecord
        {
            HostKey = SshHostKeyRecord.BuildHostKey("10.0.0.9", 22),
            KeyAlgorithm = "ssh-ed25519",
            Fingerprint = "AAAA1111",
            TrustedAt = DateTimeOffset.Now
        });

        var found = await repo.GetAsync("10.0.0.9", 22);
        Assert.NotNull(found);
        Assert.Equal("ssh-ed25519", found!.KeyAlgorithm);

        // 换端口就查不到
        Assert.Null(await repo.GetAsync("10.0.0.9", 2222));

        // 覆盖保存（指纹轮换）
        await repo.SaveAsync(new SshHostKeyRecord
        {
            HostKey = SshHostKeyRecord.BuildHostKey("10.0.0.9", 22),
            KeyAlgorithm = "ssh-ed25519",
            Fingerprint = "BBBB2222",
            TrustedAt = DateTimeOffset.Now
        });
        Assert.Equal("BBBB2222", (await repo.GetAsync("10.0.0.9", 22))!.Fingerprint);
    }

    [Fact]
    public async Task SSH主机密钥可一次清空()
    {
        var repo = new SqliteHostKeyRepository(_database);
        foreach (var host in new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3" })
        {
            await repo.SaveAsync(new SshHostKeyRecord
            {
                HostKey = SshHostKeyRecord.BuildHostKey(host, 22),
                KeyAlgorithm = "ssh-ed25519",
                Fingerprint = "FP-" + host,
                TrustedAt = DateTimeOffset.Now
            });
        }

        Assert.Equal(3, (await repo.GetAllAsync()).Count);

        await repo.ClearAsync();

        Assert.Empty(await repo.GetAllAsync());
        Assert.Null(await repo.GetAsync("10.0.0.2", 22));
    }

    [Fact]
    public void 残留的坏SHM文件不阻断启动_自动清理后恢复()
    {
        // 已初始化的库正常关掉，模拟上一实例被强杀：写一个和主库不一致的坏 -shm。
        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(_workspace.DatabasePath + "-shm", new byte[32 * 1024]);
        File.WriteAllBytes(_workspace.DatabasePath + "-wal", []);

        // 没有任何进程占用这两个文件，Initialize 的自愈逻辑应删掉它们并成功打开。
        var fresh = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        var ex = Record.Exception(fresh.Initialize);

        Assert.Null(ex);

        using var connection = fresh.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(RemoteFlowDatabase.CurrentSchemaVersion, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public async Task 连接历史计数返回真实累计总数不受列表上限影响()
    {
        var repo = new SqliteHistoryRepository(_database);
        var connectionId = Guid.NewGuid();

        for (var i = 1; i <= 6; i++)
        {
            await repo.AddAsync(new ConnectionHistoryEntry
            {
                Id = Guid.NewGuid(),
                ConnectionId = connectionId,
                ConnectionName = "计数机",
                Host = "10.0.0.1",
                Protocol = ProtocolType.Ssh,
                StartedAt = DateTimeOffset.Now.AddMinutes(-i),
                Result = ConnectionResult.Success,
                ErrorCode = ConnectionErrorCode.None
            });
        }

        Assert.Equal(6, await repo.CountByConnectionAsync(connectionId));
        // 列表接口只回最近 3 条，用于证明两者不同源。
        Assert.Equal(3, (await repo.GetByConnectionAsync(connectionId, 3)).Count);
    }

    [Fact]
    public async Task 分组表含默认与保护两列()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(connection_groups);";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }
        Assert.Contains("is_default", columns);
        Assert.Contains("is_protected", columns);
    }

    [Fact]
    public async Task 分组默认与保护字段往返一致()
    {
        var repo = new SqliteGroupRepository(_database);
        var group = new ConnectionGroup { Name = "默认组", IsDefault = true, IsProtected = true };
        await repo.AddAsync(group);

        var loaded = (await repo.GetAllAsync()).Single(g => g.Id == group.Id);
        Assert.True(loaded.IsDefault);
        Assert.True(loaded.IsProtected);
    }

    private async Task<long> CountConnectionTagsAsync(Guid connectionId)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM connection_tags WHERE connection_id = $id;";
        command.Parameters.AddWithValue("$id", connectionId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task 删除连接会一并删除其历史与标签关联()
    {
        var connections = new SqliteConnectionRepository(_database);
        var history = new SqliteHistoryRepository(_database);
        var tags = new SqliteTagRepository(_database);

        var tag = new Tag { Name = "prod" };
        await tags.AddAsync(tag);
        var connection = new ConnectionProfile { Name = "web-01", Host = "10.0.0.1", Port = 22, TagIds = [tag.Id] };
        await connections.AddAsync(connection);
        await history.AddAsync(new ConnectionHistoryEntry
        {
            ConnectionId = connection.Id,
            ConnectionName = connection.Name,
            Host = connection.Host,
            Protocol = ProtocolType.Ssh,
            StartedAt = DateTimeOffset.Now,
            Result = ConnectionResult.Success,
        });

        Assert.Single(await history.GetByConnectionAsync(connection.Id, 10));

        await connections.DeleteAsync(connection.Id);

        // 连接、历史（最近活动）与其标签关联都不得残留。
        Assert.Null(await connections.GetByIdAsync(connection.Id));
        Assert.Empty(await history.GetByConnectionAsync(connection.Id, 10));
        Assert.Equal(0, await CountConnectionTagsAsync(connection.Id));
    }
}
