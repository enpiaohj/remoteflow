using Microsoft.Data.Sqlite;
using RemoteFlow.Core.Vmware;

namespace RemoteFlow.Infrastructure.Data;

/// <summary>ESXi 来源的 SQLite 仓储。凭据外键为 ON DELETE SET NULL：凭据被删后来源保留，提示重新选择凭据。</summary>
public sealed class SqliteVmwareSourceRepository(RemoteFlowDatabase database) : IVmwareSourceRepository
{
    public async Task<IReadOnlyList<VmwareSource>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM vmware_sources ORDER BY sort_order, name COLLATE NOCASE;";

        var results = new List<VmwareSource>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<VmwareSource?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM vmware_sources WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task AddAsync(VmwareSource source, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO vmware_sources
                (id, name, host, port, kind, credential_id, cert_pin, sort_order, created_at, updated_at)
            VALUES
                ($id, $name, $host, $port, $kind, $credentialId, $certPin, $sortOrder, $createdAt, $updatedAt);
            """;
        Bind(command, source);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(VmwareSource source, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE vmware_sources SET
                name = $name, host = $host, port = $port, kind = $kind, credential_id = $credentialId,
                cert_pin = $certPin, sort_order = $sortOrder, updated_at = $updatedAt
            WHERE id = $id;
            """;
        Bind(command, source);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM vmware_sources WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(SqliteCommand command, VmwareSource source)
    {
        command.Parameters.AddWithValue("$id", source.Id.ToString());
        command.Parameters.AddWithValue("$name", source.Name);
        command.Parameters.AddWithValue("$host", source.Host);
        command.Parameters.AddWithValue("$port", source.Port);
        command.Parameters.AddWithValue("$kind", (int)source.Kind);
        command.Parameters.AddWithValue("$credentialId", (object?)source.CredentialId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$certPin", source.CertPinSha256);
        command.Parameters.AddWithValue("$sortOrder", source.SortOrder);
        command.Parameters.AddWithValue("$createdAt", source.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", source.UpdatedAt.ToString("O"));
    }

    private static VmwareSource Map(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
        Name = reader.GetString(reader.GetOrdinal("name")),
        Host = reader.GetString(reader.GetOrdinal("host")),
        Port = reader.GetInt32(reader.GetOrdinal("port")),
        Kind = (VmwareSourceKind)reader.GetInt32(reader.GetOrdinal("kind")),
        CredentialId = SqliteConnectionRepository.ReadNullableGuid(reader, "credential_id"),
        CertPinSha256 = reader.GetString(reader.GetOrdinal("cert_pin")),
        SortOrder = reader.GetInt32(reader.GetOrdinal("sort_order")),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
    };
}
