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
        command.CommandText =
            "SELECT id, name, parent_id, sort_order, icon, is_system, is_default, is_protected FROM connection_groups ORDER BY sort_order, name COLLATE NOCASE;";

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
                Icon = reader.GetString(4),
                IsSystem = reader.GetInt32(5) != 0,
                IsDefault = reader.GetBoolean(reader.GetOrdinal("is_default")),
                IsProtected = reader.GetBoolean(reader.GetOrdinal("is_protected"))
            });
        }
        return groups;
    }

    public async Task AddAsync(ConnectionGroup group, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO connection_groups (id, name, parent_id, sort_order, icon, is_system, is_default, is_protected)
            VALUES ($id, $name, $parentId, $sortOrder, $icon, $isSystem, $isDefault, $isProtected);
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
            SET name = $name, parent_id = $parentId, sort_order = $sortOrder, icon = $icon,
                is_system = $isSystem, is_default = $isDefault, is_protected = $isProtected
            WHERE id = $id;
            """;
        Bind(command, group);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 删除分组：组内连接迁移到 <paramref name="moveConnectionsTo"/>（null → 「未分组」），
    /// 子分组提升到被删除分组的父级（§16）。绝不随分组一并删除连接。
    /// </summary>
    public async Task DeleteAsync(Guid id, Guid? moveConnectionsTo, CancellationToken ct = default)
    {
        await using var connection = database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var connectionTarget = (object?)moveConnectionsTo?.ToString() ?? DBNull.Value;

        // 先取被删除分组自己的 parent_id——子分组要提升到这里。
        object? parentTarget = DBNull.Value;
        await using (var readParent = connection.CreateCommand())
        {
            readParent.Transaction = transaction;
            readParent.CommandText = "SELECT parent_id FROM connection_groups WHERE id = $id;";
            readParent.Parameters.AddWithValue("$id", id.ToString());
            var result = await readParent.ExecuteScalarAsync(ct);
            if (result is string parent)
            {
                parentTarget = parent;
            }
        }

        await using (var moveConnections = connection.CreateCommand())
        {
            moveConnections.Transaction = transaction;
            moveConnections.CommandText = "UPDATE connections SET group_id = $target WHERE group_id = $id;";
            moveConnections.Parameters.AddWithValue("$target", connectionTarget);
            moveConnections.Parameters.AddWithValue("$id", id.ToString());
            await moveConnections.ExecuteNonQueryAsync(ct);
        }

        await using (var promoteChildren = connection.CreateCommand())
        {
            promoteChildren.Transaction = transaction;
            promoteChildren.CommandText = "UPDATE connection_groups SET parent_id = $target WHERE parent_id = $id;";
            promoteChildren.Parameters.AddWithValue("$target", parentTarget);
            promoteChildren.Parameters.AddWithValue("$id", id.ToString());
            await promoteChildren.ExecuteNonQueryAsync(ct);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            // 系统分组永不删除，这里加一道防线。
            delete.CommandText = "DELETE FROM connection_groups WHERE id = $id AND is_system = 0;";
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
        command.Parameters.AddWithValue("$isSystem", group.IsSystem ? 1 : 0);
        command.Parameters.AddWithValue("$isDefault", group.IsDefault ? 1 : 0);
        command.Parameters.AddWithValue("$isProtected", group.IsProtected ? 1 : 0);
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
