using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Infrastructure.Sync.Sources;

/// <summary>connection 实体的同步源。整条 <see cref="ConnectionProfile"/>（含 TagIds / 收藏 / 备注 / 协议参数）作为一个 Payload。</summary>
public sealed class ConnectionSyncSource(IConnectionRepository connections) : ISyncEntitySource
{
    public int SchemaVersion => 1;

    public IReadOnlyList<string> EntityTypes => [SyncEntityTypes.Connection];

    public bool Handles(string entityType) => entityType == SyncEntityTypes.Connection;

    public async Task<IReadOnlyList<string>> ListEntityIdsAsync(string entityType, CancellationToken ct = default) =>
        [.. (await connections.GetAllAsync(ct)).Select(c => c.Id.ToString())];

    public async Task<byte[]?> GetPlaintextAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        var profile = await connections.GetByIdAsync(Guid.Parse(entityId), ct);
        return profile is null ? null : SyncSerializer.Serialize(profile, SchemaVersion);
    }

    public async Task ApplyAsync(
        string entityType, string entityId, byte[]? plaintext, bool deleted, int schemaVersion,
        CancellationToken ct = default)
    {
        var id = Guid.Parse(entityId);
        if (deleted)
        {
            await connections.DeleteAsync(id, ct);
            return;
        }

        var (_, profile) = SyncSerializer.Deserialize<ConnectionProfile>(plaintext!);
        profile.Id = id;
        if (await connections.GetByIdAsync(id, ct) is null)
        {
            await connections.AddAsync(profile, ct);
        }
        else
        {
            await connections.UpdateAsync(profile, ct);
        }
    }

    /// <summary>连接冲突的 LWW 依据：内容更新时间的「最后一笔」为准。</summary>
    public DateTimeOffset? ReadContentModifiedAt(byte[] plaintext, int schemaVersion)
    {
        var (_, profile) = SyncSerializer.Deserialize<ConnectionProfile>(plaintext);
        return profile.UpdatedAt;
    }
}
