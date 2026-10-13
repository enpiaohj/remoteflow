namespace RemoteFlow.Core.Cloud;

/// <summary>
/// 业务数据与同步引擎之间的接缝。每种同步实体类型（connection / credential / group / tag …）
/// 由一个实现负责序列化取本地明文、以及把拉取到的明文落地。
/// SyncCoordinator 不理解业务字段，只搬运字节。
/// </summary>
public interface ISyncEntitySource
{
    /// <summary>本 source 负责的实体类型是否包含 <paramref name="entityType"/>。</summary>
    bool Handles(string entityType);

    /// <summary>本 source 产出的 Payload Schema 版本。</summary>
    int SchemaVersion { get; }

    /// <summary>本 source 负责的全部实体类型（用于首次全量入 Outbox）。</summary>
    IReadOnlyList<string> EntityTypes { get; }

    /// <summary>枚举某类型下所有本地实体的 Id（首次同步建 Outbox 用）。</summary>
    Task<IReadOnlyList<string>> ListEntityIdsAsync(string entityType, CancellationToken ct = default);

    /// <summary>取本地当前明文（稳定序列化 + SchemaVersion）。实体已在本地删除时返回 null。</summary>
    Task<byte[]?> GetPlaintextAsync(string entityType, string entityId, CancellationToken ct = default);

    /// <summary>
    /// 把拉取到的变更应用到本地。<paramref name="deleted"/> 为真时 <paramref name="plaintext"/> 为 null（Tombstone）。
    /// <paramref name="schemaVersion"/> 是该 Payload 的 Schema 版本，低于当前版本时由实现自行迁移。
    /// </summary>
    Task ApplyAsync(
        string entityType,
        string entityId,
        byte[]? plaintext,
        bool deleted,
        int schemaVersion,
        CancellationToken ct = default);

    /// <summary>
    /// 从 Payload 读出「内容最后修改时间」，供 Last-Writer-Wins 冲突自动解决用。
    /// 不支持（返回 null）的实体在冲突时仍走用户选择对话框。
    /// </summary>
    DateTimeOffset? ReadContentModifiedAt(byte[] plaintext, int schemaVersion) => null;
}
