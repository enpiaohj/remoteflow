namespace RemoteFlow.Core.Cloud;

/// <summary>登录后持久化的云会话。RefreshToken 是 Secret，须经平台密钥库保护落盘。</summary>
public sealed record CloudSession(string RefreshToken, Guid UserId, string AppId, string ClientDeviceId);

/// <summary>登录 / 设备注册时上报的本机设备信息。</summary>
public sealed record CloudDeviceInfo(
    string AppId,
    string ClientDeviceId,
    string DeviceName,
    string Platform,
    string PublicKey);

public enum CloudRegisterOutcome
{
    Created,
    AlreadyExists,
}

public sealed record CloudVaultStatus(
    bool Exists,
    int? CurrentKeyVersion,
    bool ThisDeviceAuthorized,
    bool HasRecoveryEnvelope);

public sealed record CloudPendingDevice(
    Guid DeviceId,
    string ClientDeviceId,
    string DisplayName,
    string Platform,
    byte[] PublicKey,
    DateTimeOffset LastSeenAt);

public enum SyncPushOperationType
{
    Upsert,
    Delete,
}

public sealed record SyncPushOperation(
    string OperationId,
    SyncPushOperationType OperationType,
    string EntityType,
    string EntityId,
    long BaseVersion,
    int KeyVersion,
    int SchemaVersion,
    byte[]? Ciphertext,
    byte[]? Nonce)
{
    public static SyncPushOperation Upsert(
        string operationId, string entityType, string entityId, long baseVersion, EncryptedPayload payload) =>
        new(operationId, SyncPushOperationType.Upsert, entityType, entityId, baseVersion,
            payload.KeyVersion, payload.SchemaVersion, payload.Ciphertext, payload.Nonce);

    public static SyncPushOperation Delete(
        string operationId, string entityType, string entityId, long baseVersion, int keyVersion, int schemaVersion) =>
        new(operationId, SyncPushOperationType.Delete, entityType, entityId, baseVersion,
            keyVersion, schemaVersion, null, null);
}

public enum SyncPushStatus
{
    Applied,
    Conflict,
    Duplicate,
}

public sealed record SyncServerEntity(
    string EntityType,
    string EntityId,
    long Version,
    long Revision,
    int KeyVersion,
    int SchemaVersion,
    bool Deleted,
    byte[]? Ciphertext,
    byte[]? Nonce);

public sealed record SyncPushOperationResult(
    string OperationId,
    SyncPushStatus Status,
    string EntityType,
    string EntityId,
    long? Version,
    long? Revision,
    SyncServerEntity? Server);

public sealed record SyncPushResponse(long StreamRevision, IReadOnlyList<SyncPushOperationResult> Results);

public sealed record SyncPulledChange(
    string EntityType,
    string EntityId,
    long Version,
    long Revision,
    int KeyVersion,
    int SchemaVersion,
    bool Deleted,
    byte[]? Ciphertext,
    byte[]? Nonce);

public sealed record SyncPullPage(IReadOnlyList<SyncPulledChange> Changes, long NextCursor, bool HasMore);

/// <summary>没有可用的登录凭据 —— 需要用户重新登录 AppsCloud。</summary>
public sealed class CloudAuthRequiredException(string message) : Exception(message);

/// <summary>AppsCloud 返回了非预期的错误响应。</summary>
public sealed class CloudApiException(int statusCode, string message)
    : Exception($"AppsCloud returned {statusCode}: {message}")
{
    public int StatusCode { get; } = statusCode;
}
