using RemoteFlow.Core.Cloud;

namespace RemoteFlow.IntegrationTests.Cloud;

/// <summary>
/// 复刻 AppsCloud G4 Sync 语义的内存服务端：单调 Revision、按 baseVersion 乐观并发、
/// OperationId 幂等、Tombstone。供 SyncCoordinator 单元测试用；真实契约由 CloudRoundTripTests 覆盖。
/// </summary>
public sealed class InMemoryCloudServer
{
    private readonly Dictionary<(string Type, string Id), Entry> _entities = [];
    private readonly Dictionary<string, SyncPushOperationResult> _operations = [];
    private long _revision;

    private sealed record Entry(
        long Version, long Revision, int KeyVersion, int SchemaVersion,
        bool Deleted, byte[]? Ciphertext, byte[]? Nonce);

    /// <summary>注入损坏：把某实体的密文首字节翻转，模拟解密失败。</summary>
    public void CorruptCiphertext(string entityType, string entityId)
    {
        var key = (entityType, entityId);
        var e = _entities[key];
        var bad = (byte[])e.Ciphertext!.Clone();
        bad[0] ^= 0xFF;
        _entities[key] = e with { Ciphertext = bad };
    }

    public SyncPushResponse Push(IReadOnlyList<SyncPushOperation> operations)
    {
        var results = new List<SyncPushOperationResult>();
        foreach (var op in operations)
        {
            if (_operations.TryGetValue(op.OperationId, out var prior))
            {
                results.Add(prior with { Status = SyncPushStatus.Duplicate });
                continue;
            }

            var key = (op.EntityType, op.EntityId);
            _entities.TryGetValue(key, out var existing);
            var isUpsert = op.OperationType == SyncPushOperationType.Upsert;

            var canCreate = isUpsert && op.BaseVersion == 0 && existing is null or { Deleted: true };
            var canUpdate = op.BaseVersion > 0 && existing is { Deleted: false }
                && existing.Version == op.BaseVersion;

            SyncPushOperationResult result;
            if (canCreate || (canUpdate && isUpsert))
            {
                var version = existing is null ? 1 : existing.Version + 1;
                var entry = new Entry(version, ++_revision, op.KeyVersion, op.SchemaVersion, false, op.Ciphertext, op.Nonce);
                _entities[key] = entry;
                result = new SyncPushOperationResult(
                    op.OperationId, SyncPushStatus.Applied, op.EntityType, op.EntityId, version, entry.Revision, null);
            }
            else if (canUpdate)
            {
                var entry = new Entry(existing!.Version + 1, ++_revision, op.KeyVersion, op.SchemaVersion, true, null, null);
                _entities[key] = entry;
                result = new SyncPushOperationResult(
                    op.OperationId, SyncPushStatus.Applied, op.EntityType, op.EntityId, entry.Version, entry.Revision, null);
            }
            else
            {
                result = new SyncPushOperationResult(
                    op.OperationId, SyncPushStatus.Conflict, op.EntityType, op.EntityId, null, null,
                    existing is null
                        ? null
                        : new SyncServerEntity(
                            op.EntityType, op.EntityId, existing.Version, existing.Revision,
                            existing.KeyVersion, existing.SchemaVersion, existing.Deleted,
                            existing.Ciphertext, existing.Nonce));
            }

            _operations[op.OperationId] = result;
            results.Add(result);
        }

        return new SyncPushResponse(_revision, results);
    }

    public SyncPullPage Pull(long cursor, int limit)
    {
        var ordered = _entities
            .Where(kv => kv.Value.Revision > cursor)
            .OrderBy(kv => kv.Value.Revision)
            .Take(limit + 1)
            .Select(kv => new SyncPulledChange(
                kv.Key.Type, kv.Key.Id, kv.Value.Version, kv.Value.Revision, kv.Value.KeyVersion,
                kv.Value.SchemaVersion, kv.Value.Deleted, kv.Value.Ciphertext, kv.Value.Nonce))
            .ToList();

        var hasMore = ordered.Count > limit;
        var changes = hasMore ? ordered.Take(limit).ToList() : ordered;
        var next = changes.Count > 0 ? changes[^1].Revision : cursor;
        return new SyncPullPage(changes, next, hasMore);
    }
}
