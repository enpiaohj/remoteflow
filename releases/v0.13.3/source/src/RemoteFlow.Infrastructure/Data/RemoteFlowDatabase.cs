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
    public const int CurrentSchemaVersion = 5;

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

    /// <summary>
    /// 初始化数据库并执行必要的 Schema 迁移。应用启动时调用一次。
    /// <para>
    /// 上一个实例被强制结束（崩溃、任务管理器结束进程）时，可能残留仍在校验点的
    /// WAL / SHM 文件，导致新进程首次打开短暂拿不到锁、报 <c>SQLITE_BUSY</c> /
    /// <c>SQLITE_IOERR</c>。此处带退避重试，让数据库在非正常退出后仍能恢复启动
    /// （产品设计文档 §14.2）；重试仍失败才明确报错。
    /// </para>
    /// </summary>
    public void Initialize()
    {
        using var connection = OpenConnectionWithRetry();

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

    /// <summary>
    /// 带退避重试地打开连接。仅用于启动阶段，覆盖上一实例未完全释放锁的短暂窗口。
    /// </summary>
    private SqliteConnection OpenConnectionWithRetry()
    {
        // SQLITE_BUSY / SQLITE_LOCKED / SQLITE_IOERR / SQLITE_BUSY_RECOVERY / SQLITE_BUSY_SNAPSHOT
        var retryableCodes = new HashSet<int> { 5, 6, 10, 261, 517 };

        var delays = new[] { 150, 300, 600, 1200, 2400 };

        var triedShmRecovery = false;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return OpenConnection();
            }
            catch (SqliteException ex) when (retryableCodes.Contains(ex.SqliteErrorCode) && attempt < delays.Length)
            {
                _logger.LogWarning(
                    "打开数据库暂时失败（SQLite {Code}），{Delay}ms 后重试（第 {Attempt}/{Total} 次）",
                    ex.SqliteErrorCode, delays[attempt], attempt + 1, delays.Length);

                Thread.Sleep(delays[attempt]);
            }
            catch (SqliteException ex) when (retryableCodes.Contains(ex.SqliteErrorCode))
            {
                // 重试仍失败：多半是上一实例被强杀后留下的 -wal / -shm 与主库不一致
                // （常见于 SQLITE_IOERR）。若这两个文件当前<b>没有被任何进程占用</b>
                // （能独占打开即证明无人使用，也就没有另一个实例在跑），删掉让 SQLite
                // 从主库重新生成——已提交的数据都在主库里。只尝试一次。
                if (!triedShmRecovery && TryClearOrphanedWalFiles())
                {
                    triedShmRecovery = true;
                    _logger.LogWarning("已清理疑似残留的 -wal / -shm 文件，重新尝试打开数据库");
                    SqliteConnection.ClearAllPools();
                    continue;
                }

                throw new InvalidOperationException(
                    $"无法打开数据库：{DatabasePath}。" +
                    "可能有另一个 RemoteFlow 实例正在运行，或上一次异常退出后文件仍被锁定。" +
                    "请关闭所有 RemoteFlow 窗口后重试；若问题持续，重启系统或从备份恢复数据目录。", ex);
            }
        }
    }

    /// <summary>
    /// 尝试删除孤立的 <c>-wal</c> / <c>-shm</c> 文件。仅当二者都能被独占打开
    /// （即没有任何进程持有句柄）时才删除，避免误删正在使用中的日志。
    /// </summary>
    private bool TryClearOrphanedWalFiles()
    {
        var wal = DatabasePath + "-wal";
        var shm = DatabasePath + "-shm";

        try
        {
            foreach (var path in new[] { wal, shm })
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                // 能独占打开 = 无人占用；随即关闭再删。
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }

                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "清理 -wal / -shm 失败（文件可能仍被占用），放弃自动恢复");
            return false;
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
        """,

        // v2：分组模型正式化。
        //  - connection_groups 增加 is_system 列，显式区分系统兜底分组。
        //  - 插入唯一系统分组「未分组」（固定 Id），新连接未指定分组时归入这里。
        //  - 默认用户分组「我的设备」的种子由服务层（GroupService.EnsureSeedAsync）首启补齐，
        //    不写死在迁移里；老用户升级也会新建「我的设备」，现有分组不动。
        [2] = """
        ALTER TABLE connection_groups ADD COLUMN is_system INTEGER NOT NULL DEFAULT 0;

        INSERT OR IGNORE INTO connection_groups (id, name, parent_id, sort_order, icon, is_system)
        VALUES ('00000000-0000-0000-0000-0000000000ff', '未分组', NULL, 2147483647, '', 1);
        """,

        // v3：默认分组与保护标记。语义约束（默认组至多一个、系统组保护）由服务层执行，
        // 迁移只加列；存量数据默认都不是默认组、不受保护。
        [3] = """
        ALTER TABLE connection_groups ADD COLUMN is_default INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE connection_groups ADD COLUMN is_protected INTEGER NOT NULL DEFAULT 0;
        """,

        // v4：Cloud Sync 本地表（协议设计 §2）。仅在启用云同步后写入；未登录用户这些表保持为空，
        // 不影响本地优先体验。Outbox 不存 Secret / 明文。
        [4] = """
        CREATE TABLE sync_outbox (
            id             TEXT PRIMARY KEY NOT NULL,
            entity_type    TEXT NOT NULL,
            entity_id      TEXT NOT NULL,
            operation_type INTEGER NOT NULL,
            operation_id   TEXT NOT NULL,
            base_version   INTEGER NOT NULL DEFAULT 0,
            sequence       INTEGER NOT NULL,
            created_at     TEXT NOT NULL,
            retry_count    INTEGER NOT NULL DEFAULT 0,
            next_retry_at  TEXT NULL,
            last_error     TEXT NOT NULL DEFAULT ''
        );

        CREATE UNIQUE INDEX idx_sync_outbox_entity ON sync_outbox(entity_type, entity_id);
        CREATE INDEX idx_sync_outbox_due ON sync_outbox(next_retry_at);

        CREATE TABLE sync_state (
            app_id                   TEXT PRIMARY KEY NOT NULL,
            cursor                   INTEGER NOT NULL DEFAULT 0,
            last_successful_sync_at  TEXT NULL,
            last_attempt_at          TEXT NULL,
            status                   TEXT NOT NULL DEFAULT 'Idle'
        );

        CREATE TABLE sync_entity_state (
            entity_type          TEXT NOT NULL,
            entity_id            TEXT NOT NULL,
            server_version       INTEGER NOT NULL DEFAULT 0,
            last_local_change_at TEXT NULL,
            conflict_state       INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (entity_type, entity_id)
        );

        CREATE TABLE sync_conflict (
            id                    TEXT PRIMARY KEY NOT NULL,
            entity_type           TEXT NOT NULL,
            entity_id             TEXT NOT NULL,
            local_ciphertext      BLOB NULL,
            local_nonce           BLOB NULL,
            local_key_version     INTEGER NOT NULL DEFAULT 0,
            local_schema_version  INTEGER NOT NULL DEFAULT 0,
            remote_ciphertext     BLOB NULL,
            remote_nonce          BLOB NULL,
            remote_version        INTEGER NOT NULL DEFAULT 0,
            remote_key_version    INTEGER NOT NULL DEFAULT 0,
            remote_schema_version INTEGER NOT NULL DEFAULT 0,
            remote_deleted        INTEGER NOT NULL DEFAULT 0,
            detected_at           TEXT NOT NULL,
            resolved_at           TEXT NULL,
            resolution            INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX idx_sync_conflict_open ON sync_conflict(resolved_at);
        """,

        // v5：sync_entity_state 增加 content_hash，用于崩溃后对账（业务写已提交但 Outbox 未入队时补登记）。
        [5] = """
        ALTER TABLE sync_entity_state ADD COLUMN content_hash TEXT NOT NULL DEFAULT '';
        """
    };
}
