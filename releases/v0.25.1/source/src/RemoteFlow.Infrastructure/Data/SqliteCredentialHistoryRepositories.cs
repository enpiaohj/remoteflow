using Microsoft.Data.Sqlite;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Infrastructure.Data;

/// <summary>
/// 凭据<b>元数据</b>仓储。本类只写入 secret_reference / key_reference 这类引用键，
/// 任何情况下都不会把明文 Secret 写进 SQLite。
/// </summary>
public sealed class SqliteCredentialRepository(RemoteFlowDatabase database) : ICredentialRepository
{
    public async Task<IReadOnlyList<Credential>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM credentials ORDER BY name COLLATE NOCASE;";

        var credentials = new List<Credential>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            credentials.Add(Map(reader));
        }
        return credentials;
    }

    public async Task<Credential?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM credentials WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task AddAsync(Credential credential, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO credentials
                (id, name, type, username, domain, secret_reference, key_reference, description, created_at, updated_at)
            VALUES
                ($id, $name, $type, $username, $domain, $secretRef, $keyRef, $desc, $createdAt, $updatedAt);
            """;
        Bind(command, credential);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetSecretReferencesAsync(
        Guid id, string? passwordReference, string? privateKeyReference, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE credentials SET secret_reference = $secret, key_reference = $key WHERE id = $id;";
        command.Parameters.AddWithValue("$secret", (object?)passwordReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$key", (object?)privateKeyReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(Credential credential, CancellationToken ct = default)
    {
        credential.UpdatedAt = DateTimeOffset.Now;

        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE credentials SET
                name = $name, type = $type, username = $username, domain = $domain,
                secret_reference = $secretRef, key_reference = $keyRef,
                description = $desc, updated_at = $updatedAt
            WHERE id = $id;
            """;
        Bind(command, credential);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        // 引用该凭据的连接由外键 ON DELETE SET NULL 处理，连接本身保留。
        command.CommandText = "DELETE FROM credentials WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(SqliteCommand command, Credential credential)
    {
        command.Parameters.AddWithValue("$id", credential.Id.ToString());
        command.Parameters.AddWithValue("$name", credential.Name);
        command.Parameters.AddWithValue("$type", (int)credential.Type);
        command.Parameters.AddWithValue("$username", credential.Username);
        command.Parameters.AddWithValue("$domain", credential.Domain);
        command.Parameters.AddWithValue("$secretRef", (object?)credential.SecretReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$keyRef", (object?)credential.KeyReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$desc", credential.Description);
        command.Parameters.AddWithValue("$createdAt", credential.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", credential.UpdatedAt.ToString("O"));
    }

    private static Credential Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
        Name = reader.GetString(reader.GetOrdinal("name")),
        Type = (CredentialType)reader.GetInt32(reader.GetOrdinal("type")),
        Username = reader.GetString(reader.GetOrdinal("username")),
        Domain = reader.GetString(reader.GetOrdinal("domain")),
        SecretReference = ReadNullableString(reader, "secret_reference"),
        KeyReference = ReadNullableString(reader, "key_reference"),
        Description = reader.GetString(reader.GetOrdinal("description")),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
    };

    private static string? ReadNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}

/// <summary>
/// 连接历史仓储。只落盘主机、协议、时间、标准错误码，
/// 不记录任何密码、私钥、Token 或剪贴板内容。
/// </summary>
public sealed class SqliteHistoryRepository(RemoteFlowDatabase database) : IHistoryRepository
{
    public async Task<IReadOnlyList<ConnectionHistoryEntry>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM connection_history ORDER BY started_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadEntriesAsync(command, ct);
    }

    public async Task<IReadOnlyList<ConnectionHistoryEntry>> GetByConnectionAsync(
        Guid connectionId, int limit, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT * FROM connection_history WHERE connection_id = $cid ORDER BY started_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$cid", connectionId.ToString());
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadEntriesAsync(command, ct);
    }

    public async Task<int> CountByConnectionAsync(Guid connectionId, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM connection_history WHERE connection_id = $cid;";
        command.Parameters.AddWithValue("$cid", connectionId.ToString());
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    private static async Task<IReadOnlyList<ConnectionHistoryEntry>> ReadEntriesAsync(SqliteCommand command, CancellationToken ct)
    {
        var entries = new List<ConnectionHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new ConnectionHistoryEntry
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                ConnectionId = Guid.Parse(reader.GetString(reader.GetOrdinal("connection_id"))),
                ConnectionName = reader.GetString(reader.GetOrdinal("connection_name")),
                Host = reader.GetString(reader.GetOrdinal("host")),
                Protocol = (ProtocolType)reader.GetInt32(reader.GetOrdinal("protocol")),
                StartedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at"))),
                EndedAt = SqliteConnectionRepository.ReadNullableDate(reader, "ended_at"),
                Result = (ConnectionResult)reader.GetInt32(reader.GetOrdinal("result")),
                ErrorCode = (ConnectionErrorCode)reader.GetInt32(reader.GetOrdinal("error_code"))
            });
        }
        return entries;
    }

    public async Task AddAsync(ConnectionHistoryEntry entry, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO connection_history
                (id, connection_id, connection_name, host, protocol, started_at, ended_at, result, error_code)
            VALUES
                ($id, $connectionId, $name, $host, $protocol, $startedAt, $endedAt, $result, $errorCode);
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString());
        command.Parameters.AddWithValue("$connectionId", entry.ConnectionId.ToString());
        command.Parameters.AddWithValue("$name", entry.ConnectionName);
        command.Parameters.AddWithValue("$host", entry.Host);
        command.Parameters.AddWithValue("$protocol", (int)entry.Protocol);
        command.Parameters.AddWithValue("$startedAt", entry.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$endedAt", (object?)entry.EndedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$result", (int)entry.Result);
        command.Parameters.AddWithValue("$errorCode", (int)entry.ErrorCode);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteAsync(
        Guid entryId, DateTimeOffset endedAt, ConnectionResult result, ConnectionErrorCode errorCode, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE connection_history
            SET ended_at = $endedAt, result = $result, error_code = $errorCode
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$endedAt", endedAt.ToString("O"));
        command.Parameters.AddWithValue("$result", (int)result);
        command.Parameters.AddWithValue("$errorCode", (int)errorCode);
        command.Parameters.AddWithValue("$id", entryId.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM connection_history;";
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>已信任 SSH Host Key 仓储。</summary>
public sealed class SqliteHostKeyRepository(RemoteFlowDatabase database) : IHostKeyRepository
{
    public async Task<SshHostKeyRecord?> GetAsync(string host, int port, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT host_key, key_algorithm, fingerprint, trusted_at FROM ssh_host_keys WHERE host_key = $key;";
        command.Parameters.AddWithValue("$key", SshHostKeyRecord.BuildHostKey(host, port));

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<SshHostKeyRecord>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT host_key, key_algorithm, fingerprint, trusted_at FROM ssh_host_keys ORDER BY host_key;";

        var records = new List<SshHostKeyRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(Map(reader));
        }
        return records;
    }

    public async Task SaveAsync(SshHostKeyRecord record, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ssh_host_keys (host_key, key_algorithm, fingerprint, trusted_at)
            VALUES ($key, $alg, $fp, $at)
            ON CONFLICT(host_key) DO UPDATE SET
                key_algorithm = excluded.key_algorithm,
                fingerprint = excluded.fingerprint,
                trusted_at = excluded.trusted_at;
            """;
        command.Parameters.AddWithValue("$key", record.HostKey);
        command.Parameters.AddWithValue("$alg", record.KeyAlgorithm);
        command.Parameters.AddWithValue("$fp", record.Fingerprint);
        command.Parameters.AddWithValue("$at", record.TrustedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string host, int port, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ssh_host_keys WHERE host_key = $key;";
        command.Parameters.AddWithValue("$key", SshHostKeyRecord.BuildHostKey(host, port));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ssh_host_keys;";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static SshHostKeyRecord Map(SqliteDataReader reader) => new()
    {
        HostKey = reader.GetString(0),
        KeyAlgorithm = reader.GetString(1),
        Fingerprint = reader.GetString(2),
        TrustedAt = DateTimeOffset.Parse(reader.GetString(3))
    };
}
