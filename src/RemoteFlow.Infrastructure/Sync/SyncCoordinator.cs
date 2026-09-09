using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RemoteFlow.Core.Cloud;

namespace RemoteFlow.Infrastructure.Sync;

/// <summary>调用一次同步循环需要的、随解锁状态变化的上下文。</summary>
public sealed record SyncContext(Guid UserId, string AppId, int KeyVersion, IVaultSession Session);

/// <summary>
/// Push / Pull 状态机。一次 <see cref="RunOnceAsync"/>：先推完到期 Outbox，再从游标拉取并落地。
/// Push 409 与「本地有未推变更时拉到远端改动」都记为冲突（<see cref="ConflictService"/> 处理），
/// 不做无限自动覆盖。解密失败即停止且不推进游标（协议设计 §17）。
/// </summary>
public sealed class SyncCoordinator(
    ICloudClient client,
    SqliteSyncStore store,
    IEnumerable<ISyncEntitySource> sources,
    ConflictService conflictService,
    ILogger<SyncCoordinator> logger,
    SyncOptions? options = null,
    TimeProvider? timeProvider = null)
{
    private readonly SyncOptions _options = options ?? new SyncOptions();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly IReadOnlyList<ISyncEntitySource> _sources = [.. sources];

    public async Task<SyncRunResult> RunOnceAsync(SyncContext context, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        await store.SetStatusAsync(context.AppId, SyncStatus.Syncing, attemptAt: now, successAt: null, ct);

        int pushed = 0, pushConflicts = 0, pulled = 0, pullConflicts = 0;
        var status = SyncStatus.Synced;
        long cursor = (await store.GetStateAsync(context.AppId, ct)).Cursor;

        try
        {
            // 首次同步（游标为 0 且尚无待推变更）：先拉一轮把已有 Vault 的数据落地 / 认领版本，
            // 否则接下来的对账会把本地既有条目全部当作「新建」推上去，与服务端既有实体逐条撞冲突
            //（「刷新后看到好多冲突」的成因）。已有待推变更时不做这步——那是用户的真实改动，
            // 走正常 Push 流程，该冲突就冲突。
            if (cursor == 0 && await store.PendingCountAsync(ct) == 0)
            {
                (pulled, pullConflicts, cursor, var seedStatus) = await PullAsync(context, cursor, ct);
                status = Worse(status, seedStatus);
            }

            if (_options.ReconcileBeforePush && status is SyncStatus.Synced or SyncStatus.Conflicted)
            {
                await ReconcileAsync(context, ct);
            }

            if (status is SyncStatus.Synced or SyncStatus.Conflicted)
            {
                (pushed, pushConflicts, var pushStatus) = await PushAsync(context, now, ct);
                status = Worse(status, pushStatus);
            }

            if (status is SyncStatus.Synced or SyncStatus.Conflicted)
            {
                (int p2, int c2, cursor, var pullStatus) = await PullAsync(context, cursor, ct);
                pulled += p2;
                pullConflicts += c2;
                status = Worse(status, pullStatus);
            }

            if (pushConflicts + pullConflicts > 0 && status == SyncStatus.Synced)
            {
                status = SyncStatus.Conflicted;
            }
        }
        catch (CloudAuthRequiredException)
        {
            status = SyncStatus.AuthRequired;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "同步网络失败，转入离线");
            status = SyncStatus.Offline;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 兜底：任何未预期异常都不能让状态永远停在 Syncing。
            logger.LogError(ex, "同步循环异常，标记为出错，下个周期重试");
            status = SyncStatus.Error;
        }
        finally
        {
            var completedAt = _clock.GetUtcNow();
            await store.SetStatusAsync(
                context.AppId, status, attemptAt: null,
                successAt: status is SyncStatus.Synced or SyncStatus.Conflicted ? completedAt : null,
                CancellationToken.None);
        }

        return new SyncRunResult(pushed, pushConflicts, pulled, pullConflicts, cursor, status);
    }

    // ── Push ────────────────────────────────────────────────────

    private async Task<(int Pushed, int Conflicts, SyncStatus Status)> PushAsync(
        SyncContext context, DateTimeOffset now, CancellationToken ct)
    {
        var due = await store.GetDueEntriesAsync(now, _options.PushBatchSize, ct);
        int pushed = 0, conflicts = 0;

        foreach (var entry in due)
        {
            ct.ThrowIfCancellationRequested();
            var source = FindSource(entry.EntityType);
            if (source is null)
            {
                logger.LogWarning("跳过未知实体类型的 Outbox 条目：{Type}", entry.EntityType);
                await store.DeleteIfUnchangedAsync(entry.Id, entry.Sequence, ct);
                continue;
            }

            var baseVersion = await store.GetServerVersionAsync(entry.EntityType, entry.EntityId, ct);
            var plaintext = entry.OperationType == OutboxOperationType.Delete
                ? null
                : await source.GetPlaintextAsync(entry.EntityType, entry.EntityId, ct);
            var isDelete = entry.OperationType == OutboxOperationType.Delete || plaintext is null;

            EncryptedPayload? localPayload = null;
            SyncPushOperation operation;
            if (isDelete)
            {
                operation = SyncPushOperation.Delete(
                    entry.OperationId, entry.EntityType, entry.EntityId, baseVersion,
                    context.KeyVersion, source.SchemaVersion);
            }
            else
            {
                localPayload = context.Session.Encrypt(
                    Context(context, entry.EntityType, entry.EntityId, source.SchemaVersion), plaintext!);
                operation = SyncPushOperation.Upsert(
                    entry.OperationId, entry.EntityType, entry.EntityId, baseVersion, localPayload);
            }

            SyncPushOperationResult result;
            try
            {
                result = (await client.PushAsync([operation], ct)).Results[0];
            }
            catch (CloudApiException ex) when (ex.StatusCode == 409)
            {
                logger.LogError("Push 被拒（{Detail}）——Vault 可能未初始化，停止本轮 Push", ex.Message);
                return (pushed, conflicts, SyncStatus.Error);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await ScheduleRetryAsync(entry, ex.Message, ct);
                return (pushed, conflicts, SyncStatus.Offline);
            }

            switch (result.Status)
            {
                case SyncPushStatus.Applied or SyncPushStatus.Duplicate:
                    await store.SetServerVersionAsync(entry.EntityType, entry.EntityId, result.Version ?? baseVersion, ct);
                    await store.SetContentHashAsync(entry.EntityType, entry.EntityId, ContentHash(plaintext), ct);
                    await store.DeleteIfUnchangedAsync(entry.Id, entry.Sequence, ct);
                    pushed++;
                    break;

                case SyncPushStatus.Conflict:
                    await conflictService.RecordAsync(entry.EntityType, entry.EntityId, localPayload, result.Server, ct);
                    await store.DeleteIfUnchangedAsync(entry.Id, entry.Sequence, ct);
                    conflicts++;
                    break;
            }
        }

        return (pushed, conflicts, conflicts > 0 ? SyncStatus.Conflicted : SyncStatus.Synced);
    }

    private async Task ScheduleRetryAsync(OutboxEntry entry, string error, CancellationToken ct)
    {
        var index = Math.Min(entry.RetryCount, _options.RetryBackoff.Count - 1);
        var next = _clock.GetUtcNow() + _options.RetryBackoff[index];
        await store.MarkRetryAsync(entry.Id, entry.RetryCount + 1, next, error, ct);
    }

    // ── Pull ────────────────────────────────────────────────────

    private async Task<(int Pulled, int Conflicts, long Cursor, SyncStatus Status)> PullAsync(
        SyncContext context, long cursor, CancellationToken ct)
    {
        // 先把整段变更收进内存（按实体去重，保留最高 Revision 的那条），再按依赖顺序整体应用。
        // 设计文档 §6「一批 Changes 全部成功应用后才能推进 Cursor」、§12「按依赖顺序落地」、
        // §17「单个损坏实体不推进游标、标记 Error、保留本地数据」。
        var batch = new Dictionary<(string Type, string Id), SyncPulledChange>();
        var startCursor = cursor;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await client.PullAsync(cursor, _options.PullBatchSize, ct);
            foreach (var change in page.Changes)
            {
                var key = (change.EntityType, change.EntityId);
                if (!batch.TryGetValue(key, out var existing) || change.Revision > existing.Revision)
                {
                    batch[key] = change;
                }
            }

            cursor = page.NextCursor;
            if (!page.HasMore)
            {
                break;
            }
        }

        if (batch.Count == 0)
        {
            await store.SetCursorAsync(context.AppId, cursor, ct);
            return (0, 0, cursor, SyncStatus.Synced);
        }

        // group / tag / credential → credential-secret → connection（依赖它们的排最后）。
        var ordered = batch.Values.OrderBy(c => DependencyRank(c.EntityType)).ThenBy(c => c.Revision).ToList();

        int pulled = 0, conflicts = 0;
        var pending = ordered;

        // 两轮：第一轮按依赖顺序尽量落地；跨页导致父实体在后一页的，收集起来第二轮重试
        //（此时父实体已落地）。第二轮仍失败的才是真问题。
        for (var round = 0; round < 2 && pending.Count > 0; round++)
        {
            var stillFailing = new List<SyncPulledChange>();
            foreach (var change in pending)
            {
                switch (await TryApplyPulledAsync(context, change, ct))
                {
                    case PullOutcome.Applied:
                        pulled++;
                        break;
                    case PullOutcome.Conflict:
                        conflicts++;
                        break;
                    case PullOutcome.Deferred:
                        stillFailing.Add(change);
                        break;
                    case PullOutcome.DecryptFailed:
                        logger.LogError(
                            "拉取的实体解密失败：{Type}/{Id}——不推进游标，保留本地数据", change.EntityType, change.EntityId);
                        return (pulled, conflicts, startCursor, SyncStatus.Error);
                }
            }

            pending = stillFailing;
        }

        if (pending.Count > 0)
        {
            // 仍落不下去（多为引用了已被删除的实体，或损坏）——不推进游标，下轮重试；标记 Error 让用户可见。
            foreach (var change in pending)
            {
                logger.LogError(
                    "拉取的 {Type}/{Id} 反复无法落地（可能引用了缺失的实体）——不推进游标",
                    change.EntityType, change.EntityId);
            }

            return (pulled, conflicts, startCursor, SyncStatus.Error);
        }

        await store.SetCursorAsync(context.AppId, cursor, ct);
        return (pulled, conflicts, cursor, conflicts > 0 ? SyncStatus.Conflicted : SyncStatus.Synced);
    }

    private enum PullOutcome { Skipped, Applied, Conflict, Deferred, DecryptFailed }

    private async Task<PullOutcome> TryApplyPulledAsync(
        SyncContext context, SyncPulledChange change, CancellationToken ct)
    {
        var source = FindSource(change.EntityType);
        if (source is null)
        {
            return PullOutcome.Skipped;
        }

        if (change.Version <= await store.GetServerVersionAsync(change.EntityType, change.EntityId, ct))
        {
            // 已持有该版本（多为本设备自己刚 Push 的改动回声），无需再落地。
            return PullOutcome.Skipped;
        }

        if (await store.IsConflictedAsync(change.EntityType, change.EntityId, ct))
        {
            // 该实体已处于冲突态（多为本轮 Push 刚记的），不落地远端改动，等用户解决。
            return PullOutcome.Skipped;
        }

        if (await store.HasPendingAsync(change.EntityType, change.EntityId, ct))
        {
            await RecordPullConflictAsync(context, source, change, ct);
            return PullOutcome.Conflict;
        }

        try
        {
            await ApplyChangeAsync(context, source, change, ct);
        }
        catch (CryptographicException)
        {
            return PullOutcome.DecryptFailed;
        }
        catch (Exception ex) when (ex is SyncDependencyNotReadyException or SqliteException)
        {
            // 依赖的实体（credential / group / tag）还没落地——外键失败或显式信号，本轮之后重试。
            return PullOutcome.Deferred;
        }

        await store.SetServerVersionAsync(change.EntityType, change.EntityId, change.Version, ct);

        // 内容哈希取「落地后的实际状态」，而非拉下来的明文——仓储写入时可能改写 UpdatedAt 等字段
        // （SqliteConnectionRepository / SqliteCredentialRepository 的 UpdateAsync 会把 UpdatedAt 置为 Now）。
        // 否则下一轮对账会把这次正常拉取当成本地漂移又推上去，两台机器就无限互相覆盖 / 版本号乱涨。
        var persisted = change.Deleted
            ? null
            : await source.GetPlaintextAsync(change.EntityType, change.EntityId, ct);
        await store.SetContentHashAsync(change.EntityType, change.EntityId, ContentHash(persisted), ct);
        return PullOutcome.Applied;
    }

    /// <summary>
    /// 拉取时的应用顺序（数值小的先应用），对齐设计文档 §12：
    /// group / tag / credential 无依赖；credential-secret 依赖 credential；
    /// connection 依赖 group + credential（外键），且 connection_tags 依赖 tag。
    /// </summary>
    private static int DependencyRank(string entityType) => entityType switch
    {
        SyncEntityTypes.CredentialSecret => 1,
        SyncEntityTypes.Connection => 2,
        _ => 0,
    };

    private async Task ApplyChangeAsync(
        SyncContext context, ISyncEntitySource source, SyncPulledChange change, CancellationToken ct)
    {
        if (change.Deleted)
        {
            await source.ApplyAsync(
                change.EntityType, change.EntityId, plaintext: null, deleted: true, change.SchemaVersion, ct);
            return;
        }

        var plaintext = context.Session.Decrypt(
            Context(context, change.EntityType, change.EntityId, change.SchemaVersion, change.KeyVersion),
            new EncryptedPayload(change.Ciphertext!, change.Nonce!, change.KeyVersion, change.SchemaVersion));
        await source.ApplyAsync(
            change.EntityType, change.EntityId, plaintext, deleted: false, change.SchemaVersion, ct);
    }

    private async Task RecordPullConflictAsync(
        SyncContext context, ISyncEntitySource source, SyncPulledChange change, CancellationToken ct)
    {
        var localPlain = await source.GetPlaintextAsync(change.EntityType, change.EntityId, ct);
        EncryptedPayload? local = localPlain is null
            ? null
            : context.Session.Encrypt(
                Context(context, change.EntityType, change.EntityId, source.SchemaVersion), localPlain);

        var remote = new SyncServerEntity(
            change.EntityType, change.EntityId, change.Version, change.Revision,
            change.KeyVersion, change.SchemaVersion, change.Deleted, change.Ciphertext, change.Nonce);

        await conflictService.RecordAsync(change.EntityType, change.EntityId, local, remote, ct);
        await store.DeleteEntityAsync(change.EntityType, change.EntityId, ct);
        await store.SetServerVersionAsync(change.EntityType, change.EntityId, change.Version, ct);
    }

    // ── 辅助 ────────────────────────────────────────────────────

    // ── 对账（崩溃后补偿）────────────────────────────────────────

    /// <summary>
    /// 比对每个 source 的当前本地实体与已同步内容哈希，把漂移（业务写已提交但 Outbox
    /// 未入队、或本地删除未登记）补进 Outbox。best-effort 变更追踪的崩溃安全兜底（协议 §3）。
    /// </summary>
    public async Task ReconcileAsync(SyncContext context, CancellationToken ct = default)
    {
        foreach (var source in _sources)
        {
            foreach (var entityType in source.EntityTypes)
            {
                var localIds = await source.ListEntityIdsAsync(entityType, ct);
                var localSet = new HashSet<string>(localIds);

                foreach (var id in localIds)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await store.HasPendingAsync(entityType, id, ct)
                        || await store.IsConflictedAsync(entityType, id, ct))
                    {
                        continue;
                    }

                    var plaintext = await source.GetPlaintextAsync(entityType, id, ct);
                    if (plaintext is null)
                    {
                        continue;
                    }

                    if (ContentHash(plaintext) != await store.GetContentHashAsync(entityType, id, ct))
                    {
                        var baseVersion = await store.GetServerVersionAsync(entityType, id, ct);
                        await store.EnqueueAsync(entityType, id, OutboxOperationType.Upsert, baseVersion, ct);
                    }
                }

                foreach (var knownId in await store.GetSyncedEntityIdsAsync(entityType, ct))
                {
                    if (localSet.Contains(knownId)
                        || await store.HasPendingAsync(entityType, knownId, ct)
                        || await store.IsConflictedAsync(entityType, knownId, ct))
                    {
                        continue;
                    }

                    var baseVersion = await store.GetServerVersionAsync(entityType, knownId, ct);
                    await store.EnqueueAsync(entityType, knownId, OutboxOperationType.Delete, baseVersion, ct);
                }
            }
        }
    }

    private static string ContentHash(byte[]? plaintext) =>
        plaintext is null ? string.Empty : Convert.ToHexString(SHA256.HashData(plaintext));

    private ISyncEntitySource? FindSource(string entityType) =>
        _sources.FirstOrDefault(s => s.Handles(entityType));

    private static PayloadContext Context(
        SyncContext context, string entityType, string entityId, int schemaVersion, int? keyVersion = null) =>
        new(context.UserId, context.AppId, entityType, entityId, schemaVersion, keyVersion ?? context.KeyVersion);

    private static SyncStatus Worse(SyncStatus a, SyncStatus b)
    {
        SyncStatus[] order = [SyncStatus.Synced, SyncStatus.Conflicted, SyncStatus.Offline, SyncStatus.Error, SyncStatus.AuthRequired];
        return Array.IndexOf(order, b) > Array.IndexOf(order, a) ? b : a;
    }
}
