using RemoteFlow.Core.Abstractions;
using RemoteFlow.Core.Cloud;
using RemoteFlow.Core.Models;

namespace RemoteFlow.Infrastructure.Sync.Sources;

/// <summary>tag 实体的同步源。</summary>
public sealed class TagSyncSource(ITagRepository tags) : ISyncEntitySource
{
    public int SchemaVersion => 1;

    public IReadOnlyList<string> EntityTypes => [SyncEntityTypes.Tag];

    public bool Handles(string entityType) => entityType == SyncEntityTypes.Tag;

    public async Task<IReadOnlyList<string>> ListEntityIdsAsync(string entityType, CancellationToken ct = default) =>
        [.. (await tags.GetAllAsync(ct)).Select(t => t.Id.ToString())];

    public async Task<byte[]?> GetPlaintextAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        var id = Guid.Parse(entityId);
        var tag = (await tags.GetAllAsync(ct)).FirstOrDefault(t => t.Id == id);
        return tag is null ? null : SyncSerializer.Serialize(tag, SchemaVersion);
    }

    public async Task ApplyAsync(
        string entityType, string entityId, byte[]? plaintext, bool deleted, int schemaVersion,
        CancellationToken ct = default)
    {
        var id = Guid.Parse(entityId);
        if (deleted)
        {
            await tags.DeleteAsync(id, ct);
            return;
        }

        var (_, tag) = SyncSerializer.Deserialize<Tag>(plaintext!);
        tag.Id = id;
        if ((await tags.GetAllAsync(ct)).Any(t => t.Id == id))
        {
            await tags.UpdateAsync(tag, ct);
        }
        else
        {
            await tags.AddAsync(tag, ct);
        }
    }
}
