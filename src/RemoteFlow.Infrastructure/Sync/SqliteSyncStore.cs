using System.Globalization;
using Microsoft.Data.Sqlite;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Infrastructure.Data;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary>
/// Cloud Sync 四张本地表（<c>sync_outbox</c> / <c>sync_state</c> / <c>sync_entity_state</c> /
/// <c>sync_conflict</c>）的读写。Outbox 不存 Secret / 明文。
/// </summary>
public sealed class SqliteSyncStore(RemoteFlowDatabase database)
{
    // ── Outbox ──────────────────────────────────────────────────

    /// <summary>
    /// 登记一条待推送变更。同一实体已有条目时合并（重生 OperationId、递增 sequence、清空重试状态）——
    /// 「Update + Update → 最新 Upsert」。<paramref name="baseVersion"/> 传该实体当前已知的服务端版本。
    /// </summary>
    public async Task EnqueueAsync(
        string entityType,
        string entityId,
        OutboxOperationType operationType,
        long baseVersion,
        CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_outbox
                (id, entity_type, entity_id, operation_type, operation_id, base_version, sequence, created_at)
            VALUES
                ($id, $type, $eid, $op, $opid, $base, 1, $now)
            ON CONFLICT(entity_type, entity_id) DO UPDATE SET
                operation_type = excluded.operation_type,
                operation_id   = excluded.operation_id,
                base_version   = excluded.base_version,
                sequence       = sync_outbox.sequence + 1,
                retry_count    = 0,
                next_retry_at  = NULL,
                last_error     = '';
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$eid", entityId);
        command.Parameters.AddWithValue("$op", (int)operationType);
        command.Parameters.AddWithValue("$opid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$base", baseVersion);
        command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>到期可推送的 Outbox 条目（next_retry_at 为空或已过）。</summary>
    public async Task<IReadOnlyList<OutboxEntry>> GetDueEntriesAsync(
        DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, entity_type, entity_id, operation_type, operation_id, base_version,
                   sequence, retry_count, next_retry_at
            FROM sync_outbox
            WHERE next_retry_at IS NULL OR next_retry_at <= $now
            ORDER BY created_at
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", Iso(now));
        command.Parameters.AddWithValue("$limit", limit);

        var entries = new List<OutboxEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new OutboxEntry(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                (OutboxOperationType)reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : ParseIso(reader.GetString(8))));
        }

        return entries;
    }

    /// <summary>推送成功后删除条目——仅当 sequence 未变（期间没有新的本地写入）。</summary>
    public async Task<bool> DeleteIfUnchangedAsync(Guid id, long sequence, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sync_outbox WHERE id = $id AND sequence = $seq;";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$seq", sequence);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task DeleteEntityAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sync_outbox WHERE entity_type = $type AND entity_id = $eid;";
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$eid", entityId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkRetryAsync(
        Guid id, int retryCount, DateTimeOffset nextRetryAt, string error, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_outbox
            SET retry_count = $count, next_retry_at = $next, last_error = $error
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$count", retryCount);
        command.Parameters.AddWithValue("$next", Iso(nextRetryAt));
        command.Parameters.AddWithValue("$error", Truncate(error, 200));
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> HasPendingAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sync_outbox WHERE entity_type = $type AND entity_id = $eid LIMIT 1;";
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$eid", entityId);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<int> PendingCountAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sync_outbox;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    // ── SyncState ───────────────────────────────────────────────

    public async Task<SyncStateSnapshot> GetStateAsync(string appId, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cursor, last_successful_sync_at, last_attempt_at, status
            FROM sync_state WHERE app_id = $appId;
            """;
        command.Parameters.AddWithValue("$appId", appId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new SyncStateSnapshot(0, null, null, SyncStatus.Idle);
        }

        return new SyncStateSnapshot(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : ParseIso(reader.GetString(1)),
            reader.IsDBNull(2) ? null : ParseIso(reader.GetString(2)),
            Enum.Parse<SyncStatus>(reader.GetString(3)));
    }

    public async Task SetCursorAsync(string appId, long cursor, CancellationToken ct = default) =>
        await UpsertStateAsync(appId, ct, ("cursor", cursor));

    public async Task SetStatusAsync(
        string appId, SyncStatus status, DateTimeOffset? attemptAt, DateTimeOffset? successAt,
        CancellationToken ct = default)
    {
        var fields = new List<(string, object)> { ("status", status.ToString()) };
        if (attemptAt is { } a)
        {
            fields.Add(("last_attempt_at", Iso(a)));
        }

        if (successAt is { } s)
        {
            fields.Add(("last_successful_sync_at", Iso(s)));
        }

        await UpsertStateAsync(appId, ct, [.. fields]);
    }

    private async Task UpsertStateAsync(
        string appId, CancellationToken ct, params (string Column, object Value)[] fields)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        var setClause = string.Join(", ", fields.Select((f, i) => $"{f.Column} = $v{i}"));
        command.CommandText = $"""
            INSERT INTO sync_state (app_id) VALUES ($appId)
            ON CONFLICT(app_id) DO NOTHING;
            UPDATE sync_state SET {setClause} WHERE app_id = $appId;
            """;
        command.Parameters.AddWithValue("$appId", appId);
        for (var i = 0; i < fields.Length; i++)
        {
            command.Parameters.AddWithValue($"$v{i}", fields[i].Value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    // ── SyncEntityState ─────────────────────────────────────────

    public async Task<long> GetServerVersionAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT server_version FROM sync_entity_state WHERE entity_type = $type AND entity_id = $eid;";
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$eid", entityId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task SetServerVersionAsync(
        string entityType, string entityId, long version, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_entity_state (entity_type, entity_id, server_version, last_local_change_at)
            VALUES ($type, $eid, $version, $now)
            ON CONFLICT(entity_type, entity_id) DO UPDATE SET
                server_version = excluded.server_version,
                last_local_change_at = excluded.last_local_change_at;
            """;
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$eid", entityId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<string> GetContentHashAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT content_hash FROM sync_entity_state WHERE entity_type = $type AND entity_id = $eid;";
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$eid", entityId);
        return (await command.ExecuteScalarAsync(ct)) as string ?? string.Empty;
    }

    public async Task SetContentHashAsync(
        string entityType, string entityId, string contentHash, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_entity_state (entity_type, entity_id, content_hash)
            VALUES ($type, $eid, $hash)
            ON CONFLICT(entity_type, entity_id) DO UPDATE SET content_hash = excluded.content_hash;
            """;
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$eid", entityId);
        command.Parameters.AddWithValue("$hash", contentHash);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>已经至少同步过一次（server_version &gt; 0）的实体 Id——对账时用来发现本地删除。</summary>
    public async Task<IReadOnlyList<string>> GetSyncedEntityIdsAsync(string entityType, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT entity_id FROM sync_entity_state WHERE entity_type = $type AND server_version > 0;";
        command.Parameters.AddWithValue("$type", entityType);

        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>已同步到云端的实体数量，按实体类型汇总（server_version &gt; 0 即视为已同步）。</summary>
    public async Task<IReadOnlyList<(string EntityType, int Count)>> GetSyncedCountsAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT entity_type, COUNT(*)
            FROM sync_entity_state
            WHERE server_version > 0
            GROUP BY entity_type
            ORDER BY entity_type;
            """;

        var counts = new List<(string, int)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            counts.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        return counts;
    }

    /// <summary>
    /// 删除某实体的同步状态行（墓碑落地 / 删除推送成功后调用）。
    /// 「已同步条目统计」据此只反映仍然存在的实体，不再把墓碑算进去。
    /// </summary>
    public async Task DeleteEntityStateAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM sync_entity_state WHERE entity_type = $type AND entity_id = $eid;";
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$eid", entityId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsConflictedAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT conflict_state FROM sync_entity_state WHERE entity_type = $type AND entity_id = $eid;";
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$eid", entityId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is not null && Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
    }

    public async Task SetConflictStateAsync(
        string entityType, string entityId, bool conflicted, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_entity_state (entity_type, entity_id, conflict_state)
            VALUES ($type, $eid, $state)
            ON CONFLICT(entity_type, entity_id) DO UPDATE SET conflict_state = excluded.conflict_state;
            """;
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$eid", entityId);
        command.Parameters.AddWithValue("$state", conflicted ? 1 : 0);
        await command.ExecuteNonQueryAsync(ct);
    }

    // ── SyncConflict ────────────────────────────────────────────

    public async Task<Guid> RecordConflictAsync(
        string entityType,
        string entityId,
        EncryptedPayload? local,
        SyncServerEntity remote,
        CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        // 同一实体已有未决冲突时覆盖为最新一次
        command.CommandText = """
            DELETE FROM sync_conflict WHERE entity_type = $type AND entity_id = $eid AND resolved_at IS NULL;
            INSERT INTO sync_conflict
                (id, entity_type, entity_id, local_ciphertext, local_nonce, local_key_version, local_schema_version,
                 remote_ciphertext, remote_nonce, remote_version, remote_key_version, remote_schema_version,
                 remote_deleted, detected_at)
            VALUES
                ($id, $type, $eid, $lct, $lnc, $lkv, $lsv, $rct, $rnc, $rver, $rkv, $rsv, $rdel, $now);
            """;
        var id = Guid.NewGuid();
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$eid", entityId);
        command.Parameters.AddWithValue("$lct", (object?)local?.Ciphertext ?? DBNull.Value);
        command.Parameters.AddWithValue("$lnc", (object?)local?.Nonce ?? DBNull.Value);
        command.Parameters.AddWithValue("$lkv", local?.KeyVersion ?? 0);
        command.Parameters.AddWithValue("$lsv", local?.SchemaVersion ?? 0);
        command.Parameters.AddWithValue("$rct", (object?)remote.Ciphertext ?? DBNull.Value);
        command.Parameters.AddWithValue("$rnc", (object?)remote.Nonce ?? DBNull.Value);
        command.Parameters.AddWithValue("$rver", remote.Version);
        command.Parameters.AddWithValue("$rkv", remote.KeyVersion);
        command.Parameters.AddWithValue("$rsv", remote.SchemaVersion);
        command.Parameters.AddWithValue("$rdel", remote.Deleted ? 1 : 0);
        command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<SyncConflictRecord>> ListUnresolvedAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, entity_type, entity_id, local_ciphertext, local_nonce, local_key_version, local_schema_version,
                   remote_ciphertext, remote_nonce, remote_version, remote_key_version, remote_schema_version,
                   remote_deleted, detected_at, resolution
            FROM sync_conflict WHERE resolved_at IS NULL ORDER BY detected_at;
            """;

        var conflicts = new List<SyncConflictRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            EncryptedPayload? local = reader.IsDBNull(3)
                ? null
                : new EncryptedPayload(GetBytes(reader, 3), GetBytes(reader, 4), reader.GetInt32(5), reader.GetInt32(6));
            var remote = new SyncServerEntity(
                reader.GetString(1), reader.GetString(2), reader.GetInt64(9), 0,
                reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12) != 0,
                reader.IsDBNull(7) ? null : GetBytes(reader, 7),
                reader.IsDBNull(8) ? null : GetBytes(reader, 8));
            conflicts.Add(new SyncConflictRecord(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                local, remote, ParseIso(reader.GetString(13)), (ConflictResolution)reader.GetInt32(14)));
        }

        return conflicts;
    }

    public async Task ResolveConflictAsync(Guid id, ConflictResolution resolution, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE sync_conflict SET resolved_at = $now, resolution = $res WHERE id = $id;";
        command.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$res", (int)resolution);
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>清空全部同步本地状态（「清除此设备云数据」）。本地连接 / 凭据不受影响。</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM sync_outbox;
            DELETE FROM sync_state;
            DELETE FROM sync_entity_state;
            DELETE FROM sync_conflict;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    // ── 辅助 ────────────────────────────────────────────────────

    private static string Iso(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static byte[] GetBytes(SqliteDataReader reader, int ordinal) => (byte[])reader.GetValue(ordinal);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
