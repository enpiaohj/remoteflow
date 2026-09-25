using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Infrastructure.Sync.Sources;

/// <summary>
/// group 实体的同步源。系统兜底分组「未分组」及其它 <see cref="ConnectionGroup.IsSystem"/> 分组
/// 是本机概念，不参与同步。
/// </summary>
public sealed class GroupSyncSource(IGroupRepository groups) : ISyncEntitySource
{
    public int SchemaVersion => 1;

    public IReadOnlyList<string> EntityTypes => [SyncEntityTypes.Group];

    public bool Handles(string entityType) => entityType == SyncEntityTypes.Group;

    public async Task<IReadOnlyList<string>> ListEntityIdsAsync(string entityType, CancellationToken ct = default) =>
        [.. (await groups.GetAllAsync(ct)).Where(IsSyncable).Select(g => g.Id.ToString())];

    public async Task<byte[]?> GetPlaintextAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        var id = Guid.Parse(entityId);
        var group = (await groups.GetAllAsync(ct)).FirstOrDefault(g => g.Id == id);
        return group is null || !IsSyncable(group) ? null : SyncSerializer.Serialize(group, SchemaVersion);
    }

    public async Task ApplyAsync(
        string entityType, string entityId, byte[]? plaintext, bool deleted, int schemaVersion,
        CancellationToken ct = default)
    {
        var id = Guid.Parse(entityId);
        if (id == ConnectionGroup.UngroupedId)
        {
            return; // 系统分组不接受远端变更
        }

        if (deleted)
        {
            // 组内连接迁到「未分组」，绝不随分组删除连接
            await groups.DeleteAsync(id, moveConnectionsTo: null, ct);
            return;
        }

        var (_, group) = SyncSerializer.Deserialize<ConnectionGroup>(plaintext!);
        group.Id = id;
        group.IsSystem = false;
        if ((await groups.GetAllAsync(ct)).Any(g => g.Id == id))
        {
            await groups.UpdateAsync(group, ct);
        }
        else
        {
            await groups.AddAsync(group, ct);
        }
    }

    private static bool IsSyncable(ConnectionGroup group) =>
        !group.IsSystem && group.Id != ConnectionGroup.UngroupedId;
}
