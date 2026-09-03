using Microsoft.Data.Sqlite;
using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Infrastructure.Data;

/// <summary>分组仓储。删除分组时不静默删除组内连接。</summary>
public sealed class SqliteGroupRepository(RemoteFlowDatabase database) : IGroupRepository
{
    public async Task<IReadOnlyList<ConnectionGroup>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, parent_id, sort_order, icon FROM connection_groups ORDER BY sort_order, name COLLATE NOCASE;";

        var groups = new List<ConnectionGroup>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            groups.Add(new ConnectionGroup
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                ParentId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                SortOrder = reader.GetInt32(3),
                Icon = reader.GetString(4)
            });
        }
        return groups;
    }

    public async Task AddAsync(ConnectionGroup group, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO connection_groups (id, name, parent_id, sort_order, icon)
            VALUES ($id, $name, $parentId, $sortOrder, $icon);
            """;
        Bind(command, group);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(ConnectionGroup group, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE connection_groups
            SET name = $name, parent_id = $parentId, sort_order = $sortOrder, icon = $icon
            WHERE id = $id;
            """;
        Bind(command, group);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 删除分组。组内连接与子分组会先迁移到 <paramref name="moveConnectionsTo"/>
    /// （null 表示移动到「未分组」），绝不随分组一并删除。
    /// </summary>
    public async Task DeleteAsync(Guid id, Guid? moveConnectionsTo, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var target = (object?)moveConnectionsTo?.ToString() ?? DBNull.Value;

        await using (var moveConnections = connection.CreateCommand())
        {
            moveConnections.Transaction = transaction;
            moveConnections.CommandText = "UPDATE connections SET group_id = $target WHERE group_id = $id;";
            moveConnections.Parameters.AddWithValue("$target", target);
            moveConnections.Parameters.AddWithValue("$id", id.ToString());
            await moveConnections.ExecuteNonQueryAsync(ct);
        }

        await using (var moveChildren = connection.CreateCommand())
        {
            moveChildren.Transaction = transaction;
            moveChildren.CommandText = "UPDATE connection_groups SET parent_id = $target WHERE parent_id = $id;";
            moveChildren.Parameters.AddWithValue("$target", target);
            moveChildren.Parameters.AddWithValue("$id", id.ToString());
            await moveChildren.ExecuteNonQueryAsync(ct);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM connection_groups WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", id.ToString());
            await delete.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static void Bind(SqliteCommand command, ConnectionGroup group)
    {
        command.Parameters.AddWithValue("$id", group.Id.ToString());
        command.Parameters.AddWithValue("$name", group.Name);
        command.Parameters.AddWithValue("$parentId", (object?)group.ParentId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$sortOrder", group.SortOrder);
        command.Parameters.AddWithValue("$icon", group.Icon);
    }
}

/// <summary>标签仓储。</summary>
public sealed class SqliteTagRepository(RemoteFlowDatabase database) : ITagRepository
{
    public async Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, description, color FROM tags ORDER BY name COLLATE NOCASE;";

        var tags = new List<Tag>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tags.Add(new Tag
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                Description = reader.GetString(2),
                Color = reader.GetString(3)
            });
        }
        return tags;
    }

    public async Task AddAsync(Tag tag, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO tags (id, name, description, color) VALUES ($id, $name, $desc, $color);";
        Bind(command, tag);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(Tag tag, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tags SET name = $name, description = $desc, color = $color WHERE id = $id;";
        Bind(command, tag);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        // connection_tags 由外键 CASCADE 清理，连接本身不受影响。
        command.CommandText = "DELETE FROM tags WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(SqliteCommand command, Tag tag)
    {
        command.Parameters.AddWithValue("$id", tag.Id.ToString());
        command.Parameters.AddWithValue("$name", tag.Name);
        command.Parameters.AddWithValue("$desc", tag.Description);
        command.Parameters.AddWithValue("$color", tag.Color);
    }
}
