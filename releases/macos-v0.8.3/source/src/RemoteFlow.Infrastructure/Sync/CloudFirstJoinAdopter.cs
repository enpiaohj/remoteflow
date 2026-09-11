using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Infrastructure.Data;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary>
/// 首次加入已有 Vault 时按<b>自然键</b>把本地重复条目认领到云端 Id（见 <see cref="ICloudFirstJoinAdopter"/>）。
/// <para>
/// 触发条件（由 <see cref="SyncCoordinator"/> 在首次同步、对账之前调用）：
/// 本地已把云端全量拉下来（因此云端条目此刻都在本地库里），同时本地还留着「从未同步过」的条目
/// —— 那正是另一台机器上同一对象的第二份 Id。
/// </para>
/// <para>
/// 自然键：连接 = host + 端口 + 协议 + 名称；凭据 = 名称 + 类型 + 账号 + 域；分组 / 标签 = 名称。
/// 命中后以<b>云端那份为准</b>：改写子引用（连接的 group/credential、标签关联、分组父子）指向云端 Id，
/// 删除本机重复行及其同步状态 / 待推条目，避免再把重复的推上云。整个过程在一个 SQLite 事务内完成。
/// </para>
/// </summary>
public sealed class CloudFirstJoinAdopter(
    RemoteFlowDatabase database,
    ICredentialVault vault,
    ILogger<CloudFirstJoinAdopter> logger) : ICloudFirstJoinAdopter
{
    public async Task<int> AdoptAsync(CancellationToken ct = default)
    {
        var orphanedSecretRefs = new List<string>();
        int adopted;

        await using (var connection = database.OpenConnection())
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            try
            {
                var synced = await ReadSyncedIdsAsync(connection, transaction, ct);
                adopted = await AdoptTagsAsync(connection, transaction, synced, ct)
                    + await AdoptGroupsAsync(connection, transaction, synced, ct)
                    + await AdoptCredentialsAsync(connection, transaction, synced, orphanedSecretRefs, ct)
                    + await AdoptConnectionsAsync(connection, transaction, synced, ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }

        // 凭据密钥引用不在 SQLite 事务内，提交后再清理旧引用。
        foreach (var reference in orphanedSecretRefs)
        {
            try
            {
                await vault.DeleteSecretAsync(reference, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "清理已合并凭据的旧密钥引用失败：{Reference}", reference);
            }
        }

        if (adopted > 0)
        {
            logger.LogInformation("首次加入消重：合并了 {Count} 条与本机重复的本地条目（改认云端 Id）", adopted);
        }

        return adopted;
    }

    // ── 各类型 ──────────────────────────────────────────────────

    private static async Task<int> AdoptTagsAsync(
        SqliteConnection connection, SqliteTransaction transaction, SyncedIds synced, CancellationToken ct)
    {
        var cloudByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var locals = new List<(string Id, string Name)>();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, name FROM tags;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                if (synced.Contains(SyncEntityTypes.Tag, id))
                {
                    cloudByName[Normalize(name)] = id;
                }
                else
                {
                    locals.Add((id, name));
                }
            }
        }

        var adopted = 0;
        foreach (var (localId, name) in locals)
        {
            if (!cloudByName.TryGetValue(Normalize(name), out var cloudId) || cloudId == localId)
            {
                continue;
            }

            await ExecuteAsync(connection, transaction, """
                DELETE FROM connection_tags
                WHERE tag_id = $new AND connection_id IN (SELECT connection_id FROM connection_tags WHERE tag_id = $old);
                UPDATE connection_tags SET tag_id = $new WHERE tag_id = $old;
                DELETE FROM tags WHERE id = $old;
                """, ct, ("$old", localId), ("$new", cloudId));
            await ForgetEntityAsync(connection, transaction, SyncEntityTypes.Tag, localId, ct);
            adopted++;
        }

        return adopted;
    }

    private static async Task<int> AdoptGroupsAsync(
        SqliteConnection connection, SqliteTransaction transaction, SyncedIds synced, CancellationToken ct)
    {
        var cloudByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var locals = new List<(string Id, string Name)>();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, name FROM connection_groups WHERE is_system = 0;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                if (synced.Contains(SyncEntityTypes.Group, id))
                {
                    cloudByName[Normalize(name)] = id;
                }
                else
                {
                    locals.Add((id, name));
                }
            }
        }

        var adopted = 0;
        foreach (var (localId, name) in locals)
        {
            if (!cloudByName.TryGetValue(Normalize(name), out var cloudId) || cloudId == localId)
            {
                continue;
            }

            await ExecuteAsync(connection, transaction, """
                UPDATE connections SET group_id = $new WHERE group_id = $old;
                UPDATE connection_groups SET parent_id = $new WHERE parent_id = $old;
                DELETE FROM connection_groups WHERE id = $old;
                """, ct, ("$old", localId), ("$new", cloudId));
            await ForgetEntityAsync(connection, transaction, SyncEntityTypes.Group, localId, ct);
            adopted++;
        }

        return adopted;
    }

    private static async Task<int> AdoptCredentialsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncedIds synced,
        List<string> orphanedSecretRefs,
        CancellationToken ct)
    {
        var cloudByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var locals = new List<(string Id, string Key, string? SecretRef, string? KeyRef)>();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, name, type, username, domain, secret_reference, key_reference FROM credentials;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var key = CredentialKey(
                    reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4));
                if (synced.Contains(SyncEntityTypes.Credential, id))
                {
                    cloudByKey[key] = id;
                }
                else
                {
                    locals.Add((id, key,
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6)));
                }
            }
        }

        var adopted = 0;
        foreach (var (localId, key, secretRef, keyRef) in locals)
        {
            if (!cloudByKey.TryGetValue(key, out var cloudId) || cloudId == localId)
            {
                continue;
            }

            await ExecuteAsync(connection, transaction, """
                UPDATE connections SET credential_id = $new WHERE credential_id = $old;
                DELETE FROM credentials WHERE id = $old;
                """, ct, ("$old", localId), ("$new", cloudId));
            await ForgetEntityAsync(connection, transaction, SyncEntityTypes.Credential, localId, ct);
            await ForgetEntityAsync(connection, transaction, SyncEntityTypes.CredentialSecret, localId, ct);
            if (!string.IsNullOrEmpty(secretRef))
            {
                orphanedSecretRefs.Add(secretRef);
            }

            if (!string.IsNullOrEmpty(keyRef))
            {
                orphanedSecretRefs.Add(keyRef);
            }

            adopted++;
        }

        return adopted;
    }

    private static async Task<int> AdoptConnectionsAsync(
        SqliteConnection connection, SqliteTransaction transaction, SyncedIds synced, CancellationToken ct)
    {
        var cloudByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var locals = new List<(string Id, string Key)>();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, name, host, port, protocol FROM connections;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var key = ConnectionKey(
                    reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4));
                if (synced.Contains(SyncEntityTypes.Connection, id))
                {
                    cloudByKey[key] = id;
                }
                else
                {
                    locals.Add((id, key));
                }
            }
        }

        var adopted = 0;
        foreach (var (localId, key) in locals)
        {
            if (!cloudByKey.TryGetValue(key, out var cloudId) || cloudId == localId)
            {
                continue;
            }

            await ExecuteAsync(connection, transaction,
                "DELETE FROM connections WHERE id = $old;", ct, ("$old", localId));
            await ForgetEntityAsync(connection, transaction, SyncEntityTypes.Connection, localId, ct);
            adopted++;
        }

        return adopted;
    }

    // ── 辅助 ────────────────────────────────────────────────────

    /// <summary>本机「已同步过」的实体 Id 集合（即云端那份）。</summary>
    private sealed class SyncedIds
    {
        private readonly Dictionary<string, HashSet<string>> _byType = [];

        public void Add(string entityType, string entityId)
        {
            if (!_byType.TryGetValue(entityType, out var set))
            {
                set = [];
                _byType[entityType] = set;
            }

            set.Add(entityId);
        }

        public bool Contains(string entityType, string entityId) =>
            _byType.TryGetValue(entityType, out var set) && set.Contains(entityId);
    }

    private static async Task<SyncedIds> ReadSyncedIdsAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var synced = new SyncedIds();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT entity_type, entity_id FROM sync_entity_state WHERE server_version > 0;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            synced.Add(reader.GetString(0), reader.GetString(1));
        }

        return synced;
    }

    /// <summary>本机重复行已被云端那份取代：清掉它的同步状态与待推条目（不再推送重复的）。</summary>
    private static Task ForgetEntityAsync(
        SqliteConnection connection, SqliteTransaction transaction, string entityType, string entityId, CancellationToken ct) =>
        ExecuteAsync(connection, transaction, """
            DELETE FROM sync_entity_state WHERE entity_type = $type AND entity_id = $id;
            DELETE FROM sync_outbox WHERE entity_type = $type AND entity_id = $id;
            """, ct, ("$type", entityType), ("$id", entityId));

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken ct,
        params (string Name, string Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static string CredentialKey(string name, int type, string username, string domain) =>
        $"{Normalize(name)}|{type}|{Normalize(username)}|{Normalize(domain)}";

    private static string ConnectionKey(string name, string host, int port, int protocol) =>
        $"{Normalize(name)}|{Normalize(host)}|{port}|{protocol}";

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
