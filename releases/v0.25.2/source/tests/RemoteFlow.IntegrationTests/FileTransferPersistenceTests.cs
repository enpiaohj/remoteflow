using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Infrastructure.Data;
using RemoteFlow.Infrastructure.Sync;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 文件传输参数（通道 / SFTP 端口）的持久化与同步兼容：纯加法字段，
/// 老数据与旧客户端同步来的记录都没有该键，必须平滑取默认值，不能丢连接、不能报错。
/// </summary>
public sealed class FileTransferPersistenceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly RemoteFlowDatabase _database;
    private readonly SqliteConnectionRepository _repository;

    public FileTransferPersistenceTests()
    {
        _database = new RemoteFlowDatabase(_workspace.DatabasePath, NullLogger<RemoteFlowDatabase>.Instance);
        _database.Initialize();
        _repository = new SqliteConnectionRepository(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task 文件传输参数经数据库往返一致()
    {
        var profile = NewProfile();
        profile.FileTransfer.Channel = FileTransferChannel.Sftp;
        profile.FileTransfer.SftpPort = 2222;
        await _repository.AddAsync(profile);

        var loaded = await _repository.GetByIdAsync(profile.Id);

        Assert.Equal(FileTransferChannel.Sftp, loaded!.FileTransfer.Channel);
        Assert.Equal(2222, loaded.FileTransfer.SftpPort);
    }

    [Fact]
    public async Task 更新连接时文件传输参数随之更新()
    {
        var profile = NewProfile();
        await _repository.AddAsync(profile);

        profile.FileTransfer.Channel = FileTransferChannel.Smb;
        await _repository.UpdateAsync(profile);

        Assert.Equal(FileTransferChannel.Smb, (await _repository.GetByIdAsync(profile.Id))!.FileTransfer.Channel);
    }

    [Fact]
    public async Task 升级前写入的老数据没有该键时取默认值且其它参数不受影响()
    {
        var profile = NewProfile();
        profile.Rdp.Domain = "CORP";
        await _repository.AddAsync(profile);
        // 还原成升级前的载荷形态：只有 Rdp / Ssh / Vnc 三个键。
        Exec("UPDATE connections SET options_json = @json WHERE id = @id;",
            ("@json", """{"Rdp":{"Domain":"CORP"},"Ssh":{},"Vnc":{}}"""), ("@id", profile.Id.ToString()));

        var loaded = await _repository.GetByIdAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal(FileTransferChannel.Auto, loaded.FileTransfer.Channel);
        Assert.Equal(22, loaded.FileTransfer.SftpPort);
        Assert.Equal("CORP", loaded.Rdp.Domain);
    }

    [Fact]
    public async Task 载荷损坏时整体退回默认值而不是让连接列表加载失败()
    {
        var profile = NewProfile();
        await _repository.AddAsync(profile);
        Exec("UPDATE connections SET options_json = @json WHERE id = @id;",
            ("@json", "{ not json"), ("@id", profile.Id.ToString()));

        var all = await _repository.GetAllAsync();

        var loaded = Assert.Single(all);
        Assert.Equal(FileTransferChannel.Auto, loaded.FileTransfer.Channel);
        Assert.Equal(profile.Name, loaded.Name);
    }

    [Fact]
    public async Task 载荷里显式为null时也补默认值()
    {
        var profile = NewProfile();
        await _repository.AddAsync(profile);
        Exec("UPDATE connections SET options_json = @json WHERE id = @id;",
            ("@json", """{"Rdp":{},"Ssh":{},"Vnc":{},"FileTransfer":null}"""), ("@id", profile.Id.ToString()));

        var loaded = await _repository.GetByIdAsync(profile.Id);

        Assert.NotNull(loaded!.FileTransfer);
        Assert.Equal(FileTransferChannel.Auto, loaded.FileTransfer.Channel);
    }

    // ── 云同步载荷兼容 ──

    [Fact]
    public void 同步载荷带出文件传输参数并可完整还原()
    {
        var profile = NewProfile();
        profile.FileTransfer.Channel = FileTransferChannel.Sftp;
        profile.FileTransfer.SftpPort = 2200;

        var payload = SyncSerializer.Serialize(profile, 1);
        var (_, restored) = SyncSerializer.Deserialize<ConnectionProfile>(payload);

        Assert.Contains("\"FileTransfer\":{\"Channel\":\"Sftp\",\"SftpPort\":2200}", System.Text.Encoding.UTF8.GetString(payload));
        Assert.Equal(FileTransferChannel.Sftp, restored.FileTransfer.Channel);
        Assert.Equal(2200, restored.FileTransfer.SftpPort);
    }

    [Fact]
    public void 旧客户端同步来的载荷没有该字段时取默认值不报错()
    {
        var profile = NewProfile();
        var full = System.Text.Encoding.UTF8.GetString(SyncSerializer.Serialize(profile, 1));
        var oldStyle = System.Text.RegularExpressions.Regex.Replace(full, ",\"FileTransfer\":\\{[^}]*\\}", string.Empty);
        Assert.DoesNotContain("FileTransfer", oldStyle);

        var (version, restored) = SyncSerializer.Deserialize<ConnectionProfile>(System.Text.Encoding.UTF8.GetBytes(oldStyle));

        Assert.Equal(1, version);
        Assert.Equal(FileTransferChannel.Auto, restored.FileTransfer.Channel);
        Assert.Equal(22, restored.FileTransfer.SftpPort);
        Assert.Equal(profile.Name, restored.Name);
    }

    // ── 辅助 ──

    private static ConnectionProfile NewProfile() => new()
    {
        Name = "srv-" + Guid.NewGuid().ToString("N")[..6],
        Host = "host.example",
        Port = 3389,
        Protocol = ProtocolType.Rdp
    };

    private void Exec(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
