using System.Text.Json;
using Microsoft.Data.Sqlite;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Infrastructure.Data;

/// <summary>
/// 连接资产的 SQLite 仓储实现。
/// 协议专项参数（RDP/SSH/VNC）以 JSON 存入 <c>options_json</c> 列，
/// 新增协议参数时无需 Schema 迁移。
/// </summary>
public sealed class SqliteConnectionRepository(RemoteFlowDatabase database) : IConnectionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>协议参数的 JSON 载体。</summary>
    private sealed record OptionsPayload(RdpOptions Rdp, SshOptions Ssh, VncOptions Vnc);

    public async Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();

        // 一次性把标签关联读出来，避免每条连接一次查询造成 N+1。
        var tagMap = await LoadTagMapAsync(connection, ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM connections ORDER BY name COLLATE NOCASE;";

        var results = new List<ConnectionProfile>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var profile = Map(reader);
            if (tagMap.TryGetValue(profile.Id, out var tags))
            {
                profile.TagIds = tags;
            }
            results.Add(profile);
        }

        return results;
    }

    public async Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM connections WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var profile = Map(reader);
        await reader.CloseAsync();
        profile.TagIds = await LoadTagsForConnectionAsync(connection, id, ct);
        return profile;
    }

    public async Task AddAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO connections
                    (id, name, host, port, protocol, group_id, credential_id, favorite, notes,
                     created_at, updated_at, last_connected_at, options_json)
                VALUES
                    ($id, $name, $host, $port, $protocol, $groupId, $credentialId, $favorite, $notes,
                     $createdAt, $updatedAt, $lastConnectedAt, $optionsJson);
                """;
            BindProfile(command, profile);
            await command.ExecuteNonQueryAsync(ct);
        }

        await ReplaceTagsAsync(connection, transaction, profile, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task UpdateAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        profile.UpdatedAt = DateTimeOffset.Now;

        await using var connection = database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE connections SET
                    name = $name, host = $host, port = $port, protocol = $protocol,
                    group_id = $groupId, credential_id = $credentialId, favorite = $favorite,
                    notes = $notes, updated_at = $updatedAt, last_connected_at = $lastConnectedAt,
                    options_json = $optionsJson
                WHERE id = $id;
                """;
            BindProfile(command, profile);
            await command.ExecuteNonQueryAsync(ct);
        }

        await ReplaceTagsAsync(connection, transaction, profile, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        // 关联记录必须一并清理，否则会留下孤儿：
        //  - connection_tags 由外键 ON DELETE CASCADE 自动删；
        //  - connection_history（最近活动 / 连接历史）没有外键，必须显式删，否则「最近活动」里
        //    会残留已删除连接的历史条目。
        await using (var deleteHistory = connection.CreateCommand())
        {
            deleteHistory.Transaction = transaction;
            deleteHistory.CommandText = "DELETE FROM connection_history WHERE connection_id = $id;";
            deleteHistory.Parameters.AddWithValue("$id", id.ToString());
            await deleteHistory.ExecuteNonQueryAsync(ct);
        }

        await using (var deleteConnection = connection.CreateCommand())
        {
            deleteConnection.Transaction = transaction;
            deleteConnection.CommandText = "DELETE FROM connections WHERE id = $id;";
            deleteConnection.Parameters.AddWithValue("$id", id.ToString());
            await deleteConnection.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task TouchLastConnectedAsync(Guid id, DateTimeOffset when, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE connections SET last_connected_at = $when WHERE id = $id;";
        command.Parameters.AddWithValue("$when", when.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountByCredentialAsync(Guid credentialId, CancellationToken ct = default)
        => await CountAsync("SELECT COUNT(*) FROM connections WHERE credential_id = $id;", credentialId, ct);

    public async Task<int> CountByGroupAsync(Guid groupId, CancellationToken ct = default)
        => await CountAsync("SELECT COUNT(*) FROM connections WHERE group_id = $id;", groupId, ct);

    private async Task<int> CountAsync(string sql, Guid id, CancellationToken ct)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    // ── 映射与绑定 ────────────────────────────────────────────────

    private static void BindProfile(SqliteCommand command, ConnectionProfile profile)
    {
        var options = new OptionsPayload(profile.Rdp, profile.Ssh, profile.Vnc);

        command.Parameters.AddWithValue("$id", profile.Id.ToString());
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$host", profile.Host);
        command.Parameters.AddWithValue("$port", profile.Port);
        command.Parameters.AddWithValue("$protocol", (int)profile.Protocol);
        command.Parameters.AddWithValue("$groupId", (object?)profile.GroupId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$credentialId", (object?)profile.CredentialId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$favorite", profile.Favorite ? 1 : 0);
        command.Parameters.AddWithValue("$notes", profile.Notes);
        command.Parameters.AddWithValue("$createdAt", profile.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", profile.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$lastConnectedAt",
            (object?)profile.LastConnectedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$optionsJson", JsonSerializer.Serialize(options, JsonOptions));
    }

    private static ConnectionProfile Map(SqliteDataReader reader)
    {
        var optionsJson = reader.GetString(reader.GetOrdinal("options_json"));
        OptionsPayload? options = null;
        try
        {
            options = JsonSerializer.Deserialize<OptionsPayload>(optionsJson, JsonOptions);
        }
        catch (JsonException)
        {
            // 参数损坏不应导致整个连接列表加载失败，退回默认值即可。
        }

        return new ConnectionProfile
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            Name = reader.GetString(reader.GetOrdinal("name")),
            Host = reader.GetString(reader.GetOrdinal("host")),
            Port = reader.GetInt32(reader.GetOrdinal("port")),
            Protocol = (ProtocolType)reader.GetInt32(reader.GetOrdinal("protocol")),
            GroupId = ReadNullableGuid(reader, "group_id"),
            CredentialId = ReadNullableGuid(reader, "credential_id"),
            Favorite = reader.GetInt32(reader.GetOrdinal("favorite")) != 0,
            Notes = reader.GetString(reader.GetOrdinal("notes")),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
            LastConnectedAt = ReadNullableDate(reader, "last_connected_at"),
            Rdp = options?.Rdp ?? new RdpOptions(),
            Ssh = options?.Ssh ?? new SshOptions(),
            Vnc = options?.Vnc ?? new VncOptions()
        };
    }

    internal static Guid? ReadNullableGuid(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
    }

    internal static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
    }

    // ── 标签关联 ──────────────────────────────────────────────────

    private static async Task<Dictionary<Guid, List<Guid>>> LoadTagMapAsync(SqliteConnection connection, CancellationToken ct)
    {
        var map = new Dictionary<Guid, List<Guid>>();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT connection_id, tag_id FROM connection_tags;";

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var connectionId = Guid.Parse(reader.GetString(0));
            var tagId = Guid.Parse(reader.GetString(1));

            if (!map.TryGetValue(connectionId, out var list))
            {
                list = [];
                map[connectionId] = list;
            }
            list.Add(tagId);
        }

        return map;
    }

    private static async Task<List<Guid>> LoadTagsForConnectionAsync(SqliteConnection connection, Guid connectionId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tag_id FROM connection_tags WHERE connection_id = $id;";
        command.Parameters.AddWithValue("$id", connectionId.ToString());

        var tags = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tags.Add(Guid.Parse(reader.GetString(0)));
        }
        return tags;
    }

    private static async Task ReplaceTagsAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConnectionProfile profile, CancellationToken ct)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM connection_tags WHERE connection_id = $id;";
            delete.Parameters.AddWithValue("$id", profile.Id.ToString());
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var tagId in profile.TagIds.Distinct())
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO connection_tags (connection_id, tag_id) VALUES ($cid, $tid);";
            insert.Parameters.AddWithValue("$cid", profile.Id.ToString());
            insert.Parameters.AddWithValue("$tid", tagId.ToString());
            await insert.ExecuteNonQueryAsync(ct);
        }
    }
}
