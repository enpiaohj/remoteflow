using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Core.Models;
using RemoteFlow.Core.Sessions;
using RemoteFlow.Infrastructure.Data;
using Xunit;

namespace RemoteFlow.IntegrationTests;

/// <summary>
/// 连接历史新增的「会话种类」列：旧库自动补齐，且<b>不提升 Schema 版本</b>，
/// 保证打开过新版本的数据库仍能退回上一版使用。
/// </summary>
public sealed class HistorySessionKindTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rf-history-kind-" + Guid.NewGuid().ToString("N"));

    private RemoteFlowDatabase Open()
    {
        Directory.CreateDirectory(_directory);
        var database = new RemoteFlowDatabase(Path.Combine(_directory, "remoteflow.db"), NullLogger<RemoteFlowDatabase>.Instance);
        database.Initialize();
        return database;
    }

    private static object Scalar(RemoteFlowDatabase database, string sql)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    private static void Execute(RemoteFlowDatabase database, string sql)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task 旧库补列后版本号不变_旧版本的写法仍可插入且读作普通会话()
    {
        var database = Open();
        // 还原成上一版的库：没有这一列。
        Execute(database, "ALTER TABLE connection_history DROP COLUMN session_kind;");

        database = Open();

        Assert.Equal((long)RemoteFlowDatabase.CurrentSchemaVersion, Scalar(database, "PRAGMA user_version;"));
        Assert.Equal(8, RemoteFlowDatabase.CurrentSchemaVersion); // 与 v0.31.0 相同：旧版本不会拒绝打开
        // 上一版的 INSERT 不带新列。
        Execute(database,
            "INSERT INTO connection_history (id, connection_id, connection_name, host, protocol, started_at, ended_at, result, error_code) "
            + $"VALUES ('{Guid.NewGuid()}', '{Guid.NewGuid()}', 'old', 'host.example.test', 0, '{DateTimeOffset.Now:O}', NULL, 0, 0);");
        var entry = Assert.Single(await new SqliteHistoryRepository(database).GetRecentAsync(10));
        Assert.Equal(SessionKind.Default, entry.SessionKind);
    }

    [Fact]
    public async Task 会话种类写入后可读回_重复启动不重复补列()
    {
        var repository = new SqliteHistoryRepository(Open());
        await repository.AddAsync(new ConnectionHistoryEntry
        {
            ConnectionId = Guid.NewGuid(), ConnectionName = "n", Host = "h", Protocol = ProtocolType.Rdp,
            SessionKind = SessionKind.PowerShell, StartedAt = DateTimeOffset.Now
        });

        var reopened = new SqliteHistoryRepository(Open());

        Assert.Equal(SessionKind.PowerShell, Assert.Single(await reopened.GetRecentAsync(10)).SessionKind);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
