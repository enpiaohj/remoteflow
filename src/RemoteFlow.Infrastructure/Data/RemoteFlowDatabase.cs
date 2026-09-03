using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace RemoteFlow.Infrastructure.Data;

/// <summary>
/// SQLite 数据库访问入口。负责连接创建与 Schema 版本迁移。
/// <para>
/// 稳定性设计：
/// <list type="bullet">
///   <item>启用 WAL 日志模式，异常退出后数据库仍可恢复。</item>
///   <item>Schema 通过 <c>user_version</c> 做版本化迁移，升级路径可追溯。</item>
///   <item>迁移在事务中执行，失败则整体回滚，不留半迁移状态。</item>
/// </list>
/// </para>
/// </summary>
public sealed class RemoteFlowDatabase
{
    /// <summary>当前 Schema 版本。新增迁移时递增，并在 <see cref="Migrations"/> 中追加脚本。</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly string _connectionString;
    private readonly ILogger<RemoteFlowDatabase> _logger;

    public string DatabasePath { get; }

    public RemoteFlowDatabase(string databasePath, ILogger<RemoteFlowDatabase> logger)
    {
        DatabasePath = databasePath;
        _logger = logger;

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    /// <summary>打开一个已配置好的连接。调用方负责释放。</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        // WAL：提升并发读写能力，并让非正常退出后的数据库保持可恢复。
        // foreign_keys：确保 connection_tags 等关联表的级联约束真正生效。
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>初始化数据库并执行必要的 Schema 迁移。应用启动时调用一次。</summary>
    public void Initialize()
    {
        using var connection = OpenConnection();

        var currentVersion = GetSchemaVersion(connection);
        if (currentVersion > CurrentSchemaVersion)
        {
            // 数据库来自更高版本的 RemoteFlow。继续使用可能损坏数据，必须明确失败。
            throw new InvalidOperationException(
                $"数据库 Schema 版本为 {currentVersion}，高于当前程序支持的 {CurrentSchemaVersion}。" +
                "请升级 RemoteFlow 后再打开该数据库。");
        }

        if (currentVersion == CurrentSchemaVersion)
        {
            _logger.LogInformation("数据库 Schema 已是最新版本 {Version}", currentVersion);
            return;
        }

        _logger.LogInformation("开始迁移数据库 Schema：{From} → {To}", currentVersion, CurrentSchemaVersion);

        using var transaction = connection.BeginTransaction();
        try
        {
            for (var version = currentVersion + 1; version <= CurrentSchemaVersion; version++)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = Migrations[version];
                command.ExecuteNonQuery();

                _logger.LogInformation("已应用 Schema 迁移 v{Version}", version);
            }

            using var versionCommand = connection.CreateCommand();
            versionCommand.Transaction = transaction;
            // PRAGMA 不支持参数化，此处的值来自编译期常量，无注入风险。
            versionCommand.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
            versionCommand.ExecuteNonQuery();

            transaction.Commit();
            _logger.LogInformation("数据库 Schema 迁移完成，当前版本 {Version}", CurrentSchemaVersion);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "数据库 Schema 迁移失败，已回滚");
            throw;
        }
    }

    private static int GetSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>版本号 → 迁移脚本。每个脚本必须可在事务中整体执行。</summary>
    private static readonly Dictionary<int, string> Migrations = new()
    {
        [1] = """
        CREATE TABLE connection_groups (
            id          TEXT PRIMARY KEY NOT NULL,
            name        TEXT NOT NULL,
            parent_id   TEXT NULL REFERENCES connection_groups(id) ON DELETE SET NULL,
            sort_order  INTEGER NOT NULL DEFAULT 0,
            icon        TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE credentials (
            id                TEXT PRIMARY KEY NOT NULL,
            name              TEXT NOT NULL,
            type              INTEGER NOT NULL,
            username          TEXT NOT NULL DEFAULT '',
            domain            TEXT NOT NULL DEFAULT '',
            secret_reference  TEXT NULL,
            key_reference     TEXT NULL,
            description       TEXT NOT NULL DEFAULT '',
            created_at        TEXT NOT NULL,
            updated_at        TEXT NOT NULL
        );

        CREATE TABLE connections (
            id                 TEXT PRIMARY KEY NOT NULL,
            name               TEXT NOT NULL,
            host               TEXT NOT NULL,
            port               INTEGER NOT NULL,
            protocol           INTEGER NOT NULL,
            group_id           TEXT NULL REFERENCES connection_groups(id) ON DELETE SET NULL,
            credential_id      TEXT NULL REFERENCES credentials(id) ON DELETE SET NULL,
            favorite           INTEGER NOT NULL DEFAULT 0,
            notes              TEXT NOT NULL DEFAULT '',
            created_at         TEXT NOT NULL,
            updated_at         TEXT NOT NULL,
            last_connected_at  TEXT NULL,
            options_json       TEXT NOT NULL DEFAULT '{}'
        );

        CREATE INDEX idx_connections_group ON connections(group_id);
        CREATE INDEX idx_connections_favorite ON connections(favorite);
        CREATE INDEX idx_connections_last_connected ON connections(last_connected_at);

        CREATE TABLE tags (
            id          TEXT PRIMARY KEY NOT NULL,
            name        TEXT NOT NULL,
            description TEXT NOT NULL DEFAULT '',
            color       TEXT NOT NULL DEFAULT '#0F6CBD'
        );

        CREATE TABLE connection_tags (
            connection_id TEXT NOT NULL REFERENCES connections(id) ON DELETE CASCADE,
            tag_id        TEXT NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
            PRIMARY KEY (connection_id, tag_id)
        );

        CREATE INDEX idx_connection_tags_tag ON connection_tags(tag_id);

        CREATE TABLE connection_history (
            id              TEXT PRIMARY KEY NOT NULL,
            connection_id   TEXT NOT NULL,
            connection_name TEXT NOT NULL,
            host            TEXT NOT NULL,
            protocol        INTEGER NOT NULL,
            started_at      TEXT NOT NULL,
            ended_at        TEXT NULL,
            result          INTEGER NOT NULL,
            error_code      INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX idx_history_started ON connection_history(started_at DESC);

        CREATE TABLE ssh_host_keys (
            host_key      TEXT PRIMARY KEY NOT NULL,
            key_algorithm TEXT NOT NULL,
            fingerprint   TEXT NOT NULL,
            trusted_at    TEXT NOT NULL
        );
        """
    };
}
