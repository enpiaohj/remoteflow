using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary>
/// 把业务变更登记进 <c>sync_outbox</c>——仅当 <see cref="CloudSyncGate.Enabled"/> 为真。
/// 写失败不抛（best-effort），由对账兜底。
/// </summary>
public sealed class OutboxSyncChangeTracker(
    SqliteSyncStore store, CloudSyncGate gate, ILogger<OutboxSyncChangeTracker> logger)
    : ISyncChangeTracker
{
    public Task TrackUpsertAsync(string entityType, string entityId, CancellationToken ct = default) =>
        EnqueueAsync(entityType, entityId, OutboxOperationType.Upsert, ct);

    public Task TrackDeleteAsync(string entityType, string entityId, CancellationToken ct = default) =>
        EnqueueAsync(entityType, entityId, OutboxOperationType.Delete, ct);

    private async Task EnqueueAsync(
        string entityType, string entityId, OutboxOperationType operation, CancellationToken ct)
    {
        if (!gate.Enabled)
        {
            return;
        }

        try
        {
            var baseVersion = await store.GetServerVersionAsync(entityType, entityId, ct);
            await store.EnqueueAsync(entityType, entityId, operation, baseVersion, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "登记同步变更失败：{Type}/{Id}（将由对账补偿）", entityType, entityId);
        }
    }
}
