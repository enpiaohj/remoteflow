using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary>
/// 同步冲突的记录与解决。服务端看不到明文，冲突只能检测不能自动合并——
/// 用户在 UI 里选择「保留本机 / 使用云端」。
/// </summary>
public sealed class ConflictService(
    SqliteSyncStore store,
    IEnumerable<ISyncEntitySource> sources,
    ILogger<ConflictService> logger)
{
    private readonly IReadOnlyList<ISyncEntitySource> _sources = [.. sources];

    /// <summary>由 SyncCoordinator 调用：记录一个未决冲突并把实体标记为冲突态。</summary>
    public async Task RecordAsync(
        string entityType,
        string entityId,
        EncryptedPayload? local,
        SyncServerEntity? remote,
        CancellationToken ct = default)
    {
        var remoteEntity = remote ?? new SyncServerEntity(
            entityType, entityId, 0, 0, 0, 0, Deleted: true, Ciphertext: null, Nonce: null);
        await store.RecordConflictAsync(entityType, entityId, local, remoteEntity, ct);
        await store.SetConflictStateAsync(entityType, entityId, conflicted: true, ct);
        logger.LogInformation("已记录同步冲突：{Type}/{Id}", entityType, entityId);
    }

    public Task<IReadOnlyList<SyncConflictRecord>> ListAsync(CancellationToken ct = default) =>
        store.ListUnresolvedAsync(ct);

    /// <summary>解决一个冲突。<paramref name="context"/> 提供 UseRemote 时解密远端副本所需的会话。</summary>
    public async Task ResolveAsync(
        SyncContext context, Guid conflictId, ConflictResolution resolution, CancellationToken ct = default)
    {
        var conflict = (await store.ListUnresolvedAsync(ct)).FirstOrDefault(c => c.Id == conflictId);
        if (conflict is null)
        {
            return;
        }

        switch (resolution)
        {
            case ConflictResolution.KeepLocal:
                await KeepLocalAsync(conflict, ct);
                break;

            case ConflictResolution.UseRemote:
                await UseRemoteAsync(context, conflict, ct);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Cannot resolve to Unresolved.");
        }

        await store.SetConflictStateAsync(conflict.EntityType, conflict.EntityId, conflicted: false, ct);
        await store.ResolveConflictAsync(conflictId, resolution, ct);
        logger.LogInformation("冲突 {Id} 已按 {Resolution} 解决", conflictId, resolution);
    }

    private async Task KeepLocalAsync(SyncConflictRecord conflict, CancellationToken ct)
    {
        // 让本机版本以远端当前版本为 baseVersion 重新入队，下一轮 Push 覆盖远端。
        await store.SetServerVersionAsync(conflict.EntityType, conflict.EntityId, conflict.Remote.Version, ct);
        var operation = conflict.Local is null ? OutboxOperationType.Delete : OutboxOperationType.Upsert;
        await store.EnqueueAsync(conflict.EntityType, conflict.EntityId, operation, conflict.Remote.Version, ct);
    }

    private async Task UseRemoteAsync(SyncContext context, SyncConflictRecord conflict, CancellationToken ct)
    {
        var source = _sources.FirstOrDefault(s => s.Handles(conflict.EntityType))
            ?? throw new InvalidOperationException($"No sync source handles '{conflict.EntityType}'.");

        if (conflict.Remote.Deleted)
        {
            await source.ApplyAsync(
                conflict.EntityType, conflict.EntityId, null, deleted: true, conflict.Remote.SchemaVersion, ct);
        }
        else
        {
            var plaintext = context.Session.Decrypt(
                new PayloadContext(
                    context.UserId, context.AppId, conflict.EntityType, conflict.EntityId,
                    conflict.Remote.SchemaVersion, conflict.Remote.KeyVersion),
                new EncryptedPayload(
                    conflict.Remote.Ciphertext!, conflict.Remote.Nonce!,
                    conflict.Remote.KeyVersion, conflict.Remote.SchemaVersion));
            await source.ApplyAsync(
                conflict.EntityType, conflict.EntityId, plaintext, deleted: false,
                conflict.Remote.SchemaVersion, ct);
        }

        await store.SetServerVersionAsync(conflict.EntityType, conflict.EntityId, conflict.Remote.Version, ct);
        await store.DeleteEntityAsync(conflict.EntityType, conflict.EntityId, ct);
    }
}
