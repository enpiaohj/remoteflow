namespace RemoteFlow.Core.Cloud;

/// <summary>
/// 业务服务在成功写入本地后调用，登记一条待同步变更。
/// 未启用云同步时注入 <see cref="NoOpSyncChangeTracker"/>；启用后换成写 Outbox 的实现。
/// best-effort：崩溃窗口由 SyncCoordinator 的对账兜底。
/// </summary>
public interface ISyncChangeTracker
{
    Task TrackUpsertAsync(string entityType, string entityId, CancellationToken ct = default);

    Task TrackDeleteAsync(string entityType, string entityId, CancellationToken ct = default);
}

/// <summary>云同步未启用时的空实现。</summary>
public sealed class NoOpSyncChangeTracker : ISyncChangeTracker
{
    public static readonly NoOpSyncChangeTracker Instance = new();

    public Task TrackUpsertAsync(string entityType, string entityId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task TrackDeleteAsync(string entityType, string entityId, CancellationToken ct = default) =>
        Task.CompletedTask;
}
