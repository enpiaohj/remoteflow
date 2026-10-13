namespace RemoteFlow.Core.Cloud;

public enum OutboxOperationType
{
    Upsert,
    Delete,
}

/// <summary>一条待推送的本地变更。不含 Secret / 明文——Push 时才从业务存储即时读取并加密。</summary>
public sealed record OutboxEntry(
    Guid Id,
    string EntityType,
    string EntityId,
    OutboxOperationType OperationType,
    string OperationId,
    long BaseVersion,
    long Sequence,
    int RetryCount,
    DateTimeOffset? NextRetryAt);

/// <summary>Cloud Sync 状态机的对外状态（对应产品设计的状态标签）。</summary>
public enum SyncStatus
{
    Idle,
    Syncing,
    Synced,
    Offline,
    Conflicted,
    Error,
    AuthRequired,
}

public sealed record SyncStateSnapshot(
    long Cursor,
    DateTimeOffset? LastSuccessfulSyncAt,
    DateTimeOffset? LastAttemptAt,
    SyncStatus Status);

public enum ConflictResolution
{
    Unresolved,
    KeepLocal,
    UseRemote,
}

/// <summary>本地记录的一个未决冲突。本地与远端副本均以加密形式保存。</summary>
public sealed record SyncConflictRecord(
    Guid Id,
    string EntityType,
    string EntityId,
    EncryptedPayload? Local,
    SyncServerEntity Remote,
    DateTimeOffset DetectedAt,
    ConflictResolution Resolution);

/// <summary>一次同步循环的结果摘要。</summary>
public sealed record SyncRunResult(
    int Pushed,
    int PushConflicts,
    int Pulled,
    int PullConflicts,
    long Cursor,
    SyncStatus Status);

/// <summary>同步循环的可调参数。</summary>
public sealed record SyncOptions
{
    /// <summary>每次 Pull 的分页大小。</summary>
    public int PullBatchSize { get; init; } = 200;

    /// <summary>每次循环最多推送的 Outbox 条数。</summary>
    public int PushBatchSize { get; init; } = 200;

    /// <summary>每次 RunOnce 前先做一次对账（把漂移补进 Outbox）。</summary>
    public bool ReconcileBeforePush { get; init; } = true;

    /// <summary>瞬时失败的重试退避阶梯；超出末项后按末项周期重试。</summary>
    public IReadOnlyList<TimeSpan> RetryBackoff { get; init; } =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    ];
}
