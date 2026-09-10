using RemoteFlow.Core.Models;

namespace RemoteFlow.Core.Abstractions;

/// <summary>
/// 连接资产仓储。所有 SQL 访问集中在实现内部，不散落到上层服务。
/// </summary>
public interface IConnectionRepository
{
    Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default);
    Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(ConnectionProfile profile, CancellationToken ct = default);
    Task UpdateAsync(ConnectionProfile profile, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>更新最近连接时间。连接成功后调用。</summary>
    Task TouchLastConnectedAsync(Guid id, DateTimeOffset when, CancellationToken ct = default);

    /// <summary>统计引用了指定凭据的连接数量，用于删除凭据前的影响提示。</summary>
    Task<int> CountByCredentialAsync(Guid credentialId, CancellationToken ct = default);

    /// <summary>统计指定分组下的直属连接数量，用于删除分组前的确认。</summary>
    Task<int> CountByGroupAsync(Guid groupId, CancellationToken ct = default);
}

public interface IGroupRepository
{
    Task<IReadOnlyList<ConnectionGroup>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(ConnectionGroup group, CancellationToken ct = default);
    Task UpdateAsync(ConnectionGroup group, CancellationToken ct = default);

    /// <summary>
    /// 删除分组。组内连接不会被静默删除：
    /// <paramref name="moveConnectionsTo"/> 指定迁移目标分组，null 表示移动到「未分组」。
    /// </summary>
    Task DeleteAsync(Guid id, Guid? moveConnectionsTo, CancellationToken ct = default);
}

public interface ITagRepository
{
    Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(Tag tag, CancellationToken ct = default);
    Task UpdateAsync(Tag tag, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// 凭据元数据仓储。<b>只处理元数据，Secret 由 <see cref="ICredentialVault"/> 负责。</b>
/// </summary>
public interface ICredentialRepository
{
    Task<IReadOnlyList<Credential>> GetAllAsync(CancellationToken ct = default);
    Task<Credential?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(Credential credential, CancellationToken ct = default);
    Task UpdateAsync(Credential credential, CancellationToken ct = default);

    /// <summary>
    /// 只更新凭据的本地 Secret 引用，**不触碰 <c>updated_at</c>**。
    /// 同步引擎落地「密码 / 私钥」时需要改引用，但那不是对凭据元数据的内容修改——
    /// 若沿用 <see cref="UpdateAsync"/> 会刷新 UpdatedAt，使刚记录的元数据内容哈希立刻失效，
    /// 下一轮对账便把凭据当成「本地漂移」又推一次。
    /// </summary>
    Task SetSecretReferencesAsync(
        Guid id, string? passwordReference, string? privateKeyReference, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IHistoryRepository
{
    Task<IReadOnlyList<ConnectionHistoryEntry>> GetRecentAsync(int limit, CancellationToken ct = default);

    /// <summary>取指定连接的历史记录，按开始时间倒序。用于连接详情面板的历史与迷你图表。</summary>
    Task<IReadOnlyList<ConnectionHistoryEntry>> GetByConnectionAsync(Guid connectionId, int limit, CancellationToken ct = default);

    /// <summary>统计指定连接的历史条数（真实累计连接次数，详情「使用信息」用，
    /// 不受列表页最多 50 条展示上限影响）。</summary>
    Task<int> CountByConnectionAsync(Guid connectionId, CancellationToken ct = default);

    Task AddAsync(ConnectionHistoryEntry entry, CancellationToken ct = default);

    /// <summary>会话结束时补写结束时间与结果。</summary>
    Task CompleteAsync(Guid entryId, DateTimeOffset endedAt, ConnectionResult result, ConnectionErrorCode errorCode, CancellationToken ct = default);

    /// <summary>清空全部连接历史。设置页提供该操作。</summary>
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>已信任 SSH Host Key 的仓储。</summary>
public interface IHostKeyRepository
{
    Task<SshHostKeyRecord?> GetAsync(string host, int port, CancellationToken ct = default);
    Task<IReadOnlyList<SshHostKeyRecord>> GetAllAsync(CancellationToken ct = default);
    Task SaveAsync(SshHostKeyRecord record, CancellationToken ct = default);
    Task DeleteAsync(string host, int port, CancellationToken ct = default);

    /// <summary>清空全部已信任的 Host Key。</summary>
    Task ClearAsync(CancellationToken ct = default);
}
